using Godot;

namespace Lagoon;

/// <summary>
/// Menu di avvio: host/join sia via Steam (path primario) sia via ENet locale (fallback dev per
/// test multi-istanza sullo stesso PC, CLAUDE.md §6). Non contiene logica di rete: delega tutto
/// al <see cref="NetworkManager"/> e reagisce ai suoi segnali per aggiornare lo stato.
/// </summary>
public partial class MainMenu : Control
{
    private NetworkManager _network = null!;

    private LineEdit _targetField = null!;
    private Label _status = null!;

    // Ultimo messaggio di stato in forma NON risolta: la Label mostra testo gia' tradotto, che al
    // cambio lingua non si potrebbe ritradurre a partire da se' stesso.
    private string? _statusKey;
    private object[] _statusArgs = System.Array.Empty<object>();

    public override void _Ready()
    {
        _network = GetNode<NetworkManager>("/root/NetworkManager");

        _targetField = GetNode<LineEdit>("%TargetField");
        _status = GetNode<Label>("%StatusLabel");

        GetNode<Button>("%HostSteamButton").Pressed += () => StartHost(NetworkManager.TransportMode.Steam);
        GetNode<Button>("%JoinSteamButton").Pressed += () => StartJoin(NetworkManager.TransportMode.Steam);
        GetNode<Button>("%HostLocalButton").Pressed += () => StartHost(NetworkManager.TransportMode.LocalEnet);
        GetNode<Button>("%JoinLocalButton").Pressed += () => StartJoin(NetworkManager.TransportMode.LocalEnet);

        // Le opzioni (fra cui la scala UI) servono anche prima di entrare in partita: riusa lo
        // stesso pannello del menu di pausa, aperto direttamente sulla pagina Impostazioni.
        GetNode<Button>("%SettingsButton").Pressed += () =>
            GetParent().GetNode<PauseMenu>("PauseMenu").Open(settingsOnly: true);

        _network.HostStarted += OnHostStarted;
        _network.ClientConnected += OnClientConnected;
        _network.NetworkFailed += OnNetworkFailed;
    }

    private void StartHost(NetworkManager.TransportMode mode)
    {
        SetStatus(mode == NetworkManager.TransportMode.Steam
            ? "UI_MENU_STATUS_HOSTING_STEAM"
            : "UI_MENU_STATUS_HOSTING_LOCAL");
        _network.HostGame(mode);
    }

    private void StartJoin(NetworkManager.TransportMode mode)
    {
        SetStatus("UI_MENU_STATUS_CONNECTING");
        _network.JoinGame(mode, _targetField.Text);
    }

    private void OnHostStarted(long lobbyId)
    {
        if (lobbyId > 0)
            SetStatus("UI_MENU_STATUS_HOST_STEAM_OK", lobbyId);
        else
            SetStatus("UI_MENU_STATUS_HOST_LOCAL_OK");
        EnterGame();
    }

    private void OnClientConnected()
    {
        EnterGame();
    }

    private void EnterGame()
    {
        GetNode<GameManager>("/root/GameManager").SetPhase(GameManager.GamePhase.InGame);
        Hide();
    }

    /// Il messaggio arriva dal <see cref="NetworkManager"/> gia' tradotto (chiavi NET_ERR_*):
    /// qui si aggiunge solo la cornice "Errore: ...".
    private void OnNetworkFailed(string message)
    {
        SetStatus("UI_MENU_STATUS_ERROR", message);
    }

    /// <summary>
    /// Scrive la riga di stato da una chiave di traduzione e memorizza chiave+argomenti, cosi' il
    /// testo si puo' rigenerare se la lingua cambia mentre il menu e' a video (l'auto-translate dei
    /// Control non copre le stringhe composte: vedi la skill i18n-localization).
    /// </summary>
    private void SetStatus(string key, params object[] args)
    {
        _statusKey = key;
        _statusArgs = args;
        _status.Text = Loc.T(key, args);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged && _status != null && _statusKey != null)
            _status.Text = Loc.T(_statusKey, _statusArgs);
    }
}
