using Godot;

namespace Lagoon;

/// <summary>
/// Caricatore: un item che contiene una pila ORDINATA di cartucce (LIFO: l'ultima inserita e' la
/// prima sparata). Sottoclasse e non campi opzionali sulla base, come <c>WeaponDefinition</c>
/// (CLAUDE.md §5).
/// </summary>
[GlobalClass]
public partial class MagazineDefinition : ItemDefinition
{
    [Export] public string Caliber { get; set; } = "";
    [Export] public int Capacity { get; set; } = 30;
}
