using Godot;

namespace Lagoon;

/// <summary>
/// Motore + invertitore in esecuzione (solo host): regime, farfalla, marcia innestata. Il regime
/// nasce dall'equilibrio fra la coppia del motore e quella assorbita dall'elica, integrata a
/// sotto-passi da <see cref="PropulsionUnit"/>: a punto fisso il motore non arriva ai giri nominali,
/// con l'elica fuori dall'acqua schizza al limitatore.
///
/// Due logiche di leva (skill vehicles-boats §11): benzina = farfalla diretta (piu' un piccolo
/// regolatore del minimo); diesel = la leva chiede un regime e un PI lo tiene sotto carico.
/// </summary>
public sealed class MarineEngine
{
    public readonly EngineDefinition Engine;
    public readonly GearboxDefinition Gearbox;

    /// Velocita' angolare del motore (rad/s).
    public float Omega { get; private set; }
    /// Marcia innestata: +1 avanti, 0 folle, −1 indietro.
    public int Gear { get; private set; }
    /// Farfalla effettiva 0..1 (dopo filtro, regolatori, limitatore).
    public float Throttle { get; private set; }
    public bool Running { get; private set; }
    /// Il limitatore sta tagliando.
    public bool Limiting { get; private set; }

    public float Rpm => Omega * 60f / Mathf.Tau;
    public float Ratio => Gear >= 0 ? Gearbox.RatioForward : Gearbox.RatioReverse;

    private int _requestedGear;
    private float _demand;
    private float _neutralTime;
    private int _lastEngagedGear;
    private float _governorIntegral;
    private float _idleIntegral;
    private float _targetThrottle;

    public MarineEngine(EngineDefinition engine, GearboxDefinition gearbox)
    {
        Engine = engine;
        Gearbox = gearbox;
    }

    /// <summary>
    /// Leva unica −1..1: ±10% di corsa = folle, poi marcia innestata al minimo, poi farfalla. Il
    /// cambio passa sempre dal folle, aspetta <see cref="GearboxDefinition.ShiftSeconds"/> (piu' la
    /// permanenza se si inverte) e un regime sotto <see cref="GearboxDefinition.MaxShiftRpm"/>.
    /// </summary>
    public void Command(float lever, bool running, float dt)
    {
        if (running && !Running && Omega < IdleOmega * 0.5f)
            Omega = IdleOmega; // avviamento
        Running = running;

        float a = Mathf.Abs(lever);
        _requestedGear = !running || a < 0.1f ? 0 : (lever > 0f ? 1 : -1);
        _demand = _requestedGear == 0 ? 0f : Mathf.Clamp((a - 0.1f) / 0.9f, 0f, 1f);

        if (_requestedGear != Gear)
        {
            if (Gear != 0)
            {
                _lastEngagedGear = Gear;
                Gear = 0;
                _neutralTime = 0f;
            }
            else
            {
                _neutralTime += dt;
                float wait = Gearbox.ShiftSeconds;
                if (_lastEngagedGear != 0 && _lastEngagedGear != _requestedGear)
                    wait += Gearbox.NeutralDwellSeconds;
                if (_requestedGear != 0 && _neutralTime >= wait && Rpm <= Gearbox.MaxShiftRpm)
                    Gear = _requestedGear;
            }
            // Mentre si cambia la farfalla resta al minimo.
            _demand = 0f;
        }
        else if (Gear == 0)
        {
            _neutralTime += dt;
        }
    }

    private float IdleOmega => Engine.IdleRpm * Mathf.Tau / 60f;
    private float LimiterOmega => Engine.LimiterRpm * Mathf.Tau / 60f;

    /// <summary>
    /// Farfalla del passo (una volta per passo fisico, prima dei sotto-passi): filtro del primo
    /// ordine, regolatori, limitatore con isteresi del 2%.
    /// </summary>
    public void UpdateThrottle(float dt)
    {
        if (!Running)
        {
            _targetThrottle = 0f;
            Throttle = 0f;
            return;
        }

        float rated = Engine.RatedOmega;
        float target;
        if (Engine.Type == EngineType.Diesel)
        {
            float wanted = IdleOmega + _demand * (rated - IdleOmega);
            float err = (wanted - Omega) / rated;
            _governorIntegral = Mathf.Clamp(_governorIntegral + err * 3f * dt, 0f, 1f);
            target = Mathf.Clamp(err * 6f + _governorIntegral, 0f, 1f);
        }
        else
        {
            float err = (IdleOmega - Omega) / rated;
            _idleIntegral = Mathf.Clamp(_idleIntegral + err * 2f * dt, 0f, 0.3f);
            float idle = Mathf.Clamp(err * 4f + _idleIntegral, 0f, 0.3f);
            target = Mathf.Max(_demand, idle);
        }

        float tau = Mathf.Max(Engine.ThrottleTimeConstant, 0.01f);
        _targetThrottle += (target - _targetThrottle) * (1f - Mathf.Exp(-dt / tau));

        if (Omega > LimiterOmega)
            Limiting = true;
        else if (Omega < LimiterOmega * 0.98f)
            Limiting = false;
        Throttle = Limiting ? 0f : _targetThrottle;
    }

    /// <summary>
    /// Un sotto-passo della dinamica del regime: I·dω/dt = Q_motore − Q_carico − Q_attrito.
    /// <paramref name="loadTorque"/> e' la coppia dell'elica riportata al motore (gia' divisa per
    /// rapporto e rendimento); <paramref name="extraInertia"/> l'inerzia dell'elica vista dal motore.
    /// </summary>
    public void Integrate(float dt, float loadTorque, float extraInertia)
    {
        float rated = Engine.RatedOmega;
        float qMax = Engine.MaxTorque;
        float qEngine = Throttle * qMax * Engine.TorqueFraction(Omega / rated);
        float qFriction = Omega > 0f
            ? qMax * (Engine.FrictionFraction * (0.5f + 0.5f * Omega / rated) + Engine.PumpingFraction * (1f - Throttle) * Omega / rated)
            : 0f;
        float inertia = Mathf.Max(Engine.InertiaKgm2 + extraInertia, 1e-3f);
        Omega = Mathf.Max(0f, Omega + (qEngine - loadTorque - qFriction) / inertia * dt);
    }

    /// Potenza all'albero (W), per il consumo e la telemetria.
    public float ShaftPower => Throttle * Engine.MaxTorque * Engine.TorqueFraction(Omega / Engine.RatedOmega) * Omega;

    /// Consumo di carburante (kg/s).
    public float FuelRate => ShaftPower / 1000f * Engine.BsfcGPerKwh / 3.6e6f;
}
