using Godot;

namespace Lagoon;

/// <summary>
/// Impostazioni utente persistenti (autoload). Unico proprietario dei valori e dell'atto di
/// applicarli: la UI (<see cref="PauseMenu"/>) legge e scrive queste proprieta', non tocca mai
/// direttamente <c>DisplayServer</c> o <c>AudioServer</c>.
///
/// Scala della UI: il progetto usa stretch "disabled" (UI a dimensione pixel fissa, vedi la skill ui-hud),
/// quindi l'unica leva di scala e' <c>Window.ContentScaleFactor</c> sulla finestra root. Con lo
/// stretch disabilitato Godot lo interpreta come "1 unita' della scena = N pixel fisici": e' la via
/// documentata per offrire uno slider "Scala UI" senza toccare i singoli nodi. Di conseguenza
/// l'intero albero 2D (compreso l'HUD) continua a lavorare in coordinate LOGICHE 1:1, e la
/// matematica in pixel dell'inventario (celle, hit-test) resta valida senza modifiche.
/// </summary>
public partial class SettingsService : Node
{
    private const string ConfigPath = "user://settings.cfg";
    private const string SectionDisplay = "display";
    private const string SectionAudio = "audio";

    public const float MinUiScale = 0.75f;
    public const float MaxUiScale = 2.0f;

    /// Istanza autoload, per i chiamanti che non possono comodamente fare GetNode (helper statici).
    public static SettingsService? Instance { get; private set; }

    /// Emesso dopo ogni applicazione di una scala diversa dalla precedente.
    [Signal]
    public delegate void UiScaleChangedEventHandler(float scale);

    private float _uiScale = 1.0f;

    /// Fattore di scala della UI. Il setter NON applica nulla: chiamare <see cref="ApplyAll"/>.
    public float UiScale
    {
        get => _uiScale;
        set => _uiScale = Mathf.Clamp(value, MinUiScale, MaxUiScale);
    }

    public bool Fullscreen { get; set; }
    public bool VSyncEnabled { get; set; } = true;

    public float MasterVolume { get; set; } = 1.0f;
    public float MusicVolume { get; set; } = 1.0f;
    public float SfxVolume { get; set; } = 1.0f;

    public override void _Ready()
    {
        Instance = this;
        // Il menu impostazioni deve restare reattivo anche se in futuro qualcosa mettesse in pausa.
        ProcessMode = ProcessModeEnum.Always;

        Load();
        // Le impostazioni finestra vanno applicate a viewport gia' pronto.
        CallDeferred(MethodName.ApplyAll);
    }

    public override void _ExitTree()
    {
        if (Instance == this)
            Instance = null;
    }

    // ====================================================================================
    //  Applicazione
    // ====================================================================================

    /// Applica tutte le impostazioni allo stato corrente del runtime.
    public void ApplyAll()
    {
        ApplyUiScale();
        ApplyWindow();
        ApplyVolumes();
    }

    private void ApplyUiScale()
    {
        Window root = GetWindow();
        if (Mathf.IsEqualApprox(root.ContentScaleFactor, _uiScale))
            return;

        root.ContentScaleFactor = _uiScale;
        EmitSignal(SignalName.UiScaleChanged, _uiScale);
    }

    private void ApplyWindow()
    {
        DisplayServer.WindowSetMode(Fullscreen
            ? DisplayServer.WindowMode.Fullscreen   // borderless: piu' rapido nell'alt-tab dei test multi-istanza
            : DisplayServer.WindowMode.Windowed);

        DisplayServer.WindowSetVsyncMode(VSyncEnabled
            ? DisplayServer.VSyncMode.Enabled
            : DisplayServer.VSyncMode.Disabled);
    }

    private void ApplyVolumes()
    {
        SetBusVolume("Master", MasterVolume);
        SetBusVolume("Music", MusicVolume);
        SetBusVolume("SFX", SfxVolume);
    }

    /// Volume lineare 0..1 -> dB, con silenzio pieno a 0. Ignora i bus non definiti nel layout.
    private static void SetBusVolume(string busName, float linear)
    {
        int index = AudioServer.GetBusIndex(busName);
        if (index < 0)
            return;

        AudioServer.SetBusVolumeDb(index, linear <= 0.0001f ? -80f : Mathf.LinearToDb(linear));
    }

    /// I PopupMenu sono Window separate: se non sono incorporate nella finestra principale NON
    /// ereditano il ContentScaleFactor della root e resterebbero alla scala 1.0.
    public static void ApplyToPopup(Window popup)
    {
        if (Instance != null)
            popup.ContentScaleFactor = Instance.UiScale;
    }

    // ====================================================================================
    //  Persistenza
    // ====================================================================================

    public void Load()
    {
        var config = new ConfigFile();
        if (config.Load(ConfigPath) != Error.Ok)
            return; // primo avvio: restano i default dichiarati sulle proprieta'

        UiScale = (float)config.GetValue(SectionDisplay, "ui_scale", _uiScale);
        Fullscreen = (bool)config.GetValue(SectionDisplay, "fullscreen", Fullscreen);
        VSyncEnabled = (bool)config.GetValue(SectionDisplay, "vsync", VSyncEnabled);

        MasterVolume = (float)config.GetValue(SectionAudio, "master", MasterVolume);
        MusicVolume = (float)config.GetValue(SectionAudio, "music", MusicVolume);
        SfxVolume = (float)config.GetValue(SectionAudio, "sfx", SfxVolume);
    }

    public void Save()
    {
        var config = new ConfigFile();
        config.SetValue(SectionDisplay, "ui_scale", UiScale);
        config.SetValue(SectionDisplay, "fullscreen", Fullscreen);
        config.SetValue(SectionDisplay, "vsync", VSyncEnabled);

        config.SetValue(SectionAudio, "master", MasterVolume);
        config.SetValue(SectionAudio, "music", MusicVolume);
        config.SetValue(SectionAudio, "sfx", SfxVolume);

        Error error = config.Save(ConfigPath);
        if (error != Error.Ok)
            GD.PushWarning($"[SettingsService] Salvataggio impostazioni fallito: {error}");
    }
}
