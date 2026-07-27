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
public partial class PlayerController : CharacterBody3D
{
    /// Frazione dell'altezza in piedi a cui si riduce la capsula da accovacciati.
    private const float CrouchHeightFactor = 0.6f;

    [Export] public float WalkSpeed { get; set; } = 4.0f;
    [Export] public float RunSpeed { get; set; } = 7.0f;
    [Export] public float CrouchSpeed { get; set; } = 2.0f;
    [Export] public float Gravity { get; set; } = 20.0f;

    /// Velocita' verticale impressa dal salto, in m/s.
    [Export] public float JumpVelocity { get; set; } = 6.0f;

    /// <summary>
    /// Velocita' con cui l'avatar ruota verso la direzione voluta. Serve perche' da armato
    /// l'orientamento insegue il cursore, che puo' saltare da una parte all'altra dello schermo in un
    /// frame: senza smorzamento l'avatar scatterebbe.
    /// </summary>
    [Export] public float TurnSpeed { get; set; } = 14.0f;

    /// <summary>
    /// Velocita' d'impatto oltre la quale l'atterraggio e' "duro". Non ha alcun effetto sul movimento:
    /// la legge <see cref="PlayerAnimationBridge"/> e la gira al <c>CharacterAnimator</c>, che ci scala
    /// l'ammortizzazione procedurale del bacino.
    /// </summary>
    [Export] public float HardLandingSpeed { get; set; } = 9.0f;

    /// Fattore di interpolazione per gli avatar remoti (piu' alto = piu' reattivo, meno morbido).
    [Export] public float InterpolationSpeed { get; set; } = 14.0f;

    /// Yaw della camera isometrica. L'input viene ruotato di questo angolo cosi' che
    /// "avanti" sullo schermo corrisponda alla direzione attesa. Deve combaciare con PlayerCamera.
    [Export] public float CameraYawDegrees { get; set; } = 45.0f;

    /// Quanto sotto la superficie dell'acqua si viene riportati al molo (nessun nuoto in questa fase).
    [Export] public float WaterFallbackDepth { get; set; } = 2.0f;

    // Stato replicato dal MultiplayerSynchronizer (vedi Player.tscn).

    /// Posizione, espressa nel riferimento indicato da <see cref="SyncAnchorId"/>.
    [Export] public Vector3 SyncPosition { get; set; }

    /// Imbardata dell'avatar, sempre in coordinate MONDO.
    [Export] public float SyncFacing { get; set; }

    /// <summary>
    /// Riferimento in cui e' espressa <see cref="SyncPosition"/>: 0 = mondo, altrimenti il
    /// <see cref="BoatController.VehicleId"/> del veicolo su cui si sta.
    ///
    /// Sta nello STESSO <c>SceneReplicationConfig</c> di <see cref="SyncPosition"/> perche' i due
    /// valori devono viaggiare nello stesso pacchetto: se arrivassero separati ci sarebbe un frame in
    /// cui l'ancora e' cambiata e la posizione no, cioe' un teletrasporto a ogni imbarco/sbarco.
    /// </summary>
    [Export] public int SyncAnchorId { get; set; }

    /// <summary>
    /// Velocita' orizzontale espressa nel riferimento dell'AVATAR: X = destra, Y = avanti, in m/s.
    ///
    /// E' locale e non in coordinate mondo perche' e' esattamente cio' che serve al
    /// <c>BlendSpace2D</c> della locomozione: se fosse in mondo, ogni peer dovrebbe riproiettarla
    /// usando <see cref="SyncFacing"/>, e con i due valori che arrivano in pacchetti diversi ci
    /// sarebbe un frame in cui direzione e orientamento non corrispondono. Replicando gia' la
    /// grandezza finale il problema non esiste.
    /// </summary>
    [Export] public Vector2 SyncLocalVelocity { get; set; }

    /// Stato replicato: accovacciato. Pilota il peso del layer crouch sugli altri peer.
    [Export] public bool SyncCrouching { get; set; }

    /// Stato replicato: a terra. Distingue locomozione da caduta sugli altri peer.
    [Export] public bool SyncGrounded { get; set; } = true;

    /// <summary>
    /// Salto: evento estetico, non uno stato. Emesso su OGNI peer da <see cref="BroadcastJump"/>,
    /// cosi' il layer one-shot lo intercetta senza dover confrontare stati fra un frame e l'altro.
    /// </summary>
    [Signal]
    public delegate void JumpedEventHandler();

    /// Atterraggio, con la velocita' d'impatto in m/s: sceglie fra atterraggio morbido e duro.
    [Signal]
    public delegate void LandedEventHandler(float impactSpeed);

    private PlayerInput _input = null!;
    private WeaponController? _weapon;
    private WeaponInput? _weaponInput;
    private CollisionShape3D _collision = null!;
    private ShapeCast3D _headroom = null!;
    private CapsuleShape3D _capsule = null!;

    private float _standHeight;
    private float _crouchBlend;
    private bool _wasGrounded = true;
    private float _fallSpeed;
    private Node3D _visual = null!;
    private RayCast3D _groundProbe = null!;
    private WaterVolume? _water;
    private int _ownerPeerId;

    // --- Solo peer autoritativo -----------------------------------------------------------
    private BoatController? _anchor;
    private Transform3D _lastAnchorFrame;
    private bool _hasAnchorFrame;

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
        _input = GetNode<PlayerInput>("Input");
        _visual = GetNode<Node3D>("Visual");
        _groundProbe = GetNode<RayCast3D>("GroundProbe");
        _water = WaterVolume.Find(this);

        // Opzionali: un avatar puo' esistere senza armi (NPC futuri), il movimento non deve dipenderne.
        _weapon = GetNodeOrNull<WeaponController>("Weapon");
        _weaponInput = GetNodeOrNull<WeaponInput>("WeaponInput");

        _collision = GetNode<CollisionShape3D>("CollisionShape3D");
        _headroom = GetNode<ShapeCast3D>("Headroom");
        _capsule = (CapsuleShape3D)_collision.Shape;
        _standHeight = _capsule.Height;

        // Evita che gli avatar remoti "saltino" dall'origine al primo update.
        SyncPosition = GlobalPosition;
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

        bool grounded = IsOnFloor();
        UpdateCrouch(delta, grounded);

        Vector3 velocity = Velocity;
        float speed = SelectSpeed();
        velocity.X = worldDir.X * speed;
        velocity.Z = worldDir.Z * speed;

        if (grounded)
        {
            // Il salto va impresso PRIMA di MoveAndSlide e dopo l'azzeramento, altrimenti l'azzeramento
            // di questo stesso frame lo cancellerebbe: a terra IsOnFloor() e' ancora vero quando si salta.
            velocity.Y = 0f;
            if (!SyncCrouching && _input.ReadJumpPressed())
            {
                velocity.Y = JumpVelocity;
                Rpc(MethodName.BroadcastJump);
            }
        }
        else
        {
            velocity.Y -= Gravity * (float)delta;
        }

        // Velocita' di caduta al momento del contatto: campionata prima di MoveAndSlide, perche' dopo
        // la collisione l'ha gia' azzerata.
        if (!grounded)
            _fallSpeed = Mathf.Max(_fallSpeed, -velocity.Y);

        Velocity = velocity;
        MoveAndSlide();

        DetectLanding();
        RefreshAnchor();
        CheckWaterFallback();

        // Pubblica lo stato che verra' replicato agli altri peer.
        PublishState();
        UpdateFacing(worldDir, delta);
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
    /// Aggiorna l'accovacciamento e la capsula di collisione.
    ///
    /// Rialzarsi non e' garantito: se sopra la testa non c'e' spazio (<c>Headroom</c>) si resta giu'
    /// anche lasciando il tasto, altrimenti si finirebbe incastrati dentro il soffitto. In aria non si
    /// cambia postura, per non alterare la capsula a mezz'aria.
    /// </summary>
    private void UpdateCrouch(double delta, bool grounded)
    {
        bool wants = grounded && _input.ReadCrouch();

        if (!wants && SyncCrouching)
        {
            _headroom.ForceShapecastUpdate();
            if (_headroom.IsColliding())
                wants = true;
        }

        SyncCrouching = wants;

        // La capsula si accorcia dall'ALTO: il fondo resta dov'e', cosi' l'avatar non sprofonda ne'
        // viene espulso dal pavimento quando cambia postura.
        _crouchBlend = Mathf.MoveToward(_crouchBlend, SyncCrouching ? 1f : 0f, (float)delta * 6f);
        float height = Mathf.Lerp(_standHeight, _standHeight * CrouchHeightFactor, _crouchBlend);

        _capsule.Height = height;
        _collision.Position = new Vector3(0f, (height - _standHeight) * 0.5f, 0f);
    }

    /// Rileva il passaggio aria -> terra ed emette l'evento di atterraggio una sola volta.
    private void DetectLanding()
    {
        bool grounded = IsOnFloor();

        if (grounded && !_wasGrounded)
            Rpc(MethodName.BroadcastLand, _fallSpeed);

        if (grounded)
            _fallSpeed = 0f;

        _wasGrounded = grounded;
        SyncGrounded = grounded;
    }

    /// <summary>
    /// Orientamento dell'avatar.
    ///
    /// Da ARMATO guarda il punto di mira e si muove di lato: e' quello che rende sensato un
    /// BlendSpace2D a otto direzioni, perche' altrimenti si camminerebbe sempre in avanti e meta'
    /// delle clip non verrebbe mai usata. Da DISARMATO guarda dove va, che e' piu' naturale.
    ///
    /// Il punto di mira si legge da <see cref="WeaponInput.AimPoint"/>, che lo ricalcola gia' ogni
    /// frame per il tiro: non si duplica <see cref="AimResolver"/>.
    /// </summary>
    private void UpdateFacing(Vector3 worldDir, double delta)
    {
        float target = SyncFacing;
        bool armed = _weapon is { IsArmed: true } && _weaponInput != null;

        if (armed)
        {
            Vector3 toAim = _weaponInput!.AimPoint - GlobalPosition;
            toAim.Y = 0f;
            if (toAim.LengthSquared() > 0.0001f)
                target = Mathf.Atan2(toAim.X, toAim.Z);
        }
        else if (worldDir.LengthSquared() > 0.001f)
        {
            target = Mathf.Atan2(worldDir.X, worldDir.Z);
        }

        SyncFacing = Mathf.LerpAngle(SyncFacing, target, Mathf.Clamp((float)delta * TurnSpeed, 0f, 1f));
        _visual.Rotation = new Vector3(0f, SyncFacing, 0f);
    }

    /// Proietta la velocita' nel riferimento dell'avatar (vedi <see cref="SyncLocalVelocity"/>).
    private void PublishLocomotionState()
    {
        Vector3 planar = new(Velocity.X, 0f, Velocity.Z);
        Vector3 local = planar.Rotated(Vector3.Up, -SyncFacing);
        SyncLocalVelocity = new Vector2(local.X, local.Z);
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
        _visual.Rotation = new Vector3(0f, SyncFacing, 0f);
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
    /// <c>platform_floor_layers = 4294967279</c>, cioe' tutti i layer TRANNE il 5 "vehicles".
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

        float yaw = Mathf.LerpAngle(_visual.Rotation.Y, SyncFacing, t);
        _visual.Rotation = new Vector3(0f, yaw, 0f);
    }
}
