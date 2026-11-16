using System.Collections.Generic;
using Godot;

namespace Lagoon;

/// <summary>
/// Dove stanno le mani su un'arma, misurato sul viewmodel in prima persona.
///
/// Il corpo in terza persona non ha clip da fucile: le mani le porta sull'arma un IK
/// (<see cref="ArmIkModifier"/>). Invece di dichiarare a mano "polso qui, dita verso la'" — che
/// va rifatto per ogni arma e si sbaglia — si legge la posa che l'autore del pack ha gia'
/// disegnato per le braccia in prima persona (clip <c>pose_base</c> di <c>VM_*.glb</c>) e la si
/// esprime nel riferimento dell'arma. Un'arma nuova porta la propria presa con se'.
///
/// La mano e' descritta da una terna GEOMETRICA, la stessa che <c>ArmIkModifier</c> misura sulla
/// rest pose del corpo: origine sul polso, X lungo le dita (polso -> nocca del medio), Y lungo le
/// nocche (mignolo -> indice), Z il loro prodotto. Cosi' il risultato non dipende dagli assi
/// locali degli ossi, che fra i due scheletri sono diversi.
/// </summary>
public static class ViewmodelHandPose
{
    /// Terne delle due mani nel riferimento dell'arma (canna lungo -Z).
    public readonly record struct Hands(Transform3D Right, Transform3D Left);

    private const string PoseClip = "pose_base";
    private const string GunBone = "Gun";

    private static readonly Dictionary<string, Hands?> Cache = new();

    /// Misura (una volta per scena) la presa di un viewmodel. Null se la scena non ha la forma attesa.
    public static Hands? Measure(PackedScene? viewmodel)
    {
        if (viewmodel == null)
            return null;

        string key = viewmodel.ResourcePath;
        if (Cache.TryGetValue(key, out Hands? cached))
            return cached;

        Hands? result = Sample(viewmodel);
        if (result == null)
            GD.PushWarning($"[ViewmodelHandPose] impossibile misurare la presa su {key}: le mani in terza persona resteranno alla locomozione.");

        Cache[key] = result;
        return result;
    }

    private static Hands? Sample(PackedScene viewmodel)
    {
        Node root = viewmodel.Instantiate();
        try
        {
            if (root.FindChild("Skeleton3D", true, false) is not Skeleton3D skeleton
                || root.FindChild("AnimationPlayer", true, false) is not AnimationPlayer player
                || !player.HasAnimation(PoseClip))
                return null;

            var pose = new PoseReader(skeleton, player.GetAnimation(PoseClip));
            int gun = skeleton.FindBone(GunBone);
            if (gun < 0)
                return null;

            Transform3D toWeapon = pose.Global(gun).AffineInverse();
            Transform3D? right = HandFrame(pose, skeleton, "r");
            Transform3D? left = HandFrame(pose, skeleton, "l");
            if (right == null || left == null)
                return null;

            return new Hands(toWeapon * right.Value, toWeapon * left.Value);
        }
        finally
        {
            root.Free();
        }
    }

    private static Transform3D? HandFrame(PoseReader pose, Skeleton3D skeleton, string side)
    {
        int hand = skeleton.FindBone("hand_" + side);
        int middle = skeleton.FindBone("middle_01_" + side);
        int index = skeleton.FindBone("index_01_" + side);
        int pinky = skeleton.FindBone("pinky_01_" + side);
        if (hand < 0 || middle < 0 || index < 0 || pinky < 0)
            return null;

        Vector3 wrist = pose.Global(hand).Origin;
        Vector3 fingers = (pose.Global(middle).Origin - wrist).Normalized();
        Vector3 across = pose.Global(index).Origin - pose.Global(pinky).Origin;
        across = (across - fingers * across.Dot(fingers)).Normalized();

        return new Transform3D(new Basis(fingers, across, fingers.Cross(across)), wrist);
    }

    /// <summary>
    /// Posa globale degli ossi al primo fotogramma di una clip, calcolata dalle tracce senza far
    /// girare un AnimationPlayer: la scena non entra mai nell'albero.
    /// </summary>
    private sealed class PoseReader
    {
        private readonly Skeleton3D _skeleton;
        private readonly Dictionary<string, Vector3> _positions = new();
        private readonly Dictionary<string, Quaternion> _rotations = new();
        private readonly Dictionary<int, Transform3D> _globals = new();

        public PoseReader(Skeleton3D skeleton, Animation clip)
        {
            _skeleton = skeleton;
            for (int track = 0; track < clip.GetTrackCount(); track++)
            {
                NodePath path = clip.TrackGetPath(track);
                if (path.GetSubNameCount() == 0 || clip.TrackGetKeyCount(track) == 0)
                    continue;

                string bone = path.GetSubName(0);
                switch (clip.TrackGetType(track))
                {
                    case Animation.TrackType.Position3D:
                        _positions[bone] = clip.TrackGetKeyValue(track, 0).AsVector3();
                        break;
                    case Animation.TrackType.Rotation3D:
                        _rotations[bone] = clip.TrackGetKeyValue(track, 0).AsQuaternion();
                        break;
                }
            }
        }

        public Transform3D Global(int bone)
        {
            if (_globals.TryGetValue(bone, out Transform3D known))
                return known;

            // Dove la clip non ha una traccia (l'import toglie quelle uguali al riposo) vale il riposo.
            string name = _skeleton.GetBoneName(bone);
            Transform3D rest = _skeleton.GetBoneRest(bone);
            Vector3 position = _positions.TryGetValue(name, out Vector3 p) ? p : rest.Origin;
            Quaternion rotation = _rotations.TryGetValue(name, out Quaternion q)
                ? q
                : rest.Basis.GetRotationQuaternion();

            var local = new Transform3D(new Basis(rotation), position);
            int parent = _skeleton.GetBoneParent(bone);
            Transform3D global = parent < 0 ? local : Global(parent) * local;

            _globals[bone] = global;
            return global;
        }
    }
}
