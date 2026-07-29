using Godot;

namespace Lagoon;

/// <summary>
/// Modalita' del giocatore. <c>OnFoot</c> copre anche lo stare su un ponte in movimento: quello non e'
/// una modalita' a se', e' <c>OnFoot</c> con un'ancora attiva. Un ramo separato duplicherebbe gravita'
/// e collisioni senza aggiungere nulla.
/// </summary>
public enum PlayerMode
{
    OnFoot,
    Driving,
}

/// <summary>
/// Movimento del giocatore (Fase 1). Segue il pattern CLAUDE.md §3:
///  - se questo peer e' l'autorita' del proprio avatar, calcola il movimento come in singleplayer
///    e scrive lo stato replicato (<see cref="SyncPosition"/>/<see cref="SyncFacing"/>);
///  - altrimenti (avatar remoto) NON calcola nulla: interpola verso lo stato replicato.
///
/// Nota di design (Fase 1): il movimento e' client-authoritative — ogni peer e' autorita' del
/// PROPRIO avatar e ne replica la posizione. La validazione server-side dell'input (anti-cheat)
/// e' rimandata, come la lag-compensation (vedi la skill combat-shooting). Le fasi 2/3
/// (inventario, danno) restano
/// invece pienamente server-authoritative.
///
/// Fase 4 (veicoli): <see cref="SyncPosition"/> non e' piu' necessariamente in coordinate mondo. E'
/// espressa nel sistema di riferimento indicato da <see cref="SyncAnchorId"/>, cosi' che un passeggero
/// su un'imbarcazione si replichi in coordinate LOCALI allo scafo. Chi ha bisogno della posizione
/// autoritativa in coordinate mondo usa <see cref="ResolvedSyncPosition"/>.
/// </summary>
public partial class PlayerController : CharacterMotor
{
    /// Fattore di interpolazione per gli avatar remoti (piu' alto = piu' reattivo, meno morbido).
    [Export] public float InterpolationSpeed { get; set; } = 14.0f;

    /// Yaw della camera isometrica. L'input viene ruotato di questo angolo cosi' che
    /// "avanti" sullo schermo corrisponda alla direzione attesa. Deve combaciare con PlayerCamera.
    [Export] public float CameraYawDegrees { get; set; } = 45.0f;

    /// Quanto sotto la superficie dell'acqua si viene riportati al molo (nessun nuoto in questa fase).
    [Export] public float WaterFallbackDepth { get; set; } = 2.0f;

    // Stato replicato specifico del giocatore. Il resto (posizione, imbardata, velocita' locale,
    // crouch, contatto a terra, pitch di mira) sta in CharacterMotor, condiviso con gli NPC.

    /// <summary>
    /// Riferimento in cui e' espressa <see cref="CharacterMotor.SyncPosition"/>: 0 = mondo,
    /// altrimenti il <see cref="BoatController.VehicleId"/> del veicolo su cui si sta.
    ///
    /// Sta nello STESSO <c>SceneReplicationConfig</c> della posizione perche' i due valori devono
    /// viaggiare nello stesso pacchetto: se arrivassero separati ci sarebbe un frame in cui l'ancora
    /// e' cambiata e la posizione no, cioe' un teletrasporto a ogni imbarco/sbarco.
    /// </summary>
    [Export] public int SyncAnchorId { get; set; }

    private PlayerInput _input = null!;
    private WeaponController? _weapon;
    private WeaponInput? _weaponInput;
    private RayCast3D _groundProbe = null!;
    private WaterVolume? _water;
    private int _ownerPeerId;

    // --- Solo peer autoritativo -----------------------------------------------------------
    private BoatController? _anchor;
    private Transform3D _lastAnchorFrame;
    private bool _hasAnchorFrame;

    /// Tempo residuo del latch di mira dopo un hip-fire (vedi CharacterMotor.HipFireAimSeconds).
    private float _hipFireTimer;

    // --- Su ogni peer: derivato dallo stato replicato della barca --------------------------
    private BoatController? _drivingBoat;

    // --- Solo peer remoti -----------------------------------------------------------------
    private Vector3 _remoteLocal;
    private int _lastRemoteAnchorId = -1;

    /// <summary>
    /// Al timone o a piedi. Non e' uno stato locale: si deduce da
    /// <see cref="BoatController.PilotPeerId"/>, che e' host-autoritativo e replicato, quindi ogni peer
    /// arriva alla stessa conclusione senza scambiare nulla in piu'.
    /// </summary>
    public PlayerMode Mode => _drivingBoat != null ? PlayerMode.Driving : PlayerMode.OnFoot;

    /// Veicolo che questo giocatore sta pilotando (o null).
    public BoatController? DrivingBoat => _drivingBoat;

    /// <summary>
    /// Posizione autoritativa in coordinate MONDO, ricostruita dallo stato replicato. Va usata al posto
    /// di <see cref="SyncPosition"/> in ogni calcolo host-side (vedi <c>WeaponController.RequestFire</c>):
    /// per un giocatore su un'imbarcazione <see cref="SyncPosition"/> e' locale allo scafo.
    /// </summary>
    public Vector3 ResolvedSyncPosition
    {
        get
        {
            if (SyncAnchorId == 0)
                return SyncPosition;

            BoatController? boat = VehicleRegistry.Find(this, SyncAnchorId);
            return boat != null ? boat.GlobalTransform * SyncPosition : SyncPosition;
        }
    }

    public override void _EnterTree()
    {
        // Il nome del nodo e' l'id del peer proprietario (impostato dallo spawner/host).
        // Impostiamo l'autorita' QUI, prima del _Ready dei figli, cosi' il MultiplayerSynchronizer
        // erediti l'autorita' corretta (recursive = true di default).
        if (int.TryParse(Name, out int peerId))
        {
            _ownerPeerId = peerId;
            SetMultiplayerAuthority(peerId);
        }
    }

    public override void _Ready()
    {
        // Capsula, crouch e stato comune li prepara il motore condiviso.
        base._Ready();

        _input = GetNode<PlayerInput>("Input");
        _groundProbe = GetNode<RayCast3D>("GroundProbe");
        _water = WaterVolume.Find(this);

        // Opzionali: un avatar puo' esistere senza armi, il movimento non deve dipenderne.
        _weapon = GetNodeOrNull<WeaponController>("Weapon");
        _weaponInput = GetNodeOrNull<WeaponInput>("WeaponInput");

        // Hip-fire: un colpo sparato senza mirare alza comunque l'arma per un attimo, cosi' il
        // gesto si legge. Ci si aggancia allo sparo gia' risolto invece di rileggere l'input di
        // fuoco: un solo punto di verita' su "ho sparato davvero".
        if (_weapon != null)
            _weapon.ShotResolved += OnShotResolved;

        // Evita che gli avatar remoti "saltino" dall'origine al primo update.
        SyncAnchorId = 0;
    }

    public override void _PhysicsProcess(double delta)
    {
        RefreshDrivingBoat();

        if (IsMultiplayerAuthority())
            AuthoritativeMovement(delta);
        else
            RemoteInterpolation(delta);
    }

    /// <summary>
    /// Riallinea la modalita' allo stato replicato del veicolo. Gira su OGNI peer: e' cosi' che
    /// l'avatar del pilota risulta al timone anche sulle finestre degli altri giocatori, senza un
    /// secondo meccanismo di aggancio.
    /// </summary>
    private void RefreshDrivingBoat()
    {
        BoatController? boat = _ownerPeerId != 0
            ? VehicleRegistry.FindByPilot(this, _ownerPeerId)
            : null;

        if (boat == _drivingBoat)
            return;

        _drivingBoat = boat;
        _input.MovementSuppressed = boat != null;

        // Entrando o uscendo dal timone il riferimento del trasporto non e' piu' valido: si azzera e
        // verra' ricostruito dal GroundProbe al primo tick a piedi.
        _anchor = null;
        _hasAnchorFrame = false;
    }

    private void AuthoritativeMovement(double delta)
    {
        if (Mode == PlayerMode.Driving)
        {
            DrivingUpdate();
            return;
        }

        CarryWithAnchor();

        Vector2 motion = _input.ReadMovement();
        Vector3 worldDir = new Vector3(motion.X, 0f, motion.Y)
            .Rotated(Vector3.Up, Mathf.DegToRad(CameraYawDegrees));
        if (worldDir.LengthSquared() > 1f)
            worldDir = worldDir.Normalized();

        // Accelerazione, pendenza, gradini, salto, gravita' e atterraggio: tutto nel motore condiviso.
        StepMotion(worldDir, SelectSpeed(), _input.ReadJumpPressed(), _input.ReadCrouch(), delta);

        RefreshAnchor();
        CheckWaterFallback();

        // Pubblica lo stato che verra' replicato agli altri peer.
        PublishState();
        UpdateAiming(worldDir, delta);
        PublishLocomotionState();
    }

    /// <summary>
    /// Velocita' orizzontale corrente. L'accovacciamento vince sulla corsa: non si sprinta accovacciati.
    /// Sono modificatori di velocita', non stati esclusivi, cosi' il BlendSpace2D riceve un valore
    /// continuo invece di un enum.
    /// </summary>
    private float SelectSpeed()
    {
        if (SyncCrouching)
            return CrouchSpeed;

        return _input.ReadSprint() ? RunSpeed : WalkSpeed;
    }

    /// <summary>
    /// Orientamento e mira dell'avatar, in TRE stati.
    ///
    /// IN MIRA (RMB, o latch dopo un hip-fire): la mira la calcola il cursore e la insegue il
    /// BUSTO (SpineAimModifier via SyncAimYaw/SyncAimPitch). Il corpo la segue solo quando serve:
    /// sempre in movimento (e' cio' che attiva lo strafe armato, perche' la velocita' locale si
    /// proietta su SyncFacing), con zona morta e isteresi da fermi (turn-in-place,
    /// <see cref="CharacterMotor.PlanAimFacing"/>). Mirare "dietro" non e' un caso speciale: lo
    /// scarto supera la soglia e il corpo recupera da solo per la via piu' corta.
    ///
    /// ARMATO SENZA MIRA e DISARMATO: il corpo guarda dove va, il pitch rientra smorzato e
    /// SyncAimYaw resta agganciato a SyncFacing, cosi' sui peer remoti il busto non punta mai a un
    /// residuo stantio.
    ///
    /// Il punto di mira si legge da <see cref="WeaponInput.AimPoint"/>, che lo ricalcola gia' ogni
    /// frame per il tiro: non si duplica <see cref="AimResolver"/>.
    /// </summary>
    /// Latch dell'hip-fire: parte solo sul peer proprietario, che e' l'unico a leggere l'input.
    private void OnShotResolved(Vector3 origin, Vector3 end, bool hit, bool isLocalShooter)
    {
        if (isLocalShooter)
            _hipFireTimer = HipFireAimSeconds;
    }

    private void UpdateAiming(Vector3 worldDir, double delta)
    {
        bool armed = _weapon is { IsArmed: true } && _weaponInput != null;

        if (_hipFireTimer > 0f)
            _hipFireTimer -= (float)delta;
        bool aiming = armed && (_input.ReadAim() || _hipFireTimer > 0f);
        SyncAiming = aiming;

        bool moving = worldDir.LengthSquared() > 0.001f;
        float target = SyncFacing;

        if (aiming)
        {
            // L'origine e' la SPALLA, non i piedi: il pitch di mira misurato dal suolo sarebbe
            // sempre inclinato verso l'alto anche mirando dritto davanti a se'.
            Vector3 muzzle = GlobalPosition + Vector3.Up * WeaponController.MuzzleHeight;
            Vector3 toAim = _weaponInput!.AimPoint - muzzle;

            Vector3 flat = new(toAim.X, 0f, toAim.Z);
            if (flat.LengthSquared() > 0.0001f)
            {
                SyncAimYaw = Mathf.Atan2(flat.X, flat.Z);
                SyncAimPitch = Mathf.Atan2(toAim.Y, flat.Length());
            }

            target = PlanAimFacing(SyncFacing, SyncAimYaw, moving);
        }
        else
        {
            SyncAimPitch = Mathf.Lerp(SyncAimPitch, 0f, Damp(8f, (float)delta));
            if (moving)
                target = Mathf.Atan2(worldDir.X, worldDir.Z);
        }

        UpdateFacing(target, delta);

        // Fuori mira il busto segue il corpo per definizione: si riallinea DOPO UpdateFacing,
        // cosi' i due angoli replicati non divergono mai di un frame.
        if (!aiming)
            SyncAimYaw = SyncFacing;
    }

    /// <summary>
    /// Eventi one-shot del movimento. Seguono lo stesso schema di
    /// <c>WeaponController.BroadcastShot</c>: l'autorita' del nodo li trasmette, ogni peer riemette
    /// un segnale LOCALE che il layer di animazione ascolta. Nel payload non viaggia nessun esito di
    /// gioco (CLAUDE.md §3): solo il fatto che il salto o l'atterraggio sono avvenuti.
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void BroadcastJump() => EmitSignal(SignalName.Jumped);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void BroadcastLand(float impactSpeed) => EmitSignal(SignalName.Landed, impactSpeed);

    /// La geometria dell'ostacolo superato (bordo, normale della parete, altezza) e' una misura,
    /// non un esito di gioco: serve all'IK delle mani e alla scelta della posa su ogni peer.
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void BroadcastVault(Vector3 ledgePoint, Vector3 wallNormal, float height) =>
        EmitSignal(SignalName.Vaulted, ledgePoint, wallNormal, height);

    // Il motore condiviso non conosce la rete: segnala l'evento e basta. Qui lo si trasmette.
    protected override void OnJumpTriggered() => Rpc(MethodName.BroadcastJump);

    protected override void OnLandTriggered(float impactSpeed) =>
        Rpc(MethodName.BroadcastLand, impactSpeed);

    protected override void OnVaultTriggered(Vector3 ledgePoint, Vector3 wallNormal, float height) =>
        Rpc(MethodName.BroadcastVault, ledgePoint, wallNormal, height);

    /// <summary>
    /// Al timone: nessuna gravita' e nessun <c>MoveAndSlide</c>, quindi il pilota non produce alcuna
    /// reazione sullo scafo. La posizione e' il posto di guida del veicolo, e viene pubblicata come
    /// costante nel riferimento della barca — cioe' esattamente come per un passeggero, senza codice
    /// dedicato al lato remoto.
    /// </summary>
    private void DrivingUpdate()
    {
        BoatController boat = _drivingBoat!;
        if (!GodotObject.IsInstanceValid(boat))
            return;

        Velocity = Vector3.Zero;
        GlobalPosition = boat.GlobalTransform * boat.HelmLocalPosition;

        SyncAnchorId = boat.VehicleId;
        SyncPosition = boat.HelmLocalPosition;
        SyncFacing = boat.HeadingYaw;
        Visual.Rotation = new Vector3(0f, SyncFacing, 0f);
    }

    /// <summary>
    /// Trasporta il giocatore col veicolo applicandogli il DELTA di trasformata dell'ancora.
    ///
    /// E' esatto anche in rotazione (si ruota attorno al pivot della barca, non lungo la corda), mentre
    /// la platform velocity dell'engine e' un'approssimazione del primo ordine e ricostruisce il termine
    /// angolare attorno all'origine del corpo di appoggio anziche' a quella dello scafo. Conseguenza
    /// voluta: la velocita' relativa piede/ponte e' zero, quindi il passeggero non deriva in virata e
    /// non puo' esserci tunneling attraverso il ponte.
    ///
    /// INVARIANTE FRAGILE: la platform velocity dell'engine FUNZIONA con Jolt e l'AnimatableBody3D del
    /// ponte (verificato: senza disattivarla il giocatore viaggiava al doppio della velocita' della
    /// barca, perche' i due trasporti si sommavano). E' disattivata in <c>Player.tscn</c> con
    /// <c>platform_floor_layers = 4294967263</c>, cioe' tutti i layer TRANNE il 6 "vehicle_deck" —
    /// il ponte calpestabile, che e' l'<c>AnimatableBody3D</c> su cui si cammina davvero.
    /// Se quel valore torna al default, il trasporto si applica due volte.
    /// </summary>
    private void CarryWithAnchor()
    {
        if (_anchor == null || !GodotObject.IsInstanceValid(_anchor))
        {
            _hasAnchorFrame = false;
            return;
        }

        Transform3D now = _anchor.GlobalTransform;
        if (_hasAnchorFrame)
            GlobalPosition = now * (_lastAnchorFrame.AffineInverse() * GlobalPosition);

        _lastAnchorFrame = now;
        _hasAnchorFrame = true;
    }

    /// <summary>
    /// Ricalcola su cosa si e' appoggiati leggendo il <c>RayCast3D</c> "GroundProbe" (maschera: solo il
    /// layer dei veicoli).
    ///
    /// Volutamente NON si usa <c>GetLastSlideCollision()</c>: quello ritorna null quando nel tick non
    /// c'e' stato movimento, quindi un giocatore FERMO sul ponte perderebbe e riacquisterebbe l'ancora
    /// a intermittenza — e con essa il riferimento del trasporto, cioe' resterebbe indietro a scatti.
    /// </summary>
    private void RefreshAnchor()
    {
        _groundProbe.ForceRaycastUpdate();
        BoatController? found = _groundProbe.IsColliding()
            ? FindOwningBoat(_groundProbe.GetCollider() as Node)
            : null;

        if (found == _anchor)
            return;

        // Cambio di appoggio: il frame precedente appartiene a un altro riferimento, va scartato.
        _anchor = found;
        _hasAnchorFrame = false;
    }

    private static BoatController? FindOwningBoat(Node? node)
    {
        while (node != null)
        {
            if (node is BoatController boat)
                return boat;
            node = node.GetParent();
        }
        return null;
    }

    /// Pubblica <see cref="SyncAnchorId"/> e <see cref="SyncPosition"/> nel riferimento corrente.
    private void PublishState()
    {
        if (_anchor != null && GodotObject.IsInstanceValid(_anchor))
        {
            SyncAnchorId = _anchor.VehicleId;
            SyncPosition = _anchor.GlobalTransform.AffineInverse() * GlobalPosition;
        }
        else
        {
            SyncAnchorId = 0;
            SyncPosition = GlobalPosition;
        }
    }

    /// <summary>
    /// Rientro al molo per chi finisce in acqua: in questa fase non c'e' nuoto ne' annegamento. Il
    /// fondale resta come pavimento di sicurezza (niente cade all'infinito), ma il rientro scatta prima
    /// di toccarlo. Client-autoritativo come tutto il movimento della Fase 1.
    /// </summary>
    private void CheckWaterFallback()
    {
        if (_water == null || GlobalPosition.Y > _water.SurfaceY - WaterFallbackDepth)
            return;

        if (GetTree().GetFirstNodeInGroup("water_respawn") is not Node3D respawn)
            return;

        GlobalPosition = respawn.GlobalPosition;
        Velocity = Vector3.Zero;
        _anchor = null;
        _hasAnchorFrame = false;
    }

    private void RemoteInterpolation(double delta)
    {
        float t = Mathf.Clamp((float)delta * InterpolationSpeed, 0f, 1f);

        // Si interpola nello spazio dell'ANCORA, non nel mondo. E' correttezza, non stile: interpolando
        // in mondo verso un bersaglio che si muove a v m/s l'errore stazionario e' v/InterpolationSpeed
        // (circa 0.5 m a 7.5 m/s), cioe' i passeggeri remoti scivolerebbero visibilmente verso poppa.
        if (SyncAnchorId != _lastRemoteAnchorId)
        {
            // Cambio di riferimento (imbarco/sbarco): si riaggancia senza interpolare, altrimenti si
            // interpolerebbe fra due punti espressi in sistemi di coordinate diversi.
            _lastRemoteAnchorId = SyncAnchorId;
            _remoteLocal = SyncPosition;
        }
        else
        {
            _remoteLocal = _remoteLocal.Lerp(SyncPosition, t);
        }

        BoatController? anchor = VehicleRegistry.Find(this, SyncAnchorId);
        GlobalPosition = anchor != null ? anchor.GlobalTransform * _remoteLocal : _remoteLocal;

        ApplyRemoteFacing(InterpolationSpeed, delta);
    }
}
