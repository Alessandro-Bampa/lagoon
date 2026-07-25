using Godot;

namespace Lagoon;

/// <summary>
/// Widget puramente visivo di un <see cref="ItemInstance"/> dentro una griglia (icona ruotata secondo
/// lo stato dell'item + conteggio stack). Non intercetta il mouse (<c>MouseFilter.Ignore</c>): drag e
/// drop sono gestiti dalla <see cref="GridPanelView"/> contenitrice, cosi' i drop su una cella
/// occupata raggiungono la griglia invece di essere "rubati" dall'item.
/// </summary>
public partial class ItemView : Control
{
    private readonly ItemInstance _item;

    public ItemView(ItemInstance item)
    {
        _item = item;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Ready()
    {
        var visual = ItemVisual.Build(_item.Definition, _item.Rotated, _item.StackCount);
        CustomMinimumSize = visual.Size;
        Size = visual.Size;
        AddChild(visual);
    }
}
