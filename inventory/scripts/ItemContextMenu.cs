using Godot;

namespace Lagoon;

/// <summary>
/// Menu contestuale (tasto destro) su un item. Le voci disponibili dipendono dall'oggetto e da dove
/// si trova; le azioni si traducono nelle stesse operazioni gia' esistenti (spostamento fra
/// <see cref="ItemAddress"/>, uso, apertura pacchetto).
///
/// Le voci legate alle armi (scarico caricatore, svuota munizioni, piega calcio, smonta moduli)
/// compaiono ma sono DISABILITATE: la Fase 3 (skill combat-shooting) ha introdotto il tiro e le
/// munizioni, ma il caricatore vive come stato host-side dentro <see cref="WeaponController"/>
/// e non come item
/// manipolabile in griglia. Allegati e durabilita' restano fuori dal prototipo (CLAUDE.md §7).
/// </summary>
public partial class ItemContextMenu : PopupMenu
{
    private enum Action
    {
        Use,
        Inspect,
        Examine,
        Equip,
        Unpack,
        Discard,
        UnloadMagazine,
        UnloadAmmo,
        FoldStock,
    }

    private readonly InventoryScreen _screen;
    private readonly ItemInstance _item;
    private readonly ItemAddress _source;

    public ItemContextMenu(InventoryScreen screen, ItemInstance item, ItemAddress source)
    {
        _screen = screen;
        _item = item;
        _source = source;
    }

    public override void _Ready()
    {
        ItemDefinition def = _item.Definition;
        bool inInventory = _source.IsPlayer;

        AddEntry("Usa", Action.Use, def.Category == ItemCategory.Consumable && inInventory);
        AddEntry("Ispeziona", Action.Inspect, true);
        AddEntry("Esamina", Action.Examine, true);

        if (def.EquipSlot != EquipSlotType.None)
            AddEntry("Equipaggia", Action.Equip, inInventory);

        if (!string.IsNullOrEmpty(def.UnpackYields))
            AddEntry("Apri pacchetto", Action.Unpack, inInventory);

        AddEntry("Getta a terra", Action.Discard, inInventory);

        // --- ganci Fase 3: visibili ma non attivi -------------------------------------
        if (def.Category == ItemCategory.Weapon)
        {
            AddSeparator();
            AddEntry("Scarica caricatore", Action.UnloadMagazine, false);
            AddEntry("Svuota munizioni", Action.UnloadAmmo, false);
            AddEntry("Piega calcio", Action.FoldStock, false);
        }

        IdPressed += OnIdPressed;
    }

    private void AddEntry(string label, Action action, bool enabled)
    {
        AddItem(label, (int)action);
        int index = GetItemIndex((int)action);
        SetItemDisabled(index, !enabled);
        if (!enabled && action is Action.UnloadMagazine or Action.UnloadAmmo or Action.FoldStock)
            SetItemTooltip(index, "Disponibile con il sistema armi (Fase 3).");
    }

    private void OnIdPressed(long id)
    {
        switch ((Action)id)
        {
            case Action.Use:
                _screen.Inventory.SubmitUse(_item.InstanceId);
                break;

            case Action.Inspect:
            case Action.Examine:
                // "Esamina" coincide con la scheda dettagli: l'XP da scoperta richiede il sistema
                // di progressione del personaggio, non previsto nel prototipo.
                _screen.OpenInspectWindow(_item);
                break;

            case Action.Equip:
                _screen.QuickEquip(_item, _source);
                break;

            case Action.Unpack:
                _screen.Inventory.SubmitUnpack(_item.InstanceId);
                break;

            case Action.Discard:
                _screen.Inventory.SubmitMove(
                    _source, _item.InstanceId, ItemAddress.Ground(),
                    PlayerInventory.AutoPlace, 0, false);
                break;
        }

        QueueFree();
    }
}
