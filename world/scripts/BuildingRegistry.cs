using Godot;

namespace Lagoon;

/// <summary>
/// Lookup degli edifici. Nessuno stato: solo ricerche sull'albero, sullo stampo di
/// <see cref="VehicleRegistry"/>.
///
/// Non c'e' un id replicato come per veicoli e pickup, e non serve: un <see cref="BuildingVolume"/>
/// e' geometria statica del livello, identica su tutti i peer, e non viene mai nominata in una RPC.
/// </summary>
public static class BuildingRegistry
{
    /// <summary>
    /// Edificio che contiene <paramref name="feetPos"/>, con il piano in cui ci si trova, oppure
    /// null. Il primo che risponde vince: gli edifici non si compenetrano.
    /// </summary>
    public static BuildingVolume? FindContaining(
        Node context, Vector3 feetPos, float slack, out int floorIndex)
    {
        floorIndex = -1;

        foreach (Node node in context.GetTree().GetNodesInGroup(BuildingVolume.GroupName))
        {
            if (node is not BuildingVolume building || building.IsQueuedForDeletion())
                continue;

            int index = building.FloorIndexAt(feetPos, slack);
            if (index < 0)
                continue;

            floorIndex = index;
            return building;
        }

        return null;
    }
}
