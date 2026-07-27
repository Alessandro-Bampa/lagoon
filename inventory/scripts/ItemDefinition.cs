using Godot;

namespace Lagoon;

/// <summary>
/// Definizione (tipo) di un oggetto: dati puri, nessun side-effect (CLAUDE.md §4). Salvata come
/// <c>.tres</c> in <c>resources/items/</c> e caricata dall'<see cref="ItemDatabase"/>.
///
/// Sulla rete non si trasmette mai la definizione: solo l'<see cref="ItemId"/> (stringa), che host
/// e client risolvono in modo identico tramite l'<see cref="ItemDatabase"/> (dato statico presente
/// in ogni build).
/// </summary>
[GlobalClass]
public partial class ItemDefinition : Resource
{
    /// Identificatore stabile e univoco del tipo (chiave di rete e di database). Es: "backpack".
    [Export] public string ItemId { get; set; } = "";

    // ------------------------------------------------------------------------------------
    //  Testo localizzato
    //
    //  I .tres NON contengono testo: nome, descrizione ed effetto sono CHIAVI derivate
    //  dall'ItemId, tradotte a runtime da locales/items.csv. Cosi' e' impossibile creare un
    //  item con un nome hardcoded, e la definizione resta un dato puro (CLAUDE.md §4).
    //
    //  Sulla rete continua a viaggiare solo l'ItemId: due giocatori con lingue diverse
    //  vedono nomi diversi dello stesso identico oggetto, senza alcuna desincronizzazione.
    // ------------------------------------------------------------------------------------

    public string NameKey => $"ITEM_{ItemId.ToUpperInvariant()}_NAME";
    public string DescriptionKey => $"ITEM_{ItemId.ToUpperInvariant()}_DESC";
    public string EffectKey => $"ITEM_{ItemId.ToUpperInvariant()}_EFFECT";

    /// Nome localizzato. NON memorizzarlo in un campo: cambia quando cambia la lingua.
    public string DisplayName => Loc.T(NameKey);

    /// Descrizione estesa. Stringa vuota se l'item non ne ha una (chiave assente, nessun warning).
    public string Description => Loc.TOrEmpty(DescriptionKey);

    /// Effetto di gioco in forma leggibile. Vuoto per gli oggetti che non ne hanno.
    public string Effect => Loc.TOrEmpty(EffectKey);

    // Dimensioni in celle di griglia (orientamento base, prima di un'eventuale rotazione).
    [Export] public int Width { get; set; } = 1;
    [Export] public int Height { get; set; } = 1;

    /// Peso di UNA unita' in kg (il peso totale di uno stack e' Weight * StackCount).
    [Export] public float Weight { get; set; } = 0.1f;

    /// Icona opzionale. Se null, la UI ripiega su <see cref="ResolveIcon"/> (convenzione per ItemId)
    /// e infine su un placeholder colorato per categoria.
    [Export] public Texture2D? Icon { get; set; }

    [Export] public ItemCategory Category { get; set; } = ItemCategory.Generic;

    /// Slot in cui l'item puo' essere equipaggiato (None = non equipaggiabile).
    [Export] public EquipSlotType EquipSlot { get; set; } = EquipSlotType.None;

    // Se e' un container (zaino/gilet), espone una griglia interna ContainerColumns x ContainerRows.
    [Export] public bool IsContainer { get; set; }
    [Export] public int ContainerColumns { get; set; }
    [Export] public int ContainerRows { get; set; }

    /// Massimo impilamento (1 = non impilabile).
    [Export] public int MaxStack { get; set; } = 1;

    /// Assegnabile a uno slot della hotbar (menu rapido) — es. medkit, granate.
    [Export] public bool QuickUsable { get; set; }

    // Pacchetto sigillato: "Apri pacchetto" lo sostituisce con UnpackCount unita' di UnpackYields.
    [Export] public string UnpackYields { get; set; } = "";
    [Export] public int UnpackCount { get; set; }

    /// Contenitore fisso nel mondo (cassa/baule): si apre, non si raccoglie.
    [Export] public bool WorldAnchored { get; set; }

    /// Convenzione di percorso per l'icona degli asset di prototipo (SVG in assets/textures/items/).
    private const string IconFolder = "res://assets/textures/items/";

    /// <summary>
    /// Icona effettiva da mostrare: <see cref="Icon"/> se assegnata, altrimenti tentativo di
    /// caricamento per convenzione (<c>assets/textures/items/&lt;ItemId&gt;.svg</c>). Ritorna null se
    /// nessuna texture e' disponibile: la UI in tal caso disegna un placeholder colorato.
    ///
    /// Evita di accoppiare i <c>.tres</c> all'import pipeline delle texture (UID): l'icona e'
    /// risolta a runtime per percorso.
    /// </summary>
    public Texture2D? ResolveIcon()
    {
        if (Icon != null)
            return Icon;

        string path = $"{IconFolder}{ItemId}.svg";
        if (ResourceLoader.Exists(path))
            return ResourceLoader.Load<Texture2D>(path);

        return null;
    }
}
