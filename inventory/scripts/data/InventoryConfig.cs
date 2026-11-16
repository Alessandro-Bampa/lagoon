using Godot;

namespace Lagoon;

/// <summary>
/// Valori di taratura dell'inventario (i [CONFIG] della specifica). Vivono in
/// <c>resources/inventory_config.tres</c>, caricato da <see cref="ItemDatabase"/>: nessuno di
/// questi numeri va scritto nel codice. <see cref="Current"/> non e' stato di gioco ma dato
/// statico di build, identico su ogni peer.
/// </summary>
[GlobalClass]
public partial class InventoryConfig : Resource
{
    public static InventoryConfig Current { get; set; } = new();

    [ExportGroup("UI")]
    /// Lato massimo di una cella a 1080p; la schermata lo riduce su finestre piccole.
    [Export] public int CellPx { get; set; } = 64;
    [Export] public int MinCellPx { get; set; } = 36;
    [Export] public float DragThresholdPx { get; set; } = 6f;
    [Export] public float DragSourceAlpha { get; set; } = 0.35f;
    [Export] public float ErrorToastSeconds { get; set; } = 2f;
    [Export] public int TagMaxLength { get; set; } = 16;

    [ExportGroup("Peso")]
    [Export] public float OverweightStartKg { get; set; } = 26f;
    [Export] public float OverweightCriticalKg { get; set; } = 61f;
    [Export] public float OverweightMaxKg { get; set; } = 70f;

    /// Fattore di velocita' al raggiungimento della soglia critica e di quella massima.
    [Export] public float CriticalSpeedFactor { get; set; } = 0.6f;
    [Export] public float MaxSpeedFactor { get; set; } = 0.15f;

    [ExportGroup("Regole")]
    [Export] public bool AllowPistolInPrimarySlots { get; set; } = true;

    /// Uno zaino o un rig entrano in uno zaino solo se vuoti.
    [Export] public bool NestedBagsMustBeEmpty { get; set; } = true;

    /// Item indossato automaticamente nello slot tasche (sostituibile per varianti piu' grandi).
    [Export] public string PocketsItemId { get; set; } = "pockets";

    /// Righe della griglia virtuale "a terra" quando non si puo' calcolare dallo schermo.
    [Export] public int GroundColumns { get; set; } = 10;
}
