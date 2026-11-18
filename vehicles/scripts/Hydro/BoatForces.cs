using Godot;

namespace Lagoon;

/// <summary>
/// Stato del corpo e accumulatore di forza e coppia per un passo fisico della barca (solo host).
///
/// La coppia si accumula QUI, rispetto al baricentro: <c>τ += (p − G) × F</c>, e al corpo si applicano
/// solo forza centrale e coppia. In Godot il punto di <c>apply_force</c> e' uno spostamento
/// dall'ORIGINE del corpo in assi globali, non un punto del mondo ne' un punto locale: e' una fonte
/// classica di errori, cosi' non la si usa affatto.
/// </summary>
public sealed class BoatForces
{
    public Transform3D Transform;
    /// Baricentro in coordinate mondo.
    public Vector3 Com;
    public Vector3 LinearVelocity;
    public Vector3 AngularVelocity;
    public float Dt;
    public float Mass;
    /// Momenti principali d'inerzia (assi della barca).
    public Vector3 Inertia;
    public Vector3 Gravity;
    public WaterVolume? Water;

    public Vector3 Force;
    public Vector3 Torque;

    public Basis Basis => Transform.Basis;
    public Vector3 Forward => -Transform.Basis.Z;
    public Vector3 Right => Transform.Basis.X;
    public Vector3 Up => Transform.Basis.Y;

    public void Reset()
    {
        Force = Vector3.Zero;
        Torque = Vector3.Zero;
    }

    /// Forza <paramref name="force"/> applicata nel punto del mondo <paramref name="point"/>.
    public void AddAt(Vector3 force, Vector3 point)
    {
        Force += force;
        Torque += (point - Com).Cross(force);
    }

    /// Velocita' di un punto del mondo solidale con la barca.
    public Vector3 PointVelocity(Vector3 point) => LinearVelocity + AngularVelocity.Cross(point - Com);

    /// Velocita' dell'acqua (onde + corrente) in un punto; zero fuori dall'acqua.
    public Vector3 WaterVelocity(Vector3 point)
    {
        if (Water == null || !Water.Contains(point))
            return Vector3.Zero;
        return Water.Sample(point).Velocity;
    }

    /// Profondita' del punto sotto la superficie con le onde (negativa = fuori dall'acqua).
    public float Depth(Vector3 point)
    {
        if (Water == null || !Water.Contains(point))
            return -1000f;
        return Water.Sample(point).Height - point.Y;
    }
}
