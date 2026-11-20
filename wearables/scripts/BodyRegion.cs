using System;

namespace Lagoon;

/// <summary>
/// Regioni del corpo, una per bit. Ogni mesh del personaggio (corpo, capi, varianti in prima persona)
/// porta l'INDICE della regione nel colore di vertice: <c>COLOR.r = indice / 255</c>, scritto da
/// <c>tools/blender/charlib.py</c> (<c>REGIONS</c>, stesso ordine). Lo shader del personaggio
/// scarta i frammenti delle regioni nascoste (<see cref="CharacterOutfit"/>).
///
/// Lati uniti: braccio destro e sinistro sono la stessa regione. Si appende in coda.
/// </summary>
[Flags]
public enum BodyRegion
{
    None = 0,
    Scalp = 1 << 0,     // cranio e nuca
    Face = 1 << 1,      // fascia degli occhi (e gli occhi)
    Jaw = 1 << 2,       // naso in giu': bocca, mento, guance basse
    Neck = 1 << 3,
    Chest = 1 << 4,     // petto, spalle, schiena alta
    Abdomen = 1 << 5,
    Pelvis = 1 << 6,    // dove stanno i boxer
    UpperArm = 1 << 7,
    Forearm = 1 << 8,
    Hand = 1 << 9,
    Thigh = 1 << 10,
    Calf = 1 << 11,
    Ankle = 1 << 12,    // gli ultimi 10 cm della gamba e il collo del piede
    Foot = 1 << 13,
}
