using Godot;

namespace Lagoon;

/// <summary>
/// Aspetto ottico di un tipo d'acqua (mare, laguna, rio, ...): solo dati, nessuna logica
/// (CLAUDE.md §4: <c>resources/</c>). <see cref="WaterSurface"/> sceglie la tavolozza della zona in
/// cui si trova la camera (<see cref="WaterMap.ZoneAt"/>) e ci sfuma verso in qualche secondo: tutta
/// l'acqua visibile cambia colore insieme, entrando in mare o in un rio.
///
/// Il modello ottico e' quello dello shader: la luce del fondale attraversa l'acqua attenuata canale
/// per canale (<see cref="Absorption"/>, legge di Beer-Lambert: il rosso sparisce per primo), mentre
/// la torbidita' (<see cref="Turbidity"/>) sostituisce il fondale col colore diffuso
/// (<see cref="ScatterColor"/>), illuminato dal sole. Mare limpido: poca torbidita', molto
/// assorbimento del rosso → blu profondo. Laguna: molta torbidita' verde-oliva → si vede poco sotto.
///
/// <c>[Tool]</c> perche' la legge <see cref="WaterSurface"/>, che gira anche nell'editor: senza, nell'editor
/// la risorsa sarebbe un segnaposto senza la classe C# e il cast fallirebbe.
/// </summary>
[GlobalClass, Tool]
public partial class WaterPalette : Resource
{
    /// Classi di osm_terrain.py (vedi skill venice-terrain §2) a cui si applica: 0 mare, 2 laguna, ...
    [Export] public int[] ClassIds { get; set; } = System.Array.Empty<int>();

    /// Colore dell'acqua "piena" (torbida o profonda), illuminato come un materiale diffuso.
    [Export] public Color ScatterColor { get; set; } = new(0.18f, 0.32f, 0.28f);

    /// Assorbimento per metro, per canale RGB (Beer-Lambert). Piu' alto = scurisce prima.
    [Export] public Vector3 Absorption { get; set; } = new(0.8f, 0.4f, 0.45f);

    /// Torbidita' per metro: quanto in fretta il fondale sparisce nel colore diffuso.
    [Export] public float Turbidity { get; set; } = 0.6f;

    /// Moltiplica la schiuma (acqua salmastra e mossa ne fa di piu').
    [Export] public float FoamAmount { get; set; } = 1.0f;
}
