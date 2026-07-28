using Godot;

namespace Lagoon;

/// <summary>
/// Appoggia le mani sul bordo dell'ostacolo durante lo scavalcamento.
///
/// La clip <c>vault_low</c> e' generica e in place: le braccia si protendono nella direzione
/// giusta, ma il punto di contatto vero lo conosce solo chi ha misurato l'ostacolo coi raycast.
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

    /// Finestra di contatto, come frazioni della durata: le mani toccano fra In e Out.
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
    private Vector3 _ledge;
    private float _weight;

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
    /// Avvia la finestra di contatto sul bordo misurato. <paramref name="duration"/> e' la durata
    /// della clip di vault: la finestra e' espressa come sue frazioni, cosi' clip e IK restano
    /// sincronizzate anche se la clip cambia lunghezza.
    /// </summary>
    public void Begin(Vector3 ledgePoint, float duration)
    {
        _ledge = ledgePoint;
        _duration = Mathf.Max(duration, 0.1f);
        _elapsed = 0f;
    }

    public override void _Process(double delta)
    {
        if (_ik == null || _skeleton == null || _leftTarget == null || _rightTarget == null)
            return;

        float dt = (float)delta;
        bool inContact = false;

        if (_elapsed >= 0f)
        {
            _elapsed += dt;
            float t = _elapsed / _duration;
            inContact = t >= ContactIn && t <= ContactOut;
            if (t > 1f)
                _elapsed = -1f;
        }

        _weight = Mathf.Lerp(_weight, inContact ? 1f : 0f, 1f - Mathf.Exp(-RampSpeed * dt));
        _ik.Influence = _weight;

        if (_weight <= 0.001f)
            return;

        // Le mani si dispongono ai lati del punto di aggancio, lungo la tangente del bordo —
        // che, non avendo misurato l'orientamento della parete, e' la perpendicolare alla
        // direzione corpo -> bordo sul piano orizzontale.
        Vector3 toLedge = _ledge - GlobalPosition;
        toLedge.Y = 0f;
        Vector3 tangent = toLedge.LengthSquared() > 0.0001f
            ? toLedge.Normalized().Cross(Vector3.Up)
            : Vector3.Right;

        _leftTarget.GlobalPosition = _ledge + tangent * HandSpacing;
        _rightTarget.GlobalPosition = _ledge - tangent * HandSpacing;
    }
}
