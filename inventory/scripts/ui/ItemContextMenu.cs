using System;
using System.Collections.Generic;
using Godot;

namespace Lagoon;

/// <summary>
/// Menu contestuale (tasto destro) su un item. Compaiono solo le voci pertinenti all'oggetto;
/// quelle pertinenti ma al momento impossibili restano grigie, col motivo nel tooltip.
/// Ogni voce si traduce in una richiesta all'host: qui non si decide nulla.
///
/// E' un <c>PopupMenu</c>, cioe' una <c>Window</c> a parte: non eredita scala ne' auto-translate
/// (skill ui-hud §1), quindi le voci si traducono a mano e la scala la applica chi lo apre.
/// </summary>
public partial class ItemContextMenu : PopupMenu
{
    private readonly InventoryScreen _screen;
    private readonly ItemInstance _item;
    private readonly int _space;
    private readonly List<Action> _actions = new();

    public ItemContextMenu(InventoryScreen screen, ItemInstance item, int space)
    {
        _screen = screen;
        _item = item;
        _space = space;
    }

    public override void _Ready()
    {
        ItemDefinition def = _item.Definition;
        PlayerInventory inv = _screen.Inventory;
        bool mine = _space == ItemAddress.PlayerSpace;
        bool movable = InventoryRules.CanTake(_screen.Spaces, _space, _item) == InvError.Ok;
        int id = _item.InstanceId;

        Add(this, "UI_INV_CTX_INSPECT", () => _screen.OpenInspectWindow(_item));

        if (_item.IsContainer || _item.SlotItems.Length > 0)
            Add(this, "UI_INV_CTX_OPEN", () => _screen.OpenItem(_item, _space));
        else if (def.UnpackYields.Length > 0)
            Add(this, "UI_INV_CTX_UNPACK", () => inv.SubmitUnpack(id), mine, "UI_INV_CTX_WHY_NOT_MINE");

        if (ItemCategories.IsA(def.Category, ItemCategory.Consumable))
            Add(this, "UI_INV_CTX_USE", () => inv.SubmitUse(id), mine, "UI_INV_CTX_WHY_NOT_MINE");

        if (EquipRules.IsEquippable(def) && movable)
        {
            if (mine && _item.EquippedIn != EquipSlotType.None)
                Add(this, "UI_INV_CTX_UNEQUIP", () => inv.SubmitQuickMove(_space, id, 0));
            else
                Add(this, "UI_INV_CTX_EQUIP", () => inv.SubmitQuickEquip(_space, id));
        }

        AddAmmoEntries(inv, id);

        if (mine && def.QuickUsable)
        {
            var bind = new PopupMenu();
            for (int i = 0; i < PlayerInventoryModel.QuickSlotCount; i++)
            {
                int slot = i;
                string key = Loc.KeyFor($"quick_slot_{PlayerInventoryModel.QuickSlotLabels[i]}");
                Add(bind, Loc.T("UI_INV_CTX_BIND_SLOT", key), () => inv.SubmitAssignQuickSlot(slot, id), translated: true);
            }
            AddSubmenu(bind, "UI_INV_CTX_BIND");
        }

        if (_item.IsContainer)
        {
            Add(this, "UI_INV_CTX_TAG", () => _screen.OpenTagDialog(_item, _space));
            if (_item.TagName.Length > 0)
                Add(this, "UI_INV_CTX_TAG_REMOVE", () => inv.SubmitTag(_space, id, "", 0));
        }

        if (mine)
        {
            AddSeparator();
            Add(this, "UI_INV_CTX_DISCARD", () => _screen.Discard(_item, _space), movable, "UI_INV_ERR_FIXED");
        }
    }

    /// Voci "Carica munizioni ►" e "Scarica munizioni" per caricatori e armi con caricatore.
    private void AddAmmoEntries(PlayerInventory inv, int id)
    {
        ItemInstance? magazine = InventoryRules.MagazineOf(_item);
        if (magazine?.Definition is not MagazineDefinition magDef)
            return;

        // Tipi di munizione compatibili presenti nel proprio inventario, col totale di ciascuno.
        var totals = new Dictionary<AmmoDefinition, int>();
        foreach (ItemInstance candidate in _screen.Model.AllItems())
            if (candidate.Definition is AmmoDefinition ammo && ammo.Caliber == magDef.Caliber)
                totals[ammo] = totals.GetValueOrDefault(ammo) + candidate.StackCount;

        bool full = magazine.RoundsFree <= 0;
        if (totals.Count == 0 || full)
        {
            Add(this, "UI_INV_CTX_LOAD_AMMO", () => { }, enabled: false,
                whyKey: full ? "UI_INV_ERR_MAGFULL" : "UI_INV_CTX_WHY_NO_AMMO");
        }
        else
        {
            var load = new PopupMenu();
            foreach (var (ammo, total) in totals)
            {
                string ammoId = ammo.ItemId;
                Add(load, Loc.T("UI_INV_CTX_LOAD_AMMO_LINE", ammo.DisplayName, total),
                    () => inv.SubmitLoadAmmo(_space, id, ammoId), translated: true);
            }
            AddSubmenu(load, "UI_INV_CTX_LOAD_AMMO");
        }

        Add(this, "UI_INV_CTX_UNLOAD_AMMO", () => inv.SubmitUnloadAmmo(_space, id),
            magazine.Rounds.Count > 0, "UI_INV_CTX_WHY_EMPTY");
    }

    /// Aggiunge una voce. L'id della voce e' l'indice dell'azione: i sottomenu condividono la lista.
    private void Add(
        PopupMenu menu, string label, Action action, bool enabled = true, string whyKey = "",
        bool translated = false)
    {
        int actionId = _actions.Count;
        _actions.Add(action);

        menu.AddItem(translated ? label : Loc.T(label), actionId);
        int index = menu.GetItemIndex(actionId);
        menu.SetItemDisabled(index, !enabled);
        if (!enabled && whyKey.Length > 0)
            menu.SetItemTooltip(index, Loc.T(whyKey));
    }

    private void AddSubmenu(PopupMenu submenu, string labelKey)
    {
        submenu.IdPressed += OnIdPressed;
        AddChild(submenu);
        SettingsService.ApplyToPopup(submenu);
        AddSubmenuNodeItem(Loc.T(labelKey), submenu);
    }

    public void Open(Vector2 globalPosition)
    {
        IdPressed += OnIdPressed;
        // Liberato alla chiusura, anche se si clicca fuori senza scegliere.
        PopupHide += () => CallDeferred(Node.MethodName.QueueFree);
        SettingsService.ApplyToPopup(this);
        Position = (Vector2I)globalPosition;
        Popup();
    }

    private void OnIdPressed(long id)
    {
        if (id >= 0 && id < _actions.Count)
            _actions[(int)id]();
    }
}
