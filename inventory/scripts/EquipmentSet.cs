using System.Collections.Generic;

namespace Lagoon;

/// <summary>
/// Insieme degli slot di equipaggiamento del giocatore (testa, torso, gambe, piedi, gilet, zaino +
/// slot arma riservati alla Fase 3). Un item entra in uno slot solo se la sua
/// <see cref="ItemDefinition.EquipSlot"/> combacia. C# puro, come il resto del modello.
/// </summary>
public sealed class EquipmentSet
{
    /// Ordine canonico degli slot (usato dalla UI e dalle iterazioni di ricerca).
    public static readonly EquipSlotType[] AllSlots =
    {
        EquipSlotType.Head,
        EquipSlotType.Torso,
        EquipSlotType.Legs,
        EquipSlotType.Feet,
        EquipSlotType.Vest,
        EquipSlotType.Backpack,
        EquipSlotType.SecureContainer,
        EquipSlotType.WeaponPrimary,
        EquipSlotType.WeaponSecondary,
        EquipSlotType.Sidearm,
    };

    private readonly Dictionary<EquipSlotType, ItemInstance> _slots = new();

    public ItemInstance? Get(EquipSlotType slot) => _slots.GetValueOrDefault(slot);

    public bool IsEmpty(EquipSlotType slot) => !_slots.ContainsKey(slot);

    /// Equipaggia l'item nello slot. Fallisce se il tipo di slot non combacia o lo slot e' occupato.
    public bool Equip(EquipSlotType slot, ItemInstance item)
    {
        if (item.Definition.EquipSlot != slot)
            return false;
        if (_slots.ContainsKey(slot))
            return false;

        _slots[slot] = item;
        return true;
    }

    /// Toglie e restituisce l'item nello slot (null se vuoto).
    public ItemInstance? Unequip(EquipSlotType slot)
    {
        if (_slots.Remove(slot, out ItemInstance? item))
            return item;
        return null;
    }

    public IEnumerable<KeyValuePair<EquipSlotType, ItemInstance>> All() => _slots;
}
