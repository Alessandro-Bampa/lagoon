using System.Collections.Generic;
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
    private const string SectionLocale = "locale";
    private const string SectionGameplay = "gameplay";
    private const string SectionInput = "input";

    /// Valore di <see cref="Language"/> che significa "segui la lingua del sistema operativo".
    public const string SystemLanguage = "system";

    /// Lingue offerte nel menu, nell'ordine in cui compaiono. L'etichetta e' l'ENDONIMO (il nome
    /// della lingua nella lingua stessa): un giocatore che ha avviato per sbaglio in una lingua che
    /// non conosce deve poter ritrovare la propria nell'elenco.
    public static readonly (string Code, string Label)[] AvailableLanguages =
    {
        (SystemLanguage, ""), // etichetta tradotta a runtime (UI_SETTINGS_LANGUAGE_SYSTEM)
        ("it", "Italiano"),
        ("en", "English"),
    };

    public const float MinUiScale = 0.75f;
    public const float MaxUiScale = 2.0f;

    /// Istanza autoload, per i chiamanti che non possono comodamente fare GetNode (helper statici).
    public static SettingsService? Instance { get; private set; }

    /// Emesso dopo ogni applicazione di una scala diversa dalla precedente.
    [Signal]
    public delegate void UiScaleChangedEventHandler(float scale);

    /// Emesso dopo ogni cambio effettivo di lingua. La UI da scena si aggiorna gia' da sola
    /// (Godot notifica NotificationTranslationChanged a tutti i Control): questo segnale serve a
    /// chi non e' un Control, o a chi deve rifare un lavoro piu' grosso di un semplice testo.
    [Signal]
    public delegate void LanguageChangedEventHandler(string locale);

    /// <summary>
    /// Lingua della UI: <c>"system"</c>, oppure un codice locale fra
    /// <see cref="AvailableLanguages"/>. Il setter NON applica nulla: chiamare <see cref="ApplyAll"/>.
    /// </summary>
    public string Language { get; set; } = SystemLanguage;

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

    /// <summary>
    /// Mira a interruttore invece che a pressione mantenuta: un clic entra in mira, il successivo ne
    /// esce. Letta da <see cref="PlayerInput"/>, che e' l'unico posto in cui la differenza esiste.
    /// </summary>
    public bool AimToggle { get; set; }

    /// Accovacciamento a interruttore invece che a pressione mantenuta. Vedi <see cref="AimToggle"/>.
    public bool CrouchToggle { get; set; }

    /// <summary>
    /// Comandi riassegnati dal giocatore: azione -> evento in forma testuale
    /// (<see cref="InputBindings.Serialize"/>). Contiene SOLO gli scostamenti dai binding di progetto,
    /// cosi' un domani cambiare un default lo propaga a chi non l'ha mai toccato.
    /// </summary>
    private readonly Dictionary<string, string> _bindingOverrides = new();

    public override void _Ready()
    {
        Instance = this;
        // Il menu impostazioni deve restare reattivo anche se in futuro qualcosa mettesse in pausa.
        ProcessMode = ProcessModeEnum.Always;

        // Prima di Load: dopo aver applicato gli override non sarebbero piu' i predefiniti.
        InputBindings.CaptureDefaults();
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
        // Per prima: tutto cio' che viene mostrato dopo deve gia' essere nella lingua giusta.
        ApplyLocale();
        ApplyUiScale();
        ApplyWindow();
        ApplyVolumes();
        ApplyBindings();
    }

    /// <summary>
    /// Riporta l'InputMap ai binding di progetto e ci riapplica sopra gli override. Passare dai
    /// default e' cio' che rende l'operazione idempotente: si puo' richiamare a ogni ApplyAll senza
    /// accumulare eventi sulle azioni.
    /// </summary>
    private void ApplyBindings()
    {
        InputBindings.RestoreAllDefaults();

        foreach ((string action, string serialized) in _bindingOverrides)
        {
            InputEvent? inputEvent = InputBindings.Deserialize(serialized);
            if (inputEvent == null)
            {
                GD.PushWarning($"[SettingsService] Binding illeggibile per '{action}': \"{serialized}\"");
                continue;
            }

            InputBindings.Assign(action, inputEvent);
        }
    }

    /// <summary>
    /// Riassegna un'azione e applica subito il nuovo tasto. Il salvataggio su disco resta a carico
    /// di chi chiude il menu, come per tutte le altre impostazioni.
    /// </summary>
    public void SetBinding(string action, InputEvent inputEvent)
    {
        string? serialized = InputBindings.Serialize(inputEvent);
        if (serialized == null)
            return;

        InputBindings.Assign(action, inputEvent);
        if (InputBindings.DiffersFromDefault(action))
            _bindingOverrides[action] = serialized;
        else
            _bindingOverrides.Remove(action); // ri-assegnato al suo predefinito: non e' piu' uno scostamento
    }

    /// Riporta tutti i comandi ai binding di progetto.
    public void ResetBindings()
    {
        _bindingOverrides.Clear();
        InputBindings.RestoreAllDefaults();
    }

    /// <summary>
    /// Imposta la lingua di <see cref="TranslationServer"/>. Con <see cref="SystemLanguage"/> si
    /// segue la lingua del sistema: se non e' fra quelle tradotte, il fallback dichiarato in
    /// <c>project.godot</c> (inglese) copre tutto, quindi non serve nessuna lista di controllo qui.
    /// </summary>
    private void ApplyLocale()
    {
        string target = Language == SystemLanguage ? OS.GetLocaleLanguage() : Language;
        if (TranslationServer.GetLocale() == target)
            return;

        TranslationServer.SetLocale(target);
        EmitSignal(SignalName.LanguageChanged, target);
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

        Language = (string)config.GetValue(SectionLocale, "language", Language);

        UiScale = (float)config.GetValue(SectionDisplay, "ui_scale", _uiScale);
        Fullscreen = (bool)config.GetValue(SectionDisplay, "fullscreen", Fullscreen);
        VSyncEnabled = (bool)config.GetValue(SectionDisplay, "vsync", VSyncEnabled);

        MasterVolume = (float)config.GetValue(SectionAudio, "master", MasterVolume);
        MusicVolume = (float)config.GetValue(SectionAudio, "music", MusicVolume);
        SfxVolume = (float)config.GetValue(SectionAudio, "sfx", SfxVolume);

        AimToggle = (bool)config.GetValue(SectionGameplay, "aim_toggle", AimToggle);
        CrouchToggle = (bool)config.GetValue(SectionGameplay, "crouch_toggle", CrouchToggle);

        _bindingOverrides.Clear();
        if (config.HasSection(SectionInput))
            foreach (string action in config.GetSectionKeys(SectionInput))
                _bindingOverrides[action] = (string)config.GetValue(SectionInput, action, "");
    }

    public void Save()
    {
        var config = new ConfigFile();
        config.SetValue(SectionLocale, "language", Language);

        config.SetValue(SectionDisplay, "ui_scale", UiScale);
        config.SetValue(SectionDisplay, "fullscreen", Fullscreen);
        config.SetValue(SectionDisplay, "vsync", VSyncEnabled);

        config.SetValue(SectionAudio, "master", MasterVolume);
        config.SetValue(SectionAudio, "music", MusicVolume);
        config.SetValue(SectionAudio, "sfx", SfxVolume);

        config.SetValue(SectionGameplay, "aim_toggle", AimToggle);
        config.SetValue(SectionGameplay, "crouch_toggle", CrouchToggle);

        // Solo gli scostamenti: le azioni mai toccate restano governate da project.godot.
        foreach ((string action, string serialized) in _bindingOverrides)
            config.SetValue(SectionInput, action, serialized);

        Error error = config.Save(ConfigPath);
        if (error != Error.Ok)
            GD.PushWarning($"[SettingsService] Salvataggio impostazioni fallito: {error}");
    }
}
