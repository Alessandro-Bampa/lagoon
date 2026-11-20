using Godot;

namespace Lagoon;

/// <summary>
/// Collega lo stato di un NPC al suo <see cref="CharacterAnimator"/>.
///
/// E' il gemello di <see cref="PlayerAnimationBridge"/>, e la sua esistenza e' il collaudo di un
/// invariante: <c>animation/</c> non dipende da <c>player/</c>. Il rig, l'albero e i modificatori
/// di scheletro sono gli stessi identici oggetti — cambia solo chi scrive le proprieta' in ingresso.
///
/// Legge SOLO stato gia' replicato, quindi gira identico sull'host e su ogni client (CLAUDE.md §3).
/// </summary>
public partial class NpcAnimationBridge : Node
{
    private NpcController _controller = null!;
    private CharacterAnimator _animator = null!;
    private CharacterOutfit? _outfit;
    private CharacterAppearance? _appearance;
    private HealthComponent? _health;

    public override void _Ready()
    {
        _controller = GetParent<NpcController>();
        _animator = GetNode<CharacterAnimator>("../Visual/CharacterRig");
        _outfit = _animator.GetNodeOrNull<CharacterOutfit>("Outfit");
        _appearance = GetParent().GetNodeOrNull<CharacterAppearance>("Appearance");

        _controller.Jumped += OnJumped;
        _controller.Landed += OnLanded;
        _controller.Vaulted += OnVaulted;

        // Stesso schema del giocatore: la RPC estetica di HealthComponent viene riemessa come
        // segnale locale su ogni peer, e qui si trasforma in flinch animato.
        _health = GetParent().GetNodeOrNull<HealthComponent>("Health");
        if (_health != null)
            _health.HitReaction += OnHitReaction;

        // Le velocita' sono i punti degli spazi di blend: vanno prese da chi si muove, non
        // ridichiarate qui, altrimenti divergono in silenzio e i piedi scivolano.
        _animator.WalkSpeed = _controller.WalkSpeed;
        _animator.RunSpeed = _controller.RunSpeed;
        _animator.CrouchSpeed = _controller.CrouchSpeed;
        _animator.HardLandingSpeed = _controller.HardLandingSpeed;
    }

    public override void _ExitTree()
    {
        _controller.Jumped -= OnJumped;
        _controller.Landed -= OnLanded;
        _controller.Vaulted -= OnVaulted;
        if (_health != null)
            _health.HitReaction -= OnHitReaction;
    }

    public override void _Process(double delta)
    {
        // Vestiti: stato replicato dall'host (CharacterAppearance), resa locale (CharacterOutfit).
        if (_outfit != null && _appearance != null)
            _outfit.Worn = _appearance.WornItemIds;

        _animator.LocalVelocity = _controller.SyncLocalVelocity;
        _animator.Crouching = _controller.SyncCrouching;
        _animator.Grounded = _controller.SyncGrounded;
        _animator.Vaulting = _controller.SyncVaulting;
        _animator.Dead = _health?.IsDead ?? false;

        // Nessuna arma per ora: Weapon resta null e le mani restano alla locomozione. Quando gli
        // NPC saranno armati bastera' che il controller riempia Weapon e SyncAiming — lo sguardo
        // qui sotto e' gia' quello del giocatore.
        _animator.Weapon = null;
        _animator.Aiming = _controller.SyncAiming;
        _animator.LookYaw = _controller.SyncAimYaw;
        _animator.LookPitch = _controller.SyncAimPitch;
    }

    private void OnJumped() => _animator.TriggerJump();

    private void OnLanded(float impactSpeed) => _animator.TriggerLand(impactSpeed);

    private void OnVaulted(Vector3 ledgePoint, Vector3 wallNormal, float height)
    {
        bool mantle = height > _controller.VaultMaxHeight;
        _animator.TriggerVault(ledgePoint, wallNormal, mantle,
            mantle ? _controller.MantleDuration : _controller.VaultDuration);
    }

    private void OnHitReaction(Vector3 worldDirection) =>
        _animator.TriggerHitReaction(worldDirection);
}
