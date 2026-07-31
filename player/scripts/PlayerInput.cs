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
    private SettingsService _settings = null!;

    // Stato degli interruttori (modalita' "toggle" di mira e accovacciamento) e fronte di salita del
    // tasto corrispondente. Vedi UpdateToggles.
    private bool _aimLatched;
    private bool _crouchLatched;
    private bool _aimWasPressed;
    private bool _crouchWasPressed;
    private ulong _lastToggleFrame = ulong.MaxValue;

    public override void _Ready()
    {
        // L'autorita' del root e' gia' stata impostata nel suo _EnterTree.
        _isLocalAuthority = GetParent().IsMultiplayerAuthority();
        _game = GetNode<GameManager>("/root/GameManager");
        _settings = GetNode<SettingsService>("/root/SettingsService");
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

    /// Corsa (Shift tenuto). Come il movimento, e' soppressa al timone.
    public bool ReadSprint() => ReadHeld("sprint");

    /// <summary>
    /// Mira (tasto destro del mouse). A pressione mantenuta, oppure a interruttore se
    /// <see cref="SettingsService.AimToggle"/> e' attivo.
    /// </summary>
    public bool ReadAim()
    {
        UpdateToggles();
        return _settings.AimToggle ? _aimLatched && !Blocked() : ReadHeld("aim");
    }

    /// <summary>
    /// Accovacciamento (Ctrl). A pressione mantenuta, oppure a interruttore se
    /// <see cref="SettingsService.CrouchToggle"/> e' attivo.
    /// </summary>
    public bool ReadCrouch()
    {
        UpdateToggles();
        return _settings.CrouchToggle ? _crouchLatched && !Blocked() : ReadHeld("crouch");
    }

    /// <summary>
    /// Aggiorna gli interruttori di mira e accovacciamento una volta per frame di fisica.
    ///
    /// Il fronte di salita si calcola qui e non con <c>IsActionJustPressed</c> dentro le Read*: quelle
    /// sono chiamate piu' volte per frame dal <see cref="PlayerController"/> (e in ordine non
    /// garantito), e un interruttore che commuta a ogni lettura commuterebbe due volte. La guardia sul
    /// numero di frame rende l'aggiornamento indipendente da quante volte lo si chiede.
    ///
    /// Lo stato dei tasti si campiona SEMPRE, anche quando l'input di gameplay e' bloccato: cosi'
    /// rilasciare il tasto dentro l'inventario non si presenta come una nuova pressione alla chiusura.
    /// Cio' che si sospende e' solo la commutazione, non il campionamento.
    /// </summary>
    private void UpdateToggles()
    {
        ulong frame = Engine.GetPhysicsFrames();
        if (frame == _lastToggleFrame)
            return;
        _lastToggleFrame = frame;

        bool aimPressed = Input.IsActionPressed("aim");
        bool crouchPressed = Input.IsActionPressed("crouch");
        bool blocked = Blocked();

        if (aimPressed && !_aimWasPressed && !blocked && _settings.AimToggle)
            _aimLatched = !_aimLatched;

        if (crouchPressed && !_crouchWasPressed && !blocked && _settings.CrouchToggle)
            _crouchLatched = !_crouchLatched;

        _aimWasPressed = aimPressed;
        _crouchWasPressed = crouchPressed;

        // Passare da interruttore a pressione mantenuta (o salire al timone) non deve lasciare
        // l'avatar bloccato in mira o accovacciato.
        if (!_settings.AimToggle || MovementSuppressed)
            _aimLatched = false;
        if (!_settings.CrouchToggle || MovementSuppressed)
            _crouchLatched = false;
    }

    /// True quando l'input di gameplay locale non va letto (non e' il proprio avatar, UI modale
    /// aperta, oppure si e' al timone).
    private bool Blocked() => !_isLocalAuthority || _game.UiModalOpen || MovementSuppressed;

    /// <summary>
    /// Salto (Spazio). E' un evento, non uno stato: si consuma alla lettura tramite
    /// <c>IsActionJustPressed</c>, cosi' tenere premuto non produce salti ripetuti.
    /// </summary>
    public bool ReadJumpPressed()
    {
        return !Blocked() && Input.IsActionJustPressed("jump");
    }

    private bool ReadHeld(string action) => !Blocked() && Input.IsActionPressed(action);
}
