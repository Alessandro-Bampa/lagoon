namespace Lagoon;

/// <summary>
/// Costanti di rete condivise. Nessuno stato: solo valori di configurazione.
/// </summary>
public static class NetworkConstants
{
    /// Porta usata dal trasporto ENet locale (fallback dev per test multi-istanza).
    public const int DefaultPort = 27015;

    /// AppID di test "Spacewar" di Valve, usato finche' non esiste un AppID reale (CLAUDE.md §6).
    public const uint SteamAppId = 480;

    /// Limite di giocatori del prototipo (CLAUDE.md §1: co-op fino a 4).
    public const int MaxPlayers = 4;

    /// ID del peer host nell'API Multiplayer di Godot (l'host e' sempre 1).
    public const int HostPeerId = 1;
}
