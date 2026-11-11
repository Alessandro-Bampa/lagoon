using Godot;

namespace Lagoon;

/// <summary>
/// HUD dell'inventario, istanziato SOLO per l'avatar locale (stesso criterio della camera in
/// <see cref="PlayerNetworkSync"/>). Crea il proprio <c>CanvasLayer</c> con la schermata inventario
/// e la hotbar, e traduce l'input in richieste. Nessuna logica di inventario qui.
///
/// E' anche il PROPRIETARIO DEL CURSORE: in gioco il mouse e' catturato dallo sguardo, e solo qui
/// si decide quando restituirlo alla UI (inventario aperto, modale aperta). Chi apre una UI non
/// tocca mai <c>Input.MouseMode</c>: alza il proprio stato e basta.
///
/// Interazione col mondo in stile Tarkov:
///  - GUARDANDO un oggetto a portata compare il prompt "Nome [F]" (bianco semitrasparente);
///  - <b>F</b> breve raccoglie l'oggetto, oppure APRE il contenitore se e' una cassa;
///  - <b>F</b> tenuto premuto apre il menu contestuale a terra (Raccogli / Esamina).
/// </summary>
public partial class PlayerHud : Node
{
    /// Quanto va tenuto premuto F per ottenere il menu contestuale invece della raccolta diretta.
    private const float HoldSeconds = 0.35f;

    /// Coseno dell'angolo massimo fra lo sguardo e un oggetto perche' lo si stia "guardando" (20°).
    private const float LookAtCosine = 0.94f;

    /// Altezza del centro di un oggetto a terra: si punta al cubo, non alla sua base.
    private const float PickupCenterHeight = 0.25f;

    private PlayerInventory _inventory = null!;
    private Camera3D? _camera;
    private GameManager _game = null!;
    private SettingsService? _settings;
    private InventoryScreen? _screen;
    private Hotbar? _hotbar;
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
    /// cursore appartiene al drag &amp; drop, quindi reticolo e input di combattimento tacciono, ed e'
    /// anche il discriminante che disambigua il tasto R (rotazione vs ricarica) — vedi
    /// <see cref="WeaponInput"/>.
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
        // coordinate LOGICHE, quindi il rect si adatta da solo anche quando cambia la scala UI
        // (ContentScaleFactor) -- vedi SettingsService.
        _hudRoot = new Control { TopLevel = true, MouseFilter = Control.MouseFilterEnum.Ignore };
        layer.AddChild(_hudRoot);

        _screen = new InventoryScreen(_inventory) { Visible = false };
        _hudRoot.AddChild(_screen);

        _hotbar = new Hotbar(_inventory);
        _hudRoot.AddChild(_hotbar);

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

    /// <summary>
    /// Aggiunge reticolo e pannello arma/salute (Fase 3). Vivono qui e non in una scena propria
    /// perche' condividono il <c>_hudRoot</c> gia' sincronizzato col viewport, ed esistono solo per
    /// l'avatar locale come tutto il resto della HUD.
    /// </summary>
    private void AddCombatHud()
    {
        Node parent = GetParent();
        var weapon = parent.GetNodeOrNull<WeaponController>("Weapon");
        var weaponInput = parent.GetNodeOrNull<WeaponInput>("WeaponInput");
        var camera = parent.GetNodeOrNull<FirstPersonCamera>("PlayerCamera");
        var health = parent.GetNodeOrNull<HealthComponent>("Health");

        if (weapon == null || health == null || _hudRoot == null)
            return;

        _hudRoot.AddChild(new WeaponHudPanel(weapon, health));

        if (weaponInput != null && camera != null)
            _hudRoot.AddChild(new CrosshairOverlay(weapon, weaponInput, camera, _game, this));
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
        _hotbar?.Refresh();
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
        if (_heldTarget != null)
            ShowGroundMenu(_heldTarget);
    }

    /// Mostra il prompt solo sull'oggetto che si sta guardando, e solo a chi gioca qui.
    private void UpdateWorldPrompt()
    {
        ItemPickup? nearest = FindTargetPickup();
        if (ReferenceEquals(nearest, _prompted))
            return;

        if (GodotObject.IsInstanceValid(_prompted))
            _prompted?.SetPromptVisible(false);

        _prompted = nearest;
        _prompted?.SetPromptVisible(true);
    }

    private void ShowGroundMenu(ItemPickup pickup)
    {
        var db = GetNodeOrNull<ItemDatabase>("/root/ItemDatabase");
        ItemDefinition? def = db?.Get(pickup.ItemId);
        if (def == null || _hudRoot == null)
            return;

        var menu = new PopupMenu();
        // Un PopupMenu costruito in codice non passa dall'auto-translate: le voci si traducono qui.
        menu.AddItem(Loc.T(pickup.Anchored ? "UI_HUD_GROUND_OPEN" : "UI_HUD_GROUND_PICKUP"), 0);
        menu.AddItem(Loc.T("UI_HUD_GROUND_EXAMINE"), 1);
        _hudRoot.AddChild(menu);
        SettingsService.ApplyToPopup(menu);

        int uid = pickup.Uid;
        bool anchored = pickup.Anchored;
        menu.IdPressed += id =>
        {
            if (id == 0)
                Interact(uid, anchored);
            else
                _screen?.OpenInspectWindow(new ItemInstance(uid, def) { StackCount = pickup.StackCount });
            menu.QueueFree();
        };

        // Il mouse era catturato dallo sguardo: il menu si apre al centro, dove c'e' il reticolo.
        menu.Position = (Vector2I)(GetViewport().GetVisibleRect().Size * 0.5f);
        menu.Popup();

        // Il menu e' un'azione da inventario: assicura che la schermata sia visibile per usarlo.
        if (_screen != null)
            _screen.Visible = true;
    }

    /// F breve: le casse si aprono nel pannello destro, gli oggetti sfusi si raccolgono.
    private void Interact(int uid, bool anchored)
    {
        if (anchored)
        {
            if (_screen != null)
            {
                _screen.Visible = true;
                _screen.OpenWorldContainer(uid);
            }
            return;
        }

        _inventory.SubmitMove(
            new ItemAddress(ItemAddress.RealmType.WorldLoose, uid), 0,
            ItemAddress.Pockets(), PlayerInventory.AutoPlace, 0, false);
    }

    // ====================================================================================
    //  Input
    // ====================================================================================

    // Usa _Input (non _UnhandledInput): "toggle_inventory" e' su Tab, che altrimenti verrebbe
    // consumato dalla navigazione focus della UI. Consumiamo solo le azioni riconosciute.
    public override void _Input(InputEvent @event)
    {
        // A menu di pausa aperto l'input di gioco non deve passare sotto la UI modale.
        if (!_isLocal || _game.UiModalOpen)
            return;

        if (@event.IsActionPressed("toggle_inventory"))
        {
            if (_screen != null)
                _screen.Visible = !_screen.Visible;
            GetViewport().SetInputAsHandled();
            return;
        }

        // R e' legato a DUE azioni: "rotate_item" qui e "reload" in WeaponInput. La disambiguazione
        // e' la visibilita' dell'inventario: consumiamo l'evento solo a schermata aperta, cosi' a
        // inventario chiuso resta unhandled e raggiunge _UnhandledInput di WeaponInput.
        if (InventoryOpen && @event.IsActionPressed("rotate_item"))
        {
            _screen?.ToggleRotation();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event.IsActionPressed("quick_drop"))
        {
            _screen?.QuickDropHovered();
            GetViewport().SetInputAsHandled();
            return;
        }

        // F: distinzione pressione breve / prolungata.
        //
        // F e' condiviso col sistema veicoli (prendi/lascia il timone). Stesso meccanismo di R (skill
        // ui-hud §4): decidiamo QUI se il contesto piu' specifico e' il nostro e consumiamo l'evento
        // solo in quel caso; altrimenti resta unhandled e raggiunge VehicleInput._UnhandledInput.
        // Effetto collaterale desiderabile: F a vuoto non viene piu' ingoiato.
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
                Interact(_heldTarget.Uid, _heldTarget.Anchored);
            _heldTarget = null;
            return;
        }

        for (int i = 0; i < PlayerInventoryModel.QuickSlotCount; i++)
        {
            if (@event.IsActionPressed($"quick_slot_{PlayerInventoryModel.QuickSlotLabels[i]}"))
            {
                // Col cursore su un item lo si assegna; altrimenti si seleziona lo slot.
                _screen?.AssignHoveredToQuickSlot(i);
                _hotbar?.SelectSlot(i);
                GetViewport().SetInputAsHandled();
                return;
            }
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
