using Godot;

namespace Lagoon;

/// <summary>
/// Conferma di colpo andato a segno: quattro tacche rosse al centro dello schermo, per un istante.
///
/// NON e' un reticolo: non c'e' nulla di disegnato finche' non si colpisce. Dal fianco si spara
/// senza riferimento a schermo, in mira si usano le tacche dell'arma (skill combat-shooting).
/// Sparisce quando l'inventario o una modale prendono il cursore.
/// </summary>
public partial class HitMarkerOverlay : Control
{
    private const float Seconds = 0.15f;

    private static readonly Color HitColor = new(1f, 0.35f, 0.25f, 0.95f);

    private readonly WeaponController _weapon;
    private readonly GameManager _game;
    private readonly PlayerHud _hud;

    private float _remaining;

    public HitMarkerOverlay(WeaponController weapon, GameManager game, PlayerHud hud)
    {
        _weapon = weapon;
        _game = game;
        _hud = hud;

        // Non deve mai rubare il cursore all'inventario sottostante.
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
    }

    public override void _Ready() => _weapon.ShotResolved += OnShotResolved;

    public override void _ExitTree() => _weapon.ShotResolved -= OnShotResolved;

    public override void _Process(double delta)
    {
        if (_remaining <= 0f)
            return;

        _remaining = Mathf.Max(0f, _remaining - (float)delta);
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_remaining <= 0f || _game.UiModalOpen || _hud.InventoryOpen)
            return;

        Vector2 center = Size * 0.5f;
        Color color = HitColor with { A = HitColor.A * (_remaining / Seconds) };

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
        if (!hit || !isLocalShooter)
            return;

        _remaining = Seconds;
        QueueRedraw();
    }
}
