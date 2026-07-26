using Godot;
using GDDict = Godot.Collections.Dictionary;

namespace Lagoon;

/// <summary>
/// Layer di rete dell'inventario di un giocatore (CLAUDE.md §3). Vive come figlio del Player ma —
/// a differenza del movimento (Fase 1, client-authoritative) — la sua autorita' e' SEMPRE l'host:
/// tutte le mutazioni girano sull'host, che poi fa il push dello stato al proprietario.
///
/// Dopo il refactor in stile Tarkov l'API si e' ridotta a poche RPC: quasi ogni interazione
/// (trascinamento, quick move, equip rapido, scarto, raccolta, saccheggio, voci del menu
/// contestuale) e' UNO spostamento fra due <see cref="ItemAddress"/>, gestito da
/// <see cref="ItemTransfer"/>. Cosi' le regole vivono in un solo punto invece di essere ripetute in
/// una RPC per ogni combinazione.
/// </summary>
public partial class PlayerInventory : Node
{
    /// Emesso sul proprietario quando lo stato replicato cambia (per aggiornare la UI).
    [Signal]
    public delegate void InventoryChangedEventHandler();

    /// Emesso SULL'HOST a ogni push dello stato autoritativo. Serve ai sistemi host-side che
    /// dipendono dal contenuto dell'inventario — il <see cref="WeaponController"/> lo usa per
    /// ricalcolare le munizioni di riserva e per rivalidare l'arma impugnata quando questa viene
    /// spostata o lasciata cadere.
    [Signal]
    public delegate void HostStateChangedEventHandler();

    /// Distanza massima entro cui l'host accetta interazioni col mondo (raccolta, saccheggio).
    public const float PickupRange = 3.5f;

    /// Valore di <c>x</c> che significa "auto-piazza nel primo spazio libero".
    public const int AutoPlace = -1;

    public PlayerInventoryModel Model { get; } = new();

    private ItemDatabase _db = null!;
    private int _ownerPeerId = NetworkConstants.HostPeerId;

    public int OwnerPeerId => _ownerPeerId;

    public override void _EnterTree()
    {
        // Sovrascrive il set per-peer ricorsivo fatto da PlayerController._EnterTree: l'inventario
        // e' autoritativo lato host, non lato proprietario dell'avatar.
        SetMultiplayerAuthority(NetworkConstants.HostPeerId);
    }

    public override void _Ready()
    {
        _db = GetNode<ItemDatabase>("/root/ItemDatabase");
        if (int.TryParse(GetParent().Name, out int peerId))
            _ownerPeerId = peerId;
    }

    // ====================================================================================
    //  API host (chiamate localmente sull'host, es. da GameWorld al momento dello spawn)
    // ====================================================================================

    /// Kit iniziale: equipaggia rig, zaino e contenitore sicuro, poi aggiunge qualche item.
    public void HostGiveStartingKit()
    {
        if (!IsMultiplayerAuthority())
            return;

        GiveById("vest", 1);             // slot Rig libero -> auto-equip
        GiveById("backpack", 1);         // slot Zaino libero -> auto-equip
        GiveById("secure_container", 1); // slot Contenitore Sicuro
        GiveById("medkit", 1);
        // Armi: slot WeaponPrimary/Sidearm liberi -> auto-equip, pronte da impugnare con 1 e 3.
        GiveById("rifle", 1);
        GiveById("pistol", 1);
        GiveById("ammo", 60);
        PushState();
    }

    private void GiveById(string itemId, int stack)
    {
        var def = _db.Get(itemId);
        if (def != null)
            Model.TryPickup(def, stack);
    }

    // ====================================================================================
    //  Submit: chiamate dalla UI del proprietario. Host = locale; client = RpcId(host).
    // ====================================================================================

    /// <summary>
    /// Sposta un item da un indirizzo a un altro. <paramref name="x"/> = <see cref="AutoPlace"/>
    /// lascia scegliere all'host la prima cella libera (quick move, equip rapido, raccolta con F).
    /// </summary>
    public void SubmitMove(ItemAddress from, int itemId, ItemAddress to, int x, int y, bool rotated)
    {
        if (IsMultiplayerAuthority())
            RequestMove((int)from.Realm, from.A, from.B, itemId, (int)to.Realm, to.A, to.B, x, y, rotated);
        else
            RpcId(NetworkConstants.HostPeerId, MethodName.RequestMove,
                (int)from.Realm, from.A, from.B, itemId, (int)to.Realm, to.A, to.B, x, y, rotated);
    }

    public void SubmitAssignQuickSlot(int slotIndex, int instanceId)
    {
        if (IsMultiplayerAuthority())
            RequestAssignQuickSlot(slotIndex, instanceId);
        else
            RpcId(NetworkConstants.HostPeerId, MethodName.RequestAssignQuickSlot, slotIndex, instanceId);
    }

    /// Consuma un item utilizzabile (medicinali/cibo). L'effetto sulla salute arrivera' con la Fase 3.
    public void SubmitUse(int instanceId)
    {
        if (IsMultiplayerAuthority())
            RequestUse(instanceId);
        else
            RpcId(NetworkConstants.HostPeerId, MethodName.RequestUse, instanceId);
    }

    /// Apre un pacchetto sigillato, sostituendolo col suo contenuto.
    public void SubmitUnpack(int instanceId)
    {
        if (IsMultiplayerAuthority())
            RequestUnpack(instanceId);
        else
            RpcId(NetworkConstants.HostPeerId, MethodName.RequestUnpack, instanceId);
    }

    /// Richiede all'host di reinviare lo stato (quando la UI e' pronta, per evitare la corsa
    /// tra la replica dell'avatar e il primo <see cref="SyncFullState"/>).
    public void SubmitRequestState()
    {
        if (IsMultiplayerAuthority())
            PushState();
        else
            RpcId(NetworkConstants.HostPeerId, MethodName.RequestState);
    }

    // ====================================================================================
    //  Request: eseguite SOLO sull'host (validate). Mai fidarsi dell'input del client (§3.4).
    // ====================================================================================

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void RequestMove(
        int fromRealm, int fromA, int fromB, int itemId,
        int toRealm, int toA, int toB, int x, int y, bool rotated)
    {
        if (!ValidateSender())
            return;

        GameWorld? world = GetGameWorld();
        if (world == null)
            return;

        var ctx = new ItemTransfer.Context
        {
            Model = Model,
            World = world,
            Resolve = ResolveDefinition,
            PlayerPosition = GetParent<Node3D>().GlobalPosition,
            Reach = PickupRange,
        };

        ItemTransfer.Execute(
            ctx, ItemAddress.Decode(fromRealm, fromA, fromB), itemId,
            ItemAddress.Decode(toRealm, toA, toB), x, y, rotated);

        // Push anche quando l'operazione viene rifiutata: il rollback puo' aver comunque ricollocato
        // l'item (auto-stow), quindi il client deve sempre ripartire dallo stato autoritativo.
        PushState();
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void RequestAssignQuickSlot(int slotIndex, int instanceId)
    {
        if (!ValidateSender())
            return;
        if (Model.AssignQuickSlot(slotIndex, instanceId))
            PushState();
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void RequestUse(int instanceId)
    {
        if (!ValidateSender())
            return;

        ItemInstance? item = Model.Find(instanceId);
        if (item == null || item.Definition.Category != ItemCategory.Consumable)
            return;

        // Consuma una unita'; a zero l'item sparisce. L'effetto (cura, idratazione) e' Fase 3+.
        item.StackCount--;
        if (item.StackCount <= 0)
            Model.Extract(instanceId);

        PushState();
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void RequestUnpack(int instanceId)
    {
        if (!ValidateSender())
            return;

        ItemInstance? item = Model.Find(instanceId);
        ItemDefinition? yields = item != null && !string.IsNullOrEmpty(item.Definition.UnpackYields)
            ? _db.Get(item.Definition.UnpackYields)
            : null;
        if (item == null || yields == null)
            return;

        Model.Extract(instanceId);
        if (!Model.TryPickup(yields, item.Definition.UnpackCount))
            Model.TryStoreInstance(item); // niente spazio: il pacchetto resta chiuso

        PushState();
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void RequestState()
    {
        if (!ValidateSender())
            return;
        PushState();
    }

    // ====================================================================================
    //  Push dello stato + replica
    // ====================================================================================

    /// Versione pubblica di <see cref="PushState"/>, per i sistemi host-side che mutano l'inventario
    /// dall'esterno (es. la ricarica dell'arma, che consuma munizioni dalle griglie).
    public void HostPushState() => PushState();

    private void PushState()
    {
        GDDict data = Model.Serialize();
        if (_ownerPeerId == NetworkConstants.HostPeerId)
            SyncFullState(data); // il proprietario e' l'host stesso: aggiorna in locale
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

    private ItemDefinition? ResolveDefinition(string itemId) => _db.Get(itemId);

    private GameWorld? GetGameWorld() => GetTree().GetFirstNodeInGroup(GameWorld.GroupName) as GameWorld;
}
