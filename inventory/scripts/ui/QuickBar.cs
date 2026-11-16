using System.Collections.Generic;
using Godot;

namespace Lagoon;

/// <summary>
/// Barra di accesso rapido, sempre visibile in basso: <see cref="PlayerInventoryModel.QuickSlotCount"/>
/// slot collegati ai tasti 4-9 e 0. Un collegamento vale per <c>InstanceId</c>: se l'oggetto
/// lascia l'inventario lo slot si svuota. Lo slot e' "attivo" solo se l'oggetto sta in rig, tasche
/// o slot speciali; altrove resta collegato ma appare spento.
/// </summary>
public partial class QuickBar : Control
{
    public const int SlotSize = 56;

    private readonly PlayerInventory _inventory;
    private readonly InventoryScreen _screen;
    private readonly List<QuickSlotView> _slots = new();
    private HBoxContainer _row = null!;

    public QuickBar(PlayerInventory inventory, InventoryScreen screen)
    {
        _inventory = inventory;
        _screen = screen;
    }

    public override void _Ready()
    {
        // Copre lo schermo ma lascia passare il mouse (solo gli slot lo intercettano).
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        _row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        _row.MouseFilter = MouseFilterEnum.Ignore;
        _row.AddThemeConstantOverride("separation", 6);
        AddChild(_row);
        _row.SetAnchorsPreset(LayoutPreset.BottomWide);
        _row.OffsetTop = -(SlotSize + 12);
        _row.OffsetBottom = -12;

        for (int i = 0; i < PlayerInventoryModel.QuickSlotCount; i++)
        {
            var slot = new QuickSlotView(i, _inventory, _screen);
            _row.AddChild(slot);
            _slots.Add(slot);
        }
        Refresh();
    }

    public void Refresh()
    {
        foreach (QuickSlotView slot in _slots)
            slot.QueueRedraw();
    }

    /// Lampeggio dello slot alla pressione del tasto (riscontro visivo dell'uso).
    public void Flash(int index)
    {
        if (index >= 0 && index < _slots.Count)
            _slots[index].Flash();
    }
}

/// Singolo slot della barra rapida: bersaglio di rilascio e riquadro dell'oggetto collegato.
public partial class QuickSlotView : Control, IDropTarget
{
    private readonly int _index;
    private readonly PlayerInventory _inventory;
    private readonly InventoryScreen _screen;
    private DragState? _previewDrag;
    private DropResolution _preview;
    private float _flash;

    public QuickSlotView(int index, PlayerInventory inventory, InventoryScreen screen)
    {
        _index = index;
        _inventory = inventory;
        _screen = screen;
        MouseFilter = MouseFilterEnum.Stop;
        CustomMinimumSize = new Vector2(QuickBar.SlotSize, QuickBar.SlotSize);
    }

    public override void _EnterTree() => _screen.RegisterTarget(this);
    public override void _ExitTree() => _screen.UnregisterTarget(this);

    private ItemInstance? Bound => _inventory.Model.Find(_inventory.Model.QuickSlots[_index]);

    public void Flash()
    {
        _flash = 1f;
        SetProcess(true);
        QueueRedraw();
    }

    public override void _Ready() => SetProcess(false);

    public override void _Process(double delta)
    {
        _flash = Mathf.Max(0f, _flash - (float)delta * 4f);
        if (_flash <= 0f)
            SetProcess(false);
        QueueRedraw();
    }

    public override void _Draw()
    {
        var rect = new Rect2(Vector2.Zero, Size);
        DrawRect(rect, new Color(0.04f, 0.045f, 0.05f, 0.82f));

        ItemInstance? item = Bound;
        if (item != null)
        {
            // Spento se l'oggetto non e' a portata di mano (zaino, pouch).
            bool usable = _inventory.Model.IsInQuickAccessArea(item);
            ItemTilePainter.Draw(this, rect.Grow(-2f), item, rotated: false, contain: true,
                alpha: usable ? 1f : 0.35f, showName: false);
        }

        Color border = _flash > 0f
            ? InvTheme.PanelBorder.Lerp(InvTheme.Accent, _flash)
            : InvTheme.PanelBorder;
        DrawRect(rect.Grow(-0.5f), border, filled: false, width: 1f);

        // L'etichetta e' il tasto assegnato, letto dall'InputMap.
        string key = Loc.KeyFor($"quick_slot_{PlayerInventoryModel.QuickSlotLabels[_index]}");
        DrawString(InvTheme.Font, new Vector2(4, 13), key, HorizontalAlignment.Left, -1, 11, InvTheme.TextDim);

        if (_previewDrag != null)
            DrawRect(rect, _preview.Valid ? InvTheme.Valid : InvTheme.Invalid);
    }

    public override void _GuiInput(InputEvent @event)
    {
        // Tasto destro: scollega.
        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } && Bound != null)
        {
            _inventory.SubmitAssignQuickSlot(_index, 0);
            AcceptEvent();
        }
    }

    public override string _GetTooltip(Vector2 atPosition)
    {
        ItemInstance? item = Bound;
        return item != null ? item.Definition.DisplayName : "";
    }

    public DropResolution Resolve(DragState drag, Vector2 globalMouse)
        => new()
        {
            Kind = DropKind.QuickSlot,
            QuickIndex = _index,
            Error = drag.Space == ItemAddress.PlayerSpace && drag.Item.Definition.QuickUsable
                ? InvError.Ok
                : InvError.Incompatible,
        };

    public void SetPreview(DragState? drag, DropResolution resolution)
    {
        if (drag == null && _previewDrag == null)
            return;
        _previewDrag = drag;
        _preview = resolution;
        QueueRedraw();
    }
}
