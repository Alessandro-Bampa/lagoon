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
    private GameManager _game = null!;

    public override void _Ready()
    {
        // L'autorita' del root e' gia' stata impostata nel suo _EnterTree.
        _isLocalAuthority = GetParent().IsMultiplayerAuthority();
        _game = GetNode<GameManager>("/root/GameManager");
    }

    /// <summary>
    /// True quando il movimento a piedi e' soppresso (il giocatore e' al timone di un veicolo): la
    /// direzione va all'imbarcazione, non all'avatar. La guardia sta qui, accanto alle altre due, e non
    /// sparsa nel <see cref="PlayerController"/>.
    /// </summary>
    public bool MovementSuppressed { get; set; }

    /// Vettore di movimento su piano XZ: X = destra/sinistra, Y = giu'/su (schermo).
    /// Ritorna zero se questo non e' il peer proprietario (input solo sul proprio peer, §3),
    /// se una UI modale (menu di pausa) sta assorbendo l'input locale, o se si sta guidando.
    public Vector2 ReadMovement()
    {
        return MovementSuppressed ? Vector2.Zero : ReadRawMovement();
    }

    /// <summary>
    /// Direzione grezza, ignorando <see cref="MovementSuppressed"/>: la legge
    /// <see cref="VehicleInput"/> per ricavarne acceleratore e timone. Restano attive le guardie su
    /// autorita' locale e UI modale.
    /// </summary>
    public Vector2 ReadRawMovement()
    {
        if (!_isLocalAuthority || _game.UiModalOpen)
            return Vector2.Zero;

        return Input.GetVector("move_left", "move_right", "move_up", "move_down");
    }
}
