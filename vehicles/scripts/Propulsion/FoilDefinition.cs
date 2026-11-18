using Godot;

namespace Lagoon;

public enum FoilKind
{
    /// Timone: ruota col comando, puo' stare nella scia dell'elica, flusso laterale attenuato.
    Rudder,
    /// Piede del fuoribordo (gambo + skeg): ruota con lo sterzo e col trim.
    LowerUnit,
    /// Chiglia o skeg fissi: stabilita' di rotta e resistenza laterale.
    Keel,
}

/// <summary>
/// Appendice a profilo (solo dati): timone, piede del fuoribordo, chiglia. Il modello di forza e' in
/// <see cref="Foil"/>. Orientamento nel modello: il nodo di montaggio ha l'origine nel centro di
/// pressione (~25% della corda), −Z = verso il bordo d'attacco (prua), Y = apertura (in su).
/// </summary>
[GlobalClass, Tool]
public partial class FoilDefinition : Resource
{
    [Export] public FoilKind Kind { get; set; } = FoilKind.Rudder;
    /// Nodo dello scafo su cui sta (chiglie fisse; timone = <c>Mount_Rudder</c>). Vuoto per il piede.
    [Export] public string Mount { get; set; } = "";
    /// Area laterale (m²).
    [Export] public float Area { get; set; } = 0.15f;
    /// Apertura (altezza) del profilo (m).
    [Export] public float Span { get; set; } = 0.5f;
    /// Effetto specchio: 1,5-2 se il bordo superiore e' a filo dello scafo.
    [Export] public float AspectRatioMultiplier { get; set; } = 1.5f;
    /// Angolo di stallo: 30-40° per timoni nella scia dell'elica, 15-25° fuori scia.
    [Export] public float StallDegrees { get; set; } = 32f;
    /// Efficacia con flusso dal bordo d'uscita (retromarcia).
    [Export] public float ReverseEfficiency { get; set; } = 0.6f;
    /// Raddrizzamento del flusso laterale da parte di scafo ed elica (solo timoni): γ_R.
    [Export] public float LateralShielding { get; set; } = 0.5f;
    /// Resistenza di profilo.
    [Export] public float DragCoefficient { get; set; } = 0.015f;
    /// Accelerazione della scia raggiunta al timone (k_x, 0,6-0,8). Solo timoni dietro l'elica.
    [Export] public float SlipstreamFactor { get; set; } = 0.7f;

    /// <summary>
    /// Chiglie lunghe: il profilo si valuta in tanti segmenti lungo <see cref="Chord"/>, ognuno con la
    /// propria velocita'. Cosi' la chiglia smorza anche l'imbardata (un profilo in un punto solo, al
    /// baricentro, non lo farebbe). 1 = profilo concentrato (timoni, piedi).
    /// </summary>
    [Export] public int ChordSegments { get; set; } = 1;
    /// Lunghezza della chiglia lungo la quale si distribuiscono i segmenti (m).
    [Export] public float Chord { get; set; }

    public float AspectRatio => Span * Span / Mathf.Max(Area, 1e-4f) * AspectRatioMultiplier;
}
