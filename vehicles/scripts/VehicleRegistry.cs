using Godot;

namespace Lagoon;

/// <summary>
/// Lookup dei veicoli. Nessuno stato: solo ricerche sull'albero.
///
/// Si cerca per <see cref="BoatController.VehicleId"/> — l'id REPLICATO — e non per NodePath ne' per
/// nome del nodo, per la stessa ragione per cui i pickup si cercano per <see cref="ItemPickup.Uid"/>:
/// l'id e' l'unica identita' valida su tutti i peer.
/// </summary>
public static class VehicleRegistry
{
    /// Veicolo con l'id dato (o null). L'id 0 significa "nessun veicolo" e non viene mai cercato.
    public static BoatController? Find(Node context, int vehicleId)
    {
        if (vehicleId == 0)
            return null;

        foreach (Node node in context.GetTree().GetNodesInGroup(BoatController.GroupName))
            if (node is BoatController boat && boat.VehicleId == vehicleId && !boat.IsQueuedForDeletion())
                return boat;
        return null;
    }

    /// Veicolo pilotato dal peer dato (o null). Si legge lo stato replicato, valido su ogni peer.
    public static BoatController? FindByPilot(Node context, int peerId)
    {
        if (peerId == 0)
            return null;

        foreach (Node node in context.GetTree().GetNodesInGroup(BoatController.GroupName))
            if (node is BoatController boat && boat.PilotPeerId == peerId && !boat.IsQueuedForDeletion())
                return boat;
        return null;
    }

    /// <summary>
    /// Veicolo il cui timone e' il piu' vicino a <paramref name="from"/> entro la sua
    /// <see cref="BoatController.HelmRange"/> (o null). <paramref name="distance"/> riceve la distanza
    /// dal timone trovato, oppure <see cref="float.MaxValue"/> se non c'e' nessun candidato.
    /// </summary>
    public static BoatController? NearestHelm(Node context, Vector3 from, out float distance)
    {
        BoatController? nearest = null;
        distance = float.MaxValue;

        foreach (Node node in context.GetTree().GetNodesInGroup(BoatController.GroupName))
        {
            if (node is not BoatController boat || boat.IsQueuedForDeletion())
                continue;

            float d = boat.HelmGlobalPosition.DistanceTo(from);
            if (d <= boat.HelmRange && d < distance)
            {
                distance = d;
                nearest = boat;
            }
        }

        if (nearest == null)
            distance = float.MaxValue;
        return nearest;
    }

    /// <summary>
    /// Avatar del peer dato (o null). Serve all'host per validare un intento contro la posizione
    /// REPLICATA del richiedente invece che contro un dato arrivato dal client.
    /// </summary>
    public static PlayerController? FindPlayer(Node context, int peerId)
    {
        var world = context.GetTree().GetFirstNodeInGroup(GameWorld.GroupName) as GameWorld;
        return world?.FindPlayer(peerId);
    }
}
