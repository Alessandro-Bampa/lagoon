using System.Collections.Generic;

namespace Lagoon;

/// <summary>
/// Regole degli slot di equipaggiamento: quali categorie accetta ogni slot e quali slot un
/// oggetto indossato blocca. Valgono identiche per il giocatore e, in futuro, per i cadaveri.
/// C# puro, usato sia dall'host (validazione) sia dalla UI (evidenziazioni).
/// </summary>
public static class EquipRules
{
    /// Ordine canonico degli slot (iterazioni di ricerca, serializzazione).
    public static readonly EquipSlotType[] AllSlots =
    {
        EquipSlotType.Earpiece, EquipSlotType.Headwear, EquipSlotType.FaceCover,
        EquipSlotType.Armband, EquipSlotType.BodyArmor, EquipSlotType.Eyewear,
        EquipSlotType.OnSling, EquipSlotType.Holster, EquipSlotType.OnBack, EquipSlotType.Scabbard,
        EquipSlotType.TacticalRig, EquipSlotType.Pockets,
        EquipSlotType.Special1, EquipSlotType.Special2, EquipSlotType.Special3,
        EquipSlotType.Backpack, EquipSlotType.SecureContainer,
        EquipSlotType.Top, EquipSlotType.Legs, EquipSlotType.Footwear, EquipSlotType.Gloves,
    };

    public static bool IsSpecial(EquipSlotType slot)
        => slot is EquipSlotType.Special1 or EquipSlotType.Special2 or EquipSlotType.Special3;

    public static bool IsWeaponSlot(EquipSlotType slot)
        => slot is EquipSlotType.OnSling or EquipSlotType.OnBack or EquipSlotType.Holster;

    public static bool Accepts(EquipSlotType slot, ItemDefinition def)
    {
        ItemCategory c = def.Category;
        switch (slot)
        {
            case EquipSlotType.Earpiece: return c == ItemCategory.Earpiece;
            case EquipSlotType.Headwear: return c == ItemCategory.Headwear;
            case EquipSlotType.FaceCover: return c == ItemCategory.FaceCover;
            case EquipSlotType.Armband: return c == ItemCategory.Armband;
            case EquipSlotType.BodyArmor: return c == ItemCategory.BodyArmor;
            case EquipSlotType.Eyewear: return c == ItemCategory.Eyewear;
            case EquipSlotType.OnSling:
            case EquipSlotType.OnBack:
                if (c == ItemCategory.Pistol)
                    return InventoryConfig.Current.AllowPistolInPrimarySlots;
                return ItemCategories.IsA(c, ItemCategory.Weapon)
                    && c != ItemCategory.Melee && c != ItemCategory.Throwable;
            case EquipSlotType.Holster: return c == ItemCategory.Pistol;
            case EquipSlotType.Scabbard: return c == ItemCategory.Melee;
            case EquipSlotType.TacticalRig: return ItemCategories.IsA(c, ItemCategory.ChestRig);
            case EquipSlotType.Pockets: return c == ItemCategory.Pockets;
            case EquipSlotType.Special1:
            case EquipSlotType.Special2:
            case EquipSlotType.Special3:
                return c == ItemCategory.SpecialItem;
            case EquipSlotType.Backpack: return c == ItemCategory.Backpack;
            case EquipSlotType.SecureContainer: return c == ItemCategory.SecureContainer;
            case EquipSlotType.Top: return c == ItemCategory.Top;
            case EquipSlotType.Legs: return c == ItemCategory.Legs;
            case EquipSlotType.Footwear: return c == ItemCategory.Footwear;
            case EquipSlotType.Gloves: return c == ItemCategory.Gloves;
            default: return false;
        }
    }

    /// Slot compatibili nell'ordine di preferenza dell'equipaggiamento rapido: per una pistola la
    /// fondina prima degli slot principali, per un'arma lunga la tracolla prima della schiena.
    public static IEnumerable<EquipSlotType> SlotsFor(ItemDefinition def)
    {
        if (def.Category == ItemCategory.Pistol && Accepts(EquipSlotType.Holster, def))
            yield return EquipSlotType.Holster;

        foreach (EquipSlotType slot in AllSlots)
        {
            if (slot == EquipSlotType.Holster && def.Category == ItemCategory.Pistol)
                continue;
            if (Accepts(slot, def))
                yield return slot;
        }
    }

    public static bool IsEquippable(ItemDefinition def)
    {
        foreach (EquipSlotType _ in SlotsFor(def))
            return true;
        return false;
    }

    /// True se un oggetto di questo tipo, indossato, blocca <paramref name="slot"/>.
    public static bool Blocks(ItemDefinition def, EquipSlotType slot) => slot switch
    {
        EquipSlotType.Earpiece => def.BlocksEarpiece,
        EquipSlotType.Eyewear => def.BlocksEyewear,
        EquipSlotType.FaceCover => def.BlocksFaceCover,
        EquipSlotType.Headwear => def.BlocksHeadwear,
        EquipSlotType.BodyArmor => def.BlocksBodyArmor,
        _ => false,
    };

    /// Oggetto indossato che blocca <paramref name="slot"/> (null se libero), ignorando <paramref name="ignore"/>.
    public static ItemInstance? BlockerOf(PlayerInventoryModel model, EquipSlotType slot, ItemInstance? ignore = null)
    {
        foreach (EquipSlotType other in AllSlots)
        {
            ItemInstance? worn = model.Get(other);
            if (worn != null && !ReferenceEquals(worn, ignore) && Blocks(worn.Definition, slot))
                return worn;
        }
        return null;
    }

    /// <summary>
    /// Puo' <paramref name="item"/> andare in <paramref name="slot"/>? <paramref name="leaving"/>
    /// e' l'oggetto che nello stesso gesto lascia l'equipaggiamento (lo scambio): non occupa e
    /// non blocca. Uno slot occupato da altro restituisce <see cref="InvError.NoSpace"/>.
    /// </summary>
    public static InvError CanEquip(
        PlayerInventoryModel model, ItemInstance item, EquipSlotType slot, ItemInstance? leaving = null)
    {
        if (!Accepts(slot, item.Definition))
            return InvError.Incompatible;

        ItemInstance? occupant = model.Get(slot);
        if (occupant != null && !ReferenceEquals(occupant, item) && !ReferenceEquals(occupant, leaving))
            return InvError.NoSpace;

        // Lo slot e' bloccato da qualcosa di gia' indossato?
        foreach (EquipSlotType other in AllSlots)
        {
            ItemInstance? worn = model.Get(other);
            if (worn == null || ReferenceEquals(worn, item) || ReferenceEquals(worn, leaving))
                continue;
            if (Blocks(worn.Definition, slot))
                return InvError.SlotBlocked;
            // ...oppure e' l'oggetto nuovo a bloccare uno slot gia' occupato?
            if (Blocks(item.Definition, other))
                return InvError.SlotBlocked;
        }
        return InvError.Ok;
    }
}
