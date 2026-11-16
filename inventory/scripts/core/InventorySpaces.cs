using Godot;

namespace Lagoon;

/// <summary>
/// Risolve gli "spazi" in cui vivono gli item dal punto di vista di UN giocatore: il suo
/// inventario (spazio 0) e gli oggetti nel mondo entro la sua portata (spazio = uid).
///
/// La usano sia l'host (per validare ed eseguire) sia la UI del proprietario (per colorare le
/// anteprime con le STESSE regole): un oggetto fuori portata semplicemente non si risolve.
/// </summary>
public sealed class InventorySpaces
{
    private readonly Node3D _player;
    private readonly float _reach;

    public PlayerInventoryModel Player { get; }

    public InventorySpaces(Node3D player, PlayerInventoryModel model, float reach)
    {
        _player = player;
        Player = model;
        _reach = reach;
    }

    public Vector3 PlayerPosition => _player.GlobalPosition;

    /// L'oggetto nel mondo con quell'uid, se esiste ed e' a portata.
    public ItemPickup? Pickup(int uid)
    {
        if (uid <= 0 || !_player.IsInsideTree())
            return null;

        foreach (Node node in _player.GetTree().GetNodesInGroup(ItemPickup.GroupName))
        {
            if (node is ItemPickup pickup && pickup.Uid == uid && !pickup.IsQueuedForDeletion())
                return IsReachable(pickup) ? pickup : null;
        }
        return null;
    }

    public bool IsReachable(ItemPickup pickup)
        => !pickup.IsQueuedForDeletion()
           && pickup.GlobalPosition.DistanceTo(_player.GlobalPosition) <= _reach;

    /// Radice dell'albero di uno spazio nel mondo (null per lo spazio del giocatore).
    public ItemInstance? WorldRoot(int uid) => Pickup(uid)?.Root;

    /// Istanza per id nello spazio indicato.
    public ItemInstance? Find(int space, int instanceId)
    {
        if (instanceId == 0)
            return null;
        if (space == ItemAddress.PlayerSpace)
            return Player.Find(instanceId);

        ItemInstance? root = WorldRoot(space);
        return root != null ? ItemTree.Find(root, instanceId) : null;
    }

    /// Indirizzo in cui si trova ora un item di questo spazio (Ground per una radice nel mondo).
    public static ItemAddress AddressOf(ItemInstance item, int space)
    {
        if (item.EquippedIn != EquipSlotType.None)
            return ItemAddress.Equip(item.EquippedIn);
        if (item.Parent != null && item.ParentGrid >= 0)
            return ItemAddress.GridOf(space, item.Parent.InstanceId, item.ParentGrid);
        if (item.Parent != null && item.ParentSlot >= 0)
            return ItemAddress.SlotOf(space, item.Parent.InstanceId, item.ParentSlot);
        return ItemAddress.Ground();
    }
}
