using Godot;
using GDDict = Godot.Collections.Dictionary;

namespace Lagoon;

/// <summary>
/// Radice della scena di gioco. Gestisce lo spawn/despawn degli avatar reagendo agli eventi peer
/// del <see cref="NetworkManager"/> (via <see cref="EventBus"/>), e — per la Fase 2 — anche gli item
/// nel mondo (pickup/drop). Tutto avviene SOLO sull'host (autorita' server, CLAUDE.md §3): i
/// <c>MultiplayerSpawner</c> replicano automaticamente avatar e pickup a tutti i client.
/// </summary>
public partial class GameWorld : Node3D
{
    /// Gruppo per raggiungere il GameWorld dai nodi Player (PlayerInventory) senza path fragili.
    public const string GroupName = "game_world";

    private const string PlayerScenePath = "res://player/scenes/Player.tscn";
    private const string PickupScenePath = "res://inventory/scenes/ItemPickup.tscn";

    /// <summary>
    /// Punto del mondo attorno a cui nascono gli avatar e gli oggetti di prova; Y = quota del suolo.
    /// Zero nel livello di prova; sulla mappa vera (<c>MainLaguna.tscn</c>) e' Piazza San Marco.
    /// </summary>
    [Export] public Vector3 SpawnOrigin { get; set; } = Vector3.Zero;

    private PackedScene _playerScene = null!;
    private PackedScene _pickupScene = null!;
    private Node3D _players = null!;
    private Node3D _worldItems = null!;
    private MultiplayerSpawner _itemSpawner = null!;
    private EventBus _eventBus = null!;

    private int _pickupCounter;
    private int _spawnSerial;
    private bool _worldPopulated;

    public override void _Ready()
    {
        AddToGroup(GroupName);

        _playerScene = GD.Load<PackedScene>(PlayerScenePath);
        _pickupScene = GD.Load<PackedScene>(PickupScenePath);
        _players = GetNode<Node3D>("Players");
        _worldItems = GetNode<Node3D>("WorldItems");
        _itemSpawner = GetNode<MultiplayerSpawner>("ItemSpawner");
        _eventBus = GetNode<EventBus>("/root/EventBus");

        // La spawn-function va impostata su TUTTI i peer: ricostruisce il pickup dai dati replicati.
        _itemSpawner.SpawnFunction = new Callable(this, MethodName.SpawnPickupNode);

        _eventBus.PeerJoined += OnPeerJoined;
        _eventBus.PeerLeft += OnPeerLeft;
    }

    // ====================================================================================
    //  Avatar
    // ====================================================================================

    private void OnPeerJoined(long peerId)
    {
        // Doppia guardia: solo l'host istanzia gli avatar e popola il mondo.
        if (!Multiplayer.IsServer())
            return;

        PopulateWorldItemsOnce();
        SpawnPlayer(peerId);
    }

    private void OnPeerLeft(long peerId)
    {
        if (!Multiplayer.IsServer())
            return;

        Node? existing = _players.GetNodeOrNull(peerId.ToString());
        existing?.QueueFree();
    }

    private void SpawnPlayer(long peerId)
    {
        if (_players.HasNode(peerId.ToString()))
            return;

        Node3D player = _playerScene.Instantiate<Node3D>();
        // Il nome = id del peer: PlayerController lo usa per l'autorita' di rete e il MultiplayerSpawner
        // replica nome + istanza ai client.
        player.Name = peerId.ToString();
        player.Position = SpawnPointFor(peerId);
        _players.AddChild(player, forceReadableName: true);

        // Kit iniziale (host-authoritative): gilet + zaino + qualche item, cosi' le griglie esistono
        // subito e il criterio di completamento della Fase 2 e' verificabile da subito.
        player.GetNode<PlayerInventory>("Inventory").HostGiveStartingKit();

        GD.Print($"[GameWorld] Avatar spawnato per il peer {peerId}.");
    }

    /// <summary>
    /// Punto di nascita di un peer: una di quattro posizioni affiancate attorno a
    /// <see cref="SpawnOrigin"/>, ricavata dall'id. E' deterministico di proposito: il movimento e'
    /// del proprietario (Fase 1) e il <c>MultiplayerSpawner</c> non replica la posizione scelta
    /// dall'host, quindi l'avatar di un client si colloca da se' (<see cref="PlayerController"/>)
    /// con lo stesso calcolo, senza aspettare la rete. Due peer possono cadere sulla stessa
    /// posizione: le capsule si scostano.
    /// </summary>
    public Vector3 SpawnPointFor(long peerId)
    {
        int slot = (int)(System.Math.Abs(peerId) % 4);
        return SpawnOrigin + new Vector3((slot - 1.5f) * 2.0f, 1.0f, 0f);
    }

    /// <summary>
    /// Avatar del peer dato (o null). Il nome del nodo E' l'id del peer (vedi <see cref="SpawnPlayer"/>),
    /// quindi la ricerca e' diretta. Serve all'host per validare un intento (es. "prendo il timone")
    /// contro la posizione replicata del richiedente.
    /// </summary>
    public PlayerController? FindPlayer(int peerId)
    {
        return _players.GetNodeOrNull<PlayerController>(peerId.ToString());
    }

    /// Tutti gli avatar presenti (es. per sapere chi sta a bordo di una barca, skill vehicles-boats §12).
    public System.Collections.Generic.IEnumerable<PlayerController> AllPlayers()
    {
        foreach (Node child in _players.GetChildren())
            if (child is PlayerController player && !player.IsQueuedForDeletion())
                yield return player;
    }

    // ====================================================================================
    //  Item nel mondo (host-authoritative, replicati dal MultiplayerSpawner)
    // ====================================================================================

    /// <summary>
    /// Spawna un pickup nel mondo dal payload serializzato di un item (solo host), alla quota di
    /// <paramref name="position"/> (il suolo: chi lascia un oggetto passa la posizione dei piedi). <paramref name="reuseUid"/> &gt; 0 riusa un uid esistente: serve a
    /// <see cref="ReplacePickupPayload"/> per mantenere stabile l'identita' del pickup.
    /// </summary>
    public void SpawnPickupFromPayload(GDDict payload, Vector3 position, int reuseUid = 0, bool anchored = false)
    {
        if (!Multiplayer.IsServer())
            return;

        var data = new GDDict
        {
            { "uid", reuseUid > 0 ? reuseUid : ++_pickupCounter },
            { "payload", payload },
            { "x", position.X },
            { "y", position.Y },
            { "z", position.Z },
            { "anchored", anchored },
        };
        _itemSpawner.Spawn(data);
    }

    /// <summary>
    /// Spawna a terra un'istanza gia' costruita (col suo contenuto). Solo host.
    /// </summary>
    public void SpawnPickup(ItemInstance? item, Vector3 position)
    {
        if (item != null)
            SpawnPickupFromPayload(ItemCodec.Serialize(item), position);
    }

    /// Spawna un item "nudo" (senza contenuto): comodita' per il popolamento di test.
    public void SpawnPickup(string itemId, int stackCount, Vector3 position)
    {
        var db = GetNode<ItemDatabase>("/root/ItemDatabase");
        SpawnPickup(ItemFactory.Create(db, itemId, stackCount), position);
    }

    /// <summary>
    /// Spawna un contenitore FISSO (cassa, baule): con F si apre invece di essere raccolto.
    /// <paramref name="contents"/> = istanze da metterci dentro (quelle che non entrano si scartano).
    /// I cadaveri useranno lo stesso meccanismo.
    /// </summary>
    public void SpawnWorldContainer(string itemId, Vector3 position, params ItemInstance?[] contents)
    {
        var db = GetNode<ItemDatabase>("/root/ItemDatabase");
        ItemInstance? root = ItemFactory.Create(db, itemId);
        if (root == null || !root.IsContainer)
        {
            GD.PrintErr($"[GameWorld] '{itemId}' non e' un contenitore valido.");
            return;
        }

        foreach (ItemInstance? item in contents)
        {
            if (item == null)
                continue;
            if (!InventoryRules.TryFindSpot(root, item, out int g, out int x, out int y, out bool rotated)
                || !root.Grids[g].Place(item, x, y, rotated))
                GD.PrintErr($"[GameWorld] '{item.Definition.ItemId}' non entra in '{itemId}'.");
        }

        SpawnPickupFromPayload(ItemCodec.Serialize(root), position, anchored: true);
    }

    /// <summary>
    /// Sostituisce il contenuto di un pickup a terra: despawn + respawn con lo STESSO uid e la
    /// stessa posizione. I dati di spawn del <c>MultiplayerSpawner</c> restano cosi' l'unica fonte
    /// di verita' (anche per i late-joiner), senza replicare Dictionary annidati su altri canali.
    /// </summary>
    public void ReplacePickupPayload(int uid, GDDict newPayload)
    {
        if (!Multiplayer.IsServer())
            return;

        ItemPickup? existing = FindPickup(uid);
        if (existing == null)
            return;

        Vector3 position = existing.Position;
        bool anchored = existing.Anchored; // una cassa resta una cassa dopo essere stata saccheggiata
        DespawnPickup(uid);
        SpawnPickupFromPayload(newPayload, position, reuseUid: uid, anchored: anchored);
    }

    /// Ricostruisce il nodo pickup dai dati di spawn (eseguita su host e client dal MultiplayerSpawner).
    public Node SpawnPickupNode(Variant data)
    {
        GDDict dict = data.AsGodotDictionary();
        int uid = dict["uid"].AsInt32();
        var pickup = _pickupScene.Instantiate<ItemPickup>();
        // Uid replicato = identita' cross-peer (find/despawn per uid, non per nome del nodo).
        // Il nome include il contatore per evitare collisioni con il nodo in fase di QueueFree
        // durante un ReplacePickupPayload (stesso uid, nodo vecchio non ancora liberato).
        pickup.Name = $"pk_{uid}_{++_spawnSerial}";
        pickup.Uid = uid;
        pickup.Payload = dict["payload"].AsGodotDictionary();
        pickup.Anchored = dict.TryGetValue("anchored", out Variant anchored) && anchored.AsBool();
        // La quota viaggia con i dati di spawn: sulla mappa vera il suolo non e' a 0 (isole a +1 m).
        float y = dict.TryGetValue("y", out Variant yValue) ? yValue.AsSingle() : 0f;
        pickup.Position = new Vector3(dict["x"].AsSingle(), y, dict["z"].AsSingle());
        return pickup;
    }

    /// <summary>
    /// Pickup con lo uid dato (o null). Ricerca per uid (non per nome del nodo) e ignora i nodi
    /// gia' in coda di distruzione: durante un <see cref="ReplacePickupPayload"/> il vecchio nodo
    /// con lo stesso uid puo' essere ancora nell'albero fino a fine frame.
    /// </summary>
    public ItemPickup? FindPickup(int uid)
    {
        foreach (Node child in _worldItems.GetChildren())
            if (child is ItemPickup pickup && pickup.Uid == uid && !pickup.IsQueuedForDeletion())
                return pickup;
        return null;
    }

    /// Rimuove un pickup dal mondo (solo host); il MultiplayerSpawner replica la rimozione.
    public void DespawnPickup(int uid)
    {
        if (!Multiplayer.IsServer())
            return;
        FindPickup(uid)?.QueueFree();
    }

    private void PopulateWorldItemsOnce()
    {
        if (_worldPopulated)
            return;
        _worldPopulated = true;

        var db = GetNode<ItemDatabase>("/root/ItemDatabase");
        ItemInstance? Item(string id, int stack = 1) => ItemFactory.Create(db, id, stack);
        // Posizioni relative al punto di spawn, sul suolo.
        Vector3 At(float x, float z) => SpawnOrigin + new Vector3(x, 0f, z);

        // Oggetti di prova davanti allo spawn: coprono slot, blocchi, stack, caricatori e filtri.
        SpawnPickup("helmet", 1, At(-4.5f, 3.0f));
        SpawnPickup("headset", 1, At(-3.0f, 3.0f));
        SpawnPickup("body_armor", 1, At(-1.5f, 3.0f));
        SpawnPickup("backpack", 1, At(0.0f, 3.0f));
        SpawnPickup("ammo_545", 45, At(1.5f, 3.0f));
        SpawnPickup("medkit", 1, At(3.0f, 3.0f));
        SpawnPickup("ammo_545_box", 1, At(4.5f, 3.0f));

        SpawnPickup("armored_rig", 1, At(-4.5f, 5.0f));
        SpawnPickup("knife", 1, At(-3.0f, 5.0f));
        SpawnPickup("flashlight", 1, At(-1.5f, 5.0f));
        SpawnPickup(ItemFactory.CreateWeapon(db, "rifle", "mag_545", "ammo_545"), At(0.5f, 5.0f));
        SpawnPickup(ItemFactory.CreateWeapon(db, "pistol", "mag_9mm", "ammo_9mm"), At(2.5f, 5.0f));
        SpawnPickup("ammo_case", 1, At(4.0f, 5.0f));
        SpawnPickup("cash", 1500, At(-1.5f, 6.5f));
        SpawnPickup("canned_food", 1, At(0.0f, 6.5f));
        SpawnPickup("water_bottle", 1, At(1.5f, 6.5f));
        SpawnPickup("baton", 1, At(3.0f, 6.5f));

        // Contenitori fissi: con F si aprono nel pannello destro (schermata di saccheggio).
        SpawnWorldContainer("supply_crate", At(-6.0f, 4.0f),
            Item("ammo_545", 60), Item("ammo_9mm", 50), Item("ammo_545_box"),
            ItemFactory.CreateMagazine(db, "mag_545", "ammo_545", 30),
            ItemFactory.CreateMagazine(db, "mag_545", "ammo_545", 12),
            ItemFactory.CreateMagazine(db, "mag_9mm", "ammo_9mm", 12),
            Item("medkit"), Item("painkillers"), Item("syringe"));

        SpawnWorldContainer("stash_box", At(6.5f, 4.5f),
            Item("helmet"), Item("cash", 5000), Item("cash", 800), Item("key"), Item("keycard"),
            Item("usb_drive"), Item("cell_phone"), Item("cigarettes"), Item("lighter"), Item("vape"),
            Item("nails"), Item("poison"), Item("pills_bottle"), Item("doughnut"), Item("pickles"),
            Item("canned_food"), Item("water_bottle"), Item("knife"), Item("flashlight"));

        SpawnWorldContainer("cardboard_box", At(6.5f, 7.0f),
            Item("canned_food"), Item("doughnut"), Item("cigarettes"), Item("lighter"));
    }
}
