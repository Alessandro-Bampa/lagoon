using Godot;
using Godot.Collections;

namespace Lagoon;

/// <summary>
/// Filtro whitelist/blacklist su una griglia, uno slot o un intero contenitore. Dato puro.
/// Regola: passa se (le inclusioni sono vuote OPPURE l'item e' incluso) E NON e' escluso.
/// L'esclusione vince sempre. Le categorie si confrontano sull'albero
/// (<see cref="ItemCategories.IsA"/>): includere <c>Ammo</c> include anche <c>AmmoBox</c>.
/// </summary>
[GlobalClass]
public partial class ItemFilter : Resource
{
    [Export] public Array<ItemCategory> IncludeCategories { get; set; } = new();
    [Export] public Array<string> IncludeItems { get; set; } = new();
    [Export] public Array<ItemCategory> ExcludeCategories { get; set; } = new();
    [Export] public Array<string> ExcludeItems { get; set; } = new();

    public bool Accepts(ItemDefinition def)
    {
        foreach (ItemCategory c in ExcludeCategories)
            if (ItemCategories.IsA(def.Category, c))
                return false;
        if (ExcludeItems.Contains(def.ItemId))
            return false;

        if (IncludeCategories.Count == 0 && IncludeItems.Count == 0)
            return true;
        if (IncludeItems.Contains(def.ItemId))
            return true;
        foreach (ItemCategory c in IncludeCategories)
            if (ItemCategories.IsA(def.Category, c))
                return true;
        return false;
    }
}
