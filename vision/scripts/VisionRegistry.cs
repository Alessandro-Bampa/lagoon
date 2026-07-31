using Godot;

namespace Lagoon;

/// <summary>
/// Lookup della <see cref="VisionSource"/> dell'avatar LOCALE. Nessuno stato: solo una ricerca
/// sull'albero, come <see cref="VehicleRegistry"/>.
///
/// Non e' un autoload e non e' un singleton di stato (vietati da CLAUDE.md §5): e' una ricerca per
/// gruppo, cioe' la stessa forma gia' usata per veicoli, pickup e mondo. Il gruppo contiene al
/// massimo un nodo per peer, perche' la visione e' INDIVIDUALE e ogni processo ha un solo avatar
/// locale. Se un giorno servisse la visione condivisa fra compagni, e' qui che cambierebbe:
/// il gruppo conterrebbe piu' sorgenti e l'unione andrebbe fatta dai chiamanti.
/// </summary>
public static class VisionRegistry
{
    /// <summary>
    /// La sorgente di visione dell'avatar locale, o null se non c'e' ancora un avatar (menu
    /// principale, caricamento del livello, avatar remoto non ancora spawnato). Chi la usa deve
    /// trattare il null come "nessun occultamento", mai come "non vedo nulla": all'avvio si
    /// vedrebbe un mondo completamente vuoto.
    /// </summary>
    public static VisionSource? Local(Node context)
        => context.GetTree().GetFirstNodeInGroup(VisionSource.LocalGroupName) as VisionSource;
}
