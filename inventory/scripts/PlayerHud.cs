using Godot;

namespace Lagoon;

/// <summary>
/// HUD dell'inventario, istanziato SOLO per l'avatar locale (stesso criterio della camera in
/// <see cref="FirstPersonCamera"/>). Crea il proprio <c>CanvasLayer</c> con la schermata inventario
/// e la barra rapida, e traduce l'input in richieste. Nessuna logica di inventario qui.
///
/// E' anche il PROPRIETARIO DEL CURSORE: in gioco il mouse e' catturato dallo sguardo, e solo qui
/// si decide quando restituirlo alla UI (inventario aperto, modale aperta). Chi apre una UI non
/// tocca mai <c>Input.MouseMode</c>: alza il proprio stato e basta.
///
/// Interazione col mondo in stile Tarkov:
///  - GUARDANDO un oggetto a portata compare il prompt "Nome [F]" (bianco semitrasparente);
///  - <b>F</b> breve raccoglie l'oggetto, oppure APRE il contenitore se e' una cassa;
///  - <b>F</b> tenuto premuto apre il menu contestuale a terra (Raccogli / Apri / Ispeziona).
/// </summary>
public partial class PlayerHud : Node
{
    /// Quanto va tenuto premuto F per ottenere il menu contestuale invece della raccolta diretta.
    private const float HoldSeconds = 0.35f;

    /// Coseno dell'angolo massimo fra lo sguardo e un oggetto perche' lo si stia "guardando" (20°).
    private const float LookAtCosine = 0.94f;

    /// Altezza del centro di un oggetto a terra: si punta al corpo dell'oggetto, non alla base.
    private const float PickupCenterHeight = 0.25f;

    private PlayerInventory _inventory = null!;
    private Camera3D? _camera;
    private GameManager _game = null!;
    private SettingsService? _settings;
    private InventoryScreen? _screen;
    private QuickBar? _quickBar;
    private Control? _hudRoot;
    private bool _isLocal;

    // Stato della pressione di F.
    private bool _holdingInteract;
    private float _heldFor;
    private ItemPickup? _heldTarget;
    private bool _holdConsumed;

    /// <summary>
    /// True se la pressione di F in corso appartiene a NOI e non al sistema veicoli. Deciso una volta
    /// sola alla pressione: anche il rilascio deve andare allo stesso proprietario, altrimenti
    /// <see cref="_holdingInteract"/> resterebbe appeso e il menu contestuale si aprirebbe da solo.
    /// </summary>
    private bool _interactClaimed;

    // Pickup attualmente evidenziato col prompt.
    private ItemPickup? _prompted;

    /// <summary>
    /// True se la schermata inventario e' aperta. Serve al sistema di tiro: a inventario aperto il
    /// cursore appartiene alla UI, quindi l'input di combattimento tace, ed e' anche il discriminante
    /// che disambigua il tasto R (rotazione vs ricarica) — vedi <see cref="WeaponInput"/>.
    /// </summary>
    public bool InventoryOpen => _screen is { Visible: true };

    public override void _Ready()
    {
        // Il Player root ha autorita' = peer proprietario: vero solo sull'istanza locale.
        _isLocal = GetParent().IsMultiplayerAuthority();
        _inventory = GetParent().GetNode<PlayerInventory>("Inventory");
        _game = GetNode<GameManager>("/root/GameManager");

        if (!_isLocal)
        {
            SetProcess(false);
            SetProcessInput(false);
            return;
        }

        _camera = GetParent().GetNodeOrNull<Camera3D>("PlayerCamera");

        var layer = new CanvasLayer { Layer = 10 };
        AddChild(layer);

        // I Control "top-level" sotto CanvasLayer non seguono in modo affidabile l'area visibile:
        // _hudRoot sincronizza a mano il proprio rect col viewport. GetVisibleRect() e' gia' in
        // coordinate LOGICHE, quindi il rect si adatta da solo anche quando cambia la scala UI.
        _hudRoot = new Control { TopLevel = true, MouseFilter = Control.MouseFilterEnum.Ignore };
        layer.AddChild(_hudRoot);

        _screen = new InventoryScreen(_inventory) { Visible = false };
        _hudRoot.AddChild(_screen);

        _quickBar = new QuickBar(_inventory, _screen);
        _hudRoot.AddChild(_quickBar);

        AddCombatHud();

        SyncHudRect();
        GetTree().Root.SizeChanged += SyncHudRect;

        // Cambiare ContentScaleFactor ridimensiona il viewport logico: risincronizza subito il rect.
        _settings = GetNodeOrNull<SettingsService>("/root/SettingsService");
        if (_settings != null)
            _settings.UiScaleChanged += OnUiScaleChanged;

        _inventory.InventoryChanged += OnInventoryChanged;
        _inventory.SubmitRequestState();
    }

    public override void _ExitTree()
    {
        if (!_isLocal)
            return;

        GetTree().Root.SizeChanged -= SyncHudRect;
        if (_settings != null)
            _settings.UiScaleChanged -= OnUiScaleChanged;
        _inventory.InventoryChanged -= OnInventoryChanged;

        _game.InventoryOpen = false;
        // Senza avatar locale non c'e' piu' nessuno sguardo da pilotare: il cursore torna alla UI.
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    /// In gioco il mouse e' dello sguardo; inventario e modali lo restituiscono al cursore.
    private void UpdateMouseCapture()
    {
        Input.MouseModeEnum wanted = _game.UiModalOpen || InventoryOpen
            ? Input.MouseModeEnum.Visible
            : Input.MouseModeEnum.Captured;

        if (Input.MouseMode != wanted)
            Input.MouseMode = wanted;
    }

    /// Pannello arma/salute e conferma di colpo: condividono il <c>_hudRoot</c> gia' sincronizzato
    /// col viewport, ed esistono solo per l'avatar locale come tutto il resto della HUD.
    private void AddCombatHud()
    {
        Node parent = GetParent();
        var weapon = parent.GetNodeOrNull<WeaponController>("Weapon");
        var health = parent.GetNodeOrNull<HealthComponent>("Health");

        if (weapon == null || health == null || _hudRoot == null)
            return;

        _hudRoot.AddChild(new WeaponHudPanel(weapon, health));

        // Nessun reticolo: solo la conferma del colpo a segno.
        _hudRoot.AddChild(new HitMarkerOverlay(weapon, _game, this));
    }

    private void OnUiScaleChanged(float scale) => SyncHudRect();

    private void SyncHudRect()
    {
        if (_hudRoot == null)
            return;
        Rect2 visible = GetViewport().GetVisibleRect();
        _hudRoot.Position = visible.Position;
        _hudRoot.Size = visible.Size;
    }

    private void OnInventoryChanged()
    {
        _screen?.Rebuild();
        _quickBar?.Refresh();
    }

    /// Apre/chiude la schermata. All'apertura chiede lo stato all'host: i contatori dei caricatori
    /// cambiano sparando senza un push per ogni colpo, quindi qui ci si riallinea.
    private void SetInventoryOpen(bool open)
    {
        if (_screen == null || _screen.Visible == open)
            return;

        _screen.Visible = open;
        _game.InventoryOpen = open;
        if (open)
            _inventory.SubmitRequestState();
        else
            _screen.OnClosed();
    }

    // ====================================================================================
    //  Prompt in-world + gestione della pressione di F
    // ====================================================================================

    public override void _Process(double delta)
    {
        UpdateMouseCapture();
        UpdateWorldPrompt();

        // Se il menu di pausa si apre mentre F e' premuto, annulla la pressione in corso.
        if (_game.UiModalOpen)
        {
            _holdingInteract = false;
            _interactClaimed = false;
            _heldTarget = null;
            return;
        }

        if (!_holdingInteract || _holdConsumed)
            return;

        _heldFor += (float)delta;
        if (_heldFor < HoldSeconds)
            return;

        // Pressione lunga: menu contestuale sull'oggetto a terra.
        _holdConsumed = true;
        if (_heldTarget != null && GodotObject.IsInstanceValid(_heldTarget))
            ShowGroundMenu(_heldTarget);
    }

    /// Mostra il prompt solo sull'oggetto che si sta guardando, e solo a chi gioca qui.
    private void UpdateWorldPrompt()
    {
        ItemPickup? nearest = InventoryOpen ? null : FindTargetPickup();
        if (ReferenceEquals(nearest, _prompted))
            return;

        if (GodotObject.IsInstanceValid(_prompted))
            _prompted?.SetPromptVisible(false);

        _prompted = nearest;
        _prompted?.SetPromptVisible(true);
    }

    private void ShowGroundMenu(ItemPickup pickup)
    {
        ItemInstance? root = pickup.Root;
        if (root == null || _hudRoot == null)
            return;

        var menu = new PopupMenu();
        // Un PopupMenu costruito in codice non passa dall'auto-translate: le voci si traducono qui.
        if (!pickup.Anchored)
            menu.AddItem(Loc.T("UI_HUD_GROUND_PICKUP"), 0);
        if (pickup.IsContainer)
            menu.AddItem(Loc.T("UI_HUD_GROUND_OPEN"), 1);
        menu.AddItem(Loc.T("UI_HUD_GROUND_EXAMINE"), 2);
        _hudRoot.AddChild(menu);
        SettingsService.ApplyToPopup(menu);

        int uid = pickup.Uid;
        menu.IdPressed += id =>
        {
            switch (id)
            {
                case 0:
                    _inventory.SubmitPickup(uid);
                    break;
                case 1:
                    OpenLoot(uid);
                    break;
                default:
                    SetInventoryOpen(true);
                    _screen?.OpenInspectWindow(root);
                    break;
            }
        };
        menu.PopupHide += () => menu.CallDeferred(Node.MethodName.QueueFree);

        // Il mouse era catturato dallo sguardo: il menu si apre al centro dello schermo. Serve il
        // cursore per sceglierne una voce, quindi la schermata si apre insieme al menu.
        SetInventoryOpen(true);
        menu.Position = (Vector2I)(GetViewport().GetVisibleRect().Size * 0.5f);
        menu.Popup();
    }

    /// F breve: le casse si aprono nella colonna destra, gli oggetti sfusi si raccolgono.
    private void Interact(ItemPickup pickup)
    {
        if (pickup.Anchored)
            OpenLoot(pickup.Uid);
        else
            _inventory.SubmitPickup(pickup.Uid);
    }

    private void OpenLoot(int uid)
    {
        SetInventoryOpen(true);
        _screen?.OpenWorldContainer(uid);
    }

    // ====================================================================================
    //  Input
    // ====================================================================================

    // Usa _Input (non _UnhandledInput): "toggle_inventory" e' su Tab, che altrimenti verrebbe
    // consumato dalla navigazione focus della UI. Consumiamo solo le azioni riconosciute.
    public override void _Input(InputEvent @event)
    {
        // A menu di pausa aperto l'input di gioco non deve passare sotto la UI modale.
        if (!_isLocal || _game.UiModalOpen || _screen == null)
            return;

        if (@event.IsActionPressed("toggle_inventory"))
        {
            SetInventoryOpen(!InventoryOpen);
            GetViewport().SetInputAsHandled();
            return;
        }

        if (InventoryOpen)
        {
            HandleInventoryInput(@event);
            return;
        }

        HandleWorldInput(@event);
    }

    /// Tasti validi solo a schermata aperta. Chi scrive in un campo di testo (tag, quantita')
    /// tiene per se' i tasti: le scorciatoie tacciono.
    private void HandleInventoryInput(InputEvent @event)
    {
        // Esc: ultima finestra aperta, poi l'inventario (il menu di pausa a inventario aperto si astiene).
        if (@event.IsActionPressed("toggle_menu"))
        {
            if (!_screen!.HandleEscape())
                SetInventoryOpen(false);
            GetViewport().SetInputAsHandled();
            return;
        }

        if (GetViewport().GuiGetFocusOwner() is LineEdit)
            return;

        // R e' legato a DUE azioni: "rotate_item" qui e "reload" in WeaponInput. La disambiguazione
        // e' la visibilita' dell'inventario: a schermata chiusa l'evento resta unhandled e
        // raggiunge _UnhandledInput di WeaponInput.
        if (@event.IsActionPressed("rotate_item"))
        {
            _screen!.ToggleRotation();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event.IsActionPressed("quick_drop"))
        {
            _screen!.DiscardFocused();
            GetViewport().SetInputAsHandled();
            return;
        }

        // Tasti rapidi col cursore su una tile: collegano l'oggetto allo slot.
        for (int i = 0; i < PlayerInventoryModel.QuickSlotCount; i++)
        {
            if (!@event.IsActionPressed($"quick_slot_{PlayerInventoryModel.QuickSlotLabels[i]}"))
                continue;
            _screen!.BindFocusedToQuickSlot(i);
            _quickBar?.Flash(i);
            GetViewport().SetInputAsHandled();
            return;
        }
    }

    private void HandleWorldInput(InputEvent @event)
    {
        // F: distinzione pressione breve / prolungata.
        //
        // F e' condiviso col sistema veicoli (prendi/lascia il timone). Stesso meccanismo di R (skill
        // ui-hud §5): decidiamo QUI se il contesto piu' specifico e' il nostro e consumiamo l'evento
        // solo in quel caso; altrimenti resta unhandled e raggiunge VehicleInput._UnhandledInput.
        if (@event.IsActionPressed("interact"))
        {
            ItemPickup? target = FindTargetPickup(out float pickupDistance);
            _interactClaimed = target != null
                && !VehicleInteraction.VehicleWins(GetParent<PlayerController>(), pickupDistance);
            if (!_interactClaimed)
                return;

            _holdingInteract = true;
            _holdConsumed = false;
            _heldFor = 0f;
            _heldTarget = target;
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event.IsActionReleased("interact"))
        {
            if (!_interactClaimed)
                return;

            _interactClaimed = false;
            _holdingInteract = false;
            if (!_holdConsumed && _heldTarget != null && GodotObject.IsInstanceValid(_heldTarget))
                Interact(_heldTarget);
            _heldTarget = null;
            return;
        }

        // Tasti rapidi in gioco: usano l'oggetto collegato, se e' a portata di mano (rig, tasche,
        // slot speciali). Oggi "usare" consuma un consumabile; l'effetto arrivera' con la salute.
        for (int i = 0; i < PlayerInventoryModel.QuickSlotCount; i++)
        {
            if (!@event.IsActionPressed($"quick_slot_{PlayerInventoryModel.QuickSlotLabels[i]}"))
                continue;

            ItemInstance? bound = _inventory.Model.Find(_inventory.Model.QuickSlots[i]);
            if (bound != null && _inventory.Model.IsInQuickAccessArea(bound)
                && ItemCategories.IsA(bound.Definition.Category, ItemCategory.Consumable))
                _inventory.SubmitUse(bound.InstanceId);
            _quickBar?.Flash(i);
            GetViewport().SetInputAsHandled();
            return;
        }
    }

    /// Pickup che si sta guardando, entro il raggio di interazione (o null).
    private ItemPickup? FindTargetPickup() => FindTargetPickup(out _);

    /// <summary>
    /// Fra i pickup a portata sceglie quello piu' vicino all'asse dello sguardo, entro
    /// <see cref="LookAtCosine"/>. La PORTATA resta la distanza dal corpo, la stessa che l'host
    /// rivalida (<see cref="PlayerInventory.PickupRange"/>): lo sguardo sceglie solo QUALE oggetto,
    /// ed e' una decisione locale. Restituisce anche la distanza del candidato
    /// (<see cref="float.MaxValue"/> se non ce n'e'), per l'arbitraggio di F col timone.
    /// </summary>
    private ItemPickup? FindTargetPickup(out float distance)
    {
        distance = float.MaxValue;
        if (_camera == null)
            return null;

        Vector3 playerPos = GetParent<Node3D>().GlobalPosition;
        Vector3 eye = _camera.GlobalPosition;
        Vector3 look = -_camera.GlobalBasis.Z;

        ItemPickup? target = null;
        float bestAlignment = LookAtCosine;

        foreach (Node node in GetTree().GetNodesInGroup(ItemPickup.GroupName))
        {
            if (node is not ItemPickup pickup || pickup.IsQueuedForDeletion())
                continue;

            float d = pickup.GlobalPosition.DistanceTo(playerPos);
            if (d > PlayerInventory.PickupRange)
                continue;

            Vector3 toPickup = pickup.GlobalPosition + Vector3.Up * PickupCenterHeight - eye;
            float alignment = toPickup.Normalized().Dot(look);
            if (alignment > bestAlignment)
            {
                bestAlignment = alignment;
                target = pickup;
                distance = d;
            }
        }

        return target;
    }
}
