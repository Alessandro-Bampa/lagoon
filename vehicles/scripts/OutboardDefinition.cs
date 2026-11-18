using Godot;

namespace Lagoon;

/// <summary>
/// Un fuoribordo montabile su qualunque scafo (solo dati, CLAUDE.md §4): modello, motore, invertitore,
/// elica, piede, sterzo e trim. Il modello (<c>Outboard_*.glb</c>) ha origine sul bordo dello specchio
/// (= perno del trim) e i nodi che legge <see cref="OutboardMotor"/>: <c>Swivel</c>, <c>Propeller</c>,
/// <c>Thrust</c>, <c>Tiller</c>, <c>TillerGrip</c>.
/// </summary>
[GlobalClass, Tool]
public partial class OutboardDefinition : Resource
{
    [Export] public PackedScene? Model { get; set; }

    [ExportGroup("Propulsione")]
    [Export] public EngineDefinition? Engine { get; set; }
    [Export] public GearboxDefinition? Gearbox { get; set; }
    [Export] public PropellerDefinition? Propeller { get; set; }
    /// Gambo + skeg come profilo: presa laterale della poppa in virata.
    [Export] public FoilDefinition? LowerUnit { get; set; }

    [ExportGroup("Sterzo")]
    /// Angolo massimo di sterzo del motore attorno al piantone.
    [Export] public float MaxSteerDegrees { get; set; } = 32f;
    /// Velocita' con cui il motore insegue il comando di sterzo (gradi al secondo).
    [Export] public float SteerSpeedDegrees { get; set; } = 90f;

    [ExportGroup("Trim")]
    /// Campo del trim idraulico (negativo = trim in, prua giu').
    [Export] public float TrimMinDegrees { get; set; } = -6f;
    [Export] public float TrimMaxDegrees { get; set; } = 16f;
    [Export] public float TrimRateDegrees { get; set; } = 3f;
    /// Assistenza: trim di crociera e velocita' (m/s) fra cui passa dal trim in a quello di crociera.
    [Export] public float AutoTrimCruiseDegrees { get; set; } = 2f;
    [Export] public float AutoTrimSpeedLow { get; set; } = 4f;
    [Export] public float AutoTrimSpeedHigh { get; set; } = 9f;

    [ExportGroup("Resa")]
    /// Giri massimi DISEGNATI dell'elica (oltre, a 60 fps, l'occhio vede l'effetto stroboscopico).
    [Export] public float MaxPropRpm { get; set; } = 1500f;

    [Export] public float Mass { get; set; } = 50f;
}
