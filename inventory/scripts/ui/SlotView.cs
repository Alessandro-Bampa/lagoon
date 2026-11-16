using Godot;

namespace Lagoon;

/// <summary>
/// Riquadro di UNO slot: uno slot di equipaggiamento del personaggio oppure uno slot di un oggetto
/// (il pozzetto del caricatore di un'arma). Contiene al massimo un item, disegnato scalato per
/// entrare nel riquadro mantenendo le proporzioni.
///
/// Durante un trascinamento gli slot che accetterebbero l'oggetto si evidenziano; uno slot
/// bloccato da un altro oggetto indossato mostra un tratteggio rosso e ne dice il motivo nel tooltip.
/// </summary>
public partial class SlotView : Control, IDropTarget
{
    private readonly InventoryScreen _screen;
    private readonly ItemInstance? _occupant;
    private readonly Vector2 _cells;
    private readonly string _emptyKey;

    public ItemAddress Address { get; }

    private DragState? _previewDrag;
    private DropResolution _preview;

    /// <param name="cells">Dimensione del riquadro in celle (anche frazionaria).</param>
    /// <param name="emptyKey">Chiave del testo mostrato in filigrana quando lo slot e' vuoto.</param>
    public SlotView(InventoryScreen screen, ItemAddress address, ItemInstance? occupant, Vector2 cells, string emptyKey = "")
    {
        _screen = screen;
        Address = address;
        _occupant = occupant;
        _cells = cells;
        _emptyKey = emptyKey;
        MouseFilter = MouseFilterEnum.Stop;
    }

    public override void _Ready()
    {
        CustomMinimumSize = _cells * InvTheme.Cell;
        SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        SizeFlagsVertical = SizeFlags.ShrinkBegin;
        MouseExited += () => _screen.ClearHovered(this);
    }

    public override void _EnterTree() => _screen.RegisterTarget(this);
    public override void _ExitTree() => _screen.UnregisterTarget(this);

    /// Oggetto indossato che blocca questo slot di equipaggiamento (null se libero o non applicabile).
    private ItemInstance? Blocker
        => Address.IsEquipSlot ? EquipRules.BlockerOf(_screen.Model, Address.EquipSlot) : null;

    public override void _Draw()
    {
        var rect = new Rect2(Vector2.Zero, Size);
        DrawRect(rect, InvTheme.SlotBg);
        DrawRect(rect.Grow(-0.5f), InvTheme.SlotBorder, filled: false, width: 1f);

        DragState? drag = _screen.Drag;

        if (_occupant != null)
        {
            bool dragged = drag != null && ReferenceEquals(drag.Item, _occupant);
            ItemTilePainter.Draw(
                this, rect.Grow(-1f), _occupant, rotated: false, contain: true,
                alpha: dragged ? InventoryConfig.Current.DragSourceAlpha : 1f);
            if (ReferenceEquals(_screen.Selected, _occupant))
                DrawRect(rect.Grow(-1f), new Color(1f, 1f, 1f, 0.55f), filled: false, width: 1f);
        }
        else if (_emptyKey.Length > 0)
        {
            // Filigrana: il nome dello slot, tenue, al centro del riquadro vuoto.
            string text = Loc.T(_emptyKey).ToUpperInvariant();
            int size = InvTheme.LabelSize;
            DrawString(InvTheme.Font, new Vector2(0, Size.Y / 2f + size * 0.35f), text,
                HorizontalAlignment.Center, Size.X, size, InvTheme.TextFaint);
        }

        if (Blocker != null)
        {
            // Slot bloccato: tratteggio rosso in diagonale.
            DrawRect(rect, new Color(InvTheme.Blocked, 0.14f));
            for (float offset = -Size.Y; offset < Size.X; offset += 10f)
                DrawLine(new Vector2(offset, Size.Y), new Vector2(offset + Size.Y, 0), new Color(InvTheme.Blocked, 0.5f), 1f);
            DrawRect(rect.Grow(-0.5f), InvTheme.Blocked, filled: false, width: 1f);
        }

        if (drag == null)
            return;

        if (_previewDrag != null)
        {
            DrawRect(rect, _preview.Valid ? InvTheme.Valid : InvTheme.Invalid);
            return;
        }

        // Tutti gli slot compatibili con l'oggetto in mano si accendono di verde tenue.
        if (Resolve(drag, Vector2.Zero).Valid)
            DrawRect(rect.Grow(-1f), new Color(InvTheme.ValidBorder, 0.6f), filled: false, width: 1f);
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseMotion)
        {
            _screen.SetHovered(this, _occupant, Address.Space);
            return;
        }

        if (@event is not InputEventMouseButton { Pressed: true } click || _occupant == null)
            return;

        _screen.OnItemPressed(_occupant, Address.Space, rotated: false, click.Position / Size, click, fromSlot: true);
        AcceptEvent();
    }

    public override string _GetTooltip(Vector2 atPosition)
    {
        ItemInstance? blocker = Blocker;
        if (blocker != null)
            return Loc.T("UI_INV_SLOT_BLOCKED_BY", blocker.Definition.DisplayName);
        return _occupant != null ? InventoryScreen.TooltipFor(_occupant) : "";
    }

    public DropResolution Resolve(DragState drag, Vector2 globalMouse)
    {
        var result = new DropResolution { Kind = DropKind.None, Error = InvError.Ok };
        if (ReferenceEquals(drag.Item, _occupant))
            return result;

        result.Kind = DropKind.Slot;
        result.Dest = Address;
        // Slot occupato = scambio: l'occupante lascia il posto nello stesso gesto.
        result.Error = InventoryRules.CanPlace(_screen.Spaces, drag.Item, Address, 0, 0, false, leaving: _occupant);
        return result;
    }

    public void SetPreview(DragState? drag, DropResolution resolution)
    {
        if (drag == null && _previewDrag == null)
            return;
        _previewDrag = drag;
        _preview = resolution;
        QueueRedraw();
    }
}
