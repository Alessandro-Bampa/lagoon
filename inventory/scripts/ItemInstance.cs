namespace Lagoon;

/// <summary>
/// Istanza runtime di un oggetto in un inventario (a differenza di <see cref="ItemDefinition"/>, che
/// e' il TIPO). Ha un <see cref="InstanceId"/> univoco assegnato dall'host, una posizione nella
/// griglia che la contiene, un flag di rotazione e — se e' un container — una propria
/// <see cref="ContainerGrid"/> annidata.
///
/// E' C# puro (nessun nodo Godot): tutta la logica di inventario resta testabile e agnostica alla
/// scena. La replica di rete la serializza in un <c>Dictionary</c> (vedi <see cref="PlayerInventoryModel"/>).
/// </summary>
public sealed class ItemInstance
{
    /// Id univoco nel modello del giocatore, assegnato dall'host. Chiave per move/equip/drop via RPC.
    public int InstanceId { get; }

    public ItemDefinition Definition { get; }

    // Posizione (celle) nella griglia contenitrice; significativa solo quando l'item e' in una griglia.
    public int GridX { get; set; }
    public int GridY { get; set; }
    public bool Rotated { get; set; }

    public int StackCount { get; set; } = 1;

    /// Griglia interna se l'item e' un container (zaino/gilet); null altrimenti.
    public InventoryGrid? ContainerGrid { get; }

    /// Larghezza/altezza effettivamente occupate, tenendo conto della rotazione.
    public int OccupiedWidth => Rotated ? Definition.Height : Definition.Width;
    public int OccupiedHeight => Rotated ? Definition.Width : Definition.Height;

    public bool IsContainer => ContainerGrid != null;

    public ItemInstance(int instanceId, ItemDefinition definition)
    {
        InstanceId = instanceId;
        Definition = definition;

        if (definition.IsContainer && definition.ContainerColumns > 0 && definition.ContainerRows > 0)
            ContainerGrid = new InventoryGrid(definition.ContainerColumns, definition.ContainerRows);
    }

    /// Peso totale ricorsivo: il proprio stack piu' l'intero contenuto dell'eventuale container.
    public float TotalWeight()
    {
        float weight = Definition.Weight * StackCount;
        if (ContainerGrid != null)
            weight += ContainerGrid.TotalWeight();
        return weight;
    }
}
