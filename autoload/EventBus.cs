using Godot;

namespace Lagoon;

/// <summary>
/// Bus di eventi globale (autoload). Serve SOLO per comunicazione fra sistemi non collegati
/// direttamente nella scene tree (CLAUDE.md §5), es. NetworkManager (autoload) -> GameWorld (scena).
/// Non contiene logica di gioco ne' stato: e' un semplice hub di segnali.
/// </summary>
public partial class EventBus : Node
{
    /// Emesso (lato host) quando un peer entra in partita: id del peer connesso.
    [Signal]
    public delegate void PeerJoinedEventHandler(long peerId);

    /// Emesso (lato host) quando un peer lascia la partita.
    [Signal]
    public delegate void PeerLeftEventHandler(long peerId);

    /// Emesso quando la connessione al server e' pronta lato client locale.
    [Signal]
    public delegate void ConnectedToServerEventHandler();

    /// Emesso quando la connessione fallisce o cade (client) / la sessione termina.
    [Signal]
    public delegate void NetworkErrorEventHandler(string message);
}
