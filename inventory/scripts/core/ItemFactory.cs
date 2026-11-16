using System;

namespace Lagoon;

/// <summary>
/// Creazione di istanze lato HOST (kit iniziale, popolamento del mondo). Gli id arrivano
/// dall'<see cref="ItemDatabase"/>, unico allocatore della sessione.
/// </summary>
public static class ItemFactory
{
    public static ItemInstance? Create(ItemDatabase db, string itemId, int stack = 1)
    {
        ItemDefinition? def = db.Get(itemId);
        if (def == null)
        {
            Godot.GD.PrintErr($"[ItemFactory] ItemId sconosciuto: '{itemId}'");
            return null;
        }
        return new ItemInstance(db.AllocateInstanceId(), def)
        {
            StackCount = Math.Clamp(stack, 1, Math.Max(1, def.MaxStack)),
        };
    }

    /// Caricatore con <paramref name="rounds"/> cartucce (limitate dalla capienza e dal calibro).
    public static ItemInstance? CreateMagazine(ItemDatabase db, string magazineId, string ammoId, int rounds)
    {
        ItemInstance? magazine = Create(db, magazineId);
        if (magazine?.Definition is MagazineDefinition magDef
            && db.Get(ammoId) is AmmoDefinition ammo && ammo.Caliber == magDef.Caliber)
            magazine.PushRounds(ammo, Math.Min(rounds, magDef.Capacity));
        return magazine;
    }

    /// Arma con un caricatore pieno gia' montato.
    public static ItemInstance? CreateWeapon(ItemDatabase db, string weaponId, string magazineId, string ammoId)
    {
        ItemInstance? weapon = Create(db, weaponId);
        if (weapon == null)
            return null;

        int slot = weapon.MagazineSlotIndex;
        ItemInstance? magazine = slot >= 0 ? CreateMagazine(db, magazineId, ammoId, int.MaxValue) : null;
        if (magazine != null)
            weapon.SetSlot(slot, magazine);
        return weapon;
    }
}
