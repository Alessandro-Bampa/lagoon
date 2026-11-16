using Godot;

namespace Lagoon;

/// <summary>
/// Una delle sotto-griglie di un contenitore (una tasca di un rig, lo scomparto di uno zaino).
/// Un oggetto non puo' stare a cavallo di due sotto-griglie. <see cref="Offset"/> e' la posizione
/// nel layout disegnato del contenitore, in celle (ammesse frazioni per distanziare le tasche):
/// serve solo alla UI.
/// </summary>
[GlobalClass]
public partial class ItemGridDef : Resource
{
    [Export] public int Columns { get; set; } = 1;
    [Export] public int Rows { get; set; } = 1;

    /// Posizione nel layout del contenitore, in celle.
    [Export] public Vector2 Offset { get; set; }

    /// Filtro proprio di questa griglia (null = vale quello del contenitore).
    [Export] public ItemFilter? Filter { get; set; }
}
