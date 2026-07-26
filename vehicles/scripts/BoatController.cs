using Godot;

namespace Lagoon;

/// <summary>
/// Imbarcazione a galleggiamento per punti (Fase 4).
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
/// L'acqua e' un piano piatto a quota costante (<see cref="WaterVolume.SurfaceY"/>): nessuna onda e
/// soprattutto nessun corpo fisico per l'acqua.
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

    [Export] public float ThrustForce { get; set; } = 9000f;
    /// La retromarcia e' volutamente piu' debole della marcia avanti.
    [Export] public float ReverseFactor { get; set; } = 0.4f;
    [Export] public float SteerTorque { get; set; } = 12000f;

    /// Profondita' oltre la quale la spinta di galleggiamento non cresce piu' (metri).
    [Export] public float MaxSubmerge { get; set; } = 0.8f;
    /// Smorzamento verticale: senza questo la barca oscilla come una molla non smorzata.
    [Export] public float VerticalDampFactor { get; set; } = 1.5f;
    /// Attrito di chiglia: senza questo la barca scivola di lato come sul ghiaccio.
    [Export] public float LateralDragFactor { get; set; } = 4.0f;

    /// Distanza massima dal timone per poterlo prendere (validata dall'host, non dal client).
    [Export] public float HelmRange { get; set; } = 3.0f;
    /// Se non arriva nessun intento per questo tempo, i comandi si azzerano (vedi <see cref="HostSimulate"/>).
    [Export] public float InputTimeoutSeconds { get; set; } = 0.5f;
    /// Reattivita' dell'interpolazione di presentazione sui client.
    [Export] public float RemoteLerpSpeed { get; set; } = 14.0f;

    /// Solo per il collaudo (fasi 1-3): forza l'acceleratore a tutta senza pilota. Da spegnere.
    [Export] public bool DebugAutoDrive { get; set; }
    [Export] public float DebugSteer { get; set; }

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

    // ====================================================================================
    //  Stato locale
    // ====================================================================================

    // Solo host: ultimo intento ricevuto dal pilota.
    private float _throttle;
    private float _steer;
    private float _sinceLastInput;

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

    private Marker3D[] _floaters = null!;
    private Marker3D _helmSeat = null!;
    private Node3D _visual = null!;
    private WaterVolume? _water;
    private EventBus _eventBus = null!;
    private float _gravity;

    public bool HasPilot => PilotPeerId != 0;

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

        var floaters = new Godot.Collections.Array<Marker3D>();
        foreach (Node child in GetNode<Node3D>("Floaters").GetChildren())
            if (child is Marker3D marker)
                floaters.Add(marker);
        _floaters = [.. floaters];

        _gravity = (float)(double)ProjectSettings.GetSetting("physics/3d/default_gravity", 9.8);

        // Il centro di massa nell'origine rende banale l'offset da passare ad ApplyForce.
        CenterOfMassMode = CenterOfMassModeEnum.Custom;
        CenterOfMass = Vector3.Zero;
        FreezeMode = FreezeModeEnum.Kinematic;
        CanSleep = false;

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

    /// <summary>
    /// Sceglie fra simulazione e presentazione. Va richiamata a ogni cambio di stato della rete e non
    /// una volta sola in <c>_Ready</c>: il livello viene caricato PRIMA che esista un peer, e con
    /// <c>MultiplayerPeer == null</c> <see cref="Node.IsMultiplayerAuthority"/> e' vero anche su un
    /// futuro client — che continuerebbe a simulare in parallelo all'host (violazione di §3).
    /// </summary>
    private void ApplySimulationMode()
    {
        bool host = IsMultiplayerAuthority();
        Freeze = !host;

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
        _throttle = 0f;
        _steer = 0f;
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
        }

        if (DebugAutoDrive)
        {
            _throttle = 1f;
            _steer = DebugSteer;
        }

        HostApplyBuoyancy();
        HostApplyPropulsion();
        HostPublishState();
    }

    private void HostApplyBuoyancy()
    {
        if (_floaters.Length == 0)
            return;

        float surface = _water?.SurfaceY ?? 0f;

        // A immersione MEDIA del 50% la spinta pareggia esattamente il peso: la quota di riposo non va
        // tarata a mano e resta corretta anche cambiando Mass o il numero di punti di galleggiamento.
        float perFloater = Mass * _gravity / _floaters.Length * 2f;

        foreach (Marker3D floater in _floaters)
        {
            Vector3 point = floater.GlobalPosition;
            if (_water != null && !_water.Contains(point))
                continue;

            float depth = Mathf.Clamp(surface - point.Y, 0f, MaxSubmerge);
            if (depth <= 0f)
                continue;

            // ATTENZIONE: il secondo argomento di ApplyForce e' un OFFSET dal centro di massa in
            // orientamento globale, non un punto globale. In Godot non esiste ApplyForceAtPosition.
            ApplyForce(Vector3.Up * (depth / MaxSubmerge) * perFloater, point - GlobalPosition);
        }

        ApplyCentralForce(Vector3.Down * LinearVelocity.Y * Mass * VerticalDampFactor);
    }

    private void HostApplyPropulsion()
    {
        Vector3 forward = -GlobalTransform.Basis.Z;
        Vector3 right = GlobalTransform.Basis.X;

        ApplyCentralForce(forward * _throttle * ThrustForce * (_throttle >= 0f ? 1f : ReverseFactor));
        ApplyCentralForce(-right * LinearVelocity.Dot(right) * Mass * LateralDragFactor);

        // Il timone non fa nulla da fermo: e' fisicamente giusto ed evita di girare sul posto.
        float speedFactor = Mathf.Clamp(LinearVelocity.Dot(forward) / 4f, -1f, 1f);
        ApplyTorque(Vector3.Up * -_steer * SteerTorque * speedFactor);
    }

    private void HostPublishState()
    {
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

    public void SubmitControls(float throttle, float steer)
    {
        if (IsMultiplayerAuthority())
            RequestControls(throttle, steer);
        else
            RpcId(NetworkConstants.HostPeerId, MethodName.RequestControls, throttle, steer);
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

        PilotPeerId = sender;
        _throttle = 0f;
        _steer = 0f;
        _sinceLastInput = 0f;
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void RequestLeaveHelm()
    {
        if (!ValidatePilot())
            return;

        PilotPeerId = 0;
        _throttle = 0f;
        _steer = 0f;
    }

    /// <summary>
    /// Intento di guida. Unreliable e a rate fisso: il pilota lo invia sempre, anche quando i comandi
    /// non cambiano, perche' un pacchetto "acceleratore a zero" perso lascerebbe la barca a tutta forza.
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    public void RequestControls(float throttle, float steer)
    {
        if (!ValidatePilot())
            return;

        // Un valore non finito (o enorme) sparerebbe la barca fuori dal mondo: si rifiuta e si satura.
        if (!float.IsFinite(throttle) || !float.IsFinite(steer))
            return;

        _throttle = Mathf.Clamp(throttle, -1f, 1f);
        _steer = Mathf.Clamp(steer, -1f, 1f);
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
