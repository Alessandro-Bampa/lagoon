using Godot;
using RidArray = Godot.Collections.Array<Godot.Rid>;

namespace Lagoon;

/// <summary>
/// Traduce la posizione del cursore in un punto di mira nel mondo. Nessuno stato: utility pura,
/// usata dal client per costruire l'intento da inviare all'host e dal reticolo per sapere a che
/// distanza sta mirando.
///
/// NOTA sulla camera ORTOGONALE (vedi IsometricCamera): con una proiezione ortogonale
/// <c>ProjectRayNormal</c> restituisce la STESSA direzione per ogni pixel dello schermo (l'asse -Z
/// della camera); varia solo l'origine. Conseguenza accettata in fase di prototipo: il raggio puo'
/// legittimamente agganciare un bersaglio che, in coordinate mondo, sta "dietro" al giocatore, ma
/// che sullo schermo e' esattamente sotto al cursore. E' il comportamento intuitivo per chi gioca —
/// si mira a quel che si vede — e l'host valida comunque la distanza massima.
/// </summary>
public static class AimResolver
{
    /// Lunghezza del raggio di sonda: abbondante rispetto a qualunque MaxRangeMeters d'arma.
    private const float ProbeLength = 200f;

    /// Altezza del piano di ripiego: la mira "vola" all'altezza del petto di un bersaglio in piedi,
    /// non a terra, cosi' puntare il vuoto non fa sparare nel pavimento.
    public const float ChestHeight = 1.1f;

    /// <summary>
    /// Punto del mondo sotto al cursore. Prova prima un raycast fisico contro mondo e hitbox
    /// (escludendo <paramref name="exclude"/>, tipicamente la propria hitbox); se non colpisce nulla
    /// ripiega sull'intersezione col piano orizzontale a <see cref="ChestHeight"/>.
    /// </summary>
    public static Vector3 ResolveAimPoint(Camera3D camera, Vector2 screenPosition, Rid exclude)
    {
        Vector3 from = camera.ProjectRayOrigin(screenPosition);
        Vector3 dir = camera.ProjectRayNormal(screenPosition);

        var query = PhysicsRayQueryParameters3D.Create(from, from + dir * ProbeLength);
        query.CollisionMask = CollisionLayers.AimMask;
        query.CollideWithAreas = true;
        query.CollideWithBodies = true;
        if (exclude.IsValid)
            query.Exclude = new RidArray { exclude };

        var hit = camera.GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (hit.Count > 0)
            return (Vector3)hit["position"];

        var plane = new Plane(Vector3.Up, ChestHeight);
        Vector3? onPlane = plane.IntersectsRay(from, dir);
        return onPlane ?? from + dir * 50f;
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

        var query = PhysicsRayQueryParameters3D.Create(origin, target);
        query.CollisionMask = CollisionLayers.AimMask;
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
