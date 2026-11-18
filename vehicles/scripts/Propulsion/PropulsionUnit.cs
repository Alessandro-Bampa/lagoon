using Godot;

namespace Lagoon;

/// <summary>
/// Motore + invertitore + elica (solo host): integra il regime, calcola spinta e coppia col modello
/// K_T/K_Q e applica alla barca spinta, effetto evolutivo e reazione di coppia (skill vehicles-boats §11).
/// Lo usano fuoribordo (<see cref="OutboardDrive"/>) ed entrobordo (<see cref="ShaftDrive"/>): cambia
/// solo dove sta l'elica e dove punta.
/// </summary>
public sealed class PropulsionUnit
{
    private const int Substeps = 6;
    private const float MinRevs = 0.05f;

    public readonly MarineEngine Engine;
    public readonly PropellerDefinition Propeller;
    private readonly float _kt0, _kq0, _j0;

    // --- Risultati del passo ---------------------------------------------------------------
    /// Giri dell'elica al secondo, con segno (negativi in retromarcia).
    public float Revs { get; private set; }
    /// Spinta lungo l'asse (N, negativa in retromarcia), prima della deduzione.
    public float Thrust { get; private set; }
    /// Coppia assorbita dall'elica (N·m, valore assoluto).
    public float Torque { get; private set; }
    /// Velocita' d'avanzo all'elica (m/s).
    public float AdvanceSpeed { get; private set; }
    /// Profondita' del centro dell'elica sotto la superficie (m).
    public float Depth { get; private set; }
    /// Frazione di disco immersa.
    public float Immersion { get; private set; }
    public bool Ventilating { get; private set; }
    /// Fattore di spinta della ventilazione (1 = nessuna).
    public float VentilationFactor { get; private set; } = 1f;
    /// Regresso apparente (0..1), solo in marcia avanti.
    public float Slip { get; private set; }

    public PropulsionUnit(EngineDefinition engine, GearboxDefinition gearbox, PropellerDefinition propeller)
    {
        Engine = new MarineEngine(engine, gearbox);
        Propeller = propeller;
        _kt0 = propeller.ResolvedKt0;
        _kq0 = propeller.ResolvedKq0;
        _j0 = Mathf.Max(propeller.ResolvedJ0, 0.1f);
    }

    /// <summary>
    /// Passo completo. <paramref name="propPoint"/> e <paramref name="axis"/> (versore della spinta in
    /// marcia avanti) sono nel mondo. Applica le forze a <paramref name="ctx"/>.
    /// </summary>
    public void Step(BoatForces ctx, float lever, bool running, Vector3 propPoint, Vector3 axis)
    {
        float dt = ctx.Dt;
        float rho = ctx.Water?.Density ?? 1025f;
        float d = Propeller.Diameter;
        float radius = d * 0.5f;

        Engine.Command(lever, running, dt);
        Engine.UpdateThrottle(dt);

        // Acqua all'elica: velocita' d'avanzo e immersione.
        Vector3 relative = ctx.PointVelocity(propPoint) - ctx.WaterVelocity(propPoint);
        float va = (1f - Propeller.WakeFraction) * relative.Dot(axis);
        AdvanceSpeed = va;
        Depth = ctx.Depth(propPoint);
        Immersion = Mathf.Clamp((Depth + radius) / (2f * radius), 0f, 1f);

        // Regime a sotto-passi: la coppia dell'elica dipende dai giri, e un motore leggero con
        // l'elica che ventila e' rigido.
        int gear = Engine.Gear;
        float ratio = Engine.Ratio;
        float eta = Engine.Gearbox.Efficiency;
        float extraInertia = gear != 0 ? Propeller.InertiaKgm2 / (ratio * ratio) : 0f;
        float sub = dt / Substeps;
        float thrust = 0f, torque = 0f;
        for (int s = 0; s < Substeps; s++)
        {
            float n = gear * Engine.Omega / (Mathf.Tau * ratio);
            Evaluate(n, va, rho, out thrust, out torque);
            float load = gear != 0 ? torque / (ratio * eta) : 0f;
            Engine.Integrate(sub, load, extraInertia);
        }
        float revs = gear * Engine.Omega / (Mathf.Tau * ratio);
        Evaluate(revs, va, rho, out thrust, out torque);
        Revs = revs;
        Thrust = thrust;
        Torque = torque;
        Slip = gear > 0 && revs > MinRevs ? Mathf.Clamp(1f - va / (revs * Propeller.Pitch), -1f, 1f) : 0f;

        UpdateVentilation(revs, dt);

        // Forze sulla barca.
        float hand = (float)Propeller.Hand;
        ctx.AddAt((1f - Propeller.ThrustDeduction) * thrust * axis, propPoint);

        if (gear != 0)
        {
            Vector3 rel = ctx.LinearVelocity - ctx.WaterVelocity(ctx.Com);
            float u = new Vector2(rel.X, rel.Z).Length();
            float walk = gear > 0 ? Propeller.PropWalkAhead : Propeller.PropWalkAstern;
            float low = 1f / (1f + Mathf.Pow(u / Mathf.Max(Propeller.PropWalkSpeed, 0.1f), 2f));
            ctx.AddAt(walk * Mathf.Abs(thrust) * low * hand * gear * ctx.Right, propPoint);
            ctx.Torque += -torque * hand * gear * axis;
        }
    }

    /// <summary>
    /// Stima statica del tiro a punto fisso (N, netto della deduzione di spinta) a tutto gas: il regime
    /// in cui la coppia del motore (curva, attriti) pareggia quella dell'elica a J = 0; il diesel col
    /// regolatore non supera i giri nominali. Serve all'anteprima nell'editor, non alla simulazione.
    /// </summary>
    public static float EstimateBollard(EngineDefinition engine, GearboxDefinition gearbox, PropellerDefinition prop,
        float rho, out float engineRpm)
    {
        float ratio = Mathf.Max(gearbox.RatioForward, 0.1f);
        float d = prop.Diameter;
        float kq0 = prop.ResolvedKq0;
        float rated = engine.RatedOmega;
        float qMax = engine.MaxTorque;
        float lo = 0f, hi = engine.LimiterRpm;
        for (int i = 0; i < 40; i++)
        {
            float rpm = 0.5f * (lo + hi);
            float omega = rpm * Mathf.Tau / 60f;
            float n = omega / (Mathf.Tau * ratio);
            float qEngine = qMax * (engine.TorqueFraction(omega / rated) - engine.FrictionFraction * (0.5f + 0.5f * omega / rated));
            float qProp = rho * n * n * Mathf.Pow(d, 5f) * kq0 / (ratio * gearbox.Efficiency);
            if (qEngine > qProp)
                lo = rpm;
            else
                hi = rpm;
        }
        engineRpm = 0.5f * (lo + hi);
        if (engine.Type == EngineType.Diesel)
            engineRpm = Mathf.Min(engineRpm, engine.RatedRpm);
        float revs = engineRpm / 60f / ratio;
        float kt = prop.ResolvedKt0 * (prop.Nozzle ? prop.NozzleK0 : 1f);
        return rho * revs * revs * Mathf.Pow(d, 4f) * kt * (1f - prop.ThrustDeduction);
    }

    /// <summary>
    /// K_T, K_Q lineari in J, limitati; retromarcia, mantello, immersione e ventilazione.
    /// T = ρ n² D⁴ K_T, Q = ρ n² D⁵ K_Q (con segno di n per T, valore assoluto per Q).
    /// </summary>
    private void Evaluate(float n, float va, float rho, out float thrust, out float torque)
    {
        float d = Propeller.Diameter;
        float absN = Mathf.Abs(n);
        float sign = n >= 0f ? 1f : -1f;
        float j = sign * va / (Mathf.Max(absN, MinRevs) * d);
        float kt = Mathf.Clamp(_kt0 * (1f - j / _j0), -0.3f * _kt0, 1.3f * _kt0);
        float kq = Mathf.Clamp(_kq0 * (1f - 0.8f * j / _j0), 0.15f * _kq0, 1.3f * _kq0);
        if (n < 0f)
            kt *= Propeller.ReverseEfficiency;
        if (Propeller.Nozzle)
            kt *= 1f + (Propeller.NozzleK0 - 1f) * Mathf.Max(0f, 1f - j / Mathf.Max(Propeller.NozzleJn, 0.05f));

        float n2 = n * n;
        float d4 = d * d * d * d;
        float vent = VentilationFactor;
        thrust = rho * n2 * d4 * kt * sign * Immersion * vent;
        torque = rho * n2 * d4 * d * kq * Immersion * Mathf.Pow(vent, 0.8f);
        _lastKtRatio = kt / Mathf.Max(_kt0, 1e-4f);
    }

    private float _lastKtRatio;

    /// <summary>
    /// Ventilazione: elica vicina alla superficie e caricata (K_T/K_T0 &gt; 0,5, oltre meta' dei giri
    /// massimi) aspira aria: perde spinta in ~0,1 s, la coppia cala, il motore va su di giri. Si
    /// recupera in ~0,6 s quando le condizioni tornano normali (isteresi).
    /// </summary>
    private void UpdateVentilation(float revs, float dt)
    {
        float maxRevs = Engine.Engine.LimiterRpm / 60f / Mathf.Max(Engine.Ratio, 0.1f);
        bool shallow = Depth < Propeller.VentilationDepthRatio * Propeller.Diameter;
        bool loaded = _lastKtRatio > 0.5f && Mathf.Abs(revs) > 0.5f * maxRevs;
        bool trigger = shallow && loaded && Immersion > 0f;
        if (trigger)
            Ventilating = true;
        else if (!shallow || Mathf.Abs(revs) < 0.35f * maxRevs)
            Ventilating = false;

        float target = Ventilating ? 1f - Propeller.VentilationLoss : 1f;
        float rate = Ventilating ? dt / 0.1f : dt / 0.6f;
        VentilationFactor = Mathf.MoveToward(VentilationFactor, target, rate);
    }
}
