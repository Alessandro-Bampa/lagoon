using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
    private const string SectionGraphics = "graphics";

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

    public const float MinMouseSensitivity = 0.1f;
    public const float MaxMouseSensitivity = 3.0f;

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

    private float _mouseSensitivity = 1.0f;

    /// Moltiplicatore della velocita' dello sguardo. Non c'e' nulla da applicare: lo legge
    /// <see cref="FirstPersonCamera"/> a ogni movimento del mouse.
    public float MouseSensitivity
    {
        get => _mouseSensitivity;
        set => _mouseSensitivity = Mathf.Clamp(value, MinMouseSensitivity, MaxMouseSensitivity);
    }

    /// Emesso da <see cref="ApplyAll"/> quando distanza di visione o nebbia sono cambiate. Lo
    /// ascolta <see cref="WorldRenderSettings"/> della mappa caricata, che le applica al mondo.
    [Signal]
    public delegate void GraphicsChangedEventHandler();

    private int _renderDistance = WorldRenderSettings.DefaultLevel;

    /// <summary>
    /// Distanza di visione PREFERITA da questo giocatore, indice in <see cref="WorldRenderSettings.Levels"/>.
    /// Se si ospita la partita vale per tutta la sessione; da client puo' solo abbassare quella
    /// dell'host (vedi <see cref="WorldRenderSettings"/>). Il setter NON applica nulla.
    /// </summary>
    public int RenderDistance
    {
        get => _renderDistance;
        set => _renderDistance = Mathf.Clamp(value, 0, WorldRenderSettings.Levels.Length - 1);
    }

    private float _fogDensity = 0.3f;

    /// <summary>
    /// Nebbia di distanza, 0..1, sempre locale. 0 = la minima che copre ancora il bordo delle region
    /// caricate, 1 = la piu' fitta: la traduzione in densita' sta in <see cref="WorldRenderSettings"/>.
    /// </summary>
    public float FogDensity
    {
        get => _fogDensity;
        set => _fogDensity = Mathf.Clamp(value, 0f, 1f);
    }

    private int _appliedRenderDistance = -1;
    private float _appliedFogDensity = -1f;

    /// <summary>
    /// Tetto di fps quando la finestra del gioco non ha il focus: la seconda istanza dei test
    /// multi-istanza, un alt-tab. Disegnata a pieno ritmo, una finestra che nessuno guarda costava
    /// quanto quella in primo piano (19/11/2026, MainLaguna: editor + due istanze a 160 fps = GPU al
    /// 100 %, ~300 W). Rete e fisica non ne risentono: la fisica ha il suo passo fisso e ne fa piu' di
    /// uno per frame.
    /// </summary>
    public const int BackgroundMaxFps = 30;

    /// Tetto in primo piano (<c>application/run/max_fps</c>, letto all'avvio; 0 = nessun tetto).
    private int _foregroundMaxFps;
    /// Falso finche' <see cref="_Ready"/> non ha letto il tetto, in headless e nelle prove in tempo simulato.
    private bool _throttleInBackground;
    /// Ultimo stato del focus applicato; null = mai applicato (il primo frame lo applica comunque).
    private bool? _windowFocused;

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

        _foregroundMaxFps = Engine.MaxFps;
        // Le prove in tempo simulato (--fixed-fps, es. tools/boat_trials.gd) devono girare alla
        // velocita' della CPU anche se la loro finestra perde il focus.
        _throttleInBackground = System.Array.IndexOf(OS.GetCmdlineArgs(), "--fixed-fps") < 0
            && DisplayServer.GetName() != "headless";
    }

    public override void _ExitTree()
    {
        if (Instance == this)
            Instance = null;
    }

    /// <summary>
    /// Il primo piano si legge a ogni frame invece di affidarsi a NotificationApplicationFocusIn/Out:
    /// una finestra che nasce senza focus (la seconda istanza) non riceve mai FocusOut, e una che lo
    /// riceve durante l'avvio, prima che l'albero esista, non riceve FocusIn (provato il 19/11/2026:
    /// istanza in primo piano rimasta a 30 fps). Costa qualche microsecondo.
    /// </summary>
    public override void _Process(double delta)
    {
        if (!_throttleInBackground)
            return;
        bool focused = IsGameWindowInForeground();
        if (focused == _windowFocused)
            return;
        _windowFocused = focused;
        Engine.MaxFps = focused ? _foregroundMaxFps
            : _foregroundMaxFps > 0 ? Mathf.Min(_foregroundMaxFps, BackgroundMaxFps) : BackgroundMaxFps;
    }

    /// <summary>
    /// Su Windows non basta <c>DisplayServer.WindowIsFocused</c>: una finestra creata mentre un'altra
    /// applicazione e' in primo piano risulta attiva per il proprio thread senza esserlo mai per il
    /// sistema, e il flag resta vero per sempre (seconda istanza lanciata dall'editor: "focused" con
    /// un'altra app davanti, 160 fps). Si confronta quindi con la finestra in primo piano del
    /// sistema, risalendo alla radice: l'istanza incorporata nella scheda Gioco dell'editor (--wid) e'
    /// una finestra figlia, e conta come in primo piano quando lo e' l'editor che la mostra.
    /// </summary>
    private static bool IsGameWindowInForeground()
    {
        if (!OperatingSystem.IsWindows())
            return DisplayServer.WindowIsFocused();
        nint own = (nint)DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle);
        nint foreground = GetForegroundWindow();
        return foreground != 0 && GetAncestor(foreground, GaRootOwner) == GetAncestor(own, GaRootOwner);
    }

    private const uint GaRootOwner = 3;

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint flags);

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
        ApplyGraphics();
    }

    /// <summary>
    /// Distanza di visione e nebbia non toccano un server globale: dipendono dalla mappa caricata
    /// (e dall'host). Qui si avvisa soltanto chi le applica, e solo se sono cambiate davvero.
    /// </summary>
    private void ApplyGraphics()
    {
        if (_appliedRenderDistance == _renderDistance && Mathf.IsEqualApprox(_appliedFogDensity, _fogDensity))
            return;

        _appliedRenderDistance = _renderDistance;
        _appliedFogDensity = _fogDensity;
        EmitSignal(SignalName.GraphicsChanged);
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

        RenderDistance = (int)config.GetValue(SectionGraphics, "render_distance", _renderDistance);
        FogDensity = (float)config.GetValue(SectionGraphics, "fog_density", _fogDensity);

        MasterVolume = (float)config.GetValue(SectionAudio, "master", MasterVolume);
        MusicVolume = (float)config.GetValue(SectionAudio, "music", MusicVolume);
        SfxVolume = (float)config.GetValue(SectionAudio, "sfx", SfxVolume);

        AimToggle = (bool)config.GetValue(SectionGameplay, "aim_toggle", AimToggle);
        CrouchToggle = (bool)config.GetValue(SectionGameplay, "crouch_toggle", CrouchToggle);
        MouseSensitivity = (float)config.GetValue(SectionGameplay, "mouse_sensitivity", _mouseSensitivity);

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

        config.SetValue(SectionGraphics, "render_distance", RenderDistance);
        config.SetValue(SectionGraphics, "fog_density", FogDensity);

        config.SetValue(SectionAudio, "master", MasterVolume);
        config.SetValue(SectionAudio, "music", MusicVolume);
        config.SetValue(SectionAudio, "sfx", SfxVolume);

        config.SetValue(SectionGameplay, "aim_toggle", AimToggle);
        config.SetValue(SectionGameplay, "crouch_toggle", CrouchToggle);
        config.SetValue(SectionGameplay, "mouse_sensitivity", MouseSensitivity);

        // Solo gli scostamenti: le azioni mai toccate restano governate da project.godot.
        foreach ((string action, string serialized) in _bindingOverrides)
            config.SetValue(SectionInput, action, serialized);

        Error error = config.Save(ConfigPath);
        if (error != Error.Ok)
            GD.PushWarning($"[SettingsService] Salvataggio impostazioni fallito: {error}");
    }
}
