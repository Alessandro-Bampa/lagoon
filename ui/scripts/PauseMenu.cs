using Godot;

namespace Lagoon;

/// <summary>
/// Menu di pausa (ESC) con sotto-pagina Impostazioni. E' il punto in cui il giocatore regola la
/// <b>Scala UI</b>: il progetto usa una UI a dimensione pixel fissa (stretch "disabled",
/// CLAUDE.md §7), quindi su schermi ad alta risoluzione serve una leva esplicita per ingrandirla.
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
    private SettingsService _settings = null!;
    private GameManager _game = null!;

    private Control _rootPage = null!;
    private Control _settingsPage = null!;
    private Button _leaveButton = null!;

    private HSlider _uiScaleSlider = null!;
    private Label _uiScaleValue = null!;
    private CheckButton _fullscreenToggle = null!;
    private CheckButton _vsyncToggle = null!;
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
        _leaveButton = GetNode<Button>("%LeaveButton");

        _uiScaleSlider = GetNode<HSlider>("%UiScaleSlider");
        _uiScaleValue = GetNode<Label>("%UiScaleValue");
        _fullscreenToggle = GetNode<CheckButton>("%FullscreenToggle");
        _vsyncToggle = GetNode<CheckButton>("%VSyncToggle");
        _masterSlider = GetNode<HSlider>("%MasterSlider");
        _masterValue = GetNode<Label>("%MasterValue");
        _musicSlider = GetNode<HSlider>("%MusicSlider");
        _musicValue = GetNode<Label>("%MusicValue");
        _sfxSlider = GetNode<HSlider>("%SfxSlider");
        _sfxValue = GetNode<Label>("%SfxValue");

        _uiScaleSlider.MinValue = SettingsService.MinUiScale;
        _uiScaleSlider.MaxValue = SettingsService.MaxUiScale;

        GetNode<Button>("%ResumeButton").Pressed += Close;
        GetNode<Button>("%SettingsButton").Pressed += () => ShowPage(settings: true);
        GetNode<Button>("%BackButton").Pressed += () => ShowPage(settings: false);
        _leaveButton.Pressed += OnLeaveSession;
        GetNode<Button>("%QuitButton").Pressed += () => GetTree().Quit();

        // Applicazione live: il giocatore vede l'effetto mentre trascina.
        _uiScaleSlider.ValueChanged += _ => ApplyFromControls();
        _masterSlider.ValueChanged += _ => ApplyFromControls();
        _musicSlider.ValueChanged += _ => ApplyFromControls();
        _sfxSlider.ValueChanged += _ => ApplyFromControls();
        _fullscreenToggle.Toggled += _ => ApplyFromControls();
        _vsyncToggle.Toggled += _ => ApplyFromControls();

        Hide();
    }

    // ====================================================================================
    //  Apertura / chiusura
    // ====================================================================================

    // _Input e non _UnhandledInput: la schermata inventario ha MouseFilter.Stop e i pannelli
    // catturano il focus, quindi ESC va intercettato prima che la UI sottostante lo consumi.
    public override void _Input(InputEvent @event)
    {
        if (!@event.IsActionPressed("toggle_menu"))
            return;

        if (!Visible)
            Open();
        else if (_settingsPage.Visible)
            ShowPage(settings: false); // ESC nelle impostazioni torna indietro, non chiude tutto
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
        ShowPage(settings: settingsOnly);
        Show();
        _game.UiModalOpen = true;
    }

    public void Close()
    {
        Hide();
        _game.UiModalOpen = false;
        _settings.Save();
    }

    private void ShowPage(bool settings)
    {
        _rootPage.Visible = !settings;
        _settingsPage.Visible = settings;
        if (!settings)
            _settings.Save(); // uscendo dalle impostazioni consolida su disco
    }

    private void OnLeaveSession()
    {
        // La disconnessione pulita (ritorno al MainMenu, despawn dei player) non e' ancora
        // supportata dal NetworkManager: chiudere il processo e' l'unica uscita coerente.
        // Vedi CLAUDE.md §12 (host migration / uscita dalla sessione non implementate).
        GetTree().Quit();
    }

    // ====================================================================================
    //  Impostazioni <-> controlli
    // ====================================================================================

    private void SyncControlsFromSettings()
    {
        _syncing = true;

        _uiScaleSlider.Value = _settings.UiScale;
        _fullscreenToggle.ButtonPressed = _settings.Fullscreen;
        _vsyncToggle.ButtonPressed = _settings.VSyncEnabled;
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

        _settings.UiScale = (float)_uiScaleSlider.Value;
        _settings.Fullscreen = _fullscreenToggle.ButtonPressed;
        _settings.VSyncEnabled = _vsyncToggle.ButtonPressed;
        _settings.MasterVolume = (float)_masterSlider.Value;
        _settings.MusicVolume = (float)_musicSlider.Value;
        _settings.SfxVolume = (float)_sfxSlider.Value;

        _settings.ApplyAll();
        UpdateValueLabels();
    }

    private void UpdateValueLabels()
    {
        _uiScaleValue.Text = $"{_uiScaleSlider.Value * 100:0}%";
        _masterValue.Text = $"{_masterSlider.Value * 100:0}%";
        _musicValue.Text = $"{_musicSlider.Value * 100:0}%";
        _sfxValue.Text = $"{_sfxSlider.Value * 100:0}%";
    }
}
