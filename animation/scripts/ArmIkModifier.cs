using System.Collections.Generic;
using Godot;

namespace Lagoon;

/// <summary>
/// Porta le mani del corpo in terza persona dove devono stare: sull'arma, oppure sul bordo di un
/// ostacolo durante scavalcamento e arrampicata. IK analitico a due ossi per braccio, piu'
/// orientamento del polso e chiusura delle dita.
///
/// Per l'arma la regola e' "l'ARMA guida le mani", non il contrario: prima colloca
/// <see cref="WeaponHoldRig"/> rispetto alla testa — che a questo punto della passata e' gia'
/// dove sara' disegnata — poi le braccia la raggiungono. Canna allineata e due mani sull'arma
/// valgono per costruzione, con qualunque locomozione sotto.
///
/// L'orientamento della mano non dipende dagli assi locali dell'osso: si ricava dalla GEOMETRIA
/// della rest pose (direzione delle dita, asse fra le nocche), quindi regge un corpo diverso
/// purche' mappato sullo stesso profilo umanoide.
///
/// Tutto lavora nello spazio dello SCHELETRO. Pura resa (CLAUDE.md §3).
/// </summary>
public partial class ArmIkModifier : SkeletonModifier3D
{
    /// Bersaglio di una mano, nello spazio dello scheletro.
    public struct HandGoal
    {
        /// Dove va il polso.
        public Vector3 Position;

        /// Direzione polso -> nocca del medio.
        public Vector3 Fingers;

        /// Direzione mignolo -> indice (l'asse delle nocche).
        public Vector3 Across;

        /// Verso cui piega il gomito.
        public Vector3 PoleDirection;

        /// Quanto vale il bersaglio, da 0 (posa animata) a 1.
        public float Weight;

        /// Quanto si chiudono le dita, da 0 (come nella clip) a 1 (pugno sull'impugnatura).
        public float Curl;
    }

    /// L'arma da collocare e impugnare, o null.
    public WeaponHoldRig? Hold { get; set; }

    /// Sguardo nello spazio dello scheletro: -Z lungo la mira, X a destra.
    public Basis LookBasis { get; set; } = Basis.Identity;

    /// Bersagli "esterni" (il bordo di un ostacolo): si risolvono DOPO quelli dell'arma, quindi
    /// col loro peso li sostituiscono. Li scrive <see cref="CharacterAnimator"/>.
    public HandGoal Right;
    public HandGoal Left;

    private sealed class Arm
    {
        public int Upper = -1;
        public int Lower = -1;
        public int Hand = -1;

        /// Da "terna geometrica della mano a riposo" a "base dell'osso a riposo".
        public Basis RestFrameToBone = Basis.Identity;

        /// Falangi da chiudere: osso e posa chiusa (locale), gia' calcolata.
        public readonly List<(int Bone, Quaternion Closed)> Fingers = new();

        public bool Valid => Upper >= 0 && Lower >= 0 && Hand >= 0;
    }

    /// Flessione delle tre falangi di un dito chiuso sull'impugnatura, in gradi.
    private static readonly float[] GripCurl = { 55f, 70f, 45f };

    /// Flessione dell'indice della mano destra: resta piu' disteso, e' sul grilletto.
    private static readonly float[] TriggerCurl = { 25f, 45f, 25f };

    private Skeleton3D? _skeleton;
    private int _head = -1;
    private readonly Arm _right = new();
    private readonly Arm _left = new();

    public override void _Ready() => Resolve();

    public override void _SkeletonChanged(Skeleton3D oldSkeleton, Skeleton3D newSkeleton) => Resolve();

    private void Resolve()
    {
        _skeleton = GetSkeleton();
        if (_skeleton == null)
            return;

        _head = _skeleton.FindBone(HumanoidBones.Head);
        ResolveArm(_right, "Right");
        ResolveArm(_left, "Left");
    }

    private void ResolveArm(Arm arm, string side)
    {
        Skeleton3D skeleton = _skeleton!;
        arm.Upper = skeleton.FindBone(side + "UpperArm");
        arm.Lower = skeleton.FindBone(side + "LowerArm");
        arm.Hand = skeleton.FindBone(side + "Hand");

        int middle = skeleton.FindBone(HumanoidBones.Knuckle(side, "Middle"));
        int index = skeleton.FindBone(HumanoidBones.Knuckle(side, "Index"));
        int little = skeleton.FindBone(HumanoidBones.Knuckle(side, "Little"));
        if (!arm.Valid || middle < 0 || index < 0 || little < 0)
        {
            GD.PushWarning($"[ArmIkModifier] braccio {side} incompleto: la mano non raggiungera' l'arma.");
            arm.Upper = -1;
            return;
        }

        // Terna della mano misurata sulla rest pose: dita, asse mignolo -> indice, e il loro
        // prodotto. E' la stessa definizione di ViewmodelHandPose, quindi una terna misurata sul
        // viewmodel si applica a questo corpo senza conoscerne gli assi.
        Transform3D hand = skeleton.GetBoneGlobalRest(arm.Hand);
        Vector3 fingers = (skeleton.GetBoneGlobalRest(middle).Origin - hand.Origin).Normalized();
        Vector3 across = skeleton.GetBoneGlobalRest(index).Origin - skeleton.GetBoneGlobalRest(little).Origin;
        across = (across - fingers * across.Dot(fingers)).Normalized();

        var restFrame = new Basis(fingers, across, fingers.Cross(across));
        arm.RestFrameToBone = restFrame.Inverse() * hand.Basis.Orthonormalized();

        // Chiusura delle dita. La libreria non ha una posa di presa a due mani (nella sua posa
        // da pistola la sinistra e' aperta), quindi la si costruisce: ogni falange ruota attorno
        // all'asse delle nocche. Il verso che porta le dita verso il palmo e' opposto fra le due mani.
        float sign = side == "Right" ? 1f : -1f;
        arm.Fingers.Clear();
        foreach (string finger in new[] { "Index", "Middle", "Ring", "Little" })
        {
            float[] curl = side == "Right" && finger == "Index" ? TriggerCurl : GripCurl;
            string[] phalanges = { "Proximal", "Intermediate", "Distal" };
            for (int i = 0; i < phalanges.Length; i++)
            {
                int bone = skeleton.FindBone(side + finger + phalanges[i]);
                if (bone < 0)
                    continue;

                Vector3 axis = skeleton.GetBoneGlobalRest(bone).Basis.Orthonormalized().Inverse() * across;
                Quaternion rest = skeleton.GetBoneRest(bone).Basis.GetRotationQuaternion();
                arm.Fingers.Add((bone, rest * new Quaternion(axis.Normalized(), sign * Mathf.DegToRad(curl[i]))));
            }
        }
    }

    public override void _ProcessModificationWithDelta(double delta)
    {
        if (_skeleton == null || Influence <= 0.001f)
            return;

        if (Hold is { HasWeapon: true } && _head >= 0)
        {
            // Gli occhi veri del corpo: la testa DOPO locomozione e mira del busto.
            Vector3 eyes = _skeleton.GetBoneGlobalPose(_head).Origin + LookBasis * Hold.EyeFromHead;
            Hold.Place(new Transform3D(LookBasis, eyes));

            var right = new HandGoal();
            var left = new HandGoal();
            Hold.FillHandGoals(LookBasis.X, ref right, ref left);
            Solve(_right, right);
            Solve(_left, left);
        }

        Solve(_right, Right);
        Solve(_left, Left);
    }

    private void Solve(Arm arm, HandGoal goal)
    {
        float weight = Mathf.Clamp(goal.Weight * Influence, 0f, 1f);
        if (!arm.Valid || weight <= 0.001f)
            return;

        Skeleton3D skeleton = _skeleton!;
        Vector3 shoulder = skeleton.GetBoneGlobalPose(arm.Upper).Origin;
        Vector3 elbow = skeleton.GetBoneGlobalPose(arm.Lower).Origin;
        Vector3 wrist = skeleton.GetBoneGlobalPose(arm.Hand).Origin;

        float upperLength = shoulder.DistanceTo(elbow);
        float lowerLength = elbow.DistanceTo(wrist);
        if (upperLength < 0.001f || lowerLength < 0.001f)
            return;

        // Il bersaglio si sfuma dalla posa animata: accendere l'IK di colpo farebbe scattare il braccio.
        Vector3 target = wrist.Lerp(goal.Position, weight);
        Vector3 reach = target - shoulder;
        float distance = Mathf.Clamp(reach.Length(), 0.05f, (upperLength + lowerLength) * 0.999f);
        Vector3 direction = reach.LengthSquared() > 0.000001f ? reach.Normalized() : Vector3.Forward;

        // Teorema del coseno: angolo alla spalla fra la retta verso il bersaglio e il braccio.
        float cos = Mathf.Clamp(
            (upperLength * upperLength + distance * distance - lowerLength * lowerLength)
            / (2f * upperLength * distance), -1f, 1f);
        float sin = Mathf.Sqrt(1f - cos * cos);

        // Piano di piegatura: il polo, tolta la componente lungo la retta spalla -> bersaglio.
        Vector3 bend = goal.PoleDirection;
        bend -= direction * bend.Dot(direction);
        if (bend.LengthSquared() < 0.000001f)
            bend = elbow - shoulder - direction * (elbow - shoulder).Dot(direction);
        if (bend.LengthSquared() < 0.000001f)
            return;
        bend = bend.Normalized();

        Vector3 newElbow = shoulder + direction * (upperLength * cos) + bend * (upperLength * sin);
        Vector3 newWrist = shoulder + direction * distance;

        SkeletonMath.RotateBone(skeleton, arm.Upper,
            new Quaternion((elbow - shoulder).Normalized(), (newElbow - shoulder).Normalized()));

        // Dopo aver ruotato il braccio, gomito e polso si sono mossi: si rileggono.
        elbow = skeleton.GetBoneGlobalPose(arm.Lower).Origin;
        wrist = skeleton.GetBoneGlobalPose(arm.Hand).Origin;
        SkeletonMath.RotateBone(skeleton, arm.Lower,
            new Quaternion((wrist - elbow).Normalized(), (newWrist - elbow).Normalized()));

        OrientHand(arm, goal, weight);
    }

    private void OrientHand(Arm arm, HandGoal goal, float weight)
    {
        if (goal.Fingers.LengthSquared() < 0.0001f || goal.Across.LengthSquared() < 0.0001f)
            return;

        Vector3 fingers = goal.Fingers.Normalized();
        Vector3 across = goal.Across - fingers * goal.Across.Dot(fingers);
        if (across.LengthSquared() < 0.0001f)
            return;
        across = across.Normalized();

        Skeleton3D skeleton = _skeleton!;
        Basis desired = new Basis(fingers, across, fingers.Cross(across)) * arm.RestFrameToBone;
        Quaternion current = skeleton.GetBoneGlobalPose(arm.Hand).Basis.GetRotationQuaternion();
        Quaternion blended = current.Slerp(desired.Orthonormalized().GetRotationQuaternion(), weight);

        SkeletonMath.SetBoneRotation(skeleton, arm.Hand, blended);

        float curl = Mathf.Clamp(goal.Curl, 0f, 1f) * weight;
        if (curl <= 0.001f)
            return;

        foreach ((int bone, Quaternion closed) in arm.Fingers)
            skeleton.SetBonePoseRotation(bone, skeleton.GetBonePoseRotation(bone).Slerp(closed, curl));
    }
}
