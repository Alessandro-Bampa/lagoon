using Godot;

namespace Lagoon;

/// <summary>
/// Oggetto che galleggia (casse, barili, detriti, boe): un <c>RigidBody3D</c> con punti di
/// galleggiamento (<c>Marker3D</c> figli di <c>Floaters</c>).
///
/// Stesso schema di <see cref="BoatController"/>, senza timone: fisica SOLO sull'host
/// (<see cref="Buoyancy.Apply"/>), che pubblica la posa; i client congelano il corpo e si avvicinano
/// alla posa replicata. Se l'host tace, l'oggetto si ferma. Il nodo sta nel livello (NodePath uguale
/// su ogni peer): per oggetti creati a runtime servira' uno spawner.
///
/// Collisioni (vedi <c>FloatingCrate.tscn</c>): layer 5 <c>vehicles</c>, maschera world + vehicles.
/// Il giocatore non ha il layer 5 nella maschera: non spinge le casse (Jolt tratta il CharacterBody
/// come massa infinita, le scaglierebbe via), ma la barca le urta.
/// </summary>
public partial class FloatingBody : RigidBody3D
{
    [Export] public float MaxSubmerge { get; set; } = 0.4f;
    /// 2 = galleggia immerso a meta'; 3 = piu' leggero (legno secco), 1,2 = quasi a fil d'acqua.
    [Export] public float BuoyancyRatio { get; set; } = 2.5f;
    [Export] public float VerticalDrag { get; set; } = 3.0f;
    /// Trascinamento orizzontale rispetto all'acqua: e' quello che fa "andare con le onde".
    [Export] public float HorizontalDrag { get; set; } = 0.8f;
    [Export] public float RemoteLerpSpeed { get; set; } = 12.0f;

    [Export] public Vector3 SyncBodyPosition { get; set; }
    [Export] public Quaternion SyncBodyRotation { get; set; } = Quaternion.Identity;

    private Node3D[] _floaters = System.Array.Empty<Node3D>();
    private WaterVolume? _water;
    private EventBus? _eventBus;
    private float _gravity;

    public override void _EnterTree()
    {
        SetMultiplayerAuthority(NetworkConstants.HostPeerId);
    }

    public override void _Ready()
    {
        _floaters = Buoyancy.CollectPoints(GetNodeOrNull("Floaters"));
        _water = WaterVolume.Find(this);
        _gravity = (float)(double)ProjectSettings.GetSetting("physics/3d/default_gravity", 9.8);
        FreezeMode = FreezeModeEnum.Kinematic;

        SyncBodyPosition = GlobalPosition;
        SyncBodyRotation = GlobalBasis.GetRotationQuaternion();

        _eventBus = GetNodeOrNull<EventBus>("/root/EventBus");
        if (_eventBus != null)
        {
            _eventBus.ConnectedToServer += ApplySimulationMode;
            _eventBus.NetworkError += OnNetworkError;
        }
        ApplySimulationMode();
    }

    public override void _ExitTree()
    {
        if (_eventBus != null)
        {
            _eventBus.ConnectedToServer -= ApplySimulationMode;
            _eventBus.NetworkError -= OnNetworkError;
        }
    }

    private void OnNetworkError(string message) => ApplySimulationMode();

    /// Host: fisica vera. Client: corpo congelato, solo presentazione (vedi BoatController).
    private void ApplySimulationMode()
    {
        Freeze = !IsMultiplayerAuthority();
        if (Freeze)
        {
            LinearVelocity = Vector3.Zero;
            AngularVelocity = Vector3.Zero;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!IsMultiplayerAuthority())
        {
            float t = Mathf.Clamp((float)delta * RemoteLerpSpeed, 0f, 1f);
            GlobalPosition = GlobalPosition.Lerp(SyncBodyPosition, t);
            Quaternion = Quaternion.Slerp(SyncBodyRotation.Normalized(), t).Normalized();
            return;
        }

        if (_water != null)
        {
            var settings = new Buoyancy.Settings(MaxSubmerge, BuoyancyRatio, VerticalDrag, HorizontalDrag);
            Buoyancy.Apply(this, _floaters, _water, _gravity, settings);
        }

        SyncBodyPosition = GlobalPosition;
        SyncBodyRotation = GlobalBasis.GetRotationQuaternion();
    }
}
