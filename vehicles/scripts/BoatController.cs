using Godot;

namespace Lagoon;

/// <summary>
/// Una barca su misura: corpo rigido mosso dal nucleo fisico per triangoli (<see cref="BoatPhysics"/>,
/// skill vehicles-boats §10) che monta il figlio <see cref="BoatAssembly"/> da una <see cref="BoatConfig"/>.
/// Massa, baricentro, inerzia e smorzamenti del corpo li scrive il codice: i valori dell'ispettore del
/// <c>RigidBody3D</c> non contano.
///
/// A differenza del movimento del giocatore, che e' client-authoritative (eccezione dichiarata della
/// Fase 1), la fisica della barca e' **interamente host-autoritativa** (CLAUDE.md §3): solo l'host
/// integra le forze; i client congelano il corpo (<c>Freeze</c>) e ne interpolano la posa replicata.
/// Il discriminante rispetto a "il client simula in parallelo" e' verificabile: se l'host smette di
/// inviare stato, la barca si ferma invece di derivare, perche' il bersaglio dell'interpolazione e'
/// funzione esclusiva dei dati ricevuti.
///
/// Il client invia solo un INTENTO di guida (acceleratore/timone), e solo se e' il pilota registrato
/// dall'host. Nessun client decide di essere al timone: <see cref="PilotPeerId"/> e' una proprieta'
/// REPLICATA dall'host, non un broadcast via RPC — cosi' anche un late-joiner vede lo stato giusto.
///
/// L'acqua non ha corpo fisico: la superficie (marea + onde) la campiona il solutore per ogni vertice
/// della mesh idrodinamica.
/// </summary>
public partial class BoatController : RigidBody3D
{
    /// Gruppo per raggiungere i veicoli senza path fragili (come <see cref="ItemPickup.GroupName"/>).
    public const string GroupName = "vehicle";

    // ====================================================================================
    //  Identita' e parametri
    // ====================================================================================

    /// <summary>
    /// Identita' cross-peer del veicolo. Come <see cref="ItemPickup.Uid"/>: si cerca per questo id, non
    /// per NodePath (fragile) ne' per nome del nodo. 0 non e' un id valido: 0 significa "nessun veicolo".
    /// </summary>
    [Export] public int VehicleId { get; set; } = 1;

    /// <summary>
    /// Massa di una persona a bordo SENZA equipaggiamento (kg): con l'inventario (host) fa il peso
    /// che sbanda e appoppa la barca (vedi <see cref="BoatMassModel"/>).
    /// </summary>
    [Export] public float CrewBodyMass { get; set; } = 80f;

    /// Distanza massima dal timone per poterlo prendere (validata dall'host, non dal client).
    [Export] public float HelmRange { get; set; } = 3.0f;
    /// Se non arriva nessun intento per questo tempo, i comandi si azzerano (vedi <see cref="HostSimulate"/>).
    [Export] public float InputTimeoutSeconds { get; set; } = 0.5f;
    /// Reattivita' dell'interpolazione di presentazione sui client.
    [Export] public float RemoteLerpSpeed { get; set; } = 14.0f;

    /// <summary>
    /// Solo per il collaudo (<c>tools/boat_trials.gd</c>, <c>playtest_water.gd</c>): comandi imposti
    /// senza pilota, motore acceso. Da spegnere.
    /// </summary>
    [Export] public bool DebugAutoDrive { get; set; }
    [Export] public float DebugSteer { get; set; }
    [Export] public float DebugThrottle { get; set; } = 1f;
    [Export] public float DebugTrimCommand { get; set; }
    [Export] public bool DebugAutoTrim { get; set; } = true;
    [Export] public float DebugThruster { get; set; }

    // ====================================================================================
    //  Stato replicato (Boat/Sync, autorita' host, visibile a tutti)
    // ====================================================================================

    [Export] public Vector3 SyncBodyPosition { get; set; }
    [Export] public Quaternion SyncBodyRotation { get; set; } = Quaternion.Identity;
    [Export] public Vector3 SyncLinearVelocity { get; set; }
    [Export] public Vector3 SyncAngularVelocity { get; set; }

    /// <summary>
    /// Peer del pilota; 0 = timone libero. E' il RISULTATO di una decisione dell'host, quindi si
    /// replica e non si broadcasta (CLAUDE.md §3: una RPC accetta un intento, non un risultato).
    /// </summary>
    [Export] public int PilotPeerId { get; set; }

    /// <summary>
    /// Stato degli attuatori, deciso dall'host dall'intento del pilota e replicato perche' ogni peer
    /// veda motore, timone, elica e volante (e, in futuro, ne senta il suono): leva (−1..1), angolo di
    /// governo (rad: fuoribordo sul piantone o timone; positivo = vira a dritta), trim (rad), giri del
    /// motore e dell'elica (con segno: negativi in retromarcia).
    /// </summary>
    [Export] public float SyncThrottle { get; set; }
    [Export] public float SyncMotorAngle { get; set; }
    [Export] public float SyncTrim { get; set; }
    [Export] public float SyncEngineRpm { get; set; }
    [Export] public float SyncPropRpm { get; set; }

    // ====================================================================================
    //  Stato locale
    // ====================================================================================

    // Solo host: ultimo intento ricevuto dal pilota.
    private float _throttle;
    private float _steer;
    private float _trimCommand;
    private float _thruster;
    private bool _manualTrim;
    private float _sinceLastInput;
    private float _sinceCrewUpdate;
    private readonly System.Collections.Generic.HashSet<int> _crewSeen = new();

    // Solo client: eta' dell'ultimo pacchetto di stato ricevuto, per l'extrapolazione.
    private float _sinceLastState;
    private bool _gotFirstState;

    /// <summary>
    /// Rete di sicurezza: se per qualunque motivo non arrivasse nessuno stato, dopo questo tempo lo
    /// scafo si mostra comunque alla posa che ha. Una barca invisibile ma SOLIDA e' un guasto peggiore
    /// di una barca disegnata nel posto sbagliato per un istante.
    /// </summary>
    private const float FirstStateTimeoutSeconds = 2.0f;

    /// <summary>
    /// Tetto all'extrapolazione (3 intervalli di replica). Senza questo tetto il termine
    /// <c>velocita' * eta' del pacchetto</c> cresce senza limite: a pacchetti persi la barca schizza in
    /// avanti, e alla caduta dell'host se ne va all'infinito. Col tetto il bersaglio si assesta e la
    /// barca si FERMA quando l'host tace — che e' l'invariante di §3 da cui dipende tutto il resto.
    /// </summary>
    private const float MaxExtrapolationSeconds = 0.15f;

    private Marker3D _helmSeat = null!;
    private Node3D _visual = null!;
    private WaterVolume? _water;
    private BoatAssembly? _assembly;
    private BoatPhysics? _physics;
    private BoatMassModel? _massModel;
    private EventBus _eventBus = null!;

    /// Nucleo fisico per triangoli (null = configurazione o mesh idrodinamica mancante: barca ferma).
    public BoatPhysics? Physics => _physics;

    public bool HasPilot => PilotPeerId != 0;

    /// Montaggio da configurazione (scafo, propulsione, consolle, masse).
    public BoatAssembly? Assembly => _assembly;

    /// <summary>
    /// Posizione del posto di guida nello spazio LOCALE della barca: e' esattamente la
    /// <see cref="PlayerController.SyncPosition"/> che il pilota pubblica. Costante.
    /// </summary>
    public Vector3 HelmLocalPosition => _helmSeat.Position;

    public Vector3 HelmGlobalPosition => _helmSeat.GlobalPosition;

    /// <summary>
    /// Imbardata dello scafo nella convenzione usata da <see cref="PlayerController.SyncFacing"/>
    /// (angolo che porta +Z locale sulla direzione data): serve a far guardare il pilota verso prua.
    /// </summary>
    public float HeadingYaw
    {
        get
        {
            Vector3 forward = -GlobalTransform.Basis.Z;
            return Mathf.Atan2(forward.X, forward.Z);
        }
    }

    // ====================================================================================
    //  Ciclo di vita
    // ====================================================================================

    public override void _EnterTree()
    {
        // Host-autoritativa. La barca sta nel livello, non sotto Player, quindi il set ricorsivo di
        // PlayerController._EnterTree non la sfiora; lo impostiamo comunque esplicitamente per
        // simmetria con WeaponController e perche' il MultiplayerSynchronizer figlio erediti l'autorita'.
        SetMultiplayerAuthority(NetworkConstants.HostPeerId);
    }

    public override void _Ready()
    {
        AddToGroup(GroupName);

        _helmSeat = GetNode<Marker3D>("HelmSeat");
        _visual = GetNode<Node3D>("Visual");
        _eventBus = GetNode<EventBus>("/root/EventBus");
        _water = WaterVolume.Find(this);
        _assembly = GetNodeOrNull<BoatAssembly>("Assembly");

        // Massa, baricentro e inerzia li ha gia' scritti BoatMassModel (Assembly._Ready viene prima).
        // Gravita' sommata dal solutore (la massa aggiunta la scala), nessuno smorzamento del motore
        // fisico (agirebbe anche in aria e senza dipendere dall'immersione), urti veloci col CCD.
        FreezeMode = FreezeModeEnum.Kinematic;
        CanSleep = false;
        GravityScale = 0f;
        LinearDampMode = DampMode.Replace;
        LinearDamp = 0f;
        AngularDampMode = DampMode.Replace;
        AngularDamp = 0f;
        ContinuousCd = true;

        _physics = _assembly?.Physics;
        _massModel = _assembly?.MassModel;
        if (_physics != null)
            _physics.UpdateStatics(Mass, _water?.Density ?? 1025f);
        else
            GD.PushError($"[BoatController] '{Name}': manca la configurazione o la mesh HydroHull dello scafo; la barca resta ferma.");

        SyncBodyPosition = GlobalPosition;
        SyncBodyRotation = GlobalBasis.GetRotationQuaternion();

        // L'arrivo di uno stato si prende dal segnale del Synchronizer, NON confrontando i valori
        // replicati: con la barca all'ormeggio lo stato ricevuto e' identico a quello dell'editor,
        // quindi un confronto non scatterebbe mai e lo scafo resterebbe invisibile fino al primo
        // movimento (bug osservato in multi-macchina).
        GetNode<MultiplayerSynchronizer>("Sync").Synchronized += OnStateReceived;

        _eventBus.PeerLeft += OnPeerLeft;
        _eventBus.PeerJoined += OnNetworkChanged;
        _eventBus.ConnectedToServer += OnNetworkStateChanged;
        _eventBus.NetworkError += OnNetworkFailed;

        ApplySimulationMode();
    }

    public override void _ExitTree()
    {
        _eventBus.PeerLeft -= OnPeerLeft;
        _eventBus.PeerJoined -= OnNetworkChanged;
        _eventBus.ConnectedToServer -= OnNetworkStateChanged;
        _eventBus.NetworkError -= OnNetworkFailed;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (IsMultiplayerAuthority())
            HostSimulate(delta);
        else
            RemotePresentation(delta);
    }

    public override void _Process(double delta)
    {
        // Resa del governo su ogni peer, dallo stato replicato (sull'host e' lo stesso valore).
        _assembly?.ApplyHelmPose(SyncMotorAngle, SyncTrim, SyncPropRpm, delta);
    }

    /// <summary>
    /// Passo del nucleo fisico (solo host, solo barche montate da configurazione): legge lo stato del
    /// corpo nel passo stesso e applica UNA forza centrale e UNA coppia (vedi <see cref="BoatPhysics"/>).
    /// </summary>
    public override void _IntegrateForces(PhysicsDirectBodyState3D state)
    {
        if (_physics == null || Freeze || !IsMultiplayerAuthority())
            return;
        _physics.Step(state, Mass, _massModel?.Inertia ?? Inertia, _water, CurrentCommands());
    }

    private BoatCommands CurrentCommands() => new()
    {
        Throttle = _throttle,
        Steer = _steer,
        Trim = _trimCommand,
        Thruster = _thruster,
        AutoTrim = !_manualTrim,
        Running = HasPilot || DebugAutoDrive,
    };

    /// <summary>
    /// Sceglie fra simulazione e presentazione. Va richiamata a ogni cambio di stato della rete e non
    /// una volta sola in <c>_Ready</c>: il livello viene caricato PRIMA che esista un peer, e con
    /// <c>MultiplayerPeer == null</c> <see cref="Node.IsMultiplayerAuthority"/> e' vero anche su un
    /// futuro client — che continuerebbe a simulare in parallelo all'host (violazione di §3).
    /// </summary>
    private void ApplySimulationMode()
    {
        bool host = IsMultiplayerAuthority();
        // Senza nucleo fisico (configurazione rotta) la barca resta ferma anche sull'host: niente
        // gravita' senza galleggiamento.
        Freeze = !host || _physics == null;

        if (host)
            return;

        // Il client non integra nulla e non deve nemmeno mostrare la posa dell'editor finche' non
        // arriva il primo pacchetto (altrimenti un late-joiner vede la barca all'ormeggio iniziale).
        LinearVelocity = Vector3.Zero;
        AngularVelocity = Vector3.Zero;
        _visual.Visible = _gotFirstState;
    }

    private void OnNetworkStateChanged() => ApplySimulationMode();
    private void OnNetworkChanged(long peerId) => ApplySimulationMode();
    private void OnNetworkFailed(string message) => ApplySimulationMode();

    private void OnPeerLeft(long peerId)
    {
        if (!IsMultiplayerAuthority() || PilotPeerId != (int)peerId)
            return;

        // Il pilota si e' disconnesso: timone libero e comandi a zero. Il timeout di
        // InputTimeoutSeconds e' la seconda rete di sicurezza, indipendente da questo segnale.
        PilotPeerId = 0;
        ResetControls();
    }

    /// Comandi a zero e trim restituito all'assistenza (nuovo pilota o timone lasciato).
    private void ResetControls()
    {
        _throttle = 0f;
        _steer = 0f;
        _trimCommand = 0f;
        _thruster = 0f;
        _manualTrim = false;
    }

    // ====================================================================================
    //  Simulazione (solo host)
    // ====================================================================================

    private void HostSimulate(double delta)
    {
        _sinceLastInput += (float)delta;
        if (!HasPilot || _sinceLastInput > InputTimeoutSeconds)
        {
            // Nessun pilota, o il pilota ha smesso di parlare (pacchetti persi, client bloccato,
            // processo morto prima che ENet lo rilevi): la barca va in folle, non resta a tutta forza.
            _throttle = 0f;
            _steer = 0f;
            _trimCommand = 0f;
            _thruster = 0f;
        }

        if (DebugAutoDrive)
        {
            _throttle = DebugThrottle;
            _steer = DebugSteer;
            _trimCommand = DebugTrimCommand;
            _manualTrim = !DebugAutoTrim;
            _thruster = DebugThruster;
        }

        // Le forze sono in _IntegrateForces; qui solo le masse (prima del passo) e la pubblicazione.
        HostUpdateCrew(delta);
        HostPublishState();
    }

    /// <summary>
    /// Chi sta a bordo pesa (10 Hz): ogni giocatore ancorato a questa barca (passeggero o pilota) e'
    /// una massa dinamica ai suoi piedi, corpo + equipaggiamento. La posizione e' quella REPLICATA
    /// nello spazio della barca (<see cref="PlayerController.SyncPosition"/> con l'ancora), cosi'
    /// spostarsi sul ponte sposta il baricentro. Massa e inerzia si riscrivono solo se cambiano.
    /// </summary>
    private void HostUpdateCrew(double delta)
    {
        if (_massModel == null)
            return;
        _sinceCrewUpdate += (float)delta;
        if (_sinceCrewUpdate < 0.1f)
            return;
        _sinceCrewUpdate = 0f;

        _crewSeen.Clear();
        if (GetTree().GetFirstNodeInGroup(GameWorld.GroupName) is GameWorld world)
        {
            float bodyCenter = _assembly?.PilotBodyCenter ?? 1f;
            foreach (PlayerController player in world.AllPlayers())
            {
                if (player.SyncAnchorId != VehicleId || !int.TryParse(player.Name, out int peer))
                    continue;
                float carried = player.GetNodeOrNull<PlayerInventory>("Inventory")?.Model.TotalWeight() ?? 0f;
                _massModel.SetDynamic(peer, CrewBodyMass + carried, player.SyncPosition - Vector3.Up * bodyCenter);
                _crewSeen.Add(peer);
            }
        }
        foreach (int key in _debugMasses)
            _crewSeen.Add(key);
        _massModel.RetainDynamic(_crewSeen);

        if (_massModel.Apply(this))
            _physics?.UpdateStatics(Mass, _water?.Density ?? 1025f);
    }

    private readonly System.Collections.Generic.HashSet<int> _debugMasses = new();

    /// <summary>
    /// Solo collaudo: una massa (kg) a bordo nel punto <paramref name="localPosition"/> (spazio della
    /// barca), identificata da <paramref name="key"/> (negativa: non collide coi peer). Massa 0 la toglie.
    /// </summary>
    public void DebugSetMass(int key, float mass, Vector3 localPosition)
    {
        if (_massModel == null)
            return;
        if (mass > 0f)
            _debugMasses.Add(key);
        else
            _debugMasses.Remove(key);
        _massModel.SetDynamic(key, mass, localPosition);
        _sinceCrewUpdate = 1f;
    }

    /// <summary>
    /// Solo collaudo: braccio raddrizzante GZ (m) in acqua piatta allo sbandamento dato, a
    /// dislocamento costante (stessa idrostatica del runtime). GM ≈ GZ / sin φ per piccoli angoli.
    /// Positivo = la barca torna dritta.
    /// </summary>
    public float DebugRightingArm(float heelDegrees)
    {
        if (_physics == null)
            return 0f;
        float volume = Mass / (_water?.Density ?? 1025f);
        return _physics.Hydro.Hull.RightingArm(volume, CenterOfMass, Mathf.DegToRad(heelDegrees));
    }

    /// Solo collaudo: trim del fuoribordo imposto (gradi) e lasciato al comando manuale.
    public void DebugSetTrimDegrees(float degrees)
    {
        _physics?.Outboard?.SetTrim(Mathf.DegToRad(degrees));
        DebugAutoTrim = false;
        DebugTrimCommand = 0f;
    }

    /// <summary>
    /// Telemetria dell'host per collaudo e HUD di prova (nodi, giri, spinta, resistenze, assetto,
    /// sbandamento, Froude, impatti, ventilazione, massa e baricentro).
    /// </summary>
    public Godot.Collections.Dictionary DebugTelemetry()
    {
        var d = new Godot.Collections.Dictionary();
        Basis b = GlobalBasis;
        Vector3 fwd = -b.Z;
        d["pitch_deg"] = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(fwd.Y, -1f, 1f)));
        d["heel_deg"] = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(-b.X.Y, -1f, 1f)));
        d["mass"] = Mass;
        d["com"] = CenterOfMass;
        d["inertia"] = Inertia;
        if (_physics == null)
            return d;
        HydroSolver h = _physics.Hydro;
        d["speed_kn"] = h.SpeedThroughWater / 0.514444f;
        d["speed_ground_kn"] = new Vector2(LinearVelocity.X, LinearVelocity.Z).Length() / 0.514444f;
        d["immersed_area"] = h.ImmersedArea;
        d["static_area"] = _physics.StaticImmersedArea;
        d["static_heave"] = _physics.StaticHeave;
        d["volume"] = h.DisplacedVolume;
        d["wetted_length"] = h.WettedLength;
        d["fn"] = h.Froude;
        d["fnv"] = h.FroudeVolumetric;
        d["drag_viscous"] = h.ViscousDrag;
        d["drag_pressure"] = h.PressureDrag;
        d["drag_wave"] = h.WaveDrag;
        d["slam_g"] = _physics.LastSlamG;
        d["invalid_steps"] = _physics.InvalidSteps;
        d["clamped_steps"] = _physics.ClampedSteps;
        d["water_height"] = h.MeanWaterHeight;
        d["drive"] = _physics.Outboard != null ? "outboard" : _physics.Shaft != null ? "inboard" : "";
        d["thruster"] = _physics.Thruster != null;
        d["com_physics"] = GlobalTransform.AffineInverse() * _physics.Context.Com;
        d["lcb_static"] = _physics.StaticBuoyancyCenter.Z;
        d["step_ms"] = _physics.StepMilliseconds;
        d["hull_length"] = h.Hull.Bounds.Size.Z;
        d["hull_beam"] = h.Hull.Bounds.Size.X;
        d["hull_bottom"] = h.Hull.Bounds.Position.Y;
        d["hull_triangles"] = h.Hull.TriangleCount;
        d["hull_volume"] = h.Hull.Volume;
        PropulsionUnit? unit = _physics.Outboard?.Unit ?? _physics.Shaft?.Unit;
        if (unit != null)
        {
            d["rpm"] = unit.Engine.Rpm;
            d["gear"] = unit.Engine.Gear;
            d["throttle"] = unit.Engine.Throttle;
            d["thrust"] = unit.Thrust;
            d["thrust_net"] = unit.Thrust * (1f - unit.Propeller.ThrustDeduction);
            d["prop_torque"] = unit.Torque;
            d["prop_depth"] = unit.Depth;
            d["prop_immersion"] = unit.Immersion;
            d["slip"] = unit.Slip;
            d["ventilating"] = unit.Ventilating;
            d["limiter"] = unit.Engine.Limiting;
            d["fuel_kg_h"] = unit.Engine.FuelRate * 3600f;
        }
        d["steer_deg"] = Mathf.RadToDeg(SyncMotorAngle);
        d["trim_deg"] = Mathf.RadToDeg(SyncTrim);
        return d;
    }

    private void HostPublishState()
    {
        SyncThrottle = _throttle;
        if (_physics?.Outboard is OutboardDrive outboard)
        {
            SyncMotorAngle = outboard.SteerAngle;
            SyncTrim = outboard.Trim;
        }
        else if (_physics?.Shaft is ShaftDrive shaft)
        {
            SyncMotorAngle = shaft.RudderAngle;
        }
        PropulsionUnit? unit = _physics?.Outboard?.Unit ?? _physics?.Shaft?.Unit;
        SyncEngineRpm = unit?.Engine.Rpm ?? 0f;
        SyncPropRpm = (unit?.Revs ?? 0f) * 60f;
        SyncBodyPosition = GlobalPosition;
        SyncBodyRotation = GlobalBasis.GetRotationQuaternion();
        SyncLinearVelocity = LinearVelocity;
        SyncAngularVelocity = AngularVelocity;
    }

    // ====================================================================================
    //  Presentazione (solo client)
    // ====================================================================================

    /// <summary>
    /// Uno stato dall'host e' arrivato (segnale del <c>MultiplayerSynchronizer</c> figlio). Serve solo a
    /// datare l'extrapolazione e a rivelare lo scafo al primo pacchetto: non entra in nessun calcolo di
    /// gioco, quindi non e' un'eccezione a §3.
    /// </summary>
    private void OnStateReceived()
    {
        _sinceLastState = 0f;
        if (_gotFirstState)
            return;

        // Primo stato: ci si aggancia di netto, senza interpolare da una posa arbitraria.
        _gotFirstState = true;
        _visual.Visible = true;
        GlobalPosition = SyncBodyPosition;
        Quaternion = SyncBodyRotation.Normalized();
    }

    private void RemotePresentation(double delta)
    {
        _sinceLastState += (float)delta;

        if (!_gotFirstState)
        {
            if (_sinceLastState > FirstStateTimeoutSeconds)
            {
                _gotFirstState = true;
                _visual.Visible = true;
            }
            return;
        }

        // Presentazione, non simulazione: si stima dove sara' la posa REPLICATA adesso (le velocita'
        // arrivano dall'host) e ci si avvicina con un lerp. Nessuna forza, nessuna integrazione locale:
        // se l'host tace, il bersaglio resta fermo e la barca si arresta invece di divergere.
        float t = Mathf.Clamp((float)delta * RemoteLerpSpeed, 0f, 1f);
        float age = Mathf.Min(_sinceLastState, MaxExtrapolationSeconds);
        Vector3 target = SyncBodyPosition + SyncLinearVelocity * age;
        GlobalPosition = GlobalPosition.Lerp(target, t);

        // Slerp e non lerp di Eulero: col beccheggio del galleggiamento interpolare gli angoli di
        // Eulero e' sbagliato vicino al wrap.
        Quaternion = Quaternion.Slerp(SyncBodyRotation.Normalized(), t).Normalized();
    }

    // ====================================================================================
    //  Submit / Request (forma canonica CLAUDE.md §3)
    // ====================================================================================

    /// Chiamata dal proprietario dell'avatar. Host = locale; client = RpcId(host).
    public void SubmitTakeHelm()
    {
        if (IsMultiplayerAuthority())
            RequestTakeHelm();
        else
            RpcId(NetworkConstants.HostPeerId, MethodName.RequestTakeHelm);
    }

    public void SubmitLeaveHelm()
    {
        if (IsMultiplayerAuthority())
            RequestLeaveHelm();
        else
            RpcId(NetworkConstants.HostPeerId, MethodName.RequestLeaveHelm);
    }

    /// <summary>
    /// Intento di guida: leva, governo, trim (−1 giu' / +1 su / 0) ed elica di prua, tutti −1..1 e
    /// relativi al veicolo.
    /// </summary>
    public void SubmitControls(float throttle, float steer, float trim, float thruster)
    {
        if (IsMultiplayerAuthority())
            RequestControls(throttle, steer, trim, thruster);
        else
            RpcId(NetworkConstants.HostPeerId, MethodName.RequestControls, throttle, steer, trim, thruster);
    }

    /// <summary>
    /// Intento "voglio il timone". Chiunque puo' chiederlo, quindi qui NON si valida contro
    /// <see cref="PilotPeerId"/>: si valida che il timone sia libero e che il richiedente sia
    /// davvero accanto ad esso, usando la posizione REPLICATA del suo avatar (mai un dato del client).
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void RequestTakeHelm()
    {
        if (!IsMultiplayerAuthority() || HasPilot)
            return;

        int sender = ResolveSender();
        PlayerController? player = VehicleRegistry.FindPlayer(this, sender);
        if (player == null)
            return;

        if (player.ResolvedSyncPosition.DistanceTo(HelmGlobalPosition) > HelmRange)
            return;

        // Dall'acqua il timone e' a portata di braccio ma non di mano: prima si sale a bordo.
        if (player.SyncSwimming)
            return;

        PilotPeerId = sender;
        ResetControls();
        _sinceLastInput = 0f;
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void RequestLeaveHelm()
    {
        if (!ValidatePilot())
            return;

        PilotPeerId = 0;
        ResetControls();
    }

    /// <summary>
    /// Intento di guida. Unreliable e a rate fisso: il pilota lo invia sempre, anche quando i comandi
    /// non cambiano, perche' un pacchetto "acceleratore a zero" perso lascerebbe la barca a tutta forza.
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    public void RequestControls(float throttle, float steer, float trim, float thruster)
    {
        if (!ValidatePilot())
            return;

        // Un valore non finito (o enorme) sparerebbe la barca fuori dal mondo: si rifiuta e si satura.
        if (!float.IsFinite(throttle) || !float.IsFinite(steer) || !float.IsFinite(trim) || !float.IsFinite(thruster))
            return;

        _throttle = Mathf.Clamp(throttle, -1f, 1f);
        _steer = Mathf.Clamp(steer, -1f, 1f);
        _trimCommand = Mathf.Clamp(trim, -1f, 1f);
        _thruster = Mathf.Clamp(thruster, -1f, 1f);
        // Toccare il trim lo toglie all'assistenza fino a quando si lascia il timone.
        if (Mathf.Abs(_trimCommand) > 0.01f)
            _manualTrim = true;
        _sinceLastInput = 0f;
    }

    /// Peer mittente dell'RPC corrente; 0 significa "chiamata locale dell'host".
    private int ResolveSender()
    {
        int sender = Multiplayer.GetRemoteSenderId();
        return sender == 0 ? NetworkConstants.HostPeerId : sender;
    }

    /// <summary>
    /// Guardia per guida e abbandono: il mittente deve essere il pilota registrato DALL'HOST.
    /// Differenza da <c>WeaponController.ValidateSender()</c>: la' il mittente atteso e' un
    /// <c>_ownerPeerId</c> fisso, qui e' uno stato dinamico posseduto dall'host — ed e' esattamente il
    /// punto di validazione che un MultiplayerSynchronizer con autorita' sul pilota non avrebbe.
    /// </summary>
    private bool ValidatePilot()
    {
        if (!IsMultiplayerAuthority())
            return false;
        return ResolveSender() == PilotPeerId;
    }
}
