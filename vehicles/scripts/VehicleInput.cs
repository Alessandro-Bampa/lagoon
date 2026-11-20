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

    /// Visuale in terza persona al timone (solo sull'avatar locale).
    private BoatChaseCamera? _chaseCamera;

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

        // Figlia di questo nodo (un Node, non un Node3D): la sua posa e' gia' in coordinate mondo,
        // e non c'e' da toccare Player.tscn. Esiste solo sul peer proprietario, come la camera FPS.
        _chaseCamera = new BoatChaseCamera { Name = "BoatChaseCamera" };
        _chaseCamera.Setup(_player, _player.GetNode<FirstPersonCamera>("PlayerCamera"));
        AddChild(_chaseCamera);
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
        bool blocked = _game.UiModalOpen;
        float trim = blocked ? 0f : Input.GetAxis("vehicle_trim_down", "vehicle_trim_up");
        float thruster = blocked ? 0f : Input.GetAxis("vehicle_thruster_left", "vehicle_thruster_right");

        // Comandi RELATIVI AL VEICOLO, non allo sguardo: volutamente NON si ruota dello yaw della
        // camera come si fa per il camminare. Al timone ci si puo' guardare attorno, e il timone
        // non deve cambiare significato mentre lo si fa. Il trim e' una velocita' (tenuto = si muove
        // a 2-4°/s); toccarlo toglie il trim all'assistenza finche' non si lascia il timone.
        _player.DrivingBoat?.SubmitControls(-motion.Y, motion.X, trim, thruster);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_game.UiModalOpen)
            return;

        // Visuale al timone: prima/terza persona e ritorno verso la prua. Pura resa locale.
        if (_chaseCamera != null && _player.DrivingBoat != null)
        {
            if (@event.IsActionPressed("vehicle_camera_toggle"))
            {
                _chaseCamera.Toggle();
                GetViewport().SetInputAsHandled();
                return;
            }
            if (@event.IsActionPressed("vehicle_camera_reset"))
            {
                _chaseCamera.ResetView();
                GetViewport().SetInputAsHandled();
                return;
            }
        }

        if (!@event.IsActionPressed("interact"))
            return;

        // Al timone: F lo lascia. E' il caso che vince su tutto (vedi VehicleInteraction).
        BoatController? driving = _player.DrivingBoat;
        if (driving != null && GodotObject.IsInstanceValid(driving))
        {
            driving.SubmitLeaveHelm();
            GetViewport().SetInputAsHandled();
            return;
        }

        // A piedi (o a nuoto): il piu' vicino fra timone e cancello scorrevole (stesso arbitro della HUD).
        VehicleInteraction.Target(_player, out BoatController? nearest, out int gate, out _);
        if (nearest == null)
            return;

        // Solo intenti: l'host rivalida distanza (sulla posizione replicata), timone libero, frequenza.
        if (gate >= 0)
            nearest.SubmitToggleGate(gate);
        else if (!nearest.HasPilot)
            nearest.SubmitTakeHelm();
        else
            return;
        GetViewport().SetInputAsHandled();
    }
}
