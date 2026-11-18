using Godot;

namespace Lagoon;

/// <summary>Come si governa la barca: barra del fuoribordo o volante su consolle.</summary>
public enum HelmType
{
    /// Seduti a poppa, la mano sinistra sulla barra: si sterza spostando il motore.
    Tiller,
    /// Seduti dietro la consolle, le mani sul volante (timoneria verso il motore o il timone).
    Wheel,
}

/// <summary>
/// La barca "su misura" (solo dati): scafo, propulsione, governo, consolle, accessori, livrea, masse
/// e coefficienti idrodinamici. Le barche del gioco saranno personalizzate cambiando questi pezzi:
/// <see cref="BoatAssembly"/> le monta, <see cref="BoatPhysics"/> le muove con lo stesso codice per
/// ogni classe (barchino, gozzo, peschereccio...).
///
/// Rete: la configurazione sta nella scena del livello, quindi e' identica su ogni peer per
/// costruzione. Cambiarla a partita in corso (cantiere, base) richiedera' di replicarla: lacuna.
/// </summary>
[GlobalClass, Tool]
public partial class BoatConfig : Resource
{
    /// <summary>
    /// Scafo (<c>Barchino_*.glb</c>, <c>*_Placeholder.glb</c>): attacchi, sedili, mesh idrodinamica
    /// <c>HydroHull</c>, collisioni.
    /// </summary>
    [Export] public PackedScene? Hull { get; set; }

    /// Massa dello scafo vuoto (kg). Motore e masse fisse si aggiungono.
    [Export] public float HullMass { get; set; } = 380f;

    /// Baricentro dello scafo vuoto, nello spazio del modello (fondo e pagliolo pesano piu' delle murate).
    [Export] public Vector3 HullCenterOfMass { get; set; } = new(0f, -0.08f, 0f);

    /// Coefficienti idrodinamici dello scafo (null = default di un planante).
    [Export] public HydroCoefficients? Hydro { get; set; }

    [ExportGroup("Propulsione")]
    /// Fuoribordo su <c>Mount_Motor</c>. Alternativo a <see cref="Inboard"/>.
    [Export] public OutboardDefinition? Motor { get; set; }
    /// Entrobordo in linea d'asse con timone (<c>Mount_Prop</c>, <c>Mount_Rudder</c>, <c>Mount_Engine</c>).
    [Export] public InboardDefinition? Inboard { get; set; }
    /// Elica di manovra di prua (<c>Mount_Thruster</c>), opzionale.
    [Export] public BowThrusterDefinition? Thruster { get; set; }
    /// Chiglie e skeg fissi (ciascuno sul nodo <see cref="FoilDefinition.Mount"/>).
    [Export] public FoilDefinition[] FixedFoils { get; set; } = System.Array.Empty<FoilDefinition>();

    [ExportGroup("Masse")]
    /// Zavorra, serbatoi, stiva, sovrastrutture: masse fisse nello spazio del modello.
    [Export] public BoatMassItem[] MassItems { get; set; } = System.Array.Empty<BoatMassItem>();

    [ExportGroup("Governo")]
    [Export] public HelmType Helm { get; set; } = HelmType.Tiller;

    /// Consolle col volante (solo con <see cref="HelmType.Wheel"/>), montata su <c>Mount_Console</c>.
    [Export] public PackedScene? Console { get; set; }

    /// Giri del volante da tutto a sinistra a tutto a destra (timoneria meccanica: ~2,5).
    [Export] public float WheelTurnsLockToLock { get; set; } = 2.5f;

    [ExportGroup("Aspetto")]
    [Export] public BoatAccessory[] Accessories { get; set; } = System.Array.Empty<BoatAccessory>();

    [Export] public BoatLivery? Livery { get; set; }
}
