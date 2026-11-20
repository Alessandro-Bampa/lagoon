using Godot;

namespace Lagoon;

/// <summary>
/// Nucleo fisico di una barca montata da configurazione (solo host, skill vehicles-boats §10-§12).
/// Un passo, dentro <c>_IntegrateForces</c>:
/// <list type="number">
/// <item>gravita' esplicita (il corpo ha <c>gravity_scale = 0</c>: va scalata con la massa aggiunta);</item>
/// <item>scafo per triangoli (<see cref="HydroSolver"/>);</item>
/// <item>propulsione (fuoribordo o entrobordo), poi appendici (il timone usa la scia appena calcolata),
/// elica di prua, chiglie fisse;</item>
/// <item>limite di non inversione delle resistenze, massa aggiunta per asse, sanificazione;</item>
/// <item>una sola forza centrale e una coppia sul corpo.</item>
/// </list>
/// Stesso codice per ogni classe di barca: cambiano solo i dati (<see cref="BoatConfig"/>).
/// </summary>
public sealed class BoatPhysics
{
    public readonly HydroSolver Hydro;
    public readonly HydroCoefficients Coefficients;
    public readonly OutboardDrive? Outboard;
    public readonly ShaftDrive? Shaft;
    public readonly BowThruster? Thruster;
    private readonly FoilDefinition[] _fixedFoils;
    private readonly Transform3D[] _fixedFoilMounts;
    private readonly BoatForces _ctx = new();
    private readonly Vector3 _gravity = DefaultGravity();

    /// Area immersa al galleggiamento di progetto in acqua ferma (normalizza la massa aggiunta).
    public float StaticImmersedArea { get; private set; } = 1f;
    /// Pescaggio dell'origine in acqua ferma (quota dell'origine sotto il pelo dell'acqua).
    public float StaticHeave { get; private set; }
    private float _staticMass = -1f;

    /// Passi scartati per NaN/Inf (le prove falliscono se &gt; 0).
    public int InvalidSteps { get; private set; }
    /// Fattore di immersione della massa aggiunta nell'ultimo passo.
    public float AddedMassScale { get; private set; }
    public float LastSlamG { get; private set; }
    /// Il passo e' stato limitato (forza fuori scala: errore da indagare).
    public int ClampedSteps { get; private set; }

    public BoatForces Context => _ctx;

    public BoatPhysics(HydroHull hull, HydroCoefficients coefficients, OutboardDrive? outboard, ShaftDrive? shaft,
        BowThruster? thruster, FoilDefinition[] fixedFoils, Transform3D[] fixedFoilMounts)
    {
        Hydro = new HydroSolver(hull);
        Coefficients = coefficients;
        Outboard = outboard;
        Shaft = shaft;
        Thruster = thruster;
        _fixedFoils = fixedFoils;
        _fixedFoilMounts = fixedFoilMounts;
    }

    /// Ricalcola il galleggiamento statico se la massa e' cambiata di oltre il 5%.
    public void UpdateStatics(float mass, float density)
    {
        if (_staticMass > 0f && Mathf.Abs(mass - _staticMass) < 0.05f * _staticMass)
            return;
        _staticMass = mass;
        float volume = mass / Mathf.Max(density, 1f);
        StaticHeave = Hydro.Hull.SolveHeave(volume, 0f, 0f);
        var statics = Hydro.Hull.ComputeStatics(new Transform3D(Basis.Identity, new Vector3(0f, StaticHeave, 0f)));
        StaticImmersedArea = Mathf.Max(statics.WettedArea, 0.01f);
        StaticBuoyancyCenter = statics.Buoyancy;
    }

    /// Centro di carena statico (assetto 0): solo X e Z sono significativi.
    public Vector3 StaticBuoyancyCenter { get; private set; }

    /// Tempo medio di un passo (ms), media mobile: budget §18.4 (barca del giocatore ≤ 0,5 ms).
    public double StepMilliseconds { get; private set; }
    private readonly System.Diagnostics.Stopwatch _watch = new();

    public void Step(PhysicsDirectBodyState3D state, float mass, Vector3 inertia, WaterVolume? water, in BoatCommands cmd)
    {
        _watch.Restart();
        BoatForces ctx = _ctx;
        ctx.Transform = state.Transform;
        ctx.Com = state.Transform * state.CenterOfMassLocal;
        ctx.LinearVelocity = state.LinearVelocity;
        ctx.AngularVelocity = state.AngularVelocity;
        ctx.Dt = state.Step;
        ctx.Mass = mass;
        ctx.Inertia = inertia;
        // Non state.TotalGravity: con gravity_scale = 0 e' gia' scalata a zero.
        ctx.Gravity = _gravity;
        ctx.Water = water;
        ctx.Reset();

        // Gravita' esplicita: viene scalata con le altre forze dalla massa aggiunta.
        ctx.Force += mass * ctx.Gravity;

        float density = water?.Density ?? 1025f;
        Vector3 wind = water?.WindVelocity ?? Vector3.Zero;
        Hydro.Step(ctx, Coefficients, wind, density);
        LastSlamG = Hydro.SlamG;
        float speed = Hydro.SpeedThroughWater;

        Outboard?.Step(ctx, cmd, speed);
        Shaft?.Step(ctx, cmd);
        Thruster?.Step(ctx, cmd.Thruster, cmd.Running, speed);
        for (int i = 0; i < _fixedFoils.Length; i++)
        {
            Transform3D mount = ctx.Transform * _fixedFoilMounts[i];
            Basis b = mount.Basis.Orthonormalized();
            Foil.Apply(_fixedFoils[i], ctx, mount.Origin, b.Z, b.X, b.Y);
        }

        Vector3 force = ctx.Force;
        Vector3 torque = ctx.Torque;

        // Niente inversione: la componente frenante delle resistenze non puo' annullare in un passo
        // piu' della velocita' relativa all'acqua (la portanza, perpendicolare, non si tocca).
        Vector3 rel = ctx.LinearVelocity - Hydro.MeanWaterVelocity;
        float relSpeed = rel.Length();
        if (relSpeed > 1e-4f)
        {
            Vector3 against = -rel / relSpeed;
            float braking = Hydro.ResistiveForce.Dot(against);
            float limit = relSpeed * mass / Mathf.Max(ctx.Dt, 1e-4f);
            if (braking > limit)
                force -= against * (braking - limit);
        }

        // Massa aggiunta: F' = F·m/(m + A) per asse, A scalata con l'immersione. La scalatura vale per
        // l'accelerazione RELATIVA agli assi della barca, non per la parte dovuta alla rotazione: in
        // virata a regime l'accelerazione centripeta laterale u·r e' tutta "rotazione", e la quantita'
        // di moto dell'acqua che la produce e' quella d'AVANZO (A_avanzo·u ruotata), non quella di
        // deriva. Scalando anche lei la barca girava come se pesasse (1 + a_deriva) volte: deriva in
        // virata gonfiata del 30-80% e punto di rotazione davanti alla prua. Forma corretta (Fossen,
        // corpo con massa diagonale M = m + A): F' = (F − ω × (M v)) ⊘ (1 + a) + m·(ω × v), con v
        // relativa all'acqua. Il momento di Munk (v × A v) resta escluso: il modello per triangoli non
        // ha la ripresa viscosa che in uno scafo vero lo compensa in parte.
        AddedMassScale = Mathf.Clamp(Hydro.ImmersedArea / StaticImmersedArea, 0f, 1.5f);
        if (Coefficients.AddedMassEnabled && AddedMassScale > 0f)
        {
            Basis basis = ctx.Basis;
            Basis inv = basis.Transposed();
            Vector3 fb = inv * force;
            Vector3 tb = inv * torque;
            Vector3 lin = HydroSolver.AxesFromSurgeSwayHeave(Coefficients.AddedMass) * AddedMassScale;
            Vector3 ang = HydroSolver.AxesFromRollPitchYaw(Coefficients.AddedInertia) * AddedMassScale;
            Vector3 vb = inv * (ctx.LinearVelocity - Hydro.MeanWaterVelocity);
            Vector3 wb = inv * ctx.AngularVelocity;
            Vector3 one = Vector3.One;
            Vector3 coriolis = wb.Cross(mass * (one + lin) * vb);
            fb = (fb - coriolis) / (one + lin) + mass * wb.Cross(vb);
            tb = new Vector3(tb.X / (1f + ang.X), tb.Y / (1f + ang.Y), tb.Z / (1f + ang.Z));
            force = basis * fb;
            torque = basis * tb;
        }

        // Sanificazione: NaN/Inf scartati, forze fuori scala limitate (e contate: sono errori).
        if (!IsFinite(force) || !IsFinite(torque))
        {
            InvalidSteps++;
            GD.PushWarning($"[BoatPhysics] forza non finita scartata: F={force} T={torque} v={ctx.LinearVelocity} w={ctx.AngularVelocity}");
            return;
        }
        float g = -ctx.Gravity.Y;
        float fMax = 50f * mass * g;
        float length = Mathf.Max(Hydro.Hull.Bounds.Size.Z, 1f);
        if (force.Length() > fMax)
        {
            force = force.Normalized() * fMax;
            ClampedSteps++;
        }
        if (torque.Length() > fMax * length)
        {
            torque = torque.Normalized() * fMax * length;
            ClampedSteps++;
        }

        state.ApplyCentralForce(force);
        state.ApplyTorque(torque);
        StepMilliseconds += (_watch.Elapsed.TotalMilliseconds - StepMilliseconds) * 0.02;
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static Vector3 DefaultGravity()
        => Vector3.Down * (float)(double)ProjectSettings.GetSetting("physics/3d/default_gravity", 9.8);
}
