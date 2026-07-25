using Godot;

namespace Lagoon;

/// <summary>
/// Colori placeholder per categoria, usati quando un item non ha una texture icona
/// (<see cref="ItemDefinition.ResolveIcon"/> nullo) e per tingere i pickup nel mondo.
/// </summary>
public static class InventoryColors
{
    public static Color ForCategory(ItemCategory category) => category switch
    {
        ItemCategory.Container => new Color(0.55f, 0.40f, 0.22f),
        ItemCategory.Equipment => new Color(0.30f, 0.45f, 0.65f),
        ItemCategory.Weapon => new Color(0.35f, 0.35f, 0.38f),
        ItemCategory.Consumable => new Color(0.30f, 0.60f, 0.35f),
        _ => new Color(0.45f, 0.45f, 0.48f),
    };
}
