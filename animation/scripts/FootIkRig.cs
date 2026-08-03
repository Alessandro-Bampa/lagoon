using Godot;

namespace Lagoon;

/// <summary>
/// Appoggia i piedi sul terreno vero invece che sul piano immaginario in cui e' stata registrata la
/// clip.
///
/// E' la parte del sistema che REAGISCE al terreno: su una rampa, su uno scalino o sul ponte
/// inclinato di una barca, le clip di locomozione continuano a mettere i piedi tutti alla stessa
/// quota, e il personaggio affonda da un lato e galleggia dall'altro. Qui ogni piede cerca il suolo
/// col proprio raggio, l'IK lo porta li', e il bacino scende quanto basta perche' il piede piu' in
/// basso ci arrivi senza stirare la gamba.
///
/// Nessuna clip in piu': e' esattamente il tipo di cosa che non si puo' animare a mano, perche'
/// dipende dalla geometria sotto i piedi in quel preciso istante.
///
/// Pura resa (CLAUDE.md §3): gira su ogni peer da stato gia' replicato, non produce stato di gioco.
/// </summary>
public partial class FootIkRig : Node3D
{
    private const string LeftUpper = "LeftUpLeg";
    private const string LeftLower = "LeftLeg";
    private const string LeftFoot = "LeftFoot";
    private const string RightUpper = "RightUpLeg";
    private const string RightLower = "RightLeg";
    private const string RightFoot = "RightFoot";

    /// <summary>
    /// Quanto sopra la caviglia animata parte il raggio, in metri. Deve superare l'altezza del
    /// gradino piu' alto che si vuole assecondare, altrimenti il raggio parte gia' dentro lo scalino.
    /// </summary>
    [Export] public float ProbeAbove { get; set; } = 0.45f;

    /// Quanto sotto la caviglia animata arriva il raggio, in metri.
    [Export] public float ProbeBelow { get; set; } = 0.55f;

    /// <summary>
    /// Dislivello massimo che l'IK asseconda, in metri. Oltre, si lascia perdere: significa che il
    /// piede sta sopra un burrone o dentro un muro, e stirare la gamba sarebbe peggio.
    /// </summary>
    [Export] public float MaxFootOffset { get; set; } = 0.40f;

    /// Inclinazione massima con cui il piede si adatta alla pendenza, in gradi.
    [Export] public float MaxFootPitchDegrees { get; set; } = 35.0f;

    /// Velocita' con cui i piedi inseguono il terreno, in frazione al secondo. Bassa = piu' morbido.
    [Export] public float FootSmoothing { get; set; } = 14.0f;

    /// <summary>
    /// Velocita' orizzontale oltre la quale l'IK si spegne, in m/s.
    ///
    /// In movimento il piede passa parte del ciclo in aria e l'IK, che non sa distinguere le due
    /// fasi senza curve di contatto nelle clip, finirebbe per tirare in basso un piede che sta
    /// volando. Il valore precedente (5, sopra la camminata) lo lasciava acceso mentre si cammina:
    /// su una rampa i raycast inchiodavano i piedi a quote diverse a ogni passo e il personaggio
    /// "scattava" in salita. L'IK serve DA FERMI (dislivelli, pendenze, bordi): in cammino ci
    /// pensa la clip.
    /// </summary>
    [Export] public float DisableAboveSpeed { get; set; } = 0.6f;

    // ====================================================================================
    //  Stato in ingresso
    // ====================================================================================

    /// A terra. In aria l'IK e' spento: non c'e' nessun suolo da assecondare.
    public bool Grounded { get; set; } = true;

    /// Velocita' locale dell'avatar, per spegnere l'IK in corsa.
    public Vector2 LocalVelocity { get; set; }

    /// <summary>
    /// Di quanto va abbassato il bacino perche' il piede piu' in basso arrivi a terra, in metri.
    /// Lo legge <see cref="CharacterAnimator"/>, che e' l'unico a scrivere la posizione del rig.
    /// </summary>
    public float PelvisDrop { get; private set; }

    private Skeleton3D? _skeleton;
    private TwoBoneIK3D? _ik;
    private Node3D? _leftTarget;
    private Node3D? _rightTarget;
    private int _leftFoot = -1;
    private int _rightFoot = -1;
    private float _ankleHeight;
    private float _weight;
    private float _leftOffset;
    private float _rightOffset;

    public override void _Ready()
    {
        _skeleton = SkeletonLocator.Find(this);
        if (_skeleton == null)
        {
            GD.PushWarning("[FootIkRig] nessuno Skeleton3D sotto il rig: i piedi resteranno alle clip.");
            return;
        }

        _leftFoot = _skeleton.FindBone(LeftFoot);
        _rightFoot = _skeleton.FindBone(RightFoot);
        if (_leftFoot < 0 || _rightFoot < 0)
        {
            GD.PushWarning("[FootIkRig] ossa dei piedi assenti dal rig.");
            return;
        }

        // Altezza della caviglia sopra il suolo nella posa di RIPOSO: e' l'offset da lasciare fra il
        // punto di contatto e il bersaglio dell'IK, altrimenti la caviglia finisce dentro il terreno.
        _ankleHeight = _skeleton.GetBoneGlobalRest(_leftFoot).Origin.Y;

        // Differito come gli altri rig: costruire un modificatore mentre lo scheletro entra
        // nell'albero blocca il processo in silenzio.
        CallDeferred(MethodName.BuildIk);
    }

    private void BuildIk()
    {
        if (_skeleton == null)
            return;

        _leftTarget = new Node3D { Name = "LeftFootTarget", TopLevel = true };
        _rightTarget = new Node3D { Name = "RightFootTarget", TopLevel = true };
        var leftPole = new Node3D { Name = "LeftKneePole" };
        var rightPole = new Node3D { Name = "RightKneePole" };

        _skeleton.AddChild(_leftTarget);
        _skeleton.AddChild(_rightTarget);
        _skeleton.AddChild(leftPole);
        _skeleton.AddChild(rightPole);

        // Il polo del ginocchio sta DAVANTI alla gamba: le ginocchia si piegano in avanti, e senza
        // polo TwoBoneIK3D non risolve affatto la catena (misurato).
        leftPole.Position = _skeleton.GetBoneGlobalRest(_skeleton.FindBone(LeftLower)).Origin
            + new Vector3(0f, 0f, 1.0f);
        rightPole.Position = _skeleton.GetBoneGlobalRest(_skeleton.FindBone(RightLower)).Origin
            + new Vector3(0f, 0f, 1.0f);

        // Una sola istanza per entrambe le gambe: TwoBoneIK3D tiene piu' catene, e l'influenza e'
        // per modificatore — che qui va bene, perche' i due piedi si accendono e si spengono insieme.
        _ik = new TwoBoneIK3D { Name = "FootIk", Influence = 0f };
        _skeleton.AddChild(_ik);
        _ik.SetSettingCount(2);

        Configure(0, LeftUpper, LeftLower, LeftFoot, _leftTarget, leftPole);
        Configure(1, RightUpper, RightLower, RightFoot, _rightTarget, rightPole);
    }

    private void Configure(int index, string upper, string lower, string foot, Node3D target, Node3D pole)
    {
        _ik!.SetRootBoneName(index, upper);
        _ik.SetMiddleBoneName(index, lower);
        _ik.SetEndBoneName(index, foot);
        _ik.SetTargetNode(index, _ik.GetPathTo(target));
        _ik.SetPoleNode(index, _ik.GetPathTo(pole));
    }

    public override void _Process(double delta)
    {
        if (_ik == null || _skeleton == null || _leftTarget == null || _rightTarget == null)
            return;

        float dt = (float)delta;
        bool active = Grounded && LocalVelocity.Length() < DisableAboveSpeed;

        _weight = Mathf.Lerp(_weight, active ? 1f : 0f, Damp(FootSmoothing, dt));
        _ik.Influence = _weight;

        if (_weight <= 0.001f)
        {
            PelvisDrop = Mathf.Lerp(PelvisDrop, 0f, Damp(FootSmoothing, dt));
            return;
        }

        // Le pose lette QUI sono quelle animate, non quelle dell'IK: Godot ripristina le pose
        // sorgente al termine della passata dei modificatori. E' esattamente cio' che serve — il
        // bersaglio va calcolato da dove la clip METTEREBBE il piede, non da dove l'ha messo l'IK
        // al frame precedente, altrimenti si insegue se stessi.
        Transform3D leftPose = _skeleton.GlobalTransform * _skeleton.GetBoneGlobalPose(_leftFoot);
        Transform3D rightPose = _skeleton.GlobalTransform * _skeleton.GetBoneGlobalPose(_rightFoot);
        Vector3 leftAnkle = leftPose.Origin;
        Vector3 rightAnkle = rightPose.Origin;

        // Le pose lette sono quelle del rig GIA' abbassato dal frame precedente. Sommare
        // PelvisDrop le riporta dove starebbero a bacino fermo, e cosi' il calcolo non dipende
        // dal proprio risultato: senza questa correzione il sistema si assesta a meta' strada
        // (misurato: il piede restava quattro centimetri sopra il terreno, stabilmente).
        float restored = PelvisDrop;
        (float leftOffset, Vector3 leftNormal) = Probe(leftAnkle, restored);
        (float rightOffset, Vector3 rightNormal) = Probe(rightAnkle, restored);

        _leftOffset = Mathf.Lerp(_leftOffset, leftOffset, Damp(FootSmoothing, dt));
        _rightOffset = Mathf.Lerp(_rightOffset, rightOffset, Damp(FootSmoothing, dt));

        // Il bacino scende fino al piede piu' BASSO, e solo verso il basso. Su un RIALZO non
        // serve: basta piegare di piu' la gamba. Serve quando un piede deve scendere piu' in
        // basso di dove la gamba distesa arriva, ed e' l'unico modo per non staccarlo dal suolo.
        PelvisDrop = -Mathf.Min(0f, Mathf.Min(_leftOffset, _rightOffset));

        // I bersagli sono in coordinate MONDO (TopLevel), quindi l'abbassamento del bacino non li
        // sposta: e' il bacino a scendere verso piedi che restano dove il terreno li vuole.
        PlaceTarget(_leftTarget, leftPose, _leftOffset + restored, leftNormal);
        PlaceTarget(_rightTarget, rightPose, _rightOffset + restored, rightNormal);
    }

    /// <summary>
    /// Cerca il suolo sotto una caviglia.
    ///
    /// Ritorna lo scarto verticale rispetto a dove la caviglia starebbe A BACINO FERMO — da qui
    /// <paramref name="pelvisDrop"/>, che riporta la posa letta alla sua quota di riposo — e la
    /// normale del terreno. Scarto zero quando non si trova niente di sensato.
    /// </summary>
    private (float Offset, Vector3 Normal) Probe(Vector3 ankle, float pelvisDrop)
    {
        var query = PhysicsRayQueryParameters3D.Create(
            ankle + Vector3.Up * ProbeAbove,
            ankle - Vector3.Up * ProbeBelow,
            CollisionLayers.World | CollisionLayers.VehicleDeck | CollisionLayers.BuildingCover);

        Godot.Collections.Dictionary hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (hit.Count == 0)
            return (0f, Vector3.Up);

        var point = (Vector3)hit["position"];
        var normal = (Vector3)hit["normal"];

        float restingAnkleY = ankle.Y + pelvisDrop;
        float offset = point.Y + _ankleHeight - restingAnkleY;
        return (Mathf.Clamp(offset, -MaxFootOffset, MaxFootOffset), normal);
    }

    /// <summary>
    /// Posiziona il bersaglio di un piede e lo allinea alla pendenza, con un limite.
    ///
    /// L'orientamento parte da quello ANIMATO e ci si applica sopra la sola inclinazione del
    /// terreno. Costruirlo da zero dalla normale sembra piu' semplice ma e' sbagliato: butterebbe
    /// via la rotazione che l'osso del piede ha nella clip, e il piede si presenterebbe girato.
    /// </summary>
    private void PlaceTarget(Node3D target, Transform3D animated, float offset, Vector3 normal)
    {
        // Il piede segue la pendenza solo fino a un certo punto: su una parete quasi verticale
        // allinearsi del tutto metterebbe la caviglia in una posa che nessun essere umano assume.
        float tilt = Mathf.Acos(Mathf.Clamp(normal.Dot(Vector3.Up), -1f, 1f));
        float maxTilt = Mathf.DegToRad(MaxFootPitchDegrees);
        if (tilt > maxTilt)
            normal = Vector3.Up.Slerp(normal, maxTilt / tilt).Normalized();

        var toSlope = new Basis(new Quaternion(Vector3.Up, normal));

        target.GlobalTransform = new Transform3D(
            toSlope * animated.Basis,
            new Vector3(animated.Origin.X, animated.Origin.Y + offset, animated.Origin.Z));
    }

    private static float Damp(float speed, float dt) => 1.0f - Mathf.Exp(-speed * dt);
}
