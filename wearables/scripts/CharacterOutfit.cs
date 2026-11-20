using System;
using System.Collections.Generic;
using Godot;

namespace Lagoon;

/// <summary>
/// Veste il corpo in terza persona: figlio di <c>CharacterRig</c>, gira su ogni peer ed e' un
/// ricevitore puro come gli animatori. Il bridge del personaggio gli scrive <see cref="Worn"/> (dallo
/// stato replicato di <see cref="CharacterAppearance"/>); qui si caricano i modelli e si calcolano
/// le maschere.
///
/// Come in Unreal (Leader Pose) e Tarkov: ogni capo e' una mesh skinnata sugli STESSI ossi del corpo,
/// agganciata al suo <c>Skeleton3D</c>; la <c>Skin</c> si lega per nome d'osso. Nessun costo di
/// animazione, nessuno scheletro in piu'.
///
/// Compenetrazioni: ogni capo dichiara le regioni che copre (<see cref="WearableDefinition.CoversRegions"/>)
/// e il proprio strato. Una regione coperta si nasconde nello shader (<c>character_psx.gdshader</c>,
/// parametro per istanza <see cref="HiddenRegionsParam"/>) sul corpo e sui capi di strato minore:
/// gli stivali nascondono la caviglia dei pantaloni, la giacca il busto nudo. E' il sistema delle
/// "partizioni" di Bethesda/DayZ fatto con un bit per regione invece di mesh separate.
///
/// In prima persona chiede al rig di ricostruire le copie di <see cref="FirstPersonBodyView"/>, che
/// cosi' includono i capi (pantaloni e stivali guardando in basso).
/// </summary>
public partial class CharacterOutfit : Node
{
    public const string HiddenRegionsParam = "hidden_regions";
    private const string PiecePrefix = "Wear_";

    private static Shader? _shader;
    private static readonly Dictionary<Texture2D, ShaderMaterial> MaterialByTexture = new();
    private static readonly Dictionary<string, Piece[]> PiecesByPath = new();

    /// Mesh e skin di un modello indossabile, estratte una volta per file.
    public readonly record struct Piece(ArrayMesh Mesh, Skin? Skin, Texture2D? Texture);

    private CharacterAnimator _rig = null!;
    private ItemDatabase? _db;
    private MeshInstance3D? _body;
    private string[] _worn = Array.Empty<string>();
    private bool _dirty = true;

    /// <summary>ItemId indossati (dallo stato replicato). Ordine irrilevante.</summary>
    public string[] Worn
    {
        get => _worn;
        set
        {
            value ??= Array.Empty<string>();
            if (value.AsSpan().SequenceEqual(_worn))
                return;
            _worn = value;
            _dirty = true;
        }
    }

    /// Emesso dopo ogni cambio d'abito applicato (il viewmodel lo usa per rivestire le braccia).
    [Signal] public delegate void OutfitAppliedEventHandler();

    public override void _Ready()
    {
        _rig = GetParent<CharacterAnimator>();
        _db = GetNodeOrNull<ItemDatabase>("/root/ItemDatabase");
    }

    public override void _Process(double delta)
    {
        // Lo scheletro lo risolve il rig nel suo _Ready: si aspetta che ci sia.
        if (!_dirty || _rig.Skeleton == null)
            return;
        _dirty = false;
        Apply(_rig.Skeleton);
    }

    /// Definizioni indossate risolte, nell'ordine di <see cref="Worn"/>.
    public List<WearableDefinition> WornDefinitions()
    {
        var list = new List<WearableDefinition>();
        foreach (string id in _worn)
        {
            if (_db?.Get(id) is WearableDefinition def)
                list.Add(def);
        }
        return list;
    }

    private void Apply(Skeleton3D skeleton)
    {
        _body ??= FindBody(skeleton);
        if (_body != null && _body.MaterialOverride == null)
            _body.MaterialOverride = MaterialFor(BaseTexture(_body.Mesh));

        // via i capi di prima (e le loro copie in prima persona, che hanno lo stesso prefisso)
        foreach (Node child in skeleton.GetChildren())
        {
            if (child is MeshInstance3D old && old.Name.ToString().StartsWith(PiecePrefix))
            {
                skeleton.RemoveChild(old);
                old.QueueFree();
            }
        }

        List<WearableDefinition> defs = WornDefinitions();
        var maskOf = new Dictionary<string, int>();
        foreach (WearableDefinition def in defs)
        {
            Piece[] pieces = LoadPieces(def.WornModelPath);
            for (int i = 0; i < pieces.Length; i++)
            {
                string name = $"{PiecePrefix}{def.ItemId}_{i}";
                var piece = new MeshInstance3D
                {
                    Name = name,
                    Mesh = pieces[i].Mesh,
                    Skin = pieces[i].Skin,
                    Skeleton = new NodePath(".."),
                    Layers = _body?.Layers ?? 1,
                    MaterialOverride = MaterialFor(pieces[i].Texture, doubleSided: true),
                };
                // in prima persona le parti alte di un capo si tolgono, non si collassano
                piece.SetMeta(FirstPersonBodyView.CullMeta, true);
                skeleton.AddChild(piece);
                maskOf[name] = (int)HiddenBy(defs, def.Layer);
            }
        }
        if (_body != null)
            maskOf[_body.Name] = (int)HiddenBy(defs, 0);

        // Prima le copie in prima persona (se il rig le usa), poi le maschere su tutto.
        _rig.RefreshFirstPersonBody();
        ApplyMasks(skeleton, maskOf);
        EmitSignal(SignalName.OutfitApplied);
    }

    /// Regioni nascoste per un capo di strato <paramref name="layer"/>: quelle coperte dagli strati
    /// sopra. Per il corpo (strato 0) tutte quelle coperte.
    public static BodyRegion HiddenBy(IEnumerable<WearableDefinition> worn, int layer)
    {
        BodyRegion hidden = BodyRegion.None;
        foreach (WearableDefinition def in worn)
        {
            if (def.Layer > layer)
                hidden |= def.CoversRegions;
        }
        return hidden;
    }

    /// Imposta la maschera su ogni mesh dello scheletro: originali e copie (stesso nome + suffisso).
    private static void ApplyMasks(Skeleton3D skeleton, Dictionary<string, int> maskOf)
    {
        foreach (Node child in skeleton.GetChildren())
        {
            if (child is not MeshInstance3D mesh)
                continue;
            string name = mesh.Name.ToString();
            foreach (var (key, mask) in maskOf)
            {
                if (name.StartsWith(key))
                {
                    mesh.SetInstanceShaderParameter(HiddenRegionsParam, mask);
                    break;
                }
            }
        }
    }

    private static MeshInstance3D? FindBody(Skeleton3D skeleton)
    {
        foreach (Node child in skeleton.GetChildren())
        {
            if (child is MeshInstance3D mesh && !mesh.Name.ToString().StartsWith(PiecePrefix)
                && !mesh.Name.ToString().Contains("FirstPerson"))
                return mesh;
        }
        return null;
    }

    // ------------------------------------------------------------------------------------
    //  Caricamento e materiali (condivisi fra tutti i personaggi)
    // ------------------------------------------------------------------------------------

    /// Mesh skinnate del modello indossabile, una volta per file. Vuoto se il file non c'e'.
    public static Piece[] LoadPieces(string path)
    {
        if (PiecesByPath.TryGetValue(path, out Piece[]? cached))
            return cached;

        var pieces = new List<Piece>();
        if (ResourceLoader.Exists(path) && GD.Load<PackedScene>(path) is { } scene)
        {
            Node root = scene.Instantiate();
            foreach (Node node in root.FindChildren("*", nameof(MeshInstance3D), true, false))
            {
                if (node is MeshInstance3D mi && mi.Mesh is ArrayMesh mesh)
                {
                    if (mesh.GetSurfaceCount() > 1)
                        GD.PushWarning($"[CharacterOutfit] {path}: {mesh.GetSurfaceCount()} superfici, si usa la texture della prima.");
                    pieces.Add(new Piece(mesh, mi.Skin, BaseTexture(mesh)));
                }
            }
            root.Free();
        }
        else
        {
            GD.PushWarning($"[CharacterOutfit] modello indossabile assente: {path}");
        }

        Piece[] result = pieces.ToArray();
        PiecesByPath[path] = result;
        return result;
    }

    public static Texture2D? BaseTexture(Mesh? mesh)
    {
        if (mesh == null || mesh.GetSurfaceCount() == 0)
            return null;
        return mesh.SurfaceGetMaterial(0) is BaseMaterial3D material ? material.AlbedoTexture : null;
    }

    /// Un materiale per texture, condiviso: la maschera e' per istanza, non per materiale.
    /// <paramref name="doubleSided"/>: i capi si vedono anche da dentro (interno scurito).
    public static ShaderMaterial MaterialFor(Texture2D? texture, bool doubleSided = false)
    {
        _shader ??= GD.Load<Shader>("res://wearables/shaders/character_psx.gdshader");
        _clothShader ??= GD.Load<Shader>("res://wearables/shaders/character_psx_cloth.gdshader");
        var key = texture ?? PlaceholderTexture;
        var cache = doubleSided ? ClothMaterialByTexture : MaterialByTexture;
        if (cache.TryGetValue(key, out ShaderMaterial? material))
            return material;
        material = new ShaderMaterial { Shader = doubleSided ? _clothShader : _shader };
        material.SetShaderParameter("albedo", key);
        cache[key] = material;
        return material;
    }

    private static Shader? _clothShader;
    private static readonly Dictionary<Texture2D, ShaderMaterial> ClothMaterialByTexture = new();

    private static Texture2D? _placeholder;
    private static Texture2D PlaceholderTexture
    {
        get
        {
            if (_placeholder != null)
                return _placeholder;
            var image = Image.CreateEmpty(2, 2, false, Image.Format.Rgb8);
            image.Fill(new Color(0.5f, 0.5f, 0.5f));
            _placeholder = ImageTexture.CreateFromImage(image);
            return _placeholder;
        }
    }
}
