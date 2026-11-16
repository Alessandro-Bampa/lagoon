using Godot;

namespace Lagoon;

/// <summary>
/// Operazioni sulle pose usate dai modificatori di scheletro. Tutte lavorano nello spazio dello
/// SCHELETRO (quello di <c>GetBoneGlobalPose</c>) e scrivono la posa LOCALE dell'osso: i figli
/// seguono da soli.
/// </summary>
public static class SkeletonMath
{
    /// Applica a <paramref name="bone"/> una rotazione espressa nello spazio dello scheletro.
    public static void RotateBone(Skeleton3D skeleton, int bone, Quaternion rotation)
    {
        Quaternion toParent = ParentRotation(skeleton, bone).Inverse();
        Quaternion local = toParent * rotation * toParent.Inverse();
        skeleton.SetBonePoseRotation(bone, (local * skeleton.GetBonePoseRotation(bone)).Normalized());
    }

    /// Impone a <paramref name="bone"/> un orientamento espresso nello spazio dello scheletro.
    public static void SetBoneRotation(Skeleton3D skeleton, int bone, Quaternion rotation)
    {
        Quaternion local = ParentRotation(skeleton, bone).Inverse() * rotation;
        skeleton.SetBonePoseRotation(bone, local.Normalized());
    }

    private static Quaternion ParentRotation(Skeleton3D skeleton, int bone)
    {
        int parent = skeleton.GetBoneParent(bone);
        return parent < 0
            ? Quaternion.Identity
            : skeleton.GetBoneGlobalPose(parent).Basis.GetRotationQuaternion();
    }

    /// Smorzamento esponenziale, indipendente dal frame rate.
    public static float Damp(float speed, float dt) => 1.0f - Mathf.Exp(-speed * dt);
}
