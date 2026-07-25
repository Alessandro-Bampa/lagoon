namespace Lagoon;

/// <summary>
/// Categoria funzionale di un item. Guida il comportamento di default (es. un Container espone
/// una griglia interna) e il colore placeholder nella UI quando manca una texture.
/// </summary>
public enum ItemCategory
{
    Generic,
    Equipment,
    Container,
    Weapon,
    Consumable,
}
