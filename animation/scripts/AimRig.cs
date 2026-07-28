using Godot;

namespace Lagoon;

/// <summary>
/// Costruisce e pilota il <see cref="SpineAimModifier"/>: e' l'adattatore fra lo stato che riceve
/// <see cref="CharacterAnimator"/> e il modificatore che tocca le ossa.
///
/// Come <see cref="WeaponGripRig"/>, i nodi si creano DA CODICE e non nella scena: il rig arriva da
/// <c>Body_Base.glb</c>, che si rigenera, e figli aggiunti a mano dentro una scena istanziata da un
/// <c>.glb</c> si perdono o si sdoppiano al reimport. Costruirli qui li lega ai NOMI dei bone.
///
/// Pura resa: nessuna autorita', nessuna validazione, nessuno stato da replicare (CLAUDE.md §3).
/// </summary>
public partial class AimRig : Node3D
{
    /// Velocita' con cui la correzione di mira entra ed esce, in frazione al secondo.
    [Export] public float BlendSpeed { get; set; } = 10.0f;

    /// <summary>
    /// Quanto il lean procedurale reagisce all'accelerazione laterale, in radianti per (m/s^2).
    /// Copre curve, partenze e arresti senza spendere una clip: e' il tipo di movimento che
    /// un'animazione fissa non puo' avere, perche' dipende da come si sta guidando il personaggio.
    /// </summary>
    [Export] public float LeanPerLateralAcceleration { get; set; } = 0.010f;

    /// Inclinazione laterale massima, in gradi.
    [Export] public float MaxLeanDegrees { get; set; } = 12.0f;

    /// Velocita' di rientro del lean, in frazione al secondo.
    [Export] public float LeanSmoothing { get; set; } = 8.0f;

    // ====================================================================================
    //  Stato in ingresso: lo scrive CharacterAnimator ogni frame
    // ====================================================================================

    /// Direzione di mira in coordinate mondo. Vettore nullo = non si sta mirando.
    public Vector3 AimDirection { get; set; }

    /// Quanto la mira deve avere effetto: 0 da disarmato o in aria, 1 con l'arma in pugno.
    public float Weight { get; set; }

    /// Velocita' locale dell'avatar (X = destra, Y = avanti), da cui si ricava il lean.
    public Vector2 LocalVelocity { get; set; }

    private SpineAimModifier? _modifier;
    private Skeleton3D? _skeleton;
    private float _weight;
    private float _lean;
    private float _lastLateralSpeed;

    public override void _Ready()
    {
        _skeleton = SkeletonLocator.Find(this);
        if (_skeleton == null)
        {
            GD.PushWarning("[AimRig] nessuno Skeleton3D sotto il rig: la mira procedurale resta spenta.");
            return;
        }

        // Differito per lo stesso motivo di WeaponGripRig: costruire un modificatore mentre lo
        // scheletro sta ancora entrando nell'albero blocca il processo, in silenzio.
        CallDeferred(MethodName.BuildModifier);
    }

    private void BuildModifier()
    {
        if (_skeleton == null)
            return;

        _modifier = new SpineAimModifier { Name = "SpineAim", Influence = 0f };
        _skeleton.AddChild(_modifier);
    }

    public override void _Process(double delta)
    {
        if (_modifier == null)
            return;

        float dt = (float)delta;

        _weight = Mathf.Lerp(_weight, Weight, Damp(BlendSpeed, dt));
        _modifier.Influence = _weight;
        _modifier.AimDirection = AimDirection;
        _modifier.Lean = UpdateLean(dt);
    }

    /// <summary>
    /// Lean procedurale: il busto si inclina CONTRO l'accelerazione laterale, come chi curva in
    /// corsa. Si deriva l'accelerazione dalla velocita' laterale invece di prenderla dalla fisica
    /// perche' cosi' vale identica sugli avatar remoti, dove la velocita' e' replicata e la fisica no.
    /// </summary>
    private float UpdateLean(float dt)
    {
        float lateral = LocalVelocity.X;
        float acceleration = dt > 0.0001f ? (lateral - _lastLateralSpeed) / dt : 0f;
        _lastLateralSpeed = lateral;

        float max = Mathf.DegToRad(MaxLeanDegrees);
        float target = Mathf.Clamp(-acceleration * LeanPerLateralAcceleration, -max, max);

        _lean = Mathf.Lerp(_lean, target * _weight, Damp(LeanSmoothing, dt));
        return _lean;
    }

    private static float Damp(float speed, float dt) => 1.0f - Mathf.Exp(-speed * dt);
}
