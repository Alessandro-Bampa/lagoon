using System.Collections.Generic;
using Godot;

namespace Lagoon;

/// <summary>
/// Schermata inventario in stile Tarkov, adattata all'isometrico. Impianto a DOPPIO PANNELLO:
///  - a sinistra il proprio equipaggiamento (slot indossati, tasche, rig, zaino, contenitore sicuro);
///  - a destra la fonte esterna: gli oggetti a terra vicini, oppure il contenuto del contenitore
///    aperto con F (cassa, zaino droppato);
///  - sopra, un layer di FINESTRE pop-up trascinabili (contenitori aperti con doppio click e schede
///    Ispeziona), di cui se ne possono tenere aperte piu' d'una.
///
/// E' anche il punto in cui convergono le scorciatoie rapide, perche' tutte si riducono a uno
/// spostamento fra due <see cref="ItemAddress"/>: Ctrl+click (lato opposto), Alt+click (equipaggia),
/// Delete (getta l'item sotto il cursore), tasti 4-9/0 (assegna alla hotbar l'item sotto il cursore).
///
/// Non muta mai lo stato: legge il modello replicato ed emette richieste verso l'host.
/// </summary>
public partial class InventoryScreen : Control
{
    public PlayerInventory Inventory { get; }
    public PlayerInventoryModel Model => Inventory.Model;

    /// Rotazione da applicare al prossimo piazzamento (toggle con R durante il trascinamento).
    public bool PendingRotated { get; private set; }

    /// Definizione dell'item trascinato: serve ai bersagli per calcolare l'ingombro anche quando
    /// l'item non appartiene al proprio modello (es. saccheggio da una cassa).
    public ItemDefinition? DraggedDefinition { get; private set; }

    private VBoxContainer _gearColumn = null!;
    private VBoxContainer _storageColumn = null!;
    private VBoxContainer _sourceColumn = null!;
    private Control _windowLayer = null!;
    private Label _weightLabel = null!;
    private Label _rotationLabel = null!;

    /// Contenitore nel mondo attualmente aperto nel pannello destro (0 = oggetti a terra).
    private int _openWorldUid;

    // Item attualmente sotto il cursore (per Delete e per i tasti rapidi).
    private ItemInstance? _hovered;
    private ItemAddress _hoveredFrom;

    private float _sinceGroundRefresh;

    public InventoryScreen(PlayerInventory inventory)
    {
        Inventory = inventory;
    }

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop; // blocca l'input di gioco quando aperto
        BuildChrome();
        SetProcess(true);
        Rebuild();
    }

    // ====================================================================================
    //  Struttura
    // ====================================================================================

    /// Distanza costante dai bordi della finestra e fra le colonne (guida UI, punto 3).
    private const int Gutter = 24;

    private void BuildChrome()
    {
        var background = new ColorRect { Color = new Color(0.05f, 0.05f, 0.07f, 0.88f) };
        background.SetAnchorsPreset(LayoutPreset.FullRect);
        background.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(background);

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", Gutter);
        margin.AddThemeConstantOverride("margin_top", Gutter);
        margin.AddThemeConstantOverride("margin_right", Gutter);
        // Lascia libera la fascia della hotbar: senza questo le griglie finirebbero dietro di essa.
        margin.AddThemeConstantOverride("margin_bottom", HotbarSlotView.SlotSize + 32);
        AddChild(margin);

        var columns = new HBoxContainer();
        columns.AddThemeConstantOverride("separation", Gutter);
        margin.AddChild(columns);

        // Impianto a tre fasce ancorate ai bordi (guida UI, punto 2): la scheda del personaggio
        // resta agganciata a sinistra, la fonte esterna a destra, e le proprie griglie assorbono
        // tutto lo spazio in mezzo. Cosi' i due pannelli non "scivolano" al variare della
        // risoluzione o della scala UI: restano dove il giocatore si aspetta di trovarli.
        _gearColumn = AddColumn(columns, "UI_INV_COL_GEAR", GearColumnWidth, SizeFlags.ShrinkBegin);
        _storageColumn = AddColumn(columns, "UI_INV_COL_STORAGE", GridColumnWidth, SizeFlags.ExpandFill);
        _sourceColumn = AddColumn(columns, "UI_INV_COL_SOURCE", GridColumnWidth, SizeFlags.ShrinkEnd);

        // Testo composto e gia' tradotto: niente auto-translate, l'aggiornamento passa da Rebuild
        // e da UpdateRotationLabel (rieseguiti su NotificationTranslationChanged).
        _weightLabel = new Label { AutoTranslateMode = AutoTranslateModeEnum.Disabled };
        _gearColumn.AddChild(_weightLabel);
        _rotationLabel = new Label { AutoTranslateMode = AutoTranslateModeEnum.Disabled };
        _gearColumn.AddChild(_rotationLabel);

        // Layer finestre: sopra i pannelli, non partecipa al layout.
        _windowLayer = new Control { MouseFilter = MouseFilterEnum.Ignore };
        _windowLayer.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_windowLayer);

        UpdateRotationLabel();
    }

    /// Larghezza minima della colonna del personaggio: etichetta (80) + slot (96) + separazione,
    /// piu' lo spazio della barra di scorrimento verticale.
    private const int GearColumnWidth = 200;

    /// Larghezza minima delle colonne di griglie: la piu' larga in gioco e' 6 celle da 48 px (288),
    /// piu' il margine della barra di scorrimento verticale — sotto questa soglia comparirebbe
    /// anche quella orizzontale.
    private const int GridColumnWidth = 320;

    /// <summary>
    /// Colonna con intestazione dentro un proprio ScrollContainer.
    /// Un <c>ScrollContainer</c> con scorrimento orizzontale "auto" ha larghezza minima ~0: senza
    /// <c>CustomMinimumSize</c> e size flags espliciti le colonne collasserebbero sul lato sinistro
    /// dello schermo. <paramref name="minWidth"/> garantisce la leggibilita' su finestre strette;
    /// <paramref name="horizontal"/> decide l'ancoraggio: <c>ShrinkBegin</c> incolla la colonna al
    /// bordo sinistro, <c>ShrinkEnd</c> a quello destro, <c>ExpandFill</c> le fa assorbire lo spazio
    /// residuo (una sola colonna dovrebbe espandersi, altrimenti le altre non restano ancorate).
    /// </summary>
    private static VBoxContainer AddColumn(
        HBoxContainer parent, string titleKey, int minWidth, SizeFlags horizontal)
    {
        var outer = new VBoxContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = horizontal,
        };
        outer.AddThemeConstantOverride("separation", 6);
        parent.AddChild(outer);

        // Chiave di traduzione: essendo un Control nell'albero, la risolve l'auto-translate di
        // Godot, che la riaggiorna da solo al cambio lingua.
        var header = new Label { Text = titleKey };
        header.AddThemeColorOverride("font_color", new Color(0.75f, 0.75f, 0.8f));
        outer.AddChild(header);

        // Scroll orizzontale su "auto": le griglie hanno larghezza fissa in pixel (celle da 48), e
        // su finestre strette verrebbero altrimenti tagliate senza modo di raggiungerle.
        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Auto,
            CustomMinimumSize = new Vector2(minWidth, 0),
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        outer.AddChild(scroll);

        // ExpandFill anche qui: lo ScrollContainer allarga il figlio fino alla propria larghezza,
        // cosi' le griglie restano allineate a sinistra invece di essere centrate su un blocco stretto.
        var content = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        content.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(content);
        return content;
    }

    // ====================================================================================
    //  Ricostruzione dallo stato replicato
    // ====================================================================================

    public void Rebuild()
    {
        RebuildGear();
        RebuildStorage();
        RebuildSource();
        RefreshWindows();
        _weightLabel.Text = Loc.T("UI_INV_WEIGHT", Loc.Num(Model.TotalWeight()), Loc.Num(Model.MaxLoadKg, "0"));
    }

    /// Le finestre aperte rileggono il proprio contenuto dall'indirizzo (niente dati stale).
    private void RefreshWindows()
    {
        foreach (Node child in _windowLayer.GetChildren())
            if (child is ContainerWindow window)
                window.Refresh();
    }

    /// <summary>
    /// Griglia corrente per un indirizzo, lato client: dal modello replicato per gli indirizzi del
    /// giocatore, dal payload del world item per quelli nel mondo. Null se non piu' raggiungibile.
    /// </summary>
    public InventoryGrid? ResolveGrid(ItemAddress address)
    {
        switch (address.Realm)
        {
            case ItemAddress.RealmType.PlayerGrid:
                return Model.GridFor(address.ContainerId);

            case ItemAddress.RealmType.PlayerEquip:
                return Model.Equipment.Get(address.Slot)?.ContainerGrid;

            case ItemAddress.RealmType.WorldContainer:
            {
                ItemPickup? pickup = FindWorldItem(address.WorldItemUid);
                if (pickup == null || !IsNearby(pickup))
                    return null;

                ItemInstance? root = PlayerInventoryModel.DeserializeItemWith(
                    pickup.Payload, ResolveDefinition, null);
                if (root == null)
                    return null;

                return address.ContainerInstanceId == 0
                    ? root.ContainerGrid
                    : ItemTree.FindGrid(root, address.ContainerInstanceId);
            }

            default:
                return null;
        }
    }

    /// Colonna 1: gli slot indossati che non sono contenitori, piu' le armi (riservate alla Fase 3).
    private static readonly EquipSlotType[] GearSlots =
    {
        EquipSlotType.Head,
        EquipSlotType.Torso,
        EquipSlotType.Legs,
        EquipSlotType.Feet,
        EquipSlotType.WeaponPrimary,
        EquipSlotType.WeaponSecondary,
        EquipSlotType.Sidearm,
    };

    private void RebuildGear()
    {
        ClearFrom(_gearColumn, keep: 2); // conserva le label peso/rotazione
        foreach (EquipSlotType slot in GearSlots)
            _gearColumn.AddChild(BuildSlotRow(slot));
    }

    /// Colonna 2: tasche + i contenitori indossati (rig, zaino, contenitore sicuro) con le griglie.
    private void RebuildStorage()
    {
        ClearFrom(_storageColumn, keep: 0);

        AddLabeledGrid(_storageColumn, Loc.T("UI_INV_POCKETS"), Model.Pockets, ItemAddress.Pockets(), OpenableIn(Model.Pockets));

        AddContainerSlot(EquipSlotType.Vest);
        AddContainerSlot(EquipSlotType.Backpack);
        AddContainerSlot(EquipSlotType.SecureContainer);
    }

    private void AddContainerSlot(EquipSlotType slot)
    {
        _storageColumn.AddChild(BuildSlotRow(slot));

        ItemInstance? equipped = Model.Equipment.Get(slot);
        if (equipped?.ContainerGrid != null)
        {
            AddLabeledGrid(
                _storageColumn, EquipmentSlotView.SlotLabel(slot), equipped.ContainerGrid,
                ItemAddress.PlayerGridAt(equipped.InstanceId), OpenableIn(equipped.ContainerGrid));
        }
    }

    /// Colonna 3: contenitore aperto, oppure gli oggetti a terra nelle vicinanze.
    private void RebuildSource()
    {
        ClearFrom(_sourceColumn, keep: 0);

        if (_openWorldUid != 0 && BuildOpenWorldContainer())
            return;

        _openWorldUid = 0;
        BuildGroundItems();
    }

    /// Contenuto del contenitore/cassa aperto. False se non e' piu' raggiungibile.
    private bool BuildOpenWorldContainer()
    {
        ItemPickup? pickup = FindWorldItem(_openWorldUid);
        ItemInstance? root = pickup != null && IsNearby(pickup)
            ? PlayerInventoryModel.DeserializeItemWith(pickup.Payload, ResolveDefinition, null)
            : null;

        if (root?.ContainerGrid == null)
            return false;

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 8);
        var back = new Button { Text = "UI_INV_BACK_TO_GROUND" };
        back.Pressed += () => { _openWorldUid = 0; RebuildSource(); };
        header.AddChild(back);
        header.AddChild(new Label
        {
            Text = root.Definition.DisplayName,
            AutoTranslateMode = AutoTranslateModeEnum.Disabled,
            VerticalAlignment = VerticalAlignment.Center,
        });
        _sourceColumn.AddChild(header);

        AddLabeledGrid(
            _sourceColumn, Loc.T("UI_INV_CONTENTS"), root.ContainerGrid,
            ItemAddress.WorldContainerAt(_openWorldUid), OpenableIn(root.ContainerGrid));
        return true;
    }

    /// <summary>
    /// Oggetti sfusi a terra nel raggio, disposti in una griglia virtuale. Ogni item e' un world
    /// item a se': l'InstanceId di display e' l'uid del pickup (univoco), e la sua sorgente e'
    /// <see cref="ItemAddress.WorldLoose"/> con quell'uid.
    /// </summary>
    private void BuildGroundItems()
    {
        var grid = new InventoryGrid(GroundColumns, GroundRowsToFill());
        var openable = new HashSet<int>();
        var uidOf = new Dictionary<int, int>();
        int count = 0;

        foreach (ItemPickup pickup in NearbyWorldItems())
        {
            ItemDefinition? def = ResolveDefinition(pickup.ItemId);
            if (def == null)
                continue;

            var display = new ItemInstance(pickup.Uid, def) { StackCount = pickup.StackCount };
            if (!grid.TryAutoPlace(display))
                continue;

            uidOf[display.InstanceId] = pickup.Uid;
            count++;
            if (pickup.IsContainer)
                openable.Add(display.InstanceId);
        }

        if (count == 0)
            _sourceColumn.AddChild(Dim(Loc.T("UI_INV_GROUND_EMPTY")));

        var view = new GridPanelView(
            this, grid, ItemAddress.Ground(),
            sourceOf: item => new ItemAddress(ItemAddress.RealmType.WorldLoose, uidOf.GetValueOrDefault(item.InstanceId)),
            openable: openable);
        _sourceColumn.AddChild(view);
    }

    private const int GroundColumns = 6;

    /// Righe minime della griglia a terra su viewport bassi (o a scala UI molto alta).
    private const int GroundMinRows = 8;

    /// Spazio occupato sopra la griglia dentro la colonna: intestazione "A TERRA" piu' le
    /// separazioni verticali del VBox che la contiene.
    private const int SourceHeaderHeight = 34;

    /// <summary>
    /// Righe della griglia a terra: si estende fino al bordo inferiore dell'area utile, che si
    /// ferma sopra la fascia riservata alla hotbar (lo stesso margine usato in BuildChrome).
    /// Si calcola dal viewport, gia' in coordinate logiche, quindi segue anche la scala UI; viene
    /// rivalutata a ogni RebuildSource, cosi' si adatta al ridimensionamento della finestra.
    /// </summary>
    private int GroundRowsToFill()
    {
        float available = GetViewportRect().Size.Y
            - Gutter                                // margine superiore
            - (HotbarSlotView.SlotSize + 32)        // fascia hotbar riservata
            - SourceHeaderHeight;

        return Mathf.Max(GroundMinRows, Mathf.FloorToInt(available / GridPanelView.CellSize));
    }

    private HBoxContainer BuildSlotRow(EquipSlotType slot)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        row.AddChild(new Label
        {
            Text = EquipmentSlotView.SlotLabel(slot),
            AutoTranslateMode = AutoTranslateModeEnum.Disabled,
            CustomMinimumSize = new Vector2(80, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.AddChild(new EquipmentSlotView(slot, Model.Equipment.Get(slot), this));
        return row;
    }

    /// <paramref name="title"/> e' testo GIA' tradotto (viene da Loc.T o da un DisplayName).
    private void AddLabeledGrid(
        VBoxContainer parent, string title, InventoryGrid grid, ItemAddress address, HashSet<int> openable)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);
        box.AddChild(new Label { Text = title, AutoTranslateMode = AutoTranslateModeEnum.Disabled });
        box.AddChild(new GridPanelView(this, grid, address, openable: openable));
        parent.AddChild(box);
    }

    /// InstanceId dei contenitori dentro una griglia: sono quelli apribili con doppio click.
    private static HashSet<int> OpenableIn(InventoryGrid grid)
    {
        var set = new HashSet<int>();
        foreach (var item in grid.Items)
            if (item.ContainerGrid != null)
                set.Add(item.InstanceId);
        return set;
    }

    /// Riceve testo GIA' tradotto: l'auto-translate va disattivato (skill i18n-localization).
    private static Label Dim(string text)
    {
        var label = new Label { Text = text, AutoTranslateMode = AutoTranslateModeEnum.Disabled };
        label.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.66f));
        return label;
    }

    private static void ClearFrom(Node parent, int keep)
    {
        var children = parent.GetChildren();
        for (int i = children.Count - 1; i >= keep; i--)
        {
            Node child = children[i];
            parent.RemoveChild(child);
            child.QueueFree();
        }
    }

    // ====================================================================================
    //  Trascinamento e rotazione
    // ====================================================================================

    public void OnDragStart(ItemDefinition definition, bool initialRotated)
    {
        DraggedDefinition = definition;
        PendingRotated = initialRotated;
        UpdateRotationLabel();
    }

    public void ToggleRotation()
    {
        PendingRotated = !PendingRotated;
        UpdateRotationLabel();
    }

    /// Celle occupate dall'item in trascinamento con la rotazione corrente (0,0 se nessun drag).
    public (int width, int height) DraggedFootprint()
    {
        if (DraggedDefinition == null)
            return (0, 0);
        return PendingRotated
            ? (DraggedDefinition.Height, DraggedDefinition.Width)
            : (DraggedDefinition.Width, DraggedDefinition.Height);
    }

    private void UpdateRotationLabel()
    {
        if (_rotationLabel == null)
            return;

        // Il tasto si legge dall'InputMap invece di scriverlo nel testo (skill ui-hud §4).
        _rotationLabel.Text = Loc.T(
            "UI_INV_ROTATION",
            Loc.KeyFor("rotate_item"),
            Loc.T(PendingRotated ? "UI_INV_ROTATION_ON" : "UI_INV_ROTATION_OFF"));
    }

    /// Al cambio lingua i testi composti non si riaggiornano da soli: si ricostruisce la schermata,
    /// che e' gia' l'operazione che la rigenera interamente dallo stato replicato.
    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged && _rotationLabel != null)
        {
            UpdateRotationLabel();
            Rebuild();
        }
    }

    // ====================================================================================
    //  Hover + scorciatoie rapide
    // ====================================================================================

    public void SetHovered(ItemInstance? item, ItemAddress from)
    {
        _hovered = item;
        _hoveredFrom = from;
    }

    public void ClearHovered(ItemAddress from)
    {
        if (_hovered != null && _hoveredFrom.Equals(from))
            _hovered = null;
    }

    /// Delete: getta a terra l'item sotto il cursore, senza doverlo trascinare.
    public void QuickDropHovered()
    {
        if (_hovered == null || !_hoveredFrom.IsPlayer)
            return;
        Inventory.SubmitMove(_hoveredFrom, _hovered.InstanceId, ItemAddress.Ground(),
            PlayerInventory.AutoPlace, 0, false);
    }

    /// Tasti 4-9/0: assegna alla hotbar l'item sotto il cursore (deve stare in tasche o rig).
    public void AssignHoveredToQuickSlot(int slotIndex)
    {
        if (_hovered == null || !_hoveredFrom.IsPlayer)
            return;
        Inventory.SubmitAssignQuickSlot(slotIndex, _hovered.InstanceId);
    }

    /// Ctrl+click: sposta l'item nel primo spazio libero del lato opposto.
    public void QuickMove(ItemInstance item, ItemAddress source)
    {
        ItemAddress target = source.IsPlayer ? CurrentExternalTarget() : ItemAddress.Pockets();
        Inventory.SubmitMove(source, item.InstanceId, target, PlayerInventory.AutoPlace, 0, false);
    }

    /// Alt+click: equipaggia nello slot corrispondente, se compatibile.
    public void QuickEquip(ItemInstance item, ItemAddress source)
    {
        EquipSlotType slot = item.Definition.EquipSlot;
        if (slot == EquipSlotType.None)
            return;
        Inventory.SubmitMove(source, item.InstanceId, ItemAddress.Equip(slot),
            PlayerInventory.AutoPlace, 0, false);
    }

    /// <summary>
    /// Destinazione "esterna" corrente per il quick move: il contenitore aperto se c'e', altrimenti
    /// il terreno. Con la destinazione player si usa invece l'auto-stow del modello (tasche→rig→zaino).
    /// </summary>
    private ItemAddress CurrentExternalTarget()
        => _openWorldUid != 0 ? ItemAddress.WorldContainerAt(_openWorldUid) : ItemAddress.Ground();

    // ====================================================================================
    //  Finestre e menu
    // ====================================================================================

    /// Apre (o porta in primo piano) la finestra di un contenitore.
    public void OpenContainerWindow(ItemInstance container, ItemAddress source)
    {
        if (container.ContainerGrid == null)
            return;

        // Indirizzo della griglia interna: dentro l'inventario e' il container stesso; nel mondo e'
        // il container dentro quel world item.
        ItemAddress gridAddress = source.Realm switch
        {
            ItemAddress.RealmType.WorldLoose => ItemAddress.WorldContainerAt(source.WorldItemUid),
            ItemAddress.RealmType.WorldContainer =>
                ItemAddress.WorldContainerAt(source.WorldItemUid, container.InstanceId),
            ItemAddress.RealmType.PlayerEquip => ItemAddress.PlayerGridAt(container.InstanceId),
            _ => ItemAddress.PlayerGridAt(container.InstanceId),
        };

        foreach (Node child in _windowLayer.GetChildren())
        {
            if (child is ContainerWindow existing && existing.GridAddress.Equals(gridAddress))
            {
                existing.MoveToFront();
                return;
            }
        }

        var window = new ContainerWindow(this, container.Definition.DisplayName, gridAddress);
        _windowLayer.AddChild(window);
        window.Position = NextWindowPosition();
    }

    public void OpenInspectWindow(ItemInstance item)
    {
        var window = new InspectWindow(item);
        _windowLayer.AddChild(window);
        window.Position = NextWindowPosition();
    }

    /// Sfalsa le finestre cosi' quelle aperte in sequenza non si sovrappongono esattamente.
    /// La cascata parte da una frazione del layer invece che da coordinate fisse: su finestre
    /// piccole (o a scala UI alta) un offset assoluto finirebbe subito fuori dall'area utile.
    private Vector2 NextWindowPosition()
    {
        int index = _windowLayer.GetChildCount() - 1;
        Vector2 area = _windowLayer.Size;
        var origin = new Vector2(area.X * 0.30f, area.Y * 0.15f);
        return origin + new Vector2(index * WindowCascade, index * WindowCascade);
    }

    private const int WindowCascade = 28;

    public void OpenContextMenu(ItemInstance item, ItemAddress source, Vector2 globalPosition)
    {
        var menu = new ItemContextMenu(this, item, source);
        _windowLayer.AddChild(menu);
        // Un PopupMenu e' una Window a se': senza questo resterebbe a scala 1.0 mentre il resto
        // della UI segue lo slider "Scala UI".
        SettingsService.ApplyToPopup(menu);
        menu.Position = (Vector2I)globalPosition;
        menu.Popup();
    }

    /// Apre nel pannello destro il contenuto di un contenitore nel mondo (chiamato con F).
    public void OpenWorldContainer(int worldItemUid)
    {
        _openWorldUid = worldItemUid;
        RebuildSource();
    }

    // ====================================================================================
    //  Aggiornamento periodico degli oggetti a terra
    // ====================================================================================

    public override void _Process(double delta)
    {
        // Il set di oggetti vicini cambia col movimento del giocatore e con drop/raccolte altrui.
        if (!IsVisibleInTree() || GetViewport().GuiIsDragging())
            return;

        _sinceGroundRefresh += (float)delta;
        if (_sinceGroundRefresh < 0.4f)
            return;

        _sinceGroundRefresh = 0f;
        RebuildSource();
    }

    private IEnumerable<ItemPickup> NearbyWorldItems()
    {
        foreach (Node node in GetTree().GetNodesInGroup(ItemPickup.GroupName))
            if (node is ItemPickup pickup && IsNearby(pickup))
                yield return pickup;
    }

    private bool IsNearby(ItemPickup pickup)
    {
        Node3D? player = Inventory.GetParent<Node3D>();
        return player != null
            && !pickup.IsQueuedForDeletion()
            && pickup.GlobalPosition.DistanceTo(player.GlobalPosition) <= PlayerInventory.PickupRange;
    }

    private ItemPickup? FindWorldItem(int uid)
    {
        foreach (Node node in GetTree().GetNodesInGroup(ItemPickup.GroupName))
            if (node is ItemPickup pickup && pickup.Uid == uid && !pickup.IsQueuedForDeletion())
                return pickup;
        return null;
    }

    private ItemDefinition? ResolveDefinition(string itemId)
        => GetNodeOrNull<ItemDatabase>("/root/ItemDatabase")?.Get(itemId);
}
