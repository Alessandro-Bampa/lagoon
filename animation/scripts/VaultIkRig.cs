using Godot;

namespace Lagoon;

/// <summary>
/// Appoggia le mani sul bordo dell'ostacolo durante scavalcamento e arrampicata.
///
/// Le clip (<c>vault_low</c>, <c>mantle_high</c>) sono generiche e in place: le braccia si
/// protendono nella direzione giusta, ma il punto di contatto vero — e l'orientamento del bordo su
/// cui allineare le mani — li conosce solo chi ha misurato l'ostacolo con la sonda geometrica.
/// Qui due catene di <c>TwoBoneIK3D</c> portano le mani ESATTAMENTE sul bordo, con l'influenza
/// che sale e scende dentro la finestra di contatto della clip.
///
/// Stesse regole degli altri rig procedurali (skill character-animation):
///  - costruzione DIFFERITA: creare un modificatore mentre lo scheletro entra nell'albero
///    blocca il processo in silenzio;
///  - ogni catena ha il suo POLE NODE: senza, TwoBoneIK3D non risolve affatto (misurato);
///  - le impostazioni si dichiarano DOPO AddChild, quando il modificatore ha uno scheletro.
///
/// Pura resa (CLAUDE.md §3): il punto del bordo arriva dall'evento estetico Vaulted, gira su
/// ogni peer e non produce stato di gioco.
/// </summary>
public partial class VaultIkRig : Node3D
{
    /// Distanza laterale di ciascuna mano dal punto centrale del bordo, in metri.
    [Export] public float HandSpacing { get; set; } = 0.24f;

    /// Finestra di contatto DI DEFAULT, come frazioni della durata: le mani toccano fra In e Out.
    /// La manovra puo' sovrascriverla in <see cref="Begin"/>: arrampicarsi tiene le mani sul bordo
    /// molto piu' a lungo che scavalcare, ed e' una proprieta' della clip, non del rig.
    [Export] public float ContactIn { get; set; } = 0.12f;

    [Export] public float ContactOut { get; set; } = 0.55f;

    /// Velocita' della rampa dell'influenza, in frazione al secondo.
    [Export] public float RampSpeed { get; set; } = 18.0f;

    private Skeleton3D? _skeleton;
    private TwoBoneIK3D? _ik;
    private Node3D? _leftTarget;
    private Node3D? _rightTarget;

    private float _elapsed = -1f;
    private float _duration = 0.9f;
    private float _contactIn;
    private float _contactOut;
    private Vector3 _ledge;
    private Vector3 _tangent = Vector3.Right;
    private float _weight;
    private float _active;

    /// <summary>
    /// Quanto le mani stanno sul BORDO invece che sull'arma, da 0 a 1.
    ///
    /// Lo legge <see cref="CharacterAnimator"/> per cedere le mani allo scavalcamento: sono due
    /// vincoli sulle stesse ossa, e come per il bacino (<c>UpdatePelvisOffset</c>) devono avere un
    /// arbitro solo, o si sovrascrivono a vicenda senza dare errori.
    /// </summary>
    public float HandsOnLedge => _weight;

    /// <summary>
    /// Quanto e' in corso una manovra di parkour, da 0 a 1, sull'INTERA durata e non solo nella
    /// finestra di contatto.
    ///
    /// E' distinto da <see cref="HandsOnLedge"/> perche' risponde a una domanda diversa: le mani
    /// servono al bordo solo mentre lo toccano, ma l'arma va tolta di mezzo per tutto il gesto —
    /// vederla ricomparire fra lo stacco e l'appoggio sarebbe peggio che non toglierla affatto.
    /// </summary>
    public float ParkourActive => _active;

    public override void _Ready()
    {
        _skeleton = SkeletonLocator.Find(this);
        if (_skeleton == null)
        {
            GD.PushWarning("[VaultIkRig] nessuno Skeleton3D sotto il rig: mani senza IK sul bordo.");
            return;
        }

        CallDeferred(MethodName.BuildIk);
    }

    private void BuildIk()
    {
        if (_skeleton == null)
            return;

        _leftTarget = new Node3D { Name = "VaultLeftHandTarget", TopLevel = true };
        _rightTarget = new Node3D { Name = "VaultRightHandTarget", TopLevel = true };
        var leftPole = new Node3D { Name = "VaultLeftElbowPole" };
        var rightPole = new Node3D { Name = "VaultRightElbowPole" };

        _skeleton.AddChild(_leftTarget);
        _skeleton.AddChild(_rightTarget);
        _skeleton.AddChild(leftPole);
        _skeleton.AddChild(rightPole);

        // Con le mani appoggiate su un bordo davanti al corpo i gomiti puntano in fuori e in
        // alto: il polo sta sopra il gomito a riposo, spostato lateralmente dal proprio lato.
        Vector3 leftElbow = _skeleton.GetBoneGlobalRest(_skeleton.FindBone("LeftForeArm")).Origin;
        Vector3 rightElbow = _skeleton.GetBoneGlobalRest(_skeleton.FindBone("RightForeArm")).Origin;
        leftPole.Position = leftElbow + new Vector3(0.5f, 0.6f, 0f);
        rightPole.Position = rightElbow + new Vector3(-0.5f, 0.6f, 0f);

        _ik = new TwoBoneIK3D { Name = "VaultIk", Influence = 0f };
        _skeleton.AddChild(_ik);
        _ik.SetSettingCount(2);

        Configure(0, "LeftArm", "LeftForeArm", "LeftHand", _leftTarget, leftPole);
        Configure(1, "RightArm", "RightForeArm", "RightHand", _rightTarget, rightPole);
    }

    private void Configure(int index, string upper, string lower, string hand, Node3D target, Node3D pole)
    {
        _ik!.SetRootBoneName(index, upper);
        _ik.SetMiddleBoneName(index, lower);
        _ik.SetEndBoneName(index, hand);
        _ik.SetTargetNode(index, _ik.GetPathTo(target));
        _ik.SetPoleNode(index, _ik.GetPathTo(pole));
    }

    /// <summary>
    /// Avvia la finestra di contatto sul bordo misurato.
    ///
    /// <paramref name="duration"/> e' la durata della clip in corso: la finestra e' espressa come
    /// sue frazioni, cosi' clip e IK restano sincronizzate anche se la clip cambia lunghezza.
    /// </summary>
    /// <param name="ledgePoint">Punto d'appiglio misurato, in coordinate mondo.</param>
    /// <param name="wallNormal">
    /// Normale orizzontale della parete: da' la tangente VERA del bordo. Prima si stimava con la
    /// perpendicolare alla direzione corpo → bordo, che su un muro preso di sbieco metteva le mani
    /// lungo una linea che col bordo non c'entrava nulla.
    /// </param>
    /// <param name="contactIn">Inizio del contatto, in frazioni della durata.</param>
    /// <param name="contactOut">Fine del contatto, in frazioni della durata.</param>
    public void Begin(Vector3 ledgePoint, Vector3 wallNormal, float duration,
        float contactIn = -1f, float contactOut = -1f)
    {
        _ledge = ledgePoint;
        _duration = Mathf.Max(duration, 0.1f);
        _contactIn = contactIn >= 0f ? contactIn : ContactIn;
        _contactOut = contactOut >= 0f ? contactOut : ContactOut;
        _elapsed = 0f;

        Vector3 flat = new Vector3(wallNormal.X, 0f, wallNormal.Z);
        _tangent = flat.LengthSquared() > 0.0001f
            ? flat.Normalized().Cross(Vector3.Up).Normalized()
            : Vector3.Right;
    }

    public override void _Process(double delta)
    {
        if (_ik == null || _skeleton == null || _leftTarget == null || _rightTarget == null)
            return;

        float dt = (float)delta;
        bool inContact = false;
        bool running = false;

        if (_elapsed >= 0f)
        {
            _elapsed += dt;
            float t = _elapsed / _duration;
            inContact = t >= _contactIn && t <= _contactOut;
            running = t <= 1f;
            if (t > 1f)
                _elapsed = -1f;
        }

        float ramp = 1f - Mathf.Exp(-RampSpeed * dt);
        _weight = Mathf.Lerp(_weight, inContact ? 1f : 0f, ramp);

        // Il rientro dell'arma e' piu' lento della presa sul bordo: rimetterla in mano di scatto
        // nell'ultimo fotogramma della clip si vede, lasciarla sparire un attimo in piu' no.
        _active = Mathf.Lerp(_active, running ? 1f : 0f, running ? ramp : ramp * 0.35f);
        _ik.Influence = _weight;

        if (_weight <= 0.001f)
            return;

        // Le mani si dispongono ai lati del punto di aggancio, lungo la tangente del bordo.
        _leftTarget.GlobalPosition = _ledge + _tangent * HandSpacing;
        _rightTarget.GlobalPosition = _ledge - _tangent * HandSpacing;
    }
}
