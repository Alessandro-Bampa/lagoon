using Godot;

namespace Lagoon;

/// <summary>
/// Reticolo al centro dello schermo. Da disarmati e' un solo punto (serve a puntare gli oggetti da
/// raccogliere); con un'arma in mano si aggiunge l'anello della dispersione.
///
/// L'anello rappresenta il cono di tiro reale: il raggio in pixel deriva dalla stessa formula usata
/// dall'host (<see cref="WeaponDefinition.SpreadDegrees"/>), convertita da angolo a pixel col campo
/// visivo corrente della camera. Sparisce quando l'inventario o una modale prendono il cursore.
/// </summary>
public partial class CrosshairOverlay : Control
{
    private const float MinRadius = 5f;
    private const float MaxRadius = 140f;
    private const float HitMarkerSeconds = 0.15f;

    private static readonly Color RingColor = new(1f, 1f, 1f, 0.65f);
    private static readonly Color DotColor = new(1f, 1f, 1f, 0.9f);
    private static readonly Color HitColor = new(1f, 0.35f, 0.25f, 0.95f);

    private readonly WeaponController _weapon;
    private readonly WeaponInput _input;
    private readonly FirstPersonCamera _camera;
    private readonly GameManager _game;
    private readonly PlayerHud _hud;

    private float _hitMarker;

    public CrosshairOverlay(
        WeaponController weapon, WeaponInput input, FirstPersonCamera camera,
        GameManager game, PlayerHud hud)
    {
        _weapon = weapon;
        _input = input;
        _camera = camera;
        _game = game;
        _hud = hud;

        // Non deve mai rubare il cursore all'inventario sottostante.
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
    }

    public override void _Ready()
    {
        _weapon.ShotResolved += OnShotResolved;
    }

    public override void _ExitTree()
    {
        _weapon.ShotResolved -= OnShotResolved;
    }

    public override void _Process(double delta)
    {
        if (_hitMarker > 0f)
            _hitMarker = Mathf.Max(0f, _hitMarker - (float)delta);

        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_game.UiModalOpen || _hud.InventoryOpen)
            return;

        Vector2 center = Size * 0.5f;
        DrawCircle(center, 2f, DotColor);

        WeaponDefinition? weapon = _weapon.HeldWeapon;
        if (weapon == null)
            return;

        DrawArc(center, RingRadius(weapon), 0f, Mathf.Tau, 48, RingColor, 1.5f, antialiased: true);

        if (_hitMarker > 0f)
            DrawHitMarker(center);
    }

    /// <summary>
    /// Raggio dell'anello in pixel. In prospettiva un angolo dal centro copre
    /// <c>tan(angolo) / tan(fov/2)</c> della mezza altezza dello schermo, a qualunque distanza: la
    /// distanza di mira entra solo nella formula della dispersione.
    /// </summary>
    private float RingRadius(WeaponDefinition weapon)
    {
        float spread = weapon.SpreadDegrees(_input.AimDistance, _weapon.RecoilSpread);
        float halfFov = Mathf.DegToRad(_camera.Fov * 0.5f);
        float radius = Mathf.Tan(Mathf.DegToRad(spread)) / Mathf.Tan(halfFov) * Size.Y * 0.5f;

        return Mathf.Clamp(radius, MinRadius, MaxRadius);
    }

    /// Quattro tacche diagonali: conferma che il colpo ha trovato un bersaglio.
    private void DrawHitMarker(Vector2 center)
    {
        float alpha = _hitMarker / HitMarkerSeconds;
        Color color = HitColor with { A = HitColor.A * alpha };

        const float inner = 5f;
        const float outer = 11f;
        foreach (Vector2 dir in new[]
        {
            new Vector2(1, 1), new Vector2(1, -1), new Vector2(-1, 1), new Vector2(-1, -1),
        })
        {
            Vector2 unit = dir.Normalized();
            DrawLine(center + unit * inner, center + unit * outer, color, 2f, antialiased: true);
        }
    }

    private void OnShotResolved(Vector3 origin, Vector3 end, bool hit, bool isLocalShooter)
    {
        if (hit && isLocalShooter)
            _hitMarker = HitMarkerSeconds;
    }
}
