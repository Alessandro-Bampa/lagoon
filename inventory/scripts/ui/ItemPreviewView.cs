using Godot;

namespace Lagoon;

/// Riquadro che disegna un item con <see cref="ItemTilePainter"/>: anteprima del trascinamento,
/// scheda Ispeziona. Non intercetta mai il mouse.
public partial class ItemPreviewView : Control
{
    public ItemInstance? Item { get; set; }
    public bool Rotated { get; set; }
    public bool Contain { get; set; }
    public bool ShowName { get; set; } = true;
    public float Alpha { get; set; } = 1f;

    public ItemPreviewView()
    {
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Draw()
    {
        if (Item != null)
            ItemTilePainter.Draw(
                this, new Rect2(Vector2.Zero, Size), Item, Rotated, Contain, Alpha, ShowName);
    }
}
