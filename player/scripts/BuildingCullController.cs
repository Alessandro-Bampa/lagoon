using Godot;

namespace Lagoon;

/// <summary>
/// Cutaway degli edifici: decide cosa la camera dell'avatar LOCALE disegna, in funzione del piano in
/// cui il giocatore si trova (skill <c>building-cutaway</c>). Va montato come figlio del root del
/// giocatore, accanto a <c>VisibilityGate</c>, di cui e' il gemello per il rendering della struttura.
///
/// Fa due cose, entrambe per i soli EDIFICI e solo mentre ci si sta DENTRO:
/// <list type="number">
/// <item>toglie dalla resa i piani sopra a quello in cui ci si trova, con le loro ombre;</item>
/// <item>sfuma i muri del piano corrente rivolti alla camera, cioe' quelli fra la camera e il
/// giocatore (<see cref="BuildingVolume.CollectCameraSideMeshes"/>).</item>
/// </list>
///
/// Le due cose stanno nello stesso nodo perche' rispondono alla stessa domanda — «in che piano sono
/// e da che parte guardo?» — e la risposta e' <b>autorata</b>: piani, quote, render layer. Nessuna
/// misura puo' dedurre "quanti piani ha questo edificio", ed e' l'intero motivo per cui questo nodo
/// esiste.
///
/// FUORI DAGLI EDIFICI NON SUCCEDE NULLA. Un muro isolato, una roccia o la murata di una barca che
/// coprono il giocatore restano pieni: e' una lacuna dichiarata, non una svista. C'e' stato un
/// sistema che apriva qualunque superficie in base al campo visivo, ed e' stato rimosso perche' in
/// gioco rendeva imprevedibile cosa sarebbe diventato trasparente (skill <c>building-cutaway</c>).
///
/// Nessuno stato di gioco, nessuna RPC, nessuna proprieta' replicata, nessuna modifica alla fisica.
/// E' per costruzione una decisione locale del singolo peer — due giocatori possono stare in due
/// piani diversi dello stesso edificio e vedere due cose diverse, che e' l'unico comportamento
/// corretto in cooperativa.
///
/// Espone anche <see cref="AimCeilingHeight"/>, che <c>WeaponInput</c> gira ad
/// <c>AimResolver.ResolveAimPoint</c>: il cursore non deve posarsi su cio' che il cutaway ha appena
/// nascosto, e chi conosce il piano corrente e' questo nodo.
///
/// La guardia di autorita' in <see cref="_Ready"/> NON e' un'ottimizzazione ma correttezza, per la
/// stessa ragione documentata in <c>ShroudRenderer</c>: ogni avatar, anche remoto, porta con se' una
/// <c>PlayerCamera</c>, e solo quella locale ha <c>Current = true</c>. Lasciar girare il controller
/// su un avatar remoto significherebbe far dettare a lui il culling delle ombre, che sono stato di
/// processo condiviso fra tutte le camere.
/// </summary>
public partial class BuildingCullController : Node
{
    /// Spegnibile per il debug (vedere l'edificio intero da dentro) senza smontare il nodo.
    [Export] public bool Enabled { get; set; } = true;

    /// <summary>
    /// Intervallo fra due interrogazioni, in secondi. Non serve la frequenza di frame: e' un test
    /// geometrico su pochi volumi, e a 15 Hz il ritardo massimo di un tetto che sparisce e' sotto la
    /// soglia percettiva. Stesso ragionamento di <c>VisibilityGate.QueryInterval</c>.
    /// </summary>
    [Export] public float QueryInterval { get; set; } = 1f / 15f;

    /// <summary>
    /// Quota mondo sotto la quale il raggio del cursore deve restare, o infinito all'aperto. E' il
    /// soffitto della stanza in cui si sta: sopra non c'e' nulla di mirabile, perche' o e' culled o
    /// e' il proprio soffitto.
    /// </summary>
    public float AimCeilingHeight { get; private set; } = float.PositiveInfinity;

    /// <summary>
    /// Velocita' con cui un muro passa da pieno a sfumato e viceversa (frazione al secondo). Serve
    /// perche' la selezione dei muri cambia a scatti — a ogni cambio di piano e a ogni rotazione
    /// della camera — e senza interpolazione le superfici commuterebbero di colpo.
    /// </summary>
    [Export] public float FadeSpeed { get; set; } = 6f;

    private CharacterMotor _motor = null!;
    private Camera3D _camera = null!;

    private float _queryTimer;

    /// <summary>
    /// Muri attualmente sfumati o in via di sfumatura, con il loro valore corrente e se sono ancora
    /// voluti. Le voci non volute scendono a zero e vengono lasciate andare: senza questa memoria,
    /// un muro che esce dalla selezione tornerebbe pieno di colpo.
    /// </summary>
    private readonly System.Collections.Generic.Dictionary<MeshInstance3D, FadeState> _fades = new();

    /// Riusato a ogni interrogazione per non allocare 15 volte al secondo.
    private readonly System.Collections.Generic.List<MeshInstance3D> _wallBuffer = new();

    private readonly System.Collections.Generic.List<MeshInstance3D> _releaseBuffer = new();

    private struct FadeState
    {
        public float Value;
        public bool Wanted;
    }

    /// Nome del parametro d'ISTANZA del materiale di mondo. Scriverlo su un materiale che non lo
    /// dichiara NON e' un errore: il valore si perde in silenzio, ed e' il modo muto in cui questo
    /// sistema si rompe (skill <c>building-cutaway</c>).
    private static readonly StringName FadeParameter = "fade";

    /// Ultima maschera applicata: si scrive sulla camera solo quando cambia davvero.
    private uint _appliedMask;

    /// Edificio in cui ci si trovava all'ultima interrogazione, e piano. -1 = fuori.
    private BuildingVolume? _currentBuilding;
    private int _currentFloor = -1;

    public override void _Ready()
    {
        Node parent = GetParent();

        // L'autorita' della root e' gia' stata impostata nel suo _EnterTree.
        if (!parent.IsMultiplayerAuthority())
        {
            SetProcess(false);
            return;
        }

        _motor = parent as CharacterMotor ?? throw new System.InvalidOperationException(
            "BuildingCullController va montato sotto un CharacterMotor.");
        _camera = parent.GetNode<Camera3D>("PlayerCamera");

        _appliedMask = RenderLayers.Everything;
        _camera.CullMask = _appliedMask;

        _queryTimer = GD.Randf() * QueryInterval;
    }

    /// <summary>
    /// L'ordine conta: prima <see cref="UpdateBuilding"/>, che a ogni cambio di piano riaccende TUTTE
    /// le ombre del piano visibile (<c>ApplyFloorShadows</c>), poi <see cref="AdvanceFades"/>, che le
    /// rispegne sui muri sfumati. Invertirli lascerebbe l'ombra piena di un muro trasparente per
    /// tutti i frame fino al prossimo cambio di piano.
    /// </summary>
    public override void _Process(double delta)
    {
        UpdateBuilding((float)delta);
        AdvanceFades((float)delta);
    }

    private void UpdateBuilding(float delta)
    {
        if (!Enabled)
        {
            Apply(RenderLayers.Everything);
            LeaveBuilding();
            return;
        }

        _queryTimer -= delta;
        if (_queryTimer > 0f)
            return;
        _queryTimer = QueryInterval;

        // Quota dei PIEDI dallo stato replicato: e' la stessa porta usata da VisionSource, quindi il
        // cutaway segue il giocatore anche quando sta su una barca (SyncPosition e' locale allo
        // scafo, ResolvedSyncPosition no).
        Vector3 feet = _motor.ResolvedSyncPosition;
        feet.Y = _motor.ResolvedFeetY;

        // Isteresi: chi e' gia' dentro esce con una soglia piu' larga di quella con cui e' entrato,
        // altrimenti sulla soglia di una porta il tetto lampeggia a ogni micro-oscillazione.
        float slack = _currentBuilding != null ? _currentBuilding.ExitHysteresis : 0f;

        // Prima si riprova l'edificio corrente con la soglia larga; solo se non risponde piu' si
        // riparte dalla ricerca completa, con la soglia stretta.
        BuildingVolume? building = null;
        int floor = -1;

        if (_currentBuilding != null)
        {
            floor = _currentBuilding.FloorIndexAt(feet, slack);
            if (floor >= 0)
                building = _currentBuilding;
        }

        if (building == null)
            building = BuildingRegistry.FindContaining(this, feet, 0f, out floor);

        if (building == null)
        {
            // Fuori: l'edificio si vede intero e pieno. Nessuna sfumatura all'avvicinarsi — da fuori
            // deve leggersi come un volume chiuso, altrimenti non esiste piu' il momento in cui si
            // entra.
            LeaveBuilding();
            Apply(RenderLayers.Everything);
            return;
        }

        Enter(building, floor);

        // Dentro: si vede il proprio piano e tutti quelli SOTTO. I piani inferiori restano accesi di
        // proposito — nasconderli lascerebbe vedere il cielo attraverso il vano scala, che e' peggio
        // del problema che il cutaway risolve. L'involucro non e' piu' un layer a parte: appartiene
        // al proprio piano, quindi quello dei piani alti sparisce insieme al resto del piano e
        // quello del piano corrente resta, sfumato.
        Apply(RenderLayers.NonBuildingMask | RenderLayers.FloorsUpTo(floor));

        SelectCameraSideWalls(building, floor);
    }

    /// <summary>
    /// Marca come "voluti" i muri del piano corrente rivolti alla camera. Si rifa' a ogni
    /// interrogazione e non una volta all'ingresso, perche' la camera RUOTA (Q/E): l'insieme dei muri
    /// che stanno fra la camera e il giocatore cambia mentre si gira attorno all'edificio.
    ///
    /// Il retro della camera e' <c>GlobalBasis.Z</c>: in Godot una camera guarda lungo il proprio
    /// -Z, quindi +Z punta verso l'osservatore.
    /// </summary>
    private void SelectCameraSideWalls(BuildingVolume building, int floor)
    {
        _wallBuffer.Clear();
        building.CollectCameraSideMeshes(floor, _camera.GlobalBasis.Z, _wallBuffer);

        MarkNoneWanted();

        foreach (MeshInstance3D mesh in _wallBuffer)
        {
            _fades.TryGetValue(mesh, out FadeState state);
            state.Wanted = true;
            _fades[mesh] = state;
        }
    }

    private void MarkNoneWanted()
    {
        _releaseBuffer.Clear();
        _releaseBuffer.AddRange(_fades.Keys);

        foreach (MeshInstance3D mesh in _releaseBuffer)
        {
            FadeState state = _fades[mesh];
            state.Wanted = false;
            _fades[mesh] = state;
        }
    }

    /// <summary>
    /// Interpola i valori di sfumatura e li scrive sulle mesh. Gira a ogni FRAME, non al ritmo delle
    /// interrogazioni: la selezione puo' anche cambiare a 15 Hz, ma la transizione deve essere
    /// morbida.
    ///
    /// Una voce arrivata a zero e non piu' voluta viene lasciata andare dopo aver rimesso il valore
    /// pieno e riacceso l'ombra. Il ripristino non e' cosmetico: <c>fade</c> e <c>CastShadow</c>
    /// vivono sull'istanza della mesh, non sulla camera, quindi un muro dimenticato resterebbe
    /// granulare per il resto della partita anche a chilometri di distanza.
    /// </summary>
    private void AdvanceFades(float delta)
    {
        if (_fades.Count == 0)
            return;

        float step = Mathf.Clamp(delta * FadeSpeed, 0f, 1f);

        _releaseBuffer.Clear();
        _releaseBuffer.AddRange(_fades.Keys);

        foreach (MeshInstance3D mesh in _releaseBuffer)
        {
            FadeState state = _fades[mesh];

            if (!GodotObject.IsInstanceValid(mesh))
            {
                _fades.Remove(mesh);
                continue;
            }

            state.Value = Mathf.Lerp(state.Value, state.Wanted ? 1f : 0f, step);

            if (!state.Wanted && state.Value < 0.01f)
            {
                ReleaseFade(mesh);
                _fades.Remove(mesh);
                continue;
            }

            _fades[mesh] = state;
            mesh.SetInstanceShaderParameter(FadeParameter, state.Value);

            // Il CullMask non tocca la shadow map: un muro granulare continuerebbe a proiettare
            // un'ombra PIENA, e la stanza resterebbe al buio sotto una parete che si vede attraverso.
            mesh.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        }
    }

    private static void ReleaseFade(MeshInstance3D mesh)
    {
        mesh.SetInstanceShaderParameter(FadeParameter, 0f);
        mesh.CastShadow = GeometryInstance3D.ShadowCastingSetting.On;
    }

    /// Rimette subito ogni muro al suo stato pieno, senza aspettare l'interpolazione.
    private void RestoreAllFades()
    {
        foreach (MeshInstance3D mesh in _fades.Keys)
            if (GodotObject.IsInstanceValid(mesh))
                ReleaseFade(mesh);

        _fades.Clear();
    }

    /// Memorizza edificio e piano correnti, aggiornando le ombre solo quando qualcosa e' cambiato.
    private void Enter(BuildingVolume building, int floor)
    {
        if (_currentBuilding == building && _currentFloor == floor)
            return;

        // Se si passa direttamente da un edificio a un altro, il primo va ripristinato.
        if (_currentBuilding != null && _currentBuilding != building)
            Restore(_currentBuilding);

        _currentBuilding = building;
        _currentFloor = floor;
        AimCeilingHeight = building.CeilingHeightOf(floor);
        building.ApplyFloorShadows(floor);
    }

    /// <summary>
    /// Uscendo, i muri non tornano pieni di colpo: si limitano a non essere piu' "voluti" e
    /// rientrano con l'interpolazione. Uscire da una porta e vedere la facciata riapparire di scatto
    /// e' esattamente cio' che l'isteresi sulla soglia esiste per evitare.
    /// </summary>
    private void LeaveBuilding()
    {
        if (_currentBuilding == null)
            return;

        Restore(_currentBuilding);
        MarkNoneWanted();
        _currentBuilding = null;
        _currentFloor = -1;
        AimCeilingHeight = float.PositiveInfinity;
    }

    /// <summary>
    /// Rimette un edificio nel suo stato pieno. Obbligatorio, non cosmetico: le ombre sono stato di
    /// PROCESSO, non della camera, e un piano lasciato con <c>CastShadow = Off</c> resterebbe senza
    /// ombra per il resto della partita anche a chilometri di distanza. E' anche il motivo
    /// dell'<c>_ExitTree</c>.
    /// </summary>
    private static void Restore(BuildingVolume building) => building.ApplyFloorShadows(-1);

    private void Apply(uint mask)
    {
        if (_appliedMask == mask)
            return;

        _appliedMask = mask;
        _camera.CullMask = mask;
    }

    public override void _ExitTree()
    {
        LeaveBuilding();
        RestoreAllFades();
    }
}
