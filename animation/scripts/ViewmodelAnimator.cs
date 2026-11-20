using Godot;

namespace Lagoon;

/// <summary>
/// Viewmodel in PRIMA persona: le braccia e l'arma che vede il giocatore locale.
///
/// E' figlio della camera, quindi vive nel suo riferimento (-Z = sguardo). Il modello e'
/// <see cref="WeaponAnimationSet.ViewmodelScene"/>: braccia + arma su uno scheletro solo, con le
/// clip autoriali del Retro Weapon Pack. La posa di mira e' quella del pack, costruita attorno
/// all'origine della camera: in mira le tacche cadono al centro dello schermo per costruzione,
/// cioe' dove l'host fa passare il colpo.
///
/// Come <see cref="CharacterAnimator"/> e' un ricevitore puro: chi lo guida scrive lo stato e
/// chiama <see cref="Fire"/>. Esiste solo sul peer proprietario dell'avatar (CLAUDE.md §3: pura
/// resa locale, niente RPC, niente stato di gioco).
///
/// <code>
/// Hip (idle/walk/run) ──────────────┐
/// Crouch (idle_crouch/walk_crouch) ─┴ Stance ─┐
/// Aim (idle_aim/walk_aim) ────────────────────┴ Sight ─ Fire ─ Reload ─ Draw ─ Hide [─ Empty]
/// </code>
/// </summary>
public partial class ViewmodelAnimator : Node3D
{
    private static readonly string[] LoopingClips =
    {
        "idle", "idle_aim", "idle_crouch", "walk", "walk_aim", "walk_crouch", "run",
    };

    private const string HipPosition = "parameters/Hip/blend_position";
    private const string CrouchPosition = "parameters/Crouch/blend_position";
    private const string AimPosition = "parameters/Aim/blend_position";
    private const string StanceAmount = "parameters/Stance/blend_amount";
    private const string SightAmount = "parameters/Sight/blend_amount";
    private const string FireClipRequest = "parameters/FireClip/transition_request";
    private const string FireRequest = "parameters/Fire/request";
    private const string ReloadRequest = "parameters/Reload/request";
    private const string DrawRequest = "parameters/Draw/request";
    private const string HideRequest = "parameters/Hide/request";
    private const string HideScale = "parameters/HideScale/scale";
    private const string EmptyAmount = "parameters/Empty/blend_amount";

    /// Velocita' di chi si muove, in m/s: i punti degli spazi di blend. Le riallinea il bridge.
    [Export] public float WalkSpeed { get; set; } = 4.0f;

    [Export] public float RunSpeed { get; set; } = 7.0f;
    [Export] public float CrouchSpeed { get; set; } = 2.0f;

    /// Durata del gesto con cui si ripone l'arma prima di estrarre la successiva, in secondi.
    [Export] public float HideSeconds { get; set; } = 0.28f;

    /// Ritardo dell'arma rispetto allo sguardo: gradi di rotazione per radiante/secondo di mouse.
    [Export] public float SwayAmount { get; set; } = 0.9f;

    /// Rotazione massima dello sway, in gradi.
    [Export] public float SwayLimitDegrees { get; set; } = 3.5f;

    /// Di quanto scende l'arma mentre le mani sono occupate (scavalcamento), in metri.
    [Export] public float LoweredDrop { get; set; } = 0.45f;

    /// <summary>
    /// Scostamento del viewmodel FUORI mira, nel riferimento della camera (X destra, Y alto, Z
    /// verso chi guarda). Il pack e' disegnato per un campo visivo di ~59 gradi verticali: con
    /// quello di gioco, piu' largo, braccia e arma si allontanano verso il centro, e qui le si
    /// riporta dove stanno bene. In mira vale zero: le tacche devono restare sull'asse.
    /// </summary>
    [Export] public Vector3 HipOffset { get; set; } = Vector3.Zero;

    // ====================================================================================
    //  Stato in ingresso
    // ====================================================================================

    /// Velocita' orizzontale, in m/s.
    public float Speed { get; set; }

    public bool Grounded { get; set; } = true;
    public bool Crouching { get; set; }

    /// Mira col tasto destro.
    public bool Aiming { get; set; }

    public bool Reloading { get; set; }

    /// Caricatore vuoto: la pistola resta con il carrello arretrato.
    public bool MagazineEmpty { get; set; }

    /// Mani occupate (scavalcamento): l'arma esce dalla vista.
    public bool Lowered { get; set; }

    // ====================================================================================
    //  Uscite
    // ====================================================================================

    public bool HasWeapon => _model != null;

    /// Quanto si e' in mira, da 0 a 1, gia' smorzato.
    public float AimWeight { get; private set; }

    /// Bocca dell'arma in coordinate mondo: da qui partono vampa e tracciante del giocatore locale.
    public Vector3 MuzzlePosition => _muzzle?.GlobalPosition ?? GlobalPosition;

    public Node3D? Muzzle => _muzzle;

    /// <summary>
    /// Rotazione dell'osso <c>camera</c> del viewmodel rispetto al riposo, nel riferimento della
    /// camera: e' lo scossone autoriale di ricarica ed estrazione. La applica
    /// <c>FirstPersonCamera</c>; qui la si toglie dal viewmodel, cosi' braccia e arma restano
    /// ferme rispetto alla visuale come nel pack.
    /// </summary>
    public Quaternion CameraShake { get; private set; } = Quaternion.Identity;

    private WeaponAnimationSet? _weapon;
    private WeaponAnimationSet? _pending;
    private bool _swapQueued;
    private float _hideRemaining;

    private Node3D? _model;
    private AnimationTree? _tree;
    private Skeleton3D? _skeleton;
    private Node3D? _muzzle;
    private int _cameraBone = -1;
    private bool _hasEmptyPose;
    private bool _wasReloading;
    private float _hideLength = 0.3f;

    private float _crouchWeight;
    private float _lowerWeight;
    private float _emptyWeight;
    private float _speed;

    private Vector2 _swayInput;
    private Vector2 _sway;

    // Rinculo visivo: x = arretramento (m), y = impennata (rad), z = rollio (rad).
    private Vector3 _kick;
    private Vector3 _kickVelocity;
    private readonly RandomNumberGenerator _rng = new();

    /// Abbassamento d'atterraggio, in metri: rientra da solo.
    private float _landDip;

    public override void _Ready() => _rng.Randomize();

    /// Modello del viewmodel appena istanziato (o tolto): chi lo riveste (<c>ViewmodelOutfit</c>)
    /// ci riaggancia braccia e capi.
    [Signal] public delegate void ModelSwappedEventHandler();

    /// Scheletro del viewmodel corrente (braccia + arma), null se disarmati.
    public Skeleton3D? ArmsSkeleton => _skeleton;

    /// Cambia l'arma: quella in mano viene riposta, poi si estrae la nuova. Null = disarmato.
    public void SetWeapon(WeaponAnimationSet? weapon)
    {
        WeaponAnimationSet? target = weapon?.ViewmodelScene != null ? weapon : null;
        if (ReferenceEquals(target, _swapQueued ? _pending : _weapon))
            return;

        _pending = target;
        _swapQueued = true;

        if (_model == null || _tree == null)
        {
            Swap();
            return;
        }

        if (_hideRemaining <= 0f)
        {
            _tree.Set(HideScale, _hideLength / Mathf.Max(HideSeconds, 0.05f));
            _tree.Set(HideRequest, (int)AnimationNodeOneShot.OneShotRequest.Fire);
            _hideRemaining = HideSeconds;
        }
    }

    /// Sparo: clip del pack (al fianco o in mira) piu' il rinculo procedurale.
    public void Fire()
    {
        if (_tree == null || _weapon == null)
            return;

        bool sighted = AimWeight > 0.5f;
        _tree.Set(FireClipRequest, sighted ? "aim" : "hip");
        _tree.Set(FireRequest, (int)AnimationNodeOneShot.OneShotRequest.Fire);

        float factor = Mathf.Lerp(1f, _weapon.AimKickFactor, AimWeight);
        _kick.X = Mathf.Min(_kick.X + _weapon.KickBack * factor, _weapon.KickBack * 2f);
        _kick.Y = Mathf.Min(
            _kick.Y + Mathf.DegToRad(_weapon.KickPitchDegrees) * factor,
            Mathf.DegToRad(_weapon.KickPitchDegrees) * 2f);
        _kick.Z += Mathf.DegToRad(_rng.RandfRange(-_weapon.KickRollDegrees, _weapon.KickRollDegrees)) * factor;
    }

    /// Movimento dello sguardo in questo frame, in radianti (x = imbardata, y = inclinazione).
    public void AddLookDelta(Vector2 radians) => _swayInput += radians;

    /// Atterraggio: l'arma accusa il colpo e rientra.
    public void Land(float impactSpeed) =>
        _landDip = Mathf.Max(_landDip, Mathf.Clamp(impactSpeed / 9f, 0f, 1f) * 0.06f);

    public override void _Process(double delta)
    {
        float dt = (float)delta;

        if (_swapQueued && _hideRemaining > 0f)
        {
            _hideRemaining -= dt;
            if (_hideRemaining <= 0f)
                Swap();
        }

        UpdateWeights(dt);
        UpdateTree();
        UpdateTransform(dt);
    }

    private void UpdateWeights(float dt)
    {
        _speed = Mathf.Lerp(_speed, Grounded ? Speed : 0f, SkeletonMath.Damp(12f, dt));
        _crouchWeight = Mathf.Lerp(_crouchWeight, Crouching ? 1f : 0f, SkeletonMath.Damp(10f, dt));
        _lowerWeight = Mathf.Lerp(_lowerWeight, Lowered ? 1f : 0f, SkeletonMath.Damp(10f, dt));
        _emptyWeight = Mathf.Lerp(_emptyWeight, MagazineEmpty && !Reloading ? 1f : 0f, SkeletonMath.Damp(30f, dt));

        // La mira avanza a tempo costante: e' un tempo di gioco (quanto ci vuole a portare
        // l'arma all'occhio), non uno smorzamento.
        float aimSeconds = _weapon?.AimSeconds ?? 0.18f;
        bool aiming = Aiming && _model != null && !Reloading && !Lowered && _hideRemaining <= 0f;
        AimWeight = Mathf.MoveToward(AimWeight, aiming ? 1f : 0f, dt / Mathf.Max(aimSeconds, 0.01f));
    }

    private void UpdateTree()
    {
        if (_tree == null)
            return;

        _tree.Set(HipPosition, Mathf.Min(_speed, RunSpeed));
        _tree.Set(CrouchPosition, Mathf.Min(_speed, CrouchSpeed));
        _tree.Set(AimPosition, Mathf.Min(_speed, WalkSpeed));
        _tree.Set(StanceAmount, _crouchWeight);
        _tree.Set(SightAmount, Mathf.SmoothStep(0f, 1f, AimWeight));
        if (_hasEmptyPose)
            _tree.Set(EmptyAmount, _emptyWeight);

        if (Reloading != _wasReloading)
        {
            _wasReloading = Reloading;
            _tree.Set(ReloadRequest, Reloading
                ? (int)AnimationNodeOneShot.OneShotRequest.Fire
                : (int)AnimationNodeOneShot.OneShotRequest.FadeOut);
        }

        // Scossone autoriale della camera: l'osso `camera` rispetto al suo riposo.
        if (_skeleton != null && _cameraBone >= 0)
        {
            Basis pose = _skeleton.GetBoneGlobalPose(_cameraBone).Basis.Orthonormalized();
            Basis rest = _skeleton.GetBoneGlobalRest(_cameraBone).Basis.Orthonormalized();
            CameraShake = (pose * rest.Inverse()).GetRotationQuaternion();
        }
    }

    private void UpdateTransform(float dt)
    {
        // Sway: l'arma resta indietro rispetto allo sguardo e lo raggiunge.
        float limit = Mathf.DegToRad(SwayLimitDegrees);
        Vector2 target = dt > 0.0001f
            ? (_swayInput / dt * Mathf.DegToRad(SwayAmount)).Clamp(new Vector2(-limit, -limit), new Vector2(limit, limit))
            : Vector2.Zero;
        _swayInput = Vector2.Zero;
        _sway = _sway.Lerp(target * Mathf.Lerp(1f, 0.25f, AimWeight), SkeletonMath.Damp(9f, dt));

        StepKick(dt);
        _landDip = Mathf.Lerp(_landDip, 0f, SkeletonMath.Damp(8f, dt));

        // Rotazione: contro lo scossone della camera, piu' sway e rinculo. Attorno a +X positivo
        // la canna (che guarda -Z) sale.
        float sighted = Mathf.SmoothStep(0f, 1f, AimWeight);
        float aimPitch = Mathf.DegToRad(_weapon?.AimPitchDegrees ?? 0f) * sighted;
        var basis = new Basis(Vector3.Right, aimPitch)
            * new Basis(CameraShake.Inverse())
            * new Basis(Vector3.Up, -_sway.X)
            * new Basis(Vector3.Right, -_sway.Y + _kick.Y)
            * new Basis(Vector3.Back, _kick.Z);

        // Il rinculo arretra lungo +Z (verso chi guarda); lo scostamento "al fianco" svanisce
        // entrando in mira.
        Vector3 origin = HipOffset * (1f - sighted)
            + new Vector3(0f, -_landDip - LoweredDrop * _lowerWeight, _kick.X);

        Transform = new Transform3D(basis, origin);

        if (_model != null)
            _model.Visible = _lowerWeight < 0.95f;
    }

    private void StepKick(float dt)
    {
        if (_weapon == null)
        {
            _kick = Vector3.Zero;
            _kickVelocity = Vector3.Zero;
            return;
        }

        // Molla smorzata a passi piccoli: integrata in un colpo solo, a 30 fps divergerebbe.
        int steps = Mathf.Clamp(Mathf.CeilToInt(dt / 0.008f), 1, 8);
        float step = dt / steps;
        for (int i = 0; i < steps; i++)
        {
            _kickVelocity += (-_weapon.KickStiffness * _kick - _weapon.KickDamping * _kickVelocity) * step;
            _kick += _kickVelocity * step;
        }
    }

    // ====================================================================================
    //  Modello e albero
    // ====================================================================================

    private void Swap()
    {
        _swapQueued = false;
        _hideRemaining = 0f;

        _tree = null;
        _skeleton = null;
        _muzzle = null;
        _cameraBone = -1;
        _model?.QueueFree();
        _model = null;
        CameraShake = Quaternion.Identity;
        AimWeight = 0f;
        _wasReloading = false;
        _kick = Vector3.Zero;
        _kickVelocity = Vector3.Zero;

        _weapon = _pending;
        _pending = null;
        if (_weapon?.ViewmodelScene == null)
            return;

        _model = _weapon.ViewmodelScene.Instantiate<Node3D>();
        AddChild(_model);

        RetroMaterials.Apply(_model);
        foreach (Node node in _model.FindChildren("*", nameof(MeshInstance3D), true, false))
        {
            // Niente ombre: quella dell'arma la fa il corpo in terza persona, dalla posizione giusta.
            var mesh = (MeshInstance3D)node;
            mesh.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;

            // Il modello sta a pochi centimetri dalla camera: senza margine il culling lo scarta
            // quando la sua AABB (calcolata a riposo) esce dal frustum durante una clip.
            mesh.ExtraCullMargin = 2.0f;
        }

        _skeleton = _model.FindChild("Skeleton3D", true, false) as Skeleton3D;
        _cameraBone = _skeleton?.FindBone("camera") ?? -1;
        _muzzle = _model.FindChild("Muzzle", true, false) as Node3D;
        EmitSignal(SignalName.ModelSwapped);

        if (_model.FindChild("AnimationPlayer", true, false) is not AnimationPlayer player)
        {
            GD.PushWarning($"[ViewmodelAnimator] {_model.SceneFilePath} non ha un AnimationPlayer: il viewmodel restera' fermo.");
            return;
        }

        foreach (string clip in LoopingClips)
        {
            if (player.HasAnimation(clip))
                player.GetAnimation(clip).LoopMode = Animation.LoopModeEnum.Linear;
        }

        _hasEmptyPose = player.HasAnimation("empty");
        _hideLength = player.HasAnimation("hide") ? (float)player.GetAnimation("hide").Length : HideSeconds;

        _tree = new AnimationTree { Name = "AnimationTree" };
        _model.AddChild(_tree);
        foreach (StringName library in player.GetAnimationLibraryList())
            _tree.AddAnimationLibrary(library, player.GetAnimationLibrary(library));
        _tree.RootNode = _tree.GetPathTo(player.GetNode(player.RootNode));
        _tree.TreeRoot = BuildTree(player);
        _tree.Active = true;

        // Un Transition resta muto finche' non riceve la prima richiesta.
        _tree.Set(FireClipRequest, "hip");
        _tree.Set(DrawRequest, (int)AnimationNodeOneShot.OneShotRequest.Fire);
    }

    private AnimationNodeBlendTree BuildTree(AnimationPlayer player)
    {
        var tree = new AnimationNodeBlendTree();

        var hip = new AnimationNodeBlendSpace1D { MinSpace = 0f, MaxSpace = RunSpeed };
        hip.AddBlendPoint(Clip("idle"), 0f, name: "idle");
        hip.AddBlendPoint(Clip("walk"), WalkSpeed, name: "walk");
        hip.AddBlendPoint(Clip("run"), RunSpeed, name: "run");
        tree.AddNode("Hip", hip);

        var crouch = new AnimationNodeBlendSpace1D { MinSpace = 0f, MaxSpace = CrouchSpeed };
        crouch.AddBlendPoint(Clip("idle_crouch"), 0f, name: "idle_crouch");
        crouch.AddBlendPoint(Clip("walk_crouch"), CrouchSpeed, name: "walk_crouch");
        tree.AddNode("Crouch", crouch);

        var aim = new AnimationNodeBlendSpace1D { MinSpace = 0f, MaxSpace = WalkSpeed };
        aim.AddBlendPoint(Clip("idle_aim"), 0f, name: "idle_aim");
        aim.AddBlendPoint(Clip("walk_aim"), WalkSpeed, name: "walk_aim");
        tree.AddNode("Aim", aim);

        tree.AddNode("Stance", new AnimationNodeBlend2());
        tree.ConnectNode("Stance", 0, "Hip");
        tree.ConnectNode("Stance", 1, "Crouch");

        tree.AddNode("Sight", new AnimationNodeBlend2());
        tree.ConnectNode("Sight", 0, "Stance");
        tree.ConnectNode("Sight", 1, "Aim");

        var fireClip = new AnimationNodeTransition { XfadeTime = 0f };
        fireClip.AddInput("hip");
        fireClip.AddInput("aim");
        tree.AddNode("FireHip", Clip("fire"));
        tree.AddNode("FireAim", Clip("fire_aim"));
        tree.AddNode("FireClip", fireClip);
        tree.ConnectNode("FireClip", 0, "FireHip");
        tree.ConnectNode("FireClip", 1, "FireAim");

        string last = AddOneShot(tree, "Fire", "Sight", "FireClip", 0.02f, 0.1f);

        tree.AddNode("ReloadClip", Clip("reload"));
        last = AddOneShot(tree, "Reload", last, "ReloadClip", 0.1f, 0.15f);

        tree.AddNode("DrawClip", Clip("draw"));
        last = AddOneShot(tree, "Draw", last, "DrawClip", 0f, 0.12f);

        tree.AddNode("HideClip", Clip("hide"));
        tree.AddNode("HideScale", new AnimationNodeTimeScale());
        tree.ConnectNode("HideScale", 0, "HideClip");
        last = AddOneShot(tree, "Hide", last, "HideScale", 0.04f, 0f);

        if (_hasEmptyPose)
        {
            // Carrello arretrato a caricatore vuoto: una posa delle sole parti dell'ARMA. La
            // clip contiene anche le braccia (a riposo: l'export campiona tutto lo scheletro),
            // quindi il filtro si restringe agli ossi che discendono da quello dell'arma.
            var empty = new AnimationNodeBlend2 { FilterEnabled = true };
            Animation pose = player.GetAnimation("empty");
            for (int track = 0; track < pose.GetTrackCount(); track++)
            {
                NodePath path = pose.TrackGetPath(track);
                if (path.GetSubNameCount() > 0 && IsGunBone(path.GetSubName(0)))
                    empty.SetFilterPath(path, true);
            }

            tree.AddNode("EmptyPose", Clip("empty"));
            tree.AddNode("Empty", empty);
            tree.ConnectNode("Empty", 0, last);
            tree.ConnectNode("Empty", 1, "EmptyPose");
            last = "Empty";
        }

        tree.ConnectNode("output", 0, last);
        return tree;
    }

    /// True se l'osso appartiene all'arma: nel viewmodel discendono tutti dall'osso <c>Gun</c>.
    private bool IsGunBone(string name)
    {
        if (_skeleton == null)
            return false;

        int gun = _skeleton.FindBone("Gun");
        for (int bone = _skeleton.FindBone(name); bone >= 0; bone = _skeleton.GetBoneParent(bone))
        {
            if (bone == gun)
                return true;
        }

        return false;
    }

    private static string AddOneShot(
        AnimationNodeBlendTree tree, string name, string input, string shot, float fadeIn, float fadeOut)
    {
        tree.AddNode(name, new AnimationNodeOneShot { FadeInTime = fadeIn, FadeOutTime = fadeOut });
        tree.ConnectNode(name, 0, input);
        tree.ConnectNode(name, 1, shot);
        return name;
    }

    private static AnimationNodeAnimation Clip(string name) => new() { Animation = name };
}
