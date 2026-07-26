using Godot;

namespace Lagoon;

/// <summary>
/// Configura la "presentazione locale" dell'avatar in base all'autorita' di rete:
///  - attiva la camera isometrica SOLO per il player locale;
///  - colora il placeholder (verde = tu, rosso = altri) per rendere evidente il criterio di
///    completamento della Fase 1 durante il test multi-istanza (CLAUDE.md §6).
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

        // Colore placeholder per distinguere a colpo d'occhio locale vs remoto.
        var mesh = parent.GetNode<MeshInstance3D>("Visual/MeshInstance3D");
        mesh.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = isLocal ? LocalColor : RemoteColor,
        };
    }
}
