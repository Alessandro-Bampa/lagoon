using System;
using System.Collections.Generic;
using Godot;

namespace Lagoon;

/// <summary>
/// Mesh idrodinamica di uno scafo, nello spazio della barca: chiusa, normali verso l'esterno, separata
/// da quella di resa (nodo <c>HydroHull</c> del modello, skill vehicles-boats §10). Solo dati
/// precalcolati: vertici saldati (l'acqua si campiona una volta per vertice), triangoli, normali, aree.
///
/// L'orientamento si verifica col volume con segno (teorema della divergenza): se i triangoli
/// arrivano col verso opposto si girano tutti. Cosi' non dipende dalla convenzione di avvolgimento
/// dell'importatore.
/// </summary>
public sealed class HydroHull
{
    public readonly Vector3[] Vertices;
    /// Indici a terne nei <see cref="Vertices"/>.
    public readonly int[] Triangles;
    public readonly Vector3[] Normals;
    public readonly float[] Areas;
    public readonly Vector3[] Centroids;
    public readonly float TotalArea;
    /// Volume racchiuso (m³): con lo scafo tutto sotto, la spinta massima e' ρ·g·Volume.
    public readonly float Volume;
    public readonly Aabb Bounds;

    public int TriangleCount => Areas.Length;

    private HydroHull(Vector3[] vertices, int[] triangles)
    {
        Vertices = vertices;
        Triangles = triangles;
        int n = triangles.Length / 3;
        Normals = new Vector3[n];
        Areas = new float[n];
        Centroids = new Vector3[n];

        float signedVolume = 0f;
        for (int i = 0; i < n; i++)
        {
            Vector3 a = vertices[triangles[3 * i]], b = vertices[triangles[3 * i + 1]], c = vertices[triangles[3 * i + 2]];
            Vector3 cross = (b - a).Cross(c - a);
            float len = cross.Length();
            Areas[i] = len * 0.5f;
            Normals[i] = len > 1e-12f ? cross / len : Vector3.Up;
            Centroids[i] = (a + b + c) / 3f;
            signedVolume += Centroids[i].Dot(Normals[i]) * Areas[i] / 3f;
        }

        if (signedVolume < 0f)
        {
            // Avvolgimento opposto: si girano i triangoli (scambio di due indici) e le normali.
            for (int i = 0; i < n; i++)
            {
                (Triangles[3 * i + 1], Triangles[3 * i + 2]) = (Triangles[3 * i + 2], Triangles[3 * i + 1]);
                Normals[i] = -Normals[i];
            }
            signedVolume = -signedVolume;
        }

        Volume = signedVolume;
        float total = 0f;
        foreach (float a in Areas)
            total += a;
        TotalArea = total;

        var bounds = new Aabb(vertices[0], Vector3.Zero);
        foreach (Vector3 v in vertices)
            bounds = bounds.Expand(v);
        Bounds = bounds;
    }

    /// <summary>
    /// Costruisce lo scafo dalle facce di una mesh (<see cref="Mesh.GetFaces"/>: terne di vertici)
    /// portate nello spazio della barca da <paramref name="toBoat"/>. Saldatura al millimetro; i
    /// triangoli degeneri (area &lt; 1e-6 m²) si scartano.
    /// </summary>
    public static HydroHull? FromFaces(Vector3[] faces, Transform3D toBoat)
    {
        var index = new Dictionary<(long, long, long), int>();
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        Span<int> tri = stackalloc int[3];

        for (int f = 0; f + 2 < faces.Length; f += 3)
        {
            for (int k = 0; k < 3; k++)
            {
                Vector3 p = toBoat * faces[f + k];
                var key = ((long)Mathf.Round(p.X * 1000f), (long)Mathf.Round(p.Y * 1000f), (long)Mathf.Round(p.Z * 1000f));
                if (!index.TryGetValue(key, out int id))
                {
                    id = vertices.Count;
                    vertices.Add(p);
                    index[key] = id;
                }
                tri[k] = id;
            }
            if (tri[0] == tri[1] || tri[1] == tri[2] || tri[0] == tri[2])
                continue;
            Vector3 a = vertices[tri[0]], b = vertices[tri[1]], c = vertices[tri[2]];
            if ((b - a).Cross(c - a).Length() * 0.5f < 1e-6f)
                continue;
            triangles.Add(tri[0]);
            triangles.Add(tri[1]);
            triangles.Add(tri[2]);
        }

        if (triangles.Count < 12)
            return null;
        return new HydroHull(vertices.ToArray(), triangles.ToArray());
    }

    // ====================================================================================
    //  Idrostatica in acqua piatta (strumento: equilibrio, GM, curva GZ)
    // ====================================================================================

    /// <summary>Risultato idrostatico in acqua ferma per una posa data.</summary>
    public readonly record struct Statics(
        /// Volume immerso (m³).
        float Volume,
        /// <summary>
        /// Centro di carena, spazio della barca. Valgono le coordinate ORIZZONTALI nel mondo (le uniche
        /// che servono a momento e GZ): con le sole forze verticali la quota non e' definita.
        /// </summary>
        Vector3 Buoyancy,
        /// Area bagnata (m²).
        float WettedArea);

    /// <summary>
    /// Volume e centro di carena con lo scafo nella posa <paramref name="pose"/> (barca → mondo) e
    /// l'acqua piatta a quota 0. Stesso taglio dei triangoli del runtime; il volume e' l'integrale di
    /// superficie della pressione (la spinta verticale divisa per ρ·g), il centro di carena il punto
    /// d'applicazione dei centri di pressione.
    /// </summary>
    public Statics ComputeStatics(Transform3D pose)
    {
        var world = new Vector3[Vertices.Length];
        var depth = new float[Vertices.Length];
        for (int k = 0; k < Vertices.Length; k++)
        {
            world[k] = pose * Vertices[k];
            depth[k] = -world[k].Y;
        }

        Span<Vector3> sub = stackalloc Vector3[4];
        Span<float> subDepth = stackalloc float[4];
        float force = 0f, wetted = 0f;
        Vector3 moment = Vector3.Zero;
        for (int i = 0; i < TriangleCount; i++)
        {
            int ia = Triangles[3 * i], ib = Triangles[3 * i + 1], ic = Triangles[3 * i + 2];
            Vector3 n = pose.Basis * Normals[i];
            int count = HydroMath.ClipSubmerged(world[ia], world[ib], world[ic], depth[ia], depth[ib], depth[ic], sub, subDepth);
            for (int t = 1; t + 1 < count; t++)
            {
                float area = HydroMath.TriangleArea(sub[0], sub[t], sub[t + 1]);
                if (area < 1e-6f)
                    continue;
                wetted += area;
                HydroMath.PressureSplit(sub[0], sub[t], sub[t + 1], subDepth[0], subDepth[t], subDepth[t + 1], area,
                    out float f1, out Vector3 cp1, out float f2, out Vector3 cp2);
                // Solo la componente verticale (vedi HydroSolver): -n.y per unita' di ρ·g.
                float v1 = -f1 * n.Y, v2 = -f2 * n.Y;
                force += v1 + v2;
                moment += cp1 * v1 + cp2 * v2;
            }
        }

        Vector3 center = Mathf.Abs(force) > 1e-9f ? moment / force : Vector3.Zero;
        return new Statics(force, pose.AffineInverse() * center, wetted);
    }

    /// <summary>
    /// Galleggiamento d'equilibrio in acqua piatta: quota dell'origine e assetto (rad, positivo = prua
    /// su) per cui il volume immerso vale <paramref name="volume"/> e il centro di carena sta sulla
    /// verticale del baricentro <paramref name="center"/> (spazio della barca). Bisezione sull'assetto,
    /// ognuna con la propria quota.
    /// </summary>
    public void SolveFloat(float volume, Vector3 center, out float heave, out float trim)
    {
        float Lever(float t, out float h)
        {
            h = SolveHeave(volume, 0f, t);
            Basis b = Basis.FromEuler(new Vector3(t, 0f, 0f));
            var s = ComputeStatics(new Transform3D(b, new Vector3(0f, h, 0f)));
            // Centro di carena a poppavia del baricentro (z maggiore): la prua deve scendere.
            return (b * s.Buoyancy).Z - (b * center).Z;
        }

        float lo = Mathf.DegToRad(-20f), hi = Mathf.DegToRad(20f);
        float fLo = Lever(lo, out float hLo), fHi = Lever(hi, out float hHi);
        if (fLo > 0f || fHi < 0f)
        {
            // Nessun equilibrio nel campo: si resta al bordo piu' vicino.
            bool low = Mathf.Abs(fLo) < Mathf.Abs(fHi);
            trim = low ? lo : hi;
            heave = low ? hLo : hHi;
            return;
        }
        heave = 0f;
        for (int i = 0; i < 20; i++)
        {
            float mid = 0.5f * (lo + hi);
            if (Lever(mid, out heave) > 0f)
                hi = mid;
            else
                lo = mid;
        }
        trim = 0.5f * (lo + hi);
        Lever(trim, out heave);
    }

    /// <summary>
    /// Braccio raddrizzante GZ (m) in acqua piatta allo sbandamento <paramref name="heel"/> (rad,
    /// positivo = dritta giu'), a dislocamento costante, col baricentro <paramref name="center"/>.
    /// Positivo = la barca torna dritta. GM ≈ GZ / sin φ per piccoli angoli.
    /// </summary>
    public float RightingArm(float volume, Vector3 center, float heel)
    {
        float heave = SolveHeave(volume, heel, 0f);
        Basis basis = Basis.FromEuler(new Vector3(0f, 0f, -heel));
        var statics = ComputeStatics(new Transform3D(basis, new Vector3(0f, heave, 0f)));
        return (basis * statics.Buoyancy).X - (basis * center).X;
    }

    /// <summary>
    /// Quota dell'origine della barca (sopra l'acqua piatta a 0) per cui il volume immerso vale
    /// <paramref name="volume"/>, con sbandamento e assetto dati (rad). Bisezione: il volume cresce
    /// in modo monotono abbassando lo scafo.
    /// </summary>
    public float SolveHeave(float volume, float heel, float trim)
    {
        Basis basis = Basis.FromEuler(new Vector3(trim, 0f, -heel));
        float hi = -Bounds.Position.Y + 0.01f;          // tutto fuori
        float lo = -(Bounds.Position.Y + Bounds.Size.Y) - 0.01f; // tutto sotto
        for (int i = 0; i < 30; i++)
        {
            float mid = 0.5f * (lo + hi);
            float v = ComputeStatics(new Transform3D(basis, new Vector3(0f, mid, 0f))).Volume;
            if (v > volume)
                lo = mid;
            else
                hi = mid;
        }
        return 0.5f * (lo + hi);
    }
}
