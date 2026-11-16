using System.Collections.Generic;

namespace Lagoon;

/// <summary>
/// Visite su un albero di <see cref="ItemInstance"/>: l'inventario di un giocatore e il payload di
/// un oggetto nel mondo hanno la stessa forma, quindi condividono queste funzioni. C# puro.
/// </summary>
public static class ItemTree
{
    /// Enumera l'item e tutti i suoi discendenti (griglie e slot).
    public static IEnumerable<ItemInstance> Descend(ItemInstance root)
    {
        yield return root;
        foreach (InventoryGrid grid in root.Grids)
            foreach (ItemInstance child in grid.Items)
                foreach (ItemInstance d in Descend(child))
                    yield return d;
        foreach (ItemInstance? child in root.SlotItems)
            if (child != null)
                foreach (ItemInstance d in Descend(child))
                    yield return d;
    }

    /// Istanza con l'id dato all'interno dell'albero (radice inclusa), o null.
    public static ItemInstance? Find(ItemInstance root, int instanceId)
    {
        foreach (ItemInstance item in Descend(root))
            if (item.InstanceId == instanceId)
                return item;
        return null;
    }

    /// <summary>
    /// Stacca l'item dal genitore (griglia o slot). Non riguarda gli slot del personaggio ne' le
    /// radici nel mondo: di quelli si occupa chi possiede la radice.
    /// </summary>
    public static bool DetachFromParent(ItemInstance item)
    {
        ItemInstance? parent = item.Parent;
        if (parent == null)
            return false;

        if (item.ParentGrid >= 0)
            return parent.Grids[item.ParentGrid].Remove(item);

        if (item.ParentSlot >= 0)
        {
            parent.SetSlot(item.ParentSlot, null);
            return true;
        }
        return false;
    }
}
