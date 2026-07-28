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
    private PlayerHud _hud = null!;
    private IsometricCamera _camera = null!;
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
        _hud = GetNode<PlayerHud>("../Hud");
        _camera = GetNode<IsometricCamera>("../PlayerCamera");
        _ownHitbox = GetParent().GetNodeOrNull<HitboxComponent>("Hitbox");
        _game = GetNode<GameManager>("/root/GameManager");
    }

    public override void _Process(double delta)
    {
        // Il punto di mira si aggiorna SEMPRE, anche a inventario aperto: e' resa locale, non
        // un'azione. Congelarlo lasciava PlayerController.UpdateAiming puntato su un punto
        // stantio, e alla chiusura della UI il corpo scattava verso dove il mouse ERA.
        AimPoint = AimResolver.ResolveAimPoint(
            _camera, GetViewport().GetMousePosition(), _ownHitbox?.GetRid() ?? default);

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

    /// L'input di combattimento tace quando una modale (menu di pausa, skill ui-hud) o l'inventario
    /// stanno assorbendo l'attenzione del giocatore.
    private bool Suppressed => _game.UiModalOpen || _hud.InventoryOpen;
}
