using Godot;

namespace Lagoon;

/// <summary>
/// Collega lo stato del giocatore al <see cref="CharacterAnimator"/>.
///
/// Esiste per tenere <c>animation/</c> indipendente da <c>player/</c>: l'animatore e' un ricevitore
/// generico, questo e' l'adattatore che sa dove pescare lo stato di UN GIOCATORE. Quando arriveranno
/// gli NPC avranno il proprio adattatore e riuseranno lo stesso rig e lo stesso albero.
///
/// Legge SOLO stato gia' replicato (CLAUDE.md §3): <c>SyncLocalVelocity</c>, <c>SyncCrouching</c>,
/// <c>SyncGrounded</c>, <c>HeldItemId</c>. Gira identico sul peer proprietario e su quelli remoti —
/// e' proprio questo che rende gli avatar remoti coerenti senza inviare un solo dato in piu' per
/// l'animazione. Non valida nulla e non chiama mai <c>GetRemoteSenderId</c>.
/// </summary>
public partial class PlayerAnimationBridge : Node
{
    private PlayerController _controller = null!;
    private CharacterAnimator _animator = null!;
    private WeaponController? _weapon;
    private HealthComponent? _health;

    /// Ultima arma vista, per non risolvere il <c>.tres</c> a ogni frame.
    private string _lastHeldItemId = "";
    private WeaponAnimationSet? _cachedPose;

    /// Stato per la derivata smorzata di <c>SyncFacing</c> (velocita' di rotazione del corpo).
    private float _lastFacing;
    private float _smoothedTurnRate;

    public override void _Ready()
    {
        _controller = GetParent<PlayerController>();
        _animator = GetNode<CharacterAnimator>("../Visual/CharacterRig");
        _weapon = GetParent().GetNodeOrNull<WeaponController>("Weapon");

        // Gli eventi one-shot arrivano dalle RPC del controller, che le riemette come segnali locali
        // su OGNI peer: qui basta ascoltare, senza sapere da dove sono partiti.
        _controller.Jumped += OnJumped;
        _controller.Landed += OnLanded;
        _controller.Vaulted += OnVaulted;

        if (_weapon != null)
            _weapon.ShotResolved += OnShotResolved;

        // La hit reaction arriva dalla RPC estetica di HealthComponent, riemessa come segnale
        // locale su ogni peer: stesso schema di Jumped/Landed. Nel payload viaggia la sola
        // direzione del colpo (CLAUDE.md §3).
        _health = GetParent().GetNodeOrNull<HealthComponent>("Health");
        if (_health != null)
            _health.HitReaction += OnHitReaction;

        // Le grandezze del movimento arrivano DA QUI, non sono duplicate nell'animatore: le velocita'
        // definiscono la geometria dello spazio di blend, la soglia d'impatto duro l'ampiezza
        // dell'ammortizzazione. E' l'adattatore a conoscerle entrambe, per costruzione.
        _animator.WalkSpeed = _controller.WalkSpeed;
        _animator.RunSpeed = _controller.RunSpeed;
        _animator.CrouchSpeed = _controller.CrouchSpeed;
        _animator.HardLandingSpeed = _controller.HardLandingSpeed;

        // Durata reale del volo di un salto: sale e riscende, quindi 2 * v / g. Serve a riscalare la
        // clip di salto, che e' piu' lunga del volo vero e altrimenti resterebbe a mezz'aria dopo
        // l'atterraggio.
        _animator.JumpFlightTime = 2f * _controller.JumpVelocity / Mathf.Max(_controller.Gravity, 0.001f);
    }

    public override void _ExitTree()
    {
        _controller.Jumped -= OnJumped;
        _controller.Landed -= OnLanded;
        _controller.Vaulted -= OnVaulted;
        if (_weapon != null)
            _weapon.ShotResolved -= OnShotResolved;
        if (_health != null)
            _health.HitReaction -= OnHitReaction;
    }

    public override void _Process(double delta)
    {
        _animator.LocalVelocity = _controller.SyncLocalVelocity;
        _animator.Crouching = _controller.SyncCrouching;
        _animator.Grounded = _controller.SyncGrounded;
        _animator.WeaponPose = ResolveWeaponPose();
        _animator.Aiming = _controller.SyncAiming;

        // La direzione di mira si ricostruisce dagli angoli REPLICATI (SyncAimYaw + SyncAimPitch),
        // non da WeaponInput.AimPoint che esiste solo sul peer proprietario: cosi' il bridge gira
        // identico sull'avatar locale e su quelli remoti. SyncAimYaw e' distinto da SyncFacing
        // perche' in mira il busto puo' guardare dove il corpo non guarda.
        _animator.AimDirection = CharacterAnimator.AimVector(
            _controller.SyncAimYaw, _controller.SyncAimPitch);

        _animator.TurnRate = UpdateTurnRate((float)delta);
    }

    /// <summary>
    /// Velocita' di rotazione del corpo in rad/s, derivata da <c>SyncFacing</c> e smorzata. Si
    /// deriva da stato replicato invece di leggerla dal controller cosi' il valore e' identico su
    /// ogni peer: alimenta il passo sintetico del turn-in-place.
    /// </summary>
    private float UpdateTurnRate(float dt)
    {
        float facing = _controller.SyncFacing;
        float rate = dt > 0.0001f ? Mathf.AngleDifference(_lastFacing, facing) / dt : 0f;
        _lastFacing = facing;

        _smoothedTurnRate = Mathf.Lerp(_smoothedTurnRate, rate, 1f - Mathf.Exp(-10f * dt));
        return _smoothedTurnRate;
    }

    /// <summary>
    /// Posa dell'arma impugnata, ricavata dal solo <c>HeldItemId</c> replicato. Il risultato e' in
    /// cache: <c>ItemDatabase</c> viene interrogato solo quando l'arma cambia davvero.
    /// </summary>
    private WeaponAnimationSet? ResolveWeaponPose()
    {
        string held = _weapon?.HeldItemId ?? "";
        if (held == _lastHeldItemId)
            return _cachedPose;

        _lastHeldItemId = held;
        _cachedPose = (_weapon?.HeldWeapon)?.AnimationSet;
        return _cachedPose;
    }

    private void OnJumped() => _animator.TriggerJump();

    /// <summary>
    /// Atterraggio. La velocita' d'impatto viaggia nel payload della RPC come semplice grandezza
    /// fisica, non come esito di gioco: decide solo quanto flette il bacino (CLAUDE.md §3).
    /// </summary>
    private void OnLanded(float impactSpeed) => _animator.TriggerLand(impactSpeed);

    /// Scavalcamento: il punto del bordo (misura geometrica) alimenta l'IK delle mani.
    private void OnVaulted(Vector3 ledgePoint) => _animator.TriggerVault(ledgePoint);

    /// <summary>
    /// Lo sparo riusa il segnale gia' esistente di <see cref="WeaponController"/>, che l'host
    /// trasmette a tutti: non serve una RPC nuova per l'animazione, e soprattutto non va aggiunta —
    /// duplicherebbe un evento che viaggia gia'.
    /// </summary>
    private void OnShotResolved(Vector3 origin, Vector3 end, bool hit, bool isLocalShooter)
    {
        _animator.TriggerFire();
    }

    /// Flinch del colpo incassato: la direzione e' quella di volo del proiettile, in mondo.
    /// E' l'animatore a mapparla su front/back/left/right nel riferimento del rig.
    private void OnHitReaction(Vector3 worldDirection) =>
        _animator.TriggerHitReaction(worldDirection);
}
