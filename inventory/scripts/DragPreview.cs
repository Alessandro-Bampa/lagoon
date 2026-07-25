using Godot;

namespace Lagoon;

/// <summary>
/// Anteprima di trascinamento: segue il cursore (Godot posiziona il preview sul mouse) restando
/// CENTRATA su di esso, e si aggiorna in tempo reale quando l'utente ruota con R
/// (<see cref="InventoryScreen.PendingRotated"/>) — icona inclusa, non solo la scatola.
/// </summary>
public partial class DragPreview : Control
{
    private readonly InventoryScreen _screen;
    private readonly ItemDefinition _definition;
    private readonly int _stackCount;

    private Control? _visual;
    private bool _builtRotated;

    public DragPreview(InventoryScreen screen, ItemDefinition definition, int stackCount)
    {
        _screen = screen;
        _definition = definition;
        _stackCount = stackCount;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Ready()
    {
        SetProcess(true);
        Rebuild();
    }

    public override void _Process(double delta)
    {
        if (_screen.PendingRotated != _builtRotated)
            Rebuild();
    }

    private void Rebuild()
    {
        _builtRotated = _screen.PendingRotated;

        if (_visual != null)
        {
            RemoveChild(_visual);
            _visual.QueueFree();
        }

        _visual = ItemVisual.Build(_definition, _builtRotated, _stackCount);
        _visual.Modulate = new Color(1f, 1f, 1f, 0.75f);
        AddChild(_visual);
        // Il preview e' ancorato al mouse in alto-sinistra: spostiamo la grafica di -meta' per centrarla.
        _visual.Position = -_visual.Size / 2f;
    }
}
