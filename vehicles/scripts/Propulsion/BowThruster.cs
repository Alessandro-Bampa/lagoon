using Godot;

namespace Lagoon;

/// <summary>
/// Elica di manovra di prua (solo host): forza laterale nel tunnel, efficace solo a bassa velocita'
/// (f = 1 / (1 + (U/U½)²)) e solo col tunnel sott'acqua.
/// </summary>
public sealed class BowThruster
{
    public readonly BowThrusterDefinition Definition;
    private readonly Vector3 _tunnel;
    /// Comando filtrato −1..1 (positivo = prua a dritta).
    public float Output { get; private set; }

    public BowThruster(BowThrusterDefinition definition, Vector3 tunnelInBoat)
    {
        Definition = definition;
        _tunnel = tunnelInBoat;
    }

    public void Step(BoatForces ctx, float command, bool running, float speedThroughWater)
    {
        float target = running ? Mathf.Clamp(command, -1f, 1f) : 0f;
        Output = Mathf.MoveToward(Output, target, ctx.Dt / Mathf.Max(Definition.SpinUpSeconds, 0.05f));
        if (Mathf.Abs(Output) < 1e-3f)
            return;

        Vector3 point = ctx.Transform * _tunnel;
        if (ctx.Depth(point) <= 0f)
            return;
        float ratio = speedThroughWater / Mathf.Max(Definition.HalfEffectSpeed, 0.1f);
        float effect = 1f / (1f + ratio * ratio);
        ctx.AddAt(Definition.MaxThrust * Output * effect * ctx.Right, point);
    }
}
