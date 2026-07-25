using System;
using System.Collections.Generic;
using Godot;

namespace Lagoon;

/// <summary>
/// Motore di spostamento item lato HOST: prende un <see cref="ItemAddress"/> di partenza e uno di
/// arrivo ed esegue l'operazione, validando tutto (spazio, carico, tipo di slot, anti-ciclo).
///
/// E' il cuore del refactor: trascinamento, quick move (Ctrl), quick equip (Alt), scarto rapido
/// (Delete) e le voci del menu contestuale sono TUTTE la stessa operazione con indirizzi diversi,
/// quindi esiste una sola RPC e un solo punto in cui le regole vengono applicate.
///
/// Gli item nel mondo vivono come payload serializzati dei world item: qui vengono deserializzati
/// una sola volta per uid (<see cref="WorldSession"/>), modificati in memoria e ricommittati alla
/// fine, cosi' anche spostare roba DENTRO lo stesso contenitore funziona senza conflitti.
/// </summary>
public static class ItemTransfer
{
    /// Contesto host necessario a risolvere gli indirizzi.
    public sealed class Context
    {
        public required PlayerInventoryModel Model { get; init; }
        public required GameWorld World { get; init; }
        public required Func<string, ItemDefinition?> Resolve { get; init; }

        /// Posizione del giocatore: usata per il drop a terra e per validare la distanza.
        public required Vector3 PlayerPosition { get; init; }

        /// Raggio entro cui il giocatore puo' interagire con i contenitori nel mondo.
        public required float Reach { get; init; }
    }

    /// <summary>
    /// Esegue lo spostamento. <paramref name="x"/> negativo = "auto-piazza nel primo spazio libero"
    /// (usato da quick move, equip rapido e raccolta col tasto F).
    /// Ritorna true se qualcosa e' effettivamente cambiato (il chiamante fa il push dello stato).
    /// </summary>
    public static bool Execute(
        Context ctx, ItemAddress from, int itemId, ItemAddress to, int x, int y, bool rotated)
    {
        var session = new WorldSession(ctx);

        // 1) Estrazione dalla sorgente (non ancora committata: si puo' ancora annullare).
        ItemInstance? item = Extract(ctx, session, from, itemId, out Action? rollback);
        if (item == null)
            return false;

        // 2) Adattamento degli id se l'item cambia "spazio" (inventario <-> mondo, o payload diverso).
        ItemInstance? moving = Rehome(ctx, item, from, to, session);
        if (moving == null)
        {
            rollback?.Invoke();
            return false;
        }

        // 3) Inserimento nella destinazione.
        if (!Insert(ctx, session, to, moving, x, y, rotated))
        {
            rollback?.Invoke();
            return false;
        }

        session.Commit();
        return true;
    }

    // ====================================================================================
    //  Estrazione
    // ====================================================================================

    private static ItemInstance? Extract(
        Context ctx, WorldSession session, ItemAddress from, int itemId, out Action? rollback)
    {
        rollback = null;

        switch (from.Realm)
        {
            case ItemAddress.RealmType.PlayerGrid:
            case ItemAddress.RealmType.PlayerEquip:
            {
                ItemInstance? item = ctx.Model.Extract(itemId);
                if (item == null)
                    return null;

                // Rollback: rimettilo dove capita (l'item non va perso se la destinazione rifiuta).
                ItemInstance captured = item;
                rollback = () => ctx.Model.TryStoreInstance(captured);
                return item;
            }

            case ItemAddress.RealmType.WorldLoose:
            {
                // Come sorgente, WorldLoose indica l'INTERO world item con quell'uid (A = uid).
                ItemPickup? pickup = session.Reachable(from.WorldItemUid);
                if (pickup == null || pickup.Anchored)
                    return null; // le casse non si raccolgono, si aprono

                ItemInstance? root = session.Root(from.WorldItemUid);
                if (root == null)
                    return null;

                session.MarkRemoved(from.WorldItemUid);
                return root;
            }

            case ItemAddress.RealmType.WorldContainer:
            {
                ItemInstance? root = session.Root(from.WorldItemUid);
                if (root == null)
                    return null;

                ItemInstance? taken = ItemTree.Extract(root, itemId);
                if (taken == null)
                    return null;

                session.MarkDirty(from.WorldItemUid);

                // Rollback: rimettilo nel proprio albero (auto-piazzato: la cella esatta e' persa,
                // ma l'oggetto non sparisce).
                InventoryGrid? grid = ResolveWorldGrid(root, from.ContainerInstanceId);
                ItemInstance capturedItem = taken;
                if (grid != null)
                    rollback = () => grid.TryAutoPlace(capturedItem);
                return taken;
            }

            default:
                return null;
        }
    }

    // ====================================================================================
    //  Adattamento degli id fra "spazi" diversi
    // ====================================================================================

    /// <summary>
    /// Gli InstanceId devono essere unici dentro l'albero che ospita l'item. Se sorgente e
    /// destinazione sono lo stesso spazio (stesso inventario o stesso payload) l'istanza si
    /// riusa com'e'; altrimenti la si ricostruisce assegnando id validi nella destinazione.
    /// </summary>
    private static ItemInstance? Rehome(
        Context ctx, ItemInstance item, ItemAddress from, ItemAddress to, WorldSession session)
    {
        string fromSpace = SpaceKey(from);
        string toSpace = SpaceKey(to);
        if (fromSpace == toSpace)
            return item;

        Godot.Collections.Dictionary data = PlayerInventoryModel.SerializeItem(item);

        // Verso l'inventario: id freschi dal modello del giocatore.
        if (to.IsPlayer)
            return ctx.Model.DeserializeItem(data, ctx.Resolve, allocateFreshIds: true);

        // Verso un contenitore nel mondo: id unici dentro QUEL payload.
        if (to.Realm == ItemAddress.RealmType.WorldContainer)
        {
            ItemInstance? root = session.Root(to.WorldItemUid);
            if (root == null)
                return null;
            int next = ItemTree.MaxId(root);
            return PlayerInventoryModel.DeserializeItemWith(data, ctx.Resolve, () => ++next);
        }

        // Verso il terreno: diventa il payload di un nuovo world item, gli id sono gia' coerenti.
        return item;
    }

    /// Chiave dello "spazio" che ospita gli id: l'inventario del giocatore o un preciso payload.
    private static string SpaceKey(ItemAddress address) => address.Realm switch
    {
        ItemAddress.RealmType.PlayerGrid or ItemAddress.RealmType.PlayerEquip => "player",
        ItemAddress.RealmType.WorldContainer => $"world:{address.WorldItemUid}",
        _ => "loose",
    };

    // ====================================================================================
    //  Inserimento
    // ====================================================================================

    private static bool Insert(
        Context ctx, WorldSession session, ItemAddress to, ItemInstance item, int x, int y, bool rotated)
    {
        switch (to.Realm)
        {
            case ItemAddress.RealmType.PlayerGrid:
            {
                if (!ctx.Model.FitsLoad(item))
                    return false;

                InventoryGrid? grid = ctx.Model.GridFor(to.ContainerId);
                if (grid == null || PlayerInventoryModel.WouldCreateCycle(item, grid))
                    return false;

                return x < 0 ? grid.TryAutoPlace(item) : grid.Place(item, x, y, rotated);
            }

            case ItemAddress.RealmType.PlayerEquip:
            {
                if (!ctx.Model.FitsLoad(item))
                    return false;
                return ctx.Model.EquipInstance(item, to.Slot);
            }

            case ItemAddress.RealmType.WorldContainer:
            {
                if (session.Reachable(to.WorldItemUid) == null)
                    return false;

                ItemInstance? root = session.Root(to.WorldItemUid);
                InventoryGrid? grid = root != null ? ResolveWorldGrid(root, to.ContainerInstanceId) : null;
                if (grid == null || PlayerInventoryModel.WouldCreateCycle(item, grid))
                    return false;

                bool placed = x < 0 ? grid.TryAutoPlace(item) : grid.Place(item, x, y, rotated);
                if (placed)
                    session.MarkDirty(to.WorldItemUid);
                return placed;
            }

            case ItemAddress.RealmType.WorldLoose:
                session.SpawnLoose(item);
                return true;

            default:
                return false;
        }
    }

    /// Griglia di destinazione dentro il payload: 0 = l'item radice stesso, altrimenti un annidato.
    private static InventoryGrid? ResolveWorldGrid(ItemInstance root, int containerInstanceId)
        => containerInstanceId == 0 ? root.ContainerGrid : ItemTree.FindGrid(root, containerInstanceId);

    // ====================================================================================
    //  Sessione di modifica del mondo
    // ====================================================================================

    /// <summary>
    /// Tiene in memoria gli alberi dei world item toccati dall'operazione, cosi' sorgente e
    /// destinazione condividono la stessa istanza quando sono lo stesso contenitore, e la
    /// riscrittura dei payload avviene una sola volta alla fine.
    /// </summary>
    private sealed class WorldSession
    {
        private readonly Context _ctx;
        private readonly Dictionary<int, ItemInstance> _roots = new();
        private readonly HashSet<int> _dirty = new();
        private readonly HashSet<int> _removed = new();
        private readonly List<ItemInstance> _toSpawn = new();

        public WorldSession(Context ctx) => _ctx = ctx;

        /// Il world item, se esiste ed e' abbastanza vicino al giocatore (validazione §3.4).
        public ItemPickup? Reachable(int uid)
        {
            ItemPickup? pickup = _ctx.World.FindPickup(uid);
            if (pickup == null)
                return null;
            return _ctx.PlayerPosition.DistanceTo(pickup.GlobalPosition) <= _ctx.Reach ? pickup : null;
        }

        /// Albero deserializzato del payload (una sola volta per uid).
        public ItemInstance? Root(int uid)
        {
            if (_roots.TryGetValue(uid, out ItemInstance? cached))
                return cached;

            ItemPickup? pickup = Reachable(uid);
            if (pickup == null)
                return null;

            ItemInstance? root = PlayerInventoryModel.DeserializeItemWith(pickup.Payload, _ctx.Resolve, null);
            if (root == null)
                return null;

            _roots[uid] = root;
            return root;
        }

        public void MarkDirty(int uid) => _dirty.Add(uid);

        /// Il world item e' stato raccolto per intero: va rimosso dal mondo.
        public void MarkRemoved(int uid) => _removed.Add(uid);

        public void SpawnLoose(ItemInstance item) => _toSpawn.Add(item);

        public void Commit()
        {
            foreach (int uid in _removed)
                _ctx.World.DespawnPickup(uid);

            foreach (int uid in _dirty)
            {
                if (_removed.Contains(uid) || !_roots.TryGetValue(uid, out ItemInstance? root))
                    continue;
                _ctx.World.ReplacePickupPayload(uid, PlayerInventoryModel.SerializeItem(root));
            }

            foreach (ItemInstance item in _toSpawn)
                _ctx.World.SpawnPickupFromPayload(
                    PlayerInventoryModel.SerializeItem(item), _ctx.PlayerPosition);
        }
    }
}
