using Godot;

namespace Lagoon;

/// <summary>
/// Camera in prima persona dell'avatar LOCALE. Possiede lo sguardo (imbardata e inclinazione, dal
/// mouse) e si colloca sugli occhi del rig seguendo l'osso della testa: il corpo che si vede
/// guardando in basso e' quello vero, animato, lo stesso che vedono gli altri.
///
/// E' la fonte di verita' della direzione di vista: <c>PlayerController</c> ne legge
/// <see cref="LookYaw"/>/<see cref="LookPitch"/> per muoversi, orientare il corpo e pubblicare
/// <c>SyncAimYaw</c>/<c>SyncAimPitch</c>; la mira e' il centro dello schermo (<c>AimResolver</c>).
///
/// Lo sguardo si applica solo a mouse CATTURATO. Chi cattura e chi rilascia e' <c>PlayerHud</c>:
/// qui non si decide mai se il cursore appartiene alla UI.
///
/// Pura resa locale (CLAUDE.md §3): nessuna RPC, nessuno stato di gioco. Sugli avatar remoti il
/// nodo esiste ma resta spento.
/// </summary>
public partial class FirstPersonCamera : Camera3D
{
    /// Rotazione per pixel di mouse a sensibilita' 1, in radianti.
    private const float RadiansPerPixel = 0.0022f;

    [Export] public float FovDegrees { get; set; } = 75.0f;

    /// Campo visivo mentre si mira (azione <c>aim</c> con un'arma in mano).
    [Export] public float AimFovDegrees { get; set; } = 55.0f;

    [Export] public float FovBlendSpeed { get; set; } = 12.0f;
    [Export] public float MaxPitchDegrees { get; set; } = 85.0f;

    [Export] public string HeadBone { get; set; } = "Head";

    /// <summary>
    /// Dagli occhi all'origine dell'osso della testa (la base del cranio), nel riferimento dello
    /// SGUARDO: Y in alto, Z in avanti. Ruota con l'imbardata, non con l'osso, cosi' il dondolio
    /// della testa nelle clip sposta la camera ma non la inclina.
    /// </summary>
    [Export] public Vector3 EyeOffset { get; set; } = new(0f, 0.08f, 0.1f);

    /// Quota degli occhi sopra l'origine del corpo quando il rig non ha scheletro.
    [Export] public float FallbackEyeHeight { get; set; } = 0.64f;

    /// <summary>
    /// Reattivita' con cui la camera insegue la testa, in frazione al secondo. E' un passa-basso sul
    /// dondolio delle clip di locomozione: piu' basso = visuale piu' stabile ma piu' "galleggiante".
    /// </summary>
    [Export] public float HeadFollowSpeed { get; set; } = 30.0f;

    /// Velocita' con cui il calcio da rinculo si riassorbe, in frazione al secondo.
    [Export] public float KickRecoverySpeed { get; set; } = 10.0f;

    /// <summary>
    /// Imbardata dello sguardo in radianti, coordinate mondo, stessa convenzione di
    /// <c>CharacterMotor.SyncFacing</c>: zero guarda +Z, positiva verso +X.
    /// </summary>
    public float LookYaw { get; private set; }

    /// Inclinazione dello sguardo in radianti: positiva verso l'alto, zero all'orizzonte.
    public float LookPitch { get; private set; }

    /// Mira attiva: stringe il campo visivo. La scrive <c>PlayerController</c>.
    public bool Zoomed { get; set; }

    private SettingsService _settings = null!;
    private Node3D _body = null!;
    private Skeleton3D? _skeleton;
    private int _headBone = -1;

    private Vector3 _eyeLocal;
    private bool _hasEye;

    /// Calcio da rinculo: X = imbardata, Y = inclinazione, in radianti.
    private Vector2 _kick;
    private readonly RandomNumberGenerator _rng = new();

    public override void _Ready()
    {
        // Stesso criterio di tutta la presentazione locale: l'autorita' della root, gia' impostata
        // nel suo _EnterTree. Decide QUI chi e' la camera attiva, senza dipendere da altri nodi.
        bool isLocal = GetParent().IsMultiplayerAuthority();
        Current = isLocal;
        if (!isLocal)
        {
            SetProcess(false);
            SetProcessInput(false);
            return;
        }

        _settings = GetNode<SettingsService>("/root/SettingsService");
        _body = GetParent<Node3D>();
        _rng.Randomize();

        Fov = FovDegrees;
        Position = Vector3.Up * FallbackEyeHeight;

        _skeleton = SkeletonLocator.Find(GetNode("../Visual/CharacterRig"));
        _headBone = _skeleton?.FindBone(HeadBone) ?? -1;
        if (_skeleton == null || _headBone < 0)
        {
            GD.PushWarning($"[FirstPersonCamera] osso '{HeadBone}' non trovato: la camera resta a quota fissa.");
            return;
        }

        // La posa FINALE (dopo IK e modificatori) e' leggibile solo dentro questo segnale: Godot
        // ripristina le pose sorgente subito dopo.
        _skeleton.SkeletonUpdated += OnSkeletonUpdated;

        // Differito: la copia visibile eredita il materiale della mesh, che PlayerNetworkSync
        // assegna nel proprio _Ready, e l'ordine fra i due _Ready non e' un appiglio.
        CallDeferred(MethodName.HideOwnHead);
    }

    /// <summary>
    /// Nasconde la propria testa, solo su questo peer e solo alla vista: l'ombra resta intera
    /// (<see cref="FirstPersonBodyView"/>). Senza, guardando in basso si vedrebbero mento e collo
    /// dall'interno.
    /// </summary>
    private void HideOwnHead()
        => FirstPersonBodyView.HideBone(GetNode("../Visual/CharacterRig"), _skeleton!, _headBone);

    /// _Input e non _UnhandledInput: a mouse catturato non c'e' UI che debba vedere il movimento.
    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventMouseMotion motion
            || Input.MouseMode != Input.MouseModeEnum.Captured)
            return;

        // ScreenRelative e non Relative: quest'ultimo passa dalla scala UI (ContentScaleFactor), e
        // la sensibilita' cambierebbe muovendo uno slider che non c'entra nulla. Il rapporto dei
        // campi visivi tiene costante la velocita' percepita quando si stringe la visuale.
        float scale = RadiansPerPixel * _settings.MouseSensitivity * (Fov / FovDegrees);
        float maxPitch = Mathf.DegToRad(MaxPitchDegrees);

        LookYaw = Mathf.Wrap(LookYaw - motion.ScreenRelative.X * scale, -Mathf.Pi, Mathf.Pi);
        LookPitch = Mathf.Clamp(LookPitch - motion.ScreenRelative.Y * scale, -maxPitch, maxPitch);
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;

        Fov = Mathf.Lerp(Fov, Zoomed ? AimFovDegrees : FovDegrees, Damp(FovBlendSpeed, dt));
        _kick = _kick.Lerp(Vector2.Zero, Damp(KickRecoverySpeed, dt));

        // Camera3D guarda lungo -Z, lo sguardo a imbardata zero guarda +Z: da qui il mezzo giro.
        GlobalRotation = new Vector3(LookPitch + _kick.Y, LookYaw + Mathf.Pi + _kick.X, 0f);
    }

    private void OnSkeletonUpdated()
    {
        Vector3 head = _skeleton!.GlobalTransform * _skeleton.GetBoneGlobalPose(_headBone).Origin;
        Vector3 target = head + new Basis(Vector3.Up, LookYaw) * EyeOffset - _body.GlobalPosition;

        // Si smorza la posizione RELATIVA al corpo, non quella in mondo: cosi' il movimento
        // dell'avatar non accumula ritardo e si filtra solo il dondolio dell'animazione.
        _eyeLocal = _hasEye
            ? _eyeLocal.Lerp(target, Damp(HeadFollowSpeed, (float)GetProcessDeltaTime()))
            : target;
        _hasEye = true;

        Position = _eyeLocal;
    }

    /// Ruota lo sguardo di un angolo: lo usa chi trasporta l'avatar (ponte di una barca in virata).
    public void AddYaw(float radians) => LookYaw = Mathf.Wrap(LookYaw + radians, -Mathf.Pi, Mathf.Pi);

    /// Tiene lo sguardo entro <paramref name="maxDegrees"/> da <paramref name="centerYaw"/>.
    public void ClampYawAround(float centerYaw, float maxDegrees)
    {
        float max = Mathf.DegToRad(maxDegrees);
        float offset = Mathf.Clamp(Mathf.AngleDifference(centerYaw, LookYaw), -max, max);
        LookYaw = Mathf.Wrap(centerYaw + offset, -Mathf.Pi, Mathf.Pi);
    }

    /// <summary>
    /// Calcio da rinculo, in gradi: la visuale sale e scarta di lato, poi rientra. Solo locale.
    /// Sposta davvero il centro dello schermo, quindi anche il punto mirato finche' non rientra:
    /// e' il rinculo che il giocatore deve compensare, mentre la dispersione resta dell'host.
    /// </summary>
    public void AddKick(float degrees)
    {
        if (degrees <= 0f)
            return;

        float radians = Mathf.DegToRad(degrees);
        _kick += new Vector2(_rng.RandfRange(-0.4f, 0.4f) * radians, radians);
    }

    private static float Damp(float speed, float dt) => 1.0f - Mathf.Exp(-speed * dt);
}
