using System;
using System.Collections.Generic;
using Godot;

namespace Lagoon;

/// <summary>
/// Operazioni di inventario lato HOST. Ogni interazione dell'utente arriva qui come INTENTO
/// (CLAUDE.md §3): "sposta questo li'", "rilascia questo su quello", "riponilo dove preferisci".
/// L'host ricostruisce tutto dal proprio stato, valida con <see cref="InventoryRules"/> e SOLO
/// se la validazione passa muta il modello: niente estrazioni da annullare, quindi un'operazione
/// rifiutata non sposta nulla.
///
/// Fanno eccezione le operazioni a piu' passi (unione di stack seguita da ricerca di spazio,
/// scaricamento di un caricatore): possono riuscire in parte, e ogni stato intermedio e' valido.
///
/// Il mondo si tocca solo in <see cref="Session.Commit"/>, che va chiamata SEMPRE a fine
/// richiesta, anche in caso di errore.
/// </summary>
public static class InventoryOps
{
    /// Contesto di una richiesta: spazi visti dal giocatore + modifiche al mondo da applicare.
    public sealed class Session
    {
        public InventorySpaces Spaces { get; }
        public GameWorld World { get; }
        public ItemDatabase Db { get; }

        private readonly HashSet<int> _dirty = new();
        private readonly HashSet<int> _removed = new();
        private readonly List<ItemInstance> _toSpawn = new();

        public PlayerInventoryModel Player => Spaces.Player;

        public Session(InventorySpaces spaces, GameWorld world, ItemDatabase db)
        {
            Spaces = spaces;
            World = world;
            Db = db;
        }

        /// Il contenuto di uno spazio nel mondo e' cambiato: il suo payload andra' riscritto.
        public void Touch(int space)
        {
            if (space > 0)
                _dirty.Add(space);
        }

        /// L'oggetto nel mondo e' stato raccolto per intero.
        public void MarkRemoved(int space) => _removed.Add(space);

        public void SpawnLoose(ItemInstance item) => _toSpawn.Add(item);

        public void Commit()
        {
            foreach (int uid in _removed)
                World.DespawnPickup(uid);

            foreach (int uid in _dirty)
            {
                if (_removed.Contains(uid))
                    continue;
                ItemPickup? pickup = World.FindPickup(uid);
                if (pickup?.Root != null)
                    World.ReplacePickupPayload(uid, ItemCodec.Serialize(pickup.Root));
            }

            foreach (ItemInstance item in _toSpawn)
            {
                item.GridX = item.GridY = 0;
                item.Rotated = false;
                World.SpawnPickupFromPayload(ItemCodec.Serialize(item), Spaces.PlayerPosition);
            }

            _dirty.Clear();
            _removed.Clear();
            _toSpawn.Clear();
        }
    }

    // ====================================================================================
    //  Primitive: stacca / attacca (nessuna validazione, la fa il chiamante)
    // ====================================================================================

    private static void Detach(Session s, ItemInstance item, int space)
    {
        if (item.EquippedIn != EquipSlotType.None && space == ItemAddress.PlayerSpace)
            s.Player.ClearEquip(item.EquippedIn);
        else if (item.Parent != null)
            ItemTree.DetachFromParent(item);
        else if (space > 0)
            s.MarkRemoved(space);

        s.Touch(space);
    }

    private static void Attach(Session s, ItemInstance item, ItemAddress dest, int x, int y, bool rotated)
    {
        // Un oggetto che esce dall'inventario non puo' restare collegato a uno slot rapido.
        if (dest.Space != ItemAddress.PlayerSpace || dest.IsGround)
            s.Player.ClearQuickSlotReferences(item);

        switch (dest.Type)
        {
            case ItemAddress.Kind.Ground:
                s.SpawnLoose(item);
                return;

            case ItemAddress.Kind.Slot when dest.Owner == 0:
                s.Player.SetEquip(dest.EquipSlot, item);
                return;

            case ItemAddress.Kind.Slot:
                s.Spaces.Find(dest.Space, dest.Owner)!.SetSlot(dest.Index, item);
                break;

            default:
                s.Spaces.Find(dest.Space, dest.Owner)!.Grids[dest.Index].Place(item, x, y, rotated);
                break;
        }
        s.Touch(dest.Space);
    }

    /// Elimina un'istanza esaurita (stack a zero).
    private static void Destroy(Session s, ItemInstance item, int space)
    {
        Detach(s, item, space);
        s.Player.ClearQuickSlotReferences(item);
    }

    public static ItemInstance Create(Session s, ItemDefinition def, int stack = 1)
        => new(s.Db.AllocateInstanceId(), def) { StackCount = Math.Max(1, stack) };

    // ====================================================================================
    //  Sposta (cella precisa, slot, terreno) — con divisione e scambio
    // ====================================================================================

    /// <summary>
    /// Sposta un item verso <paramref name="dest"/>. <paramref name="x"/> negativo su una griglia =
    /// "dove c'e' posto in quel contenitore". <paramref name="amount"/> fra 1 e stack-1 divide lo
    /// stack: a destinazione nasce una nuova istanza.
    /// </summary>
    public static InvError Move(
        Session s, int srcSpace, int itemId, ItemAddress dest, int x, int y, bool rotated, int amount)
    {
        ItemInstance? item = s.Spaces.Find(srcSpace, itemId);
        if (item == null)
            return InvError.NotFound;

        InvError take = InventoryRules.CanTake(s.Spaces, srcSpace, item);
        if (take != InvError.Ok)
            return take;

        if (amount > 0 && amount < item.StackCount)
            return Split(s, item, srcSpace, dest, x, y, rotated, amount);

        if (dest.IsGround)
        {
            // Un oggetto gia' sfuso a terra resta dov'e'.
            if (srcSpace > 0 && item.Parent == null)
                return InvError.NothingToDo;
            Detach(s, item, srcSpace);
            Attach(s, item, dest, 0, 0, false);
            return InvError.Ok;
        }

        if (dest.Type == ItemAddress.Kind.Grid && x < 0)
        {
            ItemInstance? owner = s.Spaces.Find(dest.Space, dest.Owner);
            return owner == null ? InvError.NoAccess : Store(s, item, srcSpace, owner, dest.Space);
        }

        if (dest.Type == ItemAddress.Kind.Slot)
        {
            ItemInstance? occupant = dest.Owner == 0
                ? s.Player.Get(dest.EquipSlot)
                : OccupantOf(s, dest);
            if (occupant != null && !ReferenceEquals(occupant, item))
                return Swap(s, item, srcSpace, dest, occupant);
        }

        InvError place = InventoryRules.CanPlace(s.Spaces, item, dest, x, y, rotated);
        if (place != InvError.Ok)
            return place;

        Detach(s, item, srcSpace);
        Attach(s, item, dest, x, y, rotated);
        return InvError.Ok;
    }

    private static ItemInstance? OccupantOf(Session s, ItemAddress slot)
    {
        ItemInstance? owner = s.Spaces.Find(slot.Space, slot.Owner);
        return owner != null && slot.Index >= 0 && slot.Index < owner.SlotItems.Length
            ? owner.SlotItems[slot.Index]
            : null;
    }

    private static InvError Split(
        Session s, ItemInstance item, int srcSpace, ItemAddress dest, int x, int y, bool rotated, int amount)
    {
        if (dest.Type == ItemAddress.Kind.Slot)
            return InvError.Incompatible;

        ItemInstance part = Create(s, item.Definition, amount);

        if (dest.IsGround)
        {
            item.StackCount -= amount;
            s.Touch(srcSpace);
            Attach(s, part, dest, 0, 0, false);
            return InvError.Ok;
        }

        ItemInstance? owner = s.Spaces.Find(dest.Space, dest.Owner);
        if (owner == null)
            return InvError.NoAccess;

        if (x < 0)
        {
            if (!InventoryRules.TryFindSpot(owner, part, out int g, out x, out y, out rotated))
                return InvError.NoSpace;
            dest = ItemAddress.GridOf(dest.Space, owner.InstanceId, g);
        }

        InvError place = InventoryRules.CanPlace(s.Spaces, part, dest, x, y, rotated);
        if (place != InvError.Ok)
            return place;

        item.StackCount -= amount;
        s.Touch(srcSpace);
        Attach(s, part, dest, x, y, rotated);
        return InvError.Ok;
    }

    /// <summary>
    /// Scambio: <paramref name="item"/> prende lo slot, <paramref name="occupant"/> va dove stava
    /// l'item (stessa cella se ci sta, altrimenti il primo spazio di quella griglia). Atomico: si
    /// validano entrambe le destinazioni prima di toccare qualcosa.
    /// </summary>
    private static InvError Swap(Session s, ItemInstance item, int srcSpace, ItemAddress dest, ItemInstance occupant)
    {
        if (!occupant.Definition.Movable)
            return InvError.Fixed;

        InvError place = InventoryRules.CanPlace(s.Spaces, item, dest, 0, 0, false, leaving: occupant);
        if (place != InvError.Ok)
            return place;

        ItemAddress origin = InventorySpaces.AddressOf(item, srcSpace);
        int ox = item.GridX, oy = item.GridY;
        bool orot = false;

        if (origin.Type == ItemAddress.Kind.Grid)
        {
            ItemInstance owner = item.Parent!;
            InventoryGrid grid = owner.Grids[origin.Index];
            if (InventoryRules.CanContain(owner, origin.Index, occupant) != InvError.Ok)
                return InvError.SwapImpossible;

            bool fits = false;
            foreach (bool rot in new[] { false, true })
            {
                int w = rot ? occupant.Height : occupant.Width;
                int h = rot ? occupant.Width : occupant.Height;
                if (grid.CanPlaceSize(w, h, ox, oy, item.InstanceId, occupant.InstanceId))
                {
                    orot = rot;
                    fits = true;
                    break;
                }
            }
            if (!fits && !grid.TryFindFreeSpot(
                    occupant.Width, occupant.Height, out ox, out oy, out orot,
                    item.InstanceId, occupant.InstanceId))
                return InvError.SwapImpossible;
        }
        else if (origin.Type == ItemAddress.Kind.Slot)
        {
            if (InventoryRules.CanPlace(s.Spaces, occupant, origin, 0, 0, false, leaving: item) != InvError.Ok)
                return InvError.SwapImpossible;
        }
        // origin == Ground: il vecchio oggetto finisce a terra, sempre possibile.

        Detach(s, item, srcSpace);
        Detach(s, occupant, dest.Space);
        Attach(s, item, dest, 0, 0, false);
        Attach(s, occupant, origin, ox, oy, orot);
        return InvError.Ok;
    }

    // ====================================================================================
    //  Riponi in un contenitore (unione agli stack, poi primo spazio libero)
    // ====================================================================================

    /// Ripone <paramref name="item"/> dentro <paramref name="owner"/>: prima si unisce agli stack
    /// compatibili gia' presenti, poi occupa il primo spazio libero ammesso.
    public static InvError Store(Session s, ItemInstance item, int srcSpace, ItemInstance owner, int destSpace)
    {
        if (owner.IsInside(item))
            return InvError.SelfContainment;

        if (item.Definition.IsStackable)
        {
            foreach (InventoryGrid grid in owner.Grids)
            {
                // Copia: l'unione puo' eliminare la sorgente se sta in questa stessa griglia.
                foreach (ItemInstance other in new List<ItemInstance>(grid.Items))
                {
                    if (!InventoryRules.CanMerge(item, other))
                        continue;
                    MergeInto(s, item, srcSpace, other, destSpace);
                    if (item.StackCount <= 0)
                        return InvError.Ok;
                }
            }
        }

        if (!InventoryRules.TryFindSpot(owner, item, out int g, out int x, out int y, out bool rotated))
        {
            InvError rule = owner.Grids.Length > 0 ? InventoryRules.CanContain(owner, 0, item) : InvError.NoSpace;
            return rule != InvError.Ok ? rule : InvError.NoSpace;
        }

        // Gia' li': nessun movimento (evita un "riordino" involontario col quick move).
        if (ReferenceEquals(item.Parent, owner) && item.ParentGrid == g
            && item.GridX == x && item.GridY == y && item.Rotated == rotated)
            return InvError.NothingToDo;

        Detach(s, item, srcSpace);
        Attach(s, item, ItemAddress.GridOf(destSpace, owner.InstanceId, g), x, y, rotated);
        return InvError.Ok;
    }

    /// Ripone nei contenitori indossati secondo la priorita' di categoria, saltando <paramref name="skip"/>.
    private static InvError StoreInPlayer(Session s, ItemInstance item, int srcSpace, ItemInstance? skip = null)
    {
        InvError last = InvError.NoSpace;
        foreach (EquipSlotType slot in InventoryRules.StoragePriority(item.Definition))
        {
            ItemInstance? container = s.Player.Get(slot);
            if (container == null || ReferenceEquals(container, skip) || container.IsInside(item))
                continue;

            InvError result = Store(s, item, srcSpace, container, ItemAddress.PlayerSpace);
            if (result == InvError.Ok)
                return InvError.Ok;
            if (result != InvError.NothingToDo)
                last = result;
        }
        return last;
    }

    private static void MergeInto(Session s, ItemInstance source, int srcSpace, ItemInstance target, int targetSpace)
    {
        int moved = Math.Min(source.StackCount, target.Definition.MaxStack - target.StackCount);
        if (moved <= 0)
            return;

        target.StackCount += moved;
        source.StackCount -= moved;
        s.Touch(targetSpace);
        s.Touch(srcSpace);
        if (source.StackCount <= 0)
            Destroy(s, source, srcSpace);
    }

    // ====================================================================================
    //  Rilascio di un item sopra un altro
    // ====================================================================================

    public static InvError DropOnItem(Session s, int srcSpace, int itemId, int targetSpace, int targetId)
    {
        ItemInstance? item = s.Spaces.Find(srcSpace, itemId);
        ItemInstance? target = s.Spaces.Find(targetSpace, targetId);
        if (item == null || target == null)
            return InvError.NotFound;

        InvError take = InventoryRules.CanTake(s.Spaces, srcSpace, item);
        if (take != InvError.Ok)
            return take;

        switch (InventoryRules.ResolveDrop(s.Spaces, item, target, targetSpace, out InvError error))
        {
            case DropAction.Merge:
                MergeInto(s, item, srcSpace, target, targetSpace);
                return InvError.Ok;

            case DropAction.LoadAmmo:
                LoadRounds(s, InventoryRules.MagazineOf(target)!, targetSpace, item, srcSpace);
                return InvError.Ok;

            case DropAction.AttachMagazine:
                return Move(
                    s, srcSpace, itemId,
                    ItemAddress.SlotOf(targetSpace, target.InstanceId, target.MagazineSlotIndex),
                    0, 0, false, 0);

            case DropAction.Insert:
                return Store(s, item, srcSpace, target, targetSpace);

            default:
                return error;
        }
    }

    // ====================================================================================
    //  Spostamento ed equipaggiamento rapidi, raccolta
    // ====================================================================================

    /// <summary>
    /// Ctrl+click. Con un contenitore aperto a destra (<paramref name="sideBUid"/>) l'oggetto
    /// passa da un lato all'altro; senza, va nel contenitore indossato successivo nella priorita'
    /// della sua categoria.
    /// </summary>
    public static InvError QuickMove(Session s, int srcSpace, int itemId, int sideBUid)
    {
        ItemInstance? item = s.Spaces.Find(srcSpace, itemId);
        if (item == null)
            return InvError.NotFound;

        InvError take = InventoryRules.CanTake(s.Spaces, srcSpace, item);
        if (take != InvError.Ok)
            return take;

        if (srcSpace != ItemAddress.PlayerSpace)
            return StoreInPlayer(s, item, srcSpace);

        if (sideBUid > 0)
        {
            ItemInstance? root = s.Spaces.WorldRoot(sideBUid);
            if (root == null)
                return InvError.NoAccess;
            return Store(s, item, srcSpace, root, sideBUid);
        }

        // Nessun lato B: contenitore successivo, saltando quello in cui si trova gia'.
        ItemInstance? current = item.Parent != null && item.ParentGrid >= 0 ? item.Root : null;
        return StoreInPlayer(s, item, srcSpace, skip: current);
    }

    /// Alt+click: primo slot compatibile libero e non bloccato; altrimenti scambio col primo compatibile.
    public static InvError QuickEquip(Session s, int srcSpace, int itemId)
    {
        ItemInstance? item = s.Spaces.Find(srcSpace, itemId);
        if (item == null)
            return InvError.NotFound;
        if (srcSpace == ItemAddress.PlayerSpace && item.EquippedIn != EquipSlotType.None)
            return InvError.NothingToDo;

        InvError last = InvError.CannotEquip;
        foreach (EquipSlotType slot in EquipRules.SlotsFor(item.Definition))
        {
            if (s.Player.Get(slot) != null)
                continue;
            InvError result = Move(s, srcSpace, itemId, ItemAddress.Equip(slot), 0, 0, false, 0);
            if (result == InvError.Ok)
                return InvError.Ok;
            last = result;
        }

        foreach (EquipSlotType slot in EquipRules.SlotsFor(item.Definition))
        {
            if (s.Player.Get(slot) == null)
                continue;
            return Move(s, srcSpace, itemId, ItemAddress.Equip(slot), 0, 0, false, 0);
        }
        return last;
    }

    /// <summary>
    /// Raccolta con F di un oggetto sfuso: si indossa se c'e' uno slot compatibile libero (zaino,
    /// rig, arma), altrimenti si ripone secondo la priorita' di categoria.
    /// </summary>
    public static InvError Pickup(Session s, int worldUid)
    {
        ItemInstance? root = s.Spaces.WorldRoot(worldUid);
        if (root == null)
            return InvError.NoAccess;

        InvError take = InventoryRules.CanTake(s.Spaces, worldUid, root);
        if (take != InvError.Ok)
            return take;

        return Acquire(s, root, worldUid);
    }

    /// Fa entrare nell'inventario un'istanza (dal mondo o appena creata): indossa o ripone.
    public static InvError Acquire(Session s, ItemInstance item, int srcSpace)
    {
        foreach (EquipSlotType slot in EquipRules.SlotsFor(item.Definition))
        {
            if (s.Player.Get(slot) != null
                || EquipRules.CanEquip(s.Player, item, slot) != InvError.Ok)
                continue;
            Detach(s, item, srcSpace);
            Attach(s, item, ItemAddress.Equip(slot), 0, 0, false);
            return InvError.Ok;
        }
        return StoreInPlayer(s, item, srcSpace);
    }

    // ====================================================================================
    //  Caricatori e munizioni
    // ====================================================================================

    private static int LoadRounds(Session s, ItemInstance magazine, int magSpace, ItemInstance ammo, int ammoSpace)
    {
        int count = Math.Min(magazine.RoundsFree, ammo.StackCount);
        if (count <= 0 || ammo.Definition is not AmmoDefinition def)
            return 0;

        magazine.PushRounds(def, count);
        ammo.StackCount -= count;
        s.Touch(magSpace);
        s.Touch(ammoSpace);
        if (ammo.StackCount <= 0)
            Destroy(s, ammo, ammoSpace);
        return count;
    }

    /// Menu "Carica munizioni": riempie il caricatore attingendo da tutti gli stack del tipo scelto
    /// presenti nel proprio inventario.
    public static InvError LoadAmmo(Session s, int space, int targetId, string ammoItemId)
    {
        ItemInstance? target = s.Spaces.Find(space, targetId);
        ItemInstance? magazine = target != null ? InventoryRules.MagazineOf(target) : null;
        if (magazine?.Definition is not MagazineDefinition magDef)
            return InvError.NotFound;
        if (magazine.RoundsFree <= 0)
            return InvError.MagFull;

        var sources = new List<ItemInstance>();
        foreach (ItemInstance candidate in s.Player.AllItems())
            if (candidate.Definition is AmmoDefinition ammo && ammo.ItemId == ammoItemId)
                sources.Add(candidate);

        int loaded = 0;
        foreach (ItemInstance source in sources)
        {
            if (((AmmoDefinition)source.Definition).Caliber != magDef.Caliber)
                return InvError.Caliber;
            loaded += LoadRounds(s, magazine, space, source, ItemAddress.PlayerSpace);
            if (magazine.RoundsFree <= 0)
                break;
        }
        return loaded > 0 ? InvError.Ok : InvError.NothingToDo;
    }

    /// <summary>
    /// "Scarica munizioni": le cartucce escono dalla cima (LIFO), si uniscono agli stack del
    /// contenitore che ospita il caricatore, poi occupano spazio libero li' e infine negli altri
    /// contenitori indossati. Finito lo spazio lo scaricamento si ferma: il resto rimane dentro.
    /// </summary>
    public static InvError UnloadAmmo(Session s, int space, int targetId)
    {
        ItemInstance? target = s.Spaces.Find(space, targetId);
        ItemInstance? magazine = target != null ? InventoryRules.MagazineOf(target) : null;
        if (magazine == null || magazine.Rounds.Count == 0)
            return InvError.NothingToDo;

        // Contenitori candidati, in ordine: quello "di casa", poi quelli indossati.
        var homes = new List<(ItemInstance Owner, int Space)>();
        ItemInstance? holder = magazine.ParentGrid >= 0 ? magazine.Parent : magazine.Parent?.Parent;
        if (holder != null && holder.IsContainer)
            homes.Add((holder, space));
        foreach (EquipSlotType slot in InventoryRules.StoragePriority(magazine.Rounds[^1].Ammo))
        {
            ItemInstance? container = s.Player.Get(slot);
            if (container != null && !ReferenceEquals(container, holder))
                homes.Add((container, ItemAddress.PlayerSpace));
        }

        bool any = false;
        while (magazine.Rounds.Count > 0)
        {
            AmmoStack top = magazine.Rounds[^1];
            int moved = PlaceLooseRounds(s, top.Ammo, top.Count, homes);
            if (moved > 0)
            {
                any = true;
                top.Count -= moved;
                s.Touch(space);
            }
            if (top.Count > 0)
                break; // spazio esaurito
            magazine.Rounds.RemoveAt(magazine.Rounds.Count - 1);
        }
        return any ? InvError.Ok : InvError.NoSpace;
    }

    /// Distribuisce fino a <paramref name="count"/> cartucce sfuse nei contenitori dati; ritorna quante.
    private static int PlaceLooseRounds(
        Session s, AmmoDefinition ammo, int count, List<(ItemInstance Owner, int Space)> homes)
    {
        int remaining = count;

        foreach (var (owner, space) in homes)
        {
            foreach (InventoryGrid grid in owner.Grids)
            {
                foreach (ItemInstance other in grid.Items)
                {
                    if (remaining <= 0)
                        return count;
                    if (other.Definition != ammo || other.StackCount >= ammo.MaxStack)
                        continue;
                    int add = Math.Min(remaining, ammo.MaxStack - other.StackCount);
                    other.StackCount += add;
                    remaining -= add;
                    s.Touch(space);
                }
            }
        }

        foreach (var (owner, space) in homes)
        {
            while (remaining > 0)
            {
                ItemInstance stack = Create(s, ammo, Math.Min(remaining, ammo.MaxStack));
                if (!InventoryRules.TryFindSpot(owner, stack, out int g, out int x, out int y, out bool rotated))
                    break;
                Attach(s, stack, ItemAddress.GridOf(space, owner.InstanceId, g), x, y, rotated);
                remaining -= stack.StackCount;
            }
        }

        return count - remaining;
    }

    // ====================================================================================
    //  Azioni sull'oggetto
    // ====================================================================================

    /// "Apri" su un pacchetto sigillato: diventa stack del contenuto, di preferenza dove stava.
    public static InvError Unpack(Session s, int itemId)
    {
        ItemInstance? box = s.Player.Find(itemId);
        ItemDefinition? yields = box != null && box.Definition.UnpackYields.Length > 0
            ? s.Db.Get(box.Definition.UnpackYields)
            : null;
        if (box == null || yields == null || box.Parent == null || box.ParentGrid < 0)
            return InvError.NotFound;

        ItemInstance owner = box.Parent;
        InventoryGrid grid = owner.Grids[box.ParentGrid];
        int bx = box.GridX, by = box.GridY;
        bool brot = box.Rotated;

        grid.Remove(box);

        var created = new List<ItemInstance>();
        int remaining = box.Definition.UnpackCount;
        bool failed = false;
        while (remaining > 0)
        {
            ItemInstance stack = Create(s, yields, Math.Min(remaining, yields.MaxStack));
            bool placed = false;
            if (InventoryRules.TryFindSpot(owner, stack, out int g, out int x, out int y, out bool rotated))
                placed = owner.Grids[g].Place(stack, x, y, rotated);

            if (!placed)
            {
                foreach (EquipSlotType slot in InventoryRules.StoragePriority(yields))
                {
                    ItemInstance? container = s.Player.Get(slot);
                    if (container == null || ReferenceEquals(container, owner))
                        continue;
                    if (InventoryRules.TryFindSpot(container, stack, out g, out x, out y, out rotated)
                        && container.Grids[g].Place(stack, x, y, rotated))
                    {
                        placed = true;
                        break;
                    }
                }
            }

            if (!placed)
            {
                failed = true;
                break;
            }
            created.Add(stack);
            remaining -= stack.StackCount;
        }

        if (failed)
        {
            // Tutto o niente: il pacchetto resta chiuso al suo posto.
            foreach (ItemInstance stack in created)
                ItemTree.DetachFromParent(stack);
            grid.Place(box, bx, by, brot);
            return InvError.NoSpace;
        }

        s.Player.ClearQuickSlotReferences(box);
        return InvError.Ok;
    }

    /// Consuma una unita' di un consumabile. L'effetto sul personaggio e' un gancio per il futuro.
    public static InvError Use(Session s, int itemId)
    {
        ItemInstance? item = s.Player.Find(itemId);
        if (item == null || !ItemCategories.IsA(item.Definition.Category, ItemCategory.Consumable))
            return InvError.NotFound;

        item.StackCount--;
        if (item.StackCount <= 0)
            Destroy(s, item, ItemAddress.PlayerSpace);
        return InvError.Ok;
    }

    public static InvError AssignQuickSlot(Session s, int slotIndex, int itemId)
    {
        if (slotIndex < 0 || slotIndex >= PlayerInventoryModel.QuickSlotCount)
            return InvError.NotFound;
        if (itemId == 0)
        {
            s.Player.SetQuickSlot(slotIndex, 0);
            return InvError.Ok;
        }

        ItemInstance? item = s.Player.Find(itemId);
        if (item == null)
            return InvError.NotFound;
        if (!item.Definition.QuickUsable)
            return InvError.Incompatible;

        s.Player.SetQuickSlot(slotIndex, itemId);
        return InvError.Ok;
    }

    public static InvError SetTag(Session s, int space, int itemId, string name, int color)
    {
        ItemInstance? item = s.Spaces.Find(space, itemId);
        if (item == null || !item.IsContainer)
            return InvError.NotFound;

        name = name.Trim();
        int max = InventoryConfig.Current.TagMaxLength;
        item.TagName = name.Length > max ? name[..max] : name;
        item.TagColor = Math.Max(0, color);
        s.Touch(space);
        return InvError.Ok;
    }

    // ====================================================================================
    //  Ordinamento automatico di una griglia
    // ====================================================================================

    /// <summary>
    /// Ordina una griglia: unisce gli stack, poi reinserisce per categoria, area e altezza.
    /// Deterministico e idempotente; se un oggetto non trova posto si ripristina la disposizione
    /// di partenza.
    /// </summary>
    public static InvError Sort(Session s, ItemAddress address)
    {
        ItemInstance? owner = s.Spaces.Find(address.Space, address.Owner);
        if (owner == null || address.Type != ItemAddress.Kind.Grid
            || address.Index < 0 || address.Index >= owner.Grids.Length)
            return InvError.NoAccess;

        InventoryGrid grid = owner.Grids[address.Index];

        // 1) Unione degli stack non pieni (sempre valida, non richiede ripristino).
        var stackables = new List<ItemInstance>(grid.Items);
        foreach (ItemInstance source in stackables)
        {
            if (source.StackCount <= 0 || !source.Definition.IsStackable)
                continue;
            foreach (ItemInstance target in stackables)
            {
                if (target.StackCount <= 0 || !InventoryRules.CanMerge(source, target))
                    continue;
                MergeInto(s, source, address.Space, target, address.Space);
                if (source.StackCount <= 0)
                    break;
            }
        }

        // 2) Fotografia, svuotamento, reinserimento in ordine.
        var items = new List<ItemInstance>(grid.Items);
        var snapshot = new List<(ItemInstance Item, int X, int Y, bool Rot)>();
        foreach (ItemInstance item in items)
            snapshot.Add((item, item.GridX, item.GridY, item.Rotated));

        items.Sort(CompareForSort);
        foreach (ItemInstance item in items)
            grid.Remove(item);

        bool ok = true;
        foreach (ItemInstance item in items)
        {
            if (!grid.TryAutoPlace(item))
            {
                ok = false;
                break;
            }
        }

        if (!ok)
        {
            foreach (ItemInstance item in items)
                grid.Remove(item);
            foreach (var (item, x, y, rot) in snapshot)
                grid.Place(item, x, y, rot);
            return InvError.NoSpace;
        }

        s.Touch(address.Space);
        return InvError.Ok;
    }

    private static int CompareForSort(ItemInstance a, ItemInstance b)
    {
        int byCategory = ItemCategories.SortOrder(a.Definition.Category)
            .CompareTo(ItemCategories.SortOrder(b.Definition.Category));
        if (byCategory != 0) return byCategory;

        int byArea = (b.Width * b.Height).CompareTo(a.Width * a.Height);
        if (byArea != 0) return byArea;

        int byHeight = b.Height.CompareTo(a.Height);
        if (byHeight != 0) return byHeight;

        int byId = string.CompareOrdinal(a.Definition.ItemId, b.Definition.ItemId);
        return byId != 0 ? byId : a.InstanceId.CompareTo(b.InstanceId);
    }
}
