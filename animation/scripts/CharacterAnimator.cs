using Godot;

namespace Lagoon;

/// <summary>
/// Pilota l'<see cref="AnimationTree"/> del personaggio.
///
/// E' un RICEVITORE PURO: non sa chi lo alimenta e non legge nulla dal mondo. Non conosce
/// <c>PlayerController</c>, non interroga il <c>Multiplayer</c>, non valida niente. Chi lo guida
/// scrive nelle proprieta' pubbliche e chiama i metodi <c>Trigger*</c> — per il giocatore lo fa
/// <see cref="PlayerAnimationBridge"/>, per gli NPC lo fara' la loro IA. E' cosi' che lo stesso rig
/// serve entrambi senza che <c>animation/</c> dipenda da <c>player/</c>.
///
/// Rispetto a CLAUDE.md §3 questo componente sta interamente dal lato "resa": riceve stato gia'
/// replicato e lo trasforma in pose. L'autorita' e la validazione vivono nei controller di movimento
/// e combattimento, mai qui.
/// </summary>
public partial class CharacterAnimator : Node3D
{
    // Percorsi dei parametri dell'AnimationTree. Corrispondono ai nomi dei nodi creati da
    // tools/build_animation_tree.gd: cambiando l'albero vanno cambiati anche qui. Sono l'UNICO
    // punto di accoppiamento fra questo C# e la struttura dell'albero.
    private const string WalkPosition = "parameters/WalkSpace/blend_position";
    private const string RunPosition = "parameters/RunSpace/blend_position";
    private const string RunAmount = "parameters/MoveBlend/blend_amount";
    private const string CrouchPosition = "parameters/CrouchSpace/blend_position";
    private const string AirAmount = "parameters/AirBlend/blend_amount";
    private const string LandRequest = "parameters/Land/request";
    private const string CrouchAmount = "parameters/CrouchBlend/blend_amount";
    private const string HoldAmount = "parameters/HoldMask/blend_amount";
    private const string AimAmount = "parameters/AimAdd/add_amount";
    private const string AimPosition = "parameters/AimSpace/blend_position";
    private const string WeaponPoseRequest = "parameters/WeaponPose/transition_request";
    private const string FirePoseRequest = "parameters/FirePose/transition_request";
    private const string HitPoseRequest = "parameters/HitPose/transition_request";
    private const string LandPoseRequest = "parameters/LandPose/transition_request";
    private const string FireRequest = "parameters/Fire/request";
    private const string HitRequest = "parameters/Hit/request";
    private const string JumpRequest = "parameters/Jump/request";
    private const string VaultRequest = "parameters/Vault/request";
    private const string VaultPoseRequest = "parameters/VaultPose/transition_request";
    private const string JumpTimeScale = "parameters/JumpScale/scale";

    // Escursione dell'aim offset, in gradi: DEVONO combaciare con AIM_YAW_DEG e AIM_PITCH_DEG di
    // tools/blender/build_procedural_clips.py, perche' le pose additive sono authorate a quegli
    // angoli e la blend_position e' normalizzata su di essi.
    private const float AimYawRangeDeg = 60.0f;
    private const float AimPitchRangeDeg = 45.0f;

    private const string WalkSpaceNode = "WalkSpace";
    private const string RunSpaceNode = "RunSpace";
    private const string CrouchSpaceNode = "CrouchSpace";
    private const string JumpClipName = "jump";
    private const string VaultClipName = "vault_low";
    private const string MantleClipName = "mantle_high";

    /// Velocita' di transizione dei pesi di blend (crouch, arma), in frazione al secondo.
    [Export] public float BlendSpeed { get; set; } = 10.0f;

    /// Reattivita' del filtro sulla velocita' di locomozione: piu' alto = piu' scattante.
    [Export] public float VelocitySmoothing { get; set; } = 18.0f;

    /// <summary>
    /// Velocita' di camminata e di corsa in m/s. Devono combaciare con quelle di chi pilota: la prima
    /// e' anche il raggio del rombo dello spazio di camminata, la seconda decide a che punto il peso
    /// della corsa arriva a 1. <see cref="PlayerAnimationBridge"/> le riallinea a
    /// <c>PlayerController</c> in <c>_Ready</c>.
    /// </summary>
    [Export] public float WalkSpeed { get; set; } = 4.0f;

    [Export] public float RunSpeed { get; set; } = 7.0f;

    /// Velocita' da accovacciati, cioe' il raggio del rombo di CrouchSpace.
    [Export] public float CrouchSpeed { get; set; } = 2.0f;

    /// Velocita' con cui si entra e si esce dalla posa di caduta.
    [Export] public float AirBlendSpeed { get; set; } = 12.0f;

    /// <summary>
    /// Abbassamento massimo del bacino all'atterraggio, in metri. E' l'ammortizzazione PROCEDURALE:
    /// non esiste (ancora) una clip di atterraggio, e una flessione smorzata rende l'impatto leggibile
    /// senza spendere due clip su un evento che dura un terzo di secondo.
    /// </summary>
    [Export] public float LandingDip { get; set; } = 0.18f;

    /// Velocita' d'impatto a cui l'abbassamento e' massimo, in m/s. Sotto, scala proporzionalmente.
    [Export] public float HardLandingSpeed { get; set; } = 9.0f;

    /// <summary>
    /// Velocita' d'impatto oltre la quale parte la clip di atterraggio MORBIDO (land_soft).
    /// Sta sopra la velocita' d'impatto di un salto normale (JumpVelocity = 6 m/s): il salto
    /// resta coperto dalla sola flessione procedurale, la clip e' per le cadute vere.
    /// </summary>
    [Export] public float SoftLandingSpeed { get; set; } = 6.5f;

    /// Velocita' di riassorbimento dell'abbassamento d'atterraggio, in frazione al secondo.
    [Export] public float LandingRecoverySpeed { get; set; } = 9.0f;

    /// <summary>
    /// Ampiezza del passo sintetico del turn-in-place, in metri di blend per radiante al secondo di
    /// rotazione del corpo. Il turn-in-place non ha una clip propria: mentre il corpo ruota da
    /// fermo, si alimenta l'asse X del blend space con la velocita' di rotazione, cosi' le gambe
    /// riproducono i passetti dello strafe invece di scivolare sul posto.
    /// </summary>
    [Export] public float TurnStepScale { get; set; } = 0.4f;

    /// Rotazione minima (rad/s) perche' il passo sintetico si attivi: sotto, il corpo e' fermo.
    [Export] public float TurnStepThreshold { get; set; } = 0.5f;


    /// <summary>
    /// Altezza dell'ostacolo, in metri, oltre la quale il parkour usa la clip di ARRAMPICATA invece
    /// di quella di scavalcamento.
    ///
    /// Sta qui e non in chi si muove per la stessa ragione di <see cref="SoftLandingSpeed"/>: chi
    /// si muove manda una misura geometrica (l'altezza), la scelta della posa e' una decisione di
    /// resa. Va tenuta allineata a <c>CharacterMotor.VaultMaxHeight</c>, che decide quale delle due
    /// TRAIETTORIE si percorre: se divergono, la clip di una manovra accompagna il moto dell'altra.
    /// </summary>
    [Export] public float MantleHeight { get; set; } = 1.2f;

    // ====================================================================================
    //  Stato in ingresso: lo scrive chi pilota, ogni frame
    // ====================================================================================

    /// <summary>
    /// Velocita' orizzontale nel riferimento dell'AVATAR: X = destra, Y = avanti, in m/s.
    /// Stesse unita' e stesso riferimento di <c>PlayerController.SyncLocalVelocity</c>, e stessi assi
    /// del BlendSpace2D: non serve nessuna conversione lungo la catena.
    /// </summary>
    public Vector2 LocalVelocity { get; set; }

    /// Accovacciato.
    public bool Crouching { get; set; }

    /// A terra. In aria la locomozione resta ferma sull'ultima posa invece di "camminare nel vuoto".
    public bool Grounded { get; set; } = true;

    /// <summary>
    /// Durata reale del volo di un salto, in secondi. Serve a riscalare la clip <c>jump</c>, che e' un
    /// arco completo di 1,03 s: senza, il personaggio atterra mentre la clip e' ancora a mezz'aria.
    /// Chi pilota la calcola dai propri parametri di salto (per il giocatore: 2 * JumpVelocity / Gravity).
    /// Zero o negativo = nessuna riscalatura.
    /// </summary>
    public float JumpFlightTime { get; set; }

    /// <summary>
    /// Posa dell'arma impugnata, o null da disarmato. Da null il layer arma si spegne e resta la sola
    /// locomozione. Cambiare arma non tocca in alcun modo la locomozione: e' l'unico punto che serve
    /// aggiornare per aggiungere un'arma nuova.
    /// </summary>
    public WeaponAnimationSet? WeaponPose { get; set; }

    /// <summary>
    /// Direzione in cui si sta mirando, in coordinate MONDO. Vettore nullo = non si mira.
    ///
    /// Non e' ridondante con l'orientamento dell'avatar: da quando la mira e' uno stato (RMB) il
    /// busto puo' guardare dove il corpo non guarda, e questa porta anche l'INCLINAZIONE, che
    /// nessuna clip contiene e che senza un layer procedurale non esisterebbe affatto.
    /// </summary>
    public Vector3 AimDirection { get; set; }

    /// <summary>
    /// Stance di mira attiva. Decide se aim offset e mira procedurale sono accesi: da armati SENZA
    /// mira l'arma e' portata rilassata (delta di porto basso) e il corpo cammina come da disarmato.
    /// </summary>
    public bool Aiming { get; set; }

    /// <summary>
    /// Velocita' di rotazione del corpo in rad/s (positiva = verso sinistra), gia' smorzata da chi
    /// pilota. Alimenta il passo sintetico del turn-in-place.
    /// </summary>
    public float TurnRate { get; set; }

    /// <summary>
    /// Quanto e' in corso una manovra di parkour, da 0 a 1. Zero se il rig non c'e'.
    ///
    /// Unico punto da cui il resto dell'animatore legge "sto scavalcando": la misura la tiene
    /// <see cref="VaultIkRig"/>, che e' anche l'unico a sapere quanto dura davvero la clip.
    /// </summary>
    private float ParkourBlend => _vaultRig?.ParkourActive ?? 0f;

    /// <summary>
    /// Ricostruisce la direzione di mira in coordinate mondo da imbardata e inclinazione replicate.
    /// Sta qui, e non nei bridge, perche' e' la stessa formula per giocatore e NPC e definisce il
    /// contratto di <see cref="AimDirection"/>.
    /// </summary>
    public static Vector3 AimVector(float yaw, float pitch)
    {
        float horizontal = Mathf.Cos(pitch);
        return new Vector3(
            Mathf.Sin(yaw) * horizontal,
            Mathf.Sin(pitch),
            Mathf.Cos(yaw) * horizontal);
    }

    private AnimationTree _tree = null!;
    private AimRig? _aimRig;
    private FootIkRig? _footRig;
    private WeaponGripRig? _gripRig;
    private VaultIkRig? _vaultRig;
    private Vector2 _smoothedVelocity;
    private float _crouchWeight;
    private float _weaponWeight;
    private float _aimWeight;
    private Vector2 _aimOffset;
    private float _runWeight;
    private float _airWeight;
    private string _lastPoseRequest = "";
    private string _lastFirePoseRequest = "";
    private Vector3 _restPosition;
    private float _landingOffset;

    public override void _Ready()
    {
        _tree = GetNode<AnimationTree>("AnimationTree");
        _tree.Active = true;

        // Opzionale: un rig senza layer procedurale continua a funzionare, solo senza mira verticale.
        _aimRig = GetNodeOrNull<AimRig>("AimRig");
        _footRig = GetNodeOrNull<FootIkRig>("FootIkRig");
        _gripRig = GetNodeOrNull<WeaponGripRig>("GripRig");
        _vaultRig = GetNodeOrNull<VaultIkRig>("VaultRig");

        // Posizione a riposo del rig: l'abbassamento d'atterraggio e' un OFFSET da questa, non un
        // valore assoluto. Il rig sta gia' a Y = -1 sotto Visual (origine ai piedi contro capsula
        // centrata), e scrivere Position senza ripartire da qui lo farebbe risalire di un metro.
        _restPosition = Position;

        VerifyBlendSpaceBounds();
    }

    /// <summary>
    /// Controlla che <see cref="WalkSpeed"/> combaci col raggio del rombo di <c>WalkSpace</c>.
    ///
    /// Le due grandezze vivono in posti diversi — qui e in <c>tools/build_animation_tree.gd</c> — e se
    /// divergono il sintomo e' muto: la posizione di blend finisce fuori dai triangoli e lo scheletro
    /// ricade sulla rest pose, cioe' la T-pose, senza un solo errore. Meglio dirlo in console.
    /// </summary>
    private void VerifyBlendSpaceBounds()
    {
        if (_tree.TreeRoot is not AnimationNodeBlendTree blendTree)
            return;

        WarnIfMismatched(blendTree, WalkSpaceNode, WalkSpeed, nameof(WalkSpeed));
        WarnIfMismatched(blendTree, RunSpaceNode, RunSpeed, nameof(RunSpeed));
        WarnIfMismatched(blendTree, CrouchSpaceNode, CrouchSpeed, nameof(CrouchSpeed));
    }

    private static void WarnIfMismatched(AnimationNodeBlendTree tree, string node, float speed, string label)
    {
        if (tree.GetNode(node) is not AnimationNodeBlendSpace2D space)
            return;

        float radius = space.MaxSpace.Y;
        if (!Mathf.IsEqualApprox(radius, speed))
        {
            GD.PushWarning($"[CharacterAnimator] {label}={speed} ma {node} arriva a {radius}: " +
                "rigenera l'albero con tools/build_animation_tree.gd o riallinea le velocita'.");
        }
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;

        // La velocita' NON si azzera piu' in aria: a coprire il volo c'e' il layer di caduta, che
        // SOSTITUISCE la locomozione invece di metterla a riposo. Cosi' all'atterraggio il ciclo di
        // passo riprende alla velocita' giusta, senza ripartire da fermo.
        _smoothedVelocity = _smoothedVelocity.Lerp(LocalVelocity, Damp(VelocitySmoothing, dt));

        UpdateLocomotion();
        UpdateAir(dt);
        UpdateCrouch(dt);
        UpdateWeapon(dt);
        UpdateAimOffset(dt);
        UpdateAimRig();
        UpdateFootRig();
        UpdatePelvisOffset(dt);
    }

    /// <summary>
    /// Alimenta il layer procedurale. La mira ha effetto solo da armati e a terra: a mezz'aria il
    /// busto segue la caduta, e torcerlo verso il cursore mentre si vola sarebbe piu' strano
    /// dell'assenza di mira.
    /// </summary>
    private void UpdateAimRig()
    {
        if (_aimRig == null)
            return;

        _aimRig.AimDirection = AimDirection;
        _aimRig.LocalVelocity = _smoothedVelocity;

        // Contro un muro l'arma si alza in "port arms": inseguire ancora il bersaglio col busto
        // torcerebbe il torso verso un punto che l'arma non sta piu' guardando.
        // Il busto insegue la mira SOLO in mira: fuori mira l'arma e' portata rilassata e il
        // torso appartiene alla locomozione. Da accovacciati e' stato provato a raddrizzarlo, per
        // togliere l'arma dalle gambe: a schermo peggiora — il personaggio si accovaccia con la
        // schiena dritta e la posa non legge piu' come un accovacciamento. La correzione giusta
        // e' locale alle braccia (WeaponGripRig.CrouchLift), non al busto.
        //
        // Durante il parkour la mira si spegne come in aria. Non basta la condizione su Grounded:
        // chi scavalca si dichiara A TERRA per tutta la manovra (la posa la mette la clip, non il
        // layer di caduta), quindi senza questo il busto continuerebbe a inseguire il cursore
        // mentre le braccia sono su un muro.
        float clearance = 1f - (_gripRig?.MuzzleBlocked ?? 0f);
        bool armedOnFoot = WeaponPose != null && Grounded && ParkourBlend < 0.05f;
        _aimRig.Weight = armedOnFoot && Aiming ? clearance : 0f;

        // La mano di supporto sta sull'arma SEMPRE, non solo in mira: e' un vincolo fisico — la
        // mano sinistra impugna l'astina — e un vincolo che vale solo mirando lascia la mano a
        // fluttuare accanto all'arma in tutto il resto del gioco.
        //
        // Prima era legata ad Aiming perche' il polo del gomito, misurato sulla posa di mira,
        // nel porto rilassato flippava. Non piu': il porto e' derivato dalla mira ruotando le
        // braccia in blocco, quindi l'arma resta fra le mani anche li', e il polo — figlio del
        // punto di presa — ruota con l'arma. Verificato su porto e mira, fermi, in camminata e in
        // corsa: la mano resta entro 3 mm dall'astina.
        //
        // L'unica eccezione e' il PARKOUR, dove le stesse mani le vuole VaultIkRig per metterle sul
        // bordo. Due IK sulle stesse ossa vanno arbitrati da un punto solo — la stessa regola del
        // bacino in UpdatePelvisOffset — e qui vince il bordo: e' l'appiglio a reggere il peso del
        // personaggio, l'arma puo' aspettare mezzo secondo.
        if (_gripRig != null)
        {
            _gripRig.SupportActive = WeaponPose != null && (_vaultRig?.HandsOnLedge ?? 0f) < 0.05f;

            // Braccia e canna alzate da accovacciati, e solo fuori mira: li' le ginocchia stanno
            // alte davanti al petto e il porto rilassato porta avambracci e canna dentro le
            // cosce. In mira resta a zero — l'arma deve guardare dove guarda il giocatore, e la
            // posa di mira tiene gia' le braccia alte.
            _gripRig.CrouchLift = armedOnFoot && !Aiming ? _crouchWeight : 0f;

            // Riposta per l'intera manovra, non solo mentre le mani toccano: vedere l'arma
            // riapparire fra lo stacco e l'appoggio sarebbe peggio che non toglierla affatto.
            _gripRig.Stowed = ParkourBlend >= 0.5f;
        }
    }

    /// Alimenta i piedi a terra. In aria si spegne da solo: non c'e' suolo da assecondare.
    private void UpdateFootRig()
    {
        if (_footRig == null)
            return;

        _footRig.Grounded = Grounded;
        _footRig.LocalVelocity = _smoothedVelocity;
    }

    /// <summary>
    /// Smorzamento esponenziale, indipendente dal frame rate.
    ///
    /// La forma ingenua <c>clamp(k * dt)</c> non lo e': a 30 fps converge quasi il doppio piu' in
    /// fretta per unita' di tempo che a 144. Qui conta perche' questo componente gira in
    /// <c>_Process</c> (frame di RENDER) mentre il movimento che lo alimenta gira in
    /// <c>_PhysicsProcess</c> (tick FISSO): i due passi non coincidono quasi mai, e con la forma
    /// ingenua la locomozione risulterebbe piu' o meno reattiva a seconda del frame rate.
    /// </summary>
    private static float Damp(float speed, float dt) => 1.0f - Mathf.Exp(-speed * dt);

    private void UpdateLocomotion()
    {
        // La posizione di blend va confinata nel ROMBO |x| + |y| <= WalkSpeed, che e' esattamente la
        // regione coperta dai quattro triangoli di WalkSpace. In diagonale a piena velocita' si avrebbe
        // |x| = |y| = WalkSpeed / sqrt(2), cioe' una somma di 1,41 * WalkSpeed: un punto FUORI dai
        // triangoli, dove il blend space non produce nulla e lo scheletro torna in T-pose.
        //
        // La stessa velocita' si scrive in TUTTI gli spazi, ognuno proiettato sul proprio rombo:
        // sono alternativi fra loro, e quello con peso 0 continua comunque ad avanzare (sync) per
        // non ripartire da un tempo vecchio quando torna in scena.
        Vector2 blendVelocity = _smoothedVelocity;

        // Passo sintetico del turn-in-place: da fermi in mira, mentre il corpo ruota, le gambe
        // riproducono lo strafe proporzionale alla rotazione. TurnRate positivo = il corpo gira a
        // SINISTRA, quindi i piedi fanno passetti a sinistra: X di blend negativa (X = destra).
        if (Aiming && Grounded && blendVelocity.Length() < 0.5f
            && Mathf.Abs(TurnRate) > TurnStepThreshold)
        {
            float maxStep = WalkSpeed * 0.6f;
            blendVelocity.X += Mathf.Clamp(-TurnRate * TurnStepScale, -maxStep, maxStep);
        }

        Vector2 walk = ClampToDiamond(blendVelocity, WalkSpeed);
        Vector2 run = ClampToDiamond(blendVelocity, RunSpeed);

        _tree.Set(WalkPosition, walk);
        _tree.Set(RunPosition, run);
        _tree.Set(CrouchPosition, ClampToDiamond(blendVelocity, CrouchSpeed));

        // Peso della corsa: cresce fra WalkSpeed e RunSpeed, e basta. Non serve piu' il fattore di
        // "avantezza" che c'era quando l'unica clip di corsa era quella frontale: ora ogni spazio ha
        // tutti e quattro gli assi, quindi correre di lato mostra lo strafe di corsa.
        float speed = _smoothedVelocity.Length();
        float band = Mathf.Max(RunSpeed - WalkSpeed, 0.001f);
        _runWeight = Mathf.Clamp((speed - WalkSpeed) / band, 0f, 1f);
        _tree.Set(RunAmount, _runWeight);
    }

    /// Proiezione sulla palla L1 di raggio <paramref name="radius"/> (il rombo dei triangoli).
    private static Vector2 ClampToDiamond(Vector2 v, float radius)
    {
        float l1 = Mathf.Abs(v.X) + Mathf.Abs(v.Y);
        return l1 > radius ? v * (radius / l1) : v;
    }

    /// Posa di caduta: sostituisce la locomozione mentre si e' in aria.
    private void UpdateAir(float dt)
    {
        _airWeight = Mathf.Lerp(_airWeight, Grounded ? 0f : 1f, Damp(AirBlendSpeed, dt));
        _tree.Set(AirAmount, _airWeight);
    }

    private void UpdateCrouch(float dt)
    {
        _crouchWeight = Mathf.Lerp(_crouchWeight, Crouching ? 1f : 0f, Damp(BlendSpeed, dt));
        _tree.Set(CrouchAmount, _crouchWeight);
    }

    /// <summary>
    /// Layer di impugnatura: una posa ASSOLUTA delle sole braccia, sostituita alla locomozione da un
    /// <c>Blend2</c> filtrato sulle otto ossa delle braccia (<c>HoldMask</c>).
    ///
    /// La locomozione resta agnostica dall'arma: reggere un fucile o una pistola non cambia come si
    /// cammina. Ma la presa e' un VINCOLO geometrico — due mani sulla stessa arma — e non uno scarto
    /// da sommare: come delta additivo riproduceva la posa giusta solo sopra la clip di riferimento,
    /// e su qualunque locomozione le mani finivano fuori dall'arma (misure nell'intestazione di
    /// <c>tools/build_weapon_poses.gd</c>). Rachide e bacino NON sono nella maschera, quindi le
    /// braccia seguono in blocco il busto della locomozione tenendo la presa, e l'errore residuo di
    /// mira lo chiude <c>SpineAimModifier</c>.
    /// </summary>
    private void UpdateWeapon(float dt)
    {
        // La posa dell'arma si sceglie PRIMA di alzarne il peso: cambiando arma la transizione
        // avviene mentre il layer e' gia' visibile, senza passare da disarmato.
        bool armed = WeaponPose != null && !string.IsNullOrEmpty(WeaponPose.HoldPose);
        bool twoHanded = armed && WeaponPose!.IsTwoHanded;

        if (armed)
        {
            // La posa dipende da arma E stato di mira: porto rilassato senza RMB, mira con.
            // I nomi sono gli ingressi del Transition WeaponPose (build_animation_tree.gd).
            string request = twoHanded
                ? (Aiming ? "rifle_aim" : "rifle_lowered")
                : (Aiming ? "pistol_aim" : "pistol");
            if (request != _lastPoseRequest)
            {
                _tree.Set(WeaponPoseRequest, request);
                _lastPoseRequest = request;
            }

            // La clip di sparo la dichiara l'ARMA (FirePose = nome dell'ingresso del
            // Transition FirePose): cosi' la pistola non spara con l'animazione del fucile.
            // Si imposta al cambio d'arma, PRIMA che il one-shot Fire possa partire.
            string firePose = WeaponPose!.FirePose;
            if (!string.IsNullOrEmpty(firePose) && firePose != _lastFirePoseRequest)
            {
                _tree.Set(FirePoseRequest, firePose);
                _lastFirePoseRequest = firePose;
            }
        }
        else
        {
            // Da disarmato il Transition resta sull'ultimo ingresso: si azzerano le richieste
            // memorizzate cosi' al prossimo equipaggiamento vengono rimandate anche se l'arma
            // e' la stessa di prima.
            _lastPoseRequest = "";
            _lastFirePoseRequest = "";
        }

        // Peso della maschera di impugnatura: da armati e' sempre acceso, con qualunque locomozione
        // sotto (in piedi, accovacciati, in aria). La posa la sceglie il Transition qui sopra.
        //
        // L'unica eccezione e' il PARKOUR: durante scavalcamento e arrampicata le braccia servono a
        // reggere il peso del corpo, e tenere la posa d'impugnatura sopra la clip la annullerebbe.
        // L'arma non viene rinfoderata davvero — resta equipaggiata, con caricatore e slot intatti:
        // qui si toglie solo di mezzo, e WeaponVisual la nasconde dalla stessa misura.
        float overlay = armed && ParkourBlend < 0.5f ? 1f : 0f;
        _weaponWeight = Mathf.Lerp(_weaponWeight, overlay, Damp(BlendSpeed, dt));
        _tree.Set(HoldAmount, _weaponWeight);
    }

    /// <summary>
    /// Aim offset additivo: la "sfera di mira" a 5 pose, pilotata da yaw/pitch della mira RELATIVI
    /// al corpo. Il grosso della posa (spalle e braccia comprese, che il procedurale non tocca) lo
    /// mette questo layer; l'errore residuo — che dipende da quale clip sta girando e con che peso —
    /// lo chiude SpineAimModifier rimisurando la posa vera di ogni frame.
    /// </summary>
    private void UpdateAimOffset(float dt)
    {
        Vector2 target = Vector2.Zero;
        bool aiming = Aiming && WeaponPose != null && AimDirection.LengthSquared() > 0.0001f;
        if (aiming)
        {
            // La mira e' in coordinate MONDO: si porta nel riferimento del rig, dove il corpo
            // guarda +Z e la sua sinistra e' +X. Yaw POSITIVO = mira a DESTRA del corpo (quindi
            // -X locale), pitch positivo = in alto: sono gli assi dell'AimSpace.
            Vector3 local = GlobalTransform.Basis.Inverse() * AimDirection;
            float yaw = Mathf.Atan2(-local.X, local.Z);
            float pitch = Mathf.Asin(Mathf.Clamp(local.Y, -1f, 1f));
            target = new Vector2(
                Mathf.Clamp(yaw / Mathf.DegToRad(AimYawRangeDeg), -1f, 1f),
                Mathf.Clamp(pitch / Mathf.DegToRad(AimPitchRangeDeg), -1f, 1f));
        }

        float weight = aiming ? 1f : 0f;
        _aimWeight = Mathf.Lerp(_aimWeight, weight, Damp(BlendSpeed, dt));
        _aimOffset = _aimOffset.Lerp(target, Damp(BlendSpeed, dt));
        _tree.Set(AimAmount, _aimWeight);
        _tree.Set(AimPosition, _aimOffset);
    }

    /// <summary>
    /// Posizione verticale del rig: un solo scrittore per due contributi.
    ///
    /// L'ammortizzazione d'atterraggio e l'abbassamento dei piedi a terra vogliono entrambi
    /// abbassare il bacino. Tenerli separati significherebbe due nodi che scrivono la stessa
    /// <c>Position</c> nello stesso frame, e chi arriva secondo cancella il primo — un conflitto che
    /// non da' errori e si manifesta come "l'IK dei piedi ogni tanto non funziona".
    /// Si sommano: l'atterraggio e' un impulso che rientra, i piedi un offset continuo.
    /// </summary>
    private void UpdatePelvisOffset(float dt)
    {
        _landingOffset = _landingOffset <= 0.0001f
            ? 0f
            : Mathf.Lerp(_landingOffset, 0f, Damp(LandingRecoverySpeed, dt));

        float drop = _landingOffset + (_footRig?.PelvisDrop ?? 0f);
        Position = _restPosition - new Vector3(0f, drop, 0f);
    }

    // ====================================================================================
    //  Eventi one-shot
    // ====================================================================================

    /// <summary>
    /// Sparo. Filtrato sull'upper body dall'albero, quindi si sovrappone alla locomozione senza
    /// interromperla: sparare correndo non ferma le gambe.
    /// </summary>
    public void TriggerFire() => Request(FireRequest);

    /// <summary>
    /// Salto. Coinvolge tutto il corpo, nessun filtro. La clip viene riscalata sul tempo di volo
    /// dichiarato da chi pilota, cosi' l'arco animato finisce quando finisce il volo vero.
    /// </summary>
    public void TriggerJump()
    {
        if (JumpFlightTime > 0.01f && _tree.HasAnimation(JumpClipName))
        {
            float clipLength = (float)_tree.GetAnimation(JumpClipName).Length;
            _tree.Set(JumpTimeScale, clipLength / JumpFlightTime);
        }

        Request(JumpRequest);
    }

    /// <summary>
    /// Atterraggio, con la velocita' d'impatto in m/s.
    ///
    /// TRE regimi alternativi, mai sommati (la clip contiene gia' la propria flessione, e
    /// sommarci quella procedurale farebbe sprofondare il personaggio nel pavimento):
    ///  - oltre <see cref="HardLandingSpeed"/>: clip di atterraggio duro;
    ///  - fra <see cref="SoftLandingSpeed"/> e Hard: clip di atterraggio morbido (procedurale);
    ///  - sotto: sola ammortizzazione del bacino — un salto normale non merita una clip.
    /// La posa la sceglie il Transition LandPose, impostato PRIMA del one-shot.
    /// </summary>
    public void TriggerLand(float impactSpeed)
    {
        if (impactSpeed >= SoftLandingSpeed)
        {
            _tree.Set(LandPoseRequest, impactSpeed >= HardLandingSpeed ? "land_hard" : "land_soft");
            Request(LandRequest);
            return;
        }

        float hardness = Mathf.Clamp(impactSpeed / Mathf.Max(HardLandingSpeed, 0.001f), 0f, 1f);
        _landingOffset = Mathf.Max(_landingOffset, hardness * LandingDip);
    }

    /// <summary>
    /// Reazione al colpo. <paramref name="worldDirection"/> e' la direzione di VOLO del proiettile
    /// in coordinate mondo, calcolata dall'host (nel payload di rete viaggia la direzione, mai il
    /// danno: CLAUDE.md §3). Il flinch e' un delta additivo sul busto: si somma a qualunque
    /// locomozione/mira in corso, identico in piedi, accovacciati o in corsa.
    /// </summary>
    public void TriggerHitReaction(Vector3 worldDirection)
    {
        if (worldDirection.LengthSquared() < 0.0001f)
            return;

        // Nel riferimento del rig il corpo guarda +Z e la sua sinistra e' +X. Un proiettile che
        // viaggia verso -Z arriva da davanti; uno che viaggia verso -X arriva dalla sua sinistra.
        Vector3 local = GlobalTransform.Basis.Inverse() * worldDirection;
        string pose = Mathf.Abs(local.Z) >= Mathf.Abs(local.X)
            ? (local.Z < 0f ? "front" : "back")
            : (local.X < 0f ? "left" : "right");

        _tree.Set(HitPoseRequest, pose);
        Request(HitRequest);
    }

    /// <summary>
    /// Parkour: scavalcamento o arrampicata, scelti QUI dall'altezza misurata dell'ostacolo — allo
    /// stesso modo in cui <see cref="TriggerLand"/> sceglie il tipo di atterraggio dalla sola
    /// velocita' d'impatto. Chi si muove manda misure, non decisioni di resa.
    ///
    /// Le clip sono IN PLACE e generiche: la traiettoria della radice la deforma il motion warping
    /// di chi pilota (che da <see cref="VaultClipLength"/> e <see cref="MantleClipLength"/> sa
    /// quanto durano le pose), le mani le mette <see cref="VaultIkRig"/> sul bordo misurato.
    /// </summary>
    /// <param name="ledgePoint">Punto d'appiglio sul bordo, in coordinate mondo.</param>
    /// <param name="wallNormal">Normale orizzontale della parete: da' l'orientamento delle mani.</param>
    /// <param name="height">Altezza dell'ostacolo rispetto ai piedi, in metri.</param>
    public void TriggerVault(Vector3 ledgePoint, Vector3 wallNormal, float height)
    {
        bool mantle = height > MantleHeight;

        _tree.Set(VaultPoseRequest, mantle ? MantleClipName : VaultClipName);

        // Arrampicandosi le mani reggono il peso quasi fino in cima; scavalcando l'appoggio e' un
        // tocco a meta' traiettoria. La finestra e' una proprieta' della clip, non del rig.
        if (mantle)
            _vaultRig?.Begin(ledgePoint, wallNormal, MantleClipLength, 0.10f, 0.80f);
        else
            _vaultRig?.Begin(ledgePoint, wallNormal, VaultClipLength);

        Request(VaultRequest);
    }

    /// Durata della clip di scavalcamento, per sincronizzare il motion warping alla posa.
    public float VaultClipLength => ClipLength(VaultClipName, 0.9f);

    /// Durata della clip di arrampicata. Finche' la clip non esiste, vale il valore dichiarato.
    public float MantleClipLength => ClipLength(MantleClipName, 1.4f);

    private float ClipLength(string clip, float fallback) =>
        _tree.HasAnimation(clip) ? (float)_tree.GetAnimation(clip).Length : fallback;

    private void Request(string parameter) =>
        _tree.Set(parameter, (int)AnimationNodeOneShot.OneShotRequest.Fire);
}
