using System.Collections.Generic;

namespace Lagoon;

/// <summary>
/// Griglia di inventario stile Tarkov (core Fase 2): una matrice Columns x Rows in cui gli item
/// occupano un rettangolo di celle in base alla loro dimensione (ed eventuale rotazione). C# puro,
/// nessuna dipendenza da nodi Godot.
///
/// La mappa di occupazione (<see cref="_cells"/>) memorizza in ogni cella l'InstanceId dell'item
/// che la occupa (0 = libera), cosi' le collisioni si verificano in O(area) senza test rettangolo-
/// rettangolo tra tutti gli item.
/// </summary>
public sealed class InventoryGrid
{
    public int Columns { get; }
    public int Rows { get; }

    private readonly List<ItemInstance> _items = new();
    private readonly int[] _cells;

    public InventoryGrid(int columns, int rows)
    {
        Columns = columns;
        Rows = rows;
        _cells = new int[columns * rows];
    }

    public IReadOnlyList<ItemInstance> Items => _items;

    private int CellIndex(int x, int y) => y * Columns + x;

    /// <summary>
    /// Verifica se <paramref name="item"/> puo' essere piazzato con angolo in alto-sinistra in
    /// (<paramref name="x"/>, <paramref name="y"/>) con la rotazione data. <paramref name="ignoreInstanceId"/>
    /// consente di ignorare le celle gia' occupate dall'item stesso (utile per un riposizionamento
    /// in-place senza rimuoverlo prima).
    /// </summary>
    public bool CanPlace(ItemInstance item, int x, int y, bool rotated, int ignoreInstanceId = 0)
    {
        int w = rotated ? item.Definition.Height : item.Definition.Width;
        int h = rotated ? item.Definition.Width : item.Definition.Height;
        return CanPlaceSize(w, h, x, y, ignoreInstanceId);
    }

    /// <summary>
    /// Variante per dimensioni gia' calcolate (celle occupate), senza bisogno di un
    /// <see cref="ItemInstance"/>: la UI la usa per validare il drop di item che non appartengono a
    /// questo modello (es. loot da un pickup a terra).
    /// </summary>
    public bool CanPlaceSize(int width, int height, int x, int y, int ignoreInstanceId = 0)
    {
        if (x < 0 || y < 0 || x + width > Columns || y + height > Rows)
            return false;

        for (int yy = y; yy < y + height; yy++)
        {
            for (int xx = x; xx < x + width; xx++)
            {
                int occupant = _cells[CellIndex(xx, yy)];
                if (occupant != 0 && occupant != ignoreInstanceId)
                    return false;
            }
        }
        return true;
    }

    /// Piazza l'item (aggiornandone posizione/rotazione). Ritorna false se non c'e' spazio.
    public bool Place(ItemInstance item, int x, int y, bool rotated)
    {
        if (!CanPlace(item, x, y, rotated))
            return false;

        item.GridX = x;
        item.GridY = y;
        item.Rotated = rotated;

        int w = item.OccupiedWidth;
        int h = item.OccupiedHeight;
        for (int yy = y; yy < y + h; yy++)
            for (int xx = x; xx < x + w; xx++)
                _cells[CellIndex(xx, yy)] = item.InstanceId;

        _items.Add(item);
        return true;
    }

    /// Rimuove l'item con l'id dato liberando le sue celle. Ritorna false se non presente.
    public bool Remove(int instanceId)
    {
        int index = _items.FindIndex(i => i.InstanceId == instanceId);
        if (index < 0)
            return false;

        for (int i = 0; i < _cells.Length; i++)
            if (_cells[i] == instanceId)
                _cells[i] = 0;

        _items.RemoveAt(index);
        return true;
    }

    public bool Contains(int instanceId) => _items.Exists(i => i.InstanceId == instanceId);

    /// Item che occupa la cella (x, y), o null se libera / fuori griglia. Usato dalla UI per capire
    /// quale item si sta iniziando a trascinare.
    public ItemInstance? ItemAt(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Columns || y >= Rows)
            return null;
        int id = _cells[CellIndex(x, y)];
        return id == 0 ? null : _items.Find(i => i.InstanceId == id);
    }

    /// <summary>
    /// Cerca il primo spazio libero (prima senza rotazione, poi ruotato) e vi piazza l'item.
    /// Ritorna true se riuscito. Usato per l'auto-stow di pickup e unequip.
    /// </summary>
    public bool TryAutoPlace(ItemInstance item)
    {
        bool squared = item.Definition.Width == item.Definition.Height;
        for (int rot = 0; rot < 2; rot++)
        {
            bool rotated = rot == 1;
            if (rotated && squared)
                break; // ruotare un quadrato non aggiunge posizioni

            int w = rotated ? item.Definition.Height : item.Definition.Width;
            int h = rotated ? item.Definition.Width : item.Definition.Height;
            for (int y = 0; y + h <= Rows; y++)
                for (int x = 0; x + w <= Columns; x++)
                    if (CanPlace(item, x, y, rotated))
                        return Place(item, x, y, rotated);
        }
        return false;
    }

    /// Peso totale ricorsivo di tutti gli item contenuti (inclusi i container annidati).
    public float TotalWeight()
    {
        float weight = 0f;
        foreach (var item in _items)
            weight += item.TotalWeight();
        return weight;
    }
}
