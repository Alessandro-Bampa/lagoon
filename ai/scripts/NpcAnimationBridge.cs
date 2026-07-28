using Godot;

namespace Lagoon;

/// <summary>
/// Collega lo stato di un NPC al suo <see cref="CharacterAnimator"/>.
///
/// E' il gemello di <see cref="PlayerAnimationBridge"/>, e la sua esistenza e' il collaudo di un
/// invariante: <c>animation/</c> non dipende da <c>player/</c>. Il rig, l'albero, la mira
/// procedurale, i piedi a terra e la reazione ai muri sono gli stessi identici oggetti — cambia
/// solo chi scrive le proprieta' in ingresso.
///
/// Legge SOLO stato gia' replicato, quindi gira identico sull'host e su ogni client (CLAUDE.md §3).
/// </summary>
public partial class NpcAnimationBridge : Node
{
    private NpcController _controller = null!;
    private CharacterAnimator _animator = null!;

    /// Stato per la derivata smorzata di <c>SyncFacing</c>.
    private float _lastFacing;
    private float _smoothedTurnRate;

    public override void _Ready()
    {
        _controller = GetParent<NpcController>();
        _animator = GetNode<CharacterAnimator>("../Visual/CharacterRig");

        _controller.Jumped += OnJumped;
        _controller.Landed += OnLanded;

        // Le velocita' definiscono la geometria degli spazi di blend: vanno prese da chi si muove,
        // non ridichiarate qui, altrimenti divergono in silenzio e la locomozione va in T-pose ai
        // bordi del rombo.
        _animator.WalkSpeed = _controller.WalkSpeed;
        _animator.RunSpeed = _controller.RunSpeed;
        _animator.CrouchSpeed = _controller.CrouchSpeed;
        _animator.HardLandingSpeed = _controller.HardLandingSpeed;
        _animator.JumpFlightTime = 2f * _controller.JumpVelocity / Mathf.Max(_controller.Gravity, 0.001f);
    }

    public override void _ExitTree()
    {
        _controller.Jumped -= OnJumped;
        _controller.Landed -= OnLanded;
    }

    public override void _Process(double delta)
    {
        _animator.LocalVelocity = _controller.SyncLocalVelocity;
        _animator.Crouching = _controller.SyncCrouching;
        _animator.Grounded = _controller.SyncGrounded;

        // Nessuna arma per ora: WeaponPose resta null e SyncAiming resta false, quindi stance e
        // mira procedurale restano spenti da soli. Quando gli NPC saranno armati bastera' che il
        // controller riempia WeaponPose, SyncAiming, SyncAimYaw e SyncAimPitch — non c'e' altro
        // da collegare: la ricostruzione della mira qui sotto e' gia' quella del giocatore.
        _animator.WeaponPose = null;
        _animator.Aiming = _controller.SyncAiming;
        _animator.AimDirection = CharacterAnimator.AimVector(
            _controller.SyncAimYaw, _controller.SyncAimPitch);
        _animator.TurnRate = UpdateTurnRate((float)delta);
    }

    /// Derivata smorzata di SyncFacing, come nel bridge del giocatore: identica su ogni peer.
    private float UpdateTurnRate(float dt)
    {
        float facing = _controller.SyncFacing;
        float rate = dt > 0.0001f ? Mathf.AngleDifference(_lastFacing, facing) / dt : 0f;
        _lastFacing = facing;

        _smoothedTurnRate = Mathf.Lerp(_smoothedTurnRate, rate, 1f - Mathf.Exp(-10f * dt));
        return _smoothedTurnRate;
    }

    private void OnJumped() => _animator.TriggerJump();

    private void OnLanded(float impactSpeed) => _animator.TriggerLand(impactSpeed);
}
