using Godot;

namespace Lagoon;

/// <summary>
/// Forza di un profilo immerso (timone, piede del fuoribordo, chiglia), modello ispirato a MMG
/// (skill vehicles-boats §11): forza normale con la pendenza di Fujii, stallo, flusso inverso,
/// resistenza di profilo; area efficace proporzionale alla parte d'apertura immersa. Un timone fuori
/// dall'acqua non fa nulla; senza flusso non fa nulla.
/// </summary>
public static class Foil
{
    /// <summary>
    /// Applica la forza a <paramref name="ctx"/>. Geometria nel mondo: <paramref name="center"/> =
    /// centro di pressione, <paramref name="chordAft"/> = dal bordo d'attacco a quello d'uscita,
    /// <paramref name="normal"/> = normale alla pala, <paramref name="spanUp"/> = apertura verso l'alto.
    /// <paramref name="extraFlowAft"/> = velocita' aggiunta dalla scia dell'elica, lungo
    /// <paramref name="washDirection"/> (verso poppa). Restituisce la forza (telemetria).
    /// </summary>
    public static Vector3 Apply(FoilDefinition def, BoatForces ctx, Vector3 center, Vector3 chordAft,
        Vector3 normal, Vector3 spanUp, float extraFlowAft = 0f, Vector3 washDirection = default)
    {
        int segments = Mathf.Max(def.ChordSegments, 1);
        if (segments == 1 || def.Chord <= 0f)
            return ApplySegment(def, 1f, ctx, center, chordAft, normal, spanUp, extraFlowAft, washDirection);

        Vector3 total = Vector3.Zero;
        for (int i = 0; i < segments; i++)
        {
            float u = (i + 0.5f) / segments - 0.5f;
            total += ApplySegment(def, 1f / segments, ctx, center + chordAft * (u * def.Chord), chordAft, normal, spanUp,
                extraFlowAft, washDirection);
        }
        return total;
    }

    private static Vector3 ApplySegment(FoilDefinition def, float areaFraction, BoatForces ctx, Vector3 center,
        Vector3 chordAft, Vector3 normal, Vector3 spanUp, float extraFlowAft, Vector3 washDirection)
    {
        float half = def.Span * 0.5f;
        Vector3 top = center + spanUp * half;
        Vector3 bottom = center - spanUp * half;
        float dTop = ctx.Depth(top);
        float dBottom = ctx.Depth(bottom);
        float fraction;
        if (dTop >= 0f)
            fraction = 1f;
        else if (dBottom <= 0f)
            fraction = 0f;
        else
            fraction = Mathf.Clamp(dBottom / (dBottom - dTop), 0f, 1f);
        if (fraction <= 0f)
            return Vector3.Zero;

        // Centro della parte immersa.
        Vector3 at = bottom + spanUp * (def.Span * fraction * 0.5f);
        Vector3 flow = ctx.WaterVelocity(at) - ctx.PointVelocity(at);
        if (extraFlowAft > 0f)
            flow += washDirection * extraFlowAft;

        float ax = flow.Dot(chordAft);
        float lat = flow.Dot(normal);
        if (def.Kind == FoilKind.Rudder)
            lat *= def.LateralShielding;
        Vector3 inPlane = chordAft * ax + normal * lat;
        float speed2 = inPlane.LengthSquared();
        if (speed2 < 1e-6f)
            return Vector3.Zero;

        float alpha = Mathf.Atan2(lat, ax);
        float rho = ctx.Water?.Density ?? 1025f;
        float area = def.Area * areaFraction * fraction;
        float q = 0.5f * rho * area * speed2;
        Vector3 force = q * NormalCoefficient(def, alpha) * normal
            + q * def.DragCoefficient * inPlane / Mathf.Sqrt(speed2);
        ctx.AddAt(force, at);
        return force;
    }

    /// <summary>
    /// C_N(α): f_α·sin α attaccato (Fujii: f_α = 6,13·Λ/(2,25+Λ)), poi in 5° verso 1,1·sin α
    /// (stallo); col flusso dal bordo d'uscita stessa legge su 180° − |α|, ridotta.
    /// </summary>
    public static float NormalCoefficient(FoilDefinition def, float alpha)
    {
        float lambda = Mathf.Max(def.AspectRatio, 0.05f);
        float slope = 6.13f * lambda / (2.25f + lambda);
        float a = Mathf.Abs(alpha);
        float sign = alpha >= 0f ? 1f : -1f;
        float reverse = 1f;
        if (a > Mathf.Pi * 0.5f)
        {
            a = Mathf.Pi - a;
            reverse = def.ReverseEfficiency;
        }
        float stall = Mathf.DegToRad(def.StallDegrees);
        float blend = Mathf.SmoothStep(stall, stall + Mathf.DegToRad(5f), a);
        float attached = slope * Mathf.Sin(a);
        float stalled = 1.1f * Mathf.Sin(a);
        return Mathf.Lerp(attached, stalled, blend) * reverse * sign;
    }
}
