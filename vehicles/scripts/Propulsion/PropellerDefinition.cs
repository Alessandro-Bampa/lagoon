using Godot;

namespace Lagoon;

/// <summary>Mano dell'elica: destrorsa = in marcia avanti gira in senso orario vista da poppa.</summary>
public enum PropellerHand
{
    Right = 1,
    Left = -1,
}

/// <summary>
/// Elica (solo dati) col modello lineare a coefficienti K_T, K_Q (skill vehicles-boats §11). Se
/// <see cref="Kt0"/>/<see cref="Kq0"/>/<see cref="J0"/> sono 0 si ricavano dal rapporto passo/diametro
/// con la tabella di partenza delle specifiche (eliche serie B a 4 pale, rapporto d'area 0,55-0,70).
/// </summary>
[GlobalClass, Tool]
public partial class PropellerDefinition : Resource
{
    [Export] public float Diameter { get; set; } = 0.30f;
    [Export] public float Pitch { get; set; } = 0.33f;
    [Export] public PropellerHand Hand { get; set; } = PropellerHand.Right;

    [ExportGroup("Coefficienti (0 = dalla tabella P/D)")]
    [Export] public float Kt0 { get; set; }
    [Export] public float Kq0 { get; set; }
    [Export] public float J0 { get; set; }

    [ExportGroup("Comportamento")]
    /// Spinta in retromarcia rispetto alla marcia avanti, a pari giri.
    [Export] public float ReverseEfficiency { get; set; } = 0.6f;
    /// Mantello Kort: piu' spinta a bassa velocita' (pescherecci, rimorchiatori).
    [Export] public bool Nozzle { get; set; }
    [Export] public float NozzleK0 { get; set; } = 1.35f;
    [Export] public float NozzleJn { get; set; } = 0.55f;
    /// Frazione di scia w e deduzione di spinta t: fuoribordo 0-0,05 / 0,02-0,05; dislocante 0,2-0,35 / 0,15-0,25.
    [Export] public float WakeFraction { get; set; } = 0.03f;
    [Export] public float ThrustDeduction { get; set; } = 0.04f;
    /// Effetto evolutivo: frazione della spinta che diventa forza laterale a bassa velocita'.
    [Export] public float PropWalkAhead { get; set; } = 0.015f;
    [Export] public float PropWalkAstern { get; set; } = 0.045f;
    /// Velocita' (m/s) a cui l'effetto evolutivo si dimezza.
    [Export] public float PropWalkSpeed { get; set; } = 1.2f;
    /// Profondita' del centro dell'elica (in diametri) sotto cui, ad alto carico, aspira aria.
    [Export] public float VentilationDepthRatio { get; set; } = 0.6f;
    /// Spinta persa quando l'elica ventila (0,6-0,8).
    [Export] public float VentilationLoss { get; set; } = 0.7f;
    /// Inerzia dell'elica con l'acqua trascinata (kg·m²), lato elica.
    [Export] public float InertiaKgm2 { get; set; } = 0.01f;

    private static readonly float[] TablePd = { 0.6f, 0.8f, 1.0f, 1.2f, 1.4f };
    private static readonly float[] TableKt = { 0.25f, 0.35f, 0.45f, 0.52f, 0.58f };
    private static readonly float[] TableKq = { 0.025f, 0.042f, 0.060f, 0.085f, 0.110f };
    private static readonly float[] TableJ0 = { 0.70f, 0.90f, 1.10f, 1.30f, 1.50f };

    public float ResolvedKt0 => Kt0 > 0f ? Kt0 : FromTable(TableKt);
    public float ResolvedKq0 => Kq0 > 0f ? Kq0 : FromTable(TableKq);
    public float ResolvedJ0 => J0 > 0f ? J0 : FromTable(TableJ0);

    private float FromTable(float[] values)
    {
        float pd = Pitch / Mathf.Max(Diameter, 0.01f);
        if (pd <= TablePd[0])
            return values[0] * pd / TablePd[0];
        for (int i = 0; i + 1 < TablePd.Length; i++)
        {
            if (pd <= TablePd[i + 1])
                return Mathf.Lerp(values[i], values[i + 1], (pd - TablePd[i]) / (TablePd[i + 1] - TablePd[i]));
        }
        int last = TablePd.Length - 1;
        return values[last] + (values[last] - values[last - 1]) * (pd - TablePd[last]) / (TablePd[last] - TablePd[last - 1]);
    }
}
