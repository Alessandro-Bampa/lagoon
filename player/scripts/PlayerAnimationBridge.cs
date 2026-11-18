using Godot;

namespace Lagoon;

/// <summary>
/// Collega lo stato del giocatore ai due animatori: il corpo in terza persona
/// (<see cref="CharacterAnimator"/>, su ogni peer) e il viewmodel in prima persona
/// (<see cref="ViewmodelAnimator"/>, solo sul peer proprietario).
///
/// Esiste per tenere <c>animation/</c> indipendente da <c>player/</c>: gli animatori sono
/// ricevitori generici, questo e' l'adattatore che sa dove pescare lo stato di UN GIOCATORE. Gli
/// NPC hanno il proprio (<c>NpcAnimationBridge</c>) e riusano lo stesso rig.
///
/// Legge SOLO stato gia' replicato (CLAUDE.md §3): gira identico sul peer proprietario e su
/// quelli remoti, ed e' questo che rende gli avatar remoti coerenti senza inviare un solo dato
/// in piu' per l'animazione. Non valida nulla e non chiama mai <c>GetRemoteSenderId</c>.
/// </summary>
public partial class PlayerAnimationBridge : Node
{
    private PlayerController _controller = null!;
    private CharacterAnimator _animator = null!;
    private WeaponController? _weapon;
    private HealthComponent? _health;

    /// Camera e viewmodel: solo sul peer proprietario, altrimenti null.
    private FirstPersonCamera? _camera;
    private ViewmodelAnimator? _viewmodel;

    /// Ultima arma vista, per non risolvere il <c>.tres</c> a ogni frame.
    private string _lastHeldItemId = "";
    private WeaponAnimationSet? _cachedSet;

    public override void _Ready()
    {
        _controller = GetParent<PlayerController>();
        _animator = GetNode<CharacterAnimator>("../Visual/CharacterRig");
        _weapon = GetParent().GetNodeOrNull<WeaponController>("Weapon");
        _health = GetParent().GetNodeOrNull<HealthComponent>("Health");

        // Gli eventi one-shot arrivano dalle RPC del controller, che le riemette come segnali locali
        // su OGNI peer: qui basta ascoltare, senza sapere da dove sono partiti.
        _controller.Jumped += OnJumped;
        _controller.Landed += OnLanded;
        _controller.Vaulted += OnVaulted;
        if (_weapon != null)
            _weapon.ShotResolved += OnShotResolved;
        if (_health != null)
            _health.HitReaction += OnHitReaction;

        // Le grandezze del movimento arrivano DA QUI, non sono duplicate negli animatori: sono i
        // punti degli spazi di blend, e se divergono dal controller i piedi scivolano.
        _animator.WalkSpeed = _controller.WalkSpeed;
        _animator.RunSpeed = _controller.RunSpeed;
        _animator.CrouchSpeed = _controller.CrouchSpeed;
        _animator.HardLandingSpeed = _controller.HardLandingSpeed;
        _animator.SwimSpeed = _controller.SwimSpeed;
        // Le clip di nuoto hanno la superficie all'origine: la si porta dove il motore tiene l'acqua.
        _animator.SwimBodyLift = _controller.EyeHeight - _controller.SwimEyeAboveSurface;

        if (!_controller.IsMultiplayerAuthority())
            return;

        _animator.FirstPersonOwner = true;
        _camera = GetParent().GetNodeOrNull<FirstPersonCamera>("PlayerCamera");
        _viewmodel = _camera?.GetNodeOrNull<ViewmodelAnimator>("Viewmodel");
        if (_viewmodel != null)
        {
            _viewmodel.WalkSpeed = _controller.WalkSpeed;
            _viewmodel.RunSpeed = _controller.RunSpeed;
            _viewmodel.CrouchSpeed = _controller.CrouchSpeed;
        }
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
        WeaponAnimationSet? weapon = ResolveWeaponSet();
        bool reloading = _weapon?.Reloading ?? false;

        _animator.LocalVelocity = _controller.SyncLocalVelocity;
        _animator.Crouching = _controller.SyncCrouching;
        _animator.Grounded = _controller.SyncGrounded;
        _animator.Vaulting = _controller.SyncVaulting;
        _animator.Swimming = _controller.SyncSwimming;

        // Al timone: seduti, mani sul governo della barca. La modalita' si deduce su ogni peer dal
        // pilota REPLICATO della barca (PlayerController.Mode), le mani da nodi della barca mossi
        // dallo stato replicato di sterzo: niente stato nuovo per l'animazione.
        BoatAssembly? helm = _controller.Mode == PlayerMode.Driving ? _controller.DrivingBoat?.Assembly : null;
        _animator.Seated = helm != null;
        _animator.SeatPose = helm?.Helm == HelmType.Wheel ? 1f : 0f;
        _animator.SeatTiller = helm?.Helm == HelmType.Tiller;
        _animator.SeatLeftHand = helm?.LeftHandTarget;
        _animator.SeatRightHand = helm?.RightHandTarget;
        _animator.Aiming = _controller.SyncAiming;
        _animator.Reloading = reloading;
        _animator.Dead = _health?.IsDead ?? false;
        _animator.Weapon = weapon;

        // Lo sguardo si ricostruisce dagli angoli REPLICATI, non dalla camera, che esiste solo
        // sul peer proprietario: cosi' il bridge gira identico su ogni peer.
        _animator.LookYaw = _controller.SyncAimYaw;
        _animator.LookPitch = _controller.SyncAimPitch;

        // Visuale da fuori (terza persona al timone, o qualunque altra camera corrente): si vede il
        // proprio corpo intero e non il viewmodel, che fluttuerebbe all'altezza degli occhi.
        bool firstPersonView = _camera == null || _camera.Current;
        _animator.ThirdPersonView = !firstPersonView;

        if (_viewmodel == null)
            return;

        _viewmodel.Visible = firstPersonView;
        _viewmodel.SetWeapon(weapon);
        _viewmodel.Speed = _controller.SyncLocalVelocity.Length();
        _viewmodel.Grounded = _controller.SyncGrounded;
        _viewmodel.Crouching = _controller.SyncCrouching;
        _viewmodel.Aiming = _controller.SyncAiming;
        _viewmodel.Reloading = reloading;
        // A nuoto le mani nuotano: niente viewmodel di nuoto (lacuna), l'arma esce di vista.
        _viewmodel.Lowered = _controller.SyncVaulting || _controller.SyncSwimming || helm != null;
        _viewmodel.MagazineEmpty = _weapon is { IsArmed: true, MagazineAmmo: <= 0 };
    }

    /// <summary>
    /// Set dell'arma impugnata, ricavato dal solo <c>HeldItemId</c> replicato. Il risultato e' in
    /// cache: <c>ItemDatabase</c> viene interrogato solo quando l'arma cambia davvero.
    /// </summary>
    private WeaponAnimationSet? ResolveWeaponSet()
    {
        string held = _weapon?.HeldItemId ?? "";
        if (held == _lastHeldItemId)
            return _cachedSet;

        _lastHeldItemId = held;
        _cachedSet = (_weapon?.HeldWeapon)?.AnimationSet;
        return _cachedSet;
    }

    private void OnJumped() => _animator.TriggerJump();

    /// <summary>
    /// Atterraggio. La velocita' d'impatto viaggia nel payload della RPC come semplice grandezza
    /// fisica, non come esito di gioco: decide solo quanto si accusa il colpo (CLAUDE.md §3).
    /// </summary>
    private void OnLanded(float impactSpeed)
    {
        _animator.TriggerLand(impactSpeed);
        _camera?.AddLandingDip(impactSpeed, _controller.HardLandingSpeed);
        _viewmodel?.Land(impactSpeed);
    }

    /// <summary>
    /// Parkour: bordo e normale (misure geometriche) portano le mani sull'ostacolo; l'altezza
    /// sceglie fra scavalcamento e arrampicata con la stessa soglia del motore, e con lei la
    /// durata su cui stendere la posa.
    /// </summary>
    private void OnVaulted(Vector3 ledgePoint, Vector3 wallNormal, float height)
    {
        bool mantle = height > _controller.VaultMaxHeight;
        _animator.TriggerVault(ledgePoint, wallNormal, mantle,
            mantle ? _controller.MantleDuration : _controller.VaultDuration);
    }

    /// <summary>
    /// Lo sparo riusa il segnale gia' esistente di <see cref="WeaponController"/>, che l'host
    /// trasmette a tutti: non serve una RPC nuova per l'animazione.
    /// </summary>
    private void OnShotResolved(Vector3 origin, Vector3 end, bool hit, bool isLocalShooter)
    {
        _animator.TriggerFire();
        if (isLocalShooter)
            _viewmodel?.Fire();
    }

    /// Flinch del colpo incassato: la direzione e' quella di volo del proiettile, in mondo.
    private void OnHitReaction(Vector3 worldDirection) =>
        _animator.TriggerHitReaction(worldDirection);
}
