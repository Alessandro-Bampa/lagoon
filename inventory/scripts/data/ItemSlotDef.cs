using Godot;

namespace Lagoon;

/// Tipo di uno slot di un oggetto. Si appende in coda (valore salvato nei .tres).
public enum ItemSlotKind
{
    /// Attacco generico (riservato al modding delle armi, non ancora implementato).
    Mod = 0,

    /// Pozzetto del caricatore: accetta un <see cref="MagazineDefinition"/> dello stesso calibro.
    Magazine = 1,
}

/// <summary>
/// Slot tipizzato DI UN OGGETTO (non del personaggio): contiene al massimo un'istanza figlia.
/// In questa fase esiste solo il pozzetto del caricatore delle armi; e' pero' gia' la struttura
/// su cui si innestera' il modding (calcio, ottiche, silenziatore).
/// </summary>
[GlobalClass]
public partial class ItemSlotDef : Resource
{
    /// Identificatore stabile nello slot, da cui deriva l'etichetta <c>UI_INV_ITEMSLOT_&lt;ID&gt;</c>.
    [Export] public string SlotId { get; set; } = "";
    [Export] public ItemSlotKind Kind { get; set; } = ItemSlotKind.Mod;
    [Export] public ItemFilter? Filter { get; set; }
}
