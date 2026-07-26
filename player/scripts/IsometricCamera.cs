using Godot;

namespace Lagoon;

/// <summary>
/// Camera isometrica fissa: proiezione ortogonale, angolo fisso, nessuna rotazione
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

    /// Velocita' con cui la scossa da rinculo si riassorbe (frazione al secondo).
    [Export] public float KickRecoverySpeed { get; set; } = 12.0f;

    private Vector3 _basePosition;
    private Vector3 _kickOffset;
    private readonly RandomNumberGenerator _rng = new();

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

        _basePosition = Position;
        _rng.Randomize();
    }

    public override void _Process(double delta)
    {
        if (_kickOffset.IsZeroApprox())
            return;

        // Solo traslazione: la camera non ruota MAI. E' un invariante della skill combat-shooting:
        // la matematica della mira in AimResolver assume un orientamento fisso.
        _kickOffset = _kickOffset.Lerp(Vector3.Zero, Mathf.Clamp((float)delta * KickRecoverySpeed, 0f, 1f));
        Position = _basePosition + _kickOffset;
    }

    /// <summary>
    /// Scossa da rinculo, solo estetica e solo locale (nessuna replica: non tocca lo stato di gioco).
    /// Sposta la camera di un piccolo offset casuale sul suo piano immagine, poi rientra.
    /// </summary>
    public void AddKick(float amount)
    {
        if (amount <= 0f)
            return;

        Vector3 right = Basis.X;
        Vector3 up = Basis.Y;
        float angle = _rng.Randf() * Mathf.Tau;
        _kickOffset += (right * Mathf.Cos(angle) + up * Mathf.Sin(angle)) * amount;
    }
}
