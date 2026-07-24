using Godot;

namespace Lagoon;

/// <summary>
/// Camera isometrica fissa (CLAUDE.md §7): proiezione ortogonale, angolo fisso, nessuna rotazione
/// libera. E' figlia dell'avatar e ne segue la posizione; l'orientamento viene impostato una volta
/// in <see cref="_Ready"/>. Il player root non ruota mai (ruota solo il nodo "Visual"), quindi la
/// visuale resta stabile mentre l'avatar si muove.
///
/// Lo yaw qui DEVE combaciare con PlayerController.CameraYawDegrees, cosi' che l'input WASD sia
/// allineato agli assi dello schermo.
/// </summary>
public partial class IsometricCamera : Camera3D
{
    [Export] public float Distance { get; set; } = 16.0f;
    [Export] public float YawDegrees { get; set; } = 45.0f;
    [Export] public float PitchDegrees { get; set; } = 40.0f;
    [Export] public float OrthogonalSize { get; set; } = 14.0f;

    public override void _Ready()
    {
        Projection = ProjectionType.Orthogonal;
        Size = OrthogonalSize;

        float yaw = Mathf.DegToRad(YawDegrees);
        float pitch = Mathf.DegToRad(PitchDegrees);

        // Posizione su una sfera attorno all'origine del genitore (l'avatar), poi guarda l'avatar.
        Position = new Vector3(
            Mathf.Sin(yaw) * Mathf.Cos(pitch),
            Mathf.Sin(pitch),
            Mathf.Cos(yaw) * Mathf.Cos(pitch)) * Distance;

        LookAt(GetParent<Node3D>().GlobalPosition, Vector3.Up);
    }
}
