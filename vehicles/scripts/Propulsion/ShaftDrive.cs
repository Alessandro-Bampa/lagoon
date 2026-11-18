using Godot;

namespace Lagoon;

/// <summary>
/// Entrobordo in linea d'asse + timone (solo host). Asse fisso dal modello (<c>Mount_Prop</c>),
/// timone a <c>Mount_Rudder</c> nella scia dell'elica (MMG semplificato, skill vehicles-boats §11):
/// <list type="bullet">
/// <item>senza flusso (ferma, in folle) il timone non fa nulla;</item>
/// <item>col colpo d'elica avanti la scia investe il timone e gira la poppa anche da fermi;</item>
/// <item>in retromarcia la scia va verso prua: il timone conta solo con abbrivio indietro, mentre
/// l'effetto evolutivo (elica destrorsa → poppa a sinistra) domina.</item>
/// </list>
/// </summary>
public sealed class ShaftDrive
{
    public readonly InboardDefinition Definition;
    public readonly PropulsionUnit Unit;
    private readonly Transform3D _prop;
    private readonly Transform3D _rudder;
    private readonly bool _rudderInWash;

    /// Angolo del timone (rad, positivo = vira a dritta: bordo d'uscita verso dritta).
    public float RudderAngle { get; private set; }

    public ShaftDrive(InboardDefinition definition, Transform3D propMount, Transform3D rudderMount)
    {
        Definition = definition;
        Unit = new PropulsionUnit(definition.Engine!, definition.Gearbox!, definition.Propeller!);
        _prop = propMount.Orthonormalized();
        _rudder = rudderMount.Orthonormalized();

        // Il timone e' nella scia se sta a poppavia dell'elica e non troppo di lato.
        Vector3 axis = -_prop.Basis.Z;
        Vector3 offset = _rudder.Origin - _prop.Origin;
        Vector3 lateral = offset - axis * offset.Dot(axis);
        _rudderInWash = offset.Dot(-axis) > 0f && lateral.Length() < definition.Propeller!.Diameter;
    }

    public void Step(BoatForces ctx, in BoatCommands cmd)
    {
        float dt = ctx.Dt;
        RudderAngle = Mathf.MoveToward(RudderAngle, cmd.Steer * Mathf.DegToRad(Definition.RudderMaxDegrees),
            Mathf.DegToRad(Definition.RudderRateDegrees) * dt);

        Transform3D xf = ctx.Transform;
        Vector3 propPoint = xf * _prop.Origin;
        Vector3 axis = (xf.Basis * -_prop.Basis.Z).Normalized();
        Unit.Step(ctx, cmd.Throttle, cmd.Running, propPoint, axis);

        if (Definition.Rudder is not FoilDefinition foil)
            return;

        // Scia sul timone (disco attuatore), solo con spinta in avanti.
        float wash = 0f;
        float thrust = Unit.Thrust;
        if (_rudderInWash && thrust > 0f)
        {
            float rho = ctx.Water?.Density ?? 1025f;
            float d = Definition.Propeller!.Diameter;
            float va = Mathf.Max(Unit.AdvanceSpeed, 0f);
            float jet = Mathf.Sqrt(va * va + 8f * thrust / (rho * Mathf.Pi * d * d));
            float slip = va + foil.SlipstreamFactor * (jet - va);
            float inWash = Mathf.Min(1f, d / Mathf.Max(foil.Span, 0.05f));
            float axial = Mathf.Sqrt(inWash * slip * slip + (1f - inWash) * va * va);
            wash = Mathf.Max(axial - va, 0f);
        }

        Basis r = xf.Basis * _rudder.Basis * new Basis(Vector3.Up, RudderAngle);
        Vector3 center = xf * _rudder.Origin;
        Foil.Apply(foil, ctx, center, r.Z.Normalized(), r.X.Normalized(), r.Y.Normalized(), wash, -axis);
    }
}
