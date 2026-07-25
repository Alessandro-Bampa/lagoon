using System.Collections.Generic;
using Godot;

namespace Lagoon;

/// <summary>
/// Finestra pop-up che mostra la griglia interna di un contenitore (rig, zaino, cassa), aperta con
/// doppio click. Se ne possono tenere aperte piu' di una e trascinarle sullo schermo per spostare
/// oggetti da un contenitore all'altro, come nel riferimento.
///
/// La griglia dentro e' la solita <see cref="GridPanelView"/>, indirizzata al container: quindi
/// drag&amp;drop, rotazione, evidenziazione, menu contestuale e scorciatoie funzionano identici.
///
/// Il contenuto viene RIRISOLTO dall'indirizzo a ogni aggiornamento di stato: la finestra non tiene
/// mai un albero deserializzato stale dopo che l'host ha applicato una modifica.
/// </summary>
public partial class ContainerWindow : FloatingWindow
{
    /// Indirizzo della griglia mostrata: identifica la finestra ed e' la chiave di aggiornamento.
    public ItemAddress GridAddress { get; }

    private readonly InventoryScreen _screen;

    public ContainerWindow(InventoryScreen screen, string title, ItemAddress gridAddress)
        : base(title)
    {
        _screen = screen;
        GridAddress = gridAddress;
    }

    protected override void BuildContent()
    {
        InventoryGrid? grid = _screen.ResolveGrid(GridAddress);
        if (grid == null)
        {
            // Il contenitore non esiste piu' (raccolto, saccheggiato o fuori portata).
            QueueFree();
            return;
        }

        // I contenitori annidati sono a loro volta apribili con doppio click.
        var openable = new HashSet<int>();
        foreach (var child in grid.Items)
            if (child.ContainerGrid != null)
                openable.Add(child.InstanceId);

        Body.AddChild(new GridPanelView(_screen, grid, GridAddress, openable: openable));
    }

    /// Ricostruisce il contenuto dallo stato corrente (chiamata dalla schermata a ogni Rebuild).
    public void Refresh() => RebuildBody();
}
