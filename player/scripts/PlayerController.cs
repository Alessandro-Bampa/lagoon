using Godot;

namespace Lagoon;

/// <summary>
/// Movimento del giocatore (Fase 1). Segue il pattern CLAUDE.md §3:
///  - se questo peer e' l'autorita' del proprio avatar, calcola il movimento come in singleplayer
///    e scrive lo stato replicato (<see cref="SyncPosition"/>/<see cref="SyncFacing"/>);
///  - altrimenti (avatar remoto) NON calcola nulla: interpola verso lo stato replicato.
///
/// Nota di design (Fase 1): il movimento e' client-authoritative — ogni peer e' autorita' del
/// PROPRIO avatar e ne replica la posizione. La validazione server-side dell'input (anti-cheat)
/// e' rimandata, come la lag-compensation (vedi la skill combat-shooting). Le fasi 2/3
/// (inventario, danno) restano
/// invece pienamente server-authoritative.
/// </summary>
public partial class PlayerController : CharacterBody3D
{
    [Export] public float Speed { get; set; } = 6.0f;
    [Export] public float Gravity { get; set; } = 20.0f;

    /// Fattore di interpolazione per gli avatar remoti (piu' alto = piu' reattivo, meno morbido).
    [Export] public float InterpolationSpeed { get; set; } = 14.0f;

    /// Yaw della camera isometrica. L'input viene ruotato di questo angolo cosi' che
    /// "avanti" sullo schermo corrisponda alla direzione attesa. Deve combaciare con PlayerCamera.
    [Export] public float CameraYawDegrees { get; set; } = 45.0f;

    // Stato replicato dal MultiplayerSynchronizer (vedi Player.tscn).
    [Export] public Vector3 SyncPosition { get; set; }
    [Export] public float SyncFacing { get; set; }

    private PlayerInput _input = null!;
    private Node3D _visual = null!;

    public override void _EnterTree()
    {
        // Il nome del nodo e' l'id del peer proprietario (impostato dallo spawner/host).
        // Impostiamo l'autorita' QUI, prima del _Ready dei figli, cosi' il MultiplayerSynchronizer
        // erediti l'autorita' corretta (recursive = true di default).
        if (int.TryParse(Name, out int peerId))
            SetMultiplayerAuthority(peerId);
    }

    public override void _Ready()
    {
        _input = GetNode<PlayerInput>("Input");
        _visual = GetNode<Node3D>("Visual");

        // Evita che gli avatar remoti "saltino" dall'origine al primo update.
        SyncPosition = GlobalPosition;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (IsMultiplayerAuthority())
            AuthoritativeMovement(delta);
        else
            RemoteInterpolation(delta);
    }

    private void AuthoritativeMovement(double delta)
    {
        Vector2 motion = _input.ReadMovement();
        Vector3 worldDir = new Vector3(motion.X, 0f, motion.Y)
            .Rotated(Vector3.Up, Mathf.DegToRad(CameraYawDegrees));
        if (worldDir.LengthSquared() > 1f)
            worldDir = worldDir.Normalized();

        Vector3 velocity = Velocity;
        velocity.X = worldDir.X * Speed;
        velocity.Z = worldDir.Z * Speed;
        velocity.Y = IsOnFloor() ? 0f : velocity.Y - Gravity * (float)delta;
        Velocity = velocity;
        MoveAndSlide();

        // Pubblica lo stato che verra' replicato agli altri peer.
        SyncPosition = GlobalPosition;
        if (worldDir.LengthSquared() > 0.001f)
        {
            SyncFacing = Mathf.Atan2(worldDir.X, worldDir.Z);
            _visual.Rotation = new Vector3(0f, SyncFacing, 0f);
        }
    }

    private void RemoteInterpolation(double delta)
    {
        float t = Mathf.Clamp((float)delta * InterpolationSpeed, 0f, 1f);
        GlobalPosition = GlobalPosition.Lerp(SyncPosition, t);
        float yaw = Mathf.LerpAngle(_visual.Rotation.Y, SyncFacing, t);
        _visual.Rotation = new Vector3(0f, yaw, 0f);
    }
}
