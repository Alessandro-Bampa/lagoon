using Godot;

namespace Lagoon;

/// <summary>
/// Camera in prima persona dell'avatar LOCALE. Possiede lo sguardo (imbardata e inclinazione, dal
/// mouse), il rinculo della visuale e il viewmodel (<see cref="ViewmodelAnimator"/>, suo figlio).
///
/// E' la fonte di verita' della direzione di vista: <c>PlayerController</c> ne legge
/// <see cref="LookYaw"/>/<see cref="LookPitch"/> per muoversi, orientare il corpo e pubblicare
/// <c>SyncAimYaw</c>/<c>SyncAimPitch</c>; la mira e' il centro dello schermo (<c>AimResolver</c>).
///
/// Sta sul punto di vista NOMINALE del personaggio (<c>CharacterMotor.EyeHeight</c> /
/// <c>CrouchEyeHeight</c> sopra i piedi), non sulla testa animata: e' esattamente il punto da cui
/// l'host fa partire i colpi (<c>ResolvedEyePosition</c>), quindi cio' che sta al centro dello
/// schermo e' cio' che viene colpito. Il dondolio del passo lo danno le clip del viewmodel.
///
/// Lo sguardo si applica solo a mouse CATTURATO. Chi cattura e chi rilascia e' <c>PlayerHud</c>:
/// qui non si decide mai se il cursore appartiene alla UI.
///
/// Pura resa locale (CLAUDE.md §3): nessuna RPC, nessuno stato di gioco. Sugli avatar remoti il
/// nodo esiste ma resta spento, e il viewmodel non viene mai creato.
/// </summary>
public partial class FirstPersonCamera : Camera3D
{
    /// Rotazione per pixel di mouse a sensibilita' 1, in radianti.
    private const float RadiansPerPixel = 0.0022f;

    [Export] public float FovDegrees { get; set; } = 75.0f;
    [Export] public float FovBlendSpeed { get; set; } = 14.0f;
    [Export] public float MaxPitchDegrees { get; set; } = 85.0f;

    /// Velocita' con cui il punto di vista scende e risale accovacciandosi, in frazione al secondo.
    [Export] public float CrouchBlendSpeed { get; set; } = 10.0f;

    /// Abbassamento massimo della visuale a un atterraggio duro, in metri.
    [Export] public float LandingDip { get; set; } = 0.14f;

    /// Durante scavalcamento e arrampicata: abbassamento (m), inclinazione in avanti e rollio (gradi).
    [Export] public float VaultDip { get; set; } = 0.22f;

    [Export] public float VaultPitchDegrees { get; set; } = 12.0f;
    [Export] public float VaultRollDegrees { get; set; } = 4.0f;

    /// Di quanto arretrano le proprie gambe viste in prima persona, in metri (vedi FirstPersonBodyView).
    [Export] public float BodyBackOffset { get; set; } = 0.12f;

    /// <summary>
    /// Imbardata dello sguardo in radianti, coordinate mondo, stessa convenzione di
    /// <c>CharacterMotor.SyncFacing</c>: zero guarda +Z, positiva verso +X.
    /// </summary>
    public float LookYaw { get; private set; }

    /// Inclinazione dello sguardo in radianti: positiva verso l'alto, zero all'orizzonte.
    public float LookPitch { get; private set; }

    /// Mira attiva: stringe il campo visivo su <see cref="AimFovDegrees"/>. La scrive <c>PlayerController</c>.
    public bool Zoomed { get; set; }

    /// Campo visivo in mira, dichiarato dall'arma impugnata.
    public float AimFovDegrees { get; set; } = 55.0f;

    /// Il viewmodel, o null sugli avatar remoti.
    public ViewmodelAnimator? Viewmodel { get; private set; }

    private SettingsService _settings = null!;
    private CharacterMotor _body = null!;

    /// <summary>
    /// Occhi da seduti al governo di una barca (sopra i piedi). La testa nelle pose sedute di UAL sta a
    /// ~1,2 m; piu' in basso la camera finirebbe fra le spalle, con le braccia davanti agli occhi.
    /// </summary>
    [Export] public float SeatedEyeHeight { get; set; } = 1.20f;
    private float _seated;

    private float _crouch;
    private float _dip;

    /// Quanto si e' dentro uno scavalcamento, da 0 a 1: la visuale si protende e si abbassa.
    private float _vault;

    // Rinculo della visuale: x = inclinazione, y = imbardata, z = rollio, in radianti. Lo sparo
    // sposta il BERSAGLIO; la visuale lo insegue di scatto e il bersaglio rientra piu' piano.
    private Vector3 _recoilTarget;
    private Vector3 _recoil;
    private float _recoilSnap = 30f;
    private float _recoilReturn = 7f;

    public override void _Ready()
    {
        Viewmodel = GetNodeOrNull<ViewmodelAnimator>("Viewmodel");

        // Stesso criterio di tutta la presentazione locale: l'autorita' della root, gia' impostata
        // nel suo _EnterTree. Decide QUI chi e' la camera attiva, senza dipendere da altri nodi.
        bool isLocal = GetParent().IsMultiplayerAuthority();
        Current = isLocal;
        if (!isLocal)
        {
            Viewmodel?.QueueFree();
            Viewmodel = null;
            SetProcess(false);
            SetProcessInput(false);
            return;
        }

        _settings = GetNode<SettingsService>("/root/SettingsService");
        _body = GetParent<CharacterMotor>();

        Fov = FovDegrees;
        Position = Vector3.Up * EyeLocalHeight();

        // Differito: lo scheletro del rig viene risolto nel suo _Ready, e l'ordine fra i due
        // _Ready non e' un appiglio.
        CallDeferred(MethodName.BuildBodyView);
    }

    private void BuildBodyView()
    {
        var rig = GetNodeOrNull<CharacterAnimator>("../Visual/CharacterRig");
        if (rig?.Skeleton != null)
            FirstPersonBodyView.Build(rig, rig.Skeleton, BodyBackOffset);
    }

    /// _Input e non _UnhandledInput: a mouse catturato non c'e' UI che debba vedere il movimento.
    public override void _Input(InputEvent @event)
    {
        // Non corrente = la visuale e' di un'altra camera (terza persona al timone, BoatChaseCamera),
        // che usa il mouse per conto suo.
        if (@event is not InputEventMouseMotion motion
            || Input.MouseMode != Input.MouseModeEnum.Captured
            || !Current)
            return;

        // ScreenRelative e non Relative: quest'ultimo passa dalla scala UI (ContentScaleFactor), e
        // la sensibilita' cambierebbe muovendo uno slider che non c'entra nulla. Il rapporto dei
        // campi visivi tiene costante la velocita' percepita quando si stringe la visuale.
        float scale = RadiansPerPixel * _settings.MouseSensitivity * (Fov / FovDegrees);
        float maxPitch = Mathf.DegToRad(MaxPitchDegrees);

        var delta = new Vector2(-motion.ScreenRelative.X * scale, -motion.ScreenRelative.Y * scale);
        LookYaw = Mathf.Wrap(LookYaw + delta.X, -Mathf.Pi, Mathf.Pi);
        LookPitch = Mathf.Clamp(LookPitch + delta.Y, -maxPitch, maxPitch);

        Viewmodel?.AddLookDelta(delta);
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;

        Fov = Mathf.Lerp(Fov, Zoomed ? AimFovDegrees : FovDegrees, Damp(FovBlendSpeed, dt));

        _crouch = Mathf.Lerp(_crouch, _body.SyncCrouching ? 1f : 0f, Damp(CrouchBlendSpeed, dt));
        bool seated = _body is PlayerController { Mode: PlayerMode.Driving, DrivingBoat.Assembly: not null };
        _seated = Mathf.Lerp(_seated, seated ? 1f : 0f, Damp(CrouchBlendSpeed, dt));
        _dip = Mathf.Lerp(_dip, 0f, Damp(7f, dt));

        // Scavalcando ci si raccoglie e ci si protende sopra l'ostacolo: senza viewmodel (l'arma e'
        // fuori vista) e' il movimento della visuale a far sentire il gesto.
        _vault = Mathf.Lerp(_vault, _body.Vaulting ? 1f : 0f, Damp(7f, dt));
        Position = Vector3.Up * (EyeLocalHeight() - _dip - VaultDip * _vault);

        _recoilTarget = _recoilTarget.Lerp(Vector3.Zero, Damp(_recoilReturn, dt));
        _recoil = _recoil.Lerp(_recoilTarget, Damp(_recoilSnap, dt));

        // Camera3D guarda lungo -Z, lo sguardo a imbardata zero guarda +Z: da qui il mezzo giro.
        float limit = Mathf.DegToRad(89f);
        var basis = Basis.FromEuler(new Vector3(
            Mathf.Clamp(LookPitch + _recoil.X - Mathf.DegToRad(VaultPitchDegrees) * _vault, -limit, limit),
            LookYaw + Mathf.Pi + _recoil.Y,
            _recoil.Z + Mathf.DegToRad(VaultRollDegrees) * _vault));

        // Scossone autoriale del viewmodel (ricarica, estrazione): gia' nel riferimento della camera.
        if (Viewmodel != null)
            basis *= new Basis(Viewmodel.CameraShake);

        GlobalBasis = basis;
    }

    /// Quota degli occhi rispetto all'origine del corpo, che sta a meta' capsula.
    private float EyeLocalHeight()
    {
        float height = Mathf.Lerp(_body.EyeHeight, _body.CrouchEyeHeight, _crouch);
        height = Mathf.Lerp(height, SeatedEyeHeight, _seated);
        return height - _body.StandHeight * 0.5f;
    }

    /// Ruota lo sguardo di un angolo: lo usa chi trasporta l'avatar (ponte di una barca in virata).
    public void AddYaw(float radians) => LookYaw = Mathf.Wrap(LookYaw + radians, -Mathf.Pi, Mathf.Pi);

    /// <summary>
    /// Impone lo sguardo: lo usa la visuale in terza persona al timone (<c>BoatChaseCamera</c>), che
    /// possiede il mouse mentre e' attiva e fa guardare il pilota dove guarda lei.
    /// </summary>
    public void SetLook(float yaw, float pitch)
    {
        float maxPitch = Mathf.DegToRad(MaxPitchDegrees);
        LookYaw = Mathf.Wrap(yaw, -Mathf.Pi, Mathf.Pi);
        LookPitch = Mathf.Clamp(pitch, -maxPitch, maxPitch);
    }

    /// Tiene lo sguardo entro <paramref name="maxDegrees"/> da <paramref name="centerYaw"/>.
    public void ClampYawAround(float centerYaw, float maxDegrees)
    {
        float max = Mathf.DegToRad(maxDegrees);
        float offset = Mathf.Clamp(Mathf.AngleDifference(centerYaw, LookYaw), -max, max);
        LookYaw = Mathf.Wrap(centerYaw + offset, -Mathf.Pi, Mathf.Pi);
    }

    /// <summary>
    /// Rinculo di un colpo, in gradi: la visuale sale (<paramref name="pitch"/>), scarta di lato
    /// (<paramref name="yaw"/>) e si inclina (<paramref name="roll"/>), poi rientra da sola.
    ///
    /// Sposta davvero il centro dello schermo, cioe' il punto mirato del colpo successivo: e' il
    /// rinculo che il giocatore compensa trascinando il mouse. Sparando a raffica i colpi si
    /// sommano piu' in fretta di quanto rientrino, e la visuale sale. Resta locale: l'host riceve
    /// solo il punto mirato, e la dispersione la tira lui.
    /// </summary>
    /// <param name="snappiness">Prontezza con cui la visuale raggiunge il rinculo, in 1/s.</param>
    /// <param name="returnSpeed">Velocita' di rientro, in 1/s.</param>
    public void AddRecoil(float pitch, float yaw, float roll, float snappiness, float returnSpeed)
    {
        _recoilTarget += new Vector3(Mathf.DegToRad(pitch), Mathf.DegToRad(yaw), Mathf.DegToRad(roll));
        _recoilSnap = Mathf.Max(snappiness, 1f);
        _recoilReturn = Mathf.Max(returnSpeed, 0.1f);
    }

    /// Atterraggio: la visuale si abbassa in proporzione all'impatto e risale.
    public void AddLandingDip(float impactSpeed, float hardLandingSpeed)
    {
        float hardness = Mathf.Clamp(impactSpeed / Mathf.Max(hardLandingSpeed, 0.001f), 0f, 1f);
        _dip = Mathf.Max(_dip, hardness * LandingDip);
    }

    private static float Damp(float speed, float dt) => 1.0f - Mathf.Exp(-speed * dt);
}
