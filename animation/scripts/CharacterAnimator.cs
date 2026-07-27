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
    private const string WeaponAmount = "parameters/WeaponBlend/blend_amount";
    private const string WeaponPoseRequest = "parameters/WeaponPose/transition_request";
    private const string FireRequest = "parameters/Fire/request";
    private const string JumpRequest = "parameters/Jump/request";
    private const string JumpTimeScale = "parameters/JumpScale/scale";

    private const string WalkSpaceNode = "WalkSpace";
    private const string RunSpaceNode = "RunSpace";
    private const string CrouchSpaceNode = "CrouchSpace";
    private const string JumpClipName = "jump";

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

    /// Velocita' di riassorbimento dell'abbassamento d'atterraggio, in frazione al secondo.
    [Export] public float LandingRecoverySpeed { get; set; } = 9.0f;

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

    private AnimationTree _tree = null!;
    private Vector2 _smoothedVelocity;
    private float _crouchWeight;
    private float _weaponWeight;
    private float _runWeight;
    private float _airWeight;
    private string _lastPoseRequest = "";
    private Vector3 _restPosition;
    private float _landingOffset;

    public override void _Ready()
    {
        _tree = GetNode<AnimationTree>("AnimationTree");
        _tree.Active = true;

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
        UpdateLandingDip(dt);
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
        // Tre spazi direzionali, stessa velocita' proiettata sul rombo di ciascuno.
        _tree.Set(WalkPosition, ClampToDiamond(_smoothedVelocity, WalkSpeed));
        _tree.Set(RunPosition, ClampToDiamond(_smoothedVelocity, RunSpeed));
        _tree.Set(CrouchPosition, ClampToDiamond(_smoothedVelocity, CrouchSpeed));

        // Peso della corsa: cresce fra WalkSpeed e RunSpeed, e basta. Non serve piu' il fattore di
        // "avantezza" che c'era quando l'unica clip di corsa era quella frontale: ora RunSpace ha
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

    private void UpdateWeapon(float dt)
    {
        // La posa dell'arma si sceglie PRIMA di alzarne il peso: cambiando arma la transizione
        // avviene mentre il layer e' gia' visibile, senza passare da disarmato.
        bool armed = WeaponPose != null && !string.IsNullOrEmpty(WeaponPose.HoldPose);
        if (armed)
        {
            string request = WeaponPose!.IsTwoHanded ? "rifle" : "pistol";
            if (request != _lastPoseRequest)
            {
                _tree.Set(WeaponPoseRequest, request);
                _lastPoseRequest = request;
            }
        }

        _weaponWeight = Mathf.Lerp(_weaponWeight, armed ? 1f : 0f, Damp(BlendSpeed, dt));
        _tree.Set(WeaponAmount, _weaponWeight);
    }

    /// Riassorbe l'abbassamento d'atterraggio e lo applica come offset del rig.
    private void UpdateLandingDip(float dt)
    {
        if (_landingOffset <= 0.0001f)
        {
            if (_landingOffset != 0f)
            {
                _landingOffset = 0f;
                Position = _restPosition;
            }
            return;
        }

        _landingOffset = Mathf.Lerp(_landingOffset, 0f, Damp(LandingRecoverySpeed, dt));
        Position = _restPosition - new Vector3(0f, _landingOffset, 0f);
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
    /// Due regimi, non uno: oltre <see cref="HardLandingSpeed"/> parte la clip di atterraggio duro,
    /// sotto resta la sola ammortizzazione procedurale del bacino. Sono ALTERNATIVI apposta — la
    /// clip contiene gia' la sua flessione, e sommarci quella procedurale farebbe sprofondare il
    /// personaggio nel pavimento.
    /// </summary>
    public void TriggerLand(float impactSpeed)
    {
        if (impactSpeed >= HardLandingSpeed)
        {
            Request(LandRequest);
            return;
        }

        float hardness = Mathf.Clamp(impactSpeed / Mathf.Max(HardLandingSpeed, 0.001f), 0f, 1f);
        _landingOffset = Mathf.Max(_landingOffset, hardness * LandingDip);
    }

    private void Request(string parameter) =>
        _tree.Set(parameter, (int)AnimationNodeOneShot.OneShotRequest.Fire);
}
