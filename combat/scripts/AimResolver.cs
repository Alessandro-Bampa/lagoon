using Godot;
using RidArray = Godot.Collections.Array<Godot.Rid>;

namespace Lagoon;

/// <summary>
/// Geometria della mira e del colpo. Nessuno stato: utility pura, usata dal client per costruire
/// l'intento da inviare all'host (<see cref="ResolveAimPoint"/>) e dall'host per tracciare il
/// colpo vero (<see cref="TraceShot"/>, <see cref="ApplySpread"/>).
///
/// Le due query usano la STESSA maschera (<see cref="CollisionLayers.ShotMask"/>): in prima
/// persona cio' che si guarda e cio' che ferma un colpo coincidono.
/// </summary>
public static class AimResolver
{
    /// Lunghezza del raggio di sonda: abbondante rispetto a qualunque MaxRangeMeters d'arma.
    private const float ProbeLength = 200f;

    /// <summary>
    /// Punto del mondo al CENTRO dello schermo: il primo ostacolo o hitbox lungo l'asse della
    /// camera, escludendo <paramref name="exclude"/> (la propria hitbox). Nel vuoto ritorna un
    /// punto lontano lungo lo stesso asse, cosi' mirare al cielo resta una direzione valida.
    /// </summary>
    public static Vector3 ResolveAimPoint(Camera3D camera, Rid exclude)
    {
        Vector3 from = camera.GlobalPosition;
        Vector3 far = from - camera.GlobalBasis.Z * ProbeLength;

        var query = PhysicsRayQueryParameters3D.Create(from, far, CollisionLayers.ShotMask);
        query.CollideWithAreas = true;
        query.CollideWithBodies = true;
        if (exclude.IsValid)
            query.Exclude = new RidArray { exclude };

        var hit = camera.GetWorld3D().DirectSpaceState.IntersectRay(query);
        return hit.Count > 0 ? (Vector3)hit["position"] : far;
    }

    /// <summary>
    /// Traccia il colpo vero e proprio (host-side). Ritorna la <see cref="HitboxComponent"/>
    /// colpita, o null se il raggio ha incontrato solo geometria o il vuoto; <paramref name="end"/>
    /// riceve sempre il punto finale, cosi' il tracciante puo' essere disegnato in ogni caso.
    /// </summary>
    public static HitboxComponent? TraceShot(
        World3D world, Vector3 origin, Vector3 direction, float maxRange, Rid exclude, out Vector3 end)
    {
        Vector3 target = origin + direction * maxRange;

        var query = PhysicsRayQueryParameters3D.Create(origin, target, CollisionLayers.ShotMask);
        query.CollideWithAreas = true;
        query.CollideWithBodies = true;
        if (exclude.IsValid)
            query.Exclude = new RidArray { exclude };

        var hit = world.DirectSpaceState.IntersectRay(query);
        if (hit.Count == 0)
        {
            end = target;
            return null;
        }

        end = (Vector3)hit["position"];
        return hit["collider"].As<GodotObject>() as HitboxComponent;
    }

    /// <summary>
    /// Applica la dispersione a una direzione: devia di un angolo casuale fino a
    /// <paramref name="spreadDegrees"/> in una direzione azimutale casuale (cono uniforme).
    /// </summary>
    public static Vector3 ApplySpread(Vector3 direction, float spreadDegrees, RandomNumberGenerator rng)
    {
        if (spreadDegrees <= 0f)
            return direction;

        // sqrt sul raggio = distribuzione uniforme sul disco, non addensata al centro.
        float angle = Mathf.DegToRad(spreadDegrees) * Mathf.Sqrt(rng.Randf());
        float azimuth = rng.Randf() * Mathf.Tau;

        // Base ortonormale attorno alla direzione: serve un vettore non parallelo da cui partire.
        Vector3 reference = Mathf.Abs(direction.Dot(Vector3.Up)) > 0.99f ? Vector3.Right : Vector3.Up;
        Vector3 tangent = direction.Cross(reference).Normalized();

        Vector3 axis = tangent.Rotated(direction, azimuth);
        return direction.Rotated(axis, angle).Normalized();
    }
}
