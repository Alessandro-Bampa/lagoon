using Godot;

namespace Lagoon;

/// <summary>
/// Configura la "presentazione locale" dell'avatar in base all'autorita' di rete:
///  - attiva la camera isometrica SOLO per il player locale;
///  - tinge il personaggio (verde = tu, rosso = altri) per distinguere gli avatar a colpo d'occhio
///    durante il test multi-istanza (CLAUDE.md §6).
/// Non tocca la logica di movimento: quella vive in <see cref="PlayerController"/>.
/// </summary>
public partial class PlayerNetworkSync : Node
{
    private static readonly Color LocalColor = new(0.2f, 0.8f, 0.3f);
    private static readonly Color RemoteColor = new(0.85f, 0.25f, 0.25f);

    public override void _Ready()
    {
        Node parent = GetParent();
        bool isLocal = parent.IsMultiplayerAuthority();

        // Camera attiva solo sull'avatar locale.
        var camera = parent.GetNode<Camera3D>("PlayerCamera");
        camera.Current = isLocal;

        // Tinta di riconoscimento.
        //
        // Cercava "Visual/MeshInstance3D", il cubo placeholder che non esiste piu' da quando sotto
        // Visual c'e' il CharacterRig: GetNode falliva a ogni spawn. Ora si tinge la mesh del
        // personaggio, ovunque sia nel sottoalbero del rig — che arriva da un .glb rigenerabile,
        // quindi il percorso esatto non e' un appiglio stabile.
        var material = new StandardMaterial3D { AlbedoColor = isLocal ? LocalColor : RemoteColor };
        foreach (MeshInstance3D mesh in FindMeshes(parent.GetNode<Node3D>("Visual")))
            mesh.MaterialOverride = material;
    }

    private static Godot.Collections.Array<MeshInstance3D> FindMeshes(Node root)
    {
        var found = new Godot.Collections.Array<MeshInstance3D>();
        Collect(root, found);
        return found;
    }

    private static void Collect(Node node, Godot.Collections.Array<MeshInstance3D> into)
    {
        if (node is MeshInstance3D mesh)
            into.Add(mesh);

        foreach (Node child in node.GetChildren())
            Collect(child, into);
    }
}
