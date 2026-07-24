using Godot;

namespace Lagoon;

/// <summary>
/// Raccolta dell'input LOCALE del giocatore. Nessuna logica di stato o di movimento
/// (CLAUDE.md §4): espone solo il vettore di movimento grezzo, letto dal
/// <see cref="PlayerController"/> quando questo peer e' l'autorita' del proprio avatar.
/// </summary>
public partial class PlayerInput : Node
{
    private bool _isLocalAuthority;

    public override void _Ready()
    {
        // L'autorita' del root e' gia' stata impostata nel suo _EnterTree.
        _isLocalAuthority = GetParent().IsMultiplayerAuthority();
    }

    /// Vettore di movimento su piano XZ: X = destra/sinistra, Y = giu'/su (schermo).
    /// Ritorna zero se questo non e' il peer proprietario (input solo sul proprio peer, §8).
    public Vector2 ReadMovement()
    {
        if (!_isLocalAuthority)
            return Vector2.Zero;

        return Input.GetVector("move_left", "move_right", "move_up", "move_down");
    }
}
