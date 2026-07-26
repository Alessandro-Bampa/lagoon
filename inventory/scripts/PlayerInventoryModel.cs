using System;
using System.Collections.Generic;
using Godot;
using GDArray = Godot.Collections.Array;
using GDDict = Godot.Collections.Dictionary;

namespace Lagoon;

/// <summary>
/// Modello autoritativo dell'inventario di UN giocatore (CLAUDE.md §3). Aggrega:
///  - 4 tasche (griglia 4x1 sempre presente);
///  - l'equipaggiamento (testa/torso/gambe/piedi/gilet/zaino + slot arma riservati);
///  - le griglie di gilet e zaino, che esistono solo se il relativo item e' equipaggiato;
///  - la hotbar (menu rapido) come riferimenti (InstanceId) a item "quick-usable".
///
/// Espone SOLO operazioni validate (spazio, tipo slot, peso, ciclo container, proprieta') ed e'
/// completamente C#-puro/testabile: il nodo <see cref="PlayerInventory"/> ci mette sopra il layer
/// di rete (RPC + serializzazione). Tutte le mutazioni vanno eseguite lato host; il client riceve
/// lo stato via <see cref="Deserialize"/> e non muta nulla localmente.
///
/// Indirizzamento delle griglie (per move via rete):
///  - <see cref="PocketsContainerId"/> (= -1): le tasche.
///  - qualsiasi altro id positivo: la griglia interna dell'item-container con quell'InstanceId.
/// Gli InstanceId sono positivi e assegnati dall'host, quindi non collidono con l'id delle tasche.
/// </summary>
public sealed class PlayerInventoryModel
{
    public const int PocketsContainerId = -1;
    public const int PocketColumns = 4;
    public const int PocketRows = 1;

    /// Slot rapidi: tasti 4-9 e 0 (i tasti 1-3 restano liberi per le armi, Fase 3).
    public static readonly string[] QuickSlotLabels = { "4", "5", "6", "7", "8", "9", "0" };
    public const int QuickSlotCount = 7;

    public const float MaxLoad = 40.0f;

    private EquipmentSet _equipment = new();
    private InventoryGrid _pockets = new(PocketColumns, PocketRows);
    private readonly int[] _quickSlots = new int[QuickSlotCount];

    // Contatore per gli InstanceId, avanza SOLO sull'host (unico a creare istanze).
    private int _nextInstanceId = 1;

    public EquipmentSet Equipment => _equipment;
    public InventoryGrid Pockets => _pockets;
    public IReadOnlyList<int> QuickSlots => _quickSlots;
    public float MaxLoadKg => MaxLoad;

    /// Griglia del gilet equipaggiato (null se nessun gilet).
    public InventoryGrid? VestGrid => _equipment.Get(EquipSlotType.Vest)?.ContainerGrid;

    /// Griglia dello zaino equipaggiato (null se nessuno zaino).
    public InventoryGrid? BackpackGrid => _equipment.Get(EquipSlotType.Backpack)?.ContainerGrid;

    /// Istanza per id (sola lettura, per la UI). Null se non presente.
    public ItemInstance? Find(int instanceId) => FindInstance(instanceId);

    /// Peso totale corrente (equipaggiamento + tasche, ricorsivo sui container).
    public float TotalWeight()
    {
        float weight = _pockets.TotalWeight();
        foreach (var kv in _equipment.All())
            weight += kv.Value.TotalWeight();
        return weight;
    }

    // ====================================================================================
    //  Operazioni di alto livello (host-side) — tutte validate
    // ====================================================================================

    /// <summary>
    /// Crea un'istanza dal tipo e la ripone (auto-equip se equipaggiabile e slot libero, altrimenti
    /// auto-stow nelle griglie). Rifiuta se supera il carico massimo o se non c'e' spazio.
    /// </summary>
    public bool TryPickup(ItemDefinition definition, int stackCount)
    {
        var item = new ItemInstance(_nextInstanceId, definition)
        {
            StackCount = Math.Max(1, stackCount),
        };

        if (TotalWeight() + item.TotalWeight() > MaxLoad)
            return false;

        bool placed;
        // Preferisci equipaggiare gli item equipaggiabili se lo slot e' libero (da' subito storage).
        if (definition.EquipSlot != EquipSlotType.None && _equipment.IsEmpty(definition.EquipSlot))
            placed = _equipment.Equip(definition.EquipSlot, item);
        else
            placed = TryAutoStore(item);

        if (!placed)
            return false;

        _nextInstanceId++;
        return true;
    }

    /// <summary>
    /// Sposta un item verso una griglia bersaglio in (x, y) con la rotazione data. La sorgente puo'
    /// essere una griglia qualsiasi o uno slot di equip (in tal caso e' un unequip-verso-griglia).
    /// Applica il controllo anti-ciclo se l'item e' un container.
    /// </summary>
    public bool TryMove(int instanceId, int targetContainerId, int x, int y, bool rotated)
    {
        ItemInstance? item = FindInstance(instanceId);
        if (item == null)
            return false;

        InventoryGrid? target = ResolveGrid(targetContainerId);
        if (target == null)
            return false;

        // Controllo anti-ciclo: un container non puo' finire dentro se stesso o un suo discendente.
        if (item.ContainerGrid != null && IsGridInsideContainer(target, item))
            return false;

        ItemLocation? source = FindLocation(instanceId);
        if (source == null)
            return false;

        int prevX = item.GridX, prevY = item.GridY;
        bool prevRot = item.Rotated;

        RemoveFrom(source.Value, instanceId);
        if (target.Place(item, x, y, rotated))
            return true;

        // Rollback: rimetti l'item esattamente dov'era.
        RestoreTo(source.Value, item, prevX, prevY, prevRot);
        return false;
    }

    /// Equipaggia in <paramref name="slot"/> un item che si trova in una griglia.
    public bool TryEquip(int instanceId, EquipSlotType slot)
    {
        ItemInstance? item = FindInstance(instanceId);
        if (item == null || item.Definition.EquipSlot != slot)
            return false;
        if (!_equipment.IsEmpty(slot))
            return false;

        ItemLocation? source = FindLocation(instanceId);
        if (source == null || source.Value.Grid == null)
            return false; // deve provenire da una griglia

        int prevX = item.GridX, prevY = item.GridY;
        bool prevRot = item.Rotated;

        source.Value.Grid.Remove(instanceId);
        if (_equipment.Equip(slot, item))
            return true;

        source.Value.Grid.Place(item, prevX, prevY, prevRot); // rollback
        return false;
    }

    /// Toglie un item equipaggiato e lo ripone nella prima griglia disponibile (auto-stow).
    public bool TryUnequip(int instanceId)
    {
        EquipSlotType slot = EquipSlotType.None;
        ItemInstance? item = null;
        foreach (var candidate in EquipmentSet.AllSlots)
        {
            var it = _equipment.Get(candidate);
            if (it != null && it.InstanceId == instanceId)
            {
                slot = candidate;
                item = it;
                break;
            }
        }
        if (item == null)
            return false;

        _equipment.Unequip(slot);
        if (TryAutoStore(item))
            return true;

        _equipment.Equip(slot, item); // rollback: nessuno spazio dove riporlo
        return false;
    }

    /// <summary>
    /// Ripone un'istanza GIA' COSTRUITA (col suo eventuale contenuto annidato), tipicamente
    /// importata dal mondo: auto-equip se lo slot combacia ed e' libero, altrimenti auto-stow.
    /// Il controllo di carico usa <see cref="ItemInstance.TotalWeight"/>, quindi tiene conto anche
    /// del contenuto di un container raccolto pieno.
    /// </summary>
    public bool TryStoreInstance(ItemInstance item)
    {
        if (TotalWeight() + item.TotalWeight() > MaxLoad)
            return false;

        EquipSlotType slot = item.Definition.EquipSlot;
        if (slot != EquipSlotType.None && _equipment.IsEmpty(slot))
            return _equipment.Equip(slot, item);

        return TryAutoStore(item);
    }

    /// <summary>
    /// Come <see cref="TryStoreInstance"/> ma in una cella precisa di una griglia bersaglio: usata
    /// dal loot per far atterrare l'item esattamente dove l'utente lo rilascia.
    /// </summary>
    public bool TryStoreInstanceAt(ItemInstance item, int targetContainerId, int x, int y, bool rotated)
    {
        if (TotalWeight() + item.TotalWeight() > MaxLoad)
            return false;

        InventoryGrid? target = ResolveGrid(targetContainerId);
        if (target == null)
            return false;

        // L'item viene da fuori: non puo' contenere la griglia bersaglio, ma la guardia costa poco
        // e protegge da payload malformati.
        if (item.ContainerGrid != null && IsGridInsideContainer(target, item))
            return false;

        return target.Place(item, x, y, rotated);
    }

    /// <summary>
    /// Rimuove un item dall'inventario (per il drop nel mondo) e restituisce l'istanza rimossa, o
    /// null se non trovata. Pulisce eventuali riferimenti nella hotbar.
    /// L'istanza restituita conserva la propria <see cref="ItemInstance.ContainerGrid"/>: il
    /// chiamante puo' serializzarla per intero, cosi' il contenuto sopravvive al drop.
    /// </summary>
    public ItemInstance? TryDrop(int instanceId)
    {
        ItemLocation? source = FindLocation(instanceId);
        ItemInstance? item = FindInstance(instanceId);
        if (source == null || item == null)
            return null;

        RemoveFrom(source.Value, instanceId);
        ClearQuickSlotReferences(instanceId);
        return item;
    }

    /// <summary>
    /// Assegna un item "quick-usable" a uno slot della hotbar. Regola Tarkov: l'item deve trovarsi
    /// nelle TASCHE o nel RIG (gilet) — non nello zaino, da cui non si estrae al volo.
    /// (L'uso effettivo dell'oggetto arrivera' con consumabili/armi.)
    /// </summary>
    public bool AssignQuickSlot(int slotIndex, int instanceId)
    {
        if (slotIndex < 0 || slotIndex >= _quickSlots.Length)
            return false;

        ItemInstance? item = FindInstance(instanceId);
        if (item == null || !item.Definition.QuickUsable || !IsInQuickAccessArea(instanceId))
            return false;

        _quickSlots[slotIndex] = instanceId;
        return true;
    }

    /// True se l'item sta nelle tasche o nella griglia del rig: le sole aree ad accesso rapido.
    public bool IsInQuickAccessArea(int instanceId)
    {
        if (_pockets.Contains(instanceId))
            return true;

        InventoryGrid? vest = VestGrid;
        return vest != null && vest.Contains(instanceId);
    }

    /// Griglia indirizzata da un containerId, per il motore di trasferimento. Null se inesistente.
    public InventoryGrid? GridFor(int containerId) => ResolveGrid(containerId);

    /// <summary>
    /// Unita' totali di un tipo di item presenti nelle GRIGLIE (tasche, rig, zaino). Gli slot di
    /// equipaggiamento sono esclusi di proposito: l'arma impugnata non e' munizione di riserva.
    /// Usata dal <see cref="WeaponController"/> per la riserva mostrata nella HUD.
    /// </summary>
    public int CountById(string itemId)
    {
        int total = 0;
        foreach (var grid in AllGrids())
            foreach (var item in grid.Items)
                if (item.Definition.ItemId == itemId)
                    total += item.StackCount;
        return total;
    }

    /// <summary>
    /// Consuma fino a <paramref name="max"/> unita' del tipo indicato dalle griglie e restituisce
    /// quante ne ha effettivamente rimosse (puo' essere meno del richiesto, o zero). Gli stack che
    /// si svuotano vengono rimossi insieme ai loro riferimenti nella hotbar.
    /// Solo host: e' una mutazione dello stato autoritativo.
    /// </summary>
    public int ConsumeById(string itemId, int max)
    {
        if (max <= 0)
            return 0;

        int remaining = max;
        var emptied = new List<int>();

        foreach (var grid in AllGrids())
        {
            foreach (var item in grid.Items)
            {
                if (remaining <= 0)
                    break;
                if (item.Definition.ItemId != itemId)
                    continue;

                int taken = Math.Min(item.StackCount, remaining);
                item.StackCount -= taken;
                remaining -= taken;
                if (item.StackCount <= 0)
                    emptied.Add(item.InstanceId);
            }
            if (remaining <= 0)
                break;
        }

        // Rimozione differita: mutare una griglia mentre la si itera invaliderebbe l'enumerazione.
        foreach (int id in emptied)
        {
            FindLocation(id)?.Grid?.Remove(id);
            ClearQuickSlotReferences(id);
        }

        return max - remaining;
    }

    /// Rimuove l'item dal punto in cui si trova (griglia o slot) e lo restituisce, senza altri effetti.
    /// Usato da <see cref="ItemTransfer"/>: il riposizionamento lo decide il chiamante.
    public ItemInstance? Extract(int instanceId)
    {
        ItemLocation? source = FindLocation(instanceId);
        ItemInstance? item = FindInstance(instanceId);
        if (source == null || item == null)
            return null;

        RemoveFrom(source.Value, instanceId);
        ClearQuickSlotReferences(instanceId);
        return item;
    }

    /// Equipaggia un'istanza gia' costruita nello slot indicato (validando il tipo di slot).
    public bool EquipInstance(ItemInstance item, EquipSlotType slot) => _equipment.Equip(slot, item);

    /// True se piazzare <paramref name="item"/> nella griglia creerebbe un ciclo di contenitori.
    public static bool WouldCreateCycle(ItemInstance item, InventoryGrid target)
        => item.ContainerGrid != null && IsGridInsideContainer(target, item);

    /// Peso totale se si aggiungesse l'item indicato (per la validazione del carico).
    public bool FitsLoad(ItemInstance item) => TotalWeight() + item.TotalWeight() <= MaxLoad;

    // ====================================================================================
    //  Localizzazione, risoluzione griglie, controllo anti-ciclo
    // ====================================================================================

    private readonly struct ItemLocation
    {
        // Esattamente uno dei due e' valorizzato: griglia oppure slot di equip.
        public readonly InventoryGrid? Grid;
        public readonly EquipSlotType Slot;

        public ItemLocation(InventoryGrid grid) { Grid = grid; Slot = EquipSlotType.None; }
        public ItemLocation(EquipSlotType slot) { Grid = null; Slot = slot; }
    }

    private ItemLocation? FindLocation(int instanceId)
    {
        foreach (var slot in EquipmentSet.AllSlots)
        {
            var it = _equipment.Get(slot);
            if (it != null && it.InstanceId == instanceId)
                return new ItemLocation(slot);
        }
        foreach (var grid in AllGrids())
            if (grid.Contains(instanceId))
                return new ItemLocation(grid);
        return null;
    }

    private void RemoveFrom(ItemLocation location, int instanceId)
    {
        if (location.Grid != null)
            location.Grid.Remove(instanceId);
        else
            _equipment.Unequip(location.Slot);
    }

    private void RestoreTo(ItemLocation location, ItemInstance item, int x, int y, bool rotated)
    {
        if (location.Grid != null)
            location.Grid.Place(item, x, y, rotated);
        else
            _equipment.Equip(location.Slot, item);
    }

    /// Griglia da un container-id di rete (vedi convenzione in testa alla classe).
    private InventoryGrid? ResolveGrid(int containerId)
    {
        if (containerId == PocketsContainerId)
            return _pockets;

        return FindInstance(containerId)?.ContainerGrid;
    }

    /// True se <paramref name="grid"/> e' la griglia di <paramref name="container"/> o di un suo
    /// discendente: base del controllo anti-ciclo (uno zaino non entra in se stesso).
    private static bool IsGridInsideContainer(InventoryGrid grid, ItemInstance container)
    {
        if (container.ContainerGrid == null)
            return false;
        if (ReferenceEquals(grid, container.ContainerGrid))
            return true;
        foreach (var child in container.ContainerGrid.Items)
            if (IsGridInsideContainer(grid, child))
                return true;
        return false;
    }

    private ItemInstance? FindInstance(int instanceId)
    {
        foreach (var item in AllInstances())
            if (item.InstanceId == instanceId)
                return item;
        return null;
    }

    private IEnumerable<ItemInstance> AllInstances()
    {
        foreach (var kv in _equipment.All())
            foreach (var it in Descend(kv.Value))
                yield return it;
        foreach (var it in _pockets.Items)
            foreach (var d in Descend(it))
                yield return d;
    }

    private static IEnumerable<ItemInstance> Descend(ItemInstance item)
    {
        yield return item;
        if (item.ContainerGrid != null)
            foreach (var child in item.ContainerGrid.Items)
                foreach (var d in Descend(child))
                    yield return d;
    }

    private IEnumerable<InventoryGrid> AllGrids()
    {
        yield return _pockets;
        foreach (var kv in _equipment.All())
            foreach (var g in GridsIn(kv.Value))
                yield return g;
        foreach (var it in _pockets.Items)
            foreach (var g in GridsIn(it))
                yield return g;
    }

    private static IEnumerable<InventoryGrid> GridsIn(ItemInstance item)
    {
        if (item.ContainerGrid == null)
            yield break;
        yield return item.ContainerGrid;
        foreach (var child in item.ContainerGrid.Items)
            foreach (var g in GridsIn(child))
                yield return g;
    }

    private bool TryAutoStore(ItemInstance item)
    {
        // Ordine di preferenza: zaino (piu' capiente), gilet, tasche.
        foreach (var grid in PreferredStorageGrids())
        {
            if (item.ContainerGrid != null && IsGridInsideContainer(grid, item))
                continue; // non riporre un container dentro se stesso
            if (grid.TryAutoPlace(item))
                return true;
        }
        return false;
    }

    private IEnumerable<InventoryGrid> PreferredStorageGrids()
    {
        if (BackpackGrid != null)
            yield return BackpackGrid;
        if (VestGrid != null)
            yield return VestGrid;
        yield return _pockets;
    }

    private void ClearQuickSlotReferences(int instanceId)
    {
        for (int i = 0; i < _quickSlots.Length; i++)
            if (_quickSlots[i] == instanceId)
                _quickSlots[i] = 0;
    }

    // ====================================================================================
    //  Serializzazione per la replica di rete (host -> proprietario)
    // ====================================================================================

    /// <summary>
    /// Serializza UN item (con il contenuto annidato) — formato usato anche come payload del
    /// pickup nel mondo, cosi' drop e replica condividono la stessa rappresentazione.
    /// </summary>
    public static GDDict SerializeItem(ItemInstance item) => SerializeInstance(item);

    /// <summary>
    /// Ricostruisce un item da un payload. Se <paramref name="allocateFreshIds"/> e' true gli
    /// InstanceId del payload vengono IGNORATI e riassegnati da QUESTO modello: indispensabile
    /// quando si importa dal mondo, dove gli id potrebbero collidere con quelli gia' presenti.
    /// </summary>
    public ItemInstance? DeserializeItem(GDDict data, Func<string, ItemDefinition?> resolve, bool allocateFreshIds)
    {
        Func<int>? allocator = allocateFreshIds ? () => _nextInstanceId++ : null;
        return DeserializeItemWith(data, resolve, allocator);
    }

    /// <summary>
    /// Variante con allocatore esplicito, per ricostruire alberi che NON appartengono a un modello
    /// giocatore (es. il payload di un pickup a terra, dove gli id devono essere unici solo dentro
    /// quel payload). Con <paramref name="allocateId"/> null conserva gli id del payload.
    /// </summary>
    public static ItemInstance? DeserializeItemWith(
        GDDict data, Func<string, ItemDefinition?> resolve, Func<int>? allocateId)
        => DeserializeInstance(data, resolve, allocateId);

    public GDDict Serialize()
    {
        var equipment = new GDDict();
        foreach (var kv in _equipment.All())
            equipment[((int)kv.Key).ToString()] = SerializeInstance(kv.Value);

        var quick = new GDArray();
        foreach (int id in _quickSlots)
            quick.Add(id);

        return new GDDict
        {
            { "pockets", SerializeGrid(_pockets) },
            { "equipment", equipment },
            { "quick", quick },
            { "next_id", _nextInstanceId },
        };
    }

    public void Deserialize(GDDict data, Func<string, ItemDefinition?> resolve)
    {
        _equipment = new EquipmentSet();
        _pockets = new InventoryGrid(PocketColumns, PocketRows);
        for (int i = 0; i < _quickSlots.Length; i++)
            _quickSlots[i] = 0;

        if (data.TryGetValue("pockets", out Variant pocketsVar))
            DeserializeGridInto(_pockets, pocketsVar.AsGodotDictionary(), resolve, allocateId: null);

        if (data.TryGetValue("equipment", out Variant equipVar))
        {
            var equipment = equipVar.AsGodotDictionary();
            foreach (var key in equipment.Keys)
            {
                var slot = (EquipSlotType)int.Parse(key.AsString());
                ItemInstance? item = DeserializeInstance(
                    equipment[key].AsGodotDictionary(), resolve, allocateId: null);
                if (item != null)
                    _equipment.Equip(slot, item);
            }
        }

        if (data.TryGetValue("quick", out Variant quickVar))
        {
            var quick = quickVar.AsGodotArray();
            for (int i = 0; i < _quickSlots.Length && i < quick.Count; i++)
                _quickSlots[i] = quick[i].AsInt32();
        }

        if (data.TryGetValue("next_id", out Variant nextVar))
            _nextInstanceId = nextVar.AsInt32();
    }

    private static GDDict SerializeInstance(ItemInstance item)
    {
        var dict = new GDDict
        {
            { "id", item.InstanceId },
            { "item", item.Definition.ItemId },
            { "x", item.GridX },
            { "y", item.GridY },
            { "rot", item.Rotated },
            { "stack", item.StackCount },
        };
        if (item.ContainerGrid != null)
            dict["grid"] = SerializeGrid(item.ContainerGrid);
        return dict;
    }

    private static GDDict SerializeGrid(InventoryGrid grid)
    {
        var items = new GDArray();
        foreach (var item in grid.Items)
            items.Add(SerializeInstance(item));
        return new GDDict
        {
            { "cols", grid.Columns },
            { "rows", grid.Rows },
            { "items", items },
        };
    }

    /// <paramref name="allocateId"/>: se non null, sostituisce l'id del payload con uno fresco
    /// (import dal mondo); se null, conserva gli id autoritativi dell'host (SyncFullState).
    private static ItemInstance? DeserializeInstance(
        GDDict dict, Func<string, ItemDefinition?> resolve, Func<int>? allocateId)
    {
        string itemId = dict["item"].AsString();
        ItemDefinition? def = resolve(itemId);
        if (def == null)
        {
            GD.PrintErr($"[Inventory] ItemId sconosciuto in deserializzazione: '{itemId}'");
            return null;
        }

        int instanceId = allocateId != null ? allocateId() : dict["id"].AsInt32();
        var item = new ItemInstance(instanceId, def)
        {
            StackCount = dict["stack"].AsInt32(),
        };

        if (item.ContainerGrid != null && dict.TryGetValue("grid", out Variant gridVar))
            DeserializeGridInto(item.ContainerGrid, gridVar.AsGodotDictionary(), resolve, allocateId);

        return item;
    }

    private static void DeserializeGridInto(
        InventoryGrid grid, GDDict gridDict, Func<string, ItemDefinition?> resolve, Func<int>? allocateId)
    {
        if (!gridDict.TryGetValue("items", out Variant itemsVar))
            return;

        foreach (var entry in itemsVar.AsGodotArray())
        {
            GDDict dict = entry.AsGodotDictionary();
            ItemInstance? item = DeserializeInstance(dict, resolve, allocateId);
            if (item == null)
                continue;
            grid.Place(item, dict["x"].AsInt32(), dict["y"].AsInt32(), dict["rot"].AsBool());
        }
    }
}
