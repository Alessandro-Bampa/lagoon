using Godot;

namespace Lagoon;

/// <summary>
/// Reticolo: sostituisce visivamente il cursore quando il giocatore ha un'arma in mano.
///
/// Il cursore del sistema resta VISIBILE e non viene catturato: tutto l'inventario e' drag &amp; drop
/// e dipende dal cursore reale (<see cref="InventoryDrag"/>, <see cref="GridPanelView"/>). Il
/// reticolo si disegna quindi attorno alla posizione del mouse invece di sostituirla, e sparisce
/// appena l'inventario o una modale prendono il controllo.
///
/// L'anello rappresenta il cono di tiro reale: il raggio in pixel deriva dalla stessa formula usata
/// dall'host (<see cref="WeaponDefinition.SpreadDegrees"/>) applicata alla distanza di mira
/// corrente. Con camera ortogonale la conversione mondo-&gt;pixel e' esatta e costante
/// (<c>altezzaViewport / Camera3D.Size</c>), quindi quello che si vede e' davvero l'area in cui il
/// colpo puo' cadere.
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
    private readonly IsometricCamera _camera;
    private readonly GameManager _game;
    private readonly PlayerHud _hud;

    private float _hitMarker;

    public CrosshairOverlay(
        WeaponController weapon, WeaponInput input, IsometricCamera camera,
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
        WeaponDefinition? weapon = _weapon.HeldWeapon;
        if (weapon == null || _game.UiModalOpen || _hud.InventoryOpen)
            return;

        Vector2 center = GetViewport().GetMousePosition() - GlobalPosition;

        DrawCircle(center, 2f, DotColor);
        DrawArc(center, RingRadius(weapon), 0f, Mathf.Tau, 48, RingColor, 1.5f, antialiased: true);

        if (_hitMarker > 0f)
            DrawHitMarker(center);
    }

    /// Raggio dell'anello in pixel: dispersione angolare -> raggio nel mondo alla distanza di mira
    /// -> pixel, sfruttando la scala costante della proiezione ortogonale.
    private float RingRadius(WeaponDefinition weapon)
    {
        float spread = weapon.SpreadDegrees(_input.AimDistance, _weapon.RecoilSpread);
        float worldRadius = _input.AimDistance * Mathf.Tan(Mathf.DegToRad(spread));

        float viewportHeight = GetViewport().GetVisibleRect().Size.Y;
        float pixelsPerMeter = _camera.Size <= 0f ? 0f : viewportHeight / _camera.Size;

        return Mathf.Clamp(worldRadius * pixelsPerMeter, MinRadius, MaxRadius);
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
