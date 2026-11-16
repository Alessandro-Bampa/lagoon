using System.Collections.Generic;
using Godot;
using Godot.Collections;

namespace Lagoon;

/// <summary>
/// Definizione (tipo) di un oggetto: dati puri, nessun side-effect (CLAUDE.md §4). Salvata come
/// <c>.tres</c> in <c>resources/items/</c> e caricata dall'<see cref="ItemDatabase"/>.
///
/// Sulla rete non si trasmette mai la definizione: solo l'<see cref="ItemId"/> (stringa), che host
/// e client risolvono in modo identico tramite l'<see cref="ItemDatabase"/>.
///
/// Le categorie con attributi propri sono sottoclassi (<c>WeaponDefinition</c>,
/// <see cref="MagazineDefinition"/>, <see cref="AmmoDefinition"/>), non campi opzionali qui.
/// </summary>
[GlobalClass]
public partial class ItemDefinition : Resource
{
    /// Identificatore stabile e univoco del tipo (chiave di rete e di database). Es: "backpack".
    [Export] public string ItemId { get; set; } = "";

    // ------------------------------------------------------------------------------------
    //  Testo localizzato: i .tres NON contengono testo, solo chiavi derivate dall'ItemId
    //  (locales/items.csv). Sulla rete viaggia l'ItemId: due lingue, stesso oggetto.
    // ------------------------------------------------------------------------------------

    public string NameKey => $"ITEM_{ItemId.ToUpperInvariant()}_NAME";
    public string ShortNameKey => $"ITEM_{ItemId.ToUpperInvariant()}_SHORT";
    public string DescriptionKey => $"ITEM_{ItemId.ToUpperInvariant()}_DESC";
    public string EffectKey => $"ITEM_{ItemId.ToUpperInvariant()}_EFFECT";

    /// Nome localizzato. NON memorizzarlo in un campo: cambia quando cambia la lingua.
    public string DisplayName => Loc.T(NameKey);

    /// Nome breve mostrato nella tile; se manca la chiave <c>_SHORT</c> ripiega sul nome intero.
    public string ShortName => Loc.TryT(ShortNameKey, out string text) ? text : DisplayName;

    public string Description => Loc.TOrEmpty(DescriptionKey);
    public string Effect => Loc.TOrEmpty(EffectKey);

    // ------------------------------------------------------------------------------------
    //  Base
    // ------------------------------------------------------------------------------------

    /// Ingombro ESTERNO in celle (orientamento base). Indipendente dalla capienza interna.
    [Export] public int Width { get; set; } = 1;
    [Export] public int Height { get; set; } = 1;

    /// Peso di UNA unita' in kg (vuoto, senza contenuto).
    [Export] public float Weight { get; set; } = 0.1f;

    /// Icona esplicita. Se null si usa la convenzione di <see cref="ResolveIcon"/>.
    [Export] public Texture2D? Icon { get; set; }

    [Export] public ItemCategory Category { get; set; } = ItemCategory.Generic;

    /// Massimo impilamento (1 = non impilabile).
    [Export] public int MaxStack { get; set; } = 1;

    /// Collegabile a uno slot rapido (medicinali, cibo, granate...).
    [Export] public bool QuickUsable { get; set; }

    // Pacchetto sigillato: "Apri" lo sostituisce con UnpackCount unita' di UnpackYields.
    [Export] public string UnpackYields { get; set; } = "";
    [Export] public int UnpackCount { get; set; }

    /// Correzione di scala del modello a terra (1 = scala nativa del .glb). Serve solo per i
    /// modelli esportati fuori scala.
    [Export] public float WorldModelScale { get; set; } = 1f;

    /// Contenitore fisso nel mondo (cassa/baule): si apre, non si raccoglie.
    [Export] public bool WorldAnchored { get; set; }

    // ------------------------------------------------------------------------------------
    //  Contenitore
    // ------------------------------------------------------------------------------------

    // Scorciatoia per il contenitore a griglia singola. Ignorata se Grids non e' vuoto.
    [Export] public int ContainerColumns { get; set; }
    [Export] public int ContainerRows { get; set; }

    /// Sotto-griglie di un contenitore multi-tasca, con la loro posizione nel layout.
    [Export] public Array<ItemGridDef> Grids { get; set; } = new();

    /// Filtro valido per tutte le griglie che non ne dichiarano uno proprio.
    [Export] public ItemFilter? ContentFilter { get; set; }

    /// Se true il filtro vale anche per cio' che sta DENTRO gli oggetti inseriti (pouch sicuro:
    /// niente contenitori che nascondono oggetti vietati).
    [Export] public bool FilterAppliesToDescendants { get; set; }

    /// Slot dell'oggetto (pozzetto del caricatore; in futuro gli attacchi dell'arma).
    [Export] public Array<ItemSlotDef> Slots { get; set; } = new();

    // ------------------------------------------------------------------------------------
    //  Regole di blocco fra slot di equipaggiamento (un casco integrale blocca le cuffie...)
    // ------------------------------------------------------------------------------------

    [Export] public bool BlocksEarpiece { get; set; }
    [Export] public bool BlocksEyewear { get; set; }
    [Export] public bool BlocksFaceCover { get; set; }
    [Export] public bool BlocksHeadwear { get; set; }
    [Export] public bool BlocksBodyArmor { get; set; }

    // ------------------------------------------------------------------------------------
    //  Derivati
    // ------------------------------------------------------------------------------------

    private List<ItemGridDef>? _gridSpecs;

    /// Griglie effettive: <see cref="Grids"/> se dichiarate, altrimenti la griglia singola.
    public IReadOnlyList<ItemGridDef> GridSpecs
    {
        get
        {
            if (_gridSpecs != null)
                return _gridSpecs;

            _gridSpecs = new List<ItemGridDef>();
            if (Grids.Count > 0)
            {
                foreach (ItemGridDef grid in Grids)
                    if (grid != null && grid.Columns > 0 && grid.Rows > 0)
                        _gridSpecs.Add(grid);
            }
            else if (ContainerColumns > 0 && ContainerRows > 0)
            {
                _gridSpecs.Add(new ItemGridDef { Columns = ContainerColumns, Rows = ContainerRows });
            }
            return _gridSpecs;
        }
    }

    public bool IsContainer => GridSpecs.Count > 0;
    public bool IsStackable => MaxStack > 1;

    /// Le tasche sono un item fisso: non si tolgono, non si spostano, non si lasciano cadere.
    public bool Movable => Category != ItemCategory.Pockets;

    /// Celle totali della capienza interna.
    public int CapacityCells
    {
        get
        {
            int cells = 0;
            foreach (ItemGridDef grid in GridSpecs)
                cells += grid.Columns * grid.Rows;
            return cells;
        }
    }

    /// Filtro effettivo della griglia <paramref name="gridIndex"/> (quello proprio o del contenitore).
    public ItemFilter? FilterFor(int gridIndex)
    {
        IReadOnlyList<ItemGridDef> specs = GridSpecs;
        if (gridIndex >= 0 && gridIndex < specs.Count && specs[gridIndex].Filter != null)
            return specs[gridIndex].Filter;
        return ContentFilter;
    }

    // ------------------------------------------------------------------------------------
    //  Grafica per convenzione di percorso (niente UID nei .tres)
    // ------------------------------------------------------------------------------------

    private const string IconFolder = "res://assets/textures/items/";
    private const string ModelFolder = "res://assets/models/items/";

    private Texture2D? _resolvedIcon;
    private bool _iconResolved;

    /// <summary>
    /// Icona effettiva: <see cref="Icon"/> se assegnata, altrimenti per convenzione
    /// <c>assets/textures/items/&lt;ItemId&gt;.png</c> (foto del modello, prodotta da
    /// <c>tools/bake_item_icons.gd</c>) e poi <c>.svg</c>. Null = la UI disegna un segnaposto.
    /// </summary>
    public Texture2D? ResolveIcon()
    {
        if (Icon != null)
            return Icon;
        if (_iconResolved)
            return _resolvedIcon;

        _iconResolved = true;
        foreach (string extension in new[] { ".png", ".svg" })
        {
            string path = $"{IconFolder}{ItemId}{extension}";
            if (ResourceLoader.Exists(path))
            {
                _resolvedIcon = ResourceLoader.Load<Texture2D>(path);
                break;
            }
        }
        return _resolvedIcon;
    }

    /// Modello 3D dell'oggetto a terra (<c>assets/models/items/&lt;ItemId&gt;.glb</c>), o null.
    public PackedScene? ResolveWorldModel()
    {
        string path = $"{ModelFolder}{ItemId}.glb";
        return ResourceLoader.Exists(path) ? ResourceLoader.Load<PackedScene>(path) : null;
    }
}
