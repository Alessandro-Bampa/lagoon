using System.Collections.Generic;
using Godot;

namespace Lagoon;

/// <summary>
/// Schermata inventario in stile Tarkov, a tre colonne:
///  1. EQUIPAGGIAMENTO — gli slot indossati e le armi, con l'indicatore di peso;
///  2. CONTENITORI INDOSSATI — rig, tasche + slot speciali, zaino, pouch, ognuno col layout delle
///     proprie sotto-griglie;
///  3. BOTTINO — il contenitore del mondo aperto con F, oppure gli oggetti sfusi a terra vicini.
/// Sopra, un layer di finestre trascinabili (contenitori, dettaglio slot, Ispeziona, dialoghi).
///
/// E' anche il CONTROLLORE DEL TRASCINAMENTO: non usa il drag &amp; drop integrato di Godot perche'
/// servono la rotazione durante il gesto, Ctrl al rilascio (dividi) e i bersagli "oggetto" con
/// priorita'. Le viste si registrano come <see cref="IDropTarget"/> e qui si decide chi sta sotto
/// il cursore.
///
/// Non muta mai lo stato: legge il modello replicato ed emette richieste verso l'host. L'anteprima
/// verde/rossa usa le STESSE regole dell'host (<see cref="InventoryRules"/>).
/// </summary>
public partial class InventoryScreen : Control
{
    public PlayerInventory Inventory { get; }
    public PlayerInventoryModel Model => Inventory.Model;
    public InventorySpaces Spaces => Inventory.Spaces;

    /// Trascinamento in corso (null se nessuno). Le viste lo leggono per attenuare la sorgente.
    public DragState? Drag { get; private set; }

    /// Ultima tile cliccata: evidenziata, e bersaglio di Canc e dei tasti rapidi se non c'e' hover.
    public ItemInstance? Selected { get; private set; }

    private const int Margin = 20;
    private const int ColumnGap = 14;

    private VBoxContainer _gear = null!;
    private VBoxContainer _containers = null!;
    private VBoxContainer _source = null!;
    private Label _sourceHeader = null!;
    private HBoxContainer _sourceButtons = null!;
    private Label _hints = null!;
    private Control _windowLayer = null!;
    private Label _toast = null!;
    private ItemPreviewView _dragPreview = null!;
    private WeightIndicator? _weight;

    private readonly List<IDropTarget> _targets = new();

    /// Contenitore nel mondo aperto nella colonna destra (0 = oggetti a terra).
    private int _openWorldUid;

    // Item sotto il cursore (per Canc e per i tasti rapidi).
    private Control? _hoverView;
    private ItemInstance? _hovered;
    private int _hoveredSpace;

    // Pressione in attesa di diventare click o trascinamento.
    private ItemInstance? _pressItem;
    private int _pressSpace;
    private bool _pressRotated;
    private Vector2 _pressGrab;
    private Vector2 _pressPosition;

    private IDropTarget? _dropTarget;
    private DropResolution _resolution;

    private float _sinceSourceCheck;
    private ulong _sourceSignature;
    private float _toastRemaining;
    private InvError _lastError;
    private ulong _lastErrorMsec;
    private bool _built;

    public InventoryScreen(PlayerInventory inventory)
    {
        Inventory = inventory;
    }

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop; // blocca l'input di gioco quando aperto
        BuildChrome();
        _built = true;
        Inventory.OperationFailed += OnOperationFailed;
        Relayout();
    }

    public override void _ExitTree()
    {
        Inventory.OperationFailed -= OnOperationFailed;
    }

    // ====================================================================================
    //  Struttura fissa
    // ====================================================================================

    private void BuildChrome()
    {
        var backdrop = new ColorRect { Color = InvTheme.Backdrop, MouseFilter = MouseFilterEnum.Ignore };
        backdrop.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(backdrop);

        var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", Margin);
        margin.AddThemeConstantOverride("margin_top", Margin - 6);
        margin.AddThemeConstantOverride("margin_right", Margin);
        // Lascia libera la fascia della barra rapida.
        margin.AddThemeConstantOverride("margin_bottom", QuickBar.SlotSize + 24);
        AddChild(margin);

        var layout = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        layout.AddThemeConstantOverride("separation", 8);
        margin.AddChild(layout);

        // Barra superiore: titolo e promemoria dei comandi.
        var top = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        var title = new Label { Text = "UI_INV_TITLE", Uppercase = true };
        title.AddThemeFontSizeOverride("font_size", 17);
        title.AddThemeColorOverride("font_color", InvTheme.Accent);
        top.AddChild(title);
        top.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore });
        _hints = InvTheme.Plain("", InvTheme.TextDim, 11);
        _hints.VerticalAlignment = VerticalAlignment.Center;
        top.AddChild(_hints);
        layout.AddChild(top);

        var rule = new ColorRect
        {
            Color = new Color(InvTheme.Accent, 0.35f),
            CustomMinimumSize = new Vector2(0, 1),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        layout.AddChild(rule);

        var columns = new HBoxContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        columns.AddThemeConstantOverride("separation", ColumnGap);
        layout.AddChild(columns);

        // Le prime due colonne restano agganciate a sinistra, il bottino a destra: lo spazio che
        // avanza sta in mezzo, cosi' nulla "scivola" al variare della risoluzione.
        _gear = AddColumn(columns, "UI_INV_COL_GEAR", out _, out _);
        _containers = AddColumn(columns, "UI_INV_COL_STORAGE", out _, out _);
        columns.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore });
        _source = AddColumn(columns, "UI_INV_COL_GROUND", out _sourceHeader, out _sourceButtons);

        _windowLayer = new Control { MouseFilter = MouseFilterEnum.Ignore };
        _windowLayer.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_windowLayer);

        // Notifica degli errori: in basso al centro, sopra la barra rapida.
        _toast = InvTheme.Plain("", InvTheme.WeightBad, 15);
        _toast.HorizontalAlignment = HorizontalAlignment.Center;
        _toast.SetAnchorsPreset(LayoutPreset.BottomWide);
        _toast.OffsetTop = -(QuickBar.SlotSize + 56);
        _toast.OffsetBottom = -(QuickBar.SlotSize + 30);
        _toast.Visible = false;
        AddChild(_toast);

        _dragPreview = new ItemPreviewView { Visible = false, Alpha = 0.9f, ZIndex = 50 };
        AddChild(_dragPreview);

        UpdateHints();
    }

    /// Colonna: pannello con intestazione e contenuto scorrevole in verticale.
    private static VBoxContainer AddColumn(
        HBoxContainer parent, string titleKey, out Label header, out HBoxContainer buttons)
    {
        var panel = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        panel.AddThemeStyleboxOverride("panel", InvTheme.Box(InvTheme.PanelBg, InvTheme.PanelBorder, 10));
        parent.AddChild(panel);

        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", 8);
        panel.AddChild(outer);

        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 6);
        header = InvTheme.Header(titleKey);
        header.AddThemeColorOverride("font_color", InvTheme.Text);
        header.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.VerticalAlignment = VerticalAlignment.Center;
        head.AddChild(header);
        buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 4);
        head.AddChild(buttons);
        outer.AddChild(head);

        // Scorrimento orizzontale disattivato: la colonna prende la larghezza del proprio contenuto.
        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        outer.AddChild(scroll);

        var content = new VBoxContainer();
        content.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(content);
        return content;
    }

    private void UpdateHints()
    {
        // I tasti si leggono dall'InputMap, non si scrivono nel testo (skill ui-hud §5).
        _hints.Text = Loc.T("UI_INV_HINTS", Loc.KeyFor("rotate_item"), Loc.KeyFor("quick_drop"));
    }

    /// <summary>
    /// Sceglie il lato della cella in base allo spazio disponibile: le tre colonne occupano circa
    /// 27 celle in larghezza e 12.5 in altezza, piu' le parti fisse (margini, intestazioni).
    /// </summary>
    private void Relayout()
    {
        InventoryConfig cfg = InventoryConfig.Current;
        Vector2 size = Size.X > 0 ? Size : GetViewportRect().Size;
        float byWidth = (size.X - 190f) / 27f;
        float byHeight = (size.Y - 290f) / 12.5f;
        int cell = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(byWidth, byHeight)), cfg.MinCellPx, cfg.CellPx);

        InvTheme.Cell = cell;
        Rebuild();
    }

    public override void _Notification(int what)
    {
        if (!_built)
            return;

        if (what == NotificationResized)
        {
            Relayout();
        }
        else if (what == NotificationTranslationChanged)
        {
            // I testi composti non si riaggiornano da soli: si rigenera tutto dallo stato.
            UpdateHints();
            Rebuild();
        }
    }

    // ====================================================================================
    //  Ricostruzione dallo stato replicato
    // ====================================================================================

    public void Rebuild()
    {
        if (!_built)
            return;

        RebuildGear();
        RebuildContainers();
        RebuildSource();
        RefreshWindows();
        Revalidate();
    }

    /// Dopo un sync le istanze possono essere state ricostruite: i riferimenti tenuti dalla UI si
    /// ri-risolvono per id, e cio' che non esiste piu' si lascia andare.
    private void Revalidate()
    {
        _hovered = null;
        _hoverView = null;

        if (Selected != null)
        {
            int space = 0;
            Selected = FindAnywhere(Selected.InstanceId, ref space);
        }

        if (_pressItem != null)
        {
            _pressItem = FindAnywhere(_pressItem.InstanceId, ref _pressSpace);
        }

        if (Drag != null)
        {
            int space = Drag.Space;
            ItemInstance? item = FindAnywhere(Drag.Item.InstanceId, ref space);
            if (item == null)
                CancelDrag();
            else if (!ReferenceEquals(item, Drag.Item) || space != Drag.Space)
            {
                Drag = new DragState(item, space, Drag.Rotated, Drag.Grab);
                _dragPreview.Item = item;
            }
        }
    }

    private void RefreshWindows()
    {
        foreach (Node child in _windowLayer.GetChildren())
            if (child is ContainerWindow window && !window.IsQueuedForDeletion())
                window.Refresh();
    }

    // --- colonna 1: equipaggiamento -------------------------------------------------------

    private void RebuildGear()
    {
        Clear(_gear);
        int cell = InvTheme.Cell;
        int gap = InvTheme.Gap;
        float wide = (6f * cell + 2f * gap) / cell; // larghezza di tre slot quadrati affiancati

        _gear.AddChild(SlotRow(gap,
            (EquipSlotType.Earpiece, new Vector2(2, 2)),
            (EquipSlotType.Headwear, new Vector2(2, 2)),
            (EquipSlotType.FaceCover, new Vector2(2, 2))));
        _gear.AddChild(SlotRow(gap,
            (EquipSlotType.Armband, new Vector2(2, 2)),
            (EquipSlotType.BodyArmor, new Vector2(2, 2)),
            (EquipSlotType.Eyewear, new Vector2(2, 2))));
        _gear.AddChild(SlotBox(EquipSlotType.OnSling, new Vector2(wide, 2)));
        _gear.AddChild(SlotBox(EquipSlotType.OnBack, new Vector2(wide, 2)));

        float holster = (wide * cell - gap) * 0.58f / cell;
        float scabbard = (wide * cell - gap) * 0.42f / cell;
        _gear.AddChild(SlotRow(gap,
            (EquipSlotType.Holster, new Vector2(holster, 2)),
            (EquipSlotType.Scabbard, new Vector2(scabbard, 2))));

        _weight = new WeightIndicator();
        _weight.SetWeight(Model.TotalWeight());
        _gear.AddChild(_weight);
    }

    private HBoxContainer SlotRow(int gap, params (EquipSlotType Slot, Vector2 Cells)[] slots)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", gap);
        foreach (var (slot, cells) in slots)
            row.AddChild(SlotBox(slot, cells));
        return row;
    }

    /// Etichetta in maiuscolo sopra il riquadro dello slot.
    private VBoxContainer SlotBox(EquipSlotType slot, Vector2 cells)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 2);
        box.AddChild(InvTheme.Header(SlotLabelKey(slot)));
        box.AddChild(new SlotView(this, ItemAddress.Equip(slot), Model.Get(slot), cells));
        return box;
    }

    /// Chiave derivata dal nome dell'enum: <c>OnSling</c> -> <c>SLOT_ONSLING</c>.
    public static string SlotLabelKey(EquipSlotType slot) => $"SLOT_{slot.ToString().ToUpperInvariant()}";

    // --- colonna 2: contenitori indossati -------------------------------------------------

    private void RebuildContainers()
    {
        Clear(_containers);
        _containers.AddChild(ContainerSection(EquipSlotType.TacticalRig));
        _containers.AddChild(PocketsSection());
        _containers.AddChild(ContainerSection(EquipSlotType.Backpack));
        _containers.AddChild(ContainerSection(EquipSlotType.SecureContainer));
    }

    /// Sezione di un contenitore indossato: a sinistra lo slot, a destra il layout delle griglie.
    private VBoxContainer ContainerSection(EquipSlotType slot)
    {
        ItemInstance? worn = Model.Get(slot);

        var section = new VBoxContainer();
        section.AddThemeConstantOverride("separation", 2);

        var head = new HBoxContainer();
        Label label = InvTheme.Header(SlotLabelKey(slot));
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(label);
        if (worn is { IsContainer: true })
        {
            if (worn.TagName.Length > 0)
                head.AddChild(InvTheme.Plain(worn.TagName, InvTheme.TagColor(worn.TagColor), InvTheme.HeaderSize));
            head.AddChild(SortButton(worn.InstanceId, ItemAddress.PlayerSpace));
        }
        section.AddChild(head);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", InvTheme.Gap);
        row.AddChild(new SlotView(this, ItemAddress.Equip(slot), worn, new Vector2(2, 2)));
        if (worn is { IsContainer: true })
            row.AddChild(new ContainerLayoutView(this, worn, ItemAddress.PlayerSpace));
        section.AddChild(row);
        return section;
    }

    /// Tasche (quattro griglie 1x1 separate) e, accanto, i tre slot speciali.
    private HBoxContainer PocketsSection()
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", InvTheme.Cell);

        var pocketsBox = new VBoxContainer();
        pocketsBox.AddThemeConstantOverride("separation", 2);
        pocketsBox.AddChild(InvTheme.Header(SlotLabelKey(EquipSlotType.Pockets)));
        ItemInstance? pockets = Model.Get(EquipSlotType.Pockets);
        if (pockets != null)
            pocketsBox.AddChild(new ContainerLayoutView(this, pockets, ItemAddress.PlayerSpace));
        row.AddChild(pocketsBox);

        var specialBox = new VBoxContainer();
        specialBox.AddThemeConstantOverride("separation", 2);
        specialBox.AddChild(InvTheme.Header("UI_INV_SPECIAL_SLOTS"));
        var specials = new HBoxContainer();
        specials.AddThemeConstantOverride("separation", InvTheme.Gap / 2);
        foreach (EquipSlotType slot in new[] { EquipSlotType.Special1, EquipSlotType.Special2, EquipSlotType.Special3 })
            specials.AddChild(new SlotView(this, ItemAddress.Equip(slot), Model.Get(slot), Vector2.One));
        specialBox.AddChild(specials);
        row.AddChild(specialBox);

        return row;
    }

    private Button SortButton(int containerId, int space)
    {
        Button sort = InvTheme.FlatButton("UI_INV_SORT");
        sort.TooltipText = Loc.T("UI_INV_SORT_TOOLTIP");
        sort.Pressed += () =>
        {
            int s = space;
            ItemInstance? container = FindAnywhere(containerId, ref s);
            if (container != null)
                SortContainer(container, s);
        };
        return sort;
    }

    // --- colonna 3: bottino / a terra -----------------------------------------------------

    private void RebuildSource()
    {
        Clear(_source);
        Clear(_sourceButtons);
        _sourceSignature = ComputeSourceSignature();

        ItemInstance? root = _openWorldUid != 0 ? Spaces.WorldRoot(_openWorldUid) : null;
        if (root is { IsContainer: true })
        {
            // Nome dell'oggetto: testo gia' tradotto, niente auto-translate.
            _sourceHeader.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
            _sourceHeader.Text = root.TagName.Length > 0
                ? Loc.T("UI_INV_WINDOW_TAGGED", root.Definition.DisplayName, root.TagName)
                : root.Definition.DisplayName;

            _sourceButtons.AddChild(SortButton(root.InstanceId, _openWorldUid));
            Button back = InvTheme.FlatButton("UI_INV_BACK_TO_GROUND");
            back.Pressed += CloseWorldContainer;
            _sourceButtons.AddChild(back);

            _source.AddChild(new ContainerLayoutView(this, root, _openWorldUid));
            return;
        }

        _openWorldUid = 0;
        _sourceHeader.AutoTranslateMode = AutoTranslateModeEnum.Inherit;
        _sourceHeader.Text = "UI_INV_COL_GROUND";

        int columns = InventoryConfig.Current.GroundColumns;
        _source.AddChild(GridView.ForGround(this, columns, GroundRows(), NearbyLooseItems()));
    }

    /// Righe della griglia "a terra": quante ne entrano nell'altezza utile della colonna.
    private int GroundRows()
    {
        float available = (Size.Y > 0 ? Size.Y : GetViewportRect().Size.Y) - 190f;
        return Mathf.Max(6, Mathf.FloorToInt(available / InvTheme.Cell));
    }

    private IEnumerable<ItemPickup> NearbyLooseItems()
    {
        foreach (Node node in GetTree().GetNodesInGroup(ItemPickup.GroupName))
            if (node is ItemPickup pickup && Spaces.IsReachable(pickup))
                yield return pickup;
    }

    /// Impronta degli oggetti vicini: cambia se uno entra/esce dal raggio o viene sostituito
    /// (l'host rimpiazza il nodo quando il contenuto cambia, quindi cambia l'instance id).
    private ulong ComputeSourceSignature()
    {
        ulong signature = 1469598103934665603UL;
        foreach (ItemPickup pickup in NearbyLooseItems())
            signature = (signature ^ pickup.GetInstanceId()) * 1099511628211UL;
        return signature;
    }

    public override void _Process(double delta)
    {
        if (!IsVisibleInTree())
            return;

        if (_toastRemaining > 0f)
        {
            _toastRemaining -= (float)delta;
            _toast.Modulate = new Color(1f, 1f, 1f, Mathf.Clamp(_toastRemaining * 3f, 0f, 1f));
            if (_toastRemaining <= 0f)
                _toast.Visible = false;
        }

        // Il mondo vicino cambia col movimento e con le azioni degli altri giocatori.
        _sinceSourceCheck += (float)delta;
        if (_sinceSourceCheck < 0.3f || Drag != null)
            return;
        _sinceSourceCheck = 0f;

        if (ComputeSourceSignature() != _sourceSignature)
        {
            RebuildSource();
            RefreshWindows();
            Revalidate();
        }
    }

    private static void Clear(Node parent)
    {
        foreach (Node child in parent.GetChildren())
        {
            parent.RemoveChild(child);
            child.QueueFree();
        }
    }

    // ====================================================================================
    //  Ricerca di un'istanza ovunque sia raggiungibile
    // ====================================================================================

    /// <summary>
    /// Istanza per id: prima nel proprio inventario, poi nello spazio indicato, infine fra gli
    /// oggetti vicini (un contenitore puo' essere passato dall'inventario al mondo o viceversa).
    /// Aggiorna <paramref name="space"/> con lo spazio in cui e' stata trovata.
    /// </summary>
    public ItemInstance? FindAnywhere(int instanceId, ref int space)
    {
        ItemInstance? item = Model.Find(instanceId);
        if (item != null)
        {
            space = ItemAddress.PlayerSpace;
            return item;
        }

        if (space > 0)
        {
            item = Spaces.Find(space, instanceId);
            if (item != null)
                return item;
        }

        foreach (ItemPickup pickup in NearbyLooseItems())
        {
            ItemInstance? root = pickup.Root;
            item = root != null ? ItemTree.Find(root, instanceId) : null;
            if (item != null)
            {
                space = pickup.Uid;
                return item;
            }
        }
        return null;
    }

    // ====================================================================================
    //  Bersagli di rilascio e hover
    // ====================================================================================

    public void RegisterTarget(IDropTarget target) => _targets.Add(target);

    public void UnregisterTarget(IDropTarget target)
    {
        _targets.Remove(target);
        if (ReferenceEquals(_dropTarget, target))
            _dropTarget = null;
    }

    private void RedrawTargets()
    {
        foreach (IDropTarget target in _targets)
            target.QueueRedraw();
    }

    public void SetHovered(Control view, ItemInstance? item, int space)
    {
        _hoverView = view;
        _hovered = item;
        _hoveredSpace = space;
    }

    public void ClearHovered(Control view)
    {
        if (ReferenceEquals(_hoverView, view))
        {
            _hoverView = null;
            _hovered = null;
        }
    }

    /// Bersaglio sotto il cursore. Una finestra copre cio' che le sta dietro: se il cursore e' su
    /// una finestra valgono solo i bersagli al suo interno.
    private IDropTarget? TargetAt(Vector2 globalMouse)
    {
        FloatingWindow? window = null;
        for (int i = _windowLayer.GetChildCount() - 1; i >= 0; i--)
        {
            if (_windowLayer.GetChild(i) is FloatingWindow candidate && !candidate.IsQueuedForDeletion()
                && candidate.GetGlobalRect().HasPoint(globalMouse))
            {
                window = candidate;
                break;
            }
        }

        foreach (IDropTarget target in _targets)
        {
            if (target is not Control control || !target.IsVisibleInTree())
                continue;
            if (!target.GetGlobalRect().HasPoint(globalMouse))
                continue;

            bool inWindow = _windowLayer.IsAncestorOf(control);
            if (window != null ? window.IsAncestorOf(control) : !inWindow)
                return target;
        }
        return null;
    }

    // ====================================================================================
    //  Click e trascinamento
    // ====================================================================================

    /// Chiamata dalle viste alla pressione di un tasto del mouse su una tile.
    public void OnItemPressed(
        ItemInstance item, int space, bool rotated, Vector2 grab, InputEventMouseButton click,
        bool fromSlot = false)
    {
        Selected = item;
        RedrawTargets();

        switch (click.ButtonIndex)
        {
            case MouseButton.Right:
                OpenContextMenu(item, space);
                return;

            case MouseButton.Middle:
                OpenInspectWindow(item);
                return;

            case MouseButton.Left:
                break;

            default:
                return;
        }

        if (click.CtrlPressed)
        {
            Inventory.SubmitQuickMove(space, item.InstanceId, _openWorldUid);
            return;
        }
        if (click.AltPressed)
        {
            Inventory.SubmitQuickEquip(space, item.InstanceId);
            return;
        }
        if (click.DoubleClick)
        {
            _pressItem = null;
            OpenItem(item, space);
            return;
        }

        if (InventoryRules.CanTake(Spaces, space, item) != InvError.Ok)
            return;

        _pressItem = item;
        _pressSpace = space;
        // In uno slot l'icona e' scalata: si afferra dal centro, con l'orientamento base.
        _pressRotated = !fromSlot && rotated;
        _pressGrab = fromSlot ? new Vector2(0.5f, 0.5f) : grab.Clamp(Vector2.Zero, Vector2.One);
        _pressPosition = GetGlobalMousePosition();
    }

    // _Input e non _GuiInput: durante il trascinamento serve il mouse ovunque sia, anche sopra
    // viste che non sono quella di partenza. Le posizioni si leggono da GetGlobalMousePosition,
    // gia' in coordinate logiche (segue la scala UI).
    public override void _Input(InputEvent @event)
    {
        if (!IsVisibleInTree() || _pressItem == null)
            return;

        if (@event is InputEventMouseMotion)
        {
            Vector2 mouse = GetGlobalMousePosition();
            if (Drag == null)
            {
                if (mouse.DistanceTo(_pressPosition) < InventoryConfig.Current.DragThresholdPx)
                    return;
                BeginDrag();
            }
            UpdateDrag(mouse);
            return;
        }

        if (@event is not InputEventMouseButton button)
            return;

        if (button.ButtonIndex == MouseButton.Left && !button.Pressed)
        {
            if (Drag != null)
            {
                CompleteDrag(button.CtrlPressed);
                GetViewport().SetInputAsHandled();
            }
            _pressItem = null;
        }
        else if (button.ButtonIndex == MouseButton.Right && button.Pressed && Drag != null)
        {
            CancelDrag();
            GetViewport().SetInputAsHandled();
        }
    }

    private void BeginDrag()
    {
        if (_pressItem == null)
            return;

        Drag = new DragState(_pressItem, _pressSpace, _pressRotated, _pressGrab);
        _dragPreview.Item = Drag.Item;
        _dragPreview.Visible = true;
        SyncPreviewSize();
        RedrawTargets();
    }

    private void SyncPreviewSize()
    {
        if (Drag == null)
            return;
        _dragPreview.Rotated = Drag.Rotated;
        _dragPreview.Size = new Vector2(Drag.FootW, Drag.FootH) * InvTheme.Cell;
        _dragPreview.QueueRedraw();
    }

    private void UpdateDrag(Vector2 mouse)
    {
        if (Drag == null)
            return;

        _dragPreview.GlobalPosition = mouse - Drag.Grab * _dragPreview.Size;

        IDropTarget? target = TargetAt(mouse);
        DropResolution resolution = target?.Resolve(Drag, mouse)
            ?? new DropResolution { Kind = DropKind.None };

        if (!ReferenceEquals(target, _dropTarget))
            _dropTarget?.SetPreview(null, default);
        _dropTarget = target;
        _dropTarget?.SetPreview(Drag, resolution);
        _resolution = resolution;
    }

    /// R durante il trascinamento: scambia l'ingombro tenendo l'oggetto sotto il cursore.
    public void ToggleRotation()
    {
        if (Drag == null || Drag.Item.Width == Drag.Item.Height)
            return;

        Drag.Rotated = !Drag.Rotated;
        Drag.Grab = new Vector2(Drag.Grab.Y, Drag.Grab.X);
        SyncPreviewSize();
        UpdateDrag(GetGlobalMousePosition());
    }

    public void CancelDrag()
    {
        _dropTarget?.SetPreview(null, default);
        _dropTarget = null;
        Drag = null;
        _pressItem = null;
        _dragPreview.Visible = false;
        _dragPreview.Item = null;
        RedrawTargets();
    }

    private void CompleteDrag(bool splitModifier)
    {
        DragState? drag = Drag;
        DropResolution resolution = _resolution;
        CancelDrag();
        if (drag == null)
            return;

        if (!resolution.Valid)
        {
            // Rilascio non valido: l'oggetto resta dov'era; si dice il perche' solo se c'e' un motivo.
            if (resolution.Kind != DropKind.None)
                ShowError(resolution.Error);
            return;
        }

        int id = drag.Item.InstanceId;
        switch (resolution.Kind)
        {
            case DropKind.Cell:
                if (splitModifier && drag.Item.StackCount > 1)
                {
                    bool rotated = drag.Rotated;
                    int space = drag.Space;
                    OpenSplitDialog(drag.Item, amount => Inventory.SubmitMove(
                        space, id, resolution.Dest, resolution.X, resolution.Y, rotated, amount));
                }
                else
                {
                    Inventory.SubmitMove(drag.Space, id, resolution.Dest, resolution.X, resolution.Y, drag.Rotated);
                }
                break;

            case DropKind.OnItem:
                Inventory.SubmitDropOnItem(drag.Space, id, resolution.TargetSpace, resolution.TargetId);
                break;

            case DropKind.Slot:
                Inventory.SubmitMove(drag.Space, id, resolution.Dest, 0, 0, false);
                break;

            case DropKind.QuickSlot:
                Inventory.SubmitAssignQuickSlot(resolution.QuickIndex, id);
                break;

            case DropKind.Ground:
                Discard(drag.Item, drag.Space);
                break;
        }
    }

    // ====================================================================================
    //  Scorciatoie (chiamate da PlayerHud)
    // ====================================================================================

    private (ItemInstance? Item, int Space) Focused()
    {
        if (_hovered != null)
            return (_hovered, _hoveredSpace);
        if (Selected != null)
        {
            int space = 0;
            ItemInstance? item = FindAnywhere(Selected.InstanceId, ref space);
            return (item, space);
        }
        return (null, 0);
    }

    /// Canc: lascia cadere a terra l'oggetto sotto il cursore.
    public void DiscardFocused()
    {
        var (item, space) = Focused();
        if (item != null && space == ItemAddress.PlayerSpace)
            Discard(item, space);
    }

    /// Tasti 4-9/0 col cursore su una tile: collega l'oggetto allo slot rapido. False se non c'e' nulla.
    public bool BindFocusedToQuickSlot(int slotIndex)
    {
        if (_hovered == null || _hoveredSpace != ItemAddress.PlayerSpace)
            return false;
        if (!_hovered.Definition.QuickUsable)
        {
            ShowError(InvError.Incompatible);
            return true;
        }
        Inventory.SubmitAssignQuickSlot(slotIndex, _hovered.InstanceId);
        return true;
    }

    public void Discard(ItemInstance item, int space)
        => Inventory.SubmitMove(space, item.InstanceId, ItemAddress.Ground(), PlayerInventory.AutoPlace, 0, false);

    public void SortContainer(ItemInstance container, int space)
    {
        for (int g = 0; g < container.Grids.Length; g++)
            Inventory.SubmitSort(ItemAddress.GridOf(space, container.InstanceId, g));
    }

    /// Esc: annulla il trascinamento, altrimenti chiude l'ultima finestra aperta, altrimenti torna
    /// dal contenitore agli oggetti a terra. False = nulla da chiudere (l'HUD chiude l'inventario).
    public bool HandleEscape()
    {
        if (Drag != null)
        {
            CancelDrag();
            return true;
        }

        for (int i = _windowLayer.GetChildCount() - 1; i >= 0; i--)
        {
            if (_windowLayer.GetChild(i) is FloatingWindow window && !window.IsQueuedForDeletion())
            {
                window.QueueFree();
                return true;
            }
        }
        return false;
    }

    /// Alla chiusura dell'inventario: nessun gesto a meta', nessuna finestra, nessun bottino aperto.
    public void OnClosed()
    {
        CancelDrag();
        Clear(_windowLayer);
        _openWorldUid = 0;
        Selected = null;
    }

    // ====================================================================================
    //  Finestre, menu, notifiche
    // ====================================================================================

    /// Doppio click / "Apri": finestra del contenitore o degli slot, pacchetto, altrimenti Ispeziona.
    public void OpenItem(ItemInstance item, int space)
    {
        if (item.IsContainer || item.SlotItems.Length > 0)
        {
            foreach (Node child in _windowLayer.GetChildren())
            {
                if (child is ContainerWindow existing && existing.ItemId == item.InstanceId
                    && !existing.IsQueuedForDeletion())
                {
                    existing.MoveToFront();
                    return;
                }
            }
            AddWindow(new ContainerWindow(this, item, space));
        }
        else if (item.Definition.UnpackYields.Length > 0 && space == ItemAddress.PlayerSpace)
        {
            Inventory.SubmitUnpack(item.InstanceId);
        }
        else
        {
            OpenInspectWindow(item);
        }
    }

    public void OpenInspectWindow(ItemInstance item) => AddWindow(new InspectWindow(item));

    public void OpenTagDialog(ItemInstance container, int space)
    {
        int id = container.InstanceId;
        AddWindow(new TagDialog(container, (name, color) => Inventory.SubmitTag(space, id, name, color)));
    }

    private void OpenSplitDialog(ItemInstance stack, System.Action<int> confirm)
        => AddWindow(new SplitDialog(stack.Definition.DisplayName, stack.StackCount, confirm), centered: true);

    private void AddWindow(FloatingWindow window, bool centered = false)
    {
        // A cascata, da una frazione del layer: un offset assoluto finirebbe fuori su finestre piccole.
        int index = _windowLayer.GetChildCount();
        Vector2 area = _windowLayer.Size;
        Vector2 origin = centered
            ? GetGlobalMousePosition() - _windowLayer.GlobalPosition - new Vector2(140, 40)
            : new Vector2(area.X * 0.34f, area.Y * 0.16f) + Vector2.One * (index % 8) * 28f;
        _windowLayer.AddChild(window);
        window.Position = origin;
    }

    private void OpenContextMenu(ItemInstance item, int space)
    {
        var menu = new ItemContextMenu(this, item, space);
        _windowLayer.AddChild(menu);
        menu.Open(GetGlobalMousePosition());
    }

    /// Apre nella colonna destra il contenuto di un contenitore del mondo (chiamata con F).
    public void OpenWorldContainer(int worldUid)
    {
        _openWorldUid = worldUid;
        RebuildSource();
    }

    private void CloseWorldContainer()
    {
        _openWorldUid = 0;
        RebuildSource();
    }

    private void OnOperationFailed(int error) => ShowError((InvError)error);

    /// Notifica testuale in basso al centro. Un errore uguale ripetuto subito non si rinnova.
    public void ShowError(InvError error)
    {
        if (error is InvError.Ok or InvError.NothingToDo)
            return;

        ulong now = Time.GetTicksMsec();
        if (error == _lastError && now - _lastErrorMsec < 300)
            return;
        _lastError = error;
        _lastErrorMsec = now;

        _toast.Text = Loc.T($"UI_INV_ERR_{error.ToString().ToUpperInvariant()}");
        _toast.Visible = true;
        _toast.Modulate = Colors.White;
        _toastRemaining = InventoryConfig.Current.ErrorToastSeconds;
    }

    /// Tooltip di una tile: nome completo, peso (col contenuto), ingombro.
    public static string TooltipFor(ItemInstance item)
        => Loc.T("UI_INV_TOOLTIP", item.Definition.DisplayName, Loc.Num(item.TotalWeight()), item.Width, item.Height);
}
