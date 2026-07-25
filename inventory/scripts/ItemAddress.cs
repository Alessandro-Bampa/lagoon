namespace Lagoon;

/// <summary>
/// Indirizzo di una posizione che puo' contenere item, ovunque essa sia: una griglia del proprio
/// inventario, uno slot indossato, un contenitore nel mondo (zaino droppato o cassa) o il terreno.
///
/// E' il fondamento del refactor: avendo UN modo di indicare "da dove" e "verso dove", tutte le
/// interazioni (trascinamento, quick move, quick equip, scarto rapido, voci del menu contestuale)
/// si riducono a una sola operazione di spostamento (<see cref="ItemTransfer"/>) e a una sola RPC,
/// invece di una RPC per ogni combinazione.
///
/// Si codifica in 3 int (<see cref="Realm"/>, <see cref="A"/>, <see cref="B"/>) per attraversare le
/// RPC senza strutture complesse.
/// </summary>
public readonly struct ItemAddress : System.IEquatable<ItemAddress>
{
    public enum RealmType
    {
        /// Una griglia dell'inventario del giocatore. A = containerId (vedi PlayerInventoryModel).
        PlayerGrid = 0,

        /// Uno slot di equipaggiamento indossato. A = EquipSlotType.
        PlayerEquip = 1,

        /// Un contenitore nel mondo. A = uid del world item, B = InstanceId del container nel payload.
        WorldContainer = 2,

        /// Il terreno: destinazione di uno scarto (l'item cade ai piedi del giocatore).
        WorldLoose = 3,
    }

    public RealmType Realm { get; }
    public int A { get; }
    public int B { get; }

    public ItemAddress(RealmType realm, int a = 0, int b = 0)
    {
        Realm = realm;
        A = a;
        B = b;
    }

    // --- costruttori espressivi ----------------------------------------------------------

    /// Una griglia dell'inventario (tasche, rig, zaino, container annidato).
    public static ItemAddress PlayerGridAt(int containerId) => new(RealmType.PlayerGrid, containerId);

    /// Le tasche (griglia sempre presente).
    public static ItemAddress Pockets() => PlayerGridAt(PlayerInventoryModel.PocketsContainerId);

    /// Uno slot indossato.
    public static ItemAddress Equip(EquipSlotType slot) => new(RealmType.PlayerEquip, (int)slot);

    /// <summary>
    /// Un contenitore nel mondo. <paramref name="containerInstanceId"/> = 0 indica l'item radice
    /// del world item stesso (es. lo zaino droppato), altrimenti un container annidato nel payload.
    /// </summary>
    public static ItemAddress WorldContainerAt(int worldItemUid, int containerInstanceId = 0)
        => new(RealmType.WorldContainer, worldItemUid, containerInstanceId);

    /// Il terreno ai piedi del giocatore.
    public static ItemAddress Ground() => new(RealmType.WorldLoose);

    // --- comodita' -----------------------------------------------------------------------

    public bool IsWorld => Realm is RealmType.WorldContainer or RealmType.WorldLoose;
    public bool IsPlayer => Realm is RealmType.PlayerGrid or RealmType.PlayerEquip;

    public EquipSlotType Slot => (EquipSlotType)A;
    public int ContainerId => A;
    public int WorldItemUid => A;
    public int ContainerInstanceId => B;

    public static ItemAddress Decode(int realm, int a, int b) => new((RealmType)realm, a, b);

    public bool Equals(ItemAddress other) => Realm == other.Realm && A == other.A && B == other.B;
    public override bool Equals(object? obj) => obj is ItemAddress other && Equals(other);
    public override int GetHashCode() => System.HashCode.Combine((int)Realm, A, B);

    public override string ToString() => $"{Realm}(a={A}, b={B})";
}
