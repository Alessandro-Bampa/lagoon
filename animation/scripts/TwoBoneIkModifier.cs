using Godot;

namespace Lagoon;

/// <summary>
/// IK analitico a due ossa (spalla-gomito-mano, anca-ginocchio-piede) come
/// <see cref="SkeletonModifier3D"/>.
///
/// Analitico e non iterativo per scelta: con DUE ossa la soluzione e' chiusa — e' il teorema del
/// coseno su un triangolo — quindi si ottiene in un passo, sempre lo stesso risultato a parita' di
/// ingresso, senza tolleranze ne' numero di iterazioni da tarare. <c>SkeletonIK3D</c> usa invece
/// FABRIK, che e' iterativo, converge in modo diverso a seconda della posa di partenza e su una
/// catena di due ossa non aggiunge nulla.
///
/// Sta in <c>animation/</c> e non in <c>player/</c> perche' non sa nulla di giocatori: riceve un
/// bersaglio e piega una catena. Vale identico per gli NPC.
///
/// Nota di rete (CLAUDE.md §3): e' pura RESA. Gira su ogni peer a partire da pose gia' calcolate e
/// non produce alcuno stato di gioco, quindi non va replicato e non richiede autorita'.
///
/// STATO: INCOMPLETO, non usare senza prima chiudere il punto qui sotto.
/// Il modificatore viene invocato (verificato: 31 chiamate su 31 frame, con scheletro, bersaglio e
/// indici di osso tutti risolti) e la soluzione trigonometrica gira, ma la posa scritta non arriva
/// allo scheletro: lo spostamento misurato dell'estremita' e' di 0,002 m verso un bersaglio distante
/// 0,111 m. Provate e scartate due vie di scrittura, <c>SetBoneGlobalPose</c> e la conversione in
/// posa locale con <c>SetBonePosePosition</c>/<c>SetBonePoseRotation</c>: stesso risultato.
/// Resta da capire l'ordine esatto fra AnimationMixer, modificatori e ricomposizione delle pose in
/// 4.7 — probabilmente serve leggere le pose di partenza in un altro momento del ciclo.
/// Fino ad allora <see cref="WeaponGripRig.EnableSupportHandIk"/> resta false: l'aggancio dell'arma
/// alla mano NON dipende da questo modificatore ed e' gia' verificato funzionante.
/// </summary>
[GlobalClass]
public partial class TwoBoneIkModifier : SkeletonModifier3D
{
    /// Radice della catena (es. <c>RightArm</c>, <c>LeftUpLeg</c>).
    [Export] public string RootBone { get; set; } = "";

    /// Osso intermedio, quello che si piega (es. <c>RightForeArm</c>, <c>LeftLeg</c>).
    [Export] public string MidBone { get; set; } = "";

    /// Estremita' della catena (es. <c>RightHand</c>, <c>LeftFoot</c>).
    [Export] public string TipBone { get; set; } = "";

    /// <summary>
    /// Nodo che l'estremita' deve raggiungere. Se non e' assegnato o non esiste, il modificatore non
    /// tocca nulla e la posa animata passa intatta.
    /// </summary>
    [Export] public NodePath TargetPath { get; set; } = new();

    /// <summary>
    /// Nodo che indica DA CHE PARTE si piega il gomito (o il ginocchio). Facoltativo: senza, si
    /// conserva il piano di piegatura dell'animazione, che e' quasi sempre quello giusto e non
    /// richiede di piazzare e mantenere un target in piu'.
    /// </summary>
    [Export] public NodePath PolePath { get; set; } = new();

    /// Se true l'estremita' assume anche l'ORIENTAMENTO del bersaglio, non solo la sua posizione.
    [Export] public bool MatchTargetRotation { get; set; } = true;

    /// <summary>
    /// Bersaglio assegnato direttamente, che ha la precedenza su <see cref="TargetPath"/>.
    ///
    /// Serve a chi costruisce il rig DA CODICE (vedi <see cref="WeaponGripRig"/>): un NodePath si
    /// puo' calcolare solo quando i due nodi sono gia' nell'albero, cioe' dopo <c>_Ready</c> — e a
    /// quel punto il path e' gia' stato risolto, quindi assegnarlo non avrebbe alcun effetto e l'IK
    /// resterebbe spento in silenzio.
    /// </summary>
    public Node3D? TargetNode { get; set; }

    private Skeleton3D? _skeleton;
    private int _root = -1;
    private int _mid = -1;
    private int _tip = -1;
    private Node3D? _target;
    private Node3D? _pole;

    public override void _Ready() => Resolve();

    /// <summary>
    /// Risolve indici di osso e nodi bersaglio. Va rifatto quando cambia lo scheletro: il rig arriva
    /// da un <c>.glb</c> rigenerabile, quindi gli indici non sono stabili fra una build e l'altra —
    /// i NOMI si'.
    /// </summary>
    private void Resolve()
    {
        _skeleton = GetSkeleton();
        if (_skeleton == null)
            return;

        _root = _skeleton.FindBone(RootBone);
        _mid = _skeleton.FindBone(MidBone);
        _tip = _skeleton.FindBone(TipBone);

        _target = TargetNode ?? (TargetPath.IsEmpty ? null : GetNodeOrNull<Node3D>(TargetPath));
        _pole = PolePath.IsEmpty ? null : GetNodeOrNull<Node3D>(PolePath);
    }

    public override void _SkeletonChanged(Skeleton3D oldSkeleton, Skeleton3D newSkeleton) => Resolve();

    public override void _ProcessModification()
    {
        // Il bersaglio assegnato da codice puo' arrivare dopo _Ready: se e' cambiato, si ririsolve.
        if (TargetNode != null && !ReferenceEquals(TargetNode, _target))
            _target = TargetNode;

        if (_skeleton == null || _target == null || _root < 0 || _mid < 0 || _tip < 0)
            return;

        float influence = Influence;
        if (influence <= 0.001f)
            return;

        Transform3D rootPose = _skeleton.GetBoneGlobalPose(_root);
        Transform3D midPose = _skeleton.GetBoneGlobalPose(_mid);
        Transform3D tipPose = _skeleton.GetBoneGlobalPose(_tip);

        // Tutto il calcolo vive nello spazio dello SCHELETRO, non del mondo: e' lo spazio in cui
        // GetBoneGlobalPose restituisce le pose e in cui SetBoneGlobalPose le rilegge.
        Transform3D toSkeleton = _skeleton.GlobalTransform.AffineInverse();
        Vector3 target = toSkeleton * _target.GlobalPosition;

        Vector3 rootPos = rootPose.Origin;
        float upper = rootPos.DistanceTo(midPose.Origin);
        float lower = midPose.Origin.DistanceTo(tipPose.Origin);
        if (upper <= 0.0001f || lower <= 0.0001f)
            return;

        Vector3 toTarget = target - rootPos;
        float reach = toTarget.Length();
        if (reach <= 0.0001f)
            return;

        Vector3 reachDir = toTarget / reach;

        // La catena non puo' ne' allungarsi ne' ripiegarsi oltre il segmento piu' corto: si confina
        // la distanza nell'intervallo raggiungibile, con un margine che evita il ginocchio
        // perfettamente disteso (dove il piano di piegatura diventa indeterminato).
        float minReach = Mathf.Abs(upper - lower) + 0.001f;
        float maxReach = upper + lower - 0.001f;
        float clamped = Mathf.Clamp(reach, minReach, maxReach);

        // Direzione di piegatura: la componente della posa ANIMATA perpendicolare alla congiungente
        // radice-bersaglio. Conservare il piano dell'animazione e' cio' che impedisce al gomito di
        // ribaltarsi quando il bersaglio passa da una parte all'altra del braccio.
        Vector3 poleReference = _pole != null
            ? toSkeleton * _pole.GlobalPosition - rootPos
            : midPose.Origin - rootPos;

        Vector3 bendDir = (poleReference - reachDir * poleReference.Dot(reachDir));
        if (bendDir.LengthSquared() < 0.000001f)
        {
            // Arto perfettamente disteso lungo il bersaglio: il piano e' indeterminato, si prende un
            // perpendicolare qualunque ricavato dalla posa della radice.
            bendDir = rootPose.Basis.Y - reachDir * rootPose.Basis.Y.Dot(reachDir);
            if (bendDir.LengthSquared() < 0.000001f)
                bendDir = reachDir.Cross(Vector3.Up);
        }
        bendDir = bendDir.Normalized();

        // Teorema del coseno: angolo fra la congiungente radice-bersaglio e il primo segmento.
        float cosRoot = (upper * upper + clamped * clamped - lower * lower) / (2f * upper * clamped);
        float rootAngle = Mathf.Acos(Mathf.Clamp(cosRoot, -1f, 1f));

        Vector3 desiredMid = rootPos + upper * (Mathf.Cos(rootAngle) * reachDir + Mathf.Sin(rootAngle) * bendDir);
        Vector3 desiredTip = rootPos + clamped * reachDir;

        // Si applicano rotazioni DELTA, non pose assolute: cosi' la torsione dell'osso decisa
        // dall'animazione (il polso che ruota, il femore che extraruota) sopravvive all'IK.
        Quaternion rootDelta = ArcRotation(midPose.Origin - rootPos, desiredMid - rootPos);
        if (influence < 1f)
            rootDelta = Quaternion.Identity.Slerp(rootDelta, influence);

        Basis rootBasis = new Basis(rootDelta) * rootPose.Basis;
        SetGlobalPose(_root, new Transform3D(rootBasis, rootPos));

        // Posa dell'osso intermedio DOPO la rotazione della radice: e' da li' che si misura il
        // secondo delta, altrimenti si sommerebbero due rotazioni calcolate sulla stessa posa.
        Vector3 midPos = rootPos + rootDelta * (midPose.Origin - rootPos);
        Vector3 tipAfterRoot = rootPos + rootDelta * (tipPose.Origin - rootPos);
        Basis midBasisAfterRoot = new Basis(rootDelta) * midPose.Basis;

        Quaternion midDelta = ArcRotation(tipAfterRoot - midPos, desiredTip - midPos);
        if (influence < 1f)
            midDelta = Quaternion.Identity.Slerp(midDelta, influence);

        Basis midBasis = new Basis(midDelta) * midBasisAfterRoot;
        SetGlobalPose(_mid, new Transform3D(midBasis, midPos));

        if (!MatchTargetRotation)
            return;

        // L'estremita' prende l'orientamento del bersaglio: e' quello che fa "impugnare" davvero
        // l'arma invece di limitarsi a toccarla.
        Vector3 tipPos = midPos + midDelta * (tipAfterRoot - midPos);
        Basis animatedTip = new Basis(midDelta) * (new Basis(rootDelta) * tipPose.Basis);
        Basis targetBasis = (toSkeleton * _target.GlobalTransform).Basis.Orthonormalized();
        Basis finalTip = influence >= 1f
            ? targetBasis
            : new Basis(animatedTip.GetRotationQuaternion().Slerp(targetBasis.GetRotationQuaternion(), influence));

        SetGlobalPose(_tip, new Transform3D(finalTip, tipPos));
    }

    /// <summary>
    /// Scrive una posa espressa nello spazio dello scheletro convertendola in posa LOCALE all'osso
    /// padre.
    ///
    /// Non si usa <c>SetBoneGlobalPose</c>: dentro un <see cref="SkeletonModifier3D"/> non ha effetto
    /// — il modificatore gira mentre lo scheletro sta gia' ricomponendo le pose globali dalle locali,
    /// quindi il valore scritto viene ricalcolato via subito dopo. E' un fallimento MUTO, identico per
    /// natura ai due bug del blend tree: il metodo esiste, accetta il valore e non succede niente.
    /// La posa locale invece e' la sorgente di verita' e sopravvive.
    /// </summary>
    private void SetGlobalPose(int bone, Transform3D pose)
    {
        int parent = _skeleton!.GetBoneParent(bone);
        Transform3D local = parent < 0
            ? pose
            : _skeleton.GetBoneGlobalPose(parent).AffineInverse() * pose;

        _skeleton.SetBonePosePosition(bone, local.Origin);
        _skeleton.SetBonePoseRotation(bone, local.Basis.GetRotationQuaternion());
    }

    /// Rotazione minima che porta <paramref name="from"/> su <paramref name="to"/>.
    private static Quaternion ArcRotation(Vector3 from, Vector3 to)
    {
        if (from.LengthSquared() < 0.000001f || to.LengthSquared() < 0.000001f)
            return Quaternion.Identity;

        return new Quaternion(from.Normalized(), to.Normalized());
    }
}
