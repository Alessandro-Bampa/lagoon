using System.Collections.Generic;

namespace Lagoon;

/// Cosa succede rilasciando un item SOPRA un altro (priorita' della specifica §12.2.1).
public enum DropAction
{
    None,
    Merge,
    LoadAmmo,
    AttachMagazine,
    Insert,
}

/// <summary>
/// Regole di validazione dell'inventario, senza effetti: rispondono "si puo'?" con un
/// <see cref="InvError"/>. Sono CONDIVISE: l'host le applica prima di mutare (CLAUDE.md §3), la UI
/// del proprietario le usa per colorare l'anteprima del trascinamento. Stesso codice, quindi cio'
/// che la UI mostra verde l'host lo accetta — salvo che lo stato sia cambiato nel frattempo.
/// </summary>
public static class InventoryRules
{
    /// L'item puo' essere preso da dove si trova?
    public static InvError CanTake(InventorySpaces spaces, int space, ItemInstance item)
    {
        if (!item.Definition.Movable)
            return InvError.Fixed;

        if (space != ItemAddress.PlayerSpace)
        {
            ItemPickup? pickup = spaces.Pickup(space);
            if (pickup == null)
                return InvError.NoAccess;
            // La radice di una cassa fissa non si raccoglie: si apre.
            if (item.Parent == null && pickup.Anchored)
                return InvError.Fixed;
        }
        return InvError.Ok;
    }

    /// <summary>
    /// Puo' <paramref name="item"/> andare in <paramref name="dest"/> alla cella (x, y)?
    /// <paramref name="leaving"/> e' l'oggetto che nello stesso gesto libera il posto (scambio).
    /// L'ordine dei controlli decide quale errore si mostra: prima lo spazio, poi le regole.
    /// </summary>
    public static InvError CanPlace(
        InventorySpaces spaces, ItemInstance item, ItemAddress dest, int x, int y, bool rotated,
        ItemInstance? leaving = null)
    {
        switch (dest.Type)
        {
            case ItemAddress.Kind.Ground:
                return InvError.Ok;

            case ItemAddress.Kind.Slot when dest.Owner == 0:
                if (dest.Space != ItemAddress.PlayerSpace)
                    return InvError.NoAccess;
                return EquipRules.CanEquip(spaces.Player, item, dest.EquipSlot, leaving);

            case ItemAddress.Kind.Slot:
                return CanPlaceInItemSlot(spaces, item, dest, leaving);

            default:
                return CanPlaceInGrid(spaces, item, dest, x, y, rotated, leaving);
        }
    }

    private static InvError CanPlaceInGrid(
        InventorySpaces spaces, ItemInstance item, ItemAddress dest, int x, int y, bool rotated,
        ItemInstance? leaving)
    {
        ItemInstance? owner = spaces.Find(dest.Space, dest.Owner);
        if (owner == null || dest.Index < 0 || dest.Index >= owner.Grids.Length)
            return InvError.NoAccess;

        InventoryGrid grid = owner.Grids[dest.Index];
        int w = rotated ? item.Height : item.Width;
        int h = rotated ? item.Width : item.Height;

        if (!grid.InBounds(w, h, x, y))
            return InvError.OutOfBounds;
        if (!grid.CanPlaceSize(w, h, x, y, item.InstanceId, leaving?.InstanceId ?? 0))
            return InvError.NoSpace;

        return CanContain(owner, dest.Index, item);
    }

    /// Regole indipendenti dalla cella: auto-contenimento, filtri (anche a cascata), annidamento.
    public static InvError CanContain(ItemInstance owner, int gridIndex, ItemInstance item)
    {
        if (owner.IsInside(item))
            return InvError.SelfContainment;

        ItemFilter? filter = owner.Definition.FilterFor(gridIndex);
        if (filter != null && !filter.Accepts(item.Definition))
            return InvError.Filter;

        // Filtri a cascata: un antenato puo' pretendere che il filtro valga per tutto cio' che
        // finisce al suo interno, a qualunque profondita' (pouch sicuro).
        for (ItemInstance? ancestor = owner; ancestor != null; ancestor = ancestor.Parent)
        {
            ItemDefinition def = ancestor.Definition;
            if (!def.FilterAppliesToDescendants || def.ContentFilter == null)
                continue;
            foreach (ItemInstance inner in ItemTree.Descend(item))
                if (!def.ContentFilter.Accepts(inner.Definition))
                    return ReferenceEquals(inner, item) ? InvError.Filter : InvError.FilterNested;
        }

        return CheckNesting(owner.Definition, item);
    }

    /// Regole di annidamento fra contenitori.
    private static InvError CheckNesting(ItemDefinition parent, ItemInstance child)
    {
        ItemDefinition def = child.Definition;
        if (!def.IsContainer)
            return InvError.Ok;

        // Uno zaino o un rig entrano in uno zaino solo se vuoti.
        if (InventoryConfig.Current.NestedBagsMustBeEmpty
            && parent.Category == ItemCategory.Backpack
            && (def.Category == ItemCategory.Backpack || ItemCategories.IsA(def.Category, ItemCategory.ChestRig))
            && !child.IsEmpty)
            return InvError.Nesting;

        // Una custodia non entra in una custodia identica (annidamento infinito di casse uguali).
        if (parent.Category == ItemCategory.Container && parent.ItemId == def.ItemId)
            return InvError.Nesting;

        return InvError.Ok;
    }

    private static InvError CanPlaceInItemSlot(
        InventorySpaces spaces, ItemInstance item, ItemAddress dest, ItemInstance? leaving)
    {
        ItemInstance? owner = spaces.Find(dest.Space, dest.Owner);
        if (owner == null || dest.Index < 0 || dest.Index >= owner.SlotItems.Length)
            return InvError.NoAccess;
        if (owner.IsInside(item))
            return InvError.SelfContainment;

        ItemSlotDef slot = owner.Definition.Slots[dest.Index];
        if (slot.Kind == ItemSlotKind.Magazine)
        {
            if (item.Definition is not MagazineDefinition magazine)
                return InvError.Incompatible;
            if (owner.Definition is WeaponDefinition weapon && weapon.Caliber != magazine.Caliber)
                return InvError.Caliber;
        }
        if (slot.Filter != null && !slot.Filter.Accepts(item.Definition))
            return InvError.Incompatible;

        ItemInstance? occupant = owner.SlotItems[dest.Index];
        if (occupant != null && !ReferenceEquals(occupant, item) && !ReferenceEquals(occupant, leaving))
            return InvError.NoSpace;

        return InvError.Ok;
    }

    /// <summary>
    /// Primo posto libero e ammesso dentro <paramref name="owner"/>, provando le sue griglie
    /// nell'ordine dichiarato. Le regole non dipendono dalla cella, quindi si valutano una volta
    /// per griglia.
    /// </summary>
    public static bool TryFindSpot(
        ItemInstance owner, ItemInstance item, out int gridIndex, out int x, out int y, out bool rotated,
        ItemInstance? leaving = null)
    {
        for (int g = 0; g < owner.Grids.Length; g++)
        {
            if (CanContain(owner, g, item) != InvError.Ok)
                continue;
            if (owner.Grids[g].TryFindFreeSpot(
                    item.Width, item.Height, out x, out y, out rotated,
                    item.InstanceId, leaving?.InstanceId ?? 0))
            {
                gridIndex = g;
                return true;
            }
        }
        gridIndex = x = y = 0;
        rotated = false;
        return false;
    }

    /// Due istanze si possono impilare (stesso tipo impilabile, destinazione non piena)?
    public static bool CanMerge(ItemInstance source, ItemInstance target)
        => !ReferenceEquals(source, target)
           && source.Definition == target.Definition
           && source.Definition.IsStackable
           && target.StackCount < target.Definition.MaxStack;

    /// Caricatore su cui agisce un'azione "munizioni" fatta su <paramref name="item"/>: se' stesso
    /// se e' un caricatore, quello montato se e' un'arma.
    public static ItemInstance? MagazineOf(ItemInstance item)
        => item.Definition is MagazineDefinition ? item : item.MountedMagazine;

    /// <summary>
    /// Decide cosa fa il rilascio di <paramref name="item"/> sopra <paramref name="target"/>,
    /// secondo le priorita' della specifica: unione di stack, caricamento munizioni, montaggio del
    /// caricatore, inserimento nel contenitore. Se una regola si applica ma fallisce si passa alla
    /// successiva; <paramref name="error"/> riporta il motivo piu' specifico.
    /// </summary>
    public static DropAction ResolveDrop(
        InventorySpaces spaces, ItemInstance item, ItemInstance target, int targetSpace, out InvError error)
    {
        error = InvError.Incompatible;
        if (ReferenceEquals(item, target) || target.IsInside(item))
        {
            error = InvError.SelfContainment;
            return DropAction.None;
        }

        if (item.Definition == target.Definition && item.Definition.IsStackable)
        {
            if (CanMerge(item, target))
                return DropAction.Merge;
            error = InvError.NoSpace;
        }

        if (item.Definition is AmmoDefinition ammo)
        {
            ItemInstance? magazine = MagazineOf(target);
            if (magazine?.Definition is MagazineDefinition magDef)
            {
                if (magDef.Caliber != ammo.Caliber)
                    error = InvError.Caliber;
                else if (magazine.RoundsFree <= 0)
                    error = InvError.MagFull;
                else
                    return DropAction.LoadAmmo;
            }
        }

        int magSlot = target.MagazineSlotIndex;
        if (item.Definition is MagazineDefinition && magSlot >= 0)
        {
            // Se c'e' gia' un caricatore e' uno scambio: il vecchio va al posto del nuovo.
            InvError slotError = CanPlace(
                spaces, item, ItemAddress.SlotOf(targetSpace, target.InstanceId, magSlot), 0, 0, false,
                leaving: target.SlotItems[magSlot]);
            if (slotError == InvError.Ok)
                return DropAction.AttachMagazine;
            error = slotError;
        }

        if (target.IsContainer)
        {
            if (TryFindSpot(target, item, out _, out _, out _, out _))
                return DropAction.Insert;
            InvError containError = CanContain(target, 0, item);
            error = containError != InvError.Ok ? containError : InvError.NoSpace;
        }

        return DropAction.None;
    }

    /// <summary>
    /// Contenitori indossati in cui lo spostamento rapido e la raccolta ripongono un oggetto, per
    /// categoria. Pouch e slot speciali non sono mai destinazioni automatiche.
    /// </summary>
    public static IReadOnlyList<EquipSlotType> StoragePriority(ItemDefinition def)
    {
        ItemCategory c = def.Category;
        if (c is ItemCategory.Magazine or ItemCategory.Throwable or ItemCategory.Meds
            || ItemCategories.IsA(c, ItemCategory.Ammo))
            return RigFirst;
        if (ItemCategories.IsA(c, ItemCategory.Weapon) && c != ItemCategory.Melee)
            return BackpackOnly;
        if (c is ItemCategory.Key or ItemCategory.Info or ItemCategory.Money)
            return PocketsFirst;
        return BackpackFirst;
    }

    private static readonly EquipSlotType[] RigFirst =
        { EquipSlotType.TacticalRig, EquipSlotType.Pockets, EquipSlotType.Backpack };
    private static readonly EquipSlotType[] PocketsFirst =
        { EquipSlotType.Pockets, EquipSlotType.TacticalRig, EquipSlotType.Backpack };
    private static readonly EquipSlotType[] BackpackFirst =
        { EquipSlotType.Backpack, EquipSlotType.TacticalRig, EquipSlotType.Pockets };
    private static readonly EquipSlotType[] BackpackOnly = { EquipSlotType.Backpack };
}
