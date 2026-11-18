using Godot;

namespace Lagoon;

/// <summary>
/// Galleggiamento per punti, condiviso da barche (<see cref="BoatController"/>) e oggetti
/// (<see cref="FloatingBody"/>). Si chiama SOLO dove gira la fisica vera, cioe' sull'host (CLAUDE.md §3).
///
/// Ogni punto riceve:
/// <list type="bullet">
/// <item><b>spinta</b> proporzionale all'immersione sotto la superficie CON le onde, saturata a
/// <see cref="Settings.MaxSubmerge"/>. Auto-bilanciata: a immersione media 1/<c>BuoyancyRatio</c> la
/// spinta pareggia il peso, qualunque siano massa e numero di punti;</item>
/// <item><b>trascinamento</b> rispetto all'ACQUA, non al mondo: la velocita' relativa e' quella del
/// punto meno la velocita' orbitale delle onde. Smorzare rispetto al mondo (come prima) frenerebbe la
/// barca anche quando sale con l'onda; cosi' invece la segue, e fuori dall'acqua (immersione 0) non
/// c'e' piu' nessuna forza: su un'onda presa veloce lo scafo vola e ricade.</item>
/// </list>
/// </summary>
public static class Buoyancy
{
    public readonly record struct Settings(
        /// Immersione (m) oltre la quale la spinta di un punto non cresce piu'.
        float MaxSubmerge,
        /// 2 = galleggia immerso a meta' di MaxSubmerge; piu' alto = piu' leggero.
        float BuoyancyRatio,
        /// Smorzamento verticale relativo all'acqua (1/s, per unita' di massa).
        float VerticalDrag,
        /// Trascinamento orizzontale relativo all'acqua (1/s). La barca usa 0: ha la sua chiglia.
        float HorizontalDrag);

    /// I <c>Marker3D</c> figli diretti di <paramref name="parent"/>: i punti di galleggiamento.
    public static Node3D[] CollectPoints(Node? parent)
    {
        var list = new System.Collections.Generic.List<Node3D>();
        if (parent != null)
            foreach (Node child in parent.GetChildren())
                if (child is Marker3D marker)
                    list.Add(marker);
        return list.ToArray();
    }

    /// <summary>
    /// Applica spinta e trascinamento ai punti dati (ne legge la posizione globale). Restituisce la frazione
    /// media d'immersione (0 = fuori dall'acqua, 1 = tutti i punti oltre MaxSubmerge).
    /// </summary>
    public static float Apply(RigidBody3D body, Node3D[] points, WaterVolume water, float gravity,
        in Settings settings)
    {
        int n = points.Length;
        if (n == 0)
            return 0f;

        float perPoint = body.Mass * gravity / n * settings.BuoyancyRatio;
        float massPerPoint = body.Mass / n;
        Vector3 origin = body.GlobalPosition;
        Vector3 com = body.GlobalTransform * body.CenterOfMass;
        float submergedSum = 0f;

        for (int i = 0; i < n; i++)
        {
            Vector3 point = points[i].GlobalPosition;
            if (!water.Contains(point))
                continue;

            WaterSample s = water.Sample(point);
            float depth = Mathf.Clamp(s.Height - point.Y, 0f, settings.MaxSubmerge);
            if (depth <= 0f)
                continue;

            float frac = depth / settings.MaxSubmerge;
            submergedSum += frac;

            Vector3 pointVelocity = body.LinearVelocity + body.AngularVelocity.Cross(point - com);
            Vector3 rel = pointVelocity - s.Velocity;

            Vector3 force = Vector3.Up * frac * perPoint;
            force += Vector3.Down * rel.Y * massPerPoint * settings.VerticalDrag * frac;
            if (settings.HorizontalDrag > 0f)
                force -= new Vector3(rel.X, 0f, rel.Z) * massPerPoint * settings.HorizontalDrag * frac;

            // Il secondo argomento di ApplyForce e' un OFFSET dall'origine del corpo in orientamento
            // globale, non un punto globale (in Godot non esiste ApplyForceAtPosition).
            body.ApplyForce(force, point - origin);
        }

        return submergedSum / n;
    }
}
