using System;
using Godot;

namespace Lagoon;

/// <summary>
/// Geometria del taglio alla linea d'acqua e del centro di pressione (skill vehicles-boats §10).
/// Funzioni pure, senza allocazioni: le usano il solutore in gioco, l'idrostatica in acqua piatta e
/// le prove (<see cref="HydroSelfTest"/>).
/// </summary>
public static class HydroMath
{
    /// Sotto questa profondita' (m) un vertice conta come emerso: evita triangoli degeneri.
    public const float DepthEpsilon = 1e-5f;

    public static float TriangleArea(Vector3 a, Vector3 b, Vector3 c) => (b - a).Cross(c - a).Length() * 0.5f;

    /// <summary>
    /// Ritaglio di Sutherland-Hodgman del triangolo (a, b, c) contro il semispazio immerso (d &gt; 0)
    /// o emerso (d ≤ 0). Restituisce il numero di vertici del poligono (0, 3 o 4) in
    /// <paramref name="outPos"/>/<paramref name="outDepth"/> (e, se non vuoti, le velocita' d'acqua
    /// interpolate in <paramref name="outVel"/>), nell'ordine originale: l'orientamento si conserva.
    /// Il punto di taglio ha profondita' 0 ed e' continuo nelle profondita': piccole variazioni del
    /// livello non fanno saltare l'area.
    /// </summary>
    public static int Clip(Vector3 a, Vector3 b, Vector3 c, float da, float db, float dc,
        Vector3 va, Vector3 vb, Vector3 vc, bool submerged,
        Span<Vector3> outPos, Span<float> outDepth, Span<Vector3> outVel)
    {
        bool withVel = outVel.Length > 0;
        int count = 0;
        for (int e = 0; e < 3; e++)
        {
            Vector3 p, q, vp, vq;
            float dp, dq;
            switch (e)
            {
                case 0: p = a; q = b; dp = da; dq = db; vp = va; vq = vb; break;
                case 1: p = b; q = c; dp = db; dq = dc; vp = vb; vq = vc; break;
                default: p = c; q = a; dp = dc; dq = da; vp = vc; vq = va; break;
            }
            bool pin = dp > DepthEpsilon;
            bool qin = dq > DepthEpsilon;
            if (pin == submerged)
            {
                outPos[count] = p;
                outDepth[count] = submerged ? dp : Mathf.Min(dp, 0f);
                if (withVel)
                    outVel[count] = vp;
                count++;
            }
            if (pin != qin)
            {
                float t = dp / (dp - dq);
                outPos[count] = p + (q - p) * t;
                outDepth[count] = 0f;
                if (withVel)
                    outVel[count] = vp + (vq - vp) * t;
                count++;
            }
        }
        return count < 3 ? 0 : count;
    }

    /// Ritaglio della parte immersa senza velocita' (idrostatica pura).
    public static int ClipSubmerged(Vector3 a, Vector3 b, Vector3 c, float da, float db, float dc,
        Span<Vector3> outPos, Span<float> outDepth)
        => Clip(a, b, c, da, db, dc, Vector3.Zero, Vector3.Zero, Vector3.Zero, true, outPos, outDepth, Span<Vector3>.Empty);

    /// <summary>
    /// Pressione idrostatica su un triangolo immerso, in unita' di ρ·g (cioe' m³): la si divide nei due
    /// triangoli con base orizzontale e se ne danno intensita' e CENTRO DI PRESSIONE, che sta sempre
    /// sotto il baricentro (la pressione cresce con la profondita'). Applicare la forza nel baricentro
    /// darebbe la spinta giusta ma una coppia sbagliata: lo scafo s'inclinerebbe in acqua piatta.
    /// <paramref name="area"/> e' l'area del triangolo; le due forze vanno lungo −normale.
    /// </summary>
    public static void PressureSplit(Vector3 p0, Vector3 p1, Vector3 p2, float d0, float d1, float d2, float area,
        out float force1, out Vector3 cp1, out float force2, out Vector3 cp2)
    {
        // a = il piu' superficiale, c = il piu' profondo.
        Vector3 a = p0, b = p1, c = p2;
        float da = d0, db = d1, dc = d2;
        if (da > db) { (a, b) = (b, a); (da, db) = (db, da); }
        if (db > dc) { (b, c) = (c, b); (db, dc) = (dc, db); }
        if (da > db) { (a, b) = (b, a); (da, db) = (db, da); }

        if (dc - da < 1e-4f)
        {
            force1 = area * (da + db + dc) / 3f;
            cp1 = (a + b + c) / 3f;
            force2 = 0f;
            cp2 = cp1;
            return;
        }

        float s = (db - da) / (dc - da);
        Vector3 m = a + (c - a) * s;
        Vector3 mb = (b + m) * 0.5f;

        // Superiore (a, b, m): vertice in alto a, base orizzontale b-m.
        float areaUp = area * s;
        float h0 = Mathf.Max(da, 0f), hUp = db - da;
        float tUp = (4f * h0 + 3f * hUp) / Mathf.Max(6f * h0 + 4f * hUp, 1e-9f);
        cp1 = a + (mb - a) * tUp;
        force1 = areaUp * (da + 2f * db) / 3f;

        // Inferiore (b, m, c): base orizzontale in alto b-m, vertice in basso c.
        float areaDown = area - areaUp;
        float h1 = Mathf.Max(db, 0f), hDown = dc - db;
        float tDown = (2f * h1 + hDown) / Mathf.Max(6f * h1 + 2f * hDown, 1e-9f);
        cp2 = mb + (c - mb) * tDown;
        force2 = areaDown * (2f * db + dc) / 3f;
    }

    /// Coefficiente d'attrito della linea di correlazione ITTC 1957.
    public static float IttcFriction(float reynolds)
    {
        float rn = Mathf.Max(reynolds, 1e5f);
        float lg = Mathf.Log(rn) / Mathf.Log(10f) - 2f;
        return 0.075f / (lg * lg);
    }

    /// <summary>
    /// Resistenza d'onda dei dislocanti pesanti, R_w / (Δ·g), in funzione del numero di Froude sulla
    /// lunghezza bagnata (tabella delle specifiche, interpolazione lineare; oltre 0,6 costante).
    /// </summary>
    public static float WaveResistance(float froude)
    {
        ReadOnlySpan<float> table = stackalloc float[] { 0f, 0.0001f, 0.0005f, 0.001f, 0.002f, 0.005f, 0.010f, 0.020f, 0.040f, 0.065f, 0.085f, 0.100f };
        // table[i] corrisponde a Fn = 0,05·(i + 1) per i ≥ 1; table[0] a Fn = 0,05.
        float x = froude / 0.05f - 1f;
        if (x <= 0f)
            return 0f;
        int i = Mathf.Min((int)x, table.Length - 2);
        if (x >= table.Length - 1)
            return table[^1];
        return Mathf.Lerp(table[i], table[i + 1], x - i);
    }
}
