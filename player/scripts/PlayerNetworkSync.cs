using Godot;

namespace Lagoon;

/// <summary>
/// Tinge il personaggio (verde = tu, rosso = altri) per distinguere gli avatar a colpo d'occhio
/// durante il test multi-istanza (CLAUDE.md §6). In prima persona il verde lo si vede sulle
/// proprie braccia. Non tocca la logica di movimento ne' la camera: quella si attiva da sola
/// (<see cref="FirstPersonCamera"/>).
/// </summary>
public partial class PlayerNetworkSync : Node
{
    private static readonly Color LocalColor = new(0.2f, 0.8f, 0.3f);
    private static readonly Color RemoteColor = new(0.85f, 0.25f, 0.25f);

    public override void _Ready()
    {
        Node parent = GetParent();
        bool isLocal = parent.IsMultiplayerAuthority();

        // Si tinge la mesh del personaggio ovunque sia nel sottoalbero del rig: arriva da un .glb
        // rigenerabile, quindi il percorso esatto non e' un appiglio stabile.
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
