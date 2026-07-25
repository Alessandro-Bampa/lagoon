using System;
using System.Collections.Generic;
using Godot;

namespace Lagoon;

/// <summary>
/// Vista di UNA griglia, ovunque essa si trovi: tasche, rig, zaino, contenitore nel mondo, cassa o
/// finestra pop-up. E' parametrata su un <see cref="ItemAddress"/>, quindi sostituisce da sola le
/// tre viste quasi identiche che esistevano prima (inventario / terreno / contenitore aperto).
///
/// Si occupa solo di presentazione e di tradurre i gesti in richieste all'host:
///  - trascinamento con evidenziazione delle celle di destinazione (verde/rosso) e rotazione live;
///  - doppio click: apre la finestra del contenitore, o la scheda Ispeziona;
///  - tasto destro: menu contestuale;
///  - Ctrl+click (spostamento rapido), Alt+click (equipaggia rapido);
///  - segnala l'item sotto il cursore alla schermata, per Delete e per i tasti rapidi 4-9/0.
/// </summary>
public partial class GridPanelView : Control
{
    public const int CellSize = 48;

    private static readonly Color ValidHighlight = new(0.35f, 0.85f, 0.4f, 0.35f);
    private static readonly Color InvalidHighlight = new(0.9f, 0.3f, 0.3f, 0.35f);

    private readonly InventoryGrid _grid;
    private readonly InventoryScreen _screen;

    /// Indirizzo di QUESTA griglia: destinazione dei drop.
    private readonly ItemAddress _address;

    /// <summary>
    /// Indirizzo di provenienza di un item mostrato qui. Coincide con <see cref="_address"/> in
    /// tutte le griglie vere; nella vista "oggetti a terra" ogni item e' invece un world item a se',
    /// quindi la sorgente e' il singolo pickup.
    /// </summary>
    private readonly Func<ItemInstance, ItemAddress> _sourceOf;

    /// Item apribili con doppio click (contenitori) per InstanceId.
    private readonly HashSet<int> _openable;

    private readonly Dictionary<int, ItemView> _views = new();
    private ItemView? _dragged;

    // Stato evidenziazione durante il drag.
    private bool _showHighlight;
    private int _hlX, _hlY, _hlW, _hlH;
    private bool _hlValid;

    public GridPanelView(
        InventoryScreen screen,
        InventoryGrid grid,
        ItemAddress address,
        Func<ItemInstance, ItemAddress>? sourceOf = null,
        HashSet<int>? openable = null)
    {
        _screen = screen;
        _grid = grid;
        _address = address;
        _sourceOf = sourceOf ?? (_ => address);
        _openable = openable ?? new HashSet<int>();
        MouseFilter = MouseFilterEnum.Stop;
    }

    public override void _Ready()
    {
        // Solo CustomMinimumSize: dentro un container e' il layout a decidere Size, e scriverla a
        // mano insieme agli anchor produce offset incoerenti (vedi nota in ItemVisual).
        // ShrinkBegin evita che la griglia venga stirata oltre le proprie celle.
        CustomMinimumSize = new Vector2(_grid.Columns * CellSize, _grid.Rows * CellSize);
        SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        SizeFlagsVertical = SizeFlags.ShrinkBegin;
        SetProcess(true);
        MouseExited += () => _screen.ClearHovered(_address);

        foreach (var item in _grid.Items)
        {
            var view = new ItemView(item);
            AddChild(view);
            view.Position = new Vector2(item.GridX * CellSize, item.GridY * CellSize);
            _views[item.InstanceId] = view;
        }
    }

    public override void _Notification(int what)
    {
        // A fine drag (rilascio valido o annullato) ripristina l'opacita' dell'item trascinato.
        if (what == NotificationDragEnd && _dragged != null)
        {
            _dragged.Modulate = Colors.White;
            _dragged = null;
        }
    }

    // ---- evidenziazione celle durante il drag -----------------------------------------

    public override void _Process(double delta)
    {
        Viewport viewport = GetViewport();
        if (!viewport.GuiIsDragging())
        {
            ClearHighlight();
            return;
        }

        Vector2 local = GetLocalMousePosition();
        if (local.X < 0 || local.Y < 0 || local.X >= Size.X || local.Y >= Size.Y)
        {
            ClearHighlight();
            return;
        }

        Variant drag = viewport.GuiGetDragData();
        if (!InventoryDrag.TryRead(drag, out InventoryDrag.Payload payload))
        {
            ClearHighlight();
            return;
        }

        var (w, h) = _screen.DraggedFootprint();
        if (w == 0)
        {
            ClearHighlight();
            return;
        }

        // Un item che arriva da un'altra griglia non occupa gia' celle qui: nulla da ignorare.
        int ignore = payload.From.Equals(_address) ? payload.InstanceId : 0;
        var (x, y) = CellAt(local);
        SetHighlight(x, y, w, h, _grid.CanPlaceSize(w, h, x, y, ignoreInstanceId: ignore));
    }

    private void SetHighlight(int x, int y, int w, int h, bool valid)
    {
        if (_showHighlight && _hlX == x && _hlY == y && _hlW == w && _hlH == h && _hlValid == valid)
            return; // nessun cambiamento: evita redraw inutili
        _showHighlight = true;
        _hlX = x; _hlY = y; _hlW = w; _hlH = h; _hlValid = valid;
        QueueRedraw();
    }

    private void ClearHighlight()
    {
        if (!_showHighlight)
            return;
        _showHighlight = false;
        QueueRedraw();
    }

    // ---- disegno -----------------------------------------------------------------------

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.11f, 0.11f, 0.13f, 0.95f));

        if (_showHighlight)
        {
            var pos = new Vector2(_hlX * CellSize, _hlY * CellSize);
            var area = new Vector2(_hlW * CellSize, _hlH * CellSize);
            DrawRect(new Rect2(pos, area), _hlValid ? ValidHighlight : InvalidHighlight);
        }

        var lines = new Color(0.32f, 0.32f, 0.36f);
        for (int x = 0; x <= _grid.Columns; x++)
            DrawLine(new Vector2(x * CellSize, 0), new Vector2(x * CellSize, _grid.Rows * CellSize), lines);
        for (int y = 0; y <= _grid.Rows; y++)
            DrawLine(new Vector2(0, y * CellSize), new Vector2(_grid.Columns * CellSize, y * CellSize), lines);
    }

    private (int x, int y) CellAt(Vector2 position)
    {
        int x = Mathf.Clamp((int)(position.X / CellSize), 0, Mathf.Max(0, _grid.Columns - 1));
        int y = Mathf.Clamp((int)(position.Y / CellSize), 0, Mathf.Max(0, _grid.Rows - 1));
        return (x, y);
    }

    // ---- mouse: hover, doppio click, menu contestuale, scorciatoie ----------------------

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseMotion motion)
        {
            var (hx, hy) = CellAt(motion.Position);
            ItemInstance? hovered = _grid.ItemAt(hx, hy);
            _screen.SetHovered(hovered, hovered != null ? _sourceOf(hovered) : default);
            return;
        }

        if (@event is not InputEventMouseButton { Pressed: true } click)
            return;

        var (x, y) = CellAt(click.Position);
        ItemInstance? item = _grid.ItemAt(x, y);
        if (item == null)
            return;

        ItemAddress source = _sourceOf(item);

        if (click.ButtonIndex == MouseButton.Right)
        {
            _screen.OpenContextMenu(item, source, click.GlobalPosition);
            AcceptEvent();
            return;
        }

        if (click.ButtonIndex != MouseButton.Left)
            return;

        // Ctrl = spostamento rapido verso il lato opposto; Alt = equipaggia rapido.
        if (click.CtrlPressed)
        {
            _screen.QuickMove(item, source);
            AcceptEvent();
            return;
        }
        if (click.AltPressed)
        {
            _screen.QuickEquip(item, source);
            AcceptEvent();
            return;
        }

        if (click.DoubleClick)
        {
            if (_openable.Contains(item.InstanceId) && item.ContainerGrid != null)
                _screen.OpenContainerWindow(item, source);
            else
                _screen.OpenInspectWindow(item);
            AcceptEvent();
        }
    }


    // ---- drag & drop -------------------------------------------------------------------

    public override Variant _GetDragData(Vector2 atPosition)
    {
        var (x, y) = CellAt(atPosition);
        ItemInstance? item = _grid.ItemAt(x, y);
        if (item == null)
            return default;

        // La rotazione "in sospeso" parte dall'orientamento dell'item; R la inverte durante il drag.
        _screen.OnDragStart(item.Definition, item.Rotated);
        SetDragPreview(new DragPreview(_screen, item.Definition, item.StackCount));

        // Affievolisce l'item originale finche' e' "in mano" (ripristinato a fine drag).
        _dragged = _views.GetValueOrDefault(item.InstanceId);
        if (_dragged != null)
            _dragged.Modulate = new Color(1f, 1f, 1f, 0.3f);

        return InventoryDrag.Make(item, _sourceOf(item));
    }

    public override bool _CanDropData(Vector2 atPosition, Variant data)
    {
        if (!InventoryDrag.TryRead(data, out InventoryDrag.Payload payload))
            return false;

        var (w, h) = _screen.DraggedFootprint();
        if (w == 0)
            return false;

        int ignore = payload.From.Equals(_address) ? payload.InstanceId : 0;
        var (x, y) = CellAt(atPosition);
        return _grid.CanPlaceSize(w, h, x, y, ignoreInstanceId: ignore);
    }

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        if (!InventoryDrag.TryRead(data, out InventoryDrag.Payload payload))
            return;

        var (x, y) = CellAt(atPosition);
        _screen.Inventory.SubmitMove(payload.From, payload.InstanceId, _address, x, y, _screen.PendingRotated);
    }
}
