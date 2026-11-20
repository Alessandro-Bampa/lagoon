using Godot;

namespace Lagoon;

/// <summary>
/// Modalita' del giocatore. <c>OnFoot</c> copre anche lo stare su un ponte in movimento: quello non e'
/// una modalita' a se', e' <c>OnFoot</c> con un'ancora attiva. Un ramo separato duplicherebbe gravita'
/// e collisioni senza aggiungere nulla.
///
/// <c>Swimming</c> invece si' (cambia chi decide la verticale), ma come <c>Driving</c> non e' uno stato
/// locale: si deduce da <see cref="CharacterMotor.SyncSwimming"/>, replicato, quindi vale su ogni peer.
/// </summary>
public enum PlayerMode
{
    OnFoot,
    Driving,
    Swimming,
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

    /// <summary>
    /// Profondita' dell'acqua sopra i piedi a cui si comincia a nuotare, in metri: il petto. Si
    /// misura sulla superficie VERA (<see cref="WaterVolume.Sample"/>: marea e onde), non sul livello
    /// medio: cadendo in un cavo d'onda si e' gia' in acqua.
    /// </summary>
    [Export] public float SwimEnterDepth { get; set; } = 1.3f;

    /// <summary>
    /// Profondita' sotto la quale, coi piedi sul fondo, si torna a camminare. Piu' bassa di
    /// <see cref="SwimEnterDepth"/> (isteresi): con le onde la profondita' oscilla, e senza margine
    /// sulla riva si passerebbe da nuoto a camminata a ogni onda.
    /// </summary>
    [Export] public float SwimExitDepth { get; set; } = 0.95f;

    /// Tempo minimo in una modalita' (nuoto o a piedi) prima di poterla lasciare, in secondi.
    [Export] public float SwimModeMinSeconds { get; set; } = 0.4f;

    /// <summary>
    /// Rete di sicurezza: sotto questa profondita' rispetto al livello dell'acqua si rientra al
    /// <c>water_respawn</c>. Non e' piu' "chi cade in acqua torna al molo" (ora si nuota): serve
    /// solo a chi esce dal bordo del livello e precipita nel vuoto.
    /// </summary>
    [Export] public float VoidFallbackDepth { get; set; } = 40.0f;

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

    /// Sguardo massimo rispetto alla prua mentre si e' al timone, in gradi: oltre, ci si
    /// guarderebbe attraverso le spalle.
    [Export] public float HelmLookLimitDegrees { get; set; } = 110.0f;

    /// Frazione della velocita' di camminata a cui ci si muove con l'arma all'occhio.
    [Export] public float AimMoveFactor { get; set; } = 0.6f;

    /// <summary>
    /// La camera locale: e' la fonte di verita' dello sguardo. WASD si muove rispetto al suo
    /// <see cref="FirstPersonCamera.LookYaw"/>, il corpo lo insegue, e imbardata e inclinazione
    /// vengono pubblicate in <c>SyncAimYaw</c>/<c>SyncAimPitch</c>.
    /// </summary>
    private FirstPersonCamera _camera = null!;

    private WeaponController? _weapon;
    private RayCast3D _groundProbe = null!;
    private WaterVolume? _water;
    private int _ownerPeerId;

    // --- Solo peer autoritativo -----------------------------------------------------------
    private BoatController? _anchor;
    private Transform3D _lastAnchorFrame;
    private bool _hasAnchorFrame;

    /// Prua al tick precedente, per far girare lo sguardo insieme alla barca che si pilota.
    private float _lastHelmHeading;
    private bool _hasHelmHeading;

    /// <summary>
    /// Barca su cui sta il bordo della manovra di parkour in corso (o null): la traiettoria la segue
    /// (<see cref="CarryManeuver"/>). Serve soprattutto risalendo dall'acqua su uno scafo che beccheggia.
    /// </summary>
    private BoatController? _maneuverAnchor;
    private Transform3D _lastManeuverFrame;

    /// Secondi dall'ultimo cambio fra nuoto e piedi (vedi SwimModeMinSeconds).
    private float _swimModeTime = 10f;

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
    public PlayerMode Mode => _drivingBoat != null
        ? PlayerMode.Driving
        : (SyncSwimming ? PlayerMode.Swimming : PlayerMode.OnFoot);

    /// Veicolo che questo giocatore sta pilotando (o null).
    public BoatController? DrivingBoat => _drivingBoat;

    /// <summary>
    /// Posizione autoritativa in coordinate MONDO, ricostruita dallo stato replicato. Va usata al posto
    /// di <see cref="SyncPosition"/> in ogni calcolo host-side (vedi <c>WeaponController.RequestFire</c>):
    /// per un giocatore su un'imbarcazione <see cref="SyncPosition"/> e' locale allo scafo.
    /// </summary>
    public override Vector3 ResolvedSyncPosition
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
        _camera = GetNode<FirstPersonCamera>("PlayerCamera");
        _water = WaterVolume.Find(this);

        // Opzionali: un avatar puo' esistere senza armi, il movimento non deve dipenderne.
        _weapon = GetNodeOrNull<WeaponController>("Weapon");

        // Evita che gli avatar remoti "saltino" dall'origine al primo update.
        SyncAnchorId = 0;

        // Il proprietario muove il proprio avatar (Fase 1), ma lo spawner non gli porta la
        // posizione scelta dall'host: senza questo un client nasceva all'origine del mondo. Il
        // punto e' deterministico (GameWorld.SpawnPointFor), quindi coincide con quello dell'host.
        if (IsMultiplayerAuthority() && GetTree().GetFirstNodeInGroup(GameWorld.GroupName) is GameWorld world)
            GlobalPosition = world.SpawnPointFor(_ownerPeerId);
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
        _hasHelmHeading = false;
    }

    private void AuthoritativeMovement(double delta)
    {
        if (Mode == PlayerMode.Driving)
        {
            // Si prende il timone da bordo, ma lo stato va comunque chiuso: al rilascio si riparte a piedi.
            EndSwim();
            DrivingUpdate();
            return;
        }

        // Durante una manovra la segue il suo riferimento (CarryManeuver), non l'ancora dei piedi.
        if (Vaulting)
            CarryManeuver();
        else
            CarryWithAnchor();

        // WASD rispetto allo SGUARDO: avanti e' dove si guarda, sul piano orizzontale.
        Vector2 motion = _input.ReadMovement();
        float yaw = _camera.LookYaw;
        Vector3 forward = new(Mathf.Sin(yaw), 0f, Mathf.Cos(yaw));
        Vector3 right = new(-forward.Z, 0f, forward.X);
        Vector3 worldDir = forward * -motion.Y + right * motion.X;
        if (worldDir.LengthSquared() > 1f)
            worldDir = worldDir.Normalized();

        WaterSample? water = SampleWater();
        UpdateSwimMode(water, delta);

        bool armed = _weapon is { IsArmed: true };
        bool swimming = SyncSwimming && water.HasValue;
        bool sighting = armed && !swimming && _input.ReadAim();

        if (swimming)
        {
            // Spazio contro un bordo basso (o una scaletta) = issarsi fuori; altrimenti, tenuto, risalire.
            float surface = water!.Value.Height;
            float speed = _input.ReadSprint() ? SwimSprintSpeed : SwimSpeed;
            if (_input.ReadJumpPressed() && TryStartSwimClimb(worldDir, surface))
            {
                EndSwim();
                _swimModeTime = 0f;
            }
            else
            {
                StepSwim(worldDir, speed, _input.ReadJumpHeld(), _input.ReadDiveHeld(),
                    surface, water.Value.Velocity, delta);
            }
        }
        else
        {
            bool sprinting = !SyncCrouching && !sighting && _input.ReadSprint();
            float speed = SyncCrouching ? CrouchSpeed : (sprinting ? RunSpeed : WalkSpeed);
            if (sighting && !SyncCrouching)
                speed *= AimMoveFactor;

            // Accelerazione, pendenza, gradini, salto, gravita' e atterraggio: tutto nel motore condiviso.
            StepMotion(worldDir, speed, _input.ReadJumpPressed(), _input.ReadCrouch(), delta);
        }

        RefreshAnchor();
        CheckFallOutOfWorld();

        // Pubblica lo stato che verra' replicato agli altri peer.
        PublishState();
        UpdateView(sighting, delta);
        PublishLocomotionState();
    }

    /// <summary>
    /// Sguardo, orientamento del corpo e mira.
    ///
    /// Lo sguardo si pubblica SEMPRE (<c>SyncAimYaw</c>/<c>SyncAimPitch</c>): e' cio' che punta
    /// busto e arma sugli altri peer. Il corpo lo insegue smorzato — in prima persona si cammina
    /// in strafe, mai "verso dove si va" — e lo scarto residuo lo chiude BodyAimModifier.
    ///
    /// <c>SyncAiming</c> = arma all'occhio (tasto <c>aim</c> con un'arma in mano). Si replica
    /// perche' serve a due cose che il solo proprietario non puo' fare: mostrare la posa di mira
    /// sugli altri peer, e dire all'host che il cono di tiro e' quello stretto.
    ///
    /// Durante il parkour il corpo NON segue lo sguardo: TryStartParkour lo ha raddrizzato sulla
    /// normale della parete, e girarlo farebbe entrare la manovra di sbieco nel muro. Lo sguardo
    /// resta libero; l'arma non si vede e non spara (WeaponController.HandsFree).
    /// </summary>
    private void UpdateView(bool sighting, double delta)
    {
        _camera.AimFovDegrees = _weapon?.HeldWeapon?.AnimationSet?.AimFovDegrees ?? _camera.FovDegrees;

        SyncAimYaw = _camera.LookYaw;
        SyncAimPitch = _camera.LookPitch;

        // A nuoto il corpo segue lo sguardo come a piedi (sighting e' gia' falso: le mani nuotano):
        // il verso delle bracciate rispetto al moto lo decide CharacterAnimator.
        if (Vaulting)
        {
            SyncAiming = false;
            _camera.Zoomed = false;
            UpdateFacing(SyncFacing, delta);
            return;
        }

        SyncAiming = sighting;
        _camera.Zoomed = sighting;
        UpdateFacing(_camera.LookYaw, delta);
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

    protected override void OnVaultTriggered(Vector3 ledgePoint, Vector3 wallNormal, float height)
    {
        // Il riferimento della manovra e' il corpo su cui sta il BORDO, non quello sotto i piedi:
        // dal ponte di una barca alla banchina la traiettoria e' ferma nel mondo; dall'acqua (o dal
        // molo) sulla barca segue la barca. L'ancora dei piedi si riacquista a manovra finita.
        _maneuverAnchor = FindOwningBoat(ManeuverLedgeCollider as Node);
        if (_maneuverAnchor != null)
            _lastManeuverFrame = _maneuverAnchor.GlobalTransform;
        _anchor = null;
        _hasAnchorFrame = false;

        Rpc(MethodName.BroadcastVault, ledgePoint, wallNormal, height);
    }

    /// <summary>
    /// Trasporta la manovra in corso col suo riferimento: stesso delta di trasformata di
    /// <see cref="CarryWithAnchor"/>, applicato ai punti della traiettoria invece che al corpo
    /// (la posizione la riscrive comunque la traiettoria). Anche lo sguardo vira con la barca.
    /// </summary>
    private void CarryManeuver()
    {
        if (_maneuverAnchor == null || !GodotObject.IsInstanceValid(_maneuverAnchor))
            return;

        Transform3D now = _maneuverAnchor.GlobalTransform;
        ShiftManeuver(now * _lastManeuverFrame.AffineInverse());
        _camera.AddYaw(Mathf.AngleDifference(HeadingOf(_lastManeuverFrame), HeadingOf(now)));
        _lastManeuverFrame = now;
    }

    // ====================================================================================
    //  Acqua
    // ====================================================================================

    /// Superficie vera (marea + onde) sulla verticale del corpo, o null fuori dall'acqua del livello.
    private WaterSample? SampleWater()
    {
        if (_water == null || !_water.Contains(GlobalPosition))
            return null;
        return _water.Sample(GlobalPosition);
    }

    /// <summary>
    /// Decide fra nuoto e piedi, con isteresi di profondita' (<see cref="SwimEnterDepth"/> /
    /// <see cref="SwimExitDepth"/>) e di tempo (<see cref="SwimModeMinSeconds"/>).
    ///
    /// Client-autoritativo come tutto il movimento (CLAUDE.md §3, eccezione dichiarata della Fase 1):
    /// il peer proprietario decide e pubblica <see cref="CharacterMotor.SyncSwimming"/>, che viaggia
    /// nello stesso pacchetto della posizione. Gli altri peer lo leggono e basta.
    /// </summary>
    private void UpdateSwimMode(WaterSample? water, double delta)
    {
        _swimModeTime += (float)delta;
        if (Vaulting)
            return;

        float feetY = GlobalPosition.Y - StandHeight * 0.5f;

        if (!SyncSwimming)
        {
            if (water is { } w && w.Height - feetY > SwimEnterDepth && _swimModeTime >= SwimModeMinSeconds)
            {
                BeginSwim();
                _swimModeTime = 0f;

                // In acqua non si e' trasportati da niente: l'ancora (un ponte) non vale piu'.
                _anchor = null;
                _hasAnchorFrame = false;
            }
            return;
        }

        // Fuori dall'area d'acqua si esce subito, senza attendere: sotto non c'e' piu' niente che regga.
        bool leave = water == null
            || (_swimModeTime >= SwimModeMinSeconds && IsOnFloor() && water.Value.Height - feetY < SwimExitDepth);
        if (leave)
        {
            EndSwim();
            _swimModeTime = 0f;
        }
    }

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
        AlignVisualWithBoat(boat);

        // Al timone non si e' accovacciati: gli occhi da seduti li mette FirstPersonCamera
        // (SeatedEyeHeight), e l'arma e' riposta (nessun colpo parte da qui).
        SyncCrouching = false;

        // Il corpo guarda la prua; lo sguardo resta libero ma vira con la barca, entro il limite.
        if (_hasHelmHeading)
            _camera.AddYaw(Mathf.AngleDifference(_lastHelmHeading, SyncFacing));
        _lastHelmHeading = SyncFacing;
        _hasHelmHeading = true;
        _camera.ClampYawAround(SyncFacing, HelmLookLimitDegrees);
        _camera.Zoomed = false;

        SyncAiming = false;
        SyncAimYaw = _camera.LookYaw;
        SyncAimPitch = _camera.LookPitch;
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
        {
            GlobalPosition = now * (_lastAnchorFrame.AffineInverse() * GlobalPosition);

            // Anche lo sguardo e' trasportato: su un ponte che vira si resta rivolti allo stesso
            // punto dello SCAFO, non del mondo, altrimenti la barca "scappa" da sotto la visuale.
            _camera.AddYaw(Mathf.AngleDifference(HeadingOf(_lastAnchorFrame), HeadingOf(now)));
        }

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
    /// Il raggio e' lungo 1,5 m dal centro (0,5 sotto i piedi): chi sta sullo spigolo di una panca ha
    /// il centro oltre il bordo, e il raggio deve arrivare al pagliolo sotto.
    /// </summary>
    private void RefreshAnchor()
    {
        // A nuoto non si e' appoggiati a nulla; durante una manovra il riferimento e' quello del bordo.
        if (SyncSwimming || Vaulting)
        {
            _anchor = null;
            _hasAnchorFrame = false;
            return;
        }

        // Manovra finita: il suo riferimento non serve piu', l'ancora la ritrova il GroundProbe.
        _maneuverAnchor = null;

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

    /// Imbardata di una trasformata, nella stessa convenzione di <c>BoatController.HeadingYaw</c>.
    private static float HeadingOf(Transform3D frame)
    {
        Vector3 forward = -frame.Basis.Z;
        return Mathf.Atan2(forward.X, forward.Z);
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
    /// Rete di sicurezza per chi esce dal bordo del livello: molto sotto il livello dell'acqua si
    /// rientra al <c>water_respawn</c>. In acqua si nuota (vedi <see cref="UpdateSwimMode"/>), quindi
    /// qui si arriva solo precipitando dove non c'e' ne' acqua ne' fondale. Client-autoritativo come
    /// tutto il movimento della Fase 1.
    /// </summary>
    private void CheckFallOutOfWorld()
    {
        if (_water == null || GlobalPosition.Y > _water.SurfaceY - VoidFallbackDepth)
            return;

        if (GetTree().GetFirstNodeInGroup("water_respawn") is not Node3D respawn)
            return;

        EndSwim();
        CancelParkour();
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
        if (Mode == PlayerMode.Driving && DrivingBoat is BoatController boat)
            AlignVisualWithBoat(boat);
    }

    /// <summary>
    /// Al timone il corpo segue beccheggio e rollio dello scafo: diritto, su una barca inclinata
    /// dall'onda, finirebbe dentro il sedile o sopra la consolle. Il Visual guarda +Z, la prua e' -Z
    /// della barca: mezzo giro attorno alla verticale della barca. Pura resa, su ogni peer.
    /// </summary>
    private void AlignVisualWithBoat(BoatController boat)
    {
        Visual.GlobalBasis = boat.GlobalBasis.Orthonormalized() * new Basis(Vector3.Up, Mathf.Pi);
    }
}
