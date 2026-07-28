using Godot;

namespace Lagoon;

/// <summary>
/// Movimento a piedi condiviso da QUALUNQUE personaggio: giocatori e NPC.
///
/// Sta in <c>core/</c> e non in <c>player/</c> perche' non sa nulla di input, di camera ne' di chi
/// decide dove andare. Riceve una direzione voluta e una velocita', e produce movimento fisico piu'
/// lo stato che serve all'animazione. Chi lo pilota e' il derivato: <see cref="PlayerController"/>
/// legge l'input locale, un NPC leggera' il proprio agente di navigazione.
///
/// Rispetto a CLAUDE.md §3 questa classe non decide nulla sull'autorita': espone i metodi, e il
/// derivato li chiama solo dove <c>IsMultiplayerAuthority()</c> e' vero. Le proprieta' <c>Sync*</c>
/// sono lo stato che i derivati replicano col proprio <c>MultiplayerSynchronizer</c>.
/// </summary>
public partial class CharacterMotor : CharacterBody3D
{
    /// Frazione dell'altezza in piedi a cui si riduce la capsula da accovacciati.
    private const float CrouchHeightFactor = 0.6f;

    [Export] public float WalkSpeed { get; set; } = 4.0f;
    [Export] public float RunSpeed { get; set; } = 7.0f;
    [Export] public float CrouchSpeed { get; set; } = 2.0f;
    [Export] public float Gravity { get; set; } = 20.0f;

    /// Velocita' verticale impressa dal salto, in m/s.
    [Export] public float JumpVelocity { get; set; } = 6.0f;

    /// <summary>
    /// Velocita' con cui l'avatar ruota verso la direzione voluta. Serve perche' da armato
    /// l'orientamento insegue il cursore, che puo' saltare da una parte all'altra dello schermo in un
    /// frame: senza smorzamento l'avatar scatterebbe.
    /// </summary>
    [Export] public float TurnSpeed { get; set; } = 14.0f;

    /// <summary>
    /// Velocita' d'impatto oltre la quale l'atterraggio e' "duro". Non ha alcun effetto sul
    /// movimento: la leggono i bridge di animazione per scalare l'ammortizzazione del bacino.
    /// </summary>
    [Export] public float HardLandingSpeed { get; set; } = 9.0f;

    /// <summary>
    /// Scarto di imbardata fra mira e corpo oltre il quale, DA FERMI in mira, il corpo comincia a
    /// ruotare (turn-in-place). Sotto questa soglia ci pensa il busto (SpineAimModifier), che
    /// corregge fino a 70 gradi: 55 lascia margine perche' parte della correzione serve al pitch.
    /// </summary>
    [Export] public float TurnStartDegrees { get; set; } = 55.0f;

    /// Isteresi del turn-in-place: una volta partito, il corpo recupera fin sotto questa soglia.
    [Export] public float TurnStopDegrees { get; set; } = 8.0f;

    /// <summary>
    /// Durata della stance di mira "agganciata" dopo un colpo sparato senza mirare (hip-fire):
    /// l'arma resta alzata quel tanto che serve a far leggere il colpo, senza vietare nulla.
    /// </summary>
    [Export] public float HipFireAimSeconds { get; set; } = 0.6f;

    /// <summary>
    /// Reattivita' con cui si raggiunge la velocita' voluta, in frazione al secondo.
    ///
    /// Prima la velocita' orizzontale veniva ASSEGNATA di colpo: il personaggio passava da fermo a
    /// piena velocita' in un frame, e con lui la posizione nel BlendSpace2D. Nessuno smorzamento
    /// lato animatore nasconde del tutto quel salto, perche' il salto e' nel dato. Valori alti
    /// restano scattanti: 25 /s copre il 99% della differenza in circa 180 ms.
    /// </summary>
    [Export] public float GroundAcceleration { get; set; } = 25.0f;

    /// Reattivita' in FRENATA. Piu' alta dell'accelerazione: si smette di correre prima di partire.
    [Export] public float GroundDeceleration { get; set; } = 30.0f;

    /// Reattivita' in aria. Bassa apposta: a mezz'aria non si cambia direzione come su un binario.
    [Export] public float AirControl { get; set; } = 4.0f;

    /// <summary>
    /// Altezza massima di un gradino che si sale camminandoci contro, in metri.
    ///
    /// Jolt non lo fa da solo: contro uno scalino il corpo si limita a strisciare. Zero disattiva
    /// del tutto lo scavalcamento dei gradini.
    /// </summary>
    [Export] public float MaxStepHeight { get; set; } = 0.35f;

    // ====================================================================================
    //  Scavalcamento (vault): UNA clip generica + motion warping, niente clip per altezza
    // ====================================================================================

    /// Altezza minima di un ostacolo scavalcabile, in metri. Sotto ci pensa TryStepUp.
    [Export] public float VaultMinHeight { get; set; } = 0.5f;

    /// Altezza massima scavalcabile, in metri (intervallo dichiarato della clip: 0,5-1,2).
    [Export] public float VaultMaxHeight { get; set; } = 1.2f;

    /// Distanza massima dall'ostacolo perche' il vault si agganci, in metri.
    [Export] public float VaultReach { get; set; } = 1.0f;

    /// <summary>
    /// Durata del vault, in secondi. DEVE combaciare con la durata della clip <c>vault_low</c>
    /// (0,9 s): e' il tempo su cui il motion warping distribuisce la traiettoria della radice,
    /// e se diverge dalla clip le pose arrivano prima o dopo i punti di contatto.
    /// </summary>
    [Export] public float VaultDuration { get; set; } = 0.9f;

    /// Quanto OLTRE il bordo si atterra, in metri (misurato dal punto di aggancio).
    [Export] public float VaultLandingDepth { get; set; } = 0.9f;

    // ====================================================================================
    //  Stato replicato, comune a tutti i personaggi
    // ====================================================================================

    /// Posizione autoritativa. I derivati possono esprimerla in un riferimento proprio.
    [Export] public Vector3 SyncPosition { get; set; }

    /// Imbardata dell'avatar, sempre in coordinate MONDO.
    [Export] public float SyncFacing { get; set; }

    /// <summary>
    /// Velocita' orizzontale espressa nel riferimento dell'AVATAR: X = destra, Y = avanti, in m/s.
    ///
    /// E' locale e non in coordinate mondo perche' e' esattamente cio' che serve al
    /// <c>BlendSpace2D</c> della locomozione: se fosse in mondo, ogni peer dovrebbe riproiettarla
    /// usando <see cref="SyncFacing"/>, e con i due valori che arrivano in pacchetti diversi ci
    /// sarebbe un frame in cui direzione e orientamento non corrispondono.
    /// </summary>
    [Export] public Vector2 SyncLocalVelocity { get; set; }

    /// Stato replicato: accovacciato. Pilota il peso del layer crouch sugli altri peer.
    [Export] public bool SyncCrouching { get; set; }

    /// Stato replicato: a terra. Distingue locomozione da caduta sugli altri peer.
    [Export] public bool SyncGrounded { get; set; } = true;

    /// <summary>
    /// Inclinazione della mira, in radianti: positiva verso l'alto, zero all'orizzonte.
    ///
    /// E' l'UNICO dato replicato aggiunto per l'animazione, e c'e' perche' non e' derivabile da
    /// nient'altro: il punto di mira lo calcola solo il peer proprietario, quindi senza questo gli
    /// avatar remoti punterebbero l'arma sempre all'orizzonte. Lo consuma il rig di mira
    /// procedurale, e in prospettiva anche il combattimento.
    /// </summary>
    [Export] public float SyncAimPitch { get; set; }

    /// <summary>
    /// Imbardata della MIRA in coordinate mondo, in radianti. Seconda eccezione dichiarata alla
    /// regola "niente dati replicati per l'animazione", per lo stesso motivo di
    /// <see cref="SyncAimPitch"/>: il punto di mira lo conosce solo il peer proprietario, e da
    /// quando il corpo puo' guardare altrove (mira col busto, turn-in-place) non e' piu'
    /// derivabile da <see cref="SyncFacing"/>. I bridge ricostruiscono la direzione di mira da
    /// (SyncAimYaw, SyncAimPitch); fuori mira i derivati la tengono uguale a SyncFacing.
    /// </summary>
    [Export] public float SyncAimYaw { get; set; }

    /// <summary>
    /// Stance di mira attiva (RMB tenuto, o latch dopo un hip-fire). Terza eccezione dichiarata:
    /// decide la posa (arma alzata contro porto rilassato) sugli avatar remoti, e non e'
    /// derivabile da nient'altro perche' l'input di mira esiste solo sul peer proprietario.
    /// </summary>
    [Export] public bool SyncAiming { get; set; }

    /// <summary>
    /// Salto: evento estetico, non uno stato. Emesso su OGNI peer dalla RPC del derivato, cosi' il
    /// layer one-shot lo intercetta senza dover confrontare stati fra un frame e l'altro.
    /// </summary>
    [Signal]
    public delegate void JumpedEventHandler();

    /// Atterraggio, con la velocita' d'impatto in m/s: sceglie fra atterraggio morbido e duro.
    [Signal]
    public delegate void LandedEventHandler(float impactSpeed);

    /// <summary>
    /// Scavalcamento: evento estetico con il punto del bordo in coordinate mondo. Il punto e' una
    /// grandezza GEOMETRICA misurata dai raycast (come la velocita' d'impatto di Landed), non un
    /// esito di gioco: serve solo all'IK delle mani sul bordo (CLAUDE.md §3).
    /// </summary>
    [Signal]
    public delegate void VaultedEventHandler(Vector3 ledgePoint);

    /// Nodo che ruota con l'avatar. Il corpo NON ruota mai: cosi' la camera figlia resta stabile.
    protected Node3D Visual = null!;

    private CollisionShape3D _collision = null!;
    private CapsuleShape3D _capsule = null!;
    private ShapeCast3D? _headroom;

    private float _standHeight;
    private float _crouchBlend;
    private bool _wasGrounded = true;
    private float _fallSpeed;

    /// Stato dell'isteresi del turn-in-place (vedi <see cref="PlanAimFacing"/>).
    private bool _turningInPlace;

    // Stato del vault in corso: tempo trascorso (< 0 = inattivo) e i tre punti della traiettoria.
    private float _vaultTime = -1f;
    private Vector3 _vaultStart;
    private Vector3 _vaultLedge;
    private Vector3 _vaultEnd;

    /// Vault in corso: il movimento e' scriptato e l'input di locomozione viene ignorato.
    public bool Vaulting => _vaultTime >= 0f;

    public override void _Ready()
    {
        Visual = GetNode<Node3D>("Visual");
        _collision = GetNode<CollisionShape3D>("CollisionShape3D");
        _capsule = (CapsuleShape3D)_collision.Shape;
        _standHeight = _capsule.Height;

        // Opzionale: un NPC che non si accovaccia non ha bisogno della sonda del soffitto.
        _headroom = GetNodeOrNull<ShapeCast3D>("Headroom");

        SyncPosition = GlobalPosition;
    }

    /// <summary>
    /// Un tick di movimento.
    /// </summary>
    /// <param name="worldDirection">Direzione voluta sul piano orizzontale, in coordinate mondo,
    /// di modulo &lt;= 1. Vettore nullo = fermarsi.</param>
    /// <param name="speed">Velocita' voluta in m/s, gia' scelta da chi pilota.</param>
    /// <param name="wantJump">Richiesta di salto in questo tick.</param>
    /// <param name="wantCrouch">Richiesta di restare accovacciati.</param>
    protected void StepMotion(Vector3 worldDirection, float speed, bool wantJump, bool wantCrouch, double delta)
    {
        float dt = (float)delta;

        // Vault in corso: la radice segue la traiettoria warpata e l'input non conta. E' il
        // "motion warping": la clip e' in place e generica, la geometria vera la mette il codice.
        if (Vaulting)
        {
            StepVault(dt);
            return;
        }

        bool grounded = IsOnFloor();

        UpdateCrouch(dt, grounded, wantCrouch);

        Vector3 velocity = Velocity;
        Vector3 planar = new(velocity.X, 0f, velocity.Z);
        Vector3 wanted = worldDirection * speed;

        // Accelerazione esponenziale, come lo smorzamento del CharacterAnimator: indipendente dal
        // frame rate, a differenza della forma ingenua clamp(k * dt). Frenata e accelerazione hanno
        // costanti diverse perche' fermarsi deve essere piu' pronto che partire.
        float rate = !grounded
            ? AirControl
            : (wanted.LengthSquared() < planar.LengthSquared() ? GroundDeceleration : GroundAcceleration);
        planar = planar.Lerp(wanted, Damp(rate, dt));

        // Su una pendenza la velocita' voluta va PROIETTATA sul piano del pavimento. Senza, salendo
        // si spinge contro la salita (si rallenta) e scendendo ci si stacca dal terreno a ogni
        // dislivello, con l'animazione che sfarfalla fra locomozione e caduta.
        if (grounded)
        {
            Vector3 normal = GetFloorNormal();
            if (normal != Vector3.Zero && normal.Y < 0.999f)
                planar = planar.Slide(normal);
        }

        velocity.X = planar.X;
        velocity.Z = planar.Z;

        if (grounded)
        {
            // Il salto va impresso PRIMA di MoveAndSlide e dopo l'azzeramento, altrimenti
            // l'azzeramento di questo stesso frame lo cancellerebbe: a terra IsOnFloor() e' ancora
            // vero quando si salta.
            velocity.Y = planar.Y;
            if (wantJump && !SyncCrouching)
            {
                // Saltare CONTRO un ostacolo scavalcabile diventa un vault: stesso tasto, la
                // geometria decide. Se non c'e' niente da scavalcare, e' un salto normale.
                if (TryStartVault(worldDirection))
                    return;

                velocity.Y = JumpVelocity;
                OnJumpTriggered();
            }
        }
        else
        {
            velocity.Y -= Gravity * dt;
        }

        // Velocita' di caduta al momento del contatto: campionata prima di MoveAndSlide, perche'
        // dopo la collisione l'ha gia' azzerata.
        if (!grounded)
            _fallSpeed = Mathf.Max(_fallSpeed, -velocity.Y);

        Velocity = velocity;
        MoveAndSlide();
        TryStepUp(dt);
        DetectLanding();
    }

    /// <summary>
    /// Prova ad agganciare uno scavalcamento nella direzione voluta (o del facing, da fermi).
    ///
    /// Tre misure, tutte raycast sul mondo statico: (1) c'e' una parete davanti entro
    /// <see cref="VaultReach"/>; (2) la sua sommita' sta nell'intervallo scavalcabile; (3) oltre il
    /// bordo esiste un punto d'atterraggio. Se una qualsiasi manca, niente vault — e chi ha chiesto
    /// il salto salta. NON genera clip per altezza: la stessa clip viene warpata sui punti misurati.
    /// </summary>
    private bool TryStartVault(Vector3 worldDirection)
    {
        Vector3 forward = worldDirection.LengthSquared() > 0.01f
            ? worldDirection.Normalized()
            : new Vector3(Mathf.Sin(SyncFacing), 0f, Mathf.Cos(SyncFacing));

        float feetY = GlobalPosition.Y - _standHeight * 0.5f;
        var space = GetWorld3D().DirectSpaceState;

        // (1) La parete, sondata a meta' della fascia scavalcabile.
        Vector3 chest = GlobalPosition + Vector3.Up * (VaultMinHeight + VaultMaxHeight) * 0.5f
            - Vector3.Up * _standHeight * 0.5f;
        var wallQuery = PhysicsRayQueryParameters3D.Create(
            chest, chest + forward * VaultReach, CollisionLayers.World | CollisionLayers.VehicleDeck);
        wallQuery.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
        Godot.Collections.Dictionary wall = space.IntersectRay(wallQuery);
        if (wall.Count == 0)
            return false;

        var wallPoint = (Vector3)wall["position"];

        // (2) La sommita': raggio in giu' da sopra il bordo, appena OLTRE la parete.
        Vector3 overLedge = wallPoint + forward * 0.15f;
        overLedge.Y = feetY + VaultMaxHeight + 0.3f;
        var topQuery = PhysicsRayQueryParameters3D.Create(
            overLedge, overLedge + Vector3.Down * (VaultMaxHeight + 0.4f),
            CollisionLayers.World | CollisionLayers.VehicleDeck);
        Godot.Collections.Dictionary top = space.IntersectRay(topQuery);
        if (top.Count == 0)
            return false;

        var ledgeTop = (Vector3)top["position"];
        float height = ledgeTop.Y - feetY;
        if (height < VaultMinHeight || height > VaultMaxHeight)
            return false;

        // (3) L'atterraggio, oltre l'ostacolo. Senza suolo di la', non si scavalca alla cieca.
        Vector3 beyond = wallPoint + forward * VaultLandingDepth;
        beyond.Y = ledgeTop.Y + 0.3f;
        var landQuery = PhysicsRayQueryParameters3D.Create(
            beyond, beyond + Vector3.Down * (height + 1.2f),
            CollisionLayers.World | CollisionLayers.VehicleDeck);
        Godot.Collections.Dictionary landing = space.IntersectRay(landQuery);
        if (landing.Count == 0)
            return false;

        var landPoint = (Vector3)landing["position"];

        _vaultStart = GlobalPosition;
        _vaultLedge = new Vector3(wallPoint.X, ledgeTop.Y, wallPoint.Z);
        _vaultEnd = landPoint + Vector3.Up * _standHeight * 0.5f;
        _vaultTime = 0f;
        Velocity = Vector3.Zero;
        OnVaultTriggered(_vaultLedge);
        return true;
    }

    /// <summary>
    /// Un tick di vault: la radice segue il warp start -> bordo -> atterraggio.
    ///
    /// L'orizzontale avanza con uno smoothstep unico; la verticale sale sul bordo entro meta'
    /// clip (la fase di appoggio/raccolta di <c>vault_low</c>) e ridiscende nella seconda meta'.
    /// La posizione si SCRIVE (movimento kinematico scriptato): MoveAndSlide combatterebbe
    /// contro l'ostacolo che si sta appunto scavalcando.
    /// </summary>
    private void StepVault(float dt)
    {
        _vaultTime += dt;
        float t = Mathf.Clamp(_vaultTime / Mathf.Max(VaultDuration, 0.01f), 0f, 1f);
        float horizontal = Mathf.SmoothStep(0f, 1f, t);

        Vector3 flat = _vaultStart.Lerp(_vaultEnd, horizontal);
        float apexY = _vaultLedge.Y + _standHeight * 0.5f + 0.08f;
        float y = t < 0.5f
            ? Mathf.Lerp(_vaultStart.Y, apexY, Mathf.SmoothStep(0f, 1f, t / 0.5f))
            : Mathf.Lerp(apexY, _vaultEnd.Y, Mathf.SmoothStep(0f, 1f, (t - 0.5f) / 0.5f));

        GlobalPosition = new Vector3(flat.X, y, flat.Z);

        // Per l'animazione si e' "a terra" per tutta la durata: la posa la mette la clip di
        // vault, non il layer di caduta. Le gambe sotto il one-shot sono comunque coperte.
        SyncGrounded = true;
        SyncLocalVelocity = Vector2.Zero;
        Velocity = Vector3.Zero;

        if (t >= 1f)
        {
            _vaultTime = -1f;
            _wasGrounded = true;
            _fallSpeed = 0f;
        }
    }

    /// <summary>
    /// Sale un gradino basso contro cui si e' appena strisciato.
    ///
    /// Si fa DOPO <c>MoveAndSlide</c> e con <c>TestMove</c>, cioe' senza muovere davvero nulla
    /// finche' non e' certo che il gradino sia salibile: si prova a salire, ad avanzare da lassu' e a
    /// riappoggiarsi. Se uno dei tre passi non riesce, non era un gradino ma un muro, e si resta dove
    /// si e'. Serve una sonda propria perche' <c>CharacterBody3D</c> non ha un'altezza di gradino e
    /// Jolt non la simula.
    /// </summary>
    private void TryStepUp(float dt)
    {
        if (MaxStepHeight <= 0.001f || !IsOnWall() || !IsOnFloor())
            return;

        Vector3 motion = new Vector3(Velocity.X, 0f, Velocity.Z) * dt;
        if (motion.LengthSquared() < 0.000001f)
            return;

        Vector3 lift = Vector3.Up * MaxStepHeight;
        Transform3D from = GlobalTransform;

        // Sopra la testa dev'esserci spazio, e da lassu' il passo in avanti dev'essere libero.
        if (TestMove(from, lift))
            return;

        Transform3D raised = from.Translated(lift);
        if (TestMove(raised, motion))
            return;

        // E si deve poter riappoggiare: se sotto non c'e' niente, quello non era un gradino ma un
        // bordo, e salirci sarebbe un salto involontario.
        Transform3D advanced = raised.Translated(motion);
        var landing = new KinematicCollision3D();
        if (!TestMove(advanced, -lift, landing))
            return;

        GlobalPosition = advanced.Origin + landing.GetTravel();
    }

    /// <summary>
    /// Aggiorna l'accovacciamento e la capsula di collisione.
    ///
    /// Rialzarsi non e' garantito: se sopra la testa non c'e' spazio (<c>Headroom</c>) si resta giu'
    /// anche lasciando il tasto, altrimenti si finirebbe incastrati dentro il soffitto. In aria non
    /// si cambia postura, per non alterare la capsula a mezz'aria.
    /// </summary>
    private void UpdateCrouch(float dt, bool grounded, bool wantCrouch)
    {
        bool wants = grounded && wantCrouch;

        if (!wants && SyncCrouching && _headroom != null)
        {
            _headroom.ForceShapecastUpdate();
            if (_headroom.IsColliding())
                wants = true;
        }

        SyncCrouching = wants;

        // La capsula si accorcia dall'ALTO: il fondo resta dov'e', cosi' l'avatar non sprofonda ne'
        // viene espulso dal pavimento quando cambia postura.
        _crouchBlend = Mathf.MoveToward(_crouchBlend, SyncCrouching ? 1f : 0f, dt * 6f);
        float height = Mathf.Lerp(_standHeight, _standHeight * CrouchHeightFactor, _crouchBlend);

        _capsule.Height = height;
        _collision.Position = new Vector3(0f, (height - _standHeight) * 0.5f, 0f);
    }

    /// Rileva il passaggio aria -> terra e notifica l'atterraggio una sola volta.
    private void DetectLanding()
    {
        bool grounded = IsOnFloor();

        if (grounded && !_wasGrounded)
            OnLandTriggered(_fallSpeed);

        if (grounded)
            _fallSpeed = 0f;

        _wasGrounded = grounded;
        SyncGrounded = grounded;
    }

    /// <summary>
    /// Ruota l'avatar verso <paramref name="targetYaw"/> con smorzamento. Ruota SOLO
    /// <see cref="Visual"/>: il corpo resta fermo, cosi' la camera figlia non gira con lui.
    /// </summary>
    protected void UpdateFacing(float targetYaw, double delta)
    {
        SyncFacing = Mathf.LerpAngle(SyncFacing, targetYaw, Mathf.Clamp((float)delta * TurnSpeed, 0f, 1f));
        Visual.Rotation = new Vector3(0f, SyncFacing, 0f);
    }

    /// <summary>
    /// Decide l'imbardata bersaglio del CORPO mentre si sta mirando: la mira la insegue il busto
    /// (SpineAimModifier), il corpo solo quando serve.
    ///
    /// In movimento il corpo insegue sempre la mira (e' cio' che attiva lo strafe armato). Da fermi
    /// c'e' una zona morta con isteresi: il corpo non ruota finche' lo scarto resta sotto
    /// <see cref="TurnStartDegrees"/>, poi recupera (turn-in-place) fin sotto
    /// <see cref="TurnStopDegrees"/>. Le due soglie sono distinte apposta: con una sola, ai bordi
    /// della zona morta il corpo partirebbe e si fermerebbe a ogni oscillazione del mouse.
    ///
    /// Pubblico e senza stato nascosto oltre a <c>_turningInPlace</c>: cosi' e' testabile dalla
    /// suite runtime e riusabile pari pari da un futuro NPC armato.
    /// </summary>
    public float PlanAimFacing(float currentYaw, float aimYaw, bool moving)
    {
        if (moving)
        {
            _turningInPlace = false;
            return aimYaw;
        }

        float delta = Mathf.Abs(Mathf.AngleDifference(currentYaw, aimYaw));

        if (_turningInPlace)
        {
            if (delta < Mathf.DegToRad(TurnStopDegrees))
                _turningInPlace = false;
        }
        else if (delta > Mathf.DegToRad(TurnStartDegrees))
        {
            _turningInPlace = true;
        }

        return _turningInPlace ? aimYaw : currentYaw;
    }

    /// <summary>
    /// Converte una velocita' planare in coordinate mondo nel riferimento dell'avatar:
    /// X = DESTRA, Y = avanti (il contratto di <see cref="SyncLocalVelocity"/>).
    ///
    /// La componente X del frame ruotato va NEGATA: il Visual guarda +Z e la sua sinistra e' +X,
    /// quindi <c>local.X &gt; 0</c> significa "verso la propria sinistra". Senza la negazione lo
    /// strafe risulta specchiato in tutti i BlendSpace2D e il lean si inclina dal lato sbagliato
    /// (bug storico, coperto da verifica in tools/verify_animation_runtime.gd).
    /// </summary>
    public Vector2 WorldToLocalVelocity(Vector3 planar, float yaw)
    {
        Vector3 local = planar.Rotated(Vector3.Up, -yaw);
        return new Vector2(-local.X, local.Z);
    }

    /// Proietta la velocita' nel riferimento dell'avatar (vedi <see cref="SyncLocalVelocity"/>).
    protected void PublishLocomotionState()
    {
        Vector3 planar = new(Velocity.X, 0f, Velocity.Z);
        SyncLocalVelocity = WorldToLocalVelocity(planar, SyncFacing);
    }

    /// <summary>
    /// Applica l'imbardata replicata a un avatar NON autoritativo: solo resa, nessun calcolo.
    /// Il ritmo lo decide chi chiama, perche' e' lo stesso con cui interpola la posizione: usarne
    /// due diversi farebbe scivolare orientamento e posizione l'uno rispetto all'altra.
    /// </summary>
    protected void ApplyRemoteFacing(float rate, double delta)
    {
        float t = Mathf.Clamp((float)delta * rate, 0f, 1f);
        Visual.Rotation = new Vector3(0f, Mathf.LerpAngle(Visual.Rotation.Y, SyncFacing, t), 0f);
    }

    /// <summary>
    /// Notifica il salto. Il derivato la sovrascrive per trasmetterlo agli altri peer; la base si
    /// limita a emettere il segnale locale, che basta a un personaggio non replicato.
    /// </summary>
    protected virtual void OnJumpTriggered() => EmitSignal(SignalName.Jumped);

    /// Come sopra per l'atterraggio. La velocita' d'impatto e' una grandezza fisica, non un esito di
    /// gioco: decide soltanto quanto flette il bacino (CLAUDE.md §3).
    protected virtual void OnLandTriggered(float impactSpeed) => EmitSignal(SignalName.Landed, impactSpeed);

    /// Come sopra per lo scavalcamento: il bordo e' una misura geometrica per l'IK delle mani.
    protected virtual void OnVaultTriggered(Vector3 ledgePoint) =>
        EmitSignal(SignalName.Vaulted, ledgePoint);

    /// Smorzamento esponenziale, indipendente dal frame rate.
    protected static float Damp(float speed, float dt) => 1.0f - Mathf.Exp(-speed * dt);
}
