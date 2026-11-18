using Godot;

namespace Lagoon;

/// <summary>
/// Invertitore e riduzione (solo dati). La leva unica ha una zona di folle (±10% della corsa); il cambio
/// di marcia passa sempre dal folle, con un ritardo, e si rifiuta sopra <see cref="MaxShiftRpm"/>
/// (il regime viene prima riportato al minimo).
/// </summary>
[GlobalClass, Tool]
public partial class GearboxDefinition : Resource
{
    /// Rapporto di riduzione motore/elica in marcia avanti e indietro.
    [Export] public float RatioForward { get; set; } = 2.0f;
    [Export] public float RatioReverse { get; set; } = 2.0f;
    [Export] public float Efficiency { get; set; } = 0.96f;
    /// Tempo d'innesto (s): fuoribordo 0,2-0,5; invertitore idraulico marino 1-2.
    [Export] public float ShiftSeconds { get; set; } = 0.3f;
    /// Permanenza minima in folle fra avanti e indietro (s).
    [Export] public float NeutralDwellSeconds { get; set; } = 0.4f;
    /// Sopra questo regime il cambio aspetta che il motore cali.
    [Export] public float MaxShiftRpm { get; set; } = 1500f;
}
