using Godot;

namespace Lagoon;

/// <summary>
/// Visuale in terza persona del pilota LOCALE: una camera che orbita attorno alla barca che si
/// guida. Si alterna alla prima persona con <c>vehicle_camera_toggle</c> e si riporta verso la prua
/// con <c>vehicle_camera_reset</c> (tasti gestiti da <see cref="VehicleInput"/>).
///
/// <list type="bullet">
/// <item><b>Distanza dalla grandezza della barca</b>: all'attivazione si misura il riquadro delle
/// mesh visibili della barca nel suo spazio locale; la distanza e' proporzionale al raggio in
/// pianta (lunghezza e larghezza, non l'altezza: un albero non deve allontanare la visuale).
/// Barchino ~7 m, peschereccio ~24 m.</item>
/// <item><b>Orbita relativa allo scafo</b>: imbardata e inclinazione dell'orbita sono scarti dalla
/// prua, quindi la visuale vira con la barca. La prua e' smorzata e si segue solo l'imbardata: beccheggio
/// e rollio dello scafo non entrano nella camera (sul mare mosso darebbe la nausea).</item>
/// <item><b>Mouse</b>: a mouse catturato ruota l'orbita (stessa sensibilita' della prima persona).
/// Il reset riporta l'orbita a zero, cioe' dietro la poppa a guardare la prua.</item>
/// <item><b>Ostacoli</b>: un raggio dal perno alla camera (mondo e ALTRI veicoli, mai la propria
/// barca) la avvicina se c'e' qualcosa in mezzo; non scende mai sotto la superficie dell'acqua.</item>
/// </list>
///
/// Pura resa locale (CLAUDE.md §3): nessuna RPC, nessuno stato replicato. L'unico effetto fuori
/// dalla camera e' che lo sguardo del pilota (<see cref="FirstPersonCamera.SetLook"/>) segue
/// l'imbardata della visuale: e' lo stesso dato client-autoritativo che il mouse scrive in prima
/// persona, e fa girare la testa del pilota verso dove guarda il giocatore.
///
/// La scelta di visuale e' ricordata per la sessione: lasciato il timone si torna in prima persona,
/// ma ripreso il timone si riparte dalla visuale lasciata.
/// </summary>
public partial class BoatChaseCamera : Camera3D
{
    /// Rotazione per pixel di mouse a sensibilita' 1, in radianti (uguale a FirstPersonCamera).
    private const float RadiansPerPixel = 0.0022f;

    /// Distanza = raggio in pianta della barca × questo fattore + <see cref="DistanceOffset"/>.
    [Export] public float DistanceFactor { get; set; } = 2.2f;

    [Export] public float DistanceOffset { get; set; } = 1.5f;
    [Export] public float MinDistance { get; set; } = 5.0f;
    [Export] public float MaxDistance { get; set; } = 60.0f;

    /// Altezza dell'orbita a riposo, in gradi sopra l'orizzonte (la camera guarda in basso).
    [Export] public float DefaultPitchDegrees { get; set; } = 12.0f;

    [Export] public float MinPitchDegrees { get; set; } = -5.0f;
    [Export] public float MaxPitchDegrees { get; set; } = 70.0f;

    /// Prontezza con cui la visuale segue la prua che cambia, in 1/s.
    [Export] public float HeadingFollowSpeed { get; set; } = 6.0f;

    /// Prontezza del ritorno verso la prua dopo il reset, in 1/s.
    [Export] public float ResetSpeed { get; set; } = 6.0f;

    /// Prontezza con cui la camera si riallontana dopo essere stata avvicinata da un ostacolo, in 1/s.
    [Export] public float ReturnSpeed { get; set; } = 3.0f;

    /// Distanza tenuta dalle superfici colpite dal raggio anti-ostacolo, in metri.
    [Export] public float CollisionMargin { get; set; } = 0.4f;

    /// Quota minima della camera sopra la superficie dell'acqua, in metri.
    [Export] public float WaterClearance { get; set; } = 0.6f;

    [Export] public float FovDegrees { get; set; } = 70.0f;

    /// Visuale in terza persona attiva (questa camera e' quella corrente).
    public bool Active { get; private set; }

    private PlayerController _player = null!;
    private FirstPersonCamera _firstPerson = null!;
    private SettingsService _settings = null!;
    private WaterVolume? _water;

    /// Visuale scelta l'ultima volta al timone: si ripropone al prossimo imbarco.
    private bool _preferThirdPerson;

    private BoatController? _boat;
    private Vector3 _pivotLocal;
    private float _distance;
    private float _currentDistance;
    private readonly Godot.Collections.Array<Rid> _exclude = new();

    private float _heading;
    private float _orbitYaw;
    private float _orbitPitch;
    private bool _resetting;

    /// Da chiamare prima di aggiungere il nodo all'albero (lo fa <see cref="VehicleInput"/>).
    public void Setup(PlayerController player, FirstPersonCamera firstPerson)
    {
        _player = player;
        _firstPerson = firstPerson;
    }

    public override void _Ready()
    {
        _settings = GetNode<SettingsService>("/root/SettingsService");
        _water = WaterVolume.Find(_player);
        Current = false;
        Fov = FovDegrees;
    }

    public override void _ExitTree()
    {
        if (Active && GodotObject.IsInstanceValid(_firstPerson))
            _firstPerson.MakeCurrent();
        Active = false;
    }

    /// Alterna prima e terza persona. Senza timone non fa nulla.
    public void Toggle()
    {
        if (_boat == null)
            return;

        _preferThirdPerson = !Active;
        if (Active)
            Deactivate();
        else
            Activate();
    }

    /// Riporta la visuale verso la prua (gradualmente).
    public void ResetView()
    {
        if (Active)
            _resetting = true;
    }

    public override void _Input(InputEvent @event)
    {
        if (!Active || @event is not InputEventMouseMotion motion
            || Input.MouseMode != Input.MouseModeEnum.Captured)
            return;

        // ScreenRelative come in FirstPersonCamera: la scala UI non deve cambiare la sensibilita'.
        float scale = RadiansPerPixel * _settings.MouseSensitivity;
        _orbitYaw = Mathf.Wrap(_orbitYaw - motion.ScreenRelative.X * scale, -Mathf.Pi, Mathf.Pi);
        _orbitPitch = Mathf.Clamp(_orbitPitch + motion.ScreenRelative.Y * scale,
            Mathf.DegToRad(MinPitchDegrees), Mathf.DegToRad(MaxPitchDegrees));
        _resetting = false;
    }

    public override void _Process(double delta)
    {
        BoatController? boat = _player.DrivingBoat;
        if (boat != null && !GodotObject.IsInstanceValid(boat))
            boat = null;

        // La modalita' "al timone" e' dedotta dallo stato replicato della barca (PlayerController.Mode):
        // qui si segue e basta, senza un secondo stato di aggancio.
        if (boat != _boat)
        {
            if (Active)
                Deactivate();
            _boat = boat;
            if (_boat != null && _preferThirdPerson)
                Activate();
        }

        if (!Active || _boat == null)
            return;

        float dt = (float)delta;
        _heading = Mathf.LerpAngle(_heading, _boat.HeadingYaw, Damp(HeadingFollowSpeed, dt));

        if (_resetting)
        {
            float k = Damp(ResetSpeed, dt);
            float defaultPitch = Mathf.DegToRad(DefaultPitchDegrees);
            _orbitYaw = Mathf.LerpAngle(_orbitYaw, 0f, k);
            _orbitPitch = Mathf.Lerp(_orbitPitch, defaultPitch, k);
            if (Mathf.Abs(_orbitYaw) < 0.002f && Mathf.Abs(_orbitPitch - defaultPitch) < 0.002f)
                _resetting = false;
        }

        float yaw = _heading + _orbitYaw;
        float cosPitch = Mathf.Cos(_orbitPitch);
        var forward = new Vector3(Mathf.Sin(yaw) * cosPitch, -Mathf.Sin(_orbitPitch), Mathf.Cos(yaw) * cosPitch);

        Vector3 pivot = _boat.GlobalTransform * _pivotLocal;
        float allowed = ObstacleDistance(pivot, -forward, _distance);

        // Un ostacolo avvicina di scatto (mai attraverso un muro), poi ci si riallontana con calma.
        _currentDistance = allowed < _currentDistance
            ? allowed
            : Mathf.Lerp(_currentDistance, allowed, Damp(ReturnSpeed, dt));

        Vector3 position = pivot - forward * _currentDistance;
        if (_water != null && _water.Contains(position))
            position.Y = Mathf.Max(position.Y, _water.HeightAt(position) + WaterClearance);

        // Camera3D guarda lungo -Z, l'imbardata zero guarda +Z: da qui il mezzo giro (come FirstPersonCamera).
        GlobalTransform = new Transform3D(Basis.FromEuler(new Vector3(-_orbitPitch, yaw + Mathf.Pi, 0f)), position);

        // La testa del pilota guarda dove guarda la visuale (entro il limite di PlayerController).
        _firstPerson.SetLook(yaw, 0f);
    }

    private void Activate()
    {
        if (_boat == null)
            return;

        MeasureBoat(_boat);
        _heading = _boat.HeadingYaw;
        _orbitYaw = 0f;
        _orbitPitch = Mathf.DegToRad(DefaultPitchDegrees);
        _currentDistance = _distance;
        _resetting = false;
        Fov = FovDegrees;
        MakeCurrent();
        Active = true;
    }

    private void Deactivate()
    {
        Active = false;
        _firstPerson.MakeCurrent();
    }

    /// <summary>
    /// Riquadro delle mesh visibili della barca nel suo spazio locale: da qui il perno dell'orbita
    /// (in pianta al centro, in quota ai tre quarti dell'altezza) e la distanza. Si rimisura a ogni
    /// attivazione: le barche su misura si montano a runtime (BoatAssembly).
    /// </summary>
    private void MeasureBoat(BoatController boat)
    {
        Transform3D toLocal = boat.GlobalTransform.AffineInverse();
        Aabb? bounds = null;
        foreach (Node node in boat.FindChildren("*", nameof(MeshInstance3D), true, false))
        {
            if (node is not MeshInstance3D mesh || mesh.Mesh == null || !mesh.IsVisibleInTree())
                continue;
            Aabb box = toLocal * mesh.GlobalTransform * mesh.GetAabb();
            bounds = bounds?.Merge(box) ?? box;
        }

        // Ripiego per una barca senza mesh: misure di un barchino.
        Aabb b = bounds ?? new Aabb(new Vector3(-1f, -0.5f, -2.5f), new Vector3(2f, 1.5f, 5f));
        Vector3 center = b.GetCenter();
        _pivotLocal = new Vector3(center.X, b.Position.Y + b.Size.Y * 0.75f, center.Z);

        float radius = 0.5f * new Vector2(b.Size.X, b.Size.Z).Length();
        _distance = Mathf.Clamp(radius * DistanceFactor + DistanceOffset, MinDistance, MaxDistance);

        _exclude.Clear();
        foreach (Node node in boat.FindChildren("*", nameof(CollisionObject3D), true, false))
            if (node is CollisionObject3D body)
                _exclude.Add(body.GetRid());
        _exclude.Add(boat.GetRid());
    }

    /// Distanza percorribile dal perno lungo <paramref name="direction"/> senza attraversare nulla.
    private float ObstacleDistance(Vector3 pivot, Vector3 direction, float distance)
    {
        var query = PhysicsRayQueryParameters3D.Create(pivot, pivot + direction * distance,
            CollisionLayers.World | CollisionLayers.Vehicles | CollisionLayers.VehicleDeck, _exclude);
        Godot.Collections.Dictionary hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (hit.Count == 0)
            return distance;

        float reached = pivot.DistanceTo((Vector3)hit["position"]);
        return Mathf.Max(reached - CollisionMargin, 0.5f);
    }

    private static float Damp(float speed, float dt) => 1.0f - Mathf.Exp(-speed * dt);
}
