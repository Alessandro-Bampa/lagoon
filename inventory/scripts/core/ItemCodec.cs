using System;
using Godot;
using GDArray = Godot.Collections.Array;
using GDDict = Godot.Collections.Dictionary;

namespace Lagoon;

/// <summary>
/// (De)serializzazione di un albero di <see cref="ItemInstance"/>. UN solo formato per la replica
/// dell'inventario e per il payload degli oggetti nel mondo, cosi' un drop non perde nulla.
///
/// Le chiavi sono volutamente corte e i valori di default si omettono: il payload viaggia a ogni
/// push di stato. Gli id si conservano sempre (sono unici nella sessione).
/// </summary>
public static class ItemCodec
{
    public static GDDict Serialize(ItemInstance item)
    {
        var dict = new GDDict
        {
            { "id", item.InstanceId },
            { "item", item.Definition.ItemId },
        };

        if (item.GridX != 0) dict["x"] = item.GridX;
        if (item.GridY != 0) dict["y"] = item.GridY;
        if (item.Rotated) dict["rot"] = true;
        if (item.StackCount != 1) dict["stack"] = item.StackCount;
        if (item.TagName.Length > 0)
        {
            dict["tag"] = item.TagName;
            dict["tagc"] = item.TagColor;
        }

        if (item.Grids.Length > 0)
        {
            var grids = new GDArray();
            foreach (InventoryGrid grid in item.Grids)
            {
                var items = new GDArray();
                foreach (ItemInstance child in grid.Items)
                    items.Add(Serialize(child));
                grids.Add(items);
            }
            dict["grids"] = grids;
        }

        GDDict? slots = null;
        for (int i = 0; i < item.SlotItems.Length; i++)
        {
            ItemInstance? child = item.SlotItems[i];
            if (child == null)
                continue;
            slots ??= new GDDict();
            slots[i.ToString()] = Serialize(child);
        }
        if (slots != null)
            dict["slots"] = slots;

        if (item.Rounds.Count > 0)
        {
            // Coppie piatte [ammoId, quantita', ...] dal fondo alla cima.
            var rounds = new GDArray();
            foreach (AmmoStack stack in item.Rounds)
            {
                rounds.Add(stack.Ammo.ItemId);
                rounds.Add(stack.Count);
            }
            dict["rounds"] = rounds;
        }

        return dict;
    }

    public static ItemInstance? Deserialize(GDDict dict, Func<string, ItemDefinition?> resolve)
    {
        string itemId = dict["item"].AsString();
        ItemDefinition? def = resolve(itemId);
        if (def == null)
        {
            GD.PrintErr($"[Inventory] ItemId sconosciuto in deserializzazione: '{itemId}'");
            return null;
        }

        var item = new ItemInstance(dict["id"].AsInt32(), def)
        {
            GridX = dict.TryGetValue("x", out Variant x) ? x.AsInt32() : 0,
            GridY = dict.TryGetValue("y", out Variant y) ? y.AsInt32() : 0,
            Rotated = dict.TryGetValue("rot", out Variant rot) && rot.AsBool(),
            StackCount = dict.TryGetValue("stack", out Variant stack) ? stack.AsInt32() : 1,
            TagName = dict.TryGetValue("tag", out Variant tag) ? tag.AsString() : "",
            TagColor = dict.TryGetValue("tagc", out Variant tagc) ? tagc.AsInt32() : 0,
        };

        if (dict.TryGetValue("grids", out Variant gridsVar))
        {
            GDArray grids = gridsVar.AsGodotArray();
            for (int g = 0; g < grids.Count && g < item.Grids.Length; g++)
            {
                foreach (Variant entry in grids[g].AsGodotArray())
                {
                    ItemInstance? child = Deserialize(entry.AsGodotDictionary(), resolve);
                    if (child == null)
                        continue;
                    // Integrita': una posizione non piu' valida (definizione cambiata) non deve
                    // far sparire l'oggetto. Si ripiega sul primo spazio libero.
                    if (!item.Grids[g].Place(child, child.GridX, child.GridY, child.Rotated)
                        && !item.Grids[g].TryAutoPlace(child))
                        GD.PrintErr($"[Inventory] '{child.Definition.ItemId}' non entra piu' in '{itemId}': scartato.");
                }
            }
        }

        if (dict.TryGetValue("slots", out Variant slotsVar))
        {
            GDDict slots = slotsVar.AsGodotDictionary();
            foreach (Variant key in slots.Keys)
            {
                int index = int.Parse(key.AsString());
                if (index < 0 || index >= item.SlotItems.Length)
                    continue;
                ItemInstance? child = Deserialize(slots[key].AsGodotDictionary(), resolve);
                if (child != null)
                    item.SetSlot(index, child);
            }
        }

        if (dict.TryGetValue("rounds", out Variant roundsVar))
        {
            GDArray rounds = roundsVar.AsGodotArray();
            for (int i = 0; i + 1 < rounds.Count; i += 2)
                if (resolve(rounds[i].AsString()) is AmmoDefinition ammo)
                    item.Rounds.Add(new AmmoStack(ammo, rounds[i + 1].AsInt32()));
        }

        return item;
    }
}
