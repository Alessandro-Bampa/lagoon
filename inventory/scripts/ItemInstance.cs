using System.Collections.Generic;

namespace Lagoon;

/// Un gruppo di cartucce uguali dentro un caricatore.
public sealed class AmmoStack
{
    public AmmoDefinition Ammo { get; }
    public int Count { get; set; }

    public AmmoStack(AmmoDefinition ammo, int count)
    {
        Ammo = ammo;
        Count = count;
    }
}

/// <summary>
/// Istanza runtime di un oggetto (a differenza di <see cref="ItemDefinition"/>, che e' il TIPO).
/// C# puro: nessun nodo Godot.
///
/// Un'istanza e' un nodo di un ALBERO: puo' avere griglie (<see cref="Grids"/>), slot
/// (<see cref="SlotItems"/>) e, se e' un caricatore, una pila di cartucce (<see cref="Rounds"/>).
/// I campi di collocazione (<see cref="Parent"/>, <see cref="ParentGrid"/>, <see cref="ParentSlot"/>,
/// <see cref="EquippedIn"/>) li scrivono SOLO <see cref="InventoryGrid"/>, <see cref="SetSlot"/> e
/// <see cref="PlayerInventoryModel"/>: chi sposta un item passa da li'.
///
/// Gli <see cref="InstanceId"/> sono unici in tutta la sessione (li alloca solo l'host, da
/// <see cref="ItemDatabase.AllocateInstanceId"/>): un'istanza passa dall'inventario al mondo e
/// viceversa senza cambiare identita'.
/// </summary>
public sealed class ItemInstance
{
    public int InstanceId { get; }
    public ItemDefinition Definition { get; }

    // Posizione (celle) nella griglia contenitrice; significativa solo dentro una griglia.
    public int GridX { get; set; }
    public int GridY { get; set; }
    public bool Rotated { get; set; }

    public int StackCount { get; set; } = 1;

    /// Etichetta e colore assegnati dal giocatore a un contenitore (vuoto = nessun tag).
    public string TagName { get; set; } = "";
    public int TagColor { get; set; }

    /// Sotto-griglie (vuoto se non e' un contenitore), una per <see cref="ItemDefinition.GridSpecs"/>.
    public InventoryGrid[] Grids { get; }

    /// Contenuto degli slot dell'oggetto, uno per <see cref="ItemDefinition.Slots"/>.
    public ItemInstance?[] SlotItems { get; }

    /// Cartucce di un caricatore, dal fondo alla cima: l'ULTIMO elemento e' il prossimo colpo.
    public List<AmmoStack> Rounds { get; } = new();

    // --- collocazione nell'albero -------------------------------------------------------

    /// Item che contiene questo (null se sta in uno slot del personaggio o e' la radice nel mondo).
    public ItemInstance? Parent { get; internal set; }

    /// Indice della griglia del genitore che lo ospita, o -1.
    public int ParentGrid { get; internal set; } = -1;

    /// Indice dello slot del genitore che lo ospita, o -1.
    public int ParentSlot { get; internal set; } = -1;

    /// Slot del personaggio in cui e' indossato (None se non e' al primo livello del giocatore).
    public EquipSlotType EquippedIn { get; internal set; } = EquipSlotType.None;

    public ItemInstance(int instanceId, ItemDefinition definition)
    {
        InstanceId = instanceId;
        Definition = definition;

        IReadOnlyList<ItemGridDef> specs = definition.GridSpecs;
        Grids = new InventoryGrid[specs.Count];
        for (int i = 0; i < specs.Count; i++)
            Grids[i] = new InventoryGrid(specs[i].Columns, specs[i].Rows, this, i);

        SlotItems = new ItemInstance?[definition.Slots.Count];
    }

    // --- ingombro -----------------------------------------------------------------------

    // Punto unico in cui il modding fara' crescere l'arma con gli attacchi (dimensione dinamica).
    public int Width => Definition.Width;
    public int Height => Definition.Height;

    public int OccupiedWidth => Rotated ? Height : Width;
    public int OccupiedHeight => Rotated ? Width : Height;

    public bool IsContainer => Grids.Length > 0;

    // --- slot dell'oggetto --------------------------------------------------------------

    /// Monta (o smonta con null) un figlio nello slot, aggiornandone la collocazione.
    public void SetSlot(int index, ItemInstance? child)
    {
        ItemInstance? previous = SlotItems[index];
        // Solo se e' ancora collocato qui: negli scambi il vecchio figlio puo' essere gia' altrove.
        if (previous != null && ReferenceEquals(previous.Parent, this) && previous.ParentSlot == index)
        {
            previous.Parent = null;
            previous.ParentSlot = -1;
        }

        SlotItems[index] = child;
        if (child != null)
        {
            child.Parent = this;
            child.ParentGrid = -1;
            child.ParentSlot = index;
            child.EquippedIn = EquipSlotType.None;
        }
    }

    /// Indice del pozzetto del caricatore, o -1 se l'oggetto non ne ha.
    public int MagazineSlotIndex
    {
        get
        {
            for (int i = 0; i < Definition.Slots.Count; i++)
                if (Definition.Slots[i].Kind == ItemSlotKind.Magazine)
                    return i;
            return -1;
        }
    }

    /// Caricatore montato (null se manca o se l'oggetto non e' un'arma).
    public ItemInstance? MountedMagazine
    {
        get
        {
            int index = MagazineSlotIndex;
            return index >= 0 ? SlotItems[index] : null;
        }
    }

    // --- caricatore ---------------------------------------------------------------------

    public int RoundCount
    {
        get
        {
            int total = 0;
            foreach (AmmoStack stack in Rounds)
                total += stack.Count;
            return total;
        }
    }

    /// Posti liberi (0 se non e' un caricatore).
    public int RoundsFree => Definition is MagazineDefinition mag ? mag.Capacity - RoundCount : 0;

    /// Inserisce cartucce in cima; due inserimenti consecutivi dello stesso tipo si fondono.
    public void PushRounds(AmmoDefinition ammo, int count)
    {
        if (count <= 0)
            return;
        if (Rounds.Count > 0 && Rounds[^1].Ammo == ammo)
            Rounds[^1].Count += count;
        else
            Rounds.Add(new AmmoStack(ammo, count));
    }

    /// Toglie la cartuccia in cima (il prossimo colpo). Null se il caricatore e' vuoto.
    public AmmoDefinition? PopRound()
    {
        if (Rounds.Count == 0)
            return null;
        AmmoStack top = Rounds[^1];
        top.Count--;
        if (top.Count <= 0)
            Rounds.RemoveAt(Rounds.Count - 1);
        return top.Ammo;
    }

    // --- albero -------------------------------------------------------------------------

    /// True se non contiene nulla (griglie, slot e cartucce vuoti).
    public bool IsEmpty
    {
        get
        {
            foreach (InventoryGrid grid in Grids)
                if (grid.Items.Count > 0)
                    return false;
            foreach (ItemInstance? child in SlotItems)
                if (child != null)
                    return false;
            return Rounds.Count == 0;
        }
    }

    /// Radice dell'albero a cui appartiene (se' stesso se non ha genitore).
    public ItemInstance Root
    {
        get
        {
            ItemInstance current = this;
            while (current.Parent != null)
                current = current.Parent;
            return current;
        }
    }

    /// True se questo item e' <paramref name="ancestor"/> o sta (a qualunque profondita') dentro di esso.
    public bool IsInside(ItemInstance ancestor)
    {
        for (ItemInstance? current = this; current != null; current = current.Parent)
            if (ReferenceEquals(current, ancestor))
                return true;
        return false;
    }

    /// Peso totale ricorsivo: stack, cartucce, contenuto delle griglie, oggetti negli slot.
    public float TotalWeight()
    {
        float weight = Definition.Weight * StackCount;
        foreach (AmmoStack stack in Rounds)
            weight += stack.Ammo.Weight * stack.Count;
        foreach (InventoryGrid grid in Grids)
            weight += grid.TotalWeight();
        foreach (ItemInstance? child in SlotItems)
            if (child != null)
                weight += child.TotalWeight();
        return weight;
    }
}
