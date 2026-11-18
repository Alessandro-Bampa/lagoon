using System;
using Godot;

namespace Lagoon;

/// <summary>
/// L'acqua di un livello: stato AUTORITATIVO (marea, stato del mare, orologio delle onde) e lettura
/// della superficie per chi galleggia. Nessun corpo fisico, come prima delle onde: niente layer
/// "water", niente da escludere da <see cref="CollisionLayers.ShotMask"/>. La resa e' di
/// <see cref="WaterSurface"/>, che legge da qui.
///
/// <para><b>Rete (CLAUDE.md §3).</b> Le onde sono una funzione deterministica di (posizione, tempo,
/// stato del mare): basta che i peer concordino su tre numeri, che l'host decide e replica dal
/// <c>MultiplayerSynchronizer</c> figlio (<see cref="SyncWaveTime"/>, <see cref="SyncSeaState"/>,
/// <see cref="SyncWaterLevel"/>). Il galleggiamento lo calcola solo l'host; sui client le stesse onde
/// servono solo a disegnare. Il nodo e' statico nel livello: la replica e' in modalita' "Always" (lo
/// stato di spawn non viaggerebbe), come <see cref="WorldRenderSettings"/>.</para>
///
/// <para><b>Gestore del mondo.</b> Il tempo atmosferico (futuro) e gli eventi di gioco agiscono solo
/// sull'host, con <see cref="SetSeaState"/>, <see cref="SetSurge"/> e <see cref="TideClockHours"/>: sono
/// metodi, non RPC, perche' il gestore girera' sull'host. Le rampe le fa questo nodo.</para>
/// </summary>
[Tool]
public partial class WaterVolume : Node3D
{
    /// Gruppo per raggiungere l'acqua senza path fragili.
    public const string GroupName = "water";

    // ====================================================================================
    //  Configurazione (uguale su ogni peer: sta nel livello)
    // ====================================================================================

    /// <summary>
    /// Livello medio del mare in coordinate mondo (lo "0" della mappa, skill venice-terrain §1). La
    /// superficie vera e' questo + marea + sovralzo + onde.
    /// </summary>
    [Export] public float BaseLevel { get; set; }

    /// Estensione orizzontale (X, Z) centrata sul nodo: fuori, niente acqua (ne' spinta ne' resa).
    [Export] public Vector2 ExtentXZ { get; set; } = new(60f, 60f);

    /// Cartella con <c>water_map.json</c> (zone ed esposizione). Vuota = esposizione uniforme.
    [Export(PropertyHint.Dir)] public string WaterMapDirectory { get; set; } = "";

    /// Esposizione alle onde dove non c'e' mappa (0 = acqua ferma, 0,5 = laguna, 1 = mare aperto).
    [Export(PropertyHint.Range, "0,1,0.01")] public float DefaultExposure { get; set; } = 0.5f;

    // I setter qui sotto applicano subito il valore se il nodo e' gia' pronto: si prova cambiando
    // l'ispettore, nell'editor (onde animate nella vista 3D) o nell'albero remoto a gioco avviato.
    // Seme, vento e numero di onde NON sono replicati: cambiarli a partita in corso vale solo per il
    // peer su cui li si cambia (collaudo); in un livello vanno fissati nella scena.

    [ExportGroup("Onde")]
    /// Seme del banco di onde: cambiarlo cambia il disegno del mare, uguale su ogni peer.
    [Export]
    public int WaveSeed
    {
        get => _waveSeed;
        set { _waveSeed = value; RebuildSpectrum(); }
    }

    /// Da dove soffia il vento (gradi bussola, 0 = nord): 135 scirocco, 45 bora.
    [Export(PropertyHint.Range, "0,360,1")]
    public float WindFromDegrees
    {
        get => _windFromDegrees;
        set { _windFromDegrees = value; RebuildSpectrum(); }
    }

    [Export(PropertyHint.Range, "1,12,1")]
    public int WaveCount
    {
        get => _waveCount;
        set { _waveCount = value; RebuildSpectrum(); }
    }

    [ExportGroup("Stato iniziale (host)")]
    /// <summary>
    /// Stato del mare (0 calmo, 1 poco mosso, 2 mosso, 3 agitato, 4 tempesta; continuo). Nella scena
    /// e' quello di partenza; cambiato dall'ispettore si applica subito. In gioco lo cambia
    /// <see cref="SetSeaState"/>.
    /// </summary>
    [Export(PropertyHint.Range, "0,4,0.05")]
    public float InitialSeaState
    {
        get => _initialSeaState;
        set { _initialSeaState = value; if (_spectrum != null) SetSeaState(value, 0f); }
    }

    /// Sovralzo meteorologico (m): scirocco + bassa pressione = acqua alta. Si applica subito.
    [Export]
    public float InitialSurge
    {
        get => _initialSurge;
        set { _initialSurge = value; if (_spectrum != null) SetSurge(value, 0f); }
    }

    [ExportGroup("Marea")]
    [Export] public bool TideEnabled { get; set; } = true;
    /// 1 = ampiezze reali di Venezia (escursione sizigiale ~1,2 m). 0 = niente marea astronomica.
    [Export(PropertyHint.Range, "0,3,0.05")] public float TideScale { get; set; } = 1f;
    /// <summary>
    /// Secondi di marea per secondo reale. 1 = tempo reale (un ciclo in 12 h 25 min: in una sessione
    /// non si vede). 60 = un'ora di marea al minuto. Quando esistera' un orologio del mondo, il gestore
    /// scrivera' <see cref="TideClockHours"/> direttamente e questo andra' a 0.
    /// </summary>
    [Export] public float TideTimeScale { get; set; } = 60f;
    /// Ora di marea iniziale (0 = alta di sizigie).
    [Export]
    public double TideStartHours
    {
        get => _tideStartHours;
        set { _tideStartHours = value; if (_spectrum != null) _tideHours = value; }
    }

    [ExportGroup("Editor")]
    /// Anima le onde nella vista 3D dell'editor.
    [Export] public bool AnimateInEditor { get; set; } = true;

    /// <summary>
    /// Solo anteprima nell'editor: rallenta o accelera lo scorrere delle onde per guardarle. In gioco
    /// la velocita' non e' un parametro: la decide la fisica (un'onda lunga corre piu' di una corta,
    /// c = sqrt(g·λ/2π)), quindi cambia con lo stato del mare.
    /// </summary>
    [Export(PropertyHint.Range, "0,4,0.05")] public float EditorTimeScale { get; set; } = 1f;

    // ====================================================================================
    //  Stato replicato (Water/Sync, autorita' host)
    // ====================================================================================

    /// Orologio delle onde dell'host, in secondi.
    [Export] public double SyncWaveTime { get; set; }
    /// Stato del mare corrente dell'host (gia' in rampa).
    [Export] public float SyncSeaState { get; set; }
    /// Quota media dell'acqua dell'host: base + marea + sovralzo (senza onde).
    [Export] public float SyncWaterLevel { get; set; }

    // ====================================================================================
    //  Stato locale
    // ====================================================================================

    /// Componenti armoniche principali della marea a Venezia (ampiezza m, periodo h, fase rad):
    /// M2 e S2 semidiurne (insieme danno sizigie e quadrature ogni 14,8 giorni), K1 e O1 diurne.
    private static readonly (float Amplitude, double PeriodHours, double Phase)[] TideConstituents =
    {
        (0.234f, 12.4206, 0.0),
        (0.139f, 12.0, 0.0),
        (0.183f, 23.9345, 0.9),
        (0.051f, 25.8193, 2.1),
    };

    /// Tetto all'errore d'orologio corretto dolcemente sul client: oltre, si riallinea di colpo.
    private const double ClockSnapSeconds = 0.5;

    private int _waveSeed = 1337;
    private float _windFromDegrees = 135f;
    private int _waveCount = WaveSpectrum.MaxWaves;
    private float _initialSeaState = 1f;
    private float _initialSurge;
    private double _tideStartHours;

    private WaveSpectrum _spectrum = null!;
    private WaterMap? _map;

    // Orologio fisico (passi da _PhysicsProcess) e sua resa interpolata per il disegno.
    private double _physicsTime;
    private double _renderTime;

    private float _seaState;
    private float _seaTarget;
    private float _seaRate;          // unita' di stato al secondo; 0 = salto immediato
    private float _surge;
    private float _surgeTarget;
    private float _surgeRate;        // m/s
    private double _tideHours;
    private float _waterLevel;

    // Client: lo stato arriva a 4 Hz. Fra un pacchetto e l'altro si prosegue la rampa con la
    // pendenza degli ultimi due (estrapolazione), altrimenti marea e mare resterebbero indietro.
    private bool _gotState;
    private double _sinceState;
    private float _lastSyncSea, _lastSyncLevel;
    private float _seaRateRemote, _levelRateRemote;

    /// Tetto all'estrapolazione: se l'host tace, la rampa si ferma invece di proseguire all'infinito.
    private const double MaxExtrapolationSeconds = 0.6;

    // ====================================================================================
    //  API di lettura (ogni peer)
    // ====================================================================================

    public WaveSpectrum Spectrum
    {
        get
        {
            if (_spectrum == null)
                Initialize();
            return _spectrum!;
        }
    }
    public WaterMap? Map => _map;

    /// Quota media dell'acqua (marea + sovralzo, senza onde). Per il rientro dal fondale e simili.
    public float SurfaceY => _waterLevel;
    public float WaterLevel => _waterLevel;

    /// Stato del mare corrente (0..4, continuo).
    public float SeaState => _seaState;

    /// Orologio delle onde per la fisica (in passi fisici).
    public double PhysicsTime => _physicsTime;

    /// Orologio delle onde per il disegno: interpolato fra i passi fisici, cosi' le onde scorrono a
    /// ogni frame ma restano nello stesso tempo della fisica (la barca non "scivola" sull'onda).
    public double RenderTime => _renderTime;

    /// Altezza della marea astronomica + sovralzo rispetto a <see cref="BaseLevel"/>.
    public float TideHeight => _waterLevel - BaseLevel;

    /// Ora di marea (host). Il futuro orologio del mondo la scrivera' qui.
    public double TideClockHours
    {
        get => _tideHours;
        set => _tideHours = value;
    }

    // ====================================================================================
    //  Ciclo di vita
    // ====================================================================================

    public override void _EnterTree()
    {
        // Esplicito come BoatController: il Synchronizer figlio eredita l'host.
        SetMultiplayerAuthority(NetworkConstants.HostPeerId);
    }

    public override void _Ready()
    {
        AddToGroup(GroupName);
        Initialize();

        if (!Engine.IsEditorHint())
            GetNodeOrNull<MultiplayerSynchronizer>("Sync")?.Connect(
                MultiplayerSynchronizer.SignalName.Synchronized, Callable.From(OnStateReceived));
    }

    /// <summary>
    /// Stato interno dalle proprieta' esportate. Si richiama anche a meta' vita: nell'editor, quando
    /// l'assembly C# si ricarica (a ogni build), i campi privati dei nodi <c>[Tool]</c> tornano ai
    /// valori iniziali e <c>_Ready</c> non riparte.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_spectrum))]
    private void Initialize()
    {
        _spectrum = new WaveSpectrum(WaveSeed, WindFromDegrees, WaveCount);
        _map = WaterMap.Load(WaterMapDirectory);

        _seaState = _seaTarget = Mathf.Clamp(InitialSeaState, 0f, (int)Lagoon.SeaState.Storm);
        _surge = _surgeTarget = InitialSurge;
        _tideHours = TideStartHours;
        _spectrum.SetSeaState(_seaState);
        _waterLevel = ComputeWaterLevel();
        PublishState();
    }

    /// Ricrea il banco di onde (seme, vento o numero cambiati), mantenendo lo stato del mare.
    private void RebuildSpectrum()
    {
        if (_spectrum == null)
            return;
        _spectrum = new WaveSpectrum(_waveSeed, _windFromDegrees, _waveCount);
        _spectrum.SetSeaState(_seaState);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_spectrum == null)
            Initialize();
        if (Engine.IsEditorHint())
        {
            if (!AnimateInEditor)
                return;
            delta *= EditorTimeScale;
        }

        if (IsHost())
            HostStep(delta);
        else
            ClientStep(delta);

        _spectrum.SetSeaState(_seaState);
    }

    public override void _Process(double delta)
    {
        // Fra un passo fisico e l'altro: stesso orologio, frazione del passo in corso.
        double step = 1.0 / Engine.PhysicsTicksPerSecond;
        _renderTime = _physicsTime + Engine.GetPhysicsInterpolationFraction() * step;
    }

    /// <summary>
    /// Vero sull'host e in singleplayer/editor. Si rivaluta a ogni passo e non una volta in
    /// <c>_Ready</c>: il livello si carica prima che esista il peer (vedi BoatController).
    /// </summary>
    private bool IsHost() => Engine.IsEditorHint() || IsMultiplayerAuthority();

    // ====================================================================================
    //  Host: rampe, marea, pubblicazione
    // ====================================================================================

    private void HostStep(double delta)
    {
        float dt = (float)delta;
        _physicsTime += delta;

        _seaState = Ramp(_seaState, _seaTarget, _seaRate, dt);
        _surge = Ramp(_surge, _surgeTarget, _surgeRate, dt);
        if (TideEnabled)
            _tideHours += delta * TideTimeScale / 3600.0;

        _waterLevel = ComputeWaterLevel();
        PublishState();
    }

    private static float Ramp(float value, float target, float rate, float dt)
    {
        if (rate <= 0f)
            return target;
        return Mathf.MoveToward(value, target, rate * dt);
    }

    private float ComputeWaterLevel()
    {
        float tide = 0f;
        if (TideEnabled && TideScale > 0f)
        {
            foreach (var (amplitude, period, phase) in TideConstituents)
                tide += amplitude * (float)Math.Cos(Math.Tau * _tideHours / period + phase);
            tide *= TideScale;
        }
        return BaseLevel + tide + _surge;
    }

    private void PublishState()
    {
        SyncWaveTime = _physicsTime;
        SyncSeaState = _seaState;
        SyncWaterLevel = _waterLevel;
    }

    /// <summary>
    /// Porta lo stato del mare a <paramref name="target"/> (0 calmo … 4 tempesta, vedi
    /// <see cref="Lagoon.SeaState"/>: <c>SetSeaState((float)SeaState.Storm)</c>) in
    /// <paramref name="seconds"/> secondi. Niente overload con l'enum: Godot non distingue gli overload
    /// e il metodo non sarebbe piu' chiamabile da GDScript o dall'ispettore remoto. Solo host: su un client non fa nulla (lo stato arriva dalla
    /// replica). Le ampiezze cambiano, le lunghezze d'onda no: nessun salto di fase.
    /// </summary>
    public void SetSeaState(float target, float seconds = 60f)
    {
        if (!IsHost())
            return;
        _seaTarget = Mathf.Clamp(target, 0f, (int)Lagoon.SeaState.Storm);
        _seaRate = seconds > 0f ? Mathf.Abs(_seaTarget - _seaState) / seconds : 0f;
    }

    /// <summary>
    /// Sovralzo meteorologico (m) da raggiungere in <paramref name="seconds"/>: si somma alla marea
    /// astronomica. Scirocco forte + bassa pressione ≈ +0,6…1 m: con l'alta di sizigie, acqua alta in
    /// Piazza San Marco (+1,0 m nella mappa). Solo host.
    /// </summary>
    public void SetSurge(float meters, float seconds = 600f)
    {
        if (!IsHost())
            return;
        _surgeTarget = meters;
        _surgeRate = seconds > 0f ? Mathf.Abs(_surgeTarget - _surge) / seconds : 0f;
    }

    // ====================================================================================
    //  Client: orologio e stato dalla replica
    // ====================================================================================

    /// <summary>
    /// Arrivato uno stato dall'host. L'orologio si corregge dolcemente (un salto farebbe "teletrasportare"
    /// le onde); oltre <see cref="ClockSnapSeconds"/> di errore si riallinea di colpo (primo pacchetto,
    /// cambio di sessione).
    /// </summary>
    private void OnStateReceived()
    {
        if (IsHost())
            return;

        double error = SyncWaveTime - _physicsTime;
        if (!_gotState || Math.Abs(error) > ClockSnapSeconds)
        {
            _physicsTime = SyncWaveTime;
            _seaState = SyncSeaState;
            _waterLevel = SyncWaterLevel;
            _seaRateRemote = _levelRateRemote = 0f;
            _gotState = true;
        }
        else
        {
            _physicsTime += error * 0.25;
            float dt = (float)Math.Max(_sinceState, 0.05);
            _seaRateRemote = (SyncSeaState - _lastSyncSea) / dt;
            _levelRateRemote = (SyncWaterLevel - _lastSyncLevel) / dt;
        }
        _lastSyncSea = SyncSeaState;
        _lastSyncLevel = SyncWaterLevel;
        _sinceState = 0.0;
    }

    private void ClientStep(double delta)
    {
        _physicsTime += delta;
        _sinceState += delta;
        if (!_gotState)
            return;

        // Bersaglio = ultimo stato + pendenza x eta' (limitata); ci si avvicina in ~0,2 s.
        float age = (float)Math.Min(_sinceState, MaxExtrapolationSeconds);
        float seaTarget = Mathf.Clamp(SyncSeaState + _seaRateRemote * age, 0f, (int)Lagoon.SeaState.Storm);
        float levelTarget = SyncWaterLevel + _levelRateRemote * age;
        float t = Mathf.Clamp((float)delta * 8f, 0f, 1f);
        _seaState = Mathf.Lerp(_seaState, seaTarget, t);
        _waterLevel = Mathf.Lerp(_waterLevel, levelTarget, t);
    }

    // ====================================================================================
    //  Lettura della superficie
    // ====================================================================================

    /// <summary>
    /// True se il punto cade dentro l'area orizzontale d'acqua. La quota non conta: l'immersione la
    /// calcola il chiamante da <see cref="Sample"/>.
    /// </summary>
    public bool Contains(Vector3 globalPoint)
    {
        Vector3 origin = GlobalPosition;
        return Mathf.Abs(globalPoint.X - origin.X) <= ExtentXZ.X * 0.5f
            && Mathf.Abs(globalPoint.Z - origin.Z) <= ExtentXZ.Y * 0.5f;
    }

    /// Esposizione alle onde nel punto (mappa o <see cref="DefaultExposure"/>).
    public float ExposureAt(double x, double z) => _map?.ExposureAt(x, z) ?? DefaultExposure;

    /// Quota della superficie sulla verticale del punto, all'orologio fisico.
    public float HeightAt(Vector3 globalPoint) => Sample(globalPoint).Height;

    /// <summary>
    /// Superficie sulla verticale di <paramref name="globalPoint"/> all'orologio fisico (o a quello
    /// dato). Gerstner sposta le particelle anche in orizzontale: la particella che ora sta sopra il
    /// punto partiva da un altro x0. Lo si trova a punto fisso (x0 = x − D(x0)): con Σk·H ≤ 0,9
    /// converge in pochi passi; 4 bastano al millimetro.
    /// </summary>
    public WaterSample Sample(Vector3 globalPoint, double? time = null)
    {
        double t = time ?? _physicsTime;
        double x = globalPoint.X, z = globalPoint.Z;
        double x0 = x, z0 = z;
        float exposure = ExposureAt(x0, z0);

        for (int i = 0; i < 4; i++)
        {
            _spectrum.Horizontal(x0, z0, t, exposure, out double dx, out double dz);
            x0 = x - dx;
            z0 = z - dz;
            exposure = ExposureAt(x0, z0);
        }

        _spectrum.Evaluate(x0, z0, t, exposure, out Vector3 disp, out Vector3 normal, out Vector3 velocity);
        return new WaterSample(_waterLevel + disp.Y, normal, velocity, exposure);
    }

    /// <summary>
    /// Prima acqua del livello (o null). Con piu' specchi d'acqua andra' cercata per contenimento; per
    /// ora ce n'e' una per livello.
    /// </summary>
    public static WaterVolume? Find(Node context)
    {
        foreach (Node node in context.GetTree().GetNodesInGroup(GroupName))
        {
            if (node is WaterVolume water)
                return water;
        }
        return null;
    }
}
