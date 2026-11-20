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
    /// Velocita' con cui il corpo ruota verso la direzione voluta. Per il giocatore la direzione e'
    /// lo sguardo: lo smorzamento assorbe gli scatti del mouse, e lo scarto residuo lo chiude il
    /// busto (BodyAimModifier).
    /// </summary>
    [Export] public float TurnSpeed { get; set; } = 14.0f;

    /// <summary>
    /// Quota degli occhi sopra i piedi, in piedi e accovacciati. E' il punto di vista NOMINALE,
    /// ricostruibile dal solo stato replicato (<see cref="ResolvedEyePosition"/>): ci stanno la
    /// camera del giocatore, l'origine dei colpi lato host e l'arma del corpo in terza persona.
    /// </summary>
    [Export] public float EyeHeight { get; set; } = 1.62f;

    [Export] public float CrouchEyeHeight { get; set; } = 1.04f;

    /// <summary>
    /// Velocita' d'impatto oltre la quale l'atterraggio e' "duro". Non ha alcun effetto sul
    /// movimento: la leggono i bridge di animazione per scalare l'ammortizzazione del bacino.
    /// </summary>
    [Export] public float HardLandingSpeed { get; set; } = 9.0f;

    /// <summary>
    /// Per quanto tempo il movimento resta bloccato dopo un atterraggio DURO, in secondi.
    ///
    /// E' il tempo in cui il personaggio si rialza: sopra <see cref="HardLandingSpeed"/> parte la
    /// clip <c>Jump_Land</c> (1,27 s), e finche' il grosso del gesto e' in corso l'input di
    /// locomozione non produce movimento — altrimenti si vedrebbe il corpo scivolare via mentre
    /// e' ancora piegato a terra. Zero disattiva del tutto il blocco.
    /// </summary>
    [Export] public float HardLandingLockSeconds { get; set; } = 0.9f;

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
    /// Quanto in basso <c>MoveAndSlide</c> cerca il pavimento per riagganciarsi, in metri.
    ///
    /// Il valore di default di Godot (0,1 m) e' pensato per terreno piatto: correndo a 7 m/s su una
    /// rampa di 20 gradi si scende 4 cm per tick, e basta un dosso perche' il corpo decolli e
    /// l'animazione passi in caduta. Va tenuto sopra al dislivello di un tick alla velocita' massima
    /// e sotto <see cref="MaxStepHeight"/>. Non impedisce di saltare: lo snap non si applica quando
    /// la velocita' punta in alto.
    /// </summary>
    [Export] public float FloorSnap { get; set; } = 0.3f;

    /// <summary>
    /// Per quanto tempo si continua a considerarsi "a terra" dopo aver perso il contatto, in secondi.
    ///
    /// Riguarda SOLO lo stato pubblicato (<see cref="SyncGrounded"/>) e il rilevamento
    /// dell'atterraggio, mai la gravita': un distacco di pochi millisecondi su uno spigolo, un
    /// gradino o il raccordo di una rampa non e' una caduta, e mostrarlo come tale fa lampeggiare la
    /// posa di volo mentre si cammina. Vale anche da coyote time percettivo.
    /// </summary>
    [Export] public float GroundedGraceSeconds { get; set; } = 0.12f;

    /// <summary>
    /// Altezza massima di un gradino che si sale camminandoci contro, in metri.
    ///
    /// Jolt non lo fa da solo: contro uno scalino il corpo si limita a strisciare. Zero disattiva
    /// del tutto lo scavalcamento dei gradini.
    /// </summary>
    [Export] public float MaxStepHeight { get; set; } = 0.35f;

    // ====================================================================================
    //  Parkour: geometria MISURATA + motion warping, niente clip per altezza
    // ====================================================================================
    //
    //  Due manovre sulla stessa sonda (ObstacleProbe): sotto si scavalca passando OLTRE
    //  l'ostacolo, sopra ci si arrampica per restare IN CIMA. La banda la decide l'altezza
    //  misurata, non un tag messo a mano sul livello.

    /// Altezza minima di un ostacolo scavalcabile, in metri. Sotto ci pensa TryStepUp.
    [Export] public float VaultMinHeight { get; set; } = 0.5f;

    /// Altezza massima scavalcabile, in metri (intervallo dichiarato della clip: 0,5-1,2).
    [Export] public float VaultMaxHeight { get; set; } = 1.2f;

    /// Altezza massima a cui ci si arrampica restando in cima, in metri.
    [Export] public float MantleMaxHeight { get; set; } = 3.0f;

    /// Distanza massima dall'ostacolo perche' la manovra si agganci, in metri.
    [Export] public float VaultReach { get; set; } = 1.0f;

    /// <summary>
    /// Durata del vault, in secondi. DEVE combaciare con la durata della clip <c>vault_low</c>
    /// (0,9 s): e' il tempo su cui il motion warping distribuisce la traiettoria della radice,
    /// e se diverge dalla clip le pose arrivano prima o dopo i punti di contatto.
    /// </summary>
    [Export] public float VaultDuration { get; set; } = 0.9f;

    /// Come sopra per l'arrampicata, sulla clip <c>mantle_high</c>. Piu' lunga: si sale piu' in alto.
    [Export] public float MantleDuration { get; set; } = 1.4f;

    /// <summary>
    /// Aria fra il corpo e la parete appena scavalcata al momento dell'atterraggio, in metri.
    ///
    /// Non e' una distanza fissa dalla parete: lo spessore vero lo misura la sonda, e la distanza
    /// fissa sbagliava in entrambi i versi — dietro un muretto sottile faceva atterrare troppo
    /// lontano, su un parapetto largo faceva atterrare ancora sopra l'ostacolo. Ed e' misurata
    /// dalla SUPERFICIE del corpo, non dal suo centro: sommandoci il raggio della capsula, il punto
    /// d'atterraggio e' un punto in cui il personaggio ci sta davvero, che e' l'unico che abbia
    /// senso verificare.
    /// </summary>
    [Export] public float VaultLandingMargin { get; set; } = 0.15f;

    /// Spessore massimo di un ostacolo scavalcabile, in metri. Oltre e' una piattaforma, non un muro.
    [Export] public float VaultMaxDepth { get; set; } = 1.5f;

    /// <summary>
    /// Velocita' orizzontale minima per agganciare una manovra, in m/s. A zero si scavalca anche
    /// da fermi: e' il default perche' avvicinarsi a un muretto e premere salto e' il gesto
    /// naturale, e pretendere la rincorsa si legge come un mancato input.
    /// </summary>
    [Export] public float MinParkourSpeed { get; set; }

    // ====================================================================================
    //  Nuoto: la quota dell'acqua la da' chi pilota, il motore non conosce l'acqua
    // ====================================================================================
    //
    //  Il corpo resta una capsula in piedi: cambia solo chi decide la verticale. A piedi e'
    //  la gravita', in acqua una molla verso la quota di galleggiamento (occhi appena sopra
    //  l'onda). Collisioni, sguardo e occhi nominali non cambiano: ResolvedEyePosition vale
    //  anche a nuoto, e l'host non ha bisogno di saperlo.

    /// Gruppo dei nodi "scaletta": vicino a uno di essi si risale dall'acqua su bordi piu' alti.
    public const string SwimLadderGroup = "swim_ladder";

    /// Velocita' a nuoto e a nuoto veloce (tasto di corsa), in m/s.
    [Export] public float SwimSpeed { get; set; } = 1.6f;

    [Export] public float SwimSprintSpeed { get; set; } = 2.6f;

    /// Reattivita' orizzontale in acqua, in frazione al secondo: bassa, l'acqua ha inerzia.
    [Export] public float SwimAcceleration { get; set; } = 3.0f;

    /// <summary>
    /// Quota degli occhi sopra la superficie (onde comprese) quando si galleggia, in metri. Il corpo
    /// in terza persona si alza di <c>EyeHeight - SwimEyeAboveSurface</c>: le clip di nuoto della
    /// libreria hanno la superficie all'origine.
    /// </summary>
    [Export] public float SwimEyeAboveSurface { get; set; } = 0.2f;

    /// Rigidezza della molla che riporta alla quota di galleggiamento, in 1/s.
    [Export] public float SwimFloatStiffness { get; set; } = 3.0f;

    /// Prontezza con cui la velocita' verticale raggiunge quella voluta, in 1/s: smorza l'onda.
    [Export] public float SwimVerticalResponse { get; set; } = 4.0f;

    /// Velocita' di salita (salto tenuto) e d'immersione (accovacciarsi tenuto), in m/s.
    [Export] public float SwimVerticalSpeed { get; set; } = 1.6f;

    /// Velocita' con cui si risale da soli verso la superficie, in m/s (spinta di galleggiamento).
    [Export] public float SwimBuoyancySpeed { get; set; } = 0.9f;

    /// Di quanto si alza la quota di galleggiamento col salto tenuto in superficie, in metri.
    [Export] public float SwimSurfaceLift { get; set; } = 0.25f;

    /// Frazione della velocita' orizzontale delle particelle d'acqua che trascina il nuotatore.
    [Export] public float SwimCurrentFollow { get; set; } = 0.5f;

    /// Altezza massima sopra l'acqua di un bordo su cui ci si issa nuotando, senza scaletta, in metri.
    [Export] public float SwimClimbMaxAboveWater { get; set; } = 0.6f;

    /// Come sopra, entro <see cref="LadderReach"/> da un nodo del gruppo <see cref="SwimLadderGroup"/>.
    [Export] public float LadderClimbMaxAboveWater { get; set; } = 1.6f;

    /// Distanza orizzontale massima fra il punto d'appoggio sulla parete e la scaletta, in metri.
    [Export] public float LadderReach { get; set; } = 1.0f;

    // ====================================================================================
    //  Stato replicato, comune a tutti i personaggi
    // ====================================================================================

    /// Posizione autoritativa. I derivati possono esprimerla in un riferimento proprio.
    [Export] public Vector3 SyncPosition { get; set; }

    /// <summary>
    /// Posizione autoritativa in coordinate MONDO. Esiste perche' <see cref="SyncPosition"/> puo'
    /// essere espressa in un riferimento proprio del derivato (per il giocatore: locale allo scafo
    /// di un'imbarcazione), e chi legge lo stato replicato dall'esterno non ha modo di saperlo.
    ///
    /// Qui la ridefinizione e' l'identita': un NPC non sale in barca, quindi la sua SyncPosition e'
    /// gia' in coordinate mondo. La sovrascrive <c>PlayerController</c>.
    ///
    /// </summary>
    public virtual Vector3 ResolvedSyncPosition => SyncPosition;

    /// <summary>
    /// Quota dei PIEDI secondo lo stato replicato, in coordinate mondo.
    ///
    /// L'origine del <c>CharacterBody3D</c> sta a meta' capsula, non a terra, e accovacciandosi non
    /// si sposta (e' la forma che si accorcia verso l'alto, vedi <c>ApplyCrouchShape</c>): sottrarre
    /// meta' <see cref="StandHeight"/> da' sempre il suolo su cui si poggia.
    /// </summary>
    public float ResolvedFeetY => ResolvedSyncPosition.Y - StandHeight * 0.5f;

    /// <summary>
    /// Punto di vista secondo lo stato REPLICATO, in coordinate mondo. E' l'origine dei colpi lato
    /// host: mai un dato del client, mai <c>GlobalPosition</c> (interpolata sugli avatar remoti).
    /// </summary>
    public Vector3 ResolvedEyePosition
    {
        get
        {
            Vector3 position = ResolvedSyncPosition;
            position.Y = ResolvedFeetY + (SyncCrouching ? CrouchEyeHeight : EyeHeight);
            return position;
        }
    }

    /// Altezza della capsula in piedi, letta una volta dalla scena. Zero prima di <c>_Ready</c>.
    public float StandHeight => _standHeight;

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
    /// Stato replicato: manovra di parkour in corso (scavalcamento o arrampicata).
    ///
    /// Non e' un dato per l'animazione — quella lo sa gia' dal proprio one-shot — ma uno stato di
    /// GIOCO: durante la manovra le mani sono sull'ostacolo, quindi non si spara e non si ricarica,
    /// e l'host deve poterlo verificare senza credere al client (CLAUDE.md §3). Il movimento e'
    /// client-autoritativo, quindi l'host non esegue <see cref="StepMotion"/> per un avatar remoto e
    /// <see cref="Vaulting"/> li' sarebbe sempre falso: senza questa proprieta' replicata la
    /// validazione host-side non avrebbe nulla su cui lavorare.
    /// </summary>
    [Export] public bool SyncVaulting { get; set; }

    /// <summary>
    /// Stato replicato: a nuoto. Come <see cref="SyncVaulting"/> e' uno stato di GIOCO oltre che di
    /// resa: le mani nuotano, quindi l'host rifiuta tiro e ricarica (<c>WeaponController.HandsFree</c>),
    /// e gli altri peer scelgono il ramo di nuoto del corpo. Lo scrive chi calcola il movimento
    /// (<see cref="BeginSwim"/>/<see cref="EndSwim"/>), che sul peer proprietario e' anche lo stato vero.
    /// </summary>
    [Export] public bool SyncSwimming { get; set; }

    /// <summary>
    /// Inclinazione dello SGUARDO, in radianti: positiva verso l'alto, zero all'orizzonte.
    ///
    /// Con <see cref="SyncAimYaw"/> e <see cref="SyncAiming"/> e' l'eccezione dichiarata alla
    /// regola "niente dati replicati per l'animazione": dove guarda un giocatore lo sa solo il
    /// suo peer, quindi senza questi gli avatar remoti punterebbero l'arma sempre all'orizzonte.
    /// I bridge ricostruiscono la direzione da (SyncAimYaw, SyncAimPitch).
    /// </summary>
    [Export] public float SyncAimPitch { get; set; }

    /// <summary>
    /// Imbardata dello SGUARDO in coordinate mondo, in radianti. Distinta da
    /// <see cref="SyncFacing"/>, che la insegue smorzata: lo scarto lo chiude il busto. Chi non ha
    /// uno sguardo proprio (NPC disarmato) la tiene uguale a SyncFacing.
    /// </summary>
    [Export] public float SyncAimYaw { get; set; }

    /// <summary>
    /// Arma all'occhio (mira col tasto destro). Decide la posa sugli avatar remoti e, lato host,
    /// quale cono di dispersione vale per i colpi (<c>WeaponDefinition.SpreadDegrees</c>).
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
    /// Scavalcamento o arrampicata: evento estetico con la geometria misurata dell'ostacolo.
    ///
    /// Sono tutte grandezze GEOMETRICHE (come la velocita' d'impatto di Landed), non esiti di
    /// gioco (CLAUDE.md §3): il bordo e la normale servono all'IK delle mani, l'altezza a scegliere
    /// la posa. La scelta della clip la fa il RICEVITORE con soglie proprie, esattamente come
    /// l'atterraggio sceglie fra morbido e duro a partire dalla sola velocita'.
    /// </summary>
    /// <param name="ledgePoint">Punto d'appiglio sul bordo, in coordinate mondo.</param>
    /// <param name="wallNormal">Normale orizzontale della parete, rivolta verso il personaggio.</param>
    /// <param name="height">Altezza del bordo rispetto ai piedi, in metri.</param>
    [Signal]
    public delegate void VaultedEventHandler(Vector3 ledgePoint, Vector3 wallNormal, float height);

    /// Nodo che ruota con l'avatar. Il corpo NON ruota mai: cosi' la camera figlia resta stabile.
    protected Node3D Visual = null!;

    private CollisionShape3D _collision = null!;
    private CapsuleShape3D _capsule = null!;
    private ShapeCast3D? _headroom;

    private float _standHeight;
    private float _crouchBlend;
    private bool _wasGrounded = true;
    private float _fallSpeed;

    /// Tempo trascorso dall'ultimo contatto reale col pavimento (vedi GroundedGraceSeconds).
    private float _airTime;

    /// Tempo residuo del blocco d'atterraggio duro (vedi <see cref="HardLandingLockSeconds"/>).
    private float _landLock;

    /// Manovra di parkour in corso. Le due fasi hanno traiettorie diverse, non solo durate diverse.
    private enum ParkourPhase
    {
        None,

        /// Si passa OLTRE l'ostacolo: apice sopra il bordo, poi discesa sul suolo di la'.
        Vault,

        /// Si resta IN CIMA: salita quasi verticale sull'appiglio, poi rimessa in piedi sul bordo.
        Mantle,
    }

    // Stato della manovra in corso: fase, tempo trascorso e i punti misurati della traiettoria.
    private ParkourPhase _phase = ParkourPhase.None;
    private float _phaseTime;
    private float _phaseDuration = 1f;
    private Vector3 _vaultStart;
    private Vector3 _vaultLedge;
    private Vector3 _vaultEnd;

    /// Manovra in corso: il movimento e' scriptato e l'input di locomozione viene ignorato.
    /// E' lo stato LOCALE del peer che calcola il movimento; per gli altri c'e' <see cref="SyncVaulting"/>.
    public bool Vaulting => _phase != ParkourPhase.None;

    /// <summary>
    /// Il corpo su cui sta il bordo dell'ultima manovra (o null). Serve al derivato che sa di veicoli:
    /// se e' il ponte di una barca, la traiettoria va trasportata con lei (<see cref="ShiftManeuver"/>).
    /// </summary>
    protected GodotObject? ManeuverLedgeCollider { get; private set; }

    /// <summary>
    /// Rialzata in corso da un atterraggio duro: il movimento e' bloccato per tutta la durata della
    /// clip. Come <see cref="Vaulting"/> e' lo stato locale di chi calcola il movimento.
    /// </summary>
    public bool LandingLocked => _landLock > 0f;

    public override void _Ready()
    {
        Visual = GetNode<Node3D>("Visual");
        _collision = GetNode<CollisionShape3D>("CollisionShape3D");
        _capsule = (CapsuleShape3D)_collision.Shape;
        _standHeight = _capsule.Height;

        // Opzionale: un NPC che non si accovaccia non ha bisogno della sonda del soffitto.
        _headroom = GetNodeOrNull<ShapeCast3D>("Headroom");

        // Gestione delle pendenze, delegata al motore invece che ricalcolata a mano (vedi
        // StepMotion). Si imposta qui e non nelle scene perche' vale per OGNI personaggio: un NPC
        // che decolla su una rampa e' lo stesso difetto del giocatore che ci decolla.
        FloorSnapLength = FloorSnap;
        FloorConstantSpeed = true;
        FloorStopOnSlope = true;

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

        // Atterraggio duro: finche' la clip di rialzata e' in corso l'input non produce movimento.
        // Si azzera la DIREZIONE VOLUTA invece della velocita': cosi' la frenata resta quella
        // esponenziale di sempre (il corpo scarica l'inerzia della caduta invece di inchiodarsi) e
        // la locomozione pubblicata rientra con continuita' verso lo zero. Gravita', collisioni e
        // gradini continuano a funzionare: si e' bloccati, non sospesi.
        if (_landLock > 0f)
        {
            _landLock -= dt;
            worldDirection = Vector3.Zero;
            wantJump = false;
        }

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

        // Nessuna proiezione manuale sul piano della pendenza: ci pensa MoveAndSlide, e con
        // FloorConstantSpeed = true la salita non costa velocita'. La proiezione fatta a mano
        // (planar.Slide(GetFloorNormal())) rallentava comunque di cos^2 e, soprattutto, produceva
        // la componente verticale positiva che disattivava lo snap al pavimento.
        velocity.X = planar.X;
        velocity.Z = planar.Z;

        if (grounded)
        {
            // A terra la verticale NON la decide questa classe: la decide MoveAndSlide, che sale
            // la pendenza facendo scivolare il movimento orizzontale sul piano del pavimento e ci
            // riappoggia con lo snap. Imprimere qui la componente verticale della salita
            // (velocity.Y = planar.Y) era il bug della rampa: con Velocity.Y POSITIVO Godot salta
            // lo snap al pavimento (lo fa solo quando la velocita' non punta in alto), quindi
            // camminando in salita il corpo si staccava di qualche millimetro a ogni frame,
            // IsOnFloor() lampeggiava e con lui la posa di caduta.
            //
            // Il tratto in discesa lo tiene invece FloorSnapLength (impostato in _Ready): senza,
            // si decolla a ogni dislivello. Il salto resta possibile perche' lo snap non si
            // applica quando la velocita' punta in alto — che e' esattamente il caso qui sotto.
            velocity.Y = 0f;
            if (wantJump && !SyncCrouching)
            {
                // Saltare CONTRO un ostacolo diventa uno scavalcamento o un'arrampicata: stesso
                // tasto, la geometria misurata decide quale. Se non c'e' niente da superare, e' un
                // salto normale.
                if (TryStartParkour(worldDirection))
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
        DetectLanding(dt);
    }

    /// <summary>
    /// Prova ad agganciare una manovra di parkour nella direzione voluta (o del facing, da fermi).
    ///
    /// E' pubblica perche' il tasto di salto non e' l'unico modo di chiederla: un NPC che trova la
    /// strada sbarrata da un muretto la chiedera' dal proprio comportamento, senza passare da un
    /// input che non ha. Chi la chiama esprime un INTENTO — la geometria decide se e' possibile —
    /// e va chiamata solo dove si e' autoritativi, come tutto il resto del movimento.
    ///
    /// La geometria non la misura piu' questa classe: la misura <see cref="ObstacleProbe"/>, che
    /// restituisce altezza, spessore, normale della parete e spazio d'atterraggio. Qui restano solo
    /// le SOGLIE — cioe' la decisione di gioco — e la costruzione della traiettoria. Se nessuna
    /// banda si applica, non era un ostacolo e chi ha chiesto il salto salta.
    ///
    /// NON genera clip per altezza: la stessa clip viene warpata sui punti misurati.
    /// </summary>
    public bool TryStartParkour(Vector3 worldDirection)
    {
        if (Vaulting)
            return false;

        // Sotto la velocita' minima si salta e basta: con MinParkourSpeed a zero (default) la
        // condizione e' sempre vera e si scavalca anche da fermi.
        Vector3 planarVelocity = new(Velocity.X, 0f, Velocity.Z);
        if (planarVelocity.Length() < MinParkourSpeed)
            return false;

        Vector3 forward = worldDirection.LengthSquared() > 0.01f
            ? worldDirection.Normalized()
            : new Vector3(Mathf.Sin(SyncFacing), 0f, Mathf.Cos(SyncFacing));

        Vector3 feet = GlobalPosition - Vector3.Up * _standHeight * 0.5f;

        // La faccia si cerca sotto il piu' basso degli ostacoli che interessano, non a mezza banda:
        // un muretto di 90 cm passerebbe sotto un raggio all'altezza del petto.
        ObstacleProbe.ObstacleInfo info = ObstacleProbe.Scan(
            GetWorld3D(), GetRid(), feet, forward,
            MantleMaxHeight, VaultMinHeight * 0.7f, VaultReach, VaultMaxDepth,
            VaultLandingMargin + _capsule.Radius, _capsule);

        if (!info.Found || info.Height < VaultMinHeight)
            return false;

        // Scavalcare (si passa oltre) o arrampicarsi (si resta in cima): decide l'altezza misurata.
        // Il muro spesso non si scavalca — ci si finirebbe sopra a meta' traiettoria — ma se e'
        // abbastanza alto lo si arrampica, che e' esattamente il gesto giusto per un parapetto.
        bool vault = info.Height <= VaultMaxHeight
            && info.Depth <= VaultMaxDepth
            && info.LandingClear;
        bool mantle = info.Height <= MantleMaxHeight && info.TopStandable;

        if (vault)
        {
            // LandingPoint e' gia' il punto misurato OLTRE il bordo (la sonda ci ha applicato
            // VaultLandingMargin e ne ha verificato l'ingombro): qui resta solo da alzarlo di
            // mezza capsula, perche' la posizione del corpo e' il suo centro.
            BeginManeuver(ParkourPhase.Vault, VaultDuration,
                info.LandingPoint + Vector3.Up * _standHeight * 0.5f, info);
        }
        else if (mantle)
        {
            BeginMantle(info);
        }
        else
        {
            return false;
        }

        return true;
    }

    /// Arrampicata sul bordo misurato: ci si ferma sulla sommita', rientrando quanto basta a non
    /// restare in bilico sul bordo.
    private void BeginMantle(in ObstacleProbe.ObstacleInfo info) =>
        BeginManeuver(ParkourPhase.Mantle, MantleDuration,
            info.LedgePoint
                - info.WallNormal * Mathf.Min(info.Depth * 0.5f, 0.45f)
                + Vector3.Up * _standHeight * 0.5f,
            info);

    private void BeginManeuver(ParkourPhase phase, float duration, Vector3 end, in ObstacleProbe.ObstacleInfo info)
    {
        _phase = phase;
        _phaseDuration = duration;
        _vaultEnd = end;
        _vaultStart = GlobalPosition;
        _vaultLedge = info.LedgePoint;
        _phaseTime = 0f;
        SyncVaulting = true;
        Velocity = Vector3.Zero;
        ManeuverLedgeCollider = info.LedgeCollider;

        // Ci si raddrizza sulla parete PRIMA di partire: su un muro angolato la direzione di input
        // non e' quella dell'ostacolo, e la traiettoria si vedrebbe entrare di sbieco nel muro.
        SyncFacing = Mathf.Atan2(-info.WallNormal.X, -info.WallNormal.Z);

        OnVaultTriggered(_vaultLedge, info.WallNormal, info.Height);
    }

    /// <summary>
    /// Sposta la manovra in corso con il suo riferimento: <paramref name="delta"/> e' il moto del
    /// corpo su cui sta il bordo dall'ultimo tick (nuovo * vecchio⁻¹). Senza, issandosi su una barca
    /// che beccheggia la traiettoria restava ferma nel mondo e si finiva dentro o sopra il ponte.
    /// </summary>
    protected void ShiftManeuver(Transform3D delta)
    {
        if (!Vaulting)
            return;
        _vaultStart = delta * _vaultStart;
        _vaultLedge = delta * _vaultLedge;
        _vaultEnd = delta * _vaultEnd;
    }

    // ====================================================================================
    //  Nuoto
    // ====================================================================================

    /// <summary>
    /// Entra in acqua: da qui la verticale la decide <see cref="StepSwim"/>. Nessun evento di
    /// atterraggio (si cade in acqua, non a terra) e niente aggancio al pavimento, che in acqua
    /// tirerebbe verso il fondale.
    /// </summary>
    protected void BeginSwim()
    {
        if (SyncSwimming)
            return;
        SyncSwimming = true;
        FloorSnapLength = 0f;
        _landLock = 0f;
        ResetFallState();
    }

    /// Esce dall'acqua (riva, scaletta, area senza acqua). Si riparte "a terra": se sotto non c'e'
    /// niente la gravita' se ne accorge al primo tick, senza un falso atterraggio.
    protected void EndSwim()
    {
        if (!SyncSwimming)
            return;
        SyncSwimming = false;
        FloorSnapLength = FloorSnap;
        ResetFallState();
    }

    private void ResetFallState()
    {
        _wasGrounded = true;
        _fallSpeed = 0f;
        _airTime = 0f;
    }

    /// <summary>
    /// Un tick a nuoto.
    ///
    /// Orizzontale: come a piedi ma con poca accelerazione, piu' una frazione della velocita' delle
    /// particelle d'acqua (le onde fanno oscillare avanti e indietro, la media e' nulla).
    /// Verticale: una molla porta gli occhi a <see cref="SwimEyeAboveSurface"/> sopra l'onda e ne
    /// insegue la velocita' verticale, cosi' si sale e si scende con l'onda invece di esserne
    /// sommersi; tuffandosi da fermi la stessa molla frena la caduta. Con <paramref name="dive"/> si
    /// scende, con <paramref name="rise"/> si risale piu' svelti (e in superficie si emerge un po' di
    /// piu'); lasciati entrambi si torna su da soli.
    /// </summary>
    /// <param name="surfaceY">Quota della superficie VERA (marea e onde) sulla verticale del corpo.</param>
    /// <param name="waterVelocity">Velocita' delle particelle d'acqua alla superficie, in m/s.</param>
    protected void StepSwim(Vector3 worldDirection, float speed, bool rise, bool dive,
        float surfaceY, Vector3 waterVelocity, double delta)
    {
        float dt = (float)delta;

        if (Vaulting)
        {
            StepVault(dt);
            return;
        }

        // In acqua non ci si accovaccia: la capsula torna in piedi appena c'e' spazio.
        UpdateCrouch(dt, false, false);

        Vector3 velocity = Velocity;
        Vector3 planar = new(velocity.X, 0f, velocity.Z);
        Vector3 drift = new Vector3(waterVelocity.X, 0f, waterVelocity.Z) * SwimCurrentFollow;
        planar = planar.Lerp(worldDirection * speed + drift, Damp(SwimAcceleration, dt));

        float floatY = surfaceY + SwimEyeAboveSurface - (EyeHeight - _standHeight * 0.5f);
        if (rise)
            floatY += SwimSurfaceLift;
        float error = floatY - GlobalPosition.Y;

        // Sponda in pendenza (riva di un rio nel terreno, ~60°): ne' pavimento ne' parete, quindi ne'
        // MoveAndSlide la sale ne' la sonda del parkour la riconosce. Spingendoci contro ci si
        // arrampica, finche' i piedi non superano SwimClimbMaxAboveWater sopra l'acqua: oltre e' una
        // sponda alta, come per l'arrampicata da fermi.
        float feetY = GlobalPosition.Y - _standHeight * 0.5f;
        bool scrambling = feetY < surfaceY + SwimClimbMaxAboveWater && PushingIntoBank(worldDirection);

        float wantedVy;
        if (scrambling)
        {
            wantedVy = SwimVerticalSpeed;
        }
        else if (error < -0.6f)
        {
            // Ben fuori dall'acqua (cavo d'onda profondo, o appena caduti dentro): si cade.
            wantedVy = velocity.Y - Gravity * dt;
        }
        else
        {
            float up = rise ? SwimVerticalSpeed : SwimBuoyancySpeed;
            wantedVy = dive
                ? -SwimVerticalSpeed
                : Mathf.Clamp(error * SwimFloatStiffness, -SwimVerticalSpeed, up);

            // L'onda si insegue solo vicino alla superficie: a tre metri sotto non ti solleva.
            float nearSurface = Mathf.Clamp(1f - Mathf.Abs(error), 0f, 1f);
            if (!dive)
                wantedVy += waterVelocity.Y * nearSurface;
        }

        float vy = error < -0.6f && !scrambling
            ? wantedVy
            : Mathf.Lerp(velocity.Y, wantedVy, Damp(SwimVerticalResponse, dt));

        Velocity = new Vector3(planar.X, vy, planar.Z);
        MoveAndSlide();

        // Per l'animazione e per l'uscita dall'acqua: niente posa di volo, niente atterraggi.
        ResetFallState();
        SyncGrounded = true;
    }

    /// <summary>
    /// Vero se nell'ultimo <c>MoveAndSlide</c> si e' toccata una superficie in pendenza (normale con
    /// componente verticale fra 0,15 e 0,9) e la direzione voluta ci entra. Le pareti verticali sono
    /// escluse: quelle si superano con <see cref="TryStartSwimClimb"/>, non strisciandoci sopra.
    /// </summary>
    private bool PushingIntoBank(Vector3 worldDirection)
    {
        if (worldDirection.LengthSquared() < 0.01f)
            return false;

        Vector3 wanted = new Vector3(worldDirection.X, 0f, worldDirection.Z).Normalized();
        for (int i = 0; i < GetSlideCollisionCount(); i++)
        {
            Vector3 normal = GetSlideCollision(i).GetNormal();
            if (normal.Y < 0.15f || normal.Y > 0.9f)
                continue;

            var into = new Vector3(-normal.X, 0f, -normal.Z);
            if (into.LengthSquared() > 0.0001f && wanted.Dot(into.Normalized()) > 0.3f)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Prova a issarsi fuori dall'acqua sul bordo davanti (banchina, riva bassa, ponte di una barca).
    ///
    /// E' la stessa arrampicata del parkour con due differenze. La parete si cerca appena sotto il
    /// pelo dell'acqua e non all'altezza dei piedi: sotto c'e' spesso il vuoto (lo scafo, una
    /// banchina su pali). E il limite si misura dall'ACQUA, non dai piedi:
    /// <see cref="SwimClimbMaxAboveWater"/>, o <see cref="LadderClimbMaxAboveWater"/> accanto a una
    /// scaletta. Come <see cref="TryStartParkour"/> esprime un intento: se la geometria non lo
    /// permette restituisce falso e chi chiede continua a nuotare.
    /// </summary>
    public bool TryStartSwimClimb(Vector3 worldDirection, float surfaceY)
    {
        if (Vaulting)
            return false;

        Vector3 forward = worldDirection.LengthSquared() > 0.01f
            ? new Vector3(worldDirection.X, 0f, worldDirection.Z).Normalized()
            : new Vector3(Mathf.Sin(SyncFacing), 0f, Mathf.Cos(SyncFacing));

        Vector3 feet = GlobalPosition - Vector3.Up * _standHeight * 0.5f;
        float submerged = surfaceY - feet.Y;
        float maxHeight = submerged + Mathf.Max(SwimClimbMaxAboveWater, LadderClimbMaxAboveWater);

        // La parete si cerca a piu' quote, dal pelo dell'acqua in su. Una banchina e' verticale fino
        // in fondo, ma lo specchio di poppa di un barchino pesca appena 0,1 m e con l'onda si alza:
        // a pelo d'acqua il raggio passa sotto o prende il fondo che risale (normale quasi verticale,
        // scartata come rampa), mentre la parete vera sta 0,15-0,3 m piu' su.
        for (int i = 0; i < SwimClimbProbeOffsets.Length; i++)
        {
            float probeHeight = Mathf.Max(submerged + SwimClimbProbeOffsets[i], VaultMinHeight * 0.7f);
            ObstacleProbe.ObstacleInfo info = ObstacleProbe.Scan(
                GetWorld3D(), GetRid(), feet, forward,
                maxHeight, probeHeight, VaultReach, VaultMaxDepth,
                VaultLandingMargin + _capsule.Radius, _capsule);

            // Bordo sottile (la murata di una barca) con spazio libero oltre: si scavalca e si finisce
            // dentro, sul pagliolo. Arrampicarsi in cima lascerebbe in bilico sulla falchetta.
            bool thinOver = info.Found && info.LandingClear && info.Depth < SwimClimbVaultDepth
                && info.LandingPoint.Y < info.LedgePoint.Y;
            if (!info.Found || (!info.TopStandable && !thinOver))
                continue;

            float aboveWater = info.LedgePoint.Y - surfaceY;
            float limit = HasSwimLadderNear(info.WallPoint) ? LadderClimbMaxAboveWater : SwimClimbMaxAboveWater;
            if (aboveWater > limit)
                return false;

            if (thinOver)
                BeginManeuver(ParkourPhase.Vault, VaultDuration, info.LandingPoint + Vector3.Up * _standHeight * 0.5f, info);
            else
                BeginMantle(info);
            return true;
        }
        return false;
    }

    /// Spessore sotto il quale il bordo raggiunto a nuoto si scavalca invece di salirci sopra (m).
    private const float SwimClimbVaultDepth = 0.35f;

    /// Quote (m, rispetto al pelo dell'acqua) a cui la salita a nuoto cerca la parete, in ordine.
    private static readonly float[] SwimClimbProbeOffsets = { -0.1f, 0.05f, 0.2f, 0.35f };

    /// Una scaletta (nodo del gruppo <see cref="SwimLadderGroup"/>) entro <see cref="LadderReach"/>, in pianta.
    private bool HasSwimLadderNear(Vector3 point)
    {
        foreach (Node node in GetTree().GetNodesInGroup(SwimLadderGroup))
        {
            if (node is not Node3D ladder)
                continue;
            Vector3 offset = ladder.GlobalPosition - point;
            offset.Y = 0f;
            if (offset.Length() <= LadderReach)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Un tick di parkour: la radice segue il warp fra i punti misurati.
    ///
    /// La posizione si SCRIVE (movimento kinematico scriptato): <c>MoveAndSlide</c> combatterebbe
    /// contro l'ostacolo che si sta appunto superando. Le due fasi hanno profili verticali diversi:
    /// il vault passa SOPRA il bordo e ridiscende, il mantle sale e basta.
    /// </summary>
    private void StepVault(float dt)
    {
        _phaseTime += dt;
        float t = Mathf.Clamp(_phaseTime / Mathf.Max(_phaseDuration, 0.01f), 0f, 1f);

        GlobalPosition = _phase == ParkourPhase.Mantle ? MantlePoint(t) : VaultPoint(t);

        // Per l'animazione si e' "a terra" per tutta la durata: la posa la mette la clip di
        // parkour, non il layer di caduta. Le gambe sotto il one-shot sono comunque coperte.
        SyncGrounded = true;
        SyncLocalVelocity = Vector2.Zero;
        Velocity = Vector3.Zero;

        if (t >= 1f)
            EndParkour();
    }

    /// <summary>
    /// Traiettoria del vault: orizzontale con uno smoothstep unico, verticale che sale sul bordo
    /// entro meta' clip (la fase di appoggio/raccolta di <c>vault_low</c>) e ridiscende nella seconda.
    /// </summary>
    private Vector3 VaultPoint(float t)
    {
        Vector3 flat = _vaultStart.Lerp(_vaultEnd, Mathf.SmoothStep(0f, 1f, t));
        float apexY = _vaultLedge.Y + _standHeight * 0.5f + 0.08f;
        float y = t < 0.5f
            ? Mathf.Lerp(_vaultStart.Y, apexY, Mathf.SmoothStep(0f, 1f, t / 0.5f))
            : Mathf.Lerp(apexY, _vaultEnd.Y, Mathf.SmoothStep(0f, 1f, (t - 0.5f) / 0.5f));

        return new Vector3(flat.X, y, flat.Z);
    }

    /// <summary>
    /// Traiettoria del mantle, in due tempi come il gesto vero: prima ci si issa quasi in verticale
    /// fino ad avere il petto all'altezza dell'appiglio (l'orizzontale quasi non avanza, ci si tira
    /// su contro il muro), poi ci si rimette in piedi portando il baricentro oltre il bordo.
    ///
    /// Diviso in due invece che su una curva unica perche' e' la differenza che si legge a schermo:
    /// con una curva sola il personaggio scivolerebbe in diagonale attraverso lo spigolo.
    /// </summary>
    private Vector3 MantlePoint(float t)
    {
        const float pullUp = 0.62f; // frazione della clip spesa a issarsi

        // Fine della salita: petto sul bordo, ancora appoggiati alla faccia del muro.
        Vector3 hang = new(_vaultStart.X, _vaultLedge.Y + _standHeight * 0.5f, _vaultStart.Z);

        if (t < pullUp)
        {
            float k = Mathf.SmoothStep(0f, 1f, t / pullUp);
            Vector3 rise = _vaultStart.Lerp(hang, k);

            // Un filo di avvicinamento al muro, per non restare appesi a mezzo metro dalla parete.
            Vector3 approach = _vaultStart.Lerp(new Vector3(_vaultLedge.X, _vaultStart.Y, _vaultLedge.Z), k * 0.5f);
            return new Vector3(approach.X, rise.Y, approach.Z);
        }

        float s = Mathf.SmoothStep(0f, 1f, (t - pullUp) / (1f - pullUp));
        Vector3 from = new(
            Mathf.Lerp(_vaultStart.X, _vaultLedge.X, 0.5f),
            hang.Y,
            Mathf.Lerp(_vaultStart.Z, _vaultLedge.Z, 0.5f));

        return from.Lerp(_vaultEnd, s);
    }

    /// <summary>
    /// Interrompe la manovra in corso restituendo il controllo fisico normale.
    ///
    /// Serve a chi deve poterla annullare dall'esterno — un colpo incassato a meta' scavalcamento,
    /// la morte — senza conoscere lo stato interno. Chiamarla quando non c'e' nulla in corso non fa
    /// nulla. Il personaggio resta dov'e': non si teletrasporta indietro.
    /// </summary>
    public void CancelParkour()
    {
        if (_phase != ParkourPhase.None)
            EndParkour();
    }

    private void EndParkour()
    {
        _phase = ParkourPhase.None;
        _phaseTime = 0f;
        SyncVaulting = false;
        _wasGrounded = true;
        _fallSpeed = 0f;
        _airTime = 0f;
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

    /// <summary>
    /// Rileva il passaggio aria -> terra e notifica l'atterraggio una sola volta.
    ///
    /// Lo stato che si PUBBLICA non e' <c>IsOnFloor()</c> nudo ma la sua versione con isteresi: un
    /// distacco piu' breve di <see cref="GroundedGraceSeconds"/> mentre si sta ancora scendendo non
    /// e' una caduta. Senza, ogni spigolo e ogni raccordo di rampa produce un frame in volo, e a
    /// valle si vede la posa di caduta lampeggiare in continuazione mentre si cammina. La gravita'
    /// continua a leggere <c>IsOnFloor()</c> vero (in <c>StepMotion</c>): l'isteresi e' solo per
    /// l'animazione e per l'atterraggio, non altera la fisica.
    /// </summary>
    private void DetectLanding(float dt)
    {
        bool onFloor = IsOnFloor();
        _airTime = onFloor ? 0f : _airTime + dt;

        // Salendo (salto, vault) si e' in volo da subito: la grazia copre i distacchi involontari,
        // che avvengono sempre con velocita' verticale nulla o negativa.
        bool grounded = onFloor || (_airTime < GroundedGraceSeconds && Velocity.Y <= 0.01f);

        if (grounded && !_wasGrounded)
        {
            // Il blocco si arma QUI, non nel ricevitore dell'evento: la soglia e' la stessa che a
            // valle sceglie la clip land_hard (CharacterAnimator.HardLandingSpeed, riallineata dai
            // bridge), quindi movimento e posa si accendono sullo stesso criterio.
            if (_fallSpeed >= HardLandingSpeed)
                _landLock = HardLandingLockSeconds;

            OnLandTriggered(_fallSpeed);
        }

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
    /// Converte una velocita' planare in coordinate mondo nel riferimento dell'avatar:
    /// X = DESTRA, Y = avanti (il contratto di <see cref="SyncLocalVelocity"/>).
    ///
    /// La componente X del frame ruotato va NEGATA: il Visual guarda +Z e la sua sinistra e' +X,
    /// quindi <c>local.X &gt; 0</c> significa "verso la propria sinistra". Senza la negazione lo
    /// bacino del corpo in terza persona gira dalla parte sbagliata quando si cammina di lato
    /// (bug storico: si vede subito in tools/playtest_fps.gd, modalita' tp).
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

    /// Come sopra per il parkour: bordo, normale e altezza sono misure geometriche (vedi Vaulted).
    protected virtual void OnVaultTriggered(Vector3 ledgePoint, Vector3 wallNormal, float height) =>
        EmitSignal(SignalName.Vaulted, ledgePoint, wallNormal, height);

    /// Smorzamento esponenziale, indipendente dal frame rate.
    protected static float Damp(float speed, float dt) => 1.0f - Mathf.Exp(-speed * dt);
}
