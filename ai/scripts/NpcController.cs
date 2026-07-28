using Godot;

namespace Lagoon;

/// <summary>
/// Movimento di un NPC umano. Esiste per dimostrare — e tenere onesto — che il sistema di
/// locomozione e' davvero condiviso: eredita da <see cref="CharacterMotor"/> esattamente come
/// <see cref="PlayerController"/>, e non duplica una riga di gravita', accelerazione, pendenza,
/// gradini o accovacciamento.
///
/// AUTORITA': host, al contrario del giocatore. Il movimento del giocatore e' client-autoritativo
/// perche' l'input e' suo; un NPC non ha nessun input da nessuna parte, quindi lo calcola l'host e
/// lo replica (CLAUDE.md §3). E' anche il motivo per cui qui non c'e' nessuna RPC di richiesta: non
/// esiste un client che possa chiedere qualcosa a questo nodo.
///
/// SCOPE DICHIARATO: naviga e basta. Niente percezione, niente inseguimento, niente tiro — quella e'
/// IA, ed e' un prototipo a se'. Qui c'e' il minimo che serve perche' un personaggio non giocante
/// cammini con lo stesso rig, le stesse animazioni e gli stessi layer procedurali del giocatore.
/// </summary>
public partial class NpcController : CharacterMotor
{
    /// <summary>
    /// Punti da percorrere in sequenza, in coordinate LOCALI al punto di spawn. Si ripetono in
    /// ciclo. Vuoto = l'NPC resta fermo, che e' comunque un caso utile (sentinella, bersaglio).
    /// </summary>
    [Export] public Vector3[] Waypoints { get; set; } = [];

    /// Distanza sotto la quale un waypoint si considera raggiunto, in metri.
    [Export] public float ArrivalDistance { get; set; } = 0.6f;

    /// Se camminare o correre fra un waypoint e l'altro.
    [Export] public bool Running { get; set; }

    private NavigationAgent3D _agent = null!;
    private Vector3 _origin;
    private int _target;

    public override void _EnterTree()
    {
        // Host-autoritativo, sempre. Va fatto in _EnterTree e non in _Ready perche' il
        // MultiplayerSynchronizer figlio eredita l'autorita' quando entra nell'albero.
        SetMultiplayerAuthority(NetworkConstants.HostPeerId);
    }

    public override void _Ready()
    {
        base._Ready();

        _agent = GetNode<NavigationAgent3D>("NavigationAgent3D");
        _origin = GlobalPosition;

        // La mappa di navigazione non e' pronta nello stesso frame in cui la scena entra
        // nell'albero: chiedere una rotta adesso restituirebbe il punto di partenza.
        CallDeferred(MethodName.SeekNext);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!IsMultiplayerAuthority())
        {
            ApplyRemoteState(delta);
            return;
        }

        Vector3 direction = Vector3.Zero;
        float speed = 0f;

        if (Waypoints.Length > 0)
        {
            // Con una mappa di navigazione si segue la rotta; senza, si punta dritti al waypoint.
            //
            // Il ripiego non e' pigrizia: un livello di prova puo' non avere ancora un
            // NavigationRegion3D, e senza rotta l'NPC resterebbe immobile — un sintomo muto che
            // si scambia per un bug dell'animazione. Meglio camminare in linea retta e prendersi
            // gli ostacoli, che e' visibile.
            //
            // La condizione NON puo' essere il solo IsNavigationFinished(): misurato, con una
            // mappa valida ma senza navmesh l'agente NON dichiara mai la rotta finita e
            // GetNextPathPosition() restituisce la posizione corrente, cioe' direzione nulla.
            // Il ripiego si aggancia anche a quel caso: se il punto di rotta e' dove siamo gia',
            // rotta non ce n'e'.
            Vector3 next = CurrentWaypoint();
            if (_agent.GetNavigationMap().IsValid && !_agent.IsNavigationFinished())
            {
                Vector3 onPath = _agent.GetNextPathPosition();
                if (onPath.DistanceSquaredTo(GlobalPosition) > 0.01f)
                    next = onPath;
            }

            direction = next - GlobalPosition;
            direction.Y = 0f;

            if (direction.LengthSquared() > 0.0001f)
            {
                direction = direction.Normalized();
                speed = Running ? RunSpeed : WalkSpeed;
            }
        }

        StepMotion(direction, speed, wantJump: false, wantCrouch: false, delta);

        // Un NPC disarmato guarda dove va, come il giocatore disarmato. La mira verticale resta a
        // zero finche' non ci sara' un'IA che decida dove puntare.
        if (direction.LengthSquared() > 0.001f)
            UpdateFacing(Mathf.Atan2(direction.X, direction.Z), delta);

        // Fuori mira il busto segue il corpo, come nel giocatore: quando un'IA armata vorra'
        // puntare altrove le bastera' scrivere SyncAimYaw/SyncAimPitch/SyncAiming.
        SyncAimYaw = SyncFacing;

        SyncPosition = GlobalPosition;
        PublishLocomotionState();

        if (Waypoints.Length > 0 && GlobalPosition.DistanceTo(CurrentWaypoint()) < ArrivalDistance)
        {
            _target = (_target + 1) % Waypoints.Length;
            SeekNext();
        }
    }

    private Vector3 CurrentWaypoint() => _origin + Waypoints[_target];

    private void SeekNext()
    {
        if (Waypoints.Length > 0)
            _agent.TargetPosition = CurrentWaypoint();
    }

    /// <summary>
    /// Sui peer non autoritativi non si calcola niente: si rispecchia lo stato replicato. Nessuna
    /// interpolazione nello spazio di un'ancora come per il giocatore — un NPC non sale in barca,
    /// e finche' non lo fara' un'interpolazione in coordinate mondo e' corretta.
    /// </summary>
    private void ApplyRemoteState(double delta)
    {
        GlobalPosition = GlobalPosition.Lerp(SyncPosition, Mathf.Clamp((float)delta * 14f, 0f, 1f));
        ApplyRemoteFacing(14f, delta);
    }
}
