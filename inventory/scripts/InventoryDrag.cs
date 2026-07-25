using Godot;
using GDDict = Godot.Collections.Dictionary;

namespace Lagoon;

/// <summary>
/// Payload del drag &amp; drop dell'inventario. Dopo il refactor porta semplicemente l'item e il suo
/// <see cref="ItemAddress"/> di provenienza: al rilascio il bersaglio conosce gia' il proprio
/// indirizzo, quindi ogni drop e' uno spostamento fra due indirizzi, senza casi speciali.
/// </summary>
public static class InventoryDrag
{
    private const string PayloadKind = "lagoon_item";

    /// Dati letti da un payload di drag valido.
    public readonly struct Payload
    {
        public int InstanceId { get; init; }
        public ItemAddress From { get; init; }
        public EquipSlotType EquipSlot { get; init; }
        public bool QuickUsable { get; init; }
    }

    /// Costruisce il payload per un item trascinato dall'indirizzo indicato.
    public static GDDict Make(ItemInstance item, ItemAddress from) => new()
    {
        { "kind", PayloadKind },
        { "id", item.InstanceId },
        { "realm", (int)from.Realm },
        { "a", from.A },
        { "b", from.B },
        { "equip_slot", (int)item.Definition.EquipSlot },
        { "quick", item.Definition.QuickUsable },
    };

    /// True se <paramref name="data"/> e' un payload item valido; in tal caso lo decodifica.
    public static bool TryRead(Variant data, out Payload payload)
    {
        payload = default;
        if (data.VariantType != Variant.Type.Dictionary)
            return false;

        GDDict dict = data.AsGodotDictionary();
        if (!dict.TryGetValue("kind", out Variant kind) || kind.AsString() != PayloadKind)
            return false;

        payload = new Payload
        {
            InstanceId = dict["id"].AsInt32(),
            From = ItemAddress.Decode(dict["realm"].AsInt32(), dict["a"].AsInt32(), dict["b"].AsInt32()),
            EquipSlot = (EquipSlotType)dict["equip_slot"].AsInt32(),
            QuickUsable = dict["quick"].AsBool(),
        };
        return true;
    }
}
