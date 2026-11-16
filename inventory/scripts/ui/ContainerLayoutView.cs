using System.Collections.Generic;
using Godot;

namespace Lagoon;

/// <summary>
/// Layout disegnato di un contenitore: una <see cref="GridView"/> per ogni sotto-griglia, alla
/// posizione dichiarata in <see cref="ItemGridDef.Offset"/> (in celle). Se nessuna griglia dichiara
/// un offset le si dispone in fila, a capo oltre <see cref="AutoWrapCells"/> ("shelf packing").
/// </summary>
public partial class ContainerLayoutView : Control
{
    /// Larghezza oltre cui il layout automatico va a capo, in celle.
    private const float AutoWrapCells = 6f;

    /// Distanza fra tasche nel layout automatico, in celle.
    private const float AutoGapCells = 0.15f;

    private readonly InventoryScreen _screen;
    private readonly ItemInstance _container;
    private readonly int _space;

    public ContainerLayoutView(InventoryScreen screen, ItemInstance container, int space)
    {
        _screen = screen;
        _container = container;
        _space = space;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Ready()
    {
        int cell = InvTheme.Cell;
        IReadOnlyList<ItemGridDef> specs = _container.Definition.GridSpecs;
        Vector2[] offsets = ResolveOffsets(specs);

        Vector2 extent = Vector2.Zero;
        for (int i = 0; i < _container.Grids.Length && i < specs.Count; i++)
        {
            GridView view = GridView.ForGrid(_screen, _container.Grids[i], _space);
            AddChild(view);
            view.Position = (offsets[i] * cell).Round();

            extent = extent.Max(offsets[i] + new Vector2(specs[i].Columns, specs[i].Rows));
        }

        CustomMinimumSize = (extent * cell).Ceil();
        SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        SizeFlagsVertical = SizeFlags.ShrinkBegin;
    }

    private static Vector2[] ResolveOffsets(IReadOnlyList<ItemGridDef> specs)
    {
        var offsets = new Vector2[specs.Count];
        bool declared = false;
        for (int i = 0; i < specs.Count; i++)
        {
            offsets[i] = specs[i].Offset;
            declared |= specs[i].Offset != Vector2.Zero;
        }
        if (declared || specs.Count <= 1)
            return offsets;

        // Layout automatico a scaffali.
        float x = 0f, y = 0f, shelf = 0f;
        for (int i = 0; i < specs.Count; i++)
        {
            if (x > 0f && x + specs[i].Columns > AutoWrapCells)
            {
                x = 0f;
                y += shelf + AutoGapCells;
                shelf = 0f;
            }
            offsets[i] = new Vector2(x, y);
            x += specs[i].Columns + AutoGapCells;
            shelf = Mathf.Max(shelf, specs[i].Rows);
        }
        return offsets;
    }
}
