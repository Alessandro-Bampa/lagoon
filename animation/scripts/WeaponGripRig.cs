using Godot;

namespace Lagoon;

/// <summary>
/// Aggancia l'arma alla MANO del personaggio e ci mette in IK la mano di supporto.
///
/// Prima l'arma stava su un <c>Node3D</c> con offset fisso sotto <c>Visual</c>: seguiva il corpo ma
/// non la mano, quindi fluttuava accanto al fianco e non aveva alcun rapporto con la posa animata.
/// Qui il punto di presa e' figlio di un <see cref="BoneAttachment3D"/> su <c>RightHand</c>, quindi
/// e' la mano a portarsi dietro l'arma — in locomozione, sparando e da accovacciati, senza un solo
/// caso particolare.
///
/// I nodi vengono creati DA CODICE e non messi nella scena: il rig arriva da <c>Body_Base.glb</c>,
/// che si rigenera (vedi la skill <c>blender-pipeline</c>), e figli aggiunti a mano dentro una scena
/// istanziata da un <c>.glb</c> si perdono o si sdoppiano a ogni reimport. Costruirli qui li lega ai
/// NOMI dei bone, che sono stabili, invece che alla struttura del file importato.
///
/// Come per <see cref="TwoBoneIkModifier"/>: e' pura resa, gira identico su ogni peer a partire da
/// stato gia' replicato e non produce niente da sincronizzare (CLAUDE.md §3).
/// </summary>
public partial class WeaponGripRig : Node3D
{
    private const string HandBone = "RightHand";
    private const string SupportRootBone = "LeftArm";
    private const string SupportMidBone = "LeftForeArm";
    private const string SupportTipBone = "LeftHand";

    /// Velocita' con cui la mano di supporto entra ed esce dall'IK, in frazione al secondo.
    [Export] public float SupportBlendSpeed { get; set; } = 8.0f;

    /// <summary>
    /// IK della mano di supporto sull'astina. FALSE per ora: <see cref="TwoBoneIkModifier"/> viene
    /// eseguito ma la posa che calcola non arriva allo scheletro (vedi la nota di stato in quella
    /// classe). Tenerlo spento evita di pagare il costo di un modificatore che non produce nulla e,
    /// soprattutto, di far credere che la presa a due mani sia gia' attiva.
    ///
    /// L'aggancio dell'arma alla mano destra NON passa di qui: e' il BoneAttachment3D, e funziona.
    /// </summary>
    [Export] public bool EnableSupportHandIk { get; set; }

    /// <summary>
    /// Punto di presa: qui va messa l'arma. E' figlio del <see cref="BoneAttachment3D"/> sulla mano
    /// destra, quindi la sua trasformata mondo e' gia' quella giusta ogni frame.
    /// </summary>
    public Node3D? GripPoint { get; private set; }

    private Skeleton3D? _skeleton;
    private Node3D? _supportTarget;
    private TwoBoneIkModifier? _supportIk;
    private WeaponAnimationSet? _weapon;
    private float _supportWeight;

    // Rinculo procedurale: nessuna clip. Sono offset che si sommano alla presa e rientrano da soli.
    private float _kickBack;
    private float _kickUp;

    public override void _Ready()
    {
        _skeleton = FindSkeleton(this);
        if (_skeleton == null)
        {
            GD.PushWarning("[WeaponGripRig] nessuno Skeleton3D sotto il rig: l'arma restera' scollegata.");
            return;
        }

        var attachment = new BoneAttachment3D { Name = "RightHandAttachment", BoneName = HandBone };
        _skeleton.AddChild(attachment);

        GripPoint = new Node3D { Name = "GripPoint" };
        attachment.AddChild(GripPoint);

        // Bersaglio della mano di supporto: figlio del punto di presa, quindi si muove CON l'arma.
        // E' esattamente cio' che serve — la mano sinistra insegue l'astina, non un punto nello spazio.
        _supportTarget = new Node3D { Name = "SupportGripTarget" };
        GripPoint.AddChild(_supportTarget);

        _supportIk = new TwoBoneIkModifier
        {
            Name = "SupportHandIk",
            RootBone = SupportRootBone,
            MidBone = SupportMidBone,
            TipBone = SupportTipBone,
            MatchTargetRotation = true,
            Influence = 0f,
            TargetNode = _supportTarget,
        };
        _skeleton.AddChild(_supportIk);
    }

    private static Skeleton3D? FindSkeleton(Node node)
    {
        foreach (Node child in node.GetParent().GetChildren())
        {
            if (child is Skeleton3D found)
                return found;

            Skeleton3D? deeper = SearchDown(child);
            if (deeper != null)
                return deeper;
        }
        return null;
    }

    private static Skeleton3D? SearchDown(Node node)
    {
        if (node is Skeleton3D skeleton)
            return skeleton;

        foreach (Node child in node.GetChildren())
        {
            Skeleton3D? found = SearchDown(child);
            if (found != null)
                return found;
        }
        return null;
    }

    /// <summary>
    /// Dichiara quale arma si impugna, o null da disarmato. E' l'UNICO punto da toccare per
    /// aggiungere un'arma: presa, rinculo e mano di supporto arrivano tutti dal
    /// <see cref="WeaponAnimationSet"/>, quindi la locomozione non viene sfiorata.
    /// </summary>
    public void ApplyWeapon(WeaponAnimationSet? weapon) => _weapon = weapon;

    /// Rinculo di un colpo. Chiamato su ogni peer, come il calcio della camera.
    public void PlayRecoil()
    {
        if (_weapon == null)
            return;

        _kickBack = _weapon.RecoilKickBack;
        _kickUp = Mathf.DegToRad(_weapon.RecoilKickUpDegrees);
    }

    public override void _Process(double delta)
    {
        if (GripPoint == null)
            return;

        float dt = (float)delta;

        // Rientro esponenziale del rinculo: indipendente dal frame rate, come lo smorzamento di
        // CharacterAnimator.
        if (_weapon != null)
        {
            float recovery = 1f - Mathf.Exp(-_weapon.RecoilRecoverySpeed * dt);
            _kickBack = Mathf.Lerp(_kickBack, 0f, recovery);
            _kickUp = Mathf.Lerp(_kickUp, 0f, recovery);
        }

        // Presa: offset dichiarato dall'arma, piu' il rinculo. L'arma punta verso +Z locale, quindi
        // il rinculo arretra lungo -Z e alza il muso ruotando attorno a X.
        Vector3 grip = _weapon?.GripOffset ?? Vector3.Zero;
        Vector3 gripRotation = _weapon?.GripRotationDegrees ?? Vector3.Zero;

        var basis = Basis.FromEuler(new Vector3(
            Mathf.DegToRad(gripRotation.X) - _kickUp,
            Mathf.DegToRad(gripRotation.Y),
            Mathf.DegToRad(gripRotation.Z)));

        GripPoint.Transform = new Transform3D(basis, grip - new Vector3(0f, 0f, _kickBack));

        // Mano di supporto: solo per le armi a due mani, e con una transizione — accenderla di colpo
        // farebbe scattare il braccio sinistro nel frame in cui si cambia arma.
        if (_supportIk == null || _supportTarget == null)
            return;

        bool twoHanded = EnableSupportHandIk && _weapon is { IsTwoHanded: true };
        _supportTarget.Position = _weapon?.SupportGripOffset ?? Vector3.Zero;
        _supportWeight = Mathf.Lerp(_supportWeight, twoHanded ? 1f : 0f, 1f - Mathf.Exp(-SupportBlendSpeed * dt));
        _supportIk.Influence = _supportWeight;
    }
}
