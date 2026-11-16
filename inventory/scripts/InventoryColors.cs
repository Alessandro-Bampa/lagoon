using Godot;

namespace Lagoon;

/// <summary>
/// Colore di sfondo per categoria: tinge la tile in inventario e il cubo segnaposto degli oggetti
/// a terra privi di modello. La categoria piu' specifica vince, poi si risale l'albero.
/// </summary>
public static class InventoryColors
{
    public static Color ForCategory(ItemCategory category)
    {
        for (ItemCategory c = category; ; c = ItemCategories.ParentOf(c))
        {
            switch (c)
            {
                case ItemCategory.Weapon: return new Color(0.42f, 0.42f, 0.46f);
                case ItemCategory.WeaponMod: return new Color(0.36f, 0.44f, 0.52f);
                case ItemCategory.Ammo: return new Color(0.62f, 0.52f, 0.24f);
                case ItemCategory.Equipment: return new Color(0.28f, 0.42f, 0.62f);
                case ItemCategory.Container: return new Color(0.55f, 0.40f, 0.22f);
                case ItemCategory.Meds: return new Color(0.70f, 0.28f, 0.28f);
                case ItemCategory.Consumable: return new Color(0.30f, 0.58f, 0.34f);
                case ItemCategory.Key: return new Color(0.62f, 0.60f, 0.30f);
                case ItemCategory.Money: return new Color(0.36f, 0.60f, 0.42f);
                case ItemCategory.Info: return new Color(0.50f, 0.36f, 0.62f);
                case ItemCategory.SpecialItem: return new Color(0.30f, 0.56f, 0.60f);
                case ItemCategory.Barter: return new Color(0.48f, 0.46f, 0.42f);
                case ItemCategory.Generic: return new Color(0.45f, 0.45f, 0.48f);
            }
        }
    }
}
