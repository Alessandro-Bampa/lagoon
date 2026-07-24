using Godot;

namespace Lagoon;

/// <summary>
/// Menu di avvio: host/join sia via Steam (path primario) sia via ENet locale (fallback dev per
/// test multi-istanza sullo stesso PC, CLAUDE.md §9/§10). Non contiene logica di rete: delega tutto
/// al <see cref="NetworkManager"/> e reagisce ai suoi segnali per aggiornare lo stato.
/// </summary>
public partial class MainMenu : Control
{
    private NetworkManager _network = null!;

    private LineEdit _targetField = null!;
    private Label _status = null!;

    public override void _Ready()
    {
        _network = GetNode<NetworkManager>("/root/NetworkManager");

        _targetField = GetNode<LineEdit>("%TargetField");
        _status = GetNode<Label>("%StatusLabel");

        GetNode<Button>("%HostSteamButton").Pressed += () => StartHost(NetworkManager.TransportMode.Steam);
        GetNode<Button>("%JoinSteamButton").Pressed += () => StartJoin(NetworkManager.TransportMode.Steam);
        GetNode<Button>("%HostLocalButton").Pressed += () => StartHost(NetworkManager.TransportMode.LocalEnet);
        GetNode<Button>("%JoinLocalButton").Pressed += () => StartJoin(NetworkManager.TransportMode.LocalEnet);

        _network.HostStarted += OnHostStarted;
        _network.ClientConnected += OnClientConnected;
        _network.NetworkFailed += OnNetworkFailed;
    }

    private void StartHost(NetworkManager.TransportMode mode)
    {
        _status.Text = mode == NetworkManager.TransportMode.Steam
            ? "Avvio host Steam..."
            : "Avvio host locale...";
        _network.HostGame(mode);
    }

    private void StartJoin(NetworkManager.TransportMode mode)
    {
        _status.Text = "Connessione...";
        _network.JoinGame(mode, _targetField.Text);
    }

    private void OnHostStarted(long lobbyId)
    {
        _status.Text = lobbyId > 0
            ? $"Host Steam attivo. Lobby ID: {lobbyId}"
            : "Host locale attivo (127.0.0.1).";
        Hide();
    }

    private void OnClientConnected()
    {
        Hide();
    }

    private void OnNetworkFailed(string message)
    {
        _status.Text = $"Errore: {message}";
    }
}
