using Godot;

namespace Lagoon;

/// <summary>
/// Entrobordo in linea d'asse + timone (solo dati): gozzi, pescherecci, cabinati. Le posizioni le da'
/// il modello dello scafo: <c>Mount_Prop</c> (centro dell'elica; −Z locale = verso della spinta in
/// marcia avanti, cioe' l'asse inclinato), <c>Mount_Rudder</c> (centro di pressione del timone, Y =
/// asse della pala), <c>Mount_Engine</c> (massa del motore). Parti mobili: <c>Rudder</c>, <c>Propeller</c>.
/// </summary>
[GlobalClass, Tool]
public partial class InboardDefinition : Resource
{
    [Export] public EngineDefinition? Engine { get; set; }
    [Export] public PropellerDefinition? Propeller { get; set; }
    [Export] public GearboxDefinition? Gearbox { get; set; }
    [Export] public FoilDefinition? Rudder { get; set; }
    [Export] public float RudderMaxDegrees { get; set; } = 35f;
    /// <summary>
    /// Velocita' del timone (°/s): barra o ruota meccanica ~40; timoneria idraulica dei pescherecci 3-5
    /// (SOLAS: da 35° a 30° dall'altra parte in 28 s).
    /// </summary>
    [Export] public float RudderRateDegrees { get; set; } = 40f;
}
