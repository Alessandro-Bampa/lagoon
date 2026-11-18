using Godot;

namespace Lagoon;

/// <summary>
/// Fuoribordo in esecuzione (solo host): sterzo sul piantone, trim sullo specchio, spinta vettorizzata,
/// piede come profilo. Nessuna forza inventata: trim in = asse di spinta che solleva la poppa (prua
/// giu'), trim out = prua su; con la marcia inserita si governa anche da fermi; in folle lo sterzo
/// non fa nulla oltre alla presa del piede (skill vehicles-boats §11).
///
/// Geometria nello spazio del motore (<see cref="OutboardMotor"/>): R = Rx(−trim)·Ry(sterzo),
/// prima lo sterzo attorno al gambo, poi il trim di tutto il motore attorno al perno sullo specchio.
/// </summary>
public sealed class OutboardDrive
{
    public readonly OutboardDefinition Definition;
    public readonly OutboardMotor Motor;
    public readonly PropulsionUnit Unit;

    /// Angolo di sterzo (rad, positivo = vira a dritta).
    public float SteerAngle { get; private set; }
    /// Trim (rad, positivo = trim out, prua su).
    public float Trim { get; private set; }

    public OutboardDrive(OutboardDefinition definition, OutboardMotor motor)
    {
        Definition = definition;
        Motor = motor;
        Unit = new PropulsionUnit(definition.Engine!, definition.Gearbox!, definition.Propeller!);
        Trim = Mathf.DegToRad(Mathf.Clamp(0f, definition.TrimMinDegrees, definition.TrimMaxDegrees));
    }

    /// Solo collaudo: trim imposto (rad), limitato al campo del trim.
    public void SetTrim(float trim)
        => Trim = Mathf.Clamp(trim, Mathf.DegToRad(Definition.TrimMinDegrees), Mathf.DegToRad(Definition.TrimMaxDegrees));

    public void Step(BoatForces ctx, in BoatCommands cmd, float speedThroughWater)
    {
        float dt = ctx.Dt;
        float maxSteer = Mathf.DegToRad(Definition.MaxSteerDegrees);
        SteerAngle = Mathf.MoveToward(SteerAngle, cmd.Steer * maxSteer, Mathf.DegToRad(Definition.SteerSpeedDegrees) * dt);

        float trimMin = Mathf.DegToRad(Definition.TrimMinDegrees);
        float trimMax = Mathf.DegToRad(Definition.TrimMaxDegrees);
        float trimRate = Mathf.DegToRad(Definition.TrimRateDegrees) * dt;
        if (cmd.AutoTrim)
            Trim = Mathf.MoveToward(Trim, Mathf.DegToRad(AutoTrimDegrees(speedThroughWater)), trimRate);
        else
            Trim = Mathf.Clamp(Trim + cmd.Trim * trimRate, trimMin, trimMax);

        Transform3D xf = ctx.Transform;
        Vector3 propPoint = xf * Motor.PropellerPoint(SteerAngle, Trim);
        Vector3 axis = (xf.Basis * Motor.ThrustAxis(SteerAngle, Trim)).Normalized();
        Unit.Step(ctx, cmd.Throttle, cmd.Running, propPoint, axis);

        // Piede: gambo e skeg poco sopra l'elica, nel piano del motore girato e trimmato.
        if (Definition.LowerUnit is FoilDefinition foil)
        {
            Basis r = xf.Basis * Motor.MotorBasis(SteerAngle, Trim);
            Vector3 up = r.Y.Normalized();
            Vector3 center = propPoint + up * (Definition.Propeller!.Diameter * 0.5f + foil.Span * 0.35f) - axis * 0.05f;
            Foil.Apply(foil, ctx, center, -axis, r.X.Normalized(), up);
        }
    }

    /// <summary>
    /// Assistenza: trim in alla partenza (uscita in planata), poi verso il trim di crociera.
    /// </summary>
    private float AutoTrimDegrees(float speed)
    {
        float lo = Definition.TrimMinDegrees;
        float cruise = Definition.AutoTrimCruiseDegrees;
        return Mathf.Lerp(lo, cruise, Mathf.SmoothStep(Definition.AutoTrimSpeedLow, Definition.AutoTrimSpeedHigh, speed));
    }
}
