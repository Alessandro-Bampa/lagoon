using Godot;

namespace Lagoon;

/// <summary>
/// Alza le BRACCIA in blocco ruotando le clavicole, senza toccare il resto del corpo.
///
/// Serve da accovacciati con l'arma al porto rilassato: le clip di crouch piegano il busto in
/// avanti di 35 gradi (<c>crouch_idle</c>) e 59 (<c>crouch_fwd</c>) e tengono le ginocchia alte
/// davanti al petto, mentre la posa d'impugnatura e' una posa delle sole braccia — figlie di
/// <c>Spine2</c>, si portano dietro quella piega e finiscono avambracci e arma dentro le cosce.
/// Misurata la distanza minima fra l'asse dell'arma e le gambe, con la pistola: 0,076 m
/// accovacciati contro 0,492 in piedi.
///
/// La rotazione va sulle CLAVICOLE e non sui bracci, per la stessa ragione per cui ci va il porto
/// rilassato generato da <c>tools/build_weapon_poses.gd</c>: le due clavicole nascono quasi nello
/// stesso punto, quindi ruotarle insieme e' quasi una rotazione rigida del blocco braccia e la
/// distanza fra le mani — cioe' la lunghezza dell'astina, il vincolo della presa — resta invariata
/// al millimetro. Ruotando i bracci, che nascono a mezzo metro l'uno dall'altro, la presa si
/// aprirebbe di qualche centimetro.
///
/// Alzare invece il BUSTO era l'altra strada, ed e' stata provata e scartata: toglie l'arma dalle
/// gambe ma il personaggio si accovaccia con la schiena dritta e la posa non legge piu' come un
/// accovacciamento.
///
/// Va scritto come <see cref="SkeletonModifier3D"/> e non come codice che tocca le ossa da fuori:
/// Godot 4.7 ripristina le pose sorgente al termine della passata dei modificatori, quindi una
/// scrittura fatta altrove verrebbe cancellata senza un solo errore.
///
/// E' pura resa (CLAUDE.md §3): gira su ogni peer da stato gia' replicato, non produce stato di
/// gioco, non richiede autorita'.
/// </summary>
[GlobalClass]
public partial class ShoulderLiftModifier : SkeletonModifier3D
{
    /// Ossa da ruotare. Le clavicole, entrambe: e' il blocco braccia a doversi alzare intero.
    [Export]
    public string[] Bones { get; set; } = ["LeftShoulder", "RightShoulder"];

    /// <summary>
    /// Asse laterale dello scheletro. Il rig guarda +Z e la sua sinistra e' +X (stessa convenzione
    /// di <see cref="SpineAimModifier.SkeletonForward"/>): ruotare attorno a +X porta l'avanti verso
    /// il basso, quindi il sollevamento e' una rotazione NEGATIVA attorno a questo asse. Il segno lo
    /// mette il modificatore, cosi' <see cref="LiftDegrees"/> resta "positivo = braccia in su".
    /// </summary>
    [Export] public Vector3 LateralAxis { get; set; } = Vector3.Right;

    /// Di quanto si alzano le braccia a influenza piena, in gradi.
    [Export] public float LiftDegrees { get; set; } = 30.0f;

    private Skeleton3D? _skeleton;
    private int[] _bones = [];

    public override void _Ready() => Resolve();

    public override void _SkeletonChanged(Skeleton3D oldSkeleton, Skeleton3D newSkeleton) => Resolve();

    /// Indici risolti dai NOMI: il rig arriva da un <c>.glb</c> rigenerabile, gli indici no.
    private void Resolve()
    {
        _skeleton = GetSkeleton();
        _bones = [];

        if (_skeleton == null)
            return;

        var resolved = new System.Collections.Generic.List<int>(Bones.Length);
        foreach (string name in Bones)
        {
            int index = _skeleton.FindBone(name);
            if (index < 0)
            {
                GD.PushWarning($"[ShoulderLiftModifier] osso '{name}' assente dal rig: le braccia non si alzeranno.");
                return;
            }
            resolved.Add(index);
        }

        _bones = [.. resolved];
    }

    public override void _ProcessModification()
    {
        if (_skeleton == null || _bones.Length == 0)
            return;

        float angle = -Mathf.DegToRad(LiftDegrees) * Influence;
        if (Mathf.IsZeroApprox(angle))
            return;

        var rotation = new Quaternion(LateralAxis.Normalized(), angle);
        foreach (int bone in _bones)
        {
            // La rotazione e' dichiarata nello spazio dello SCHELETRO e va portata in quello del
            // PADRE prima di premoltiplicarla alla posa locale: e' la premoltiplicazione in spazio
            // padre a far ruotare l'osso attorno al proprio giunto invece che attorno al proprio
            // asse. Stessa costruzione di SpineAimModifier.Rotate.
            int parent = _skeleton.GetBoneParent(bone);
            Basis parentBasis = parent < 0 ? Basis.Identity : _skeleton.GetBoneGlobalPose(parent).Basis;
            Quaternion toParent = parentBasis.GetRotationQuaternion().Inverse();

            _skeleton.SetBonePoseRotation(bone,
                toParent * rotation * toParent.Inverse() * _skeleton.GetBonePoseRotation(bone));
        }
    }
}
