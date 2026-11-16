using System.Collections.Generic;
using Godot;

namespace Lagoon;

/// <summary>
/// Vista di UNA griglia: celle, tile, anteprima del trascinamento. Tutto in un solo
/// <c>_Draw</c> (nessun nodo per tile): si ridisegna solo quando lo stato o l'anteprima cambiano.
///
/// Serve due casi con lo stesso codice:
///  - una griglia vera di un item (<see cref="ForGrid"/>): il rilascio su celle libere e' uno
///    spostamento in quella cella;
///  - la griglia virtuale "a terra" (<see cref="ForGround"/>): ogni tile e' un oggetto sfuso nel
///    mondo e il rilascio lascia cadere l'oggetto.
///
/// Non muta nulla: traduce i gesti in chiamate a <see cref="InventoryScreen"/>.
/// </summary>
public partial class GridView : Control, IDropTarget
{
    private readonly record struct Entry(ItemInstance Item, int X, int Y, bool Rotated, int Space)
    {
        public int W => Rotated ? Item.Height : Item.Width;
        public int H => Rotated ? Item.Width : Item.Height;
    }

    private readonly InventoryScreen _screen;
    private readonly int _columns;
    private readonly int _rows;
    private readonly List<Entry> _entries = new();
    private readonly int[] _cells; // indice+1 dell'entry che occupa la cella, 0 = libera
    private readonly bool _ground;

    /// Indirizzo della griglia (non usato per la vista "a terra").
    public ItemAddress Address { get; }

    private DragState? _previewDrag;
    private DropResolution _preview;
    private bool _previewRotated;

    private GridView(InventoryScreen screen, int columns, int rows, ItemAddress address, bool ground)
    {
        _screen = screen;
        _columns = columns;
        _rows = rows;
        _cells = new int[columns * rows];
        _ground = ground;
        Address = address;
        MouseFilter = MouseFilterEnum.Stop;
    }

    public static GridView ForGrid(InventoryScreen screen, InventoryGrid grid, int space)
    {
        var view = new GridView(
            screen, grid.Columns, grid.Rows,
            ItemAddress.GridOf(space, grid.Owner?.InstanceId ?? 0, grid.Index), ground: false);
        foreach (ItemInstance item in grid.Items)
            view.AddEntry(new Entry(item, item.GridX, item.GridY, item.Rotated, space));
        return view;
    }

    /// Griglia virtuale degli oggetti sfusi vicini: si impacchettano nell'ordine dato.
    public static GridView ForGround(InventoryScreen screen, int columns, int rows, IEnumerable<ItemPickup> pickups)
    {
        var view = new GridView(screen, columns, rows, ItemAddress.Ground(), ground: true);
        var packer = new InventoryGrid(columns, rows);
        foreach (ItemPickup pickup in pickups)
        {
            ItemInstance? root = pickup.Root;
            if (root == null)
                continue;
            // Si impacchetta l'ingombro, non l'istanza: piazzare la radice vera in una griglia
            // fittizia ne sporcherebbe la collocazione.
            if (packer.TryFindFreeSpot(root.Width, root.Height, out int x, out int y, out bool rotated))
            {
                packer.Place(new ItemInstance(root.InstanceId, root.Definition), x, y, rotated);
                view.AddEntry(new Entry(root, x, y, rotated, pickup.Uid));
            }
        }
        return view;
    }

    private void AddEntry(Entry entry)
    {
        _entries.Add(entry);
        for (int y = entry.Y; y < entry.Y + entry.H && y < _rows; y++)
            for (int x = entry.X; x < entry.X + entry.W && x < _columns; x++)
                _cells[y * _columns + x] = _entries.Count;
    }

    public override void _Ready()
    {
        int cell = InvTheme.Cell;
        CustomMinimumSize = new Vector2(_columns * cell, _rows * cell);
        SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        SizeFlagsVertical = SizeFlags.ShrinkBegin;
        MouseExited += () => _screen.ClearHovered(this);
    }

    public override void _EnterTree() => _screen.RegisterTarget(this);
    public override void _ExitTree() => _screen.UnregisterTarget(this);

    // ====================================================================================
    //  Disegno
    // ====================================================================================

    public override void _Draw()
    {
        int cell = InvTheme.Cell;
        var area = new Rect2(Vector2.Zero, new Vector2(_columns * cell, _rows * cell));
        DrawRect(area, InvTheme.CellBg);

        for (int x = 0; x <= _columns; x++)
            DrawLine(new Vector2(x * cell, 0), new Vector2(x * cell, area.Size.Y), InvTheme.CellLine);
        for (int y = 0; y <= _rows; y++)
            DrawLine(new Vector2(0, y * cell), new Vector2(area.Size.X, y * cell), InvTheme.CellLine);

        DragState? drag = _screen.Drag;
        foreach (Entry entry in _entries)
        {
            // La sorgente del trascinamento resta al suo posto, attenuata.
            bool dragged = drag != null && ReferenceEquals(drag.Item, entry.Item);
            float alpha = dragged ? InventoryConfig.Current.DragSourceAlpha : 1f;
            ItemTilePainter.Draw(this, RectOf(entry).Grow(-1f), entry.Item, entry.Rotated, alpha: alpha);

            if (ReferenceEquals(_screen.Selected, entry.Item))
                DrawRect(RectOf(entry).Grow(-1f), new Color(1f, 1f, 1f, 0.55f), filled: false, width: 1f);
        }

        if (_previewDrag == null)
            return;

        switch (_preview.Kind)
        {
            case DropKind.Cell:
                var foot = new Rect2(
                    _preview.X * cell, _preview.Y * cell, _previewDrag.FootW * cell, _previewDrag.FootH * cell);
                DrawRect(foot.Intersection(area), _preview.Valid ? InvTheme.Valid : InvTheme.Invalid);
                break;

            case DropKind.OnItem:
                foreach (Entry entry in _entries)
                    if (entry.Item.InstanceId == _preview.TargetId)
                        DrawRect(RectOf(entry).Grow(-1f), InvTheme.TargetBorder, filled: false, width: 2f);
                break;

            case DropKind.Ground:
                DrawRect(area, new Color(InvTheme.Valid, 0.12f));
                DrawRect(area.Grow(-1f), InvTheme.ValidBorder, filled: false, width: 1f);
                break;
        }
    }

    private static Rect2 RectOf(Entry entry)
    {
        int cell = InvTheme.Cell;
        return new Rect2(entry.X * cell, entry.Y * cell, entry.W * cell, entry.H * cell);
    }

    private Entry? EntryAt(Vector2 local)
    {
        int cell = InvTheme.Cell;
        int x = Mathf.FloorToInt(local.X / cell);
        int y = Mathf.FloorToInt(local.Y / cell);
        if (x < 0 || y < 0 || x >= _columns || y >= _rows)
            return null;
        int index = _cells[y * _columns + x];
        return index == 0 ? null : _entries[index - 1];
    }

    // ====================================================================================
    //  Mouse
    // ====================================================================================

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseMotion motion)
        {
            Entry? hovered = EntryAt(motion.Position);
            _screen.SetHovered(this, hovered?.Item, hovered?.Space ?? 0);
            return;
        }

        if (@event is not InputEventMouseButton { Pressed: true } click)
            return;

        Entry? hit = EntryAt(click.Position);
        if (hit is not { } entry)
            return;

        Rect2 rect = RectOf(entry);
        Vector2 grab = (click.Position - rect.Position) / rect.Size;
        _screen.OnItemPressed(entry.Item, entry.Space, entry.Rotated, grab, click);
        AcceptEvent();
    }

    public override string _GetTooltip(Vector2 atPosition)
        => EntryAt(atPosition) is { } entry ? InventoryScreen.TooltipFor(entry.Item) : "";

    // ====================================================================================
    //  Bersaglio di rilascio
    // ====================================================================================

    public DropResolution Resolve(DragState drag, Vector2 globalMouse)
    {
        var result = new DropResolution { Kind = DropKind.None, Error = InvError.Ok };

        if (_ground)
        {
            // Un oggetto gia' sfuso a terra non ha dove andare.
            if (drag.Space > 0 && drag.Item.Parent == null)
                return result;
            result.Kind = DropKind.Ground;
            result.Dest = ItemAddress.Ground();
            return result;
        }

        int cell = InvTheme.Cell;
        Vector2 local = globalMouse - GetGlobalRect().Position;
        InvError itemError = InvError.Ok;

        // 1) Rilascio sopra un altro oggetto: unisci, carica, monta, inserisci.
        if (EntryAt(local) is { } under && !ReferenceEquals(under.Item, drag.Item))
        {
            DropAction action = InventoryRules.ResolveDrop(
                _screen.Spaces, drag.Item, under.Item, under.Space, out itemError);
            if (action != DropAction.None)
            {
                result.Kind = DropKind.OnItem;
                result.TargetSpace = under.Space;
                result.TargetId = under.Item.InstanceId;
                return result;
            }
        }

        // 2) Celle: l'angolo in alto a sinistra dell'anteprima, arrotondato alla cella piu' vicina.
        Vector2 grabPx = drag.Grab * new Vector2(drag.FootW * cell, drag.FootH * cell);
        Vector2 corner = (local - grabPx) / cell;
        result.Kind = DropKind.Cell;
        result.Dest = Address;
        result.X = Mathf.FloorToInt(corner.X + 0.5f);
        result.Y = Mathf.FloorToInt(corner.Y + 0.5f);
        result.Error = InventoryRules.CanPlace(_screen.Spaces, drag.Item, Address, result.X, result.Y, drag.Rotated);

        // Se sotto c'era un oggetto che ha rifiutato, e' quello il motivo da mostrare.
        if (result.Error == InvError.NoSpace && itemError != InvError.Ok)
            result.Error = itemError;
        return result;
    }

    public void SetPreview(DragState? drag, DropResolution resolution)
    {
        if (drag == null && _previewDrag == null)
            return;
        if (drag != null && _previewDrag != null && _preview.SameAs(resolution)
            && _previewRotated == drag.Rotated)
            return;

        _previewDrag = drag;
        _previewRotated = drag?.Rotated ?? false;
        _preview = resolution;
        QueueRedraw();
    }
}
