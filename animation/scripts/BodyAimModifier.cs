using Godot;

namespace Lagoon;

/// <summary>
/// Orienta il corpo in terza persona: gira il bacino verso dove si cammina e piega il busto verso
/// dove si guarda.
///
/// Serve perche' la Universal Animation Library (Standard) ha solo clip in AVANTI: niente strafe,
/// niente passo indietro. Invece di sintetizzarli, si ruota il bacino nella direzione del moto e
/// si contro-ruota la spina, cosi' le gambe corrono di lato mentre petto e arma restano sullo
/// sguardo. All'indietro la clip gira al contrario (lo decide <see cref="CharacterAnimator"/>).
///
/// Pura resa (CLAUDE.md §3): parte da stato gia' replicato e non produce nulla da sincronizzare.
/// </summary>
public partial class BodyAimModifier : SkeletonModifier3D
{
    /// Quota di sguardo assorbita da ogni osso di <see cref="HumanoidBones.SpineChain"/>. Somma 1:
    /// la testa finisce a guardare esattamente dove guarda il giocatore.
    private static readonly float[] Shares = { 0.15f, 0.20f, 0.20f, 0.20f, 0.25f };

    /// Rotazione del bacino attorno alla verticale, in radianti (positiva = verso +X dello scheletro).
    public float HipYaw { get; set; }

    /// Imbardata dello sguardo RISPETTO al corpo, in radianti (stessa convenzione di HipYaw).
    public float LookYaw { get; set; }

    /// Inclinazione dello sguardo, in radianti: positiva verso l'alto.
    public float LookPitch { get; set; }

    /// Di quanto il corpo intero si protende in avanti attorno al bacino, in radianti (parkour).
    public float LeanPitch { get; set; }

    private Skeleton3D? _skeleton;
    private int _hips = -1;
    private int[] _chain = [];

    public override void _Ready() => Resolve();

    public override void _SkeletonChanged(Skeleton3D oldSkeleton, Skeleton3D newSkeleton) => Resolve();

    private void Resolve()
    {
        _skeleton = GetSkeleton();
        _hips = -1;
        _chain = [];
        if (_skeleton == null)
            return;

        _hips = _skeleton.FindBone(HumanoidBones.Hips);

        var chain = new int[HumanoidBones.SpineChain.Length];
        for (int i = 0; i < chain.Length; i++)
        {
            chain[i] = _skeleton.FindBone(HumanoidBones.SpineChain[i]);
            if (chain[i] < 0)
            {
                GD.PushWarning($"[BodyAimModifier] osso '{HumanoidBones.SpineChain[i]}' assente: il busto non seguira' lo sguardo.");
                return;
            }
        }

        _chain = chain;
    }

    public override void _ProcessModificationWithDelta(double delta)
    {
        if (_skeleton == null || _chain.Length == 0 || Influence <= 0.001f)
            return;

        // Corpo proteso: attorno a +X (la sinistra dello scheletro) una rotazione positiva porta
        // la testa verso +Z, cioe' in avanti.
        if (_hips >= 0 && !Mathf.IsZeroApprox(LeanPitch))
            SkeletonMath.RotateBone(_skeleton, _hips, new Quaternion(Vector3.Right, LeanPitch));

        // Bacino verso il moto, spina contro-ruotata: sopra la vita non cambia nulla.
        if (_hips >= 0 && !Mathf.IsZeroApprox(HipYaw))
        {
            SkeletonMath.RotateBone(_skeleton, _hips, new Quaternion(Vector3.Up, HipYaw));
            SkeletonMath.RotateBone(_skeleton, _chain[0], new Quaternion(Vector3.Up, -HipYaw));
        }

        // Lo scheletro guarda +Z e ha la sinistra a +X: guardare in alto e' una rotazione
        // NEGATIVA attorno a +X. L'asse si porta prima sull'imbardata dello sguardo.
        float yaw = Mathf.Clamp(LookYaw, -1.2f, 1.2f);
        var look = new Quaternion(Vector3.Up, yaw) * new Quaternion(Vector3.Right, -LookPitch);

        for (int i = 0; i < _chain.Length; i++)
            SkeletonMath.RotateBone(_skeleton, _chain[i], Quaternion.Identity.Slerp(look, Shares[i]));
    }
}
