using Godot;
using GDDict = Godot.Collections.Dictionary;

namespace Lagoon;

/// <summary>
/// Layer di rete dell'inventario di un giocatore (CLAUDE.md §3). Vive come figlio del Player ma la
/// sua autorita' e' SEMPRE l'host: tutte le mutazioni girano sull'host, che poi fa il push dello
/// stato al solo proprietario.
///
/// Ogni RPC porta un INTENTO, mai un risultato: "sposta l'oggetto 12 in quella cella", "rilascialo
/// sull'oggetto 30", "riponilo dove preferisci". Cosa succede davvero lo decide
/// <see cref="InventoryOps"/> dal proprio stato. Il client pre-valida con le stesse regole
/// (<see cref="InventoryRules"/>) solo per colorare l'anteprima.
/// </summary>
public partial class PlayerInventory : Node
{
    /// Emesso sul proprietario quando lo stato replicato cambia (per aggiornare la UI).
    [Signal]
    public delegate void InventoryChangedEventHandler();

    /// Emesso SULL'HOST a ogni push dello stato autoritativo: <see cref="WeaponController"/> lo usa
    /// per riallineare caricatore e riserva e per rivalidare l'arma impugnata.
    [Signal]
    public delegate void HostStateChangedEventHandler();

    /// Emesso sul proprietario quando l'host rifiuta una richiesta (codice <see cref="InvError"/>).
    [Signal]
    public delegate void OperationFailedEventHandler(int error);

    /// Distanza massima entro cui l'host accetta interazioni col mondo (raccolta, saccheggio).
    public const float PickupRange = 3.5f;

    /// Valore di <c>x</c> che significa "dove c'e' posto in quel contenitore".
    public const int AutoPlace = -1;

    public PlayerInventoryModel Model { get; } = new();

    /// Spazi visti da questo giocatore: stessa risoluzione per l'host e per la UI.
    public InventorySpaces Spaces { get; private set; } = null!;

    private ItemDatabase _db = null!;
    private int _ownerPeerId = NetworkConstants.HostPeerId;

    public int OwnerPeerId => _ownerPeerId;

    public override void _EnterTree()
    {
        // Sovrascrive il set per-peer ricorsivo di PlayerController._EnterTree: l'inventario e'
        // autoritativo lato host, non lato proprietario dell'avatar.
        SetMultiplayerAuthority(NetworkConstants.HostPeerId);
    }

    public override void _Ready()
    {
        _db = GetNode<ItemDatabase>("/root/ItemDatabase");
        Spaces = new InventorySpaces(GetParent<Node3D>(), Model, PickupRange);
        if (int.TryParse(GetParent().Name, out int peerId))
            _ownerPeerId = peerId;
    }

    // ====================================================================================
    //  API host
    // ====================================================================================

    /// Kit iniziale: tasche (fisse), rig con caricatori, zaino, pouch, armi cariche.
    public void HostGiveStartingKit()
    {
        if (!IsMultiplayerAuthority())
            return;

        InventoryOps.Session? s = OpenSession();
        if (s == null)
            return;

        void Give(ItemInstance? item)
        {
            if (item != null && InventoryOps.Acquire(s, item, ItemAddress.PlayerSpace) != InvError.Ok)
                GD.PushWarning($"[Inventory] Kit iniziale: '{item.Definition.ItemId}' non ha trovato posto.");
        }

        Give(ItemFactory.Create(_db, InventoryConfig.Current.PocketsItemId));
        // Vestiti (solo estetici): vanno da soli negli slot Busto, Gambe, Calzature.
        Give(ItemFactory.Create(_db, "work_jacket"));
        Give(ItemFactory.Create(_db, "cargo_pants"));
        Give(ItemFactory.Create(_db, "rubber_boots"));
        Give(ItemFactory.Create(_db, "vest"));
        Give(ItemFactory.Create(_db, "backpack"));
        Give(ItemFactory.Create(_db, "secure_container"));
        Give(ItemFactory.CreateWeapon(_db, "rifle", "mag_545", "ammo_545"));
        Give(ItemFactory.CreateWeapon(_db, "pistol", "mag_9mm", "ammo_9mm"));
        Give(ItemFactory.CreateMagazine(_db, "mag_545", "ammo_545", 30));
        Give(ItemFactory.CreateMagazine(_db, "mag_545", "ammo_545", 30));
        Give(ItemFactory.CreateMagazine(_db, "mag_9mm", "ammo_9mm", 12));
        Give(ItemFactory.Create(_db, "medkit"));
        Give(ItemFactory.Create(_db, "painkillers"));
        Give(ItemFactory.Create(_db, "ammo_545", 60));
        Give(ItemFactory.Create(_db, "ammo_9mm", 30));

        s.Commit();
        PushState();
    }

    /// Per i sistemi host-side che mutano l'inventario dall'esterno (ricarica dell'arma).
    public void HostPushState() => PushState();

    /// <summary>
    /// Solo host: crea un oggetto e lo sistema dove entrerebbe raccogliendolo (slot libero
    /// compatibile prima, poi le griglie). Per collaudi e strumenti di sviluppo (es.
    /// <c>tools/playtest_outfit.gd</c>), non e' un intento di rete. Ritorna true se ha trovato posto.
    /// </summary>
    public bool HostGiveItem(string itemId)
    {
        if (!IsMultiplayerAuthority())
            return false;
        InventoryOps.Session? s = OpenSession();
        ItemInstance? item = ItemFactory.Create(_db, itemId);
        if (s == null || item == null)
            return false;
        bool ok = InventoryOps.Acquire(s, item, ItemAddress.PlayerSpace) == InvError.Ok;
        if (ok)
            s.Commit();
        PushState();
        return ok;
    }

    /// Sessione di operazioni per questo giocatore. Null se il mondo non c'e' (scena in chiusura).
    public InventoryOps.Session? OpenSession()
    {
        var world = GetTree().GetFirstNodeInGroup(GameWorld.GroupName) as GameWorld;
        return world == null ? null : new InventoryOps.Session(Spaces, world, _db);
    }

    // ====================================================================================
    //  Submit: chiamate dalla UI del proprietario. Host = locale; client = RpcId(host).
    // ====================================================================================

    private void Send(StringName method, params Variant[] args)
    {
        if (IsMultiplayerAuthority())
            Call(method, args);
        else
            RpcId(NetworkConstants.HostPeerId, method, args);
    }

    /// Sposta un item in una cella/slot/a terra. <paramref name="amount"/> &gt; 0 divide lo stack.
    public void SubmitMove(int srcSpace, int itemId, ItemAddress to, int x, int y, bool rotated, int amount = 0)
        => Send(MethodName.RequestMove, srcSpace, itemId, to.Space, to.Owner, (int)to.Type, to.Index, x, y, rotated, amount);

    /// Rilascia un item sopra un altro (unisci, carica munizioni, monta caricatore, inserisci).
    public void SubmitDropOnItem(int srcSpace, int itemId, int targetSpace, int targetId)
        => Send(MethodName.RequestDropOnItem, srcSpace, itemId, targetSpace, targetId);

    /// Ctrl+click. <paramref name="sideBUid"/> = contenitore aperto a destra (0 = nessuno).
    public void SubmitQuickMove(int srcSpace, int itemId, int sideBUid)
        => Send(MethodName.RequestQuickMove, srcSpace, itemId, sideBUid);

    public void SubmitQuickEquip(int srcSpace, int itemId)
        => Send(MethodName.RequestQuickEquip, srcSpace, itemId);

    /// Raccoglie con F un oggetto sfuso nel mondo.
    public void SubmitPickup(int worldUid) => Send(MethodName.RequestPickup, worldUid);

    public void SubmitLoadAmmo(int space, int targetId, string ammoItemId)
        => Send(MethodName.RequestLoadAmmo, space, targetId, ammoItemId);

    public void SubmitUnloadAmmo(int space, int targetId)
        => Send(MethodName.RequestUnloadAmmo, space, targetId);

    public void SubmitSort(ItemAddress grid)
        => Send(MethodName.RequestSort, grid.Space, grid.Owner, grid.Index);

    public void SubmitTag(int space, int itemId, string name, int color)
        => Send(MethodName.RequestTag, space, itemId, name, color);

    public void SubmitAssignQuickSlot(int slotIndex, int itemId)
        => Send(MethodName.RequestAssignQuickSlot, slotIndex, itemId);

    public void SubmitUse(int itemId) => Send(MethodName.RequestUse, itemId);

    public void SubmitUnpack(int itemId) => Send(MethodName.RequestUnpack, itemId);

    /// Richiede all'host di reinviare lo stato (UI pronta, apertura dell'inventario).
    public void SubmitRequestState() => Send(MethodName.RequestState);

    // ====================================================================================
    //  Request: eseguite SOLO sull'host, sempre validate (§3.4)
    // ====================================================================================

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void RequestMove(
        int srcSpace, int itemId, int toSpace, int toOwner, int toType, int toIndex,
        int x, int y, bool rotated, int amount)
        => Run(s => InventoryOps.Move(
            s, srcSpace, itemId, ItemAddress.Decode(toSpace, toOwner, toType, toIndex), x, y, rotated, amount));

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void RequestDropOnItem(int srcSpace, int itemId, int targetSpace, int targetId)
        => Run(s => InventoryOps.DropOnItem(s, srcSpace, itemId, targetSpace, targetId));

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void RequestQuickMove(int srcSpace, int itemId, int sideBUid)
        => Run(s => InventoryOps.QuickMove(s, srcSpace, itemId, sideBUid));

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void RequestQuickEquip(int srcSpace, int itemId)
        => Run(s => InventoryOps.QuickEquip(s, srcSpace, itemId));

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void RequestPickup(int worldUid)
        => Run(s => InventoryOps.Pickup(s, worldUid));

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void RequestLoadAmmo(int space, int targetId, string ammoItemId)
        => Run(s => InventoryOps.LoadAmmo(s, space, targetId, ammoItemId));

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void RequestUnloadAmmo(int space, int targetId)
        => Run(s => InventoryOps.UnloadAmmo(s, space, targetId));

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void RequestSort(int space, int owner, int gridIndex)
        => Run(s => InventoryOps.Sort(s, ItemAddress.GridOf(space, owner, gridIndex)));

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void RequestTag(int space, int itemId, string name, int color)
        => Run(s => InventoryOps.SetTag(s, space, itemId, name, color));

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void RequestAssignQuickSlot(int slotIndex, int itemId)
        => Run(s => InventoryOps.AssignQuickSlot(s, slotIndex, itemId));

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void RequestUse(int itemId)
        => Run(s => InventoryOps.Use(s, itemId));

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void RequestUnpack(int itemId)
        => Run(s => InventoryOps.Unpack(s, itemId));

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void RequestState()
    {
        if (ValidateSender())
            PushState();
    }

    /// <summary>
    /// Scheletro comune di ogni richiesta: mittente, sessione, operazione, commit del mondo, push.
    /// Il push avviene SEMPRE, anche su rifiuto: il client riparte comunque dallo stato autoritativo.
    /// </summary>
    private void Run(System.Func<InventoryOps.Session, InvError> operation)
    {
        if (!ValidateSender())
            return;

        InventoryOps.Session? session = OpenSession();
        if (session == null)
            return;

        InvError result = operation(session);
        session.Commit();
        PushState();

        if (result != InvError.Ok && result != InvError.NothingToDo)
            NotifyFailure((int)result);
    }

    // ====================================================================================
    //  Push dello stato + replica
    // ====================================================================================

    private void PushState()
    {
        GDDict data = Model.Serialize();
        if (_ownerPeerId == NetworkConstants.HostPeerId)
            EmitSignal(SignalName.InventoryChanged); // il proprietario e' l'host: il modello e' gia' questo
        else
            RpcId(_ownerPeerId, MethodName.SyncFullState, data);

        // Notifica host-side: lo stato autoritativo e' cambiato, chi ne dipende si riallinei.
        EmitSignal(SignalName.HostStateChanged);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void SyncFullState(GDDict data)
    {
        Model.Deserialize(data, id => _db.Get(id));
        EmitSignal(SignalName.InventoryChanged);
    }

    private void NotifyFailure(int error)
    {
        if (_ownerPeerId == NetworkConstants.HostPeerId)
            OperationRejected(error);
        else
            RpcId(_ownerPeerId, MethodName.OperationRejected, error);
    }

    /// Estetica: l'host comunica al proprietario il motivo del rifiuto (toast e suono).
    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void OperationRejected(int error) => EmitSignal(SignalName.OperationFailed, error);

    // ====================================================================================
    //  Diagnostica
    // ====================================================================================

    /// <summary>
    /// Riepilogo testuale dello stato (uno slot per riga, contenuto annidato rientrato). Serve al
    /// collaudo automatico (<c>tools/playtest_inventory.gd</c>), che da GDScript non puo' leggere
    /// il modello C#. Solo console: non e' testo di UI.
    /// </summary>
    public string DescribeState()
    {
        var text = new System.Text.StringBuilder();
        foreach (EquipSlotType slot in EquipRules.AllSlots)
        {
            ItemInstance? item = Model.Get(slot);
            if (item != null)
                Describe(text, item, $"{slot}: ", 0);
        }
        text.Append($"peso={Model.TotalWeight():0.00} quick=[{string.Join(",", Model.QuickSlots)}]");
        return text.ToString();
    }

    private static void Describe(System.Text.StringBuilder text, ItemInstance item, string prefix, int depth)
    {
        text.Append(new string(' ', depth * 2)).Append(prefix)
            .Append($"{item.Definition.ItemId}#{item.InstanceId}");
        if (item.StackCount > 1)
            text.Append($" x{item.StackCount}");
        if (item.Definition is MagazineDefinition mag)
            text.Append($" {item.RoundCount}/{mag.Capacity}");
        if (item.TagName.Length > 0)
            text.Append($" tag='{item.TagName}'");
        text.AppendLine();

        for (int g = 0; g < item.Grids.Length; g++)
            foreach (ItemInstance child in item.Grids[g].Items)
                Describe(text, child, $"g{g}({child.GridX},{child.GridY}{(child.Rotated ? ",r" : "")}) ", depth + 1);
        for (int i = 0; i < item.SlotItems.Length; i++)
            if (item.SlotItems[i] != null)
                Describe(text, item.SlotItems[i]!, $"slot{i} ", depth + 1);
    }

    // ====================================================================================
    //  Helper
    // ====================================================================================

    private bool ValidateSender()
    {
        if (!IsMultiplayerAuthority())
            return false; // solo l'host esegue davvero il calcolo
        int sender = Multiplayer.GetRemoteSenderId();
        // sender 0 = chiamata locale dell'host; altrimenti deve essere il proprietario dell'inventario.
        return sender == 0 || sender == _ownerPeerId;
    }
}
