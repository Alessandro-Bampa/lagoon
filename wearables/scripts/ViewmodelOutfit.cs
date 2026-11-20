using System;
using System.Collections.Generic;
using Godot;

namespace Lagoon;

/// <summary>
/// Veste le braccia del viewmodel in prima persona: figlio di <see cref="ViewmodelAnimator"/>, esiste
/// solo sul peer proprietario. Ricevitore puro: il bridge gli scrive <see cref="Worn"/>.
///
/// Il viewmodel e' quello del Retro Weapon Pack (scheletro <c>FP_Arms</c>, clip, arma: da li'
/// dipendono aggancio dell'arma e tacche di mira), ma la mesh delle braccia del pack si nasconde e al
/// suo posto si agganciano allo stesso scheletro:
/// <list type="bullet">
/// <item>le braccia del corpo base, <c>Character_Base_FP.glb</c>;</item>
/// <item>la variante in prima persona di ogni capo che tocca le braccia
/// (<see cref="WearableDefinition.FirstPersonModelPath"/>: manica della giacca, guanti, fascia).</item>
/// </list>
/// Le genera <c>tools/blender/wearables/fp_arms.py</c> conformando corpo e capi al riposo
/// <c>FP_Arms</c>. Stesse texture, stesso shader e stesse maschere di regione di
/// <see cref="CharacterOutfit"/>: in prima e in terza persona si vede lo stesso vestito.
/// </summary>
public partial class ViewmodelOutfit : Node
{
    public const string BodyArmsPath = "res://assets/models/characters/Character_Base_FP.glb";
    private const string PackArmsPrefix = "FPS_Arms";
    private const string PiecePrefix = "Wear_";

    private ViewmodelAnimator _viewmodel = null!;
    private ItemDatabase? _db;
    private string[] _worn = Array.Empty<string>();
    private bool _dirty = true;

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

    public override void _Ready()
    {
        _viewmodel = GetParent<ViewmodelAnimator>();
        _db = GetNodeOrNull<ItemDatabase>("/root/ItemDatabase");
        _viewmodel.ModelSwapped += () => _dirty = true;
    }

    public override void _Process(double delta)
    {
        if (!_dirty)
            return;
        _dirty = false;
        if (_viewmodel.ArmsSkeleton is { } skeleton && skeleton.IsInsideTree())
            Apply(skeleton);
    }

    private void Apply(Skeleton3D skeleton)
    {
        uint layers = 1;
        foreach (Node child in skeleton.GetChildren())
        {
            if (child is not MeshInstance3D mesh)
                continue;
            string name = mesh.Name.ToString();
            if (name.StartsWith(PackArmsPrefix))
            {
                layers = mesh.Layers;
                mesh.Visible = false;
            }
            else if (name.StartsWith(PiecePrefix))
            {
                skeleton.RemoveChild(mesh);
                mesh.QueueFree();
            }
        }

        var defs = new List<WearableDefinition>();
        foreach (string id in _worn)
        {
            if (_db?.Get(id) is WearableDefinition def)
                defs.Add(def);
        }

        Attach(skeleton, CharacterOutfit.LoadPieces(BodyArmsPath), PiecePrefix + "Body",
            CharacterOutfit.HiddenBy(defs, 0), layers);
        foreach (WearableDefinition def in defs)
        {
            if (!ResourceLoader.Exists(def.FirstPersonModelPath))
                continue;
            Attach(skeleton, CharacterOutfit.LoadPieces(def.FirstPersonModelPath), PiecePrefix + def.ItemId,
                CharacterOutfit.HiddenBy(defs, def.Layer), layers);
        }
    }

    private static void Attach(Skeleton3D skeleton, CharacterOutfit.Piece[] pieces, string name, BodyRegion hidden, uint layers)
    {
        for (int i = 0; i < pieces.Length; i++)
        {
            var mesh = new MeshInstance3D
            {
                Name = $"{name}_{i}",
                Mesh = pieces[i].Mesh,
                Skin = pieces[i].Skin,
                Skeleton = new NodePath(".."),
                Layers = layers,
                MaterialOverride = CharacterOutfit.MaterialFor(pieces[i].Texture, doubleSided: name != PiecePrefix + "Body"),
                // come le braccia del pack: niente ombre (le fa il corpo) e margine di culling ampio
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                ExtraCullMargin = 2.0f,
            };
            skeleton.AddChild(mesh);
            mesh.SetInstanceShaderParameter(CharacterOutfit.HiddenRegionsParam, (int)hidden);
        }
    }
}
