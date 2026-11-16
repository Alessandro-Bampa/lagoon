using Godot;

namespace Lagoon;

/// <summary>
/// L'arma del corpo in TERZA persona: la istanzia, la colloca rispetto alla testa animata e dice
/// a <see cref="ArmIkModifier"/> dove vanno le mani.
///
/// E' figlia dello SCHELETRO e tutta la sua geometria vive nello spazio dello scheletro: eredita
/// la trasformata del corpo senza passare da coordinate mondo, quindi non puo' restare indietro
/// rispetto a un avatar che si muove (ne' con l'interpolazione degli avatar remoti, ne' fra un
/// tick di fisica e l'altro).
///
/// La posa non e' inventata:
/// <list type="bullet">
/// <item>i marcatori <c>EyeHip</c> ed <c>EyeAim</c> di <c>W_*.glb</c> dicono dove sta l'occhio
/// rispetto all'arma nelle pose "al fianco" e "in mira" del viewmodel;</item>
/// <item>la presa delle due mani e' quella disegnata per le braccia in prima persona
/// (<see cref="ViewmodelHandPose"/>).</item>
/// </list>
/// L'occhio pero' e' quello VERO del corpo — l'osso della testa, dopo locomozione e mira — non
/// il punto nominale: correndo o accovacciandosi il busto si sporge in avanti, e un'arma ferma
/// sull'asse del corpo resterebbe dietro alle spalle con le braccia stirate all'indietro.
///
/// La posa la scrive <see cref="ArmIkModifier"/> dentro la passata dei modificatori, l'unico
/// momento in cui la testa e' gia' dove sara' disegnata. Pura resa (CLAUDE.md §3).
/// </summary>
public partial class WeaponHoldRig : Node3D
{
    /// <summary>
    /// Dagli occhi all'origine dell'osso della testa, nel riferimento dello sguardo (X destra,
    /// Y alto, Z verso chi guarda). Dipende dal corpo, non dall'arma.
    /// </summary>
    [Export] public Vector3 EyeFromHead { get; set; } = new(0f, 0.085f, -0.10f);

    /// <summary>
    /// Scostamento dell'arma IN MIRA, solo in terza persona, nel riferimento dello sguardo. La
    /// posa di mira del viewmodel mette la tacca a pochi centimetri dalla camera, al centro: su un
    /// corpo con una testa vera l'arma entrerebbe nel volto. La si porta davanti all'occhio
    /// destro e un poco piu' avanti, come una guancia appoggiata al calcio.
    /// </summary>
    [Export] public Vector3 AimOffset { get; set; } = new(0.04f, -0.01f, -0.06f);

    /// Di quanto punta in basso la canna con l'arma abbassata (corsa), in gradi.
    [Export] public float LoweredPitchDegrees { get; set; } = 42.0f;

    /// Di quanto la canna devia verso l'interno con l'arma abbassata, in gradi.
    [Export] public float LoweredYawDegrees { get; set; } = 28.0f;

    /// Spostamento dell'arma abbassata nel riferimento dello sguardo (X destra, Y alto, Z dietro).
    [Export] public Vector3 LoweredOffset { get; set; } = new(-0.04f, -0.16f, 0.02f);

    /// Quanto l'arma si abbassa mentre si ricarica, come frazione della posa abbassata.
    [Export] public float ReloadLower { get; set; } = 0.45f;

    /// Quanto contano i bersagli delle mani, da 0 a 1. Zero da disarmati o con l'arma riposta.
    public float HandsWeight { get; private set; }

    public bool HasWeapon => _model != null;

    /// True per l'avatar del giocatore locale: l'arma non si disegna (c'e' il viewmodel) ma
    /// continua a fare ombra.
    public bool ShadowOnly
    {
        get => _shadowOnly;
        set
        {
            if (_shadowOnly == value)
                return;
            _shadowOnly = value;
            ApplyShadowMode();
        }
    }

    /// Bocca dell'arma in coordinate mondo: da qui partono vampa e tracciante.
    public Vector3 MuzzlePosition => _model != null ? GlobalTransform * _muzzle : GlobalPosition;

    private WeaponAnimationSet? _weapon;
    private Node3D? _model;
    private bool _shadowOnly;

    // Marcatori nello spazio dell'arma (canna lungo -Z).
    private Transform3D _eyeHip = Transform3D.Identity;
    private Transform3D _eyeAim = Transform3D.Identity;
    private Vector3 _muzzle;
    private ViewmodelHandPose.Hands? _hands;

    private float _aim;
    private float _lower;
    private float _shown;

    // Rinculo visivo: scarto che una molla riporta a zero.
    private float _kickBack;
    private float _kickBackVelocity;
    private float _kickPitch;
    private float _kickPitchVelocity;

    /// Cambia l'arma mostrata; null = disarmato.
    public void SetWeapon(WeaponAnimationSet? weapon)
    {
        if (ReferenceEquals(weapon, _weapon))
            return;

        _weapon = weapon;
        _model?.QueueFree();
        _model = null;
        _hands = null;

        if (weapon?.WorldScene == null)
            return;

        _model = weapon.WorldScene.Instantiate<Node3D>();
        AddChild(_model);

        _eyeHip = Marker("EyeHip");
        _eyeAim = Marker("EyeAim");
        _muzzle = Marker("Muzzle").Origin;
        _hands = ViewmodelHandPose.Measure(weapon.ViewmodelScene);

        RetroMaterials.Apply(_model);
        ApplyShadowMode();
    }

    /// Rinculo di un colpo.
    public void Kick()
    {
        if (_weapon == null)
            return;

        _kickBack = Mathf.Min(_kickBack + _weapon.KickBack, _weapon.KickBack * 2f);
        _kickPitch = Mathf.Min(
            _kickPitch + Mathf.DegToRad(_weapon.KickPitchDegrees),
            Mathf.DegToRad(_weapon.KickPitchDegrees) * 2f);
    }

    /// Avanza pesi e molle. Chiamato ogni frame da <see cref="CharacterAnimator"/>.
    public void UpdateState(float dt, bool aiming, bool lowered, bool reloading, bool stowed)
    {
        bool visible = _model != null && !stowed;
        _shown = Mathf.Lerp(_shown, visible ? 1f : 0f, SkeletonMath.Damp(14f, dt));
        HandsWeight = _model != null && _hands != null ? _shown : 0f;

        if (_model == null || _weapon == null)
            return;

        _model.Visible = _shown > 0.5f;

        float aimStep = dt / Mathf.Max(_weapon.AimSeconds, 0.01f);
        _aim = Mathf.MoveToward(_aim, aiming ? 1f : 0f, aimStep);

        float lowerTarget = lowered ? 1f : (reloading ? ReloadLower : 0f);
        _lower = Mathf.Lerp(_lower, lowerTarget, SkeletonMath.Damp(9f, dt));

        StepSpring(ref _kickBack, ref _kickBackVelocity, dt);
        StepSpring(ref _kickPitch, ref _kickPitchVelocity, dt);
    }

    /// <summary>
    /// Colloca l'arma. <paramref name="eyeFrame"/> e' il punto di vista del corpo nello spazio
    /// dello scheletro, con -Z lungo lo sguardo: origine sugli occhi veri (testa animata).
    /// </summary>
    public void Place(Transform3D eyeFrame)
    {
        if (_model == null || _weapon == null)
            return;

        // L'arma nel riferimento dello sguardo: l'inversa del marcatore "dove sta l'occhio".
        float eased = Mathf.SmoothStep(0f, 1f, _aim);
        Transform3D pose = _eyeHip.InterpolateWith(_eyeAim, eased).AffineInverse();
        pose.Origin += AimOffset * eased;

        // Arma abbassata: canna in basso e verso l'interno, ruotando attorno all'impugnatura.
        if (_lower > 0.001f)
        {
            Vector3 pivot = pose * (_hands?.Right.Origin ?? Vector3.Zero);
            var rotation = new Basis(Vector3.Right, -Mathf.DegToRad(LoweredPitchDegrees) * _lower)
                * new Basis(Vector3.Up, Mathf.DegToRad(LoweredYawDegrees) * _lower);
            var lowering = new Transform3D(rotation, pivot + LoweredOffset * _lower - rotation * pivot);
            pose = lowering * pose;
        }

        // Rinculo nello spazio dell'arma: indietro lungo +Z, canna in alto attorno a +X.
        var kick = new Transform3D(new Basis(Vector3.Right, _kickPitch), new Vector3(0f, 0f, _kickBack));

        Transform = eyeFrame * pose * kick;
    }

    /// <summary>
    /// Bersagli delle due mani per <see cref="ArmIkModifier"/>, nello spazio dello scheletro.
    /// <paramref name="lookRight"/> e' la destra dello sguardo: i gomiti piegano verso il basso e
    /// verso l'esterno in QUEL riferimento, non in quello dell'arma, o abbassandola ruoterebbero
    /// con lei.
    /// </summary>
    public void FillHandGoals(Vector3 lookRight, ref ArmIkModifier.HandGoal right, ref ArmIkModifier.HandGoal left)
    {
        right.Weight = 0f;
        left.Weight = 0f;
        if (_model == null || _hands == null)
            return;

        Transform3D weapon = Transform;
        Fill(ref right, weapon * _hands.Value.Right, Vector3.Down + lookRight * 0.55f);
        Fill(ref left, weapon * _hands.Value.Left, Vector3.Down - lookRight * 0.6f);
    }

    private void Fill(ref ArmIkModifier.HandGoal goal, Transform3D hand, Vector3 pole)
    {
        goal.Position = hand.Origin;
        goal.Fingers = hand.Basis.X;
        goal.Across = hand.Basis.Y;
        goal.PoleDirection = pole;
        goal.Weight = HandsWeight;
        goal.Curl = 1f;
    }

    private void StepSpring(ref float value, ref float velocity, float dt)
    {
        float stiffness = _weapon!.KickStiffness;
        float damping = _weapon.KickDamping;

        // Passi piccoli: a 30 fps una molla rigida integrata in un colpo solo diverge.
        int steps = Mathf.Clamp(Mathf.CeilToInt(dt / 0.008f), 1, 8);
        float step = dt / steps;
        for (int i = 0; i < steps; i++)
        {
            velocity += (-stiffness * value - damping * velocity) * step;
            value += velocity * step;
        }
    }

    /// Trasformata di un marcatore rispetto alla radice del modello.
    private Transform3D Marker(string name)
    {
        if (_model!.FindChild(name, true, false) is not Node3D marker)
        {
            GD.PushWarning($"[WeaponHoldRig] marcatore '{name}' assente da {_model.SceneFilePath}.");
            return Transform3D.Identity;
        }

        Transform3D result = marker.Transform;
        for (Node? parent = marker.GetParent(); parent != null && parent != _model; parent = parent.GetParent())
        {
            if (parent is Node3D spatial)
                result = spatial.Transform * result;
        }

        return _model.Transform * result;
    }

    private void ApplyShadowMode()
    {
        if (_model == null)
            return;

        foreach (Node node in _model.FindChildren("*", nameof(MeshInstance3D), true, false))
        {
            ((MeshInstance3D)node).CastShadow = _shadowOnly
                ? GeometryInstance3D.ShadowCastingSetting.ShadowsOnly
                : GeometryInstance3D.ShadowCastingSetting.On;
        }
    }
}
