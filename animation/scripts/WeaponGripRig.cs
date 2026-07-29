using Godot;

namespace Lagoon;

/// <summary>
/// Aggancia l'arma alla MANO del personaggio e ci mette in IK la mano di supporto.
///
/// Prima l'arma stava su un <c>Node3D</c> con offset fisso sotto <c>Visual</c>: seguiva il corpo ma
/// non la mano, quindi fluttuava accanto al fianco e non aveva alcun rapporto con la posa animata.
/// Qui il punto di presa e' figlio di un <see cref="BoneAttachment3D"/> su <c>RightHand</c>, quindi
/// e' la mano a portarsi dietro l'arma — in locomozione, sparando e da accovacciati, senza un solo
/// caso particolare.
///
/// I nodi vengono creati DA CODICE e non messi nella scena: il rig arriva da <c>Body_Base.glb</c>,
/// che si rigenera (vedi la skill <c>blender-pipeline</c>), e figli aggiunti a mano dentro una scena
/// istanziata da un <c>.glb</c> si perdono o si sdoppiano a ogni reimport. Costruirli qui li lega ai
/// NOMI dei bone, che sono stabili, invece che alla struttura del file importato.
///
/// E' pura resa: gira identico su ogni peer a partire da stato gia' replicato e non produce niente
/// da sincronizzare (CLAUDE.md §3).
/// </summary>
public partial class WeaponGripRig : Node3D
{
    private const string HandBone = "RightHand";
    private const string SupportRootBone = "LeftArm";
    private const string SupportMidBone = "LeftForeArm";
    private const string SupportTipBone = "LeftHand";

    /// Velocita' con cui la mano di supporto entra ed esce dall'IK, in frazione al secondo.
    [Export] public float SupportBlendSpeed { get; set; } = 8.0f;

    /// <summary>
    /// Lunghezza presunta dell'arma davanti alla presa, in metri: e' la portata della sonda che
    /// cerca i muri. Approssimata apposta — serve a decidere QUANDO alzare la canna, non a
    /// misurare l'arma.
    /// </summary>
    [Export] public float MuzzleReach { get; set; } = 0.75f;

    /// Margine oltre la volata entro cui si comincia gia' ad alzare l'arma, in metri.
    [Export] public float MuzzleClearance { get; set; } = 0.15f;

    /// Di quanto si alza la canna quando l'arma e' del tutto ostruita, in gradi.
    [Export] public float PortArmsPitchDegrees { get; set; } = 55.0f;

    /// Di quanto si ritrae l'arma verso il corpo quando e' del tutto ostruita, in metri.
    [Export] public float PortArmsPullBack { get; set; } = 0.18f;

    /// Velocita' con cui si entra e si esce dal "port arms", in frazione al secondo.
    [Export] public float PortArmsSpeed { get; set; } = 9.0f;

    /// <summary>
    /// IK della mano di supporto sull'astina.
    ///
    /// Era spento finche' l'IK era un modificatore scritto in casa che sembrava non applicare la
    /// posa. In realta' applicava: era la MISURA a essere sbagliata (vedi la skill
    /// <c>character-animation</c>). Ora la catena la risolve <see cref="TwoBoneIK3D"/>, nativo di
    /// Godot 4.7, che converge a zero purche' gli si dia un <c>pole_node</c> (vedi
    /// <see cref="SupportElbowHint"/>).
    ///
    /// L'aggancio dell'arma alla mano destra NON passa di qui: e' il BoneAttachment3D.
    /// </summary>
    [Export] public bool EnableSupportHandIk { get; set; } = true;

    /// <summary>
    /// Dove punta il GOMITO sinistro, in coordinate locali al punto di presa.
    ///
    /// Non e' un dettaglio estetico: senza un <c>pole_node</c> <see cref="TwoBoneIK3D"/> non risolve
    /// affatto la catena — misurato, lo spostamento della mano resta sotto i 4 mm e l'errore al
    /// bersaglio non cala. Dichiarare la sola <c>pole_direction</c> non basta.
    ///
    /// **Si MISURA dalla posa di mira, insieme alla presa** (lo stampa
    /// <c>tools/build_weapon_poses.gd</c>): vive nel frame dell'ARMA, quindi ruota con
    /// <see cref="WeaponAnimationSet.GripRotationDegrees"/>. Cambiare la presa senza rimisurarlo
    /// porta il polo dalla parte opposta e il gomito si piega **al contrario** — e nessun controllo
    /// di distanza se ne accorge, perche' la mano continua a raggiungere l'astina lo stesso.
    /// </summary>
    [Export] public Vector3 SupportElbowHint { get; set; } = new(0.126f, 0.145f, 0.081f);

    /// <summary>
    /// Punto di presa: qui va messa l'arma. E' figlio del <see cref="BoneAttachment3D"/> sulla mano
    /// destra, quindi la sua trasformata mondo e' gia' quella giusta ogni frame.
    /// </summary>
    public Node3D? GripPoint { get; private set; }

    /// <summary>
    /// La mano di supporto insegue l'astina SOLO quando questo e' vero: lo scrive
    /// <see cref="CharacterAnimator"/> con lo stato di mira. Nel porto rilassato l'arma e'
    /// inclinata verso terra e il bersaglio dell'IK (solidale alla presa) ruota con lei: il
    /// polo del gomito, misurato sulla posa di mira, li' puo' flippare e portare il gomito
    /// SOPRA la canna. Fuori mira la mano sinistra resta dov'e' nella clip, che le sta gia'
    /// addosso per costruzione (il porto e' derivato da rifle_idle).
    /// </summary>
    public bool SupportActive { get; set; } = true;

    /// <summary>
    /// Quanto la canna e' ostruita, da 0 a 1. Lo legge <see cref="CharacterAnimator"/> per abbassare
    /// la mira procedurale — un'arma alzata contro un muro non sta piu' puntando il bersaglio — e in
    /// prospettiva lo puo' leggere l'host per rifiutare il colpo.
    /// </summary>
    public float MuzzleBlocked => _probe?.Blocked ?? 0f;

    private WeaponSpaceProbe? _probe;
    private Skeleton3D? _skeleton;
    private Node3D? _supportTarget;
    private Node3D? _supportElbow;
    private TwoBoneIK3D? _supportIk;
    private WeaponAnimationSet? _weapon;
    private float _supportWeight;

    // Rinculo procedurale: nessuna clip. Sono offset che si sommano alla presa e rientrano da soli.
    private float _kickBack;
    private float _kickUp;

    public override void _Ready()
    {
        _probe = new WeaponSpaceProbe(this);
        _skeleton = SkeletonLocator.Find(this);
        if (_skeleton == null)
        {
            GD.PushWarning("[WeaponGripRig] nessuno Skeleton3D sotto il rig: l'arma restera' scollegata.");
            return;
        }

        var attachment = new BoneAttachment3D { Name = "RightHandAttachment", BoneName = HandBone };
        _skeleton.AddChild(attachment);

        GripPoint = new Node3D { Name = "GripPoint" };
        attachment.AddChild(GripPoint);

        // Bersaglio della mano di supporto: figlio del punto di presa, quindi si muove CON l'arma.
        // E' esattamente cio' che serve — la mano sinistra insegue l'astina, non un punto nello spazio.
        _supportTarget = new Node3D { Name = "SupportGripTarget" };
        GripPoint.AddChild(_supportTarget);

        // Indicazione del gomito, anch'essa solidale all'arma: girando l'arma gira il piano di
        // piegatura del braccio, che e' il comportamento voluto.
        _supportElbow = new Node3D { Name = "SupportElbowHint", Position = SupportElbowHint };
        GripPoint.AddChild(_supportElbow);

        // Il modificatore si costruisce DIFFERITO, non qui.
        //
        // Costruire un TwoBoneIK3D mentre lo scheletro sta ancora entrando nell'albero blocca il
        // processo: nessun errore, nessun crash, si pianta e basta (riprodotto in headless, e'
        // costato l'unico blocco di questa fase). Un frame di ritardo lo evita, e non ha alcun
        // costo visibile: finche' l'IK non c'e', il braccio resta alla posa animata.
        CallDeferred(MethodName.BuildSupportIk);
    }

    /// <summary>
    /// Costruisce la catena IK del braccio sinistro. Separato da <c>_Ready</c> apposta: vedi la nota
    /// sul blocco in <c>_Ready</c>.
    /// </summary>
    private void BuildSupportIk()
    {
        if (_skeleton == null || _supportTarget == null || _supportElbow == null)
            return;

        _supportIk = new TwoBoneIK3D { Name = "SupportHandIk", Influence = 0f };
        _skeleton.AddChild(_supportIk);

        // Le impostazioni si dichiarano DOPO AddChild: il modificatore risolve gli indici di osso
        // contro lo scheletro padre, e prima di essere nell'albero non ne ha uno.
        _supportIk.SetSettingCount(1);
        _supportIk.SetRootBoneName(0, SupportRootBone);
        _supportIk.SetMiddleBoneName(0, SupportMidBone);
        _supportIk.SetEndBoneName(0, SupportTipBone);

        // Bersaglio e polo si dichiarano come NodePath RELATIVI al modificatore, calcolabili solo
        // quando tutti i nodi sono gia' nell'albero. TwoBoneIK3D li risolve a ogni passata.
        //
        // Il POLO non e' opzionale: senza, il modificatore non risolve la catena (misurato: la mano
        // si sposta di 3 mm e l'errore al bersaglio non cala). Con il polo l'errore va a zero.
        _supportIk.SetTargetNode(0, _supportIk.GetPathTo(_supportTarget));
        _supportIk.SetPoleNode(0, _supportIk.GetPathTo(_supportElbow));
    }

    /// <summary>
    /// Tiene la catena IK ULTIMA fra i figli dello scheletro, cioe' ultima a girare.
    ///
    /// I <see cref="SkeletonModifier3D"/> vengono eseguiti nell'ordine dei figli, e la mano di
    /// supporto e' un vincolo che si chiude sull'ARMA: dev'essere risolta dopo tutto cio' che
    /// muove il busto, o quello che viene dopo se la porta via. Misurato con
    /// <c>SupportHandIk</c> davanti a <c>SpineAim</c>: mirando, la mano restava fino a
    /// <b>36 cm</b> fuori dall'astina — l'IK risolveva, e subito dopo il rachide ruotava
    /// trascinandosi dietro il braccio sinistro. Fuori mira non si vedeva, perche' li'
    /// <c>SpineAimModifier</c> ha influenza nulla.
    ///
    /// L'ordine non si puo' fissare alla costruzione: ogni rig procedurale crea il proprio
    /// modificatore in <c>CallDeferred</c>, quindi chi arriva ultimo dipende dall'ordine dei nodi
    /// nella scena — un accoppiamento invisibile e facilissimo da rompere spostando un nodo. Qui
    /// invece la posizione si ripara da sola, e il controllo costa un confronto di indici.
    ///
    /// Durante uno scavalcamento le mani vanno sul BORDO e non sull'arma: li' non serve cedere il
    /// posto in coda, perche' <see cref="CharacterAnimator"/> azzera <see cref="SupportActive"/> e
    /// un modificatore a influenza nulla non scrive niente, qualunque sia il suo posto.
    /// </summary>
    private void EnsureModifierRunsLast()
    {
        if (_supportIk == null || _skeleton == null)
            return;

        int last = _skeleton.GetChildCount() - 1;
        if (_supportIk.GetIndex() != last)
            _skeleton.MoveChild(_supportIk, last);
    }

    /// <summary>
    /// Dichiara quale arma si impugna, o null da disarmato. E' l'UNICO punto da toccare per
    /// aggiungere un'arma: presa, rinculo e mano di supporto arrivano tutti dal
    /// <see cref="WeaponAnimationSet"/>, quindi la locomozione non viene sfiorata.
    /// </summary>
    public void ApplyWeapon(WeaponAnimationSet? weapon) => _weapon = weapon;

    /// <summary>
    /// Alza il muso di <paramref name="radians"/>, ruotando attorno a un asse GEOMETRICO invece
    /// che sommando gradi a una componente di Eulero.
    ///
    /// Sommare il beccheggio alla X della presa sembra equivalente e non lo e': l'ordine YXZ di
    /// Godot fa si' che l'effetto di quella somma cambi SEGNO a seconda della Y della presa. Con
    /// <c>GripRotationDegrees.Y = 37</c> alzava, con <c>Y = 179</c> — la presa misurata sulla
    /// posa di mira vera del fucile — abbassava, e la differenza non e' visibile leggendo il
    /// codice (skill <c>character-animation</c> §1.8: si misura la direzione, non l'angolo).
    ///
    /// Qui l'asse e' <c>canna x alto</c>: ruotare la canna attorno a esso la porta verso l'alto
    /// del mondo per costruzione, qualunque sia l'orientamento della mano e qualunque presa
    /// dichiari l'arma. Nessuna arma futura puo' piu' invertirne il segno.
    /// </summary>
    private Basis PitchMuzzleUp(Basis grip, float radians)
    {
        if (Mathf.IsZeroApprox(radians) || GripPoint?.GetParent() is not Node3D parent)
            return grip;

        // Si lavora nello spazio del PADRE (il BoneAttachment sulla mano): l'asse si calcola in
        // coordinate mondo e ci si riporta dentro, cosi' la rotazione premoltiplicata equivale a
        // una rotazione mondo attorno a quell'asse.
        Basis toWorld = parent.GlobalTransform.Basis.Orthonormalized();
        Vector3 barrel = (toWorld * grip) * Vector3.Back;
        Vector3 axis = barrel.Cross(Vector3.Up);
        if (axis.LengthSquared() < 0.000001f)
            return grip;

        return new Basis((toWorld.Inverse() * axis).Normalized(), radians) * grip;
    }

    /// Rinculo di un colpo. Chiamato su ogni peer, come il calcio della camera.
    public void PlayRecoil()
    {
        if (_weapon == null)
            return;

        _kickBack = _weapon.RecoilKickBack;
        _kickUp = Mathf.DegToRad(_weapon.RecoilKickUpDegrees);
    }

    public override void _Process(double delta)
    {
        if (GripPoint == null)
            return;

        EnsureModifierRunsLast();

        float dt = (float)delta;

        // Rientro esponenziale del rinculo: indipendente dal frame rate, come lo smorzamento di
        // CharacterAnimator.
        if (_weapon != null)
        {
            float recovery = 1f - Mathf.Exp(-_weapon.RecoilRecoverySpeed * dt);
            _kickBack = Mathf.Lerp(_kickBack, 0f, recovery);
            _kickUp = Mathf.Lerp(_kickUp, 0f, recovery);
        }

        // Spazio davanti alla canna. Si misura PRIMA di scrivere la presa, usando la trasformata del
        // frame precedente: un frame di ritardo su una reazione smorzata non si vede, e misurare
        // dopo aver gia' alzato l'arma vorrebbe dire misurare la propria reazione.
        if (_weapon != null)
            _probe?.Update(GripPoint, MuzzleReach, MuzzleClearance, PortArmsSpeed, dt);

        // Presa: offset dichiarato dall'arma, piu' il rinculo, piu' il "port arms". L'arma punta
        // verso +Z locale, quindi rinculo e ritrazione arretrano lungo -Z.
        Vector3 grip = _weapon?.GripOffset ?? Vector3.Zero;
        Vector3 gripRotation = _weapon?.GripRotationDegrees ?? Vector3.Zero;
        float blocked = MuzzleBlocked;

        var basis = Basis.FromEuler(new Vector3(
            Mathf.DegToRad(gripRotation.X),
            Mathf.DegToRad(gripRotation.Y),
            Mathf.DegToRad(gripRotation.Z)));

        basis = PitchMuzzleUp(basis, _kickUp + Mathf.DegToRad(PortArmsPitchDegrees) * blocked);

        GripPoint.Transform = new Transform3D(
            basis, grip - new Vector3(0f, 0f, _kickBack + PortArmsPullBack * blocked));

        // Mano di supporto: solo per le armi a due mani, e con una transizione — accenderla di colpo
        // farebbe scattare il braccio sinistro nel frame in cui si cambia arma.
        if (_supportIk == null || _supportTarget == null)
            return;

        bool twoHanded = EnableSupportHandIk && SupportActive && _weapon is { IsTwoHanded: true };
        _supportTarget.Position = _weapon?.SupportGripOffset ?? Vector3.Zero;
        if (_supportElbow != null)
            _supportElbow.Position = SupportElbowHint;

        _supportWeight = Mathf.Lerp(_supportWeight, twoHanded ? 1f : 0f, 1f - Mathf.Exp(-SupportBlendSpeed * dt));
        _supportIk.Influence = _supportWeight;
    }
}
