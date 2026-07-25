using Godot;
using GDDict = Godot.Collections.Dictionary;

namespace Lagoon;

/// <summary>
/// Item a terra nel mondo. Rappresentazione puramente visiva: nessuna logica di stato. Viene creato
/// dall'host tramite la spawn-function del <c>MultiplayerSpawner</c> in <see cref="GameWorld"/> e
/// ricostruito identico sui client dagli stessi dati di spawn.
///
/// Porta un <see cref="Payload"/>: l'item serializzato COMPLETO del suo contenuto annidato (stesso
/// formato di <see cref="PlayerInventoryModel.SerializeItem"/>). E' cio' che permette a uno zaino
/// droppato di conservare gli oggetti dentro, e alla colonna "a terra" di mostrarne il contenuto
/// senza RPC aggiuntive (il payload viaggia nei dati di spawn, quindi e' gia' replicato a tutti).
///
/// L'interazione (pickup/loot) e' guidata dal giocatore locale, che invia una richiesta all'host:
/// qui non si decide nulla, si mostra soltanto.
/// </summary>
public partial class ItemPickup : Node3D
{
    /// Gruppo per la scansione di prossimita' da parte del giocatore locale.
    public const string GroupName = "world_item";

    /// Id stabile assegnato dall'host e replicato nei dati di spawn: identita' cross-peer del pickup
    /// (piu' robusto del nome del nodo per find/despawn/loot tra host e client).
    [Export] public int Uid { get; set; }

    /// Item serializzato (con contenuto). Impostato dalla spawn-function su OGNI peer.
    public GDDict Payload { get; set; } = new();

    /// <summary>
    /// Contenitore fisso nel mondo (cassa, baule): con F si APRE invece di essere raccolto, e non
    /// puo' essere spostato nell'inventario. I cadaveri, in Fase 3, useranno lo stesso meccanismo.
    /// </summary>
    [Export] public bool Anchored { get; set; }

    /// <summary>
    /// True se l'oggetto e' un contenitore (quindi apribile/saccheggiabile). Si basa sulla
    /// DEFINIZIONE, non sulla presenza della chiave "grid" nel payload: un contenitore vuoto
    /// appena spawnato e' comunque un contenitore.
    /// </summary>
    public bool IsContainer
        => GetNodeOrNull<ItemDatabase>("/root/ItemDatabase")?.Get(ItemId)?.IsContainer ?? false;

    /// ItemId dell'item di primo livello (chiave dell'ItemDatabase).
    public string ItemId => Payload.TryGetValue("item", out Variant v) ? v.AsString() : "";

    /// Numero di unita' nello stack di primo livello.
    public int StackCount => Payload.TryGetValue("stack", out Variant v) ? v.AsInt32() : 1;

    private Label3D? _label;

    public override void _Ready()
    {
        AddToGroup(GroupName);

        var db = GetNodeOrNull<ItemDatabase>("/root/ItemDatabase");
        ItemDefinition? def = db?.Get(ItemId);

        _label = GetNodeOrNull<Label3D>("Label");
        if (_label != null)
        {
            _label.Text = BuildLabel(def);
            // Prompt poco invadente: bianco semitrasparente, mostrato solo in prossimita'.
            _label.Modulate = new Color(1f, 1f, 1f, 0.72f);
            _label.OutlineModulate = new Color(0f, 0f, 0f, 0.5f);
            _label.Visible = false;
        }

        if (def != null)
        {
            var mesh = GetNodeOrNull<MeshInstance3D>("Mesh");
            if (mesh != null)
                mesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = InventoryColors.ForCategory(def.Category) };
        }
    }

    /// <summary>
    /// Mostra/nasconde il prompt. Lo pilota il giocatore LOCALE (<see cref="PlayerHud"/>) in base
    /// alla distanza: e' pura presentazione, non stato replicato.
    /// </summary>
    public void SetPromptVisible(bool visible)
    {
        if (_label != null)
            _label.Visible = visible;
    }

    private string BuildLabel(ItemDefinition? def)
    {
        string display = def?.DisplayName ?? ItemId;
        if (StackCount > 1)
            display += $" x{StackCount}";

        // Segnala a colpo d'occhio che il contenitore a terra non e' vuoto.
        int contained = CountContained();
        if (contained > 0)
            display += $" ({contained})";

        // Combinazione di tasti, come nel riferimento: "Nome  [F]" / "Cassa  [F] Apri".
        return Anchored ? $"{display}   [F] Apri" : $"{display}   [F]";
    }

    /// Numero di oggetti contenuti al primo livello del payload (0 se non e' un container).
    private int CountContained()
    {
        if (!Payload.TryGetValue("grid", out Variant gridVar))
            return 0;
        GDDict grid = gridVar.AsGodotDictionary();
        return grid.TryGetValue("items", out Variant items) ? items.AsGodotArray().Count : 0;
    }
}
