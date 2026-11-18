using Godot;

namespace Lagoon;

/// <summary>
/// Anima il CORPO di un personaggio (terza persona): quello che gli altri giocatori vedono, e
/// che il giocatore locale vede come gambe e ombra. Il viewmodel in prima persona e'
/// <see cref="ViewmodelAnimator"/>.
///
/// E' un RICEVITORE PURO: non sa chi lo alimenta e non legge nulla dal mondo. Non conosce
/// <c>PlayerController</c>, non interroga il <c>Multiplayer</c>, non valida niente. Chi lo guida
/// scrive nelle proprieta' pubbliche e chiama i metodi <c>Trigger*</c> — per il giocatore
/// <see cref="PlayerAnimationBridge"/>, per gli NPC <c>NpcAnimationBridge</c>. Rispetto a
/// CLAUDE.md §3 sta interamente dal lato "resa": riceve stato gia' replicato e lo trasforma in pose.
///
/// Le clip sono quelle della Universal Animation Library (dentro <c>Mannequin.glb</c>). L'albero
/// di blend si costruisce QUI da codice: i nomi delle clip e dei nodi stanno in un solo file.
///
/// <code>
/// Move (Idle/Walk/Jog/Sprint per velocita') ─ MoveScale ─┐
/// Crouch (Crouch_Idle/Crouch_Fwd) ─ CrouchScale ─────────┴ Stance ─┐
/// Jump (posa di volo) ─────────────────────────────────────────────┴ Air ─┐
/// Swim (Swim_Idle/Swim_Fwd per velocita') ─ SwimScale ────────────────────┴ SwimBlend ─ Land ─ Hit ─┐
/// Death01 ──────────────────────────────────────────────────────────────────────────────────────────┴ Life
/// </code>
/// Scavalcamento e arrampicata non hanno clip: li compongono delle curve sul tempo (vedi
/// <see cref="StepManeuver"/>).
/// Sopra l'albero lavorano due modificatori di scheletro: <see cref="BodyAimModifier"/> (bacino
/// verso il moto, busto verso lo sguardo) e <see cref="ArmIkModifier"/> (mani e dita sull'arma).
/// </summary>
public partial class CharacterAnimator : Node3D
{
    // Clip della libreria. L'import di Godot toglie il suffisso "_Loop" e imposta il loop da solo.
    private const string ClipIdle = "Idle";
    private const string ClipWalk = "Walk";
    private const string ClipJog = "Jog_Fwd";
    private const string ClipSprint = "Sprint";
    private const string ClipCrouchIdle = "Crouch_Idle";
    private const string ClipCrouchMove = "Crouch_Fwd";
    private const string ClipAir = "Jump";
    private const string ClipLand = "Jump_Land";
    private const string ClipHitChest = "Hit_Chest";
    private const string ClipHitHead = "Hit_Head";
    private const string ClipDeath = "Death01";

    // Clip di nuoto della libreria: la superficie dell'acqua e' all'ORIGINE della clip (la testa sta
    // a ~0,27 m in Swim_Idle, a ~0 in Swim_Fwd, corpo orizzontale con la testa 0,54 m avanti).
    private const string ClipSwimIdle = "Swim_Idle";
    private const string ClipSwimMove = "Swim_Fwd";

    // Seduti al governo di una barca: senza volante (barra del fuoribordo) e al volante. Bacino a
    // 0,54 m e 0,31 m DIETRO l'origine della clip, piano di seduta ~0,46 m.
    private const string ClipSit = "Sitting_Idle";
    private const string ClipDrive = "Driving";

    // Parametri dell'albero.
    private const string MovePosition = "parameters/Move/blend_position";
    private const string MoveScale = "parameters/MoveScale/scale";
    private const string CrouchPosition = "parameters/Crouch/blend_position";
    private const string CrouchScale = "parameters/CrouchScale/scale";
    private const string StanceAmount = "parameters/Stance/blend_amount";
    private const string AirAmount = "parameters/Air/blend_amount";
    private const string SwimPosition = "parameters/Swim/blend_position";
    private const string SwimScale = "parameters/SwimScale/scale";
    private const string SwimAmount = "parameters/SwimBlend/blend_amount";
    private const string SeatPoseAmount = "parameters/Seat/blend_amount";
    private const string SeatAmount = "parameters/SeatBlend/blend_amount";
    private const string LandRequest = "parameters/Land/request";
    private const string HitRequest = "parameters/Hit/request";
    private const string HitPoseRequest = "parameters/HitPose/transition_request";
    private const string LifeRequest = "parameters/Life/transition_request";

    /// Percorso delle tracce d'osso nelle clip: lo scheletro ha un nome unico dato dal retarget.
    private const string TrackPrefix = "%GeneralSkeleton:";

    /// <summary>
    /// Velocita' di chi si muove, in m/s: sono i punti dello spazio di blend, quindi devono
    /// combaciare con quelle del controller. Le riallineano i bridge in <c>_Ready</c>, PRIMA che
    /// l'albero venga costruito.
    /// </summary>
    [Export] public float WalkSpeed { get; set; } = 4.0f;

    [Export] public float RunSpeed { get; set; } = 7.0f;
    [Export] public float CrouchSpeed { get; set; } = 2.0f;

    /// Velocita' a cui la clip <c>Walk</c> (una camminata lenta) e' a pieno peso, in m/s.
    [Export] public float StrollSpeed { get; set; } = 1.6f;

    /// Velocita' d'impatto oltre la quale parte la clip di atterraggio, in m/s.
    [Export] public float HardLandingSpeed { get; set; } = 9.0f;

    /// Rotazione massima del bacino verso la direzione del moto, in gradi.
    [Export] public float MaxHipSwivelDegrees { get; set; } = 65.0f;

    /// Velocita' a nuoto a cui <c>Swim_Fwd</c> e' a pieno peso, in m/s (la riallinea il bridge).
    [Export] public float SwimSpeed { get; set; } = 1.6f;

    /// <summary>
    /// Di quanto si alza il corpo a nuoto, in metri: porta l'origine delle clip di nuoto (la
    /// superficie) dai piedi nominali alla quota dell'acqua. Il bridge la ricava dal motore:
    /// <c>EyeHeight - SwimEyeAboveSurface</c>.
    /// </summary>
    [Export] public float SwimBodyLift { get; set; } = 1.42f;

    /// <summary>
    /// Arretramento del corpo nelle bracciate, in metri: in <c>Swim_Fwd</c> la testa sta 0,54 m
    /// avanti all'origine, e la camera (cioe' gli occhi nominali) sta sulla verticale della capsula.
    /// </summary>
    [Export] public float SwimForwardShift { get; set; } = 0.5f;

    /// Rotazione massima del corpo verso il moto a nuoto, in gradi (di lato si nuota "di traverso").
    [Export] public float MaxSwimSwivelDegrees { get; set; } = 80.0f;

    [Export] public float SwimBlendSpeed { get; set; } = 5.0f;

    /// <summary>
    /// Da seduti il corpo avanza di tanto rispetto all'origine: nelle clip il bacino sta 0,31 m
    /// dietro i piedi, e la testa finirebbe dietro la camera in prima persona, che sta sulla
    /// verticale dei piedi. Avanzato, la testa e' sopra i piedi; i posti di guida delle barche sono
    /// misurati con questo valore (build_lagoon_boats.py, <c>Seat_Helm</c>).
    /// </summary>
    [Export] public float SeatedForwardShift { get; set; } = 0.25f;
    [Export] public float SeatBlendSpeed { get; set; } = 6.0f;

    /// Distanza fra polso e centro del palmo che stringe barra o corona (m).
    [Export] public float SeatGripToWrist { get; set; } = 0.08f;

    [Export] public float BlendSpeed { get; set; } = 10.0f;
    [Export] public float VelocitySmoothing { get; set; } = 14.0f;
    [Export] public float AirBlendSpeed { get; set; } = 12.0f;

    // ====================================================================================
    //  Stato in ingresso: lo scrive chi pilota, ogni frame
    // ====================================================================================

    /// Velocita' orizzontale nel riferimento dell'avatar: X = destra, Y = avanti, in m/s.
    public Vector2 LocalVelocity { get; set; }

    public bool Crouching { get; set; }
    public bool Grounded { get; set; } = true;

    /// Scavalcamento o arrampicata in corso: arma riposta, posa di <see cref="TriggerVault"/>.
    public bool Vaulting { get; set; }

    /// A nuoto: clip di nuoto, corpo alzato alla superficie e girato verso il moto, arma riposta.
    public bool Swimming { get; set; }

    /// Seduti al governo di una barca (pilota). Lo deduce il bridge dallo stato replicato.
    public bool Seated { get; set; }

    /// Posa seduta: 0 = barra del fuoribordo (Sitting_Idle), 1 = volante (Driving).
    public float SeatPose { get; set; }

    /// Dove vanno le mani da seduti (barra, volante): nodi della barca, letti a ogni frame. Null =
    /// quella mano resta come nella clip.
    public Node3D? SeatLeftHand { get; set; }
    public Node3D? SeatRightHand { get; set; }

    /// La sinistra impugna la barra (pugno su un tubo che va verso prua), non un volante.
    public bool SeatTiller { get; set; }

    /// Arma all'occhio (mira col tasto destro).
    public bool Aiming { get; set; }

    public bool Reloading { get; set; }
    public bool Dead { get; set; }

    /// Sguardo in coordinate mondo, in radianti: imbardata (zero guarda +Z) e inclinazione.
    public float LookYaw { get; set; }

    public float LookPitch { get; set; }

    /// Arma impugnata, o null da disarmato.
    public WeaponAnimationSet? Weapon { get; set; }

    /// <summary>
    /// True sull'avatar del giocatore locale: l'arma in terza persona fa solo ombra, perche' in
    /// vista c'e' il viewmodel.
    /// </summary>
    public bool FirstPersonOwner { get; set; }

    /// <summary>
    /// Solo con <see cref="FirstPersonOwner"/>: il giocatore locale si sta guardando da fuori (la
    /// camera corrente non e' quella in prima persona, es. la visuale esterna al timone). Corpo intero
    /// e arma visibili invece delle copie della prima persona.
    /// </summary>
    public bool ThirdPersonView { get; set; }

    // ====================================================================================
    //  Uscite
    // ====================================================================================

    /// L'arma in terza persona e la sua bocca.
    public WeaponHoldRig Hold { get; private set; } = null!;

    public Skeleton3D? Skeleton { get; private set; }

    private AnimationTree? _tree;
    private BodyAimModifier? _bodyAim;
    private ArmIkModifier? _armIk;
    private readonly RandomNumberGenerator _rng = new();

    private Vector2 _velocity;
    private float _crouchWeight;
    private float _airWeight;
    private float _hipYaw;
    private bool _backward;
    private bool _wasDead;

    // Nuoto: peso del ramo, peso delle bracciate (orizzontale) e rotazione del corpo verso il moto.
    private float _swimWeight;
    private float _seatWeight;
    private bool _seatedView;
    private bool _thirdPersonView;
    private float _swimStroke;
    private float _swimYaw;
    private bool _swimBackward;

    /// Manovra di parkour in corso: le pose le danno delle curve sul tempo, non una clip.
    private enum Maneuver
    {
        None,

        /// Si passa oltre l'ostacolo: mani sul bordo, gambe raccolte, corpo proteso in avanti.
        Vault,

        /// Ci si issa in cima: appesi per le mani, poi ginocchia sul bordo, poi in piedi.
        Mantle,
    }

    /// Quanto conta ogni ingrediente della posa in un istante della manovra.
    private struct ManeuverPose
    {
        public float Tuck;
        public float Air;
        public float Lean;
        public float Hands;

        /// Di quanto il corpo sta piu' in basso della traiettoria, in metri.
        public float Drop;
    }

    private Maneuver _maneuver = Maneuver.None;
    private float _maneuverTime;
    private float _maneuverDuration = 1f;
    private bool _maneuverConfirmed;
    private Vector3 _ledgePoint;
    private Vector3 _wallNormal;
    private float _ledgeHeight;
    private Node3D? _bodyNode;

    public override void _Ready()
    {
        Hold = new WeaponHoldRig { Name = "WeaponHold" };

        Node body = GetNode("Body");
        _bodyNode = body as Node3D;
        Skeleton = body.FindChild("GeneralSkeleton", true, false) as Skeleton3D;
        if (Skeleton == null)
        {
            AddChild(Hold);
            GD.PushWarning("[CharacterAnimator] nessun GeneralSkeleton sotto Body: controlla la BoneMap nell'import del modello.");
            return;
        }

        // L'arma e' figlia dello SCHELETRO: vive nel suo spazio e ne eredita il movimento.
        Skeleton.AddChild(Hold);

        // Differito per due ragioni: i bridge impostano le velocita' nel proprio _Ready, che
        // viene DOPO questo; e creare un modificatore mentre lo scheletro sta ancora entrando
        // nell'albero pianta il processo.
        CallDeferred(MethodName.Build);
    }

    private void Build()
    {
        Node body = GetNode("Body");
        if (body.FindChild("AnimationPlayer", true, false) is not AnimationPlayer player)
        {
            GD.PushWarning("[CharacterAnimator] nessun AnimationPlayer sotto Body: il corpo restera' in T-pose.");
            return;
        }

        _tree = new AnimationTree { Name = "AnimationTree" };
        AddChild(_tree);
        foreach (StringName library in player.GetAnimationLibraryList())
            _tree.AddAnimationLibrary(library, player.GetAnimationLibrary(library));
        _tree.RootNode = _tree.GetPathTo(player.GetNode(player.RootNode));

        foreach (string clip in new[]
        {
            ClipIdle, ClipWalk, ClipJog, ClipSprint, ClipCrouchIdle, ClipCrouchMove, ClipAir,
            ClipLand, ClipHitChest, ClipHitHead, ClipDeath, ClipSwimIdle, ClipSwimMove, ClipSit, ClipDrive,
        })
        {
            if (!_tree.HasAnimation(clip))
                GD.PushWarning($"[CharacterAnimator] clip '{clip}' assente dalla libreria: quel ramo restera' in T-pose.");
        }

        _tree.TreeRoot = BuildTree();
        _tree.Active = true;

        // Un Transition non produce NULLA finche' non riceve la prima richiesta: senza queste
        // due righe l'intero albero (Life e' l'ultimo nodo) resterebbe muto, cioe' in T-pose.
        _tree.Set(LifeRequest, "alive");
        _tree.Set(HitPoseRequest, "chest");

        // L'ordine dei figli dello scheletro e' l'ordine di esecuzione: prima il busto, poi le
        // braccia, che sono un vincolo e vanno risolte dopo tutto cio' che muove le spalle.
        _bodyAim = new BodyAimModifier { Name = "BodyAim" };
        Skeleton!.AddChild(_bodyAim);
        _armIk = new ArmIkModifier { Name = "ArmIk", Hold = Hold };
        Skeleton.AddChild(_armIk);
    }

    private AnimationNodeBlendTree BuildTree()
    {
        var tree = new AnimationNodeBlendTree();

        var move = new AnimationNodeBlendSpace1D { MinSpace = 0f, MaxSpace = RunSpeed, SyncMode = AnimationNodeBlendSpace1D.SyncModeEnum.Independent };
        move.AddBlendPoint(Clip(ClipIdle), 0f, name: ClipIdle);
        move.AddBlendPoint(Clip(ClipWalk), Mathf.Min(StrollSpeed, WalkSpeed * 0.6f), name: ClipWalk);
        move.AddBlendPoint(Clip(ClipJog), WalkSpeed, name: ClipJog);
        move.AddBlendPoint(Clip(ClipSprint), RunSpeed, name: ClipSprint);
        tree.AddNode("Move", move);
        tree.AddNode("MoveScale", new AnimationNodeTimeScale());
        tree.ConnectNode("MoveScale", 0, "Move");

        var crouch = new AnimationNodeBlendSpace1D { MinSpace = 0f, MaxSpace = CrouchSpeed, SyncMode = AnimationNodeBlendSpace1D.SyncModeEnum.Independent };
        crouch.AddBlendPoint(Clip(ClipCrouchIdle), 0f, name: ClipCrouchIdle);
        crouch.AddBlendPoint(Clip(ClipCrouchMove), CrouchSpeed, name: ClipCrouchMove);
        tree.AddNode("Crouch", crouch);
        tree.AddNode("CrouchScale", new AnimationNodeTimeScale());
        tree.ConnectNode("CrouchScale", 0, "Crouch");

        tree.AddNode("Stance", new AnimationNodeBlend2());
        tree.ConnectNode("Stance", 0, "MoveScale");
        tree.ConnectNode("Stance", 1, "CrouchScale");

        tree.AddNode("AirPose", Clip(ClipAir));
        tree.AddNode("Air", new AnimationNodeBlend2());
        tree.ConnectNode("Air", 0, "Stance");
        tree.ConnectNode("Air", 1, "AirPose");

        // Nuoto: sopra a tutta la locomozione a piedi (in acqua non ci si accovaccia ne' si vola).
        var swim = new AnimationNodeBlendSpace1D { MinSpace = 0f, MaxSpace = SwimSpeed, SyncMode = AnimationNodeBlendSpace1D.SyncModeEnum.Independent };
        swim.AddBlendPoint(Clip(ClipSwimIdle), 0f, name: ClipSwimIdle);
        swim.AddBlendPoint(Clip(ClipSwimMove), SwimSpeed, name: ClipSwimMove);
        tree.AddNode("Swim", swim);
        tree.AddNode("SwimScale", new AnimationNodeTimeScale());
        tree.ConnectNode("SwimScale", 0, "Swim");
        tree.AddNode("SwimBlend", new AnimationNodeBlend2());
        tree.ConnectNode("SwimBlend", 0, "Air");
        tree.ConnectNode("SwimBlend", 1, "SwimScale");

        // Seduti al governo: sopra a tutto il resto della locomozione.
        tree.AddNode("SitPose", Clip(ClipSit));
        tree.AddNode("DrivePose", Clip(ClipDrive));
        tree.AddNode("Seat", new AnimationNodeBlend2());
        tree.ConnectNode("Seat", 0, "SitPose");
        tree.ConnectNode("Seat", 1, "DrivePose");
        tree.AddNode("SeatBlend", new AnimationNodeBlend2());
        tree.ConnectNode("SeatBlend", 0, "SwimBlend");
        tree.ConnectNode("SeatBlend", 1, "Seat");

        tree.AddNode("LandPose", Clip(ClipLand));
        tree.AddNode("Land", new AnimationNodeOneShot { FadeInTime = 0.08f, FadeOutTime = 0.3f });
        tree.ConnectNode("Land", 0, "SeatBlend");
        tree.ConnectNode("Land", 1, "LandPose");

        // Reazione al colpo: solo busto e braccia, le gambe continuano a camminare.
        var hitPose = new AnimationNodeTransition { XfadeTime = 0f };
        hitPose.AddInput("chest");
        hitPose.AddInput("head");
        tree.AddNode("HitChest", Clip(ClipHitChest));
        tree.AddNode("HitHead", Clip(ClipHitHead));
        tree.AddNode("HitPose", hitPose);
        tree.ConnectNode("HitPose", 0, "HitChest");
        tree.ConnectNode("HitPose", 1, "HitHead");

        var hit = new AnimationNodeOneShot { FadeInTime = 0.04f, FadeOutTime = 0.18f, FilterEnabled = true };
        foreach (string bone in HumanoidBones.SpineChain)
            hit.SetFilterPath(TrackPrefix + bone, true);
        foreach (string side in new[] { "Left", "Right" })
        {
            foreach (string bone in new[] { "Shoulder", "UpperArm", "LowerArm", "Hand" })
                hit.SetFilterPath(TrackPrefix + side + bone, true);
        }

        tree.AddNode("Hit", hit);
        tree.ConnectNode("Hit", 0, "Land");
        tree.ConnectNode("Hit", 1, "HitPose");

        var life = new AnimationNodeTransition { XfadeTime = 0.2f };
        life.AddInput("alive");
        life.AddInput("dead");
        tree.AddNode("DeathPose", Clip(ClipDeath));
        tree.AddNode("Life", life);
        tree.ConnectNode("Life", 0, "Hit");
        tree.ConnectNode("Life", 1, "DeathPose");

        tree.ConnectNode("output", 0, "Life");
        return tree;
    }

    private static AnimationNodeAnimation Clip(string name) => new() { Animation = name };

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _velocity = _velocity.Lerp(LocalVelocity, SkeletonMath.Damp(VelocitySmoothing, dt));
        float speed = _velocity.Length();

        ManeuverPose maneuver = StepManeuver(dt);
        bool inManeuver = _maneuver != Maneuver.None;

        _crouchWeight = Mathf.Lerp(_crouchWeight, Crouching ? 1f : 0f, SkeletonMath.Damp(BlendSpeed, dt));

        // In una manovra la posa di volo la decide la curva; fuori, il contatto col suolo. Se la
        // manovra e' in corso ma il suo evento non e' arrivato, resta la posa di volo di prima.
        if (inManeuver)
            _airWeight = maneuver.Air;
        else
            _airWeight = Mathf.Lerp(_airWeight, Grounded && !Vaulting ? 0f : 1f, SkeletonMath.Damp(AirBlendSpeed, dt));

        UpdateSwivel(speed, dt);
        UpdateSwim(speed, dt);
        _seatWeight = Mathf.Lerp(_seatWeight, Seated && !Dead ? 1f : 0f, SkeletonMath.Damp(SeatBlendSpeed, dt));
        bool seatedView = _seatWeight > 0.5f;
        if (FirstPersonOwner && (seatedView != _seatedView || ThirdPersonView != _thirdPersonView))
        {
            // In prima persona, seduti, si vedono le proprie mani sul governo; in terza persona il
            // corpo intero (FirstPersonBodyView).
            _seatedView = seatedView;
            _thirdPersonView = ThirdPersonView;
            FirstPersonBodyView.SetView(this, _seatedView, _thirdPersonView);
        }
        UpdateWeapon(speed, dt);

        if (_bodyNode != null)
            _bodyNode.Transform = BodyOffset(maneuver.Drop);

        if (_tree == null)
            return;

        float direction = _backward ? -1f : 1f;
        _tree.Set(MovePosition, Mathf.Min(speed, RunSpeed));
        _tree.Set(MoveScale, direction);
        _tree.Set(CrouchPosition, Mathf.Min(speed, CrouchSpeed));
        _tree.Set(CrouchScale, direction);
        _tree.Set(StanceAmount, Mathf.Max(_crouchWeight, maneuver.Tuck));
        _tree.Set(AirAmount, _airWeight);

        // Oltre SwimSpeed (nuoto veloce) le bracciate accelerano invece di uscire dallo spazio di blend.
        _tree.Set(SwimPosition, Mathf.Min(speed, SwimSpeed));
        _tree.Set(SwimScale, (_swimBackward ? -1f : 1f) * Mathf.Max(1f, speed / Mathf.Max(SwimSpeed, 0.1f)));
        _tree.Set(SwimAmount, _swimWeight);
        _tree.Set(SeatAmount, _seatWeight);
        _tree.Set(SeatPoseAmount, Mathf.Clamp(SeatPose, 0f, 1f));

        if (Dead != _wasDead)
        {
            _wasDead = Dead;
            _tree.Set(LifeRequest, Dead ? "dead" : "alive");
        }

        // Sguardo nello spazio dello scheletro. Il rig ruota solo attorno alla verticale, quindi
        // basta togliere la sua imbardata; il mezzo giro e' lo stesso della camera (-Z = sguardo).
        float relativeYaw = Mathf.AngleDifference(GlobalRotation.Y, LookYaw);

        // Da morti la posa e' tutta della clip: niente mira del busto, niente mani sull'arma.
        float alive = Dead ? 0f : 1f;
        if (_bodyAim != null)
        {
            // A nuoto il busto non segue lo sguardo: sul corpo orizzontale di Swim_Fwd le rotazioni
            // "attorno alla verticale" dello scheletro diventerebbero un rollio. Gira il corpo intero.
            _bodyAim.Influence = alive * (1f - _swimWeight);
            _bodyAim.HipYaw = -_hipYaw * (1f - _airWeight) * (inManeuver ? 0f : 1f) * (1f - _seatWeight);
            _bodyAim.LookYaw = relativeYaw;
            _bodyAim.LookPitch = LookPitch;
            _bodyAim.LeanPitch = maneuver.Lean;
        }

        if (_armIk != null)
        {
            _armIk.Influence = alive;
            _armIk.LookBasis = Basis.FromEuler(new Vector3(LookPitch, relativeYaw + Mathf.Pi, 0f));
            FillLedgeGoals(maneuver.Hands);
            if (_seatWeight > 0.001f && maneuver.Hands <= 0.001f)
                FillSeatGoals(_seatWeight);
        }
    }

    /// <summary>
    /// Avanza la manovra di parkour e ne restituisce la posa. Fuori manovra tutti i pesi sono zero.
    ///
    /// Non esistono clip di scavalcamento nella libreria: la posa si compone da cio' che c'e' —
    /// l'accovacciata come "gambe raccolte", la posa di volo come "appeso" — piu' le mani portate
    /// sul bordo in IK e il busto proteso. La traiettoria la scrive <c>CharacterMotor</c>; qui si
    /// segue solo il tempo, che e' lo stesso (<c>VaultDuration</c> / <c>MantleDuration</c>).
    /// </summary>
    private ManeuverPose StepManeuver(float dt)
    {
        if (_maneuver == Maneuver.None)
            return default;

        _maneuverTime += dt;
        _maneuverConfirmed |= Vaulting;

        // Lo stato replicato dice che la manovra e' finita prima del previsto (interrotta, o
        // durata diversa da quella stimata): si salta alla coda della curva invece di restare appesi.
        float tail = _maneuverDuration * 0.85f;
        if (_maneuverConfirmed && !Vaulting && _maneuverTime < tail)
            _maneuverTime = tail;

        float t = Mathf.Clamp(_maneuverTime / Mathf.Max(_maneuverDuration, 0.01f), 0f, 1f);
        if (t >= 1f && !Vaulting)
        {
            _maneuver = Maneuver.None;
            return default;
        }

        if (_maneuver == Maneuver.Vault)
        {
            return new ManeuverPose
            {
                Tuck = Bump(t, 0.05f, 0.30f, 0.60f, 0.90f),
                Air = 0f,
                Lean = 0.45f * Bump(t, 0.10f, 0.35f, 0.55f, 0.85f),
                Hands = Bump(t, 0.08f, 0.22f, 0.50f, 0.68f),
            };
        }

        // La traiettoria di CharacterMotor porta i PIEDI all'altezza del bordo prima di avanzare
        // (era pensata per una clip che teneva il corpo appeso sotto la radice). Senza quella
        // clip il corpo resterebbe in piedi a mezz'aria accanto al muro: lo si tiene piu' in
        // basso finche' ci si issa — petto al bordo, non piedi — e lo si riallinea mentre sale.
        float hang = Mathf.Clamp(_ledgeHeight - 0.5f, 0f, 0.95f);
        return new ManeuverPose
        {
            Tuck = Bump(t, 0.55f, 0.72f, 0.84f, 1.00f),
            Air = Bump(t, 0.00f, 0.08f, 0.50f, 0.68f),
            Lean = 0.35f * Bump(t, 0.50f, 0.65f, 0.80f, 0.95f),
            Hands = Bump(t, 0.00f, 0.12f, 0.72f, 0.86f),
            Drop = hang * Bump(t, 0.00f, 0.62f, 0.62f, 0.86f),
        };
    }

    /// Sale fra <paramref name="in0"/> e <paramref name="in1"/>, scende fra <paramref name="out0"/> e <paramref name="out1"/>.
    private static float Bump(float t, float in0, float in1, float out0, float out1) =>
        Mathf.SmoothStep(in0, in1, t) * (1f - Mathf.SmoothStep(out0, out1, t));

    /// <summary>
    /// Mani sul governo: sul volante (dita attorno alla corona, verso il piantone) o sulla barra
    /// (pugno chiuso su un tubo che corre verso prua). I bersagli sono nodi della barca che si
    /// muovono con volante e motore, quindi le mani girano con loro.
    /// </summary>
    private void FillSeatGoals(float weight)
    {
        if (Skeleton == null)
            return;
        Transform3D toSkeleton = Skeleton.GlobalTransform.AffineInverse();
        FillSeatGoal(ref _armIk!.Left, SeatLeftHand, toSkeleton, weight, left: true);
        FillSeatGoal(ref _armIk.Right, SeatRightHand, toSkeleton, weight, left: false);
    }

    private void FillSeatGoal(ref ArmIkModifier.HandGoal goal, Node3D? target, Transform3D toSkeleton, float weight, bool left)
    {
        if (target == null || !IsInstanceValid(target))
        {
            goal.Weight = 0f;
            return;
        }

        Transform3D t = target.GlobalTransform;
        Vector3 up = Vector3.Up;
        // Il rig guarda +Z e la sua sinistra e' +X.
        Vector3 bodyForward = GlobalTransform.Basis.Z;
        Vector3 outward = (left ? 1f : -1f) * GlobalTransform.Basis.X;
        Vector3 fingers, across;
        if (SeatTiller)
        {
            // Barra: il tubo va verso prua; pugno sopra, dita che scendono attorno al tubo.
            Vector3 bar = (t.Basis * Vector3.Forward).Normalized();
            fingers = (-up * 0.8f - outward * 0.2f).Normalized();
            across = bar;
        }
        else
        {
            // Volante: il piantone (Y del nodo ruota) va dal pilota verso la consolle; le dita vanno verso
            // la consolle e il centro, l'asse delle nocche segue la corona.
            Node3D? wheel = target.GetParent() as Node3D;
            Vector3 axis = (wheel?.GlobalTransform.Basis.Y ?? up).Normalized();
            Vector3 hub = wheel?.GlobalPosition ?? t.Origin;
            Vector3 radial = (t.Origin - hub).Normalized();
            Vector3 tangent = axis.Cross(radial).Normalized();
            if (tangent.Dot(up) < 0f)
                tangent = -tangent;
            // Pollici in su su entrambe le mani: l'indice sta sopra il mignolo, l'asse delle nocche
            // sale lungo la corona per tutte e due.
            fingers = (axis * 0.3f - radial * 0.7f).Normalized();
            across = tangent;
        }

        // Il bersaglio e' il punto di PRESA (nel palmo), l'IK porta il POLSO: lo si arretra lungo le dita.
        goal.Position = toSkeleton * (t.Origin - fingers * SeatGripToWrist);
        goal.Fingers = toSkeleton.Basis * fingers;
        goal.Across = toSkeleton.Basis * across;
        goal.PoleDirection = toSkeleton.Basis * (outward * 0.6f + Vector3.Down * 0.6f - bodyForward * 0.2f);
        goal.Weight = weight;
        goal.Curl = 0.85f;
    }

    /// Mani sul bordo dell'ostacolo: palmi in giu', dita in avanti, a larghezza di spalle.
    private void FillLedgeGoals(float weight)
    {
        _armIk!.Right.Weight = weight;
        _armIk.Left.Weight = weight;
        if (weight <= 0.001f || Skeleton == null)
            return;

        Transform3D toSkeleton = Skeleton.GlobalTransform.AffineInverse();
        Vector3 forward = -_wallNormal;
        Vector3 right = forward.Cross(Vector3.Up).Normalized();

        // Un palmo oltre lo spigolo e appena sopra, cosi' la mano appoggia sul piano.
        Vector3 rest = _ledgePoint + forward * 0.08f + Vector3.Up * 0.03f;

        FillLedgeGoal(ref _armIk.Right, toSkeleton, rest + right * 0.22f, forward, -right, right, weight);
        FillLedgeGoal(ref _armIk.Left, toSkeleton, rest - right * 0.22f, forward, right, -right, weight);
    }

    private static void FillLedgeGoal(
        ref ArmIkModifier.HandGoal goal, Transform3D toSkeleton, Vector3 position,
        Vector3 fingers, Vector3 across, Vector3 outward, float weight)
    {
        goal.Position = toSkeleton * position;
        goal.Fingers = toSkeleton.Basis * fingers;
        goal.Across = toSkeleton.Basis * across;
        goal.PoleDirection = toSkeleton.Basis * (outward * 0.7f - fingers * 0.4f + Vector3.Down * 0.3f);
        goal.Weight = weight;
        goal.Curl = 0.15f;
    }

    /// <summary>
    /// Direzione delle gambe. La libreria ha solo clip in avanti: di lato si gira il bacino verso
    /// il moto, all'indietro la clip gira al contrario col bacino girato dalla parte opposta.
    /// </summary>
    private void UpdateSwivel(float speed, float dt)
    {
        float target = 0f;
        if (speed > 0.25f)
        {
            // Zero = avanti, positivo = verso destra.
            float angle = Mathf.Atan2(_velocity.X, _velocity.Y);
            float abs = Mathf.Abs(angle);

            // Isteresi: senza, camminando di lato il verso della clip si invertirebbe a ogni frame.
            if (_backward ? abs < Mathf.DegToRad(80f) : abs > Mathf.DegToRad(110f))
                _backward = !_backward;

            target = _backward ? angle - Mathf.Sign(angle) * Mathf.Pi : angle;
            float max = Mathf.DegToRad(MaxHipSwivelDegrees);
            target = Mathf.Clamp(target, -max, max);
        }
        else
        {
            _backward = false;
        }

        _hipYaw = Mathf.Lerp(_hipYaw, target, SkeletonMath.Damp(10f, dt));
    }

    /// <summary>
    /// Nuoto: peso del ramo, peso delle bracciate e verso del corpo. Le clip sono solo in avanti come
    /// la locomozione: di lato si gira il corpo INTERO verso il moto (non il bacino: Swim_Fwd e'
    /// orizzontale), all'indietro le bracciate vanno al contrario, con la stessa isteresi.
    /// </summary>
    private void UpdateSwim(float speed, float dt)
    {
        _swimWeight = Mathf.Lerp(_swimWeight, Swimming && !Dead ? 1f : 0f, SkeletonMath.Damp(SwimBlendSpeed, dt));
        _swimStroke = Mathf.Lerp(_swimStroke, Swimming ? Mathf.Clamp(speed / Mathf.Max(SwimSpeed, 0.1f), 0f, 1f) : 0f,
            SkeletonMath.Damp(SwimBlendSpeed, dt));

        float target = 0f;
        if (Swimming && speed > 0.2f)
        {
            float angle = Mathf.Atan2(_velocity.X, _velocity.Y);
            float abs = Mathf.Abs(angle);
            if (_swimBackward ? abs < Mathf.DegToRad(80f) : abs > Mathf.DegToRad(110f))
                _swimBackward = !_swimBackward;

            target = _swimBackward ? angle - Mathf.Sign(angle) * Mathf.Pi : angle;
            float max = Mathf.DegToRad(MaxSwimSwivelDegrees);
            target = Mathf.Clamp(target, -max, max);
        }
        else if (!Swimming)
        {
            _swimBackward = false;
        }

        _swimYaw = Mathf.Lerp(_swimYaw, target, SkeletonMath.Damp(4f, dt));
    }

    /// <summary>
    /// Trasformata del nodo <c>Body</c>: abbassamento della manovra di parkour e, a nuoto, il corpo
    /// alzato alla superficie, girato verso il moto e arretrato perche' la testa delle bracciate
    /// resti sulla verticale degli occhi nominali.
    /// </summary>
    private Transform3D BodyOffset(float drop)
    {
        // Da seduti il corpo avanza (+Z del rig = avanti): la testa sopra i piedi nominali.
        Vector3 seat = new(0f, 0f, SeatedForwardShift * _seatWeight);
        if (_swimWeight <= 0.001f)
            return new Transform3D(Basis.Identity, new Vector3(0f, -drop, 0f) + seat);

        // Yaw positivo = verso +X dello scheletro, cioe' la SUA sinistra: il moto a destra va negato.
        var basis = new Basis(Vector3.Up, -_swimYaw * _swimWeight);
        Vector3 back = basis * new Vector3(0f, 0f, -SwimForwardShift * _swimStroke);
        Vector3 lift = new(0f, SwimBodyLift * _swimWeight - drop, 0f);
        return new Transform3D(basis, lift + back * _swimWeight);
    }

    private void UpdateWeapon(float speed, float dt)
    {
        WeaponAnimationSet? weapon = Dead ? null : Weapon;
        Hold.SetWeapon(weapon);
        Hold.ShadowOnly = FirstPersonOwner && !ThirdPersonView;

        bool sprinting = Grounded && !Aiming && speed > WalkSpeed * 1.15f;
        bool stowed = Vaulting || _maneuver != Maneuver.None || Swimming || Seated;
        Hold.UpdateState(dt, Aiming, sprinting, Reloading, stowed);
    }

    // ====================================================================================
    //  Eventi one-shot
    // ====================================================================================

    /// Salto: la posa di volo entra subito, senza aspettare che lo stato "a terra" si aggiorni.
    public void TriggerJump() => _airWeight = Mathf.Max(_airWeight, 0.5f);

    /// <summary>
    /// Atterraggio, con la velocita' d'impatto in m/s. La clip parte solo sugli atterraggi duri,
    /// quelli in cui il movimento resta bloccato (<c>CharacterMotor.HardLandingLockSeconds</c>):
    /// sotto, le gambe devono poter ripartire subito.
    /// </summary>
    public void TriggerLand(float impactSpeed)
    {
        if (impactSpeed >= HardLandingSpeed)
            Request(LandRequest);
    }

    /// Sparo: rinculo dell'arma in terza persona.
    public void TriggerFire() => Hold.Kick();

    /// <summary>
    /// Scavalcamento (<paramref name="mantle"/> falso: si passa oltre) o arrampicata (vero: si
    /// resta in cima). Bordo e normale sono misure geometriche dell'ostacolo, in coordinate
    /// mondo: servono a portare le mani sul bordo. <paramref name="duration"/> e' la durata
    /// della traiettoria scritta da chi si muove, perche' posa e moto restino in fase.
    /// </summary>
    public void TriggerVault(Vector3 ledgePoint, Vector3 wallNormal, bool mantle, float duration)
    {
        if (Dead)
            return;

        var flat = new Vector3(wallNormal.X, 0f, wallNormal.Z);
        _wallNormal = flat.LengthSquared() > 0.0001f ? flat.Normalized() : -GlobalTransform.Basis.Z;
        _ledgePoint = ledgePoint;
        _ledgeHeight = ledgePoint.Y - GlobalPosition.Y;
        _maneuver = mantle ? Maneuver.Mantle : Maneuver.Vault;
        _maneuverDuration = Mathf.Max(duration, 0.1f);
        _maneuverTime = 0f;
        _maneuverConfirmed = false;
    }

    /// <summary>
    /// Reazione al colpo. <paramref name="worldDirection"/> e' la direzione di volo del
    /// proiettile calcolata dall'host: nel payload viaggia la direzione, mai il danno (CLAUDE.md §3).
    /// </summary>
    public void TriggerHitReaction(Vector3 worldDirection)
    {
        if (_tree == null || Dead)
            return;

        _tree.Set(HitPoseRequest, _rng.Randf() < 0.5f ? "chest" : "head");
        Request(HitRequest);
    }

    private void Request(string parameter) =>
        _tree?.Set(parameter, (int)AnimationNodeOneShot.OneShotRequest.Fire);
}
