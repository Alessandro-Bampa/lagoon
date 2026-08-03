using Godot;

namespace Lagoon;

/// <summary>
/// Camera isometrica: proiezione ortogonale, inclinazione e distanza FISSE, imbardata ruotabile a
/// scatti dal giocatore (Q/E, azioni <c>camera_rotate_left</c> e <c>camera_rotate_right</c>). E'
/// figlia dell'avatar e ne segue la posizione; il player root non ruota mai — ruota solo il nodo
/// "Visual" — quindi la visuale resta stabile mentre l'avatar si muove.
///
/// SCATTI, NON ROTAZIONE LIBERA. Ogni pressione somma <see cref="StepDegrees"/> (45°) e la camera ci
/// arriva interpolata: otto orientamenti possibili, tutti multipli di 45°, tutti allineati alla
/// geometria squadrata del mondo. Una rotazione continua darebbe angoli qualunque, in cui muri e
/// solai si presentano storti e l'inquadratura perde la lettura isometrica; ed e' anche cio' che
/// tiene stabile il significato del WASD, che si sposta a scatti insieme alla camera invece di
/// scivolare sotto le dita.
///
/// <see cref="CurrentYawDegrees"/> e' l'imbardata EFFETTIVA, interpolata, e va letta da chiunque
/// debba allineare qualcosa alla visuale — <c>PlayerController</c> per il movimento,
/// <c>VehicleInput</c> per il timone. Non esiste piu' una costante da tenere in sincrono a mano fra
/// due file: c'era, si chiamava <c>PlayerController.CameraYawDegrees</c>, ed era corretta solo
/// finche' la camera non ruotava.
///
/// La camera continua a NON ruotare per conto proprio, e in particolare non ruota col rinculo: la
/// scossa e' solo una traslazione sul piano immagine. La mira (<c>AimResolver</c>) non ha invece
/// bisogno di sapere nulla di tutto questo, perche' lavora su <c>ProjectRayOrigin</c>/
/// <c>ProjectRayNormal</c>, che seguono la camera qualunque sia il suo orientamento.
/// </summary>
public partial class IsometricCamera : Camera3D
{
    [Export] public float Distance { get; set; } = 16.0f;

    /// Imbardata iniziale. Le rotazioni successive partono da qui e restano sui suoi multipli.
    [Export] public float YawDegrees { get; set; } = 45.0f;

    [Export] public float PitchDegrees { get; set; } = 40.0f;
    [Export] public float OrthogonalSize { get; set; } = 14.0f;

    /// Ampiezza di uno scatto di rotazione, in gradi.
    [Export] public float StepDegrees { get; set; } = 45.0f;

    /// <summary>
    /// Velocita' con cui la camera raggiunge lo scatto richiesto (frazione al secondo). Piu' alta =
    /// piu' secca. Sotto ~6 la rotazione si sente molliccia e si perde il senso dello scatto.
    /// </summary>
    [Export] public float RotationSpeed { get; set; } = 12.0f;

    /// Velocita' con cui la scossa da rinculo si riassorbe (frazione al secondo).
    [Export] public float KickRecoverySpeed { get; set; } = 12.0f;

    /// <summary>
    /// Imbardata effettiva della camera in questo istante, in GRADI, gia' interpolata verso lo
    /// scatto richiesto. E' la fonte di verita' per chiunque debba allineare l'input alla visuale.
    /// </summary>
    public float CurrentYawDegrees { get; private set; }

    /// Imbardata verso cui si sta andando: multiplo di <see cref="StepDegrees"/> a partire da
    /// <see cref="YawDegrees"/>.
    private float _targetYawDegrees;

    private Vector3 _kickOffset;
    private readonly RandomNumberGenerator _rng = new();

    public override void _Ready()
    {
        Projection = ProjectionType.Orthogonal;
        Size = OrthogonalSize;

        CurrentYawDegrees = YawDegrees;
        _targetYawDegrees = YawDegrees;
        ApplyOrientation();

        _rng.Randomize();
    }

    /// <summary>
    /// L'input di rotazione si legge in <c>_UnhandledInput</c> e non in <c>_Process</c>: e' un evento
    /// discreto, e cosi' una UI modale che consuma i tasti (inventario, menu) lo intercetta prima,
    /// senza bisogno di consultare <c>GameManager.UiModalOpen</c> qui dentro.
    ///
    /// Vale solo per l'avatar LOCALE: la camera di un avatar remoto esiste ma non e' <c>Current</c>,
    /// e farle leggere l'input farebbe ruotare quattro camere insieme.
    /// </summary>
    public override void _UnhandledInput(InputEvent @event)
    {
        if (!Current)
            return;

        // I segni sono quelli che a schermo si leggono come "la visuale gira a sinistra / a destra".
        // Non sono deducibili dalla formula: dipendono dal verso in cui l'imbardata muove la camera
        // sulla sfera, e l'unico modo di stabilirli e' guardare.
        if (@event.IsActionPressed("camera_rotate_left"))
            Rotate(-1);
        else if (@event.IsActionPressed("camera_rotate_right"))
            Rotate(+1);
        else
            return;

        GetViewport().SetInputAsHandled();
    }

    /// <summary>
    /// Somma uno scatto. Il bersaglio NON si normalizza dentro [0, 360): resta un valore che cresce o
    /// cala senza limiti, cosi' l'interpolazione prende sempre la strada corta e non fa mai un giro
    /// completo passando per lo zero.
    /// </summary>
    private void Rotate(int direction) => _targetYawDegrees += direction * StepDegrees;

    public override void _Process(double delta)
    {
        bool turning = !Mathf.IsEqualApprox(CurrentYawDegrees, _targetYawDegrees);

        if (turning)
        {
            CurrentYawDegrees = Mathf.Lerp(
                CurrentYawDegrees, _targetYawDegrees,
                Mathf.Clamp((float)delta * RotationSpeed, 0f, 1f));

            if (Mathf.Abs(_targetYawDegrees - CurrentYawDegrees) < 0.01f)
                CurrentYawDegrees = _targetYawDegrees;
        }

        if (!turning && _kickOffset.IsZeroApprox())
            return;

        // Solo traslazione: la camera non ruota per il rinculo. E' un invariante della skill
        // combat-shooting — una scossa che ruotasse sposterebbe anche il punto mirato.
        _kickOffset = _kickOffset.Lerp(Vector3.Zero, Mathf.Clamp((float)delta * KickRecoverySpeed, 0f, 1f));

        ApplyOrientation();
    }

    /// <summary>
    /// Ricolloca la camera sulla sfera attorno all'avatar e la fa guardare verso di lui.
    ///
    /// Il <c>LookAt</c> va fatto sulla posizione BASE, senza la scossa: applicandolo alla posizione
    /// scossa la camera ruoterebbe leggermente a ogni colpo per riportare l'avatar al centro, che e'
    /// esattamente cio' che l'invariante "la camera non ruota" esclude.
    /// </summary>
    private void ApplyOrientation()
    {
        float yaw = Mathf.DegToRad(CurrentYawDegrees);
        float pitch = Mathf.DegToRad(PitchDegrees);

        Vector3 basePosition = new Vector3(
            Mathf.Sin(yaw) * Mathf.Cos(pitch),
            Mathf.Sin(pitch),
            Mathf.Cos(yaw) * Mathf.Cos(pitch)) * Distance;

        Position = basePosition;
        LookAt(GetParent<Node3D>().GlobalPosition, Vector3.Up);
        Position = basePosition + _kickOffset;
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
