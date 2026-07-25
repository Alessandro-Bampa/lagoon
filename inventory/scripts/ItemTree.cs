using System.Collections.Generic;

namespace Lagoon;

/// <summary>
/// Operazioni su un albero di <see cref="ItemInstance"/> che NON appartiene a un
/// <see cref="PlayerInventoryModel"/>: serve per il payload di un pickup a terra (un item radice
/// con il proprio contenuto annidato), che l'host deserializza, modifica e ri-serializza.
/// C# puro, nessuna dipendenza da nodi.
/// </summary>
public static class ItemTree
{
    /// Enumera l'item e tutti i suoi discendenti.
    public static IEnumerable<ItemInstance> Descend(ItemInstance root)
    {
        yield return root;
        if (root.ContainerGrid == null)
            yield break;
        foreach (var child in root.ContainerGrid.Items)
            foreach (var d in Descend(child))
                yield return d;
    }

    /// Istanza con l'id dato all'interno dell'albero (radice inclusa), o null.
    public static ItemInstance? Find(ItemInstance root, int instanceId)
    {
        foreach (var item in Descend(root))
            if (item.InstanceId == instanceId)
                return item;
        return null;
    }

    /// <summary>
    /// Rimuove dall'albero il sotto-item con l'id dato e lo restituisce. Non puo' rimuovere la
    /// radice (non ha una griglia contenitrice): in quel caso ritorna null.
    /// </summary>
    public static ItemInstance? Extract(ItemInstance root, int instanceId)
    {
        foreach (var owner in Descend(root))
        {
            InventoryGrid? grid = owner.ContainerGrid;
            if (grid == null)
                continue;

            foreach (var child in grid.Items)
            {
                if (child.InstanceId != instanceId)
                    continue;
                grid.Remove(instanceId);
                return child;
            }
        }
        return null;
    }

    /// Griglia del container con l'id dato dentro l'albero (radice inclusa), o null.
    public static InventoryGrid? FindGrid(ItemInstance root, int containerInstanceId)
        => Find(root, containerInstanceId)?.ContainerGrid;

    /// Id massimo presente nell'albero: base per allocare id nuovi e unici dentro questo payload.
    public static int MaxId(ItemInstance root)
    {
        int max = 0;
        foreach (var item in Descend(root))
            if (item.InstanceId > max)
                max = item.InstanceId;
        return max;
    }
}
