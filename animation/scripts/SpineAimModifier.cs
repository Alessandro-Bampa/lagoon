using Godot;

namespace Lagoon;

/// <summary>
/// Orienta il BUSTO verso la direzione di mira, correggendo la posa animata invece di sostituirla.
///
/// E' il pezzo che mancava al movimento armato. La posa "reggi arma" e' authored rispetto a un
/// bacino neutro, ma le clip di strafe ruotano il bacino di parecchi gradi: siccome l'arma pende
/// dalla catena <c>Hips -> Spine -> ... -> RightHand</c>, l'asse dell'arma eredita quella rotazione e
/// smette di coincidere con la direzione in cui si sta mirando. Nessuna clip lo puo' risolvere,
/// perche' l'errore dipende da QUALE clip di locomozione sta girando e con che peso.
///
/// Qui l'errore si misura e si annulla: si legge dove punta davvero il busto nella posa animata di
/// questo frame, si calcola la rotazione minima che lo porta sulla mira, e la si distribuisce lungo
/// la catena. Le somme dei pesi valgono 1, quindi il vertice ruota esattamente dell'angolo voluto
/// mentre ogni vertebra ne prende solo la sua parte — che e' come si costruisce un aim offset.
///
/// Va scritto come <see cref="SkeletonModifier3D"/> e non come codice che tocca le ossa da fuori:
/// Godot 4.7 RIPRISTINA le pose sorgente al termine della passata dei modificatori, quindi una
/// scrittura fatta altrove verrebbe cancellata senza un solo errore. Vale anche per chi legge: il
/// risultato e' osservabile solo qui dentro o nel segnale <c>skeleton_updated</c>.
///
/// E' pura resa (CLAUDE.md §3): gira su ogni peer da stato gia' replicato, non produce stato di
/// gioco, non richiede autorita'.
/// </summary>
[GlobalClass]
public partial class SpineAimModifier : SkeletonModifier3D
{
    /// <summary>
    /// Catena da ruotare, dalla radice al vertice. L'ultimo osso e' quello di cui si misura
    /// l'orientamento: dev'essere il piu' vicino alle spalle, perche' e' li' che nasce l'arma.
    /// </summary>
    [Export]
    public string[] Bones { get; set; } = ["Spine", "Spine1", "Spine2"];

    /// <summary>
    /// Quanta parte della correzione prende ogni osso, nello stesso ordine di <see cref="Bones"/>.
    /// La somma dovrebbe fare 1: sotto, il busto non arriva del tutto sulla mira; sopra, lo supera.
    /// Crescente verso l'alto perche' un torso che ruota tutto dal bacino sembra rigido.
    /// </summary>
    [Export]
    public float[] Weights { get; set; } = [0.25f, 0.35f, 0.40f];

    /// <summary>
    /// Asse che nella posa di riposo punta AVANTI, in coordinate dello scheletro.
    ///
    /// Il rig nasce da <c>build_character.py</c>, dove il personaggio guarda verso -Y di Blender:
    /// convertito in Godot diventa +Z. Non si usa <c>Vector3.Forward</c> (che in Godot e' -Z) proprio
    /// per non dare per buona una convenzione che qui non vale.
    /// </summary>
    [Export]
    public Vector3 SkeletonForward { get; set; } = Vector3.Back;

    /// <summary>
    /// Massima correzione applicabile, in gradi. Oltre, si smette di inseguire la mira invece di
    /// torcere il busto in una posa che nessuna animazione ha mai previsto.
    /// </summary>
    [Export] public float MaxCorrectionDegrees { get; set; } = 70.0f;

    /// <summary>
    /// Direzione di mira in coordinate MONDO. La scrive <see cref="AimRig"/> ogni frame. Vettore
    /// nullo = nessuna correzione.
    /// </summary>
    public Vector3 AimDirection { get; set; }

    /// <summary>
    /// Inclinazione laterale del busto in radianti, positiva verso destra. Serve al lean procedurale
    /// in curva e in accelerazione: e' un additivo, non entra nel calcolo dell'errore di mira.
    /// </summary>
    public float Lean { get; set; }

    private Skeleton3D? _skeleton;
    private int[] _bones = [];

    /// Direzione "avanti" espressa nello spazio dell'osso di vertice, misurata sulla posa di RIPOSO.
    /// Cosi' la misura non dipende da come sono orientati gli assi dei bone in questo rig.
    private Vector3 _forwardInTip = Vector3.Back;

    public override void _Ready() => Resolve();

    public override void _SkeletonChanged(Skeleton3D oldSkeleton, Skeleton3D newSkeleton) => Resolve();

    /// <summary>
    /// Risolve gli indici di osso dai NOMI. Il rig arriva da un <c>.glb</c> rigenerabile, quindi gli
    /// indici non sono stabili fra una build e l'altra — i nomi si'.
    /// </summary>
    private void Resolve()
    {
        _skeleton = GetSkeleton();
        _bones = [];

        if (_skeleton == null || Bones.Length == 0)
            return;

        var resolved = new System.Collections.Generic.List<int>(Bones.Length);
        foreach (string name in Bones)
        {
            int index = _skeleton.FindBone(name);
            if (index < 0)
            {
                GD.PushWarning($"[SpineAimModifier] osso '{name}' assente dal rig: la mira restera' ferma.");
                return;
            }
            resolved.Add(index);
        }

        _bones = [.. resolved];
        _forwardInTip = _skeleton.GetBoneGlobalRest(_bones[^1]).Basis.Inverse() * SkeletonForward;
    }

    public override void _ProcessModification()
    {
        if (_skeleton == null || _bones.Length == 0 || Weights.Length < _bones.Length)
            return;

        float influence = Influence;
        if (influence <= 0.001f)
            return;

        // La mira arriva in coordinate mondo; tutto il calcolo vive nello spazio dello SCHELETRO,
        // che e' quello in cui GetBoneGlobalPose restituisce le pose.
        Vector3 desired = _skeleton.GlobalTransform.Basis.Inverse() * AimDirection;
        if (desired.LengthSquared() < 0.0001f)
            return;
        desired = desired.Normalized();

        DistributeAim(desired, influence);

        if (!Mathf.IsZeroApprox(Lean))
            Rotate(_bones[^1], new Quaternion(desired, Lean), influence);
    }

    /// <summary>
    /// Porta il busto sulla mira ripartendo l'errore lungo la catena.
    ///
    /// L'errore si RIMISURA prima di ogni vertebra invece di calcolarlo una volta sola e spalmarlo.
    /// Non e' pignoleria: ruotando un osso ruota anche tutto il suo sottoalbero, quindi dopo il primo
    /// passo l'errore residuo non e' piu' quello di partenza, e le frazioni calcolate in anticipo
    /// arriverebbero storte. Misurato: a colpo unico restavano una ventina di gradi di scarto.
    ///
    /// Ogni vertebra prende la propria quota del residuo, quindi l'ultima chiude il conto.
    /// </summary>
    private void DistributeAim(Vector3 desired, float influence)
    {
        float remainingWeight = 0f;
        for (int i = 0; i < _bones.Length; i++)
            remainingWeight += Weights[i];

        if (remainingWeight <= 0.0001f)
            return;

        float maxAngle = Mathf.DegToRad(MaxCorrectionDegrees);

        for (int i = 0; i < _bones.Length; i++)
        {
            float share = Weights[i] / remainingWeight;
            remainingWeight -= Weights[i];

            Vector3 current = (_skeleton!.GetBoneGlobalPose(_bones[^1]).Basis * _forwardInTip).Normalized();
            Quaternion correction = LimitedRotation(current, desired, maxAngle);

            Rotate(_bones[i], correction, share * influence);
        }
    }

    /// Rotazione minima da <paramref name="from"/> a <paramref name="to"/>, troncata a un angolo massimo.
    private static Quaternion LimitedRotation(Vector3 from, Vector3 to, float maxAngle)
    {
        var full = new Quaternion(from, to);
        float angle = full.GetAngle();
        return angle <= maxAngle ? full : Quaternion.Identity.Slerp(full, maxAngle / angle);
    }

    /// <summary>
    /// Ruota un osso di una frazione di <paramref name="rotation"/> attorno alla propria origine,
    /// portandosi dietro tutto il sottoalbero.
    ///
    /// La rotazione arriva espressa nello spazio dello SCHELETRO e va convertita in quello del
    /// PADRE (coniugio con la sua posa globale) prima di premoltiplicarla alla posa locale: e' la
    /// premoltiplicazione in spazio padre a far ruotare l'osso attorno al proprio giunto anziche'
    /// attorno al proprio asse.
    /// </summary>
    private void Rotate(int bone, Quaternion rotation, float share)
    {
        if (share <= 0.0001f)
            return;

        Quaternion slice = Quaternion.Identity.Slerp(rotation, Mathf.Min(share, 1f));

        int parent = _skeleton!.GetBoneParent(bone);
        Basis parentBasis = parent < 0 ? Basis.Identity : _skeleton.GetBoneGlobalPose(parent).Basis;
        Quaternion toParent = parentBasis.GetRotationQuaternion().Inverse();
        Quaternion inParentSpace = toParent * slice * toParent.Inverse();

        _skeleton.SetBonePoseRotation(bone, inParentSpace * _skeleton.GetBonePoseRotation(bone));
    }
}
