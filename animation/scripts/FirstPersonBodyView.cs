using Godot;

namespace Lagoon;

/// <summary>
/// Corpo del giocatore locale visto in prima persona: alla camera restano bacino e gambe, mentre
/// l'OMBRA resta quella del corpo intero.
///
/// In vista ci sono gia' le braccia del viewmodel, e la camera sta sull'asse del corpo: testa,
/// busto e braccia del manichino le finirebbero davanti (o dentro). La mesh pero' e' unica e
/// skinnata, quindi non c'e' un nodo "busto" da spegnere, e agire sullo scheletro (scalare un
/// osso) lo toglierebbe anche dall'ombra proiettata. Si sdoppia allora la mesh:
/// <list type="bullet">
/// <item>l'originale resta intera e diventa <c>ShadowsOnly</c>: e' lei a fare ombra;</item>
/// <item>una copia senza ombra, con i vertici pesati da <c>Spine</c> in su collassati
/// sull'origine di quell'osso, e' l'unica che si vede: guardando in basso si vedono le gambe.</item>
/// </list>
/// La copia e' anche arretrata di qualche centimetro, cosi' guardando in basso i piedi stanno
/// davanti e non sotto la visuale.
///
/// Lo scheletro non viene toccato. Pura resa locale (CLAUDE.md §3).
///
/// <para><b>Seduti al governo</b> (barra, volante) il viewmodel e' riposto e le mani che si vedono
/// sono quelle del corpo: c'e' una seconda copia, senza arretramento, in cui testa, collo e busto
/// collassano sulla spina e spalle e braccia sul gomito (vedi <see cref="SeatedPivots"/>). Restano
/// in vista avambracci, mani e gambe. <see cref="SetView"/> sceglie quale copia mostrare.</para>
/// </summary>
public static class FirstPersonBodyView
{
    private const string ViewSuffix = "FirstPerson";
    private const string SeatedSuffix = "FirstPersonSeated";
    private const string BackOffsetMeta = "first_person_back_offset";

    /// Meta da mettere su una mesh skinnata (i vestiti di <c>CharacterOutfit</c>) perche' nella copia
    /// le parti nascoste vengano tolte invece che collassate.
    public const string CullMeta = "first_person_cull";

    /// Copia di un capo interamente nascosto (elmetto, zaino): nessuna superficie.
    private static readonly ArrayMesh EmptyMesh = new();

    /// Copie gia' calcolate per mesh sorgente (normale, seduti): i capi si rimettono spesso.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ArrayMesh, ArrayMesh?[]> Cache = new();

    /// True se <see cref="Build"/> e' stato chiamato su questo scheletro (avatar locale).
    public static bool IsBuilt(Skeleton3D skeleton) => skeleton.HasMeta(BackOffsetMeta);

    /// <summary>
    /// Rifa le copie dopo che sono cambiate le mesh skinnate (vestiti indossati o tolti). Non fa nulla
    /// se il corpo non e' mai stato sdoppiato. Dopo, chi la chiama rimette la vista con <see cref="SetView"/>.
    /// </summary>
    public static void Rebuild(Node root, Skeleton3D skeleton)
    {
        if (!IsBuilt(skeleton))
            return;
        foreach (Node node in root.FindChildren("*" + ViewSuffix + "*", nameof(MeshInstance3D), true, false))
        {
            node.GetParent().RemoveChild(node);
            node.QueueFree();
        }
        Build(root, skeleton, (float)skeleton.GetMeta(BackOffsetMeta));
    }

    /// <summary>
    /// Sdoppia ogni mesh skinnata su <paramref name="skeleton"/> sotto <paramref name="root"/>.
    /// </summary>
    /// <param name="backOffset">Arretramento della copia visibile, in metri.</param>
    public static void Build(Node root, Skeleton3D skeleton, float backOffset)
    {
        int bone = skeleton.FindBone(HumanoidBones.Spine);
        int neck = skeleton.FindBone(HumanoidBones.Neck);
        if (bone < 0)
        {
            GD.PushWarning($"[FirstPersonBodyView] osso '{HumanoidBones.Spine}' assente: il corpo resta intero.");
            return;
        }
        skeleton.SetMeta(BackOffsetMeta, backOffset);

        foreach (Node node in root.FindChildren("*", nameof(MeshInstance3D), true, false))
        {
            if (node is not MeshInstance3D source
                || source.Mesh is not ArrayMesh mesh
                || source.Name.ToString().Contains(ViewSuffix)
                || source.IsQueuedForDeletion()
                || source.GetNodeOrNull(source.Skeleton) != skeleton)
                continue;

            // Un capo (CullMeta) non si collassa: i suoi triangoli nascosti si TOLGONO. Una giacca
            // collassata sulla spina diventerebbe un sacco davanti alle gambe; tolta, ne resta l'orlo.
            bool cull = source.HasMeta(CullMeta);
            if (!Cache.TryGetValue(mesh, out ArrayMesh?[]? copies))
            {
                copies = new[]
                {
                    BuildCollapsed(mesh, source.Skin, skeleton, bone, null, cull),
                    neck >= 0 ? BuildCollapsed(mesh, source.Skin, skeleton, bone, SeatedPivots(skeleton), cull) : null,
                };
                Cache.Add(mesh, copies);
            }
            // Una mesh senza nulla da collassare (stivali, pantaloni) si copia comunque: deve
            // arretrare come il resto del corpo, o starebbe davanti ai piedi.
            ArrayMesh collapsed = copies[0] ?? mesh;
            // La maschera delle regioni coperte dai vestiti (CharacterOutfit) vale anche per le copie.
            Variant hidden = source.GetInstanceShaderParameter(CharacterOutfit.HiddenRegionsParam);

            var view = new MeshInstance3D
            {
                Name = source.Name + ViewSuffix,
                Mesh = collapsed,
                Skin = source.Skin,
                Skeleton = source.Skeleton,
                // Il personaggio guarda +Z: arretrare e' -Z nello spazio dello scheletro.
                Transform = source.Transform.Translated(new Vector3(0f, 0f, -backOffset)),
                Layers = source.Layers,
                MaterialOverride = source.MaterialOverride,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            source.GetParent().AddChild(view);
            if (hidden.VariantType != Variant.Type.Nil)
                view.SetInstanceShaderParameter(CharacterOutfit.HiddenRegionsParam, hidden);
            source.CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly;

            ArrayMesh? seated = neck >= 0 ? copies[1] ?? mesh : null;
            if (seated != null)
            {
                var seatedView = new MeshInstance3D
                {
                    Name = source.Name + SeatedSuffix,
                    Mesh = seated,
                    Skin = source.Skin,
                    Skeleton = source.Skeleton,
                    Transform = source.Transform,
                    Layers = source.Layers,
                    MaterialOverride = source.MaterialOverride,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    Visible = false,
                };
                source.GetParent().AddChild(seatedView);
                if (hidden.VariantType != Variant.Type.Nil)
                    seatedView.SetInstanceShaderParameter(CharacterOutfit.HiddenRegionsParam, hidden);
            }
        }
    }

    /// <summary>
    /// Sceglie la resa del corpo locale: la copia "seduti" (braccia visibili) o quella normale (solo
    /// bacino e gambe); in <paramref name="thirdPerson"/> (la camera corrente non e' quella in prima
    /// persona, es. la visuale esterna al timone) nessuna copia, e l'originale torna visibile intero.
    /// Non fa nulla se <see cref="Build"/> non e' stato chiamato (avatar remoti).
    /// </summary>
    public static void SetView(Node root, bool seated, bool thirdPerson)
    {
        foreach (Node node in root.FindChildren("*" + ViewSuffix + "*", nameof(MeshInstance3D), true, false))
        {
            if (node is not MeshInstance3D view)
                continue;
            string name = view.Name.ToString();
            bool isSeated = name.EndsWith(SeatedSuffix);
            view.Visible = !thirdPerson && isSeated == seated;

            if (isSeated || !name.EndsWith(ViewSuffix))
                continue;
            if (view.GetParent().GetNodeOrNull<MeshInstance3D>(name[..^ViewSuffix.Length]) is { } source)
            {
                source.CastShadow = thirdPerson
                    ? GeometryInstance3D.ShadowCastingSetting.On
                    : GeometryInstance3D.ShadowCastingSetting.ShadowsOnly;
            }
        }
    }

    /// <summary>
    /// Vista da seduti: testa, collo e busto (spina, petto) collassano sulla spina; spalle e braccia
    /// fino al gomito sul gomito. Restano avambracci, mani e gambe: sono loro che si vedono sul
    /// governo, mentre spalle e busto, a pochi centimetri dalla camera (e visibili anche dall'interno),
    /// riempirebbero lo schermo. Il busto NON va sul collo: l'addome e' pesato anche sul bacino, e lo
    /// si tirerebbe in alto in lastre che attraversano la vista.
    /// </summary>
    private static System.Func<int, int> SeatedPivots(Skeleton3D skeleton)
    {
        int spine = skeleton.FindBone(HumanoidBones.Spine);
        int neck = skeleton.FindBone(HumanoidBones.Neck);
        var map = new System.Collections.Generic.Dictionary<int, int>();
        foreach (string name in new[] { HumanoidBones.Spine, HumanoidBones.Chest, HumanoidBones.UpperChest })
        {
            int b = skeleton.FindBone(name);
            if (b >= 0)
                map[b] = spine;
        }
        foreach (string side in new[] { "Left", "Right" })
        {
            int elbow = skeleton.FindBone(side + "LowerArm");
            foreach (string part in new[] { "Shoulder", "UpperArm" })
            {
                int b = skeleton.FindBone(side + part);
                if (b >= 0 && elbow >= 0)
                    map[b] = elbow;
            }
        }
        return b => map.TryGetValue(b, out int pivot) ? pivot : (neck >= 0 && IsSelfOrDescendant(skeleton, b, neck) ? spine : -1);
    }

    /// <summary>
    /// Copia della mesh con i vertici pesati su ossi "nascosti" spostati sul punto del loro osso di
    /// riferimento (pivot), in proporzione al peso: la parte diventa un punto e la superficie resta
    /// chiusa. <paramref name="pivotFor"/> da' per ogni osso il suo pivot, o -1 se resta com'e'; senza,
    /// <paramref name="bone"/> e i suoi discendenti collassano su <paramref name="bone"/>. Ritorna
    /// null se non c'e' nulla da collassare.
    /// </summary>
    private static ArrayMesh? BuildCollapsed(ArrayMesh source, Skin? skin, Skeleton3D skeleton, int bone,
        System.Func<int, int>? pivotFor = null, bool cull = false)
    {
        if (source.GetBlendShapeCount() > 0)
        {
            GD.PushWarning("[FirstPersonBodyView] mesh con blend shape non supportata: il corpo resta intero.");
            return null;
        }

        bool multiPivot = pivotFor != null;
        pivotFor ??= b => IsSelfOrDescendant(skeleton, b, bone) ? bone : -1;
        var original = new int[8];
        ResolveBinds(skin, skeleton, pivotFor, out int[] pivotBindOf, out Vector3[] pivotOf);
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
            var gone = new bool[vertices.Length];
            for (int v = 0; v < vertices.Length && perVertex > 0; v++)
            {
                // Il vertice va TUTTO sul pivot dell'influenza nascosta piu' pesante: con una media,
                // un'ascella pesata fra busto e braccio finirebbe a mezz'aria fra i due pivot.
                for (int k = 0; k < perVertex; k++)
                    original[k] = bones[v * perVertex + k];
                float weight = 0f;
                float best = 0f;
                Vector3 target = Vector3.Zero;
                for (int k = 0; k < perVertex; k++)
                {
                    int bind = bones[v * perVertex + k];
                    if (bind < 0 || bind >= pivotBindOf.Length || pivotBindOf[bind] == -1)
                        continue;

                    float w = weights[v * perVertex + k];
                    weight += w;
                    if (w > best)
                    {
                        best = w;
                        target = pivotOf[bind];
                    }

                    // Il vertice collassato deve seguire l'osso su cui e' collassato, non quello
                    // che lo muoveva prima: un vertice della mano portato sul bacino ma ancora
                    // pesato sulla mano verrebbe trascinato via a ogni movimento del braccio.
                    if (pivotBindOf[bind] >= 0)
                        bones[v * perVertex + k] = pivotBindOf[bind];
                }

                if (weight <= 0f)
                    continue;

                // Con piu' pivot (vista da seduti) un vertice pesato poco sulle parti nascoste resta
                // dov'e': spostato a meta' verso il gomito o la spina, tenderebbe la pelle a lama.
                if (multiPivot && weight < 0.5f)
                {
                    for (int k = 0; k < perVertex; k++)
                        bones[v * perVertex + k] = original[k];
                    continue;
                }

                vertices[v] = vertices[v].Lerp(target, Mathf.Min(weight, 1f));
                gone[v] = weight >= 0.5f;
                changed = true;
            }

            arrays[(int)Mesh.ArrayType.Vertex] = vertices;
            arrays[(int)Mesh.ArrayType.Bones] = bones;

            if (cull && arrays[(int)Mesh.ArrayType.Index].VariantType != Variant.Type.Nil)
            {
                int[] index = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
                var kept = new System.Collections.Generic.List<int>(index.Length);
                for (int t = 0; t + 2 < index.Length; t += 3)
                {
                    if (gone[index[t]] || gone[index[t + 1]] || gone[index[t + 2]])
                        continue;
                    kept.Add(index[t]);
                    kept.Add(index[t + 1]);
                    kept.Add(index[t + 2]);
                }
                if (kept.Count == 0)
                    continue;          // tutto nascosto: niente superficie
                arrays[(int)Mesh.ArrayType.Index] = kept.ToArray();
            }

            // I LOD dell'import non si copiano: questa mesh la guarda solo chi ci sta dentro.
            Mesh.ArrayFormat flags = source.SurfaceGetFormat(s) & Mesh.ArrayFormat.FlagUse8BoneWeights;
            result.AddSurfaceFromArrays(source.SurfaceGetPrimitiveType(s), arrays, flags: flags);
            result.SurfaceSetMaterial(result.GetSurfaceCount() - 1, source.SurfaceGetMaterial(s));
        }

        if (cull && changed && result.GetSurfaceCount() == 0)
            return EmptyMesh;
        return changed ? result : null;
    }

    /// <summary>
    /// Gli indici d'osso di una mesh skinnata sono indici di BIND della Skin, non dello scheletro.
    /// Per ogni bind: il bind del suo pivot (-1 = non si collassa; -2 = si collassa ma il pivot non ha
    /// un bind, quindi resta l'osso di prima) e il punto del pivot nello spazio della mesh, cioe'
    /// l'inversa della bind pose del pivot (in mancanza, la sua rest).
    /// </summary>
    private static void ResolveBinds(Skin? skin, Skeleton3D skeleton, System.Func<int, int> pivotFor,
        out int[] pivotBindOf, out Vector3[] pivotOf)
    {
        int count = skin?.GetBindCount() ?? skeleton.GetBoneCount();
        var boneOfBind = new int[count];
        var bindOfBone = new System.Collections.Generic.Dictionary<int, int>();
        for (int i = 0; i < count; i++)
        {
            int target = i;
            if (skin != null)
            {
                StringName name = skin.GetBindName(i);
                target = name.IsEmpty ? skin.GetBindBone(i) : skeleton.FindBone(name);
            }
            boneOfBind[i] = target;
            if (target >= 0)
                bindOfBone[target] = i;
        }

        pivotBindOf = new int[count];
        pivotOf = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            int pivot = boneOfBind[i] >= 0 ? pivotFor(boneOfBind[i]) : -1;
            if (pivot < 0)
            {
                pivotBindOf[i] = -1;
                continue;
            }
            if (bindOfBone.TryGetValue(pivot, out int pivotBind))
            {
                pivotBindOf[i] = pivotBind;
                pivotOf[i] = skin != null
                    ? skin.GetBindPose(pivotBind).AffineInverse().Origin
                    : skeleton.GetBoneGlobalRest(pivot).Origin;
            }
            else
            {
                pivotBindOf[i] = -2;
                pivotOf[i] = skeleton.GetBoneGlobalRest(pivot).Origin;
            }
        }
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
