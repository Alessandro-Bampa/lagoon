using Godot;

namespace Lagoon;

/// <summary>
/// Setup e gestione della sessione multiplayer (autoload). E' l'UNICO punto che conosce il
/// trasporto: tutto il resto del gioco lavora sull'API Multiplayer di alto livello di Godot
/// (MultiplayerSpawner / MultiplayerSynchronizer / RPC), quindi il gameplay resta identico a
/// prescindere dal trasporto (CLAUDE.md §3).
///
/// Trasporti:
///  - <see cref="TransportMode.Steam"/>: path primario (GodotSteam). Le chiamate a Steam sono
///    "late-bound" via Engine.GetSingleton("Steam") + ClassDB, cosi' il progetto COMPILA anche
///    prima di installare l'addon GodotSteam; il path si attiva a runtime quando la GDExtension
///    e' presente. I nomi di metodi/segnali seguono le convenzioni GodotSteam 4.x e vanno
///    verificati contro la versione installata (vedi CLAUDE.md, gap 4.6.1 <-> 4.20.1).
///  - <see cref="TransportMode.LocalEnet"/>: fallback dev (ENet su 127.0.0.1) per testare piu'
///    istanze sullo stesso PC (CLAUDE.md §6) senza Steam.
///
/// NB: host migration NON implementata. Se l'host esce, la sessione termina.
/// </summary>
public partial class NetworkManager : Node
{
    public enum TransportMode
    {
        Steam,
        LocalEnet,
    }

    // --- Segnali verso la UI (MainMenu) -------------------------------------------------
    /// Host pronto. lobbyId = id lobby Steam, oppure 0 per ENet.
    [Signal]
    public delegate void HostStartedEventHandler(long lobbyId);

    /// Il client locale si e' connesso all'host.
    [Signal]
    public delegate void ClientConnectedEventHandler();

    /// Errore/fallimento di rete (mostrato in UI).
    [Signal]
    public delegate void NetworkFailedEventHandler(string message);

    public bool IsHost { get; private set; }
    public long CurrentLobbyId { get; private set; }
    public TransportMode ActiveTransport { get; private set; } = TransportMode.LocalEnet;

    private EventBus _eventBus = null!;
    private GameManager _gameManager = null!;

    // Late-bound singleton GodotSteam (null finche' l'addon non e' installato).
    private GodotObject? _steam;
    private bool _steamInitialized;

    // true = stiamo entrando nella lobby di un altro (client); false = abbiamo creato noi la lobby (host).
    // Distingue i due lobby_joined possibili, dato che creare una lobby genera anch'esso lobby_joined.
    private bool _joiningAsClient;

    // Nomi API GodotSteam (convenzioni 4.x). Isolati qui per adattarli facilmente alla versione
    // installata senza toccare la logica.
    private const string SteamSingleton = "Steam";
    private const string SteamPeerClass = "SteamMultiplayerPeer";
    private const int SteamLobbyTypePublic = 2; // GodotSteam: LOBBY_TYPE_PUBLIC

    public override void _Ready()
    {
        _eventBus = GetNode<EventBus>("/root/EventBus");
        _gameManager = GetNode<GameManager>("/root/GameManager");

        Multiplayer.PeerConnected += OnPeerConnected;
        Multiplayer.PeerDisconnected += OnPeerDisconnected;
        Multiplayer.ConnectedToServer += OnConnectedToServer;
        Multiplayer.ConnectionFailed += OnConnectionFailed;
        Multiplayer.ServerDisconnected += OnServerDisconnected;
    }

    public override void _Process(double delta)
    {
        // GodotSteam con embed_callbacks=false NON esegue le callback da solo: vanno "pompate"
        // ogni frame, altrimenti segnali come lobby_created/lobby_joined non scattano mai.
        if (_steamInitialized)
            _steam!.Call("run_callbacks");
    }

    // ====================================================================================
    //  API pubblica
    // ====================================================================================

    /// Avvia una sessione come host. Ritorna false se il trasporto non e' disponibile.
    public bool HostGame(TransportMode mode)
    {
        ActiveTransport = mode;
        return mode switch
        {
            TransportMode.LocalEnet => HostEnet(),
            TransportMode.Steam => HostSteam(),
            _ => false,
        };
    }

    /// Entra in una sessione. Per ENet <paramref name="target"/> e' un indirizzo IP;
    /// per Steam e' l'id (stringa) della lobby.
    public bool JoinGame(TransportMode mode, string target)
    {
        ActiveTransport = mode;
        return mode switch
        {
            TransportMode.LocalEnet => JoinEnet(string.IsNullOrWhiteSpace(target) ? "127.0.0.1" : target),
            TransportMode.Steam => JoinSteam(target),
            _ => false,
        };
    }

    public void Disconnect()
    {
        if (Multiplayer.MultiplayerPeer is not null and not OfflineMultiplayerPeer)
        {
            Multiplayer.MultiplayerPeer.Close();
        }
        Multiplayer.MultiplayerPeer = null;
        IsHost = false;
        CurrentLobbyId = 0;
        _gameManager.SetPhase(GameManager.GamePhase.MainMenu);
    }

    // ====================================================================================
    //  Trasporto ENet (fallback dev locale) — completamente funzionante senza Steam
    // ====================================================================================

    private bool HostEnet()
    {
        var peer = new ENetMultiplayerPeer();
        Error err = peer.CreateServer(NetworkConstants.DefaultPort, NetworkConstants.MaxPlayers);
        if (err != Error.Ok)
        {
            Fail($"ENet: impossibile creare il server (porta {NetworkConstants.DefaultPort}): {err}");
            return false;
        }

        Multiplayer.MultiplayerPeer = peer;
        BecomeHost(lobbyId: 0);
        return true;
    }

    private bool JoinEnet(string address)
    {
        var peer = new ENetMultiplayerPeer();
        Error err = peer.CreateClient(address, NetworkConstants.DefaultPort);
        if (err != Error.Ok)
        {
            Fail($"ENet: impossibile connettersi a {address}:{NetworkConstants.DefaultPort}: {err}");
            return false;
        }

        Multiplayer.MultiplayerPeer = peer;
        GD.Print($"[NetworkManager] Connessione ENet a {address}:{NetworkConstants.DefaultPort}...");
        return true; // la conferma arriva con ConnectedToServer
    }

    // ====================================================================================
    //  Trasporto Steam (primario) — late-bound, robusto all'assenza dell'addon
    // ====================================================================================

    private bool HostSteam()
    {
        if (!EnsureSteam())
            return false;

        try
        {
            // Creando la lobby riceveremo ANCHE lobby_joined (creare = entrare): questo flag dice
            // a OnSteamLobbyJoined di NON trattarci come client, altrimenti sovrascriveremmo il peer host.
            _joiningAsClient = false;
            // La creazione del peer avviene nella callback lobby_created (asincrona).
            _steam!.Call("createLobby", SteamLobbyTypePublic, NetworkConstants.MaxPlayers);
            GD.Print("[NetworkManager] Creazione lobby Steam in corso...");
            return true;
        }
        catch (System.Exception e)
        {
            Fail($"Steam: createLobby fallita (verifica la versione dell'addon): {e.Message}");
            return false;
        }
    }

    private bool JoinSteam(string lobbyIdText)
    {
        if (!EnsureSteam())
            return false;

        if (!long.TryParse(lobbyIdText, out long lobbyId))
        {
            Fail($"Steam: id lobby non valido: '{lobbyIdText}'");
            return false;
        }

        try
        {
            _joiningAsClient = true;
            _steam!.Call("joinLobby", lobbyId);
            GD.Print($"[NetworkManager] Ingresso nella lobby Steam {lobbyId}...");
            return true;
        }
        catch (System.Exception e)
        {
            Fail($"Steam: joinLobby fallita: {e.Message}");
            return false;
        }
    }

    /// Inizializza il singleton Steam, verifica l'esito e collega i segnali lobby (una sola volta).
    private bool EnsureSteam()
    {
        if (_steamInitialized)
            return true;

        if (!Engine.HasSingleton(SteamSingleton))
        {
            Fail("GodotSteam non installato: manca il singleton 'Steam'. "
                 + "Installa la GDExtension (addons/godotsteam) o usa il trasporto locale ENet.");
            return false;
        }

        try
        {
            _steam = Engine.GetSingleton(SteamSingleton);

            // Init con embed_callbacks=false: le callback le pompiamo noi in _Process via run_callbacks.
            // steamInitEx ritorna { status, verbal }: status 0 = OK (SteamAPIInitResult).
            Godot.Collections.Dictionary result =
                _steam.Call("steamInitEx", (long)NetworkConstants.SteamAppId, false).AsGodotDictionary();
            long status = result.ContainsKey("status") ? (long)result["status"] : -1;
            string verbal = result.ContainsKey("verbal") ? (string)result["verbal"] : "";
            GD.Print($"[NetworkManager] Steam init: status={status} {verbal}");

            if (status != 0)
            {
                Fail($"Steam init fallita: {verbal} (status={status}). "
                     + "Steam e' avviato e loggato? steam_appid.txt = 480?");
                return false;
            }

            _steam.Connect("lobby_created",
                Callable.From((long connectResult, long lobbyId) => OnSteamLobbyCreated(connectResult, lobbyId)));
            _steam.Connect("lobby_joined",
                Callable.From((long lobbyId, long permissions, bool locked, long response)
                    => OnSteamLobbyJoined(lobbyId)));

            _steamInitialized = true;
            return true;
        }
        catch (System.Exception e)
        {
            Fail($"Steam: inizializzazione fallita (Steam in esecuzione? appid 480?): {e.Message}");
            return false;
        }
    }

    private void OnSteamLobbyCreated(long result, long lobbyId)
    {
        const long kResultOk = 1; // EResult.k_EResultOK
        if (result != kResultOk)
        {
            Fail($"Steam: creazione lobby fallita (EResult={result}).");
            return;
        }

        MultiplayerPeer? peer = CreateSteamPeer();
        if (peer is null)
            return;

        try
        {
            ((GodotObject)peer).Call("create_host", 0);
        }
        catch (System.Exception e)
        {
            Fail($"Steam: create_host fallita: {e.Message}");
            return;
        }

        Multiplayer.MultiplayerPeer = peer;
        BecomeHost(lobbyId);
    }

    private void OnSteamLobbyJoined(long lobbyId)
    {
        CurrentLobbyId = lobbyId;

        // Se siamo l'host, questo lobby_joined e' l'effetto collaterale del nostro createLobby:
        // il peer host e' gia' stato creato in OnSteamLobbyCreated, non dobbiamo fare nulla.
        if (!_joiningAsClient)
        {
            GD.Print("[NetworkManager] lobby_joined ignorato (siamo l'host della lobby).");
            return;
        }

        long ownerId;
        try
        {
            ownerId = (long)_steam!.Call("getLobbyOwner", lobbyId);
        }
        catch (System.Exception e)
        {
            Fail($"Steam: getLobbyOwner fallita: {e.Message}");
            return;
        }

        MultiplayerPeer? peer = CreateSteamPeer();
        if (peer is null)
            return;

        try
        {
            ((GodotObject)peer).Call("create_client", ownerId, 0);
        }
        catch (System.Exception e)
        {
            Fail($"Steam: create_client fallita: {e.Message}");
            return;
        }

        Multiplayer.MultiplayerPeer = peer;
        GD.Print($"[NetworkManager] Peer Steam client verso owner {ownerId} creato.");
        // La conferma arriva con ConnectedToServer.
    }

    private MultiplayerPeer? CreateSteamPeer()
    {
        if (!ClassDB.ClassExists(SteamPeerClass))
        {
            Fail($"GodotSteam: classe '{SteamPeerClass}' non trovata. GDExtension installata?");
            return null;
        }

        var instance = ClassDB.Instantiate(SteamPeerClass);
        MultiplayerPeer? peer = instance.As<MultiplayerPeer>();
        if (peer is null)
            Fail($"GodotSteam: impossibile creare un {SteamPeerClass}.");
        return peer;
    }

    // ====================================================================================
    //  Stato host condiviso + callback dell'API Multiplayer
    // ====================================================================================

    private void BecomeHost(long lobbyId)
    {
        IsHost = true;
        CurrentLobbyId = lobbyId;
        _gameManager.SetPhase(GameManager.GamePhase.InGame);
        GD.Print($"[NetworkManager] Host avviato (transport={ActiveTransport}, lobby={lobbyId}).");

        EmitSignal(SignalName.HostStarted, lobbyId);
        // L'host non riceve PeerConnected per se stesso: notifichiamo il mondo manualmente
        // cosi' che spawni l'avatar dell'host.
        _eventBus.EmitSignal(EventBus.SignalName.PeerJoined, (long)NetworkConstants.HostPeerId);
    }

    private void OnPeerConnected(long id)
    {
        GD.Print($"[NetworkManager] Peer connesso: {id}");
        // Solo l'host decide gli spawn (autorita' server, CLAUDE.md §3).
        if (Multiplayer.IsServer())
            _eventBus.EmitSignal(EventBus.SignalName.PeerJoined, id);
    }

    private void OnPeerDisconnected(long id)
    {
        GD.Print($"[NetworkManager] Peer disconnesso: {id}");
        if (Multiplayer.IsServer())
            _eventBus.EmitSignal(EventBus.SignalName.PeerLeft, id);
    }

    private void OnConnectedToServer()
    {
        _gameManager.SetPhase(GameManager.GamePhase.InGame);
        GD.Print("[NetworkManager] Connesso all'host.");
        EmitSignal(SignalName.ClientConnected);
        _eventBus.EmitSignal(EventBus.SignalName.ConnectedToServer);
    }

    private void OnConnectionFailed()
    {
        Fail("Connessione all'host fallita.");
    }

    private void OnServerDisconnected()
    {
        // Host uscito: nel prototipo la sessione termina (niente host migration).
        Fail("L'host ha chiuso la sessione.");
        Multiplayer.MultiplayerPeer = null;
        _gameManager.SetPhase(GameManager.GamePhase.MainMenu);
    }

    private void Fail(string message)
    {
        GD.PrintErr($"[NetworkManager] {message}");
        EmitSignal(SignalName.NetworkFailed, message);
        _eventBus.EmitSignal(EventBus.SignalName.NetworkError, message);
    }
}
