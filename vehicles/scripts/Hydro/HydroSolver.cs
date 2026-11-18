using System;
using Godot;

namespace Lagoon;

/// <summary>
/// Forze fra scafo e acqua integrate sui triangoli della <see cref="HydroHull"/> (modello di Kerner,
/// skill vehicles-boats §10). Solo host. Per ogni passo:
/// <list type="number">
/// <item>vertici nel mondo e acqua campionata UNA volta per vertice (quota con le onde, velocita'
/// orbitale + corrente);</item>
/// <item>taglio dei triangoli alla linea d'acqua; per ogni pezzo immerso: spinta idrostatica (solo
/// verticale, nel centro di pressione), attrito ITTC, pressione/suzione (e' la planata: nessuna forza
/// "aquaplane" inventata); per ogni triangolo: impatto (slamming), con tetto globale; per i pezzi
/// emersi: aria e vento;</item>
/// <item>termini globali: resistenza d'onda (muro della velocita' di carena) e uno strato di
/// smorzamento di sicurezza piccolo, scalato con l'area immersa.</item>
/// </list>
/// La massa aggiunta e la sanificazione le applica <see cref="BoatPhysics"/> alla somma di TUTTE le
/// forze (anche motore e appendici). Nessuna allocazione per passo.
/// </summary>
public sealed class HydroSolver
{
    public const float AirDensity = 1.225f;
    public const float Viscosity = 1.19e-6f;

    private readonly HydroHull _hull;
    private readonly Vector3[] _world;
    private readonly float[] _depth;
    private readonly Vector3[] _waterVel;
    private readonly Vector2[] _offset;
    private readonly float[] _prevArea;
    private readonly float[] _prevVn;
    private bool _hasPrev;

    private readonly Vector3[] _poly = new Vector3[4];
    private readonly float[] _polyDepth = new float[4];
    private readonly Vector3[] _polyVel = new Vector3[4];

    // --- Risultati del passo (telemetria e altri moduli) -------------------------------------
    /// Area immersa (m²).
    public float ImmersedArea { get; private set; }
    /// Volume immerso (m³) dalla spinta verticale.
    public float DisplacedVolume { get; private set; }
    /// Lunghezza bagnata lungo l'asse prua-poppa (m).
    public float WettedLength { get; private set; }
    /// Velocita' orizzontale del baricentro rispetto all'acqua (m/s).
    public float SpeedThroughWater { get; private set; }
    public float Froude { get; private set; }
    public float FroudeVolumetric { get; private set; }
    /// Componenti di resistenza lungo la direzione del moto (N, positive = frenano).
    public float ViscousDrag { get; private set; }
    public float PressureDrag { get; private set; }
    public float WaveDrag { get; private set; }
    /// Picco d'impatto del passo in multipli di m·g (0 = nessuno).
    public float SlamG { get; private set; }
    /// Velocita' media dell'acqua sotto lo scafo (onde + corrente).
    public Vector3 MeanWaterVelocity { get; private set; }
    /// Quota media della superficie sotto lo scafo.
    public float MeanWaterHeight { get; private set; }
    /// Forze resistive del passo (per il limite di non inversione di BoatPhysics).
    public Vector3 ResistiveForce { get; private set; }

    public HydroHull Hull => _hull;

    public HydroSolver(HydroHull hull)
    {
        _hull = hull;
        _world = new Vector3[hull.Vertices.Length];
        _depth = new float[hull.Vertices.Length];
        _waterVel = new Vector3[hull.Vertices.Length];
        _offset = new Vector2[hull.Vertices.Length];
        _prevArea = new float[hull.TriangleCount];
        _prevVn = new float[hull.TriangleCount];
    }

    /// Dimentica la storia degli impatti (teletrasporto, spawn).
    public void ResetHistory() => _hasPrev = false;

    public void Step(BoatForces ctx, HydroCoefficients k, Vector3 windAt10m, float density)
    {
        WaterVolume? water = ctx.Water;
        float rho = density;
        float g = -ctx.Gravity.Y;
        Transform3D xf = ctx.Transform;
        Vector3 forward = ctx.Forward;

        // --- 1) Vertici e acqua --------------------------------------------------------------
        float minAlong = float.MaxValue, maxAlong = float.MinValue;
        Vector3 waterVelSum = Vector3.Zero;
        float waterHeightSum = 0f;
        int wet = 0;
        for (int v = 0; v < _world.Length; v++)
        {
            Vector3 p = xf * _hull.Vertices[v];
            _world[v] = p;
            if (water != null && water.Contains(p))
            {
                // Inversione di Gerstner ripartendo dal passo precedente: 2 iterazioni bastano
                // (errore di quota ~mm), 4 al primo passo o dopo un teletrasporto.
                WaterSample s = water.SampleNear(p, ref _offset[v], _hasPrev ? 2 : 4);
                float d = s.Height - p.Y;
                Vector3 vel = s.Velocity;
                if (d > 0f)
                {
                    // Sotto la superficie l'onda si attenua (e^(−k·d)): profondita' "di pressione"
                    // con lo stiramento di Wheeler (0 in superficie, idrostatica media in fondo) e
                    // velocita' orbitale ridotta. Senza, uno scafo che pesca 2 m in onde di 20 m
                    // sentirebbe sulla chiglia tutta l'onda di superficie e rollerebbe il triplo.
                    float att = Mathf.Exp(-water.PeakWavenumber(s.Exposure) * d);
                    d -= (s.Height - water.WaterLevel) * (1f - att);
                    var current = new Vector3(water.Current.X, 0f, water.Current.Z);
                    vel = (vel - current) * att + current;
                }
                _depth[v] = d;
                _waterVel[v] = vel;
                if (_depth[v] > 0f)
                {
                    float along = p.Dot(forward);
                    minAlong = Mathf.Min(minAlong, along);
                    maxAlong = Mathf.Max(maxAlong, along);
                    waterVelSum += s.Velocity;
                    waterHeightSum += s.Height;
                    wet++;
                }
            }
            else
            {
                _depth[v] = -1000f;
                _waterVel[v] = Vector3.Zero;
            }
        }

        MeanWaterVelocity = wet > 0 ? waterVelSum / wet : Vector3.Zero;
        MeanWaterHeight = wet > 0 ? waterHeightSum / wet : (water?.WaterLevel ?? 0f);
        float lWet = wet > 0 ? Mathf.Max(maxAlong - minAlong, 0.3f) : 0.3f;
        WettedLength = wet > 0 ? lWet : 0f;

        Vector3 relCom = ctx.LinearVelocity - MeanWaterVelocity;
        var relComH = new Vector3(relCom.X, 0f, relCom.Z);
        float u = relComH.Length();
        SpeedThroughWater = u;
        float cf = HydroMath.IttcFriction(u * lWet / Viscosity) + k.RoughnessDeltaCf;
        float viscK = 0.5f * rho * cf * (1f + k.FormFactorK);
        float vRef = Mathf.Max(k.ReferenceSpeed, 0.1f);
        float waterLevel = water?.WaterLevel ?? 0f;

        // --- 2) Triangoli --------------------------------------------------------------------
        Vector3 force = Vector3.Zero, torque = Vector3.Zero, resistive = Vector3.Zero;
        Vector3 slamForce = Vector3.Zero, slamTorque = Vector3.Zero;
        float buoyancy = 0f, immersed = 0f;
        Vector3 areaCentroid = Vector3.Zero;
        float viscAlong = 0f, pressAlong = 0f;
        Vector3 motionDir = u > 1e-3f ? relComH / u : forward;
        float slamGammaMax = Mathf.Max(k.SlamGammaMaxG * g, 0.01f);
        float dt = Mathf.Max(ctx.Dt, 1e-4f);
        bool airborneTerms = k.AirCd > 0f;

        Span<Vector3> poly = _poly;
        Span<float> polyDepth = _polyDepth;
        Span<Vector3> polyVel = _polyVel;

        for (int i = 0; i < _hull.TriangleCount; i++)
        {
            int ia = _hull.Triangles[3 * i], ib = _hull.Triangles[3 * i + 1], ic = _hull.Triangles[3 * i + 2];
            float da = _depth[ia], db = _depth[ib], dc = _depth[ic];
            Vector3 n = xf.Basis * _hull.Normals[i];
            bool anyWet = da > HydroMath.DepthEpsilon || db > HydroMath.DepthEpsilon || dc > HydroMath.DepthEpsilon;
            bool anyDry = da <= HydroMath.DepthEpsilon || db <= HydroMath.DepthEpsilon || dc <= HydroMath.DepthEpsilon;
            float areaWet = 0f;

            if (anyWet)
            {
                int count = HydroMath.Clip(_world[ia], _world[ib], _world[ic], da, db, dc,
                    _waterVel[ia], _waterVel[ib], _waterVel[ic], true, poly, polyDepth, polyVel);
                for (int t = 1; t + 1 < count; t++)
                {
                    Vector3 p0 = poly[0], p1 = poly[t], p2 = poly[t + 1];
                    float area = HydroMath.TriangleArea(p0, p1, p2);
                    if (area < 1e-6f)
                        continue;
                    areaWet += area;
                    float d0 = polyDepth[0], d1 = polyDepth[t], d2 = polyDepth[t + 1];
                    Vector3 c = (p0 + p1 + p2) / 3f;
                    float dMean = (d0 + d1 + d2) / 3f;

                    // Idrostatica: solo verticale, nel centro di pressione.
                    HydroMath.PressureSplit(p0, p1, p2, d0, d1, d2, area, out float f1, out Vector3 cp1, out float f2, out Vector3 cp2);
                    float scale = -rho * g * n.Y;
                    var fv1 = new Vector3(0f, f1 * scale, 0f);
                    var fv2 = new Vector3(0f, f2 * scale, 0f);
                    force += fv1 + fv2;
                    torque += (cp1 - ctx.Com).Cross(fv1) + (cp2 - ctx.Com).Cross(fv2);
                    buoyancy += (f1 + f2) * -n.Y;
                    areaCentroid += c * area;

                    // Idrodinamica: velocita' del pezzo rispetto all'acqua.
                    Vector3 uw = (polyVel[0] + polyVel[t] + polyVel[t + 1]) / 3f;
                    Vector3 vRel = ctx.PointVelocity(c) - uw;
                    float speed = vRel.Length();
                    if (speed < 1e-4f)
                        continue;

                    Vector3 vt = vRel - n * vRel.Dot(n);
                    Vector3 fVisc = -viscK * area * vt.Length() * vt;

                    float cosTheta = vRel.Dot(n) / speed;
                    float q = 0.5f * rho * speed * speed;
                    Vector3 fPress;
                    if (cosTheta > 0f)
                    {
                        float p = (k.PressureLinear * 0.5f * rho * vRef * speed + k.PressureQuadratic * q)
                            * (k.PressureExponent == 1f ? cosTheta : Mathf.Pow(cosTheta, k.PressureExponent));
                        fPress = -p * area * n;
                    }
                    else
                    {
                        float p = (k.SuctionLinear * 0.5f * rho * vRef * speed + k.SuctionQuadratic * q)
                            * (k.SuctionExponent == 1f ? -cosTheta : Mathf.Pow(-cosTheta, k.SuctionExponent));
                        p = Mathf.Min(p, k.SuctionVentK * rho * g * Mathf.Max(dMean, 0f));
                        fPress = p * area * n;
                    }

                    Vector3 fDyn = fVisc + fPress;
                    force += fDyn;
                    torque += (c - ctx.Com).Cross(fDyn);
                    resistive += fDyn;
                    viscAlong -= fVisc.Dot(motionDir);
                    pressAlong -= fPress.Dot(motionDir);
                }
            }

            // Impatto: per triangolo ORIGINALE, dalla crescita del volume spazzato.
            if (areaWet > 0f || _prevArea[i] > 0f)
            {
                Vector3 ct = (_world[ia] + _world[ib] + _world[ic]) / 3f;
                Vector3 uwt = (_waterVel[ia] + _waterVel[ib] + _waterVel[ic]) / 3f;
                Vector3 vRel = ctx.PointVelocity(ct) - uwt;
                float vn = Mathf.Max(0f, vRel.Dot(n));
                if (_hasPrev && areaWet > 0f)
                {
                    float gamma = (areaWet * vn - _prevArea[i] * _prevVn[i]) / (_hull.Areas[i] * dt);
                    float speed = vRel.Length();
                    float cosTheta = speed > 1e-4f ? vRel.Dot(n) / speed : 0f;
                    if (gamma > 0f && cosTheta > 0f)
                    {
                        float ratio = Mathf.Pow(Mathf.Clamp(gamma / slamGammaMax, 0f, 1f), k.SlamExponent);
                        Vector3 stop = ctx.Mass * vRel / dt * (2f * areaWet / _hull.TotalArea);
                        Vector3 f = -k.SlamK * ratio * cosTheta * stop;
                        slamForce += f;
                        slamTorque += (ct - ctx.Com).Cross(f);
                    }
                }
                _prevArea[i] = areaWet;
                _prevVn[i] = vn;
            }

            immersed += areaWet;

            // Aria: pezzi emersi contro il vento apparente.
            if (anyDry && airborneTerms)
            {
                int count = HydroMath.Clip(_world[ia], _world[ib], _world[ic], da, db, dc,
                    Vector3.Zero, Vector3.Zero, Vector3.Zero, false, poly, polyDepth, Span<Vector3>.Empty);
                for (int t = 1; t + 1 < count; t++)
                {
                    float area = HydroMath.TriangleArea(poly[0], poly[t], poly[t + 1]);
                    if (area < 1e-5f)
                        continue;
                    Vector3 c = (poly[0] + poly[t] + poly[t + 1]) / 3f;
                    float h = Mathf.Max(c.Y - waterLevel, 0.5f);
                    Vector3 wind = windAt10m * Mathf.Pow(h / 10f, 1f / 7f);
                    Vector3 vAir = ctx.PointVelocity(c) - wind;
                    float sp = vAir.Length();
                    if (sp < 1e-3f)
                        continue;
                    float cosA = vAir.Dot(n) / sp;
                    if (cosA <= 0f)
                        continue;
                    Vector3 f = -0.5f * AirDensity * k.AirCd * sp * sp * area * cosA * n;
                    force += f;
                    torque += (c - ctx.Com).Cross(f);
                }
            }
        }
        _hasPrev = true;

        // Tetto degli impatti: si scala tutto in proporzione.
        float slamMag = slamForce.Length();
        float slamCap = k.SlamMaxG * ctx.Mass * g;
        if (slamMag > slamCap && slamMag > 0f)
        {
            float s = slamCap / slamMag;
            slamForce *= s;
            slamTorque *= s;
            slamMag = slamCap;
        }
        force += slamForce;
        torque += slamTorque;
        SlamG = slamMag / Mathf.Max(ctx.Mass * g, 1e-3f);

        ImmersedArea = immersed;
        DisplacedVolume = buoyancy;
        ViscousDrag = viscAlong;
        PressureDrag = pressAlong;

        // --- 3) Termini globali --------------------------------------------------------------
        WaveDrag = 0f;
        Froude = u / Mathf.Sqrt(g * lWet);
        FroudeVolumetric = buoyancy > 1e-4f ? u / Mathf.Sqrt(g * Mathf.Pow(buoyancy, 1f / 3f)) : 0f;
        if (immersed > 0f && u > 1e-3f)
        {
            float fade = 1f;
            if (k.Hull != HullType.Displacement)
                fade = 1f - Mathf.SmoothStep(k.WaveFadeFnv.X, k.WaveFadeFnv.Y, FroudeVolumetric);
            float rw = HydroMath.WaveResistance(Froude) * k.WaveResistanceScale * fade;
            // Il "peso" e' quello sostenuto dall'acqua: in volo la resistenza d'onda sparisce.
            float weight = Mathf.Min(ctx.Mass * g, buoyancy * rho * g);
            WaveDrag = rw * weight;
            Vector3 fw = -WaveDrag * motionDir;
            Vector3 at = areaCentroid / immersed;
            at.Y = MeanWaterHeight;
            force += fw;
            torque += (at - ctx.Com).Cross(fw);
            resistive += fw;
        }

        // Smorzamento di sicurezza, in assi barca, scalato con l'area immersa.
        float fSub = Mathf.Clamp(immersed / _hull.TotalArea, 0f, 1f);
        if (fSub > 0f)
        {
            Basis inv = xf.Basis.Transposed();
            Vector3 vb = inv * relCom;
            Vector3 wb = inv * ctx.AngularVelocity;
            Vector3 lin = AxesFromSurgeSwayHeave(k.SafetyLinear);
            Vector3 quad = AxesFromSurgeSwayHeave(k.SafetyQuadratic);
            Vector3 ang = AxesFromRollPitchYaw(k.SafetyAngular);
            Vector3 fb = -fSub * ctx.Mass * (lin * vb + quad * vb.Abs() * vb);
            Vector3 tb = -fSub * (ang * ctx.Inertia * wb);
            Vector3 fd = xf.Basis * fb;
            force += fd;
            torque += xf.Basis * tb;
            resistive += fd;
        }

        ctx.Force += force;
        ctx.Torque += torque;
        ResistiveForce = resistive;
    }

    /// (avanzo, deriva, sussulto) → assi della barca (X deriva, Y sussulto, Z avanzo).
    public static Vector3 AxesFromSurgeSwayHeave(Vector3 v) => new(v.Y, v.Z, v.X);

    /// (rollio, beccheggio, imbardata) → assi della barca (X beccheggio, Y imbardata, Z rollio).
    public static Vector3 AxesFromRollPitchYaw(Vector3 v) => new(v.Y, v.Z, v.X);
}
