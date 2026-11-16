using Godot;
using GDDict = Godot.Collections.Dictionary;

namespace Lagoon;

/// <summary>
/// Oggetto nel mondo (sfuso a terra, zaino lasciato cadere, cassa). E' uno "spazio" di inventario
/// a se': il suo <see cref="Uid"/> e' il numero di spazio usato negli indirizzi.
///
/// Rappresentazione puramente visiva: nessuna logica di stato. Lo crea l'host tramite la
/// spawn-function del <c>MultiplayerSpawner</c> in <see cref="GameWorld"/>, e ogni peer lo
/// ricostruisce identico dagli stessi dati di spawn. Il <see cref="Payload"/> e' l'albero
/// serializzato COMPLETO (<see cref="ItemCodec"/>): uno zaino a terra conserva il contenuto e
/// chiunque puo' mostrarlo senza RPC aggiuntive.
///
/// Il payload non cambia mai sul posto: quando il contenuto cambia l'host sostituisce il nodo
/// (<see cref="GameWorld.ReplacePickupPayload"/>). Per questo <see cref="Root"/> si puo'
/// deserializzare una volta sola.
/// </summary>
public partial class ItemPickup : Node3D
{
    /// Gruppo per la scansione di prossimita' da parte del giocatore locale.
    public const string GroupName = "world_item";

    /// Id stabile assegnato dall'host e replicato nei dati di spawn: identita' cross-peer.
    [Export] public int Uid { get; set; }

    /// Albero serializzato (con contenuto). Impostato dalla spawn-function su OGNI peer.
    public GDDict Payload { get; set; } = new();

    /// Contenitore fisso (cassa, baule): con F si APRE invece di essere raccolto.
    [Export] public bool Anchored { get; set; }

    private ItemInstance? _root;
    private bool _rootParsed;
    private Label3D? _label;

    /// <summary>
    /// Albero dell'oggetto, deserializzato alla prima richiesta. Sull'host e' l'istanza che
    /// <see cref="InventoryOps"/> modifica prima di riscrivere il payload; sui client e' sola lettura.
    /// </summary>
    public ItemInstance? Root
    {
        get
        {
            if (_rootParsed)
                return _root;
            _rootParsed = true;

            var db = GetNodeOrNull<ItemDatabase>("/root/ItemDatabase");
            if (db != null && Payload.ContainsKey("item"))
                _root = ItemCodec.Deserialize(Payload, id => db.Get(id));
            return _root;
        }
    }

    public ItemDefinition? Definition => Root?.Definition;

    /// True se si puo' aprire (contenitore): dipende dalla DEFINIZIONE, anche se e' vuoto.
    public bool IsContainer => Definition?.IsContainer ?? false;

    public override void _Ready()
    {
        AddToGroup(GroupName);

        ItemDefinition? def = Definition;
        BuildVisual(def);

        _label = GetNodeOrNull<Label3D>("Label");
        if (_label != null)
        {
            _label.Text = BuildLabel(def);
            // Prompt poco invadente: bianco semitrasparente, mostrato solo guardando l'oggetto.
            _label.Modulate = new Color(1f, 1f, 1f, 0.72f);
            _label.OutlineModulate = new Color(0f, 0f, 0f, 0.5f);
            _label.Visible = false;
        }
    }

    /// Il modello dell'oggetto se esiste, altrimenti il cubo colorato per categoria.
    private void BuildVisual(ItemDefinition? def)
    {
        var box = GetNodeOrNull<MeshInstance3D>("Mesh");
        if (def == null)
            return;

        PackedScene? scene = def.ResolveWorldModel();
        if (scene?.Instantiate() is Node3D model)
        {
            AddChild(model);
            FitModel(model, def.WorldModelScale);
            if (box != null)
                box.Visible = false;
            return;
        }

        if (box != null)
            box.MaterialOverride = new StandardMaterial3D { AlbedoColor = InventoryColors.ForCategory(def.Category) };
    }

    /// Appoggia il modello a terra, centrato, alla sua scala NATIVA (la stessa che ha trascinando
    /// il .glb in una scena) moltiplicata per la correzione dichiarata dall'item.
    private static void FitModel(Node3D model, float scale)
    {
        Aabb bounds = default;
        bool any = false;
        foreach (Node node in model.FindChildren("*", "MeshInstance3D", true, false))
        {
            var mesh = (MeshInstance3D)node;
            Aabb local = RelativeTransform(mesh, model) * mesh.GetAabb();
            bounds = any ? bounds.Merge(local) : local;
            any = true;
        }
        if (model is MeshInstance3D self)
        {
            bounds = any ? bounds.Merge(self.GetAabb()) : self.GetAabb();
            any = true;
        }
        if (!any)
            return;

        if (scale <= 0f)
            scale = 1f;
        model.Scale = Vector3.One * scale;
        Vector3 center = bounds.GetCenter();
        model.Position = new Vector3(-center.X, -bounds.Position.Y, -center.Z) * scale;
    }

    private static Transform3D RelativeTransform(Node3D node, Node3D ancestor)
    {
        Transform3D transform = Transform3D.Identity;
        for (Node? current = node; current != null && current != ancestor; current = current.GetParent())
            if (current is Node3D spatial)
                transform = spatial.Transform * transform;
        return transform;
    }

    /// Mostra/nasconde il prompt. Lo pilota il giocatore LOCALE (<see cref="PlayerHud"/>).
    public void SetPromptVisible(bool visible)
    {
        if (_label != null)
            _label.Visible = visible;
    }

    private string BuildLabel(ItemDefinition? def)
    {
        ItemInstance? root = Root;
        string display = def?.DisplayName ?? "";
        if (root != null && root.StackCount > 1)
            display = Loc.T("UI_HUD_ITEM_STACK", display, root.StackCount);

        // Segnala a colpo d'occhio che il contenitore a terra non e' vuoto.
        int contained = 0;
        if (root != null)
            foreach (InventoryGrid grid in root.Grids)
                contained += grid.Items.Count;
        if (contained > 0)
            display = Loc.T("UI_HUD_ITEM_CONTAINS", display, contained);

        // Il tasto arriva dall'InputMap, non e' scritto nel testo (skill ui-hud §5).
        string key = Loc.KeyFor("interact");
        return Loc.T(Anchored ? "UI_HUD_PROMPT_OPEN" : "UI_HUD_PROMPT_PICKUP", display, key);
    }

    /// Il prompt e' testo composto: al cambio lingua va ricostruito a mano.
    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged && _label != null)
            _label.Text = BuildLabel(Definition);
    }
}
