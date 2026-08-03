using Godot;

namespace Lagoon;

/// <summary>
/// Raccolta dell'input di combattimento LOCALE (CLAUDE.md §4: nessuna logica di stato qui dentro).
/// Traduce mouse e tastiera in "intenti" da inoltrare al <see cref="WeaponController"/>, che li
/// valida sull'host. Nulla di quello che accade qui e' vincolante per la partita.
///
/// Usa <c>_UnhandledInput</c> e non <c>_Input</c>: e' cosi' che si risolve la doppia assegnazione
/// del tasto R. <see cref="PlayerHud"/> intercetta <c>rotate_item</c> in <c>_Input</c> e lo consuma
/// SOLO quando la schermata inventario e' aperta; a inventario chiuso l'evento resta unhandled e
/// arriva qui come <c>reload</c>. Anche i click del mouse sull'inventario non arrivano mai qui,
/// perche' i Control della UI li fermano prima.
/// </summary>
public partial class WeaponInput : Node
{
    private WeaponController _weapon = null!;
    private PlayerController _player = null!;
    private PlayerHud _hud = null!;
    private IsometricCamera _camera = null!;
    private BuildingCullController? _buildingCull;
    private HitboxComponent? _ownHitbox;
    private GameManager _game = null!;

    /// Punto di mira corrente, ricalcolato ogni frame. Letto anche dal reticolo per sapere a che
    /// distanza sta puntando il giocatore (la dispersione dipende da quella).
    public Vector3 AimPoint { get; private set; }

    /// Distanza dal tiratore al punto di mira, in metri.
    public float AimDistance { get; private set; }

    private ulong _lastFireMsec;

    public override void _Ready()
    {
        // L'autorita' della root e' gia' stata impostata nel suo _EnterTree.
        if (!GetParent().IsMultiplayerAuthority())
        {
            SetProcess(false);
            SetProcessUnhandledInput(false);
            return;
        }

        _weapon = GetNode<WeaponController>("../Weapon");
        _player = GetParent<PlayerController>();
        _hud = GetNode<PlayerHud>("../Hud");
        _camera = GetNode<IsometricCamera>("../PlayerCamera");
        _buildingCull = GetNodeOrNull<BuildingCullController>("../BuildingCull");
        _ownHitbox = GetParent().GetNodeOrNull<HitboxComponent>("Hitbox");
        _game = GetNode<GameManager>("/root/GameManager");
    }

    public override void _Process(double delta)
    {
        // Il punto di mira si aggiorna SEMPRE, anche a inventario aperto: e' resa locale, non
        // un'azione. Congelarlo lasciava PlayerController.UpdateAiming puntato su un punto
        // stantio, e alla chiusura della UI il corpo scattava verso dove il mouse ERA.
        //
        // Due quote, entrambe dal contesto di chi mira e non costanti globali: il pavimento su cui
        // si sta (piano di ripiego quando il cursore non trova geometria) e il soffitto della stanza
        // (oltre il quale il raggio non deve nemmeno partire, o si aggancia ai muri invisibili del
        // piano di sopra — skill building-cutaway).
        AimPoint = AimResolver.ResolveAimPoint(
            _camera,
            GetViewport().GetMousePosition(),
            _ownHitbox?.GetRid() ?? default,
            _player.ResolvedFeetY,
            _buildingCull?.AimCeilingHeight ?? float.PositiveInfinity);

        Vector3 muzzle = GetParent<Node3D>().GlobalPosition + Vector3.Up * WeaponController.MuzzleHeight;
        AimDistance = muzzle.DistanceTo(AimPoint);

        // Le AZIONI invece restano soppresse: sparare attraverso l'inventario no.
        if (Suppressed)
            return;

        // Fuoco automatico: il rateo locale evita di inondare la rete di richieste che l'host
        // scarterebbe comunque per cooldown.
        WeaponDefinition? weapon = _weapon.HeldWeapon;
        if (weapon is not { Automatic: true } || !Input.IsActionPressed("fire"))
            return;

        ulong now = Time.GetTicksMsec();
        if (now - _lastFireMsec < (ulong)weapon.ShotIntervalMsec)
            return;

        _lastFireMsec = now;
        _weapon.SubmitFire(AimPoint);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Suppressed)
            return;

        if (@event.IsActionPressed("weapon_slot_1"))
        {
            _weapon.SubmitHold(EquipSlotType.WeaponPrimary);
            GetViewport().SetInputAsHandled();
            return;
        }
        if (@event.IsActionPressed("weapon_slot_2"))
        {
            _weapon.SubmitHold(EquipSlotType.WeaponSecondary);
            GetViewport().SetInputAsHandled();
            return;
        }
        if (@event.IsActionPressed("weapon_slot_3"))
        {
            _weapon.SubmitHold(EquipSlotType.Sidearm);
            GetViewport().SetInputAsHandled();
            return;
        }

        if (!_weapon.IsArmed)
            return;

        if (@event.IsActionPressed("reload"))
        {
            _weapon.SubmitReload();
            GetViewport().SetInputAsHandled();
            return;
        }

        // Semiautomatico: un colpo per pressione. L'automatico e' gestito in _Process.
        if (@event.IsActionPressed("fire") && _weapon.HeldWeapon is { Automatic: false })
        {
            _lastFireMsec = Time.GetTicksMsec();
            _weapon.SubmitFire(AimPoint);
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>
    /// L'input di combattimento tace quando una modale (menu di pausa, skill ui-hud) o l'inventario
    /// stanno assorbendo l'attenzione del giocatore, e mentre le mani sono impegnate a scavalcare o
    /// ad arrampicarsi.
    ///
    /// Il parkour e' gia' rifiutato dall'host (<c>WeaponController.HandsFree</c>): questo e' il lato
    /// LOCALE della stessa regola, e serve perche' la vampa alla bocca e' immediata (skill
    /// combat-shooting §5) — senza, il proprietario vedrebbe l'arma sparare mentre le mani sono sul
    /// bordo, per un colpo che l'host non conta. Si legge lo stato locale del motore, che sul peer
    /// proprietario e' sempre quello vero.
    /// </summary>
    private bool Suppressed => _game.UiModalOpen || _hud.InventoryOpen || _player.Vaulting;
}
