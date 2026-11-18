using Godot;

namespace Lagoon;

public enum EngineType
{
    /// Benzina: la leva comanda la farfalla, il regime dipende dal carico.
    Petrol,
    /// Diesel col regolatore: la leva chiede un regime, il regolatore lo tiene sotto carico.
    Diesel,
}

/// <summary>
/// Motore marino (solo dati): potenza, regimi, curva di coppia, inerzia. La dinamica la integra
/// <see cref="MarineEngine"/>; la coppia massima e' quella al regime nominale (P / ω).
/// </summary>
[GlobalClass, Tool]
public partial class EngineDefinition : Resource
{
    [Export] public EngineType Type { get; set; } = EngineType.Petrol;
    /// Potenza all'albero al regime nominale (kW). 1 cv = 0,7355 kW.
    [Export] public float MaxPowerKw { get; set; } = 30f;
    [Export] public float RatedRpm { get; set; } = 5500f;
    [Export] public float IdleRpm { get; set; } = 850f;
    /// Limitatore: oltre si taglia l'accensione (con isteresi del 2%).
    [Export] public float LimiterRpm { get; set; } = 6200f;
    /// <summary>
    /// Curva di coppia: x = giri / nominali (0..1,2), y = coppia / coppia al nominale. Null = curva di
    /// default del tipo (benzina: sale fino al 40% dei giri poi piatta; diesel: piatta).
    /// </summary>
    [Export] public Curve? TorqueCurve { get; set; }
    /// Inerzia di motore e volano (kg·m²), lato motore.
    [Export] public float InertiaKgm2 { get; set; } = 0.05f;
    /// Attrito interno come frazione della coppia nominale (costante + viscoso al nominale).
    [Export] public float FrictionFraction { get; set; } = 0.06f;
    /// <summary>
    /// Perdite di pompaggio a farfalla chiusa (freno motore), frazione della coppia nominale al regime
    /// nominale: tolto il gas un benzina scende di giri in meno di un secondo.
    /// </summary>
    [Export] public float PumpingFraction { get; set; } = 0.2f;
    /// Costante di tempo della farfalla (s): benzina 0,1-0,2; diesel turbo 0,5-2.
    [Export] public float ThrottleTimeConstant { get; set; } = 0.15f;
    /// Consumo specifico (g/kWh): benzina 300-350, diesel 200-230.
    [Export] public float BsfcGPerKwh { get; set; } = 320f;
    /// Massa del motore entrobordo (kg). Per il fuoribordo vale OutboardDefinition.Mass.
    [Export] public float Mass { get; set; } = 150f;

    public float RatedOmega => RatedRpm * Mathf.Tau / 60f;
    public float MaxTorque => MaxPowerKw * 1000f / Mathf.Max(RatedOmega, 1f);

    /// Coppia disponibile a farfalla piena, come frazione della nominale, a <paramref name="rpmRatio"/>.
    public float TorqueFraction(float rpmRatio)
    {
        if (TorqueCurve != null)
            return Mathf.Max(TorqueCurve.Sample(Mathf.Clamp(rpmRatio, 0f, 1.2f)), 0f);
        if (rpmRatio > 1.05f)
            return Mathf.Max(0f, 1f - (rpmRatio - 1.05f) * 4f);
        if (Type == EngineType.Diesel)
            return rpmRatio < 0.25f ? 0.75f + rpmRatio : 1f;
        // Benzina: picco di coppia (+12%) a ~70% dei giri, come i fuoribordo veri.
        if (rpmRatio < 0.4f)
            return 0.6f + rpmRatio;
        return 1f + 0.12f * Mathf.Sin(Mathf.Pi * Mathf.Min((rpmRatio - 0.4f) / 0.6f, 1f));
    }
}
