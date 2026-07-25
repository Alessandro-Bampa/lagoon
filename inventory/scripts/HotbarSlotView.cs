using Godot;

namespace Lagoon;

/// <summary>
/// Singolo slot della hotbar (menu rapido). Accetta in drop item "quick-usable" e ne memorizza il
/// riferimento tramite l'host (<see cref="PlayerInventory.SubmitAssignQuickSlot"/>). In Fase 2
/// l'"uso" effettivo non e' implementato (arrivera' con consumabili/armi): qui si assegna e si mostra.
/// </summary>
public partial class HotbarSlotView : Control
{
    public const int SlotSize = 56;

    private readonly int _index;
    private readonly ItemInstance? _assigned;
    private readonly Hotbar _hotbar;
    private bool _selected;

    public HotbarSlotView(int index, ItemInstance? assigned, Hotbar hotbar)
    {
        _index = index;
        _assigned = assigned;
        _hotbar = hotbar;
        MouseFilter = MouseFilterEnum.Stop;
    }

    public override void _Ready()
    {
        var size = new Vector2(SlotSize, SlotSize);
        CustomMinimumSize = size;
        Size = size;
        BuildVisual();
    }

    public void SetSelected(bool selected)
    {
        _selected = selected;
        QueueRedraw();
    }

    private void BuildVisual()
    {
        var number = new Label
        {
            // Etichette 4-9 e 0 (i tasti 1-3 restano alle armi, Fase 3).
            Text = PlayerInventoryModel.QuickSlotLabels[_index],
            MouseFilter = MouseFilterEnum.Ignore,
        };
        number.SetAnchorsPreset(LayoutPreset.TopLeft);
        number.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.85f));
        AddChild(number);

        if (_assigned == null)
            return;

        Texture2D? icon = _assigned.Definition.ResolveIcon();
        if (icon != null)
        {
            var tex = new TextureRect
            {
                Texture = icon,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            tex.SetAnchorsPreset(LayoutPreset.FullRect);
            AddChild(tex);
        }
        else
        {
            var rect = new ColorRect
            {
                Color = InventoryColors.ForCategory(_assigned.Definition.Category),
                MouseFilter = MouseFilterEnum.Ignore,
            };
            rect.SetAnchorsPreset(LayoutPreset.FullRect);
            AddChild(rect);
        }
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.10f, 0.10f, 0.12f, 0.85f));
        Color border = _selected ? new Color(0.95f, 0.8f, 0.3f) : new Color(0.35f, 0.35f, 0.4f);
        DrawRect(new Rect2(Vector2.Zero, Size), border, filled: false, width: _selected ? 3f : 2f);
    }

    public override bool _CanDropData(Vector2 atPosition, Variant data)
    {
        // Solo item gia' nell'inventario: quelli a terra vanno prima raccolti. L'host verifica
        // anche che stiano in tasche o rig (regola Tarkov), qui filtriamo solo il caso evidente.
        return InventoryDrag.TryRead(data, out InventoryDrag.Payload payload)
               && payload.From.IsPlayer
               && payload.QuickUsable;
    }

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        if (InventoryDrag.TryRead(data, out InventoryDrag.Payload payload))
            _hotbar.Inventory.SubmitAssignQuickSlot(_index, payload.InstanceId);
    }
}
