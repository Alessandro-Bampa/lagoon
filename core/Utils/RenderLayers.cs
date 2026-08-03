namespace Lagoon;

/// <summary>
/// Maschere dei render layer 3D (<c>VisualInstance3D.Layers</c> e <c>Camera3D.CullMask</c>),
/// speculari alla sezione <c>[layer_names]</c> di <c>project.godot</c>. Come
/// <see cref="CollisionLayers"/>: solo costanti, nessuno stato.
///
/// Sono una cosa DIVERSA dai collision layer, pur avendo lo stesso aspetto: qui si decide solo cosa
/// una camera disegna, mai cosa esiste per la fisica. E' l'intera ragione per cui il cutaway degli
/// edifici (skill <c>building-cutaway</c>) puo' essere una decisione locale del singolo peer senza
/// violare CLAUDE.md §3: la simulazione non se ne accorge.
///
/// Regola d'oro: <see cref="Always"/> deve restare acceso in QUALUNQUE maschera di culling. Ci
/// stanno terreno, personaggi, pickup, effetti e — soprattutto — il quad dello Shroud
/// (<c>ShroudRenderer</c>), che non imposta <c>Layers</c> e quindi eredita il layer 1: spegnerlo
/// significa far sparire la nebbia dinamica.
/// </summary>
public static class RenderLayers
{
    /// Tutto cio' che non appartiene alla struttura di un edificio. Mai nascosto.
    public const uint Always = 1 << 0;

    /// Numero massimo di piani gestiti dal cutaway (layer 2..7).
    public const int MaxFloors = 6;

    /// Primo bit dei layer di piano. Il piano N sta sul layer <c>2 + N</c>.
    private const int FirstFloorBit = 1;

    /// <summary>
    /// Bit del piano <paramref name="index"/> (0 = piano terra). Fuori intervallo restituisce 0,
    /// cioe' "nessun layer": una mesh con maschera 0 non viene mai disegnata, ed e' il fallimento
    /// piu' visibile possibile — meglio di un piano che si accende insieme a un altro.
    ///
    /// Non esiste un layer separato per l'involucro esterno: da quando i muri esterni non spariscono
    /// piu' ma SFUMANO (<c>BuildingCullController</c>), un muro dell'involucro appartiene
    /// semplicemente al proprio piano, e cosi' quello dei piani superiori viene culled insieme al
    /// resto del piano.
    /// </summary>
    public static uint FloorLayerBit(int index)
    {
        if (index < 0 || index >= MaxFloors)
            return 0;
        return 1u << (FirstFloorBit + index);
    }

    /// Tutti i layer di piano insieme: la maschera "vedo gli edifici interi", usata da fuori.
    public static uint AllFloorsMask
    {
        get
        {
            uint mask = 0;
            for (int i = 0; i < MaxFloors; i++)
                mask |= FloorLayerBit(i);
            return mask;
        }
    }

    /// <summary>
    /// Maschera dei piani da <c>0</c> a <paramref name="topIndex"/> incluso.
    ///
    /// I piani SOTTO quello corrente restano accesi di proposito: nasconderli lascerebbe vedere il
    /// cielo attraverso il vano scala, che e' peggio del problema che il cutaway risolve.
    /// </summary>
    public static uint FloorsUpTo(int topIndex)
    {
        uint mask = 0;
        for (int i = 0; i <= topIndex && i < MaxFloors; i++)
            mask |= FloorLayerBit(i);
        return mask;
    }

    /// Tutti i 20 layer visivi esposti da Godot: il <c>CullMask</c> di default di una camera.
    public const uint AllLayers = 0xFFFFF;

    /// <summary>
    /// Tutto cio' che il cutaway non gestisce: i layer degli edifici tolti dal totale.
    ///
    /// E' la base da cui si costruisce ogni maschera, e non <see cref="Always"/>, perche' il
    /// controller deve solo TOGLIERE i layer degli edifici — mai decidere per i layer che qualcun
    /// altro potrebbe usare in futuro. Partendo da <see cref="Always"/> si spegnerebbero in silenzio
    /// i layer 8-20 per il solo giocatore locale, ed e' il tipo di bug che si scopre mesi dopo.
    /// </summary>
    public static uint NonBuildingMask => AllLayers & ~AllFloorsMask;

    /// Maschera di default della camera: si vede tutto. E' lo stato "fuori da ogni edificio".
    public static uint Everything => AllLayers;
}
