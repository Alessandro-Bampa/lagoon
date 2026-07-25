using Godot;

namespace Lagoon;

/// <summary>
/// Stato di gioco globale minimo (autoload). Tiene solo la fase corrente del prototipo.
/// Nessuna logica di gameplay qui: i sistemi verticali (player, combat, inventory) restano
/// nelle rispettive cartelle (CLAUDE.md §4/§5).
/// </summary>
public partial class GameManager : Node
{
    public enum GamePhase
    {
        MainMenu,
        InGame,
    }

    public GamePhase CurrentPhase { get; private set; } = GamePhase.MainMenu;

    /// True quando una UI modale (menu di pausa/impostazioni) ha il controllo dell'input.
    /// NON si usa GetTree().Paused: in multiplayer fermerebbe solo il peer locale, desincronizzandolo
    /// dagli altri (CLAUDE.md §3). Il mondo continua a girare, viene bloccato solo l'input di gameplay.
    public bool UiModalOpen { get; set; }

    public void SetPhase(GamePhase phase)
    {
        CurrentPhase = phase;
        GD.Print($"[GameManager] Fase corrente: {phase}");
    }
}
