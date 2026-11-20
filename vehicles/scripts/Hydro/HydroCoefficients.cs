using Godot;

namespace Lagoon;

/// <summary>Famiglia della carena: decide se la resistenza d'onda si dissolve con la planata.</summary>
public enum HullType
{
    Displacement,
    SemiDisplacement,
    Planing,
}

/// <summary>
/// Coefficienti idrodinamici di uno scafo (solo dati, CLAUDE.md §4). Li usa <see cref="HydroSolver"/>:
/// spinta, attrito, pressione/suzione (planata), resistenza d'onda, impatti, massa aggiunta e lo strato
/// di smorzamento di sicurezza. I valori di default sono quelli di partenza delle specifiche
/// (skill vehicles-boats §10); la taratura per barca si fa con <c>tools/boat_trials.gd</c>.
///
/// Convenzione dei vettori: lineari (avanzo, deriva, sussulto) = (Z, X, Y) della barca; angolari
/// (rollio, beccheggio, imbardata) = attorno a (Z, X, Y). Gli smorzamenti sono FRAZIONI di massa o
/// d'inerzia, cosi' valgono per barche di taglia diversa senza riscriverli.
/// </summary>
[GlobalClass, Tool]
public partial class HydroCoefficients : Resource
{
    [Export] public HullType Hull { get; set; } = HullType.Planing;

    [ExportGroup("Attrito (ITTC 1957)")]
    /// Fattore di forma k di (1 + k): 0,1-0,2 plananti, 0,2-0,4 dislocanti pieni.
    [Export] public float FormFactorK { get; set; } = 0.2f;
    /// Rugosita' (carena sporca): si somma a C_F. 0 = pulita, 0,0002-0,0008 incrostata.
    [Export] public float RoughnessDeltaCf { get; set; }

    [ExportGroup("Pressione e suzione (planata)")]
    [Export] public float PressureLinear { get; set; } = 0.05f;
    [Export] public float PressureQuadratic { get; set; } = 0.55f;
    [Export] public float PressureExponent { get; set; } = 1.0f;
    [Export] public float SuctionLinear { get; set; } = 0.05f;
    [Export] public float SuctionQuadratic { get; set; } = 0.35f;
    [Export] public float SuctionExponent { get; set; } = 1.0f;
    /// Velocita' di riferimento del termine lineare (m/s): la velocita' di crociera della barca.
    [Export] public float ReferenceSpeed { get; set; } = 8f;
    /// <summary>
    /// Limite di ventilazione: la depressione su una faccia non supera k · ρ·g·profondita' (entra aria).
    /// Senza, lo specchio di poppa di un planante verrebbe risucchiato.
    /// </summary>
    [Export] public float SuctionVentK { get; set; } = 1.0f;
    /// <summary>
    /// Presa laterale: moltiplica la parte di pressione/suzione dovuta alla velocita' di DERIVA (asse X
    /// della barca), con la forza aggiunta solo laterale e applicata sulla faccia. 1 = forma isotropa di
    /// Kerner. Separa due fenomeni che un coefficiente unico lega: la pressione sulle facce di prua in
    /// avanzo (nei dislocanti in gran parte gia' contata dalla resistenza d'onda: <c>c_p2</c> 0,1-0,15)
    /// e la resistenza al flusso trasversale in virata (per una lastra il coefficiente e' ~0,55 + 0,35,
    /// specifiche §8.3). Con <c>c_p2</c> basso e scala 1 lo scafo non "tiene": in virata scivola finche'
    /// il timone non si allinea al flusso (deriva ≈ angolo di barra, ~30°). Solo laterale perche'
    /// scalare la pressione lungo la normale delle facce di sentina darebbe forze verticali spurie
    /// (sopravvento spinta in su, sottovento risucchio in giu'): la barca sbanderebbe verso l'interno.
    /// </summary>
    [Export] public float LateralPressureScale { get; set; } = 1f;
    /// <summary>
    /// Distribuzione della pressione lungo la lunghezza bagnata (solo pressione, non suzione): 0 =
    /// uniforme (Kerner), 1 = lineare da 0 allo specchio a 2× al bordo d'attacco (centro di pressione
    /// a 2/3 della lunghezza bagnata, come la lastra planante di Savitsky ~0,7; la pressione si annulla
    /// sullo specchio ventilato). Con la distribuzione uniforme il centro di pressione cade a meta' della
    /// lunghezza bagnata: per portarlo sul baricentro la barca plana su una superficie troppo lunga,
    /// con assetto troppo basso e troppo attrito.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float PressureLeadingEdge { get; set; }
    /// <summary>
    /// Specchio ventilato (plananti e semi-plananti): in planata la pressione idrostatica si annulla
    /// sullo specchio e torna piena al bordo d'attacco (lineare, pesata come la dissolvenza della
    /// resistenza d'onda col Froude volumetrico). 0 = idrostatica piena ovunque. Senza, a 20 nodi un
    /// terzo del peso restava sulla spinta di Archimede (Savitsky: ~10%) e l'acqua "ferma" sotto lo
    /// specchio faceva da molla al beccheggio: il trim del motore non cambiava l'assetto.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float TransomRelief { get; set; }

    [ExportGroup("Resistenza d'onda")]
    /// Moltiplicatore della curva r_w(Fn) dei dislocanti pesanti: 1 pescherecci, ~0,6 leggeri/plananti.
    [Export] public float WaveResistanceScale { get; set; } = 0.6f;
    /// Dissolvenza della resistenza d'onda col numero di Froude volumetrico (plananti, semi-plananti).
    [Export] public Vector2 WaveFadeFnv { get; set; } = new(1.0f, 2.2f);

    [ExportGroup("Impatti (slamming)")]
    [Export] public float SlamK { get; set; } = 0.5f;
    /// Γ_max in multipli di g.
    [Export] public float SlamGammaMaxG { get; set; } = 3f;
    [Export] public float SlamExponent { get; set; } = 2f;
    /// Tetto della forza d'impatto totale in multipli di m·g: 10 tender/skiff, 6 open, 3 cabinato, 2 nave.
    [Export] public float SlamMaxG { get; set; } = 10f;

    [ExportGroup("Massa aggiunta")]
    [Export] public bool AddedMassEnabled { get; set; } = true;
    /// Frazioni della massa (avanzo, deriva, sussulto), al galleggiamento di progetto.
    [Export] public Vector3 AddedMass { get; set; } = new(0.05f, 0.45f, 0.5f);
    /// Frazioni dell'inerzia (rollio, beccheggio, imbardata).
    [Export] public Vector3 AddedInertia { get; set; } = new(0.15f, 0.6f, 0.45f);

    [ExportGroup("Smorzamento di sicurezza")]
    /// <summary>
    /// Lineare, in frazioni di massa al secondo (avanzo, deriva, sussulto), scalato con l'area immersa.
    /// VIETATO usarlo per fare velocita' massima o virata: serve solo al decadimento delle oscillazioni.
    /// </summary>
    [Export] public Vector3 SafetyLinear { get; set; } = new(0f, 0.05f, 0.3f);
    /// Quadratico, frazioni di massa per metro.
    [Export] public Vector3 SafetyQuadratic { get; set; } = Vector3.Zero;
    /// Angolare, frazioni d'inerzia al secondo (rollio, beccheggio, imbardata).
    [Export] public Vector3 SafetyAngular { get; set; } = new(0.6f, 0.6f, 0.3f);

    [ExportGroup("Aria")]
    /// Coefficiente di resistenza delle facce emerse (vento, moto nell'aria).
    [Export] public float AirCd { get; set; } = 1.0f;
}
