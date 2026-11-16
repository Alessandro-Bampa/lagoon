using Godot;

namespace Lagoon;

/// <summary>
/// Nomi degli ossi del profilo umanoide di Godot (<c>SkeletonProfileHumanoid</c>).
///
/// Il corpo arriva dalla Universal Animation Library con uno scheletro in stile Unreal
/// (<c>pelvis</c>, <c>spine_01</c>, <c>hand_r</c>...), ma l'import lo RINOMINA su questo profilo
/// tramite <c>animation/resources/ual_bone_map.tres</c>: cosi' un corpo diverso, mappato sullo
/// stesso profilo, riusa clip e codice senza modifiche. Ogni nome d'osso usato dal C# passa da qui.
/// </summary>
public static class HumanoidBones
{
    public const string Hips = "Hips";
    public const string Spine = "Spine";
    public const string Chest = "Chest";
    public const string UpperChest = "UpperChest";
    public const string Neck = "Neck";
    public const string Head = "Head";

    public const string LeftShoulder = "LeftShoulder";
    public const string LeftUpperArm = "LeftUpperArm";
    public const string LeftLowerArm = "LeftLowerArm";
    public const string LeftHand = "LeftHand";

    public const string RightShoulder = "RightShoulder";
    public const string RightUpperArm = "RightUpperArm";
    public const string RightLowerArm = "RightLowerArm";
    public const string RightHand = "RightHand";

    /// Ossi del busto, dal basso verso l'alto: su questi si distribuisce la mira.
    public static readonly string[] SpineChain = { Spine, Chest, UpperChest, Neck, Head };

    /// Nocche usate per ricavare l'orientamento geometrico della mano dalla rest pose.
    public static string Knuckle(string side, string finger) => side + finger + "Proximal";
}
