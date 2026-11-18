using Godot;

namespace Lagoon;

/// <summary>
/// Elica di manovra di prua (solo dati), nel tunnel <c>Mount_Thruster</c> dello scafo. Spinge di lato
/// la prua; perde efficacia in fretta sopra 2-3 nodi.
/// </summary>
[GlobalClass, Tool]
public partial class BowThrusterDefinition : Resource
{
    /// Spinta massima (N): ~10-12 kgf per kW.
    [Export] public float MaxThrust { get; set; } = 4000f;
    /// Tempo per arrivare a regime (s).
    [Export] public float SpinUpSeconds { get; set; } = 0.7f;
    /// Velocita' (m/s) a cui l'effetto si dimezza.
    [Export] public float HalfEffectSpeed { get; set; } = 1.0f;
}
