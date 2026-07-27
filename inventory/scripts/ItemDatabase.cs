using System.Collections.Generic;
using Godot;

namespace Lagoon;

/// <summary>
/// Registro globale (autoload) delle definizioni item. Carica tutti i <c>.tres</c> in
/// <c>resources/items/</c> una volta all'avvio e li mappa per <see cref="ItemDefinition.ItemId"/>.
///
/// E' la fonte comune che permette a host e client di risolvere identicamente un ItemId in una
/// definizione: sulla rete si trasmettono solo gli id (le definizioni sono dato statico di build).
/// Vive in <c>inventory/scripts/</c> come previsto dallo scaffolding (CLAUDE.md §4).
/// </summary>
public partial class ItemDatabase : Node
{
    private const string ItemsFolder = "res://resources/items";

    private readonly Dictionary<string, ItemDefinition> _byId = new();

    public override void _Ready()
    {
        LoadAll();
        GD.Print($"[ItemDatabase] Caricate {_byId.Count} definizioni item da {ItemsFolder}.");
        // Le traduzioni sono gia' registrate all'avvio degli autoload: la verifica puo' girare qui.
        WarnMissingTranslations();
    }

    /// <summary>
    /// In debug, segnala gli item privi di nome tradotto. Il nome NON sta piu' nel <c>.tres</c>: e'
    /// una chiave derivata dall'ItemId (<c>ITEM_&lt;ID&gt;_NAME</c>) risolta da <c>locales/items.csv</c>.
    /// Senza questo controllo un item aggiunto senza la riga nel CSV si scoprirebbe solo aprendo
    /// l'inventario; cosi' invece si vede all'avvio.
    /// </summary>
    private void WarnMissingTranslations()
    {
        if (!OS.IsDebugBuild())
            return;

        foreach (ItemDefinition def in _byId.Values)
            if (!Loc.TryT(def.NameKey, out _))
                GD.PushWarning(
                    $"[ItemDatabase] '{def.ItemId}' non ha un nome tradotto: aggiungi la riga "
                    + $"{def.NameKey} in locales/items.csv.");
    }

    private void LoadAll()
    {
        using DirAccess? dir = DirAccess.Open(ItemsFolder);
        if (dir == null)
        {
            GD.PrintErr($"[ItemDatabase] Cartella non accessibile: {ItemsFolder}");
            return;
        }

        foreach (string file in dir.GetFiles())
        {
            // In export gli import diventano ".tres.remap": normalizziamo al percorso sorgente.
            string name = file.EndsWith(".remap") ? file[..^".remap".Length] : file;
            if (!name.EndsWith(".tres"))
                continue;

            string path = $"{ItemsFolder}/{name}";
            var def = ResourceLoader.Load<ItemDefinition>(path);
            if (def == null || string.IsNullOrEmpty(def.ItemId))
            {
                GD.PrintErr($"[ItemDatabase] Definizione non valida o priva di ItemId: {path}");
                continue;
            }
            _byId[def.ItemId] = def;
        }
    }

    /// Definizione per id, o null se sconosciuta.
    public ItemDefinition? Get(string itemId) => _byId.GetValueOrDefault(itemId);

    public IEnumerable<ItemDefinition> All => _byId.Values;
}
