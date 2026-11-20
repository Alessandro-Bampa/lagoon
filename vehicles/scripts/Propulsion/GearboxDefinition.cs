using Godot;

namespace Lagoon;

/// <summary>
/// Invertitore e riduzione (solo dati). La leva unica ha una zona di folle (±10% della corsa); il cambio
/// di marcia passa sempre dal folle e innesta dopo <see cref="ShiftSeconds"/>, senza aspettare che il
/// motore cali: all'innesto la frizione riporta il regime a <see cref="MaxShiftRpm"/> se e' piu' alto.
/// </summary>
[GlobalClass, Tool]
public partial class GearboxDefinition : Resource
{
    /// Rapporto di riduzione motore/elica in marcia avanti e indietro.
    [Export] public float RatioForward { get; set; } = 2.0f;
    [Export] public float RatioReverse { get; set; } = 2.0f;
    [Export] public float Efficiency { get; set; } = 0.96f;
    /// <summary>
    /// Tempo d'innesto (s) contato da quando si lascia la marcia precedente: il passaggio dal folle con
    /// la manetta a tacche ne consuma gia' una parte. Fuoribordo 0,2-0,3; invertitore marino 0,5-0,8.
    /// </summary>
    [Export] public float ShiftSeconds { get; set; } = 0.3f;
    /// Regime massimo all'innesto: sopra, la frizione dell'invertitore lo riporta qui.
    [Export] public float MaxShiftRpm { get; set; } = 1500f;
}
