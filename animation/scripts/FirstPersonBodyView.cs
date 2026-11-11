using Godot;

namespace Lagoon;

/// <summary>
/// Corpo visto in prima persona: nasconde un osso (la testa) alla camera SENZA toglierlo dall'ombra.
///
/// La mesh del corpo e' unica e skinnata, quindi non c'e' un nodo "testa" da spegnere, e collassare
/// l'osso sullo scheletro lo toglierebbe anche dall'ombra proiettata. Si sdoppia allora la mesh:
/// <list type="bullet">
/// <item>l'originale resta intera e diventa <c>ShadowsOnly</c>: e' lei a fare ombra, testa compresa;</item>
/// <item>una copia, con i vertici dell'osso collassati sulla sua origine, e' l'unica che si vede e
/// non proietta ombre.</item>
/// </list>
/// Lo scheletro non viene toccato: animazione, IK e letture di posa restano identiche.
///
/// Pura resa locale (CLAUDE.md §3): si chiama solo per l'avatar del peer locale.
/// </summary>
public static class FirstPersonBodyView
{
    private const string ViewSuffix = "FirstPerson";

    /// <summary>
    /// Sdoppia ogni mesh skinnata su <paramref name="skeleton"/> sotto <paramref name="root"/>.
    /// Va chiamato dopo che i materiali della mesh sono stati assegnati: la copia li eredita qui.
    /// </summary>
    public static void HideBone(Node root, Skeleton3D skeleton, int bone)
    {
        foreach (Node node in root.FindChildren("*", nameof(MeshInstance3D), true, false))
        {
            if (node is not MeshInstance3D source
                || source.Mesh is not ArrayMesh mesh
                || source.GetNodeOrNull(source.Skeleton) != skeleton)
                continue;

            ArrayMesh? collapsed = BuildCollapsed(mesh, source.Skin, skeleton, bone);
            if (collapsed == null)
                continue;

            var view = new MeshInstance3D
            {
                Name = source.Name + ViewSuffix,
                Mesh = collapsed,
                Skin = source.Skin,
                Skeleton = source.Skeleton,
                Transform = source.Transform,
                Layers = source.Layers,
                MaterialOverride = source.MaterialOverride,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            source.GetParent().AddChild(view);
            source.CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly;
        }
    }

    /// <summary>
    /// Copia della mesh con i vertici pesati su <paramref name="bone"/> (e discendenti) spostati
    /// sull'origine dell'osso, in proporzione al peso: la testa diventa un punto sul collo e la
    /// superficie resta chiusa. Ritorna null se non c'e' nulla da collassare.
    /// </summary>
    private static ArrayMesh? BuildCollapsed(ArrayMesh source, Skin? skin, Skeleton3D skeleton, int bone)
    {
        if (source.GetBlendShapeCount() > 0)
        {
            GD.PushWarning("[FirstPersonBodyView] mesh con blend shape non supportata: la testa resta visibile.");
            return null;
        }

        bool[] hidden = HiddenBinds(skin, skeleton, bone, out Vector3 pivot);
        var result = new ArrayMesh();
        bool changed = false;

        for (int s = 0; s < source.GetSurfaceCount(); s++)
        {
            Godot.Collections.Array arrays = source.SurfaceGetArrays(s);
            Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            int[] bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
            float[] weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();

            // 4 oppure 8 influenze per vertice: lo dice la lunghezza dell'array.
            int perVertex = vertices.Length > 0 ? bones.Length / vertices.Length : 0;
            for (int v = 0; v < vertices.Length && perVertex > 0; v++)
            {
                float weight = 0f;
                for (int k = 0; k < perVertex; k++)
                {
                    int bind = bones[v * perVertex + k];
                    if (bind >= 0 && bind < hidden.Length && hidden[bind])
                        weight += weights[v * perVertex + k];
                }

                if (weight <= 0f)
                    continue;

                vertices[v] = vertices[v].Lerp(pivot, Mathf.Min(weight, 1f));
                changed = true;
            }

            arrays[(int)Mesh.ArrayType.Vertex] = vertices;

            // I LOD dell'import non si copiano: questa mesh la guarda solo chi ci sta dentro.
            Mesh.ArrayFormat flags = source.SurfaceGetFormat(s) & Mesh.ArrayFormat.FlagUse8BoneWeights;
            result.AddSurfaceFromArrays(source.SurfaceGetPrimitiveType(s), arrays, flags: flags);
            result.SurfaceSetMaterial(s, source.SurfaceGetMaterial(s));
        }

        return changed ? result : null;
    }

    /// <summary>
    /// Gli indici d'osso di una mesh skinnata sono indici di BIND della Skin, non dello scheletro:
    /// qui si marcano i bind che cadono su <paramref name="bone"/> o su un suo discendente, e si
    /// ricava l'origine dell'osso nello spazio della mesh (l'inversa della sua bind pose).
    /// </summary>
    private static bool[] HiddenBinds(Skin? skin, Skeleton3D skeleton, int bone, out Vector3 pivot)
    {
        pivot = skeleton.GetBoneGlobalRest(bone).Origin;

        int count = skin?.GetBindCount() ?? skeleton.GetBoneCount();
        var hidden = new bool[count];
        for (int i = 0; i < count; i++)
        {
            int target = i;
            if (skin != null)
            {
                StringName name = skin.GetBindName(i);
                target = name.IsEmpty ? skin.GetBindBone(i) : skeleton.FindBone(name);
            }

            hidden[i] = IsSelfOrDescendant(skeleton, target, bone);
            if (skin != null && target == bone)
                pivot = skin.GetBindPose(i).AffineInverse().Origin;
        }

        return hidden;
    }

    private static bool IsSelfOrDescendant(Skeleton3D skeleton, int candidate, int ancestor)
    {
        for (int b = candidate; b >= 0; b = skeleton.GetBoneParent(b))
        {
            if (b == ancestor)
                return true;
        }

        return false;
    }
}
