using Godot;

namespace Lagoon;

/// Stato del trascinamento in corso. La sorgente NON viene tolta dal modello: resta al suo posto,
/// attenuata, finche' il rilascio non produce una richiesta all'host.
public sealed class DragState
{
    public ItemInstance Item { get; }

    /// Spazio da cui proviene l'item (0 = inventario, altrimenti uid nel mondo).
    public int Space { get; }

    /// Orientamento con cui verrebbe piazzato (R lo alterna).
    public bool Rotated { get; set; }

    /// Punto in cui l'oggetto e' stato afferrato, come frazione (0..1) della tile.
    public Vector2 Grab { get; set; }

    public DragState(ItemInstance item, int space, bool rotated, Vector2 grab)
    {
        Item = item;
        Space = space;
        Rotated = rotated;
        Grab = grab;
    }

    public int FootW => Rotated ? Item.Height : Item.Width;
    public int FootH => Rotated ? Item.Width : Item.Height;
}

public enum DropKind
{
    None,
    Cell,
    OnItem,
    Slot,
    QuickSlot,
    Ground,
}

/// Cosa farebbe il rilascio nel punto corrente, e se l'host lo accetterebbe.
public struct DropResolution
{
    public DropKind Kind;
    public InvError Error;
    public ItemAddress Dest;
    public int X;
    public int Y;
    public int TargetSpace;
    public int TargetId;
    public int QuickIndex;

    public readonly bool Valid => Kind != DropKind.None && Error == InvError.Ok;

    public readonly bool SameAs(in DropResolution other)
        => Kind == other.Kind && Error == other.Error && Dest.Equals(other.Dest)
           && X == other.X && Y == other.Y && TargetId == other.TargetId && QuickIndex == other.QuickIndex;
}

/// <summary>
/// Un bersaglio di rilascio (griglia, slot, slot rapido). Non si usa il drag &amp; drop integrato di
/// Godot: servono la rotazione durante il trascinamento, i modificatori al rilascio e i bersagli
/// "oggetto" con priorita', quindi il bersaglio lo risolve <see cref="InventoryScreen"/>.
/// </summary>
public interface IDropTarget
{
    Rect2 GetGlobalRect();
    bool IsVisibleInTree();

    /// Cosa succederebbe rilasciando qui.
    DropResolution Resolve(DragState drag, Vector2 globalMouse);

    /// Mostra l'anteprima (footprint verde/rosso, bordo del bersaglio); null la toglie.
    void SetPreview(DragState? drag, DropResolution resolution);

    void QueueRedraw();
}
