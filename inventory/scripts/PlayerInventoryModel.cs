using System;
using System.Collections.Generic;
using Godot;
using GDArray = Godot.Collections.Array;
using GDDict = Godot.Collections.Dictionary;

namespace Lagoon;

/// <summary>
/// Stato dell'inventario di UN giocatore (CLAUDE.md §3): gli slot di equipaggiamento — ognuno
/// radice di un albero di <see cref="ItemInstance"/> — e gli slot rapidi. Non c'e' altro: anche le
/// tasche sono un item (categoria <c>Pockets</c>) indossato nel proprio slot, con quattro griglie
/// 1x1.
///
/// E' una STRUTTURA DATI, non un motore di regole: le mutazioni validate vivono in
/// <see cref="InventoryOps"/> e girano solo sull'host. Il client riceve lo stato via
/// <see cref="Deserialize"/> e non muta nulla.
/// </summary>
public sealed class PlayerInventoryModel
{
    /// Slot rapidi: tasti 4-9 e 0 (i tasti 1-3 sono delle armi).
    public static readonly string[] QuickSlotLabels = { "4", "5", "6", "7", "8", "9", "0" };
    public const int QuickSlotCount = 7;

    private readonly Dictionary<EquipSlotType, ItemInstance> _equip = new();
    private readonly int[] _quickSlots = new int[QuickSlotCount];

    public IReadOnlyList<int> QuickSlots => _quickSlots;

    // ====================================================================================
    //  Slot di equipaggiamento
    // ====================================================================================

    public ItemInstance? Get(EquipSlotType slot) => _equip.GetValueOrDefault(slot);

    /// Indossa l'item nello slot (nessuna validazione: e' compito di <see cref="EquipRules"/>).
    public void SetEquip(EquipSlotType slot, ItemInstance item)
    {
        _equip[slot] = item;
        item.Parent = null;
        item.ParentGrid = -1;
        item.ParentSlot = -1;
        item.EquippedIn = slot;
    }

    public void ClearEquip(EquipSlotType slot)
    {
        if (_equip.Remove(slot, out ItemInstance? item) && item.EquippedIn == slot)
            item.EquippedIn = EquipSlotType.None;
    }

    // ====================================================================================
    //  Visite
    // ====================================================================================

    /// Tutte le istanze possedute, a qualunque profondita'.
    public IEnumerable<ItemInstance> AllItems()
    {
        foreach (EquipSlotType slot in EquipRules.AllSlots)
        {
            if (!_equip.TryGetValue(slot, out ItemInstance? root))
                continue;
            foreach (ItemInstance item in ItemTree.Descend(root))
                yield return item;
        }
    }

    public ItemInstance? Find(int instanceId)
    {
        if (instanceId == 0)
            return null;
        foreach (ItemInstance item in AllItems())
            if (item.InstanceId == instanceId)
                return item;
        return null;
    }

    /// True se l'istanza appartiene a questo inventario.
    public bool Owns(ItemInstance item)
    {
        ItemInstance root = item.Root;
        return root.EquippedIn != EquipSlotType.None && ReferenceEquals(Get(root.EquippedIn), root);
    }

    /// <summary>
    /// Oggetti "a portata di mano": il contenuto diretto di rig e tasche. Sono i soli da cui si
    /// usa uno slot rapido e da cui si prende un caricatore per ricaricare.
    /// </summary>
    public IEnumerable<ItemInstance> QuickAccessItems()
    {
        foreach (EquipSlotType slot in new[] { EquipSlotType.TacticalRig, EquipSlotType.Pockets })
        {
            ItemInstance? container = Get(slot);
            if (container == null)
                continue;
            foreach (InventoryGrid grid in container.Grids)
                foreach (ItemInstance item in grid.Items)
                    yield return item;
        }
    }

    public bool IsInQuickAccessArea(ItemInstance item)
    {
        if (EquipRules.IsSpecial(item.EquippedIn))
            return true;
        ItemInstance? parent = item.Parent;
        return parent != null && item.ParentGrid >= 0
            && parent.EquippedIn is EquipSlotType.TacticalRig or EquipSlotType.Pockets;
    }

    // ====================================================================================
    //  Peso
    // ====================================================================================

    public float TotalWeight()
    {
        float weight = 0f;
        foreach (ItemInstance item in _equip.Values)
            weight += item.TotalWeight();
        return weight;
    }

    /// <summary>
    /// Fattore di velocita' di movimento dovuto al carico: 1 sotto la prima soglia, poi scende
    /// linearmente fino alla soglia critica e ancora fino a quella massima.
    /// </summary>
    public static float SpeedFactorFor(float weightKg)
    {
        InventoryConfig cfg = InventoryConfig.Current;
        if (weightKg <= cfg.OverweightStartKg)
            return 1f;
        if (weightKg <= cfg.OverweightCriticalKg)
        {
            float t = (weightKg - cfg.OverweightStartKg) / (cfg.OverweightCriticalKg - cfg.OverweightStartKg);
            return Mathf.Lerp(1f, cfg.CriticalSpeedFactor, t);
        }
        float u = Mathf.Clamp(
            (weightKg - cfg.OverweightCriticalKg) / (cfg.OverweightMaxKg - cfg.OverweightCriticalKg), 0f, 1f);
        return Mathf.Lerp(cfg.CriticalSpeedFactor, cfg.MaxSpeedFactor, u);
    }

    /// Oltre la soglia critica non si corre.
    public static bool CanSprintWith(float weightKg) => weightKg <= InventoryConfig.Current.OverweightCriticalKg;

    // ====================================================================================
    //  Slot rapidi
    // ====================================================================================

    public void SetQuickSlot(int index, int instanceId)
    {
        // Un oggetto sta in un solo slot rapido alla volta.
        for (int i = 0; i < _quickSlots.Length; i++)
            if (_quickSlots[i] == instanceId)
                _quickSlots[i] = 0;
        _quickSlots[index] = instanceId;
    }

    /// Scollega dagli slot rapidi l'item e tutto cio' che contiene (sta lasciando l'inventario).
    public void ClearQuickSlotReferences(ItemInstance leaving)
    {
        foreach (ItemInstance item in ItemTree.Descend(leaving))
            for (int i = 0; i < _quickSlots.Length; i++)
                if (_quickSlots[i] == item.InstanceId)
                    _quickSlots[i] = 0;
    }

    // ====================================================================================
    //  Serializzazione per la replica (host -> proprietario)
    // ====================================================================================

    public GDDict Serialize()
    {
        var equipment = new GDDict();
        foreach (var kv in _equip)
            equipment[((int)kv.Key).ToString()] = ItemCodec.Serialize(kv.Value);

        var quick = new GDArray();
        foreach (int id in _quickSlots)
            quick.Add(id);

        return new GDDict { { "equipment", equipment }, { "quick", quick } };
    }

    public void Deserialize(GDDict data, Func<string, ItemDefinition?> resolve)
    {
        _equip.Clear();
        Array.Clear(_quickSlots);

        if (data.TryGetValue("equipment", out Variant equipVar))
        {
            GDDict equipment = equipVar.AsGodotDictionary();
            foreach (Variant key in equipment.Keys)
            {
                var slot = (EquipSlotType)int.Parse(key.AsString());
                ItemInstance? item = ItemCodec.Deserialize(equipment[key].AsGodotDictionary(), resolve);
                if (item != null)
                    SetEquip(slot, item);
            }
        }

        if (data.TryGetValue("quick", out Variant quickVar))
        {
            GDArray quick = quickVar.AsGodotArray();
            for (int i = 0; i < _quickSlots.Length && i < quick.Count; i++)
                _quickSlots[i] = quick[i].AsInt32();
        }
    }
}
