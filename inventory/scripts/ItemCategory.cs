namespace Lagoon;

/// <summary>
/// Categoria di un item. Le categorie formano un ALBERO (<see cref="ItemCategories.ParentOf"/>):
/// un filtro su <see cref="Weapon"/> accetta anche <see cref="Pistol"/> e <see cref="Rifle"/>.
/// Dalla categoria discendono gli slot di equipaggiamento ammessi (<see cref="EquipRules"/>),
/// le priorita' dello spostamento rapido e il colore di sfondo della tile.
///
/// ATTENZIONE: i valori interi sono salvati nei <c>.tres</c> e nei filtri. Si appende in coda,
/// non si riordina mai.
/// </summary>
public enum ItemCategory
{
    Generic = 0,
    Equipment = 1,
    Container = 2,
    Weapon = 3,
    Consumable = 4,

    Pistol = 5,
    Rifle = 6,
    Melee = 7,
    Throwable = 8,
    WeaponMod = 9,
    Magazine = 10,
    Ammo = 11,
    AmmoBox = 12,
    Headwear = 13,
    Earpiece = 14,
    FaceCover = 15,
    Eyewear = 16,
    Armband = 17,
    BodyArmor = 18,
    ChestRig = 19,
    ArmoredRig = 20,
    Backpack = 21,
    SecureContainer = 22,
    Pockets = 23,
    Meds = 24,
    Food = 25,
    Key = 26,
    Money = 27,
    Barter = 28,
    Info = 29,
    SpecialItem = 30,
}

/// Albero delle categorie e ordinamento. Dato statico di progetto, identico su ogni peer.
public static class ItemCategories
{
    /// Genitore nell'albero; <see cref="ItemCategory.Generic"/> e' la radice (genitore di se' stessa).
    public static ItemCategory ParentOf(ItemCategory category) => category switch
    {
        ItemCategory.Pistol or ItemCategory.Rifle or ItemCategory.Melee or ItemCategory.Throwable
            => ItemCategory.Weapon,
        ItemCategory.Magazine => ItemCategory.WeaponMod,
        ItemCategory.AmmoBox => ItemCategory.Ammo,
        ItemCategory.Headwear or ItemCategory.Earpiece or ItemCategory.FaceCover or ItemCategory.Eyewear
            or ItemCategory.Armband or ItemCategory.BodyArmor or ItemCategory.ChestRig
            or ItemCategory.Backpack or ItemCategory.SecureContainer or ItemCategory.Pockets
            => ItemCategory.Equipment,
        ItemCategory.ArmoredRig => ItemCategory.ChestRig,
        ItemCategory.Meds or ItemCategory.Food => ItemCategory.Consumable,
        _ => ItemCategory.Generic,
    };

    /// True se <paramref name="category"/> e' <paramref name="ancestor"/> o una sua discendente.
    public static bool IsA(ItemCategory category, ItemCategory ancestor)
    {
        if (ancestor == ItemCategory.Generic)
            return true;
        for (ItemCategory c = category; ; c = ParentOf(c))
        {
            if (c == ancestor)
                return true;
            if (c == ItemCategory.Generic)
                return false;
        }
    }

    /// Ordine usato dall'ordinamento automatico: armi, equipaggiamento, munizioni, cure, il resto.
    public static int SortOrder(ItemCategory category)
    {
        if (IsA(category, ItemCategory.Weapon)) return 0;
        if (IsA(category, ItemCategory.WeaponMod)) return 1;
        if (IsA(category, ItemCategory.Equipment)) return 2;
        if (IsA(category, ItemCategory.Container)) return 3;
        if (IsA(category, ItemCategory.Ammo)) return 4;
        if (category == ItemCategory.Meds) return 5;
        if (IsA(category, ItemCategory.Consumable)) return 6;
        if (category == ItemCategory.Key) return 7;
        if (category == ItemCategory.Money) return 8;
        return 9;
    }
}
