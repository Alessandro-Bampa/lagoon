using System.Collections.Generic;

namespace Lagoon;

/// <summary>
/// Griglia di inventario: una matrice Columns x Rows in cui gli item occupano un rettangolo di
/// celle in base a ingombro e rotazione. C# puro.
///
/// La mappa di occupazione (<see cref="_cells"/>) memorizza in ogni cella l'InstanceId dell'item
/// che la occupa (0 = libera): le collisioni si verificano in O(area). E' una cache derivata
/// dall'elenco <see cref="Items"/>, sempre ricostruibile.
///
/// Appartiene sempre a un item (<see cref="Owner"/>): piazzare e rimuovere aggiornano la
/// collocazione dell'istanza, cosi' ogni item sa dove si trova senza ricerche.
/// </summary>
public sealed class InventoryGrid
{
    public int Columns { get; }
    public int Rows { get; }

    /// Item contenitore proprietario della griglia (null solo per le griglie virtuali della UI).
    public ItemInstance? Owner { get; }

    /// Indice della griglia fra quelle del proprietario.
    public int Index { get; }

    private readonly List<ItemInstance> _items = new();
    private readonly int[] _cells;

    public InventoryGrid(int columns, int rows, ItemInstance? owner = null, int index = 0)
    {
        Columns = columns;
        Rows = rows;
        Owner = owner;
        Index = index;
        _cells = new int[columns * rows];
    }

    public IReadOnlyList<ItemInstance> Items => _items;

    private int CellIndex(int x, int y) => y * Columns + x;

    /// True se il rettangolo sta nella griglia ed e' libero, ignorando fino a due istanze (la
    /// sorgente di uno spostamento in-place e, negli scambi, l'oggetto che le fa posto).
    public bool CanPlaceSize(int width, int height, int x, int y, int ignoreId = 0, int ignoreId2 = 0)
    {
        if (!InBounds(width, height, x, y))
            return false;

        for (int yy = y; yy < y + height; yy++)
        {
            for (int xx = x; xx < x + width; xx++)
            {
                int occupant = _cells[CellIndex(xx, yy)];
                if (occupant != 0 && occupant != ignoreId && occupant != ignoreId2)
                    return false;
            }
        }
        return true;
    }

    public bool InBounds(int width, int height, int x, int y)
        => x >= 0 && y >= 0 && x + width <= Columns && y + height <= Rows;

    /// Piazza l'item (aggiornandone posizione, rotazione e collocazione). False se non c'e' spazio.
    public bool Place(ItemInstance item, int x, int y, bool rotated)
    {
        int w = rotated ? item.Height : item.Width;
        int h = rotated ? item.Width : item.Height;
        if (!CanPlaceSize(w, h, x, y))
            return false;

        item.GridX = x;
        item.GridY = y;
        item.Rotated = rotated;
        item.Parent = Owner;
        item.ParentGrid = Index;
        item.ParentSlot = -1;
        item.EquippedIn = EquipSlotType.None;

        for (int yy = y; yy < y + h; yy++)
            for (int xx = x; xx < x + w; xx++)
                _cells[CellIndex(xx, yy)] = item.InstanceId;

        _items.Add(item);
        return true;
    }

    /// Rimuove l'item liberando le sue celle. False se non presente.
    public bool Remove(ItemInstance item)
    {
        if (!_items.Remove(item))
            return false;

        for (int i = 0; i < _cells.Length; i++)
            if (_cells[i] == item.InstanceId)
                _cells[i] = 0;

        item.Parent = null;
        item.ParentGrid = -1;
        return true;
    }

    /// Item che occupa la cella (x, y), o null se libera / fuori griglia.
    public ItemInstance? ItemAt(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Columns || y >= Rows)
            return null;
        int id = _cells[CellIndex(x, y)];
        if (id == 0)
            return null;
        foreach (ItemInstance item in _items)
            if (item.InstanceId == id)
                return item;
        return null;
    }

    /// <summary>
    /// Primo spazio libero per un ingombro <paramref name="width"/> x <paramref name="height"/>.
    /// Scansione deterministica per righe, dall'alto a sinistra; per ogni cella prova prima
    /// l'orientamento base e poi quello ruotato.
    /// </summary>
    public bool TryFindFreeSpot(
        int width, int height, out int x, out int y, out bool rotated, int ignoreId = 0, int ignoreId2 = 0)
    {
        int orientations = width != height ? 2 : 1;
        for (int yy = 0; yy < Rows; yy++)
        {
            for (int xx = 0; xx < Columns; xx++)
            {
                for (int i = 0; i < orientations; i++)
                {
                    bool rot = i == 1;
                    int w = rot ? height : width;
                    int h = rot ? width : height;
                    if (CanPlaceSize(w, h, xx, yy, ignoreId, ignoreId2))
                    {
                        x = xx;
                        y = yy;
                        rotated = rot;
                        return true;
                    }
                }
            }
        }
        x = y = 0;
        rotated = false;
        return false;
    }

    /// Cerca il primo spazio libero e vi piazza l'item.
    public bool TryAutoPlace(ItemInstance item)
        => TryFindFreeSpot(item.Width, item.Height, out int x, out int y, out bool rotated)
           && Place(item, x, y, rotated);

    public float TotalWeight()
    {
        float weight = 0f;
        foreach (ItemInstance item in _items)
            weight += item.TotalWeight();
        return weight;
    }
}
