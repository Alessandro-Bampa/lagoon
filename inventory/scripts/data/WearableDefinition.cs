using Godot;

namespace Lagoon;

/// <summary>
/// Oggetto che si vede addosso quando e' indossato: capi, elmetti, armature, rig, zaini.
/// Dati puri (CLAUDE.md §4): come si mostra lo decide <see cref="CharacterOutfit"/>.
///
/// Il modello indossato e' una mesh skinnata sullo stesso scheletro del corpo
/// (<c>assets/models/wearables/&lt;ItemId&gt;.glb</c>, generata da
/// <c>tools/blender/wearables/build_wearable.py</c>, skill <c>character-wearables</c>); la variante in
/// prima persona, per i capi che toccano le braccia, sta in <c>wearables/fp/&lt;ItemId&gt;_FP.glb</c>
/// e si trova per convenzione. Se il file manca l'oggetto resta invisibile addosso.
/// </summary>
[GlobalClass]
public partial class WearableDefinition : ItemDefinition
{
    /// Strato: 0 pelle, 1 a contatto (pantaloni, guanti, passamontagna), 2 sopra (giacche,
    /// calzature, occhiali, cuffie), 3 fasce e corpetto, 4 rig, zaino ed elmetto. Una regione
    /// coperta da un capo di strato N si nasconde sul corpo e su tutti i capi di strato minore.
    /// Lo scrive la pipeline dalla ricetta, insieme a <see cref="CoversRegions"/>.
    [Export(PropertyHint.Range, "1,4")] public int Layer { get; set; } = 1;

    /// Regioni che questo capo nasconde (del corpo e degli strati sotto).
    [Export] public BodyRegion CoversRegions { get; set; } = BodyRegion.None;

    public string WornModelPath => $"res://assets/models/wearables/{ItemId}.glb";
    public string FirstPersonModelPath => $"res://assets/models/wearables/fp/{ItemId}_FP.glb";
}
