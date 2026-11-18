using Godot;

namespace Lagoon;

/// <summary>
/// Distanza di visione e nebbia della mappa open world. Sta nella scena della mappa
/// (<c>Laguna.tscn</c>) accanto a <see cref="TerrainStreamer"/>, <c>WorldEnvironment</c> e
/// <c>FarTerrain</c>, e ne regola tre cose insieme, perche' devono restare coerenti:
/// <list type="bullet">
/// <item>quante region di Terrain3D si caricano (<see cref="TerrainStreamer.Radius"/>);</item>
/// <item>la densita' della nebbia, che copre il bordo delle region caricate e quindi il passaggio
/// dal terreno vero al piano lontano;</item>
/// <item>il <c>far</c> della camera attiva: dove la nebbia e' ormai piena (oltre non si vedrebbe
/// nulla, e il piano lontano e l'acqua si fermano li').</item>
/// </list>
///
/// <para><b>Rete (CLAUDE.md §3).</b> La distanza la decide l'HOST: e' lui a pagare le region (dovra'
/// caricarle attorno a ogni giocatore), quindi il suo valore e' lo stato della sessione,
/// <see cref="SessionRenderDistance"/>, replicato dal <c>MultiplayerSynchronizer</c> figlio. Il nodo
/// ha l'autorita' di default (peer 1 = host). Un client usa il MINIMO fra il livello dell'host e il
/// proprio: puo' scendere per la propria scheda video, non salire oltre quello che vede l'host (un
/// client che vedesse piu' lontano avrebbe terreno e bersagli dove l'host non ne ha). La nebbia e'
/// solo resa: ognuno la sceglie per se', niente replica. Il nodo e' statico nella scena (non lo
/// crea un <c>MultiplayerSpawner</c>), quindi lo stato di spawn non viaggia: la proprieta' e' in
/// modalita' "Always" con un invio al secondo, cosi' arriva anche a chi entra dopo (un intero).</para>
///
/// <para><b>Nebbia legata alla distanza.</b> Esponenziale (<c>1 - e^(-densita' * d)</c>). La densita' si
/// ricava dall'opacita' voluta al <see cref="EdgeDistance"/>, il punto piu' vicino in cui puo' gia'
/// mancare il terreno vero: da <see cref="MinEdgeFog"/> (impostazione a 0) a <see cref="MaxEdgeFog"/>
/// (a 1). Il minimo e' legato alla distanza per costruzione: abbassare la distanza infittisce la
/// nebbia, e il confine del terreno caricato resta sempre mezzo nascosto.</para>
/// </summary>
public partial class WorldRenderSettings : Node
{
    public const string GroupName = "world_render_settings";

    /// Lato di una region di Terrain3D della mappa, in metri (skill venice-terrain §1).
    public const float RegionMeters = 2048f;

    /// <summary>
    /// Un livello di distanza di visione. <c>Radius</c> = region caricate attorno al bersaglio: un
    /// quadrato di (2r+1)² region da 2 km; il terreno vero arriva ad ALMENO r × 2048 m in ogni
    /// direzione (bersaglio sul bordo della propria region), di solito di piu'. Le misure di ogni
    /// livello stanno nella skill venice-terrain §5.
    /// </summary>
    public readonly record struct Level(string LabelKey, int Radius);

    public static readonly Level[] Levels =
    {
        new("UI_SETTINGS_RENDER_LOW", 1),        // 3×3  region, terreno vero >= 2 km
        new("UI_SETTINGS_RENDER_MEDIUM", 2),     // 5×5  region, >= 4 km
        new("UI_SETTINGS_RENDER_HIGH", 3),       // 7×7  region, >= 6 km
        new("UI_SETTINGS_RENDER_VERY_HIGH", 4),  // 9×9  region, >= 8 km
    };

    /// Il livello che non costa piu' di prima di queste impostazioni (TerrainStreamer.Radius = 1).
    public const int DefaultLevel = 0;

    /// Opacita' della nebbia al bordo del terreno caricato con l'impostazione nebbia a 0 e a 1.
    public const float MinEdgeFog = 0.5f;
    public const float MaxEdgeFog = 0.95f;

    /// La camera taglia dove la nebbia arriva a questa opacita': oltre, la differenza non si vede.
    private const float FarFogOpacity = 0.98f;
    private const float MinFar = 1000f;
    /// La mappa sta in ±24 km: oltre non c'e' terra da vedere.
    private const float MaxFar = 30000f;

    [Export] public NodePath StreamerPath { get; set; } = "../TerrainStreamer";
    [Export] public NodePath EnvironmentPath { get; set; } = "../WorldEnvironment";

    private int _sessionRenderDistance = -1;

    /// <summary>
    /// Livello scelto dall'host, replicato. -1 finche' un client non l'ha ricevuto: nel frattempo
    /// vale il suo livello locale. Lo scrive solo l'autorita' (<see cref="SyncFromSettings"/>).
    /// </summary>
    [Export]
    public int SessionRenderDistance
    {
        get => _sessionRenderDistance;
        set
        {
            if (value == _sessionRenderDistance)
                return;
            _sessionRenderDistance = value;
            if (IsInsideTree())
                Apply();
        }
    }

    /// Livello in uso su questo peer: quello della sessione, abbassato dalla preferenza locale.
    public int EffectiveLevel { get; private set; } = DefaultLevel;

    public float FogDensityApplied { get; private set; }
    public float CameraFar { get; private set; } = MaxFar;

    private SettingsService _settings = null!;
    private TerrainStreamer? _streamer;
    private Environment? _environment;

    /// Il nodo della mappa caricata, o null (menu, livello di prova).
    public static WorldRenderSettings? Find(SceneTree tree) =>
        tree.GetFirstNodeInGroup(GroupName) as WorldRenderSettings;

    public override void _Ready()
    {
        AddToGroup(GroupName);
        _settings = GetNode<SettingsService>("/root/SettingsService");
        _streamer = GetNodeOrNull<TerrainStreamer>(StreamerPath);
        _environment = GetNodeOrNull<WorldEnvironment>(EnvironmentPath)?.Environment;
        if (_streamer == null)
            GD.PushWarning($"[WorldRenderSettings] TerrainStreamer non trovato in {StreamerPath}");
        if (_environment == null)
            GD.PushWarning($"[WorldRenderSettings] WorldEnvironment non trovato in {EnvironmentPath}");

        _settings.GraphicsChanged += SyncFromSettings;
        SyncFromSettings();
    }

    public override void _ExitTree()
    {
        _settings.GraphicsChanged -= SyncFromSettings;
    }

    public override void _Process(double delta)
    {
        // La camera attiva cambia (anteprima della mappa, avatar locale, morte...): il far si
        // impone a quella in uso, non a una camera precisa.
        if (GetViewport().GetCamera3D() is Camera3D cam && !Mathf.IsEqualApprox(cam.Far, CameraFar))
            cam.Far = CameraFar;
    }

    /// <summary>
    /// Le impostazioni locali sono cambiate. Sull'host (o in singleplayer) la preferenza diventa il
    /// valore della sessione e si replica da sola; su un client conta solo per abbassarlo.
    /// </summary>
    private void SyncFromSettings()
    {
        if (IsMultiplayerAuthority())
            _sessionRenderDistance = _settings.RenderDistance;
        Apply();
    }

    private void Apply()
    {
        int local = _settings.RenderDistance;
        // Client che non ha ancora ricevuto il valore dell'host (al piu' un secondo): si parte dal
        // livello piu' economico, per non caricare region che l'host potrebbe non concedere.
        int session = _sessionRenderDistance >= 0 ? _sessionRenderDistance
            : IsMultiplayerAuthority() ? local : DefaultLevel;
        EffectiveLevel = Mathf.Clamp(Mathf.Min(session, local), 0, Levels.Length - 1);
        int radius = Levels[EffectiveLevel].Radius;

        if (_streamer != null)
            _streamer.Radius = radius;

        float edgeOpacity = Mathf.Lerp(MinEdgeFog, MaxEdgeFog, _settings.FogDensity);
        FogDensityApplied = -Mathf.Log(1f - edgeOpacity) / EdgeDistance(radius);
        CameraFar = Mathf.Clamp(-Mathf.Log(1f - FarFogOpacity) / FogDensityApplied, MinFar, MaxFar);

        if (_environment != null)
        {
            _environment.FogEnabled = true;
            _environment.FogMode = Environment.FogModeEnum.Exponential;
            _environment.FogDensity = FogDensityApplied;
            // Il cielo si vela quanto il terreno al bordo: con nebbia fitta un cielo limpido
            // sopra un orizzonte grigio stona.
            _environment.FogSkyAffect = edgeOpacity;
        }

        GD.Print($"[WorldRenderSettings] livello {EffectiveLevel} (sessione {session}, locale {local}): " +
            $"raggio {radius}, nebbia {FogDensityApplied:0.000000}, far {CameraFar:0} m");
    }

    /// <summary>
    /// Distanza minima a cui puo' gia' mancare il terreno vero: bersaglio sul bordo della propria
    /// region, quindi <c>radius</c> region intere davanti.
    /// </summary>
    public static float EdgeDistance(int radius) => Mathf.Max(radius, 1) * RegionMeters;

    /// <summary>
    /// Livello dell'host quando sta abbassando quello scelto qui (solo su un client), altrimenti -1.
    /// Serve al menu per spiegare perche' si vede meno di quanto impostato.
    /// </summary>
    public int HostLimit()
    {
        if (IsMultiplayerAuthority() || _sessionRenderDistance < 0)
            return -1;
        return _sessionRenderDistance < _settings.RenderDistance ? _sessionRenderDistance : -1;
    }
}
