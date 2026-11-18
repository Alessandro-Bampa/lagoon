using Godot;

namespace Lagoon;

/// <summary>
/// Un fuoribordo montato: il modello (<see cref="OutboardDefinition.Model"/>) e le sue parti mobili.
/// Lo crea <see cref="BoatAssembly"/> sull'attacco <c>Mount_Motor</c> dello scafo.
///
/// Due ruoli separati:
/// <list type="bullet">
/// <item><b>geometria per la fisica</b> (host): punto e asse della spinta per uno sterzo e un trim
/// dati, calcolati dalle posizioni A RIPOSO dei nodi del modello, nello spazio della barca. Non si
/// leggono i nodi animati: la fisica non deve dipendere dall'ordine dei frame;</item>
/// <item><b>resa</b> (ogni peer): <see cref="ApplyPose"/> ruota il motore sul piantone, lo inclina
/// col trim e fa girare l'elica dallo stato REPLICATO.</item>
/// </list>
///
/// Convenzioni (skill vehicles-boats §11): sterzo positivo = rotazione attorno a +Y = spinta verso
/// sinistra, poppa a sinistra, prua a DRITTA; la barra (che sporge in avanti) va dalla parte opposta.
/// Trim positivo (trim out) = rotazione di −trim attorno a +X sul perno dello specchio (l'origine del
/// modello): il piede si allontana dallo specchio e la spinta punta un po' in basso → prua su.
/// </summary>
[Tool]
public partial class OutboardMotor : Node3D
{
    public OutboardDefinition Definition { get; private set; } = null!;

    /// Mano del pilota sulla barra (null se il modello non ha la barra o la barca ha il volante).
    public Node3D? TillerGrip { get; private set; }

    private Node3D? _swivel;
    private Node3D? _propeller;
    private Node3D? _tiller;
    private Basis _swivelRest = Basis.Identity;
    private Basis _propRest = Basis.Identity;
    private Transform3D _mount = Transform3D.Identity;
    private float _propAngle;

    // Nello spazio del MOTORE (origine = perno del trim), a sterzo e trim zero.
    private Vector3 _pivot;
    private Vector3 _thrustFromPivot;

    /// Monta il modello. <paramref name="mountInBoat"/> = attacco nello spazio della barca.
    public void Setup(OutboardDefinition definition, Node3D model, Transform3D mountInBoat, bool showTiller)
    {
        Definition = definition;
        AddChild(model);
        _mount = mountInBoat;
        Transform = mountInBoat;

        _swivel = model.FindChild("Swivel", true, false) as Node3D;
        _propeller = model.FindChild("Propeller", true, false) as Node3D;
        _tiller = model.FindChild("Tiller", true, false) as Node3D;
        if (_swivel == null || _propeller == null)
            GD.PushWarning($"[OutboardMotor] '{definition.ResourcePath}': mancano Swivel/Propeller, il motore restera' fermo.");

        _swivelRest = _swivel?.Basis ?? Basis.Identity;
        _propRest = _propeller?.Basis ?? Basis.Identity;
        if (_tiller != null)
            _tiller.Visible = showTiller;
        TillerGrip = showTiller ? model.FindChild("TillerGrip", true, false) as Node3D : null;

        // Si compone a mano per non dipendere da GlobalTransform prima del frame.
        Transform3D toMotor = model.Transform;
        _pivot = _swivel != null ? (toMotor * PathTransform(model, _swivel)).Origin : Vector3.Zero;
        Node3D? thrust = model.FindChild("Thrust", true, false) as Node3D;
        Vector3 thrustPoint = thrust != null ? (toMotor * PathTransform(model, thrust)).Origin : _pivot;
        _thrustFromPivot = thrustPoint - _pivot;
    }

    /// Trasformata di <paramref name="node"/> rispetto a <paramref name="root"/>, componendo i Transform.
    private static Transform3D PathTransform(Node3D root, Node3D node)
    {
        Transform3D t = Transform3D.Identity;
        for (Node? n = node; n != null && n != root; n = n.GetParent())
        {
            if (n is Node3D n3)
                t = n3.Transform * t;
        }
        return t;
    }

    private static Basis TrimBasis(float trim) => new(Vector3.Right, -trim);

    /// Orientamento del gambo (spazio della barca): X = normale del piede, Y = gambo, −Z = spinta.
    public Basis MotorBasis(float steer, float trim) => _mount.Basis * TrimBasis(trim) * new Basis(Vector3.Up, steer);

    /// Centro dell'elica (spazio della barca) con sterzo e trim dati (rad).
    public Vector3 PropellerPoint(float steer, float trim)
        => _mount * (TrimBasis(trim) * (_pivot + _thrustFromPivot.Rotated(Vector3.Up, steer)));

    /// Verso della spinta in marcia avanti (spazio della barca).
    public Vector3 ThrustAxis(float steer, float trim) => MotorBasis(steer, trim) * Vector3.Forward;

    /// <summary>
    /// Resa dallo stato replicato: motore girato di <paramref name="steer"/> e inclinato di
    /// <paramref name="trim"/>, elica ai giri <paramref name="propRpm"/> (con segno; limitati a quelli
    /// disegnabili).
    /// </summary>
    public void ApplyPose(float steer, float trim, float propRpm, double delta)
    {
        Transform = new Transform3D(_mount.Basis * TrimBasis(trim), _mount.Origin);
        if (_swivel != null)
            _swivel.Basis = new Basis(Vector3.Up, steer) * _swivelRest;

        if (_propeller == null)
            return;
        float rpm = Mathf.Clamp(propRpm, -Definition.MaxPropRpm, Definition.MaxPropRpm);
        _propAngle = Mathf.Wrap(_propAngle + rpm / 60f * Mathf.Tau * (float)delta, 0f, Mathf.Tau);
        // L'asse dell'elica e' il suo Z locale (Y di Blender, l'albero motore).
        _propeller.Basis = _propRest * new Basis(Vector3.Back, _propAngle);
    }
}
