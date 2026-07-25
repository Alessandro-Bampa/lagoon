using Godot;

namespace Lagoon;

/// <summary>
/// Singolo slot di equipaggiamento indossato. Accetta in drop solo item la cui
/// <see cref="ItemDefinition.EquipSlot"/> combacia, ed e' a sua volta sorgente di trascinamento
/// quando pieno (per togliere l'equipaggiamento portandolo in una griglia).
///
/// Come le griglie, parla per <see cref="ItemAddress"/>: il drop e' uno spostamento verso
/// <see cref="ItemAddress.Equip"/>, quindi le regole restano tutte lato host.
/// Gli slot arma sono presenti ma non funzionali fino alla Fase 3.
/// </summary>
public partial class EquipmentSlotView : Control
{
    // Riquadro fisso (2x2 celle): forma dello slot indipendente dall'ingombro dell'oggetto, che
    // viene scalato per entrarci (vedi ItemVisual.BuildFitted).
    private const int SlotWidth = 2 * GridPanelView.CellSize;
    private const int SlotHeight = 2 * GridPanelView.CellSize;

    private readonly EquipSlotType _slot;
    private readonly ItemInstance? _equipped;
    private readonly InventoryScreen _screen;

    private ItemAddress Address => ItemAddress.Equip(_slot);

    public EquipmentSlotView(EquipSlotType slot, ItemInstance? equipped, InventoryScreen screen)
    {
        _slot = slot;
        _equipped = equipped;
        _screen = screen;
        MouseFilter = MouseFilterEnum.Stop;
    }

    public override void _Ready()
    {
        var size = new Vector2(SlotWidth, SlotHeight);
        CustomMinimumSize = size;
        Size = size;
        TooltipText = _equipped?.Definition.DisplayName ?? SlotLabel(_slot);
        MouseExited += () => _screen.ClearHovered(Address);
        BuildVisual();
    }

    private void BuildVisual()
    {
        if (_equipped != null)
        {
            // Adattato al riquadro dello slot: un oggetto grande (zaino 2x3) non deve debordare.
            AddChild(ItemVisual.BuildFitted(
                _equipped.Definition, new Vector2(SlotWidth, SlotHeight), _equipped.StackCount));
            return;
        }

        var label = new Label
        {
            Text = SlotLabel(_slot),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        label.SetAnchorsPreset(LayoutPreset.FullRect);
        label.AddThemeColorOverride("font_color", new Color(0.55f, 0.55f, 0.6f));
        AddChild(label);
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.14f, 0.14f, 0.17f, 0.95f));
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.35f, 0.35f, 0.4f), filled: false, width: 2f);
    }

    // ---- interazioni ------------------------------------------------------------------

    public override void _GuiInput(InputEvent @event)
    {
        if (_equipped == null)
            return;

        if (@event is InputEventMouseMotion)
        {
            _screen.SetHovered(_equipped, Address);
            return;
        }

        if (@event is not InputEventMouseButton { Pressed: true } click)
            return;

        if (click.ButtonIndex == MouseButton.Right)
        {
            _screen.OpenContextMenu(_equipped, Address, click.GlobalPosition);
            AcceptEvent();
            return;
        }

        if (click.ButtonIndex != MouseButton.Left)
            return;

        if (click.CtrlPressed)
        {
            _screen.QuickMove(_equipped, Address);
            AcceptEvent();
            return;
        }

        if (click.DoubleClick)
        {
            // Contenitori indossati (rig/zaino/contenitore sicuro) si aprono in finestra.
            if (_equipped.ContainerGrid != null)
                _screen.OpenContainerWindow(_equipped, Address);
            else
                _screen.OpenInspectWindow(_equipped);
            AcceptEvent();
        }
    }

    public override Variant _GetDragData(Vector2 atPosition)
    {
        if (_equipped == null)
            return default;

        _screen.OnDragStart(_equipped.Definition, false);
        SetDragPreview(new DragPreview(_screen, _equipped.Definition, _equipped.StackCount));
        return InventoryDrag.Make(_equipped, Address);
    }

    public override bool _CanDropData(Vector2 atPosition, Variant data)
    {
        if (_equipped != null)
            return false; // slot occupato: niente scambio diretto in questa fase
        if (!InventoryDrag.TryRead(data, out InventoryDrag.Payload payload))
            return false;
        return payload.EquipSlot == _slot;
    }

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        if (!InventoryDrag.TryRead(data, out InventoryDrag.Payload payload))
            return;
        _screen.Inventory.SubmitMove(
            payload.From, payload.InstanceId, Address, PlayerInventory.AutoPlace, 0, false);
    }

    public static string SlotLabel(EquipSlotType slot) => slot switch
    {
        EquipSlotType.Head => "Testa",
        EquipSlotType.Torso => "Torso",
        EquipSlotType.Legs => "Gambe",
        EquipSlotType.Feet => "Piedi",
        EquipSlotType.Vest => "Rig",
        EquipSlotType.Backpack => "Zaino",
        EquipSlotType.SecureContainer => "Sicuro",
        EquipSlotType.WeaponPrimary => "Arma 1",
        EquipSlotType.WeaponSecondary => "Arma 2",
        EquipSlotType.Sidearm => "Pistola",
        _ => slot.ToString(),
    };
}
