using System;
using Godot;

namespace Lagoon;

/// <summary>
/// Verifiche numeriche del nucleo idrodinamico, chiamate da <c>tools/boat_trials.gd</c> (collaudo, non
/// una suite di test: CLAUDE.md §6). Ogni riga e' "OK ..." o "FALLITO ...".
/// </summary>
[GlobalClass]
public partial class HydroSelfTest : RefCounted
{
    public string[] Run()
    {
        var lines = new System.Collections.Generic.List<string>();
        void Check(bool ok, string what) => lines.Add((ok ? "OK      " : "FALLITO ") + what);

        // --- Centro di pressione: casi limite (skill vehicles-boats §10) ---------------------
        // Vertice in superficie, base orizzontale in basso: CP a 3/4 della mediana dal vertice.
        {
            Vector3 a = new(0, 0, 0), b = new(-1, -1, 0), c = new(1, -1, 0);
            HydroMath.PressureSplit(a, b, c, 0f, 1f, 1f, 1f, out float f1, out Vector3 cp1, out float f2, out Vector3 cp2);
            Vector3 cp = (cp1 * f1 + cp2 * f2) / (f1 + f2);
            Check(Mathf.Abs(cp.Y + 0.75f) < 1e-3f, $"CP vertice in superficie: y = {cp.Y:0.000} (atteso -0,750)");
        }
        // Base in superficie, vertice in basso: CP a meta' della mediana dalla base.
        {
            Vector3 a = new(-1, 0, 0), b = new(1, 0, 0), c = new(0, -1, 0);
            HydroMath.PressureSplit(a, b, c, 0f, 0f, 1f, 1f, out float f1, out Vector3 cp1, out float f2, out Vector3 cp2);
            Vector3 cp = (cp1 * f1 + cp2 * f2) / (f1 + f2);
            Check(Mathf.Abs(cp.Y + 0.5f) < 1e-3f, $"CP base in superficie: y = {cp.Y:0.000} (atteso -0,500)");
        }
        // Molto profondo: CP = baricentro.
        {
            Vector3 a = new(0, -1000, 0), b = new(-1, -1001, 0), c = new(1, -1001, 0);
            HydroMath.PressureSplit(a, b, c, 1000f, 1001f, 1001f, 1f, out float f1, out Vector3 cp1, out float f2, out Vector3 cp2);
            Vector3 cp = (cp1 * f1 + cp2 * f2) / (f1 + f2);
            Check(Mathf.Abs(cp.Y + 1000.6667f) < 2e-3f, $"CP in profondita' = baricentro: y = {cp.Y:0.0000} (atteso -1000,6667)");
        }
        // Triangolo inclinato qualunque contro un'integrazione numerica.
        {
            var rng = new Random(7);
            Vector3 a = new(0.1f, -0.2f, 0.3f), b = new(-0.7f, -1.3f, 0.1f), c = new(0.9f, -0.8f, -0.6f);
            HydroMath.PressureSplit(a, b, c, -a.Y, -b.Y, -c.Y, HydroMath.TriangleArea(a, b, c),
                out float f1, out Vector3 cp1, out float f2, out Vector3 cp2);
            Vector3 cp = (cp1 * f1 + cp2 * f2) / (f1 + f2);
            double sw = 0; Vector3 sp = Vector3.Zero;
            for (int i = 0; i < 400000; i++)
            {
                double u = rng.NextDouble(), v = rng.NextDouble();
                if (u + v > 1) { u = 1 - u; v = 1 - v; }
                Vector3 p = a + (b - a) * (float)u + (c - a) * (float)v;
                sw += -p.Y;
                sp += p * -p.Y;
            }
            Vector3 mc = sp / (float)sw;
            Check(cp.DistanceTo(mc) < 5e-3f, $"CP triangolo inclinato: {cp} contro Monte Carlo {mc}");
        }

        // --- Taglio continuo: il livello sale a passi di 1 mm, l'area non salta -------------
        {
            Vector3 a = new(0, -0.3f, 0), b = new(1, 0.2f, 0.2f), c = new(0.3f, 0.5f, 1f);
            Span<Vector3> poly = stackalloc Vector3[4];
            Span<float> depth = stackalloc float[4];
            float total = HydroMath.TriangleArea(a, b, c);
            float prev = 0f, worst = 0f;
            for (int i = 0; i <= 1000; i++)
            {
                float level = -0.35f + i * 0.001f;
                int n = HydroMath.ClipSubmerged(a, b, c, level - a.Y, level - b.Y, level - c.Y, poly, depth);
                float area = 0f;
                for (int t = 1; t + 1 < n; t++)
                    area += HydroMath.TriangleArea(poly[0], poly[t], poly[t + 1]);
                if (i > 0)
                    worst = Mathf.Max(worst, Mathf.Abs(area - prev) / total);
                prev = area;
            }
            Check(worst < 0.01f, $"Taglio continuo: salto massimo {worst * 100f:0.00}% dell'area per 1 mm");
        }

        // --- Cubo di 1 m, densita' 500: galleggia a 0,5 m ---------------------------------
        {
            HydroHull? cube = HydroHull.FromFaces(new BoxMesh { Size = Vector3.One }.GetFaces(), Transform3D.Identity);
            if (cube == null)
            {
                Check(false, "Cubo: mesh non valida");
            }
            else
            {
                float heave = cube.SolveHeave(0.5f, 0f, 0f);
                var statics = cube.ComputeStatics(new Transform3D(Basis.Identity, new Vector3(0, heave, 0)));
                float draft = 0.5f - heave;
                Check(Mathf.Abs(cube.Volume - 1f) < 1e-3f, $"Cubo: volume {cube.Volume:0.0000} m³");
                Check(Mathf.Abs(draft - 0.5f) < 1e-3f, $"Cubo: pescaggio {draft:0.0000} m (atteso 0,5000)");
                // Solo X e Z: con le sole forze verticali la quota del punto d'applicazione non e'
                // definita (la linea d'azione e' verticale), e non serve ne' al momento ne' al GZ.
                Check(Mathf.Abs(statics.Buoyancy.X) < 1e-3f && Mathf.Abs(statics.Buoyancy.Z) < 1e-3f,
                    $"Cubo: centro di carena sull'asse ({statics.Buoyancy.X:0.0000}, {statics.Buoyancy.Z:0.0000})");
            }
            // Chiatta 2 × 0,5 × 1 m, densita' 500, G al centro: GM = KB + BM − KG = 1,21 m. A 10°
            // (bordo non ancora in acqua) GZ = sin φ·(GM + ½·BM·tan²φ) = 0,2137 m (formula wall-sided).
            HydroHull? barge = HydroHull.FromFaces(new BoxMesh { Size = new Vector3(2f, 0.5f, 1f) }.GetFaces(), Transform3D.Identity);
            if (barge != null)
            {
                float heel = Mathf.DegToRad(10f);
                float h10 = barge.SolveHeave(0.5f, heel, 0f);
                Basis b10 = Basis.FromEuler(new Vector3(0f, 0f, -heel));
                var s10 = barge.ComputeStatics(new Transform3D(b10, new Vector3(0, h10, 0)));
                float gz = (b10 * s10.Buoyancy).X;
                Check(Mathf.Abs(gz - 0.2137f) < 0.005f, $"Chiatta sbandata di 10°: GZ {gz:0.0000} m (atteso 0,2137)");
            }
        }

        // --- Segni del timone: a dritta = forza verso sinistra -----------------------------
        {
            var foil = new FoilDefinition { Area = 0.2f, Span = 0.5f };
            float delta = Mathf.DegToRad(15f);
            var r = new Basis(Vector3.Up, delta);
            // Acqua che scorre verso poppa (+Z) rispetto al timone: barca che avanza.
            Vector3 flow = new(0, 0, 3f);
            float alpha = Mathf.Atan2(flow.Dot(r.X), flow.Dot(r.Z));
            float side = (Foil.NormalCoefficient(foil, alpha) * r.X).X;
            Check(side < 0f, $"Timone a dritta: forza laterale {side:0.00} (attesa verso sinistra, < 0)");
        }

        // --- Fuoribordo: sterzo positivo spinge a sinistra, trim out spinge in basso -------
        {
            Vector3 axis = new Basis(Vector3.Right, -Mathf.DegToRad(8f)) * new Basis(Vector3.Up, Mathf.DegToRad(20f)) * Vector3.Forward;
            Check(axis.X < 0f, $"Sterzo a dritta: spinta laterale {axis.X:0.000} (attesa < 0, poppa a sinistra)");
            Check(axis.Y < 0f, $"Trim out: componente verticale {axis.Y:0.000} (attesa < 0)");
        }

        // --- Elica: tiro a punto fisso del 30 kW di riferimento ----------------------------
        {
            var prop = new PropellerDefinition { Diameter = 0.33f, Pitch = 0.33f, Kt0 = 0.45f, Kq0 = 0.060f, J0 = 1.1f };
            // Piena potenza imposta: n tale che 2π·n·Q(n) = 30 kW, Q = ρ n² D⁵ Kq0.
            float rho = 1025f;
            float d = prop.Diameter;
            float n = Mathf.Pow(30000f / (Mathf.Tau * rho * Mathf.Pow(d, 5f) * prop.Kq0), 1f / 3f);
            float thrust = rho * n * n * Mathf.Pow(d, 4f) * prop.Kt0;
            float kgf = thrust / 9.81f;
            Check(Mathf.Abs(kgf - 410f) < 0.03f * 410f, $"Elica 30 kW a punto fisso: {kgf:0} kgf a {n:0.0} giri/s (atteso 410 ±3%)");
        }

        return lines.ToArray();
    }
}
