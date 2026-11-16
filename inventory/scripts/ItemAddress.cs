namespace Lagoon;

/// <summary>
/// Indirizzo di una DESTINAZIONE: una griglia o uno slot di un item, uno slot del personaggio,
/// oppure il terreno. La SORGENTE di uno spostamento non ha bisogno di un indirizzo: basta la
/// coppia (spazio, InstanceId), perche' gli id sono unici e ogni istanza sa dove si trova.
///
/// <b>Spazio</b>: <c>0</c> = il proprio inventario; un valore positivo = l'<c>Uid</c> di un
/// oggetto nel mondo (cassa, zaino a terra, oggetto sfuso).
///
/// Si codifica in 4 int per attraversare le RPC senza strutture.
/// </summary>
public readonly struct ItemAddress : System.IEquatable<ItemAddress>
{
    public enum Kind
    {
        /// Griglia <see cref="Index"/> dell'item <see cref="Owner"/>.
        Grid = 0,

        /// Slot <see cref="Index"/> dell'item <see cref="Owner"/>; con Owner = 0 e' uno slot del
        /// personaggio e Index e' un <see cref="EquipSlotType"/>.
        Slot = 1,

        /// Il terreno ai piedi del giocatore.
        Ground = 2,
    }

    /// Spazio che vale "il proprio inventario".
    public const int PlayerSpace = 0;

    public int Space { get; }
    public int Owner { get; }
    public Kind Type { get; }
    public int Index { get; }

    public ItemAddress(int space, int owner, Kind type, int index)
    {
        Space = space;
        Owner = owner;
        Type = type;
        Index = index;
    }

    public static ItemAddress Equip(EquipSlotType slot) => new(PlayerSpace, 0, Kind.Slot, (int)slot);
    public static ItemAddress GridOf(int space, int ownerId, int gridIndex) => new(space, ownerId, Kind.Grid, gridIndex);
    public static ItemAddress SlotOf(int space, int ownerId, int slotIndex) => new(space, ownerId, Kind.Slot, slotIndex);
    public static ItemAddress Ground() => new(PlayerSpace, 0, Kind.Ground, 0);

    public bool IsPlayer => Space == PlayerSpace && Type != Kind.Ground;
    public bool IsGround => Type == Kind.Ground;
    public bool IsEquipSlot => Type == Kind.Slot && Owner == 0 && Space == PlayerSpace;
    public EquipSlotType EquipSlot => (EquipSlotType)Index;

    public static ItemAddress Decode(int space, int owner, int type, int index)
        => new(space, owner, (Kind)type, index);

    public bool Equals(ItemAddress other)
        => Space == other.Space && Owner == other.Owner && Type == other.Type && Index == other.Index;
    public override bool Equals(object? obj) => obj is ItemAddress other && Equals(other);
    public override int GetHashCode() => System.HashCode.Combine(Space, Owner, (int)Type, Index);
    public override string ToString() => $"{Type}(space={Space}, owner={Owner}, index={Index})";
}
