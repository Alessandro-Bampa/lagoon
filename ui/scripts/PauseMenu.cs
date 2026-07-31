using System.Collections.Generic;
using Godot;

namespace Lagoon;

/// <summary>
/// Menu di pausa (ESC) con le sotto-pagine Impostazioni e Comandi (rimappatura dei tasti; le regole
/// della rimappatura stanno in <see cref="InputBindings"/>). E' il punto in cui il giocatore regola la
/// <b>Scala UI</b>: il progetto usa una UI a dimensione pixel fissa (stretch "disabled",
/// vedi la skill ui-hud), quindi su schermi ad alta risoluzione serve una leva esplicita per ingrandirla.
///
/// NON mette in pausa l'albero: in multiplayer <c>GetTree().Paused</c> fermerebbe solo questo peer
/// desincronizzandolo dagli altri (CLAUDE.md §3). Il mondo continua a girare dietro il menu; a
/// essere sospeso e' solo l'input locale di gameplay, tramite <see cref="GameManager.UiModalOpen"/>.
///
/// Il layout e' interamente a container e ancoraggi (CenterContainer + MarginContainer + VBox):
/// nessuna coordinata assoluta, cosi' il pannello resta centrato a qualunque risoluzione e scala.
/// </summary>
public partial class PauseMenu : Control
{
    /// Le tre schermate del menu. L'ordine e' quello della navigazione: Esc torna alla precedente.
    private enum Page
    {
        Root,
        Settings,
        Bindings,
    }

    private SettingsService _settings = null!;
    private GameManager _game = null!;

    private Control _rootPage = null!;
    private Control _settingsPage = null!;
    private Control _bindingsPage = null!;
    private Button _leaveButton = null!;

    private VBoxContainer _bindingsList = null!;
    private Label _bindingsHint = null!;

    /// Bottone di ciascuna azione riassegnabile, per riscriverne l'etichetta senza ricostruire l'elenco.
    private readonly Dictionary<string, Button> _bindingButtons = new();

    /// Azione in attesa di un nuovo tasto, oppure null. Mentre e' valorizzata il menu si prende TUTTI
    /// gli eventi di input: nessuno deve arrivare ai bottoni sotto.
    private string? _capturingAction;

    /// Il rilascio del tasto/pulsante con cui si e' appena confermato un binding va ingoiato:
    /// altrimenti raggiunge il bottone che ha il focus (i <c>BaseButton</c> scattano al rilascio) e
    /// riaprirebbe subito la cattura.
    private bool _swallowRelease;

    private OptionButton _languageOption = null!;
    private HSlider _uiScaleSlider = null!;
    private Label _uiScaleValue = null!;
    private CheckButton _fullscreenToggle = null!;
    private CheckButton _vsyncToggle = null!;
    private CheckButton _aimToggle = null!;
    private CheckButton _crouchToggle = null!;
    private HSlider _masterSlider = null!;
    private Label _masterValue = null!;
    private HSlider _musicSlider = null!;
    private Label _musicValue = null!;
    private HSlider _sfxSlider = null!;
    private Label _sfxValue = null!;

    /// True mentre si stanno scrivendo i controlli dai valori salvati: evita di ri-applicare
    /// (e ri-salvare) le impostazioni per ogni ValueChanged generato dal popolamento.
    private bool _syncing;

    public override void _Ready()
    {
        _settings = GetNode<SettingsService>("/root/SettingsService");
        _game = GetNode<GameManager>("/root/GameManager");

        _rootPage = GetNode<Control>("%RootPage");
        _settingsPage = GetNode<Control>("%SettingsPage");
        _bindingsPage = GetNode<Control>("%BindingsPage");
        _leaveButton = GetNode<Button>("%LeaveButton");
        _bindingsList = GetNode<VBoxContainer>("%BindingsList");
        _bindingsHint = GetNode<Label>("%BindingsHint");

        _languageOption = GetNode<OptionButton>("%LanguageOption");
        _uiScaleSlider = GetNode<HSlider>("%UiScaleSlider");
        _uiScaleValue = GetNode<Label>("%UiScaleValue");
        _fullscreenToggle = GetNode<CheckButton>("%FullscreenToggle");
        _vsyncToggle = GetNode<CheckButton>("%VSyncToggle");
        _aimToggle = GetNode<CheckButton>("%AimToggle");
        _crouchToggle = GetNode<CheckButton>("%CrouchToggle");
        _masterSlider = GetNode<HSlider>("%MasterSlider");
        _masterValue = GetNode<Label>("%MasterValue");
        _musicSlider = GetNode<HSlider>("%MusicSlider");
        _musicValue = GetNode<Label>("%MusicValue");
        _sfxSlider = GetNode<HSlider>("%SfxSlider");
        _sfxValue = GetNode<Label>("%SfxValue");

        _uiScaleSlider.MinValue = SettingsService.MinUiScale;
        _uiScaleSlider.MaxValue = SettingsService.MaxUiScale;
        BuildLanguageOptions();
        BuildBindingRows();

        GetNode<Button>("%ResumeButton").Pressed += Close;
        GetNode<Button>("%SettingsButton").Pressed += () => ShowPage(Page.Settings);
        GetNode<Button>("%BackButton").Pressed += () => ShowPage(Page.Root);
        GetNode<Button>("%BindingsButton").Pressed += () => ShowPage(Page.Bindings);
        GetNode<Button>("%BindingsBackButton").Pressed += () => ShowPage(Page.Settings);
        GetNode<Button>("%BindingsResetButton").Pressed += OnResetBindings;
        _leaveButton.Pressed += OnLeaveSession;
        GetNode<Button>("%QuitButton").Pressed += () => GetTree().Quit();

        // Applicazione live: il giocatore vede l'effetto mentre trascina.
        _languageOption.ItemSelected += _ => ApplyFromControls();
        _uiScaleSlider.ValueChanged += _ => ApplyFromControls();
        _masterSlider.ValueChanged += _ => ApplyFromControls();
        _musicSlider.ValueChanged += _ => ApplyFromControls();
        _sfxSlider.ValueChanged += _ => ApplyFromControls();
        _fullscreenToggle.Toggled += _ => ApplyFromControls();
        _vsyncToggle.Toggled += _ => ApplyFromControls();
        _aimToggle.Toggled += _ => ApplyFromControls();
        _crouchToggle.Toggled += _ => ApplyFromControls();

        Hide();
    }

    // ====================================================================================
    //  Apertura / chiusura
    // ====================================================================================

    // _Input e non _UnhandledInput: la schermata inventario ha MouseFilter.Stop e i pannelli
    // catturano il focus, quindi ESC va intercettato prima che la UI sottostante lo consumi.
    public override void _Input(InputEvent @event)
    {
        // Cattura di un nuovo binding: il menu si prende ogni evento finche' non ha finito, altrimenti
        // il tasto premuto per riassegnare verrebbe anche ESEGUITO dalla UI sottostante.
        if (_capturingAction != null)
        {
            CaptureBinding(@event);
            GetViewport().SetInputAsHandled();
            return;
        }

        if (_swallowRelease && @event is InputEventKey or InputEventMouseButton && !@event.IsPressed())
        {
            _swallowRelease = false;
            GetViewport().SetInputAsHandled();
            return;
        }

        if (!@event.IsActionPressed("toggle_menu"))
            return;

        if (!Visible)
            Open();
        else if (_bindingsPage.Visible)
            ShowPage(Page.Settings);
        else if (_settingsPage.Visible)
            ShowPage(Page.Root); // ESC nelle impostazioni torna indietro, non chiude tutto
        else
            Close();

        GetViewport().SetInputAsHandled();
    }

    /// <param name="settingsOnly">Apre direttamente le Impostazioni (usato dal menu principale,
    /// dove "Riprendi"/"Esci dalla partita" non hanno senso).</param>
    public void Open(bool settingsOnly = false)
    {
        SyncControlsFromSettings();
        // Fuori partita non c'e' una sessione da lasciare.
        _leaveButton.Visible = _game.CurrentPhase == GameManager.GamePhase.InGame;
        ShowPage(settingsOnly ? Page.Settings : Page.Root);
        Show();
        _game.UiModalOpen = true;
    }

    public void Close()
    {
        CancelCapture();
        Hide();
        _game.UiModalOpen = false;
        _settings.Save();
    }

    private void ShowPage(Page page)
    {
        CancelCapture();

        _rootPage.Visible = page == Page.Root;
        _settingsPage.Visible = page == Page.Settings;
        _bindingsPage.Visible = page == Page.Bindings;

        if (page == Page.Bindings)
            RefreshBindingLabels();
        if (page == Page.Root)
            _settings.Save(); // uscendo dalle impostazioni consolida su disco
    }

    private void OnLeaveSession()
    {
        // La disconnessione pulita (ritorno al MainMenu, despawn dei player) non e' ancora
        // supportata dal NetworkManager: chiudere il processo e' l'unica uscita coerente.
        // Vedi la skill ui-hud (host migration / uscita dalla sessione non implementate).
        GetTree().Quit();
    }

    // ====================================================================================
    //  Impostazioni <-> controlli
    // ====================================================================================

    /// <summary>
    /// Popola l'elenco delle lingue. Le voci sono ENDONIMI e restano invariate al cambio lingua
    /// (l'OptionButton ha auto_translate_mode = disabled nella scena): l'unica voce tradotta e'
    /// "Sistema", che va rigenerata quando la lingua cambia — vedi <see cref="_Notification"/>.
    /// </summary>
    private void BuildLanguageOptions()
    {
        int previous = _languageOption.Selected;
        _languageOption.Clear();

        for (int i = 0; i < SettingsService.AvailableLanguages.Length; i++)
        {
            (string code, string label) = SettingsService.AvailableLanguages[i];
            _languageOption.AddItem(
                code == SettingsService.SystemLanguage ? Loc.T("UI_SETTINGS_LANGUAGE_SYSTEM") : label, i);
        }

        if (previous >= 0 && previous < _languageOption.ItemCount)
            _languageOption.Selected = previous;
    }

    /// <summary>
    /// Testi composti da codice: la voce "Sistema" dell'elenco lingue e le etichette dei tasti (i
    /// pulsanti del mouse sono tradotti, <c>UI_MOUSE_*</c>). L'auto-translate non li copre.
    /// </summary>
    public override void _Notification(int what)
    {
        if (what != NotificationTranslationChanged || _languageOption == null)
            return;

        BuildLanguageOptions();
        RefreshBindingLabels();
    }

    // ====================================================================================
    //  Comandi (rimappatura tasti)
    // ====================================================================================

    /// <summary>
    /// Costruisce l'elenco dei comandi: un titolo per gruppo e una riga per azione (nome + pulsante
    /// col tasto corrente). Si costruisce da codice, una volta sola, perche' la sorgente di verita' e'
    /// <see cref="InputBindings.Groups"/>: aggiungere un'azione riassegnabile non deve voler dire
    /// aggiungere anche due nodi alla scena.
    /// </summary>
    private void BuildBindingRows()
    {
        foreach (InputBindings.ActionGroup group in InputBindings.Groups)
        {
            // Testo statico: l'auto-translate risolve la chiave e la riaggiorna al cambio lingua.
            _bindingsList.AddChild(new Label
            {
                Text = group.TitleKey,
                ThemeTypeVariation = "HeaderSmall",
            });

            foreach (string action in group.Actions)
            {
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 16);

                row.AddChild(new Label
                {
                    Text = InputBindings.LabelKey(action),
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                });

                var button = new Button
                {
                    CustomMinimumSize = new Vector2(150, 0),
                    AutoTranslateMode = AutoTranslateModeEnum.Disabled, // testo gia' risolto da Loc
                };
                string captured = action;
                button.Pressed += () => BeginCapture(captured);

                row.AddChild(button);
                _bindingButtons[action] = button;
                _bindingsList.AddChild(row);
            }
        }
    }

    /// Riscrive il tasto mostrato da ogni riga e riporta il suggerimento allo stato neutro.
    private void RefreshBindingLabels()
    {
        foreach ((string action, Button button) in _bindingButtons)
            button.Text = Loc.KeyFor(action);

        _bindingsHint.Text = Loc.T("UI_BIND_HINT");
    }

    private void BeginCapture(string action)
    {
        CancelCapture();

        _capturingAction = action;
        _bindingButtons[action].Text = Loc.T("UI_BIND_PRESS");
        _bindingsHint.Text = Loc.T("UI_BIND_PRESS");
    }

    private void CancelCapture()
    {
        if (_capturingAction == null)
            return;

        _capturingAction = null;
        RefreshBindingLabels();
    }

    /// <summary>
    /// Consuma l'evento con cui il giocatore sta scegliendo il nuovo tasto. Esc annulla; tutto cio'
    /// che non e' assegnabile (rotella, movimento del mouse, gamepad) viene ignorato restando in
    /// attesa. I doppioni sono ammessi e solo segnalati: nel progetto ce ne sono di voluti
    /// (skill ui-hud §4).
    /// </summary>
    private void CaptureBinding(InputEvent @event)
    {
        if (!@event.IsPressed() || @event.IsEcho())
            return;

        if (@event is InputEventKey { PhysicalKeycode: Key.Escape } or InputEventKey { Keycode: Key.Escape })
        {
            _swallowRelease = true;
            CancelCapture();
            return;
        }

        InputEvent? normalized = InputBindings.Normalize(@event);
        if (normalized == null)
            return;

        string action = _capturingAction!;
        string? conflict = InputBindings.FindConflict(normalized, action);

        _settings.SetBinding(action, normalized);
        _settings.Save();

        _swallowRelease = true;
        _capturingAction = null;
        RefreshBindingLabels();

        if (conflict != null)
            _bindingsHint.Text = Loc.T("UI_BIND_CONFLICT", Loc.KeyFor(action), Loc.T(InputBindings.LabelKey(conflict)));
    }

    private void OnResetBindings()
    {
        CancelCapture();
        _settings.ResetBindings();
        _settings.Save();
        RefreshBindingLabels();
    }

    private void SyncControlsFromSettings()
    {
        _syncing = true;

        _languageOption.Selected = LanguageIndex(_settings.Language);
        _uiScaleSlider.Value = _settings.UiScale;
        _fullscreenToggle.ButtonPressed = _settings.Fullscreen;
        _vsyncToggle.ButtonPressed = _settings.VSyncEnabled;
        _aimToggle.ButtonPressed = _settings.AimToggle;
        _crouchToggle.ButtonPressed = _settings.CrouchToggle;
        _masterSlider.Value = _settings.MasterVolume;
        _musicSlider.Value = _settings.MusicVolume;
        _sfxSlider.Value = _settings.SfxVolume;

        _syncing = false;
        UpdateValueLabels();
    }

    private void ApplyFromControls()
    {
        if (_syncing)
            return;

        _settings.Language = LanguageCode(_languageOption.Selected);
        _settings.UiScale = (float)_uiScaleSlider.Value;
        _settings.Fullscreen = _fullscreenToggle.ButtonPressed;
        _settings.VSyncEnabled = _vsyncToggle.ButtonPressed;
        _settings.AimToggle = _aimToggle.ButtonPressed;
        _settings.CrouchToggle = _crouchToggle.ButtonPressed;
        _settings.MasterVolume = (float)_masterSlider.Value;
        _settings.MusicVolume = (float)_musicSlider.Value;
        _settings.SfxVolume = (float)_sfxSlider.Value;

        _settings.ApplyAll();
        UpdateValueLabels();
    }

    private void UpdateValueLabels()
    {
        _uiScaleValue.Text = Percent(_uiScaleSlider.Value);
        _masterValue.Text = Percent(_masterSlider.Value);
        _musicValue.Text = Percent(_musicSlider.Value);
        _sfxValue.Text = Percent(_sfxSlider.Value);
    }

    private static string Percent(double value) => Loc.T("UI_SETTINGS_PERCENT", Loc.Num((float)value * 100f, "0"));

    private static int LanguageIndex(string code)
    {
        for (int i = 0; i < SettingsService.AvailableLanguages.Length; i++)
            if (SettingsService.AvailableLanguages[i].Code == code)
                return i;
        return 0; // "Sistema"
    }

    private static string LanguageCode(int index)
        => index >= 0 && index < SettingsService.AvailableLanguages.Length
            ? SettingsService.AvailableLanguages[index].Code
            : SettingsService.SystemLanguage;
}
