using Godot;

namespace Lagoon;

/// <summary>
/// Un accessorio montato su un attacco dello scafo (<c>Mount_Ladder</c>, <c>Mount_NavLight</c>, ...).
/// Solo dati: modello e nome dell'attacco. Un attacco che lo scafo non ha viene ignorato con un avviso.
/// </summary>
[GlobalClass, Tool]
public partial class BoatAccessory : Resource
{
    [Export] public PackedScene? Model { get; set; }
    [Export] public string Mount { get; set; } = "";

    /// <summary>
    /// Gruppi in cui entra il nodo montato: la scaletta va in <c>swim_ladder</c> (da li' chi nuota si
    /// issa fino a 1,6 m, skill water-ocean §8).
    /// </summary>
    [Export] public string[] Groups { get; set; } = System.Array.Empty<string>();
}
