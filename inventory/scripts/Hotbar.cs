using System.Collections.Generic;
using Godot;

namespace Lagoon;

/// <summary>
/// Barra rapida (menu rapido) sempre visibile in basso allo schermo, con
/// <see cref="PlayerInventoryModel.QuickSlotCount"/> slot. Rispecchia i riferimenti quick della
/// modello; la (ri)assegnazione avviene per drag &amp; drop di item "quick-usable". La selezione con
/// i tasti 1..5 evidenzia solo lo slot (l'uso e' rimandato alle fasi successive).
/// </summary>
public partial class Hotbar : Control
{
    public PlayerInventory Inventory { get; }

    private HBoxContainer _row = null!;
    private readonly List<HotbarSlotView> _slots = new();
    private int _selected = -1;

    public Hotbar(PlayerInventory inventory)
    {
        Inventory = inventory;
    }

    public override void _Ready()
    {
        // Copre lo schermo ma lascia passare il mouse (solo i figli lo intercettano).
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        // Fascia in basso a tutta larghezza; gli slot vengono centrati dall'HBox (Alignment.Center).
        _row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        _row.MouseFilter = MouseFilterEnum.Pass; // le aree vuote della fascia non bloccano il gioco
        _row.AddThemeConstantOverride("separation", 6);
        AddChild(_row);
        _row.SetAnchorsPreset(LayoutPreset.BottomWide);
        _row.OffsetTop = -(HotbarSlotView.SlotSize + 12);
        _row.OffsetBottom = -12;

        Refresh();
    }

    public void Refresh()
    {
        foreach (Node child in _row.GetChildren())
        {
            _row.RemoveChild(child);
            child.QueueFree();
        }
        _slots.Clear();

        IReadOnlyList<int> quick = Inventory.Model.QuickSlots;
        for (int i = 0; i < quick.Count; i++)
        {
            ItemInstance? assigned = quick[i] != 0 ? Inventory.Model.Find(quick[i]) : null;
            var slot = new HotbarSlotView(i, assigned, this);
            _row.AddChild(slot);
            _slots.Add(slot);
            slot.SetSelected(i == _selected);
        }
    }

    /// Evidenzia lo slot selezionato (tasti 1..5). Nessun effetto di gioco in Fase 2.
    public void SelectSlot(int index)
    {
        if (index < 0 || index >= _slots.Count)
            return;
        _selected = index;
        for (int i = 0; i < _slots.Count; i++)
            _slots[i].SetSelected(i == _selected);
    }
}
