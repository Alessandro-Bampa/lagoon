using Godot;

namespace Lagoon;

/// <summary>
/// Trova lo <see cref="Skeleton3D"/> del personaggio a partire da un nodo del rig.
///
/// Serve perche' lo scheletro arriva da <c>Body_Base.glb</c>: la sua posizione esatta nell'albero
/// dipende da come l'importatore ha strutturato il file, quindi un <c>NodePath</c> scritto a mano si
/// romperebbe a ogni rigenerazione dell'asset (vedi la skill <c>blender-pipeline</c>). Cercarlo per
/// TIPO fra i fratelli e' l'unico appiglio stabile.
///
/// LIMITE DICHIARATO: se un avatar avesse due scheletri, prende il primo che incontra.
/// </summary>
public static class SkeletonLocator
{
    /// Cerca fra i fratelli di <paramref name="node"/> e nei loro sottoalberi.
    public static Skeleton3D? Find(Node node)
    {
        Node? parent = node.GetParent();
        if (parent == null)
            return null;

        foreach (Node child in parent.GetChildren())
        {
            Skeleton3D? found = SearchDown(child);
            if (found != null)
                return found;
        }

        return null;
    }

    private static Skeleton3D? SearchDown(Node node)
    {
        if (node is Skeleton3D skeleton)
            return skeleton;

        foreach (Node child in node.GetChildren())
        {
            Skeleton3D? found = SearchDown(child);
            if (found != null)
                return found;
        }

        return null;
    }
}
