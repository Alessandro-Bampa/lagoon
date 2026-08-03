using Godot;

namespace Lagoon;

/// <summary>
/// Input LOCALE di guida. Nessuna logica di stato (CLAUDE.md §4): traduce i tasti in intenti e li
/// consegna al <see cref="BoatController"/>, che e' host-autoritativo. Modellato su
/// <c>WeaponInput</c>: si spegne del tutto sugli avatar remoti.
///
/// Sta in <c>vehicles/scripts/</c> pur essendo un nodo della scena <c>Player</c>, per lo stesso criterio
/// per cui <c>WeaponInput</c> sta in <c>combat/scripts/</c>: appartiene al sistema veicoli.
///
/// F e' condiviso con la raccolta oggetti. Qui si usa <c>_UnhandledInput</c>, mentre <c>PlayerHud</c>
/// usa <c>_Input</c> e consuma l'evento SOLO quando il proprio contesto vince (skill ui-hud §4).
/// L'arbitro unico e' <see cref="VehicleInteraction.VehicleWins"/>.
/// </summary>
public partial class VehicleInput : Node
{
    /// <summary>
    /// 20 Hz, rate FISSO e incondizionato — non "solo quando i comandi cambiano". Il canale e'
    /// Unreliable: un pacchetto "acceleratore a zero" perso lascerebbe la barca a tutta forza. A rate
    /// fisso ogni pacchetto e' uno stato di intento completo e l'ultimo che arriva vince.
    /// </summary>
    private const float SendIntervalSeconds = 0.05f;

    private PlayerController _player = null!;
    private PlayerInput _input = null!;
    private GameManager _game = null!;
    private float _sinceLastSend;

    public override void _Ready()
    {
        _player = GetParent<PlayerController>();

        // Solo il proprietario dell'avatar produce input (§3). Gli avatar remoti non processano nulla.
        if (!_player.IsMultiplayerAuthority())
        {
            SetPhysicsProcess(false);
            SetProcessUnhandledInput(false);
            return;
        }

        _input = _player.GetNode<PlayerInput>("Input");
        _game = GetNode<GameManager>("/root/GameManager");
    }

    public override void _PhysicsProcess(double delta)
    {
        BoatController? boat = _player.DrivingBoat;
        if (boat == null || !GodotObject.IsInstanceValid(boat))
        {
            _sinceLastSend = SendIntervalSeconds; // il prossimo intento parte subito
            return;
        }

        _sinceLastSend += (float)delta;
        if (_sinceLastSend < SendIntervalSeconds)
            return;
        _sinceLastSend = 0f;

        Vector2 motion = _input.ReadRawMovement();

        // Comandi RELATIVI AL VEICOLO, non allo schermo: volutamente NON si ruota dello yaw della
        // camera come si fa per il camminare. E' la convenzione standard per un veicolo, rende la
        // barca guidabile allo stesso modo qualunque direzione stia tenendo, e da quando la camera
        // ruota (Q/E) e' anche l'unica scelta sensata — altrimenti il timone cambierebbe significato
        // a ogni scatto di visuale.
        _player.DrivingBoat?.SubmitControls(-motion.Y, motion.X);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!@event.IsActionPressed("interact") || _game.UiModalOpen)
            return;

        // Al timone: F lo lascia. E' il caso che vince su tutto (vedi VehicleInteraction).
        BoatController? driving = _player.DrivingBoat;
        if (driving != null && GodotObject.IsInstanceValid(driving))
        {
            driving.SubmitLeaveHelm();
            GetViewport().SetInputAsHandled();
            return;
        }

        BoatController? nearest = VehicleRegistry.NearestHelm(_player, _player.GlobalPosition, out _);
        if (nearest == null || nearest.HasPilot)
            return;

        // Solo un intento: l'host rivalida timone libero e distanza sulla posizione replicata.
        nearest.SubmitTakeHelm();
        GetViewport().SetInputAsHandled();
    }
}
