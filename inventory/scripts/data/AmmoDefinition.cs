using Godot;

namespace Lagoon;

/// Cartuccia sfusa (impilabile). Il calibro decide in quali caricatori entra.
[GlobalClass]
public partial class AmmoDefinition : ItemDefinition
{
    [Export] public string Caliber { get; set; } = "";
}
