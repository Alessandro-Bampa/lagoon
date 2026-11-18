using System.Collections.Generic;
using Godot;

namespace Lagoon;

/// <summary>
/// Monta una barca su misura da una <see cref="BoatConfig"/>: scafo, propulsione, consolle,
/// accessori, livrea; poi ne ricava cio' che serve a <see cref="BoatController"/> (posto di guida,
/// mesh idrodinamica, forme di collisione, masse, nucleo fisico) e all'animazione del pilota (dove
/// vanno le mani).
///
/// Sta come figlio della barca e si costruisce nel PROPRIO <c>_Ready</c>, che Godot chiama prima di
/// quello del genitore: quando <see cref="BoatController"/> parte, <c>HelmSeat</c>, masse e
/// <see cref="Physics"/> sono gia' pronti.
///
/// Nomi letti dai modelli (generati da <c>tools/blender/build_lagoon_boats.py</c>, skill
/// vehicles-boats §9): <c>Seat_Helm</c>, <c>Mount_*</c>, <c>HydroHull</c>, <c>HullCollision</c>,
/// <c>DeckCollision</c>; sulla consolle <c>Wheel</c>, <c>Hand_L</c>, <c>Hand_R</c>.
///
/// Rete: e' pura costruzione deterministica dalla scena, uguale su ogni peer. Lo stato che cambia
/// (sterzo, gas) lo replica la barca; qui c'e' solo la resa (<see cref="ApplyHelmPose"/>).
/// <c>[Tool]</c>: nell'editor si vede la barca montata (senza collisioni) e, aprendo la scena della
/// barca, l'anteprima del galleggiamento col rapporto idrostatico (<see cref="BuildPreview"/>).
/// </summary>
[Tool]
public partial class BoatAssembly : Node3D
{
    private const string GeneratedPrefix = "Assembled";

    [Export]
    public BoatConfig? Config
    {
        get => _config;
        set
        {
            _config = value;
            if (IsInsideTree() && Engine.IsEditorHint())
                Build();
        }
    }

    private BoatConfig? _config;

    /// <summary>
    /// Altezza del CENTRO del corpo del pilota sopra i piedi: l'origine del giocatore e' il centro
    /// della capsula (in <c>Player.tscn</c> il rig sta 1 m sotto), mentre <c>Seat_Helm</c> segna i
    /// piedi sul pagliolo. <c>HelmSeat</c> della barca e' quindi <c>Seat_Helm</c> alzato di tanto,
    /// lungo la verticale DELLA BARCA.
    /// </summary>
    [Export] public float PilotBodyCenter { get; set; } = 1.0f;

    [ExportGroup("Anteprima (editor)")]
    /// <summary>
    /// Rimonta la barca e ricalcola l'anteprima. Le modifiche DENTRO le risorse (coefficienti, masse,
    /// motore...) non avvisano il nodo: dopo averle cambiate si preme qui.
    /// </summary>
    [ExportToolButton("Ricalcola anteprima")]
    public Callable RecomputePreview => Callable.From(Build);

    /// <summary>
    /// Rapporto idrostatico calcolato nell'editor quando si apre la scena della barca (solo lettura, non
    /// salvato): massa, baricentro, galleggiamento, stabilita', stime di rollio e punto fisso.
    /// </summary>
    [Export(PropertyHint.MultilineText)]
    public string Hydrostatics { get; set; } = "";

    public OutboardMotor? Motor { get; private set; }
    public HelmType Helm => _config?.Helm ?? HelmType.Tiller;

    /// <summary>
    /// Nucleo fisico (scafo per triangoli, propulsione, appendici): null nell'editor o se lo scafo non
    /// ha la mesh <c>HydroHull</c> (la barca torna al galleggiamento per punti).
    /// </summary>
    public BoatPhysics? Physics { get; private set; }

    /// Masse a componenti (scafo, motore, masse fisse; quelle dinamiche le aggiunge la barca).
    public BoatMassModel? MassModel { get; private set; }

    /// Angolo massimo del governo (rad): sterzo del fuoribordo o timone.
    public float MaxSteerAngle => Mathf.DegToRad(_config?.Motor?.MaxSteerDegrees
        ?? _config?.Inboard?.RudderMaxDegrees ?? 30f);

    /// Dove vanno le mani del pilota (null = quella mano resta come nella clip).
    public Node3D? LeftHandTarget { get; private set; }
    public Node3D? RightHandTarget { get; private set; }

    private Node3D? _wheel;
    private Basis _wheelRest = Basis.Identity;
    private Node3D? _rudderNode;
    private Basis _rudderRest = Basis.Identity;
    private Node3D? _inboardProp;
    private Basis _inboardPropRest = Basis.Identity;
    private float _inboardPropAngle;
    private readonly Dictionary<string, StandardMaterial3D> _liveried = new();
    private readonly List<(Vector3 Position, float Mass)> _massMarkers = new();

    public override void _Ready() => Build();

    public override void _ValidateProperty(Godot.Collections.Dictionary property)
    {
        // Il rapporto si vede nell'ispettore ma non si scrive nella scena e non si modifica.
        if (property["name"].AsStringName() == PropertyName.Hydrostatics)
            property["usage"] = (int)(PropertyUsageFlags.Editor | PropertyUsageFlags.ReadOnly);
    }

    /// Smonta quanto costruito prima e rimonta dalla configurazione corrente.
    public void Build()
    {
        if (GetParent() is not RigidBody3D boat)
            return;
        Node3D? visual = boat.GetNodeOrNull<Node3D>("Visual");
        if (visual == null || _config?.Hull == null)
            return;

        Clear(boat, visual);
        _liveried.Clear();

        // --- Scafo ---------------------------------------------------------------------------
        var hull = _config.Hull.Instantiate<Node3D>();
        hull.Name = GeneratedPrefix + "Hull";
        visual.AddChild(hull);
        Transform3D toBoat = visual.Transform;

        // --- Motore --------------------------------------------------------------------------
        Motor = null;
        if (_config.Motor?.Model != null && Find(hull, "Mount_Motor") is Node3D motorMount)
        {
            Motor = new OutboardMotor { Name = GeneratedPrefix + "Motor" };
            visual.AddChild(Motor);
            Motor.Setup(_config.Motor, _config.Motor.Model.Instantiate<Node3D>(),
                InHull(hull, motorMount), _config.Helm == HelmType.Tiller);
        }

        // --- Consolle e mani -----------------------------------------------------------------
        _wheel = null;
        LeftHandTarget = RightHandTarget = null;
        if (_config.Helm == HelmType.Wheel && _config.Console != null && Find(hull, "Mount_Console") is Node3D consoleMount)
        {
            var console = _config.Console.Instantiate<Node3D>();
            console.Name = GeneratedPrefix + "Console";
            visual.AddChild(console);
            console.Transform = InHull(hull, consoleMount);
            _wheel = Find(console, "Wheel") as Node3D;
            _wheelRest = _wheel?.Basis ?? Basis.Identity;
            LeftHandTarget = Find(console, "Hand_L") as Node3D;
            RightHandTarget = Find(console, "Hand_R") as Node3D;
        }
        else if (_config.Helm == HelmType.Tiller)
        {
            // Seduti a dritta: la barra e' alla sinistra del pilota, la destra resta sul ginocchio.
            LeftHandTarget = Motor?.TillerGrip;
        }

        // --- Accessori -----------------------------------------------------------------------
        foreach (BoatAccessory accessory in _config.Accessories)
        {
            if (accessory?.Model == null)
                continue;
            if (Find(hull, accessory.Mount) is not Node3D mount)
            {
                GD.PushWarning($"[BoatAssembly] lo scafo non ha l'attacco '{accessory.Mount}': accessorio saltato.");
                continue;
            }
            var node = accessory.Model.Instantiate<Node3D>();
            node.Name = GeneratedPrefix + accessory.Mount;
            foreach (string group in accessory.Groups)
                node.AddToGroup(group);
            visual.AddChild(node);
            node.Transform = InHull(hull, mount);
        }

        ApplyLivery(visual);

        // --- Posto di guida e galleggiamento -------------------------------------------------
        if (Find(hull, "Seat_Helm") is Node3D seat && boat.GetNodeOrNull<Marker3D>("HelmSeat") is Marker3D helm)
            helm.Transform = (toBoat * InHull(hull, seat)).Translated(Vector3.Up * PilotBodyCenter);

        // --- Entrobordo: parti mobili del modello dello scafo --------------------------------
        _rudderNode = _config.Inboard != null ? Find(hull, "Rudder") as Node3D : null;
        _rudderRest = _rudderNode?.Basis ?? Basis.Identity;
        _inboardProp = _config.Inboard != null ? Find(hull, "Propeller") as Node3D : null;
        _inboardPropRest = _inboardProp?.Basis ?? Basis.Identity;

        // --- Collisioni, mesh idrodinamica e massa (solo in gioco) ---------------------------
        MeshInstance3D? hullShape = Find(hull, "HullCollision") as MeshInstance3D;
        MeshInstance3D? deckShape = Find(hull, "DeckCollision") as MeshInstance3D;
        MeshInstance3D? hydroShape = Find(hull, "HydroHull") as MeshInstance3D;
        MeshInstance3D? swimShape = Find(hull, "SwimCollision") as MeshInstance3D;
        foreach (MeshInstance3D? hidden in new[] { hullShape, deckShape, hydroShape, swimShape })
        {
            if (hidden != null)
                hidden.Visible = false;
        }

        Physics = null;
        HydroHull? hydro = hydroShape?.Mesh != null
            ? HydroHull.FromFaces(hydroShape.Mesh.GetFaces(), toBoat * InHull(hull, hydroShape))
            : null;
        if (hydro == null)
            GD.PushWarning($"[BoatAssembly] '{_config.ResourcePath}': lo scafo non ha una mesh HydroHull valida: la barca non galleggera'.");
        _hydro = hydro;
        MassModel = BuildMassModel(hull, toBoat, hydro);

        if (Engine.IsEditorHint())
        {
            // Anteprima: galleggiamento d'equilibrio, baricentro, carena, masse, rapporto idrostatico.
            BuildPreview(hydro);
            return;
        }

        if (hullShape?.Mesh != null && boat.GetNodeOrNull<CollisionShape3D>("Hull") is CollisionShape3D hullCollider)
        {
            hullCollider.Transform = toBoat * InHull(hull, hullShape);
            hullCollider.Shape = hullShape.Mesh.CreateConvexShape(true, false);
        }
        if (boat.GetNodeOrNull<AnimatableBody3D>("Deck") is AnimatableBody3D deck)
        {
            // Ponte calpestabile e fasciame esterno per chi nuota, entrambi sul layer 6: lo scafo
            // del corpo rigido (layer 5) il giocatore non lo vede (skill vehicles-boats §2, §6).
            foreach ((MeshInstance3D? shape, string name) in new[] { (deckShape, "Deck"), (swimShape, "Swim") })
            {
                if (shape?.Mesh == null)
                    continue;
                deck.AddChild(new CollisionShape3D
                {
                    Name = GeneratedPrefix + name,
                    Shape = shape.Mesh.CreateTrimeshShape(),
                    Transform = deck.Transform.AffineInverse() * toBoat * InHull(hull, shape),
                });
            }
        }

        MassModel.Apply(boat, true);
        if (hydro != null)
            Physics = CreatePhysics(hull, toBoat, hydro);
    }

    /// <summary>
    /// Masse a componenti: scafo (guscio con baricentro di progetto), motore, masse fisse. Il motore
    /// fuoribordo pesa sullo specchio ~0,3 m sotto il bordo e dietro il piantone: il baricentro arretra
    /// e la barca sta appoppata, come quelle vere. Le posizioni sono nello spazio della barca.
    /// </summary>
    private BoatMassModel BuildMassModel(Node3D hull, Transform3D toBoat, HydroHull? hydro)
    {
        BoatConfig config = _config!;
        var model = new BoatMassModel();
        _massMarkers.Clear();
        Vector3 hullCenter = toBoat * config.HullCenterOfMass;
        if (hydro != null)
            model.AddHullShell(config.HullMass, hydro, hullCenter);
        else
            model.AddFixed(config.HullMass, hullCenter);
        _massMarkers.Add((hullCenter, config.HullMass));
        if (Motor != null && config.Motor != null)
        {
            Vector3 at = Motor.Transform.Origin + new Vector3(0f, -0.3f, 0.2f);
            model.AddFixed(config.Motor.Mass, at, new Vector3(0.3f, 0.8f, 0.4f));
            _massMarkers.Add((at, config.Motor.Mass));
        }
        if (config.Inboard?.Engine is EngineDefinition engine)
        {
            Vector3 at = Find(hull, "Mount_Engine") is Node3D m ? (toBoat * InHull(hull, m)).Origin : hullCenter;
            model.AddFixed(engine.Mass, at, Vector3.One * Mathf.Pow(engine.Mass / 800f, 1f / 3f));
            _massMarkers.Add((at, engine.Mass));
        }
        foreach (BoatMassItem item in config.MassItems)
        {
            if (item == null)
                continue;
            model.AddFixed(item.Mass, toBoat * item.Position, item.Size);
            _massMarkers.Add((toBoat * item.Position, item.Mass));
        }
        model.Recompute();
        return model;
    }

    /// <summary>
    /// Il nucleo fisico dai pezzi montati. Mancando un pezzo obbligatorio (motore senza elica...) si
    /// avvisa e si salta quella propulsione: la barca galleggia comunque.
    /// </summary>
    private BoatPhysics CreatePhysics(Node3D hull, Transform3D toBoat, HydroHull hydro)
    {
        BoatConfig config = _config!;
        OutboardDrive? outboard = null;
        if (Motor != null && config.Motor is OutboardDefinition od)
        {
            if (od.Engine != null && od.Gearbox != null && od.Propeller != null)
                outboard = new OutboardDrive(od, Motor);
            else
                GD.PushWarning($"[BoatAssembly] '{od.ResourcePath}': motore senza Engine/Gearbox/Propeller.");
        }

        ShaftDrive? shaft = null;
        if (config.Inboard is InboardDefinition ib)
        {
            Node3D? prop = Find(hull, "Mount_Prop") as Node3D;
            Node3D? rudder = Find(hull, "Mount_Rudder") as Node3D;
            if (ib.Engine != null && ib.Gearbox != null && ib.Propeller != null && prop != null)
                shaft = new ShaftDrive(ib, toBoat * InHull(hull, prop), rudder != null ? toBoat * InHull(hull, rudder) : toBoat * InHull(hull, prop));
            else
                GD.PushWarning($"[BoatAssembly] '{config.ResourcePath}': entrobordo incompleto (Engine/Gearbox/Propeller o Mount_Prop mancanti).");
        }

        BowThruster? thruster = null;
        if (config.Thruster is BowThrusterDefinition td && Find(hull, "Mount_Thruster") is Node3D tunnel)
            thruster = new BowThruster(td, (toBoat * InHull(hull, tunnel)).Origin);

        var foils = new System.Collections.Generic.List<FoilDefinition>();
        var mounts = new System.Collections.Generic.List<Transform3D>();
        foreach (FoilDefinition foil in config.FixedFoils)
        {
            if (foil == null)
                continue;
            if (Find(hull, foil.Mount) is Node3D at)
            {
                foils.Add(foil);
                mounts.Add(toBoat * InHull(hull, at));
            }
            else
            {
                GD.PushWarning($"[BoatAssembly] lo scafo non ha l'attacco '{foil.Mount}': profilo saltato.");
            }
        }

        return new BoatPhysics(hydro, config.Hydro ?? new HydroCoefficients(), outboard, shaft, thruster,
            foils.ToArray(), mounts.ToArray());
    }

    /// <summary>
    /// Resa del governo dallo stato replicato: motore sul piantone e trim, timone, elica, volante. Il
    /// volante gira di <see cref="BoatConfig.WheelTurnsLockToLock"/> giri da un fine corsa all'altro,
    /// nel verso della virata (orario = a dritta, visto dal pilota).
    /// </summary>
    public void ApplyHelmPose(float steerAngle, float trim, float propRpm, double delta)
    {
        Motor?.ApplyPose(steerAngle, trim, propRpm, delta);
        if (_rudderNode != null)
            _rudderNode.Basis = _rudderRest * new Basis(Vector3.Up, steerAngle);
        if (_inboardProp != null)
        {
            float rpm = Mathf.Clamp(propRpm, -900f, 900f);
            _inboardPropAngle = Mathf.Wrap(_inboardPropAngle + rpm / 60f * Mathf.Tau * (float)delta, 0f, Mathf.Tau);
            _inboardProp.Basis = _inboardPropRest * new Basis(Vector3.Back, _inboardPropAngle);
        }
        if (_wheel == null || _config == null)
            return;
        float steer = steerAngle / Mathf.Max(MaxSteerAngle, 0.01f);
        float wheelAngle = steer * _config.WheelTurnsLockToLock * 0.5f * Mathf.Tau;
        _wheel.Basis = _wheelRest * new Basis(Vector3.Up, wheelAngle);
    }

    /// Trasformata di un nodo del modello nello spazio del nodo radice dello scafo (= del Visual).
    private static Transform3D InHull(Node3D hull, Node3D node)
    {
        Transform3D t = Transform3D.Identity;
        for (Node? n = node; n != null && n != hull.GetParent(); n = n.GetParent())
        {
            if (n is Node3D n3)
                t = n3.Transform * t;
        }
        return t;
    }

    private static Node? Find(Node root, string name) => root.FindChild(name, true, false);

    /// <summary>
    /// Ricolora una COPIA di ogni materiale "per nome" (Hull, Deck, ...): i materiali del .glb sono
    /// condivisi fra tutte le istanze, toccarli ricolorerebbe ogni barca.
    /// </summary>
    private void ApplyLivery(Node3D visual)
    {
        BoatLivery? livery = _config?.Livery;
        if (livery == null)
            return;

        foreach (Node node in visual.FindChildren("*", nameof(MeshInstance3D), true, false))
        {
            if (node is not MeshInstance3D mesh || mesh.Mesh == null)
                continue;
            for (int s = 0; s < mesh.Mesh.GetSurfaceCount(); s++)
            {
                if (mesh.Mesh.SurfaceGetMaterial(s) is not StandardMaterial3D source)
                    continue;
                string name = source.ResourceName;
                if (livery.ColorFor(name) is not Color color)
                    continue;
                if (!_liveried.TryGetValue(name, out StandardMaterial3D? copy))
                {
                    copy = (StandardMaterial3D)source.Duplicate();
                    copy.AlbedoColor = color;
                    _liveried[name] = copy;
                }
                mesh.SetSurfaceOverrideMaterial(s, copy);
            }
        }
    }

    // ====================================================================================
    //  Anteprima nell'editor
    // ====================================================================================

    /// <summary>
    /// Solo quando si apre la scena DELLA barca (non dentro un livello, dove sarebbe rumore): piano
    /// dell'acqua al galleggiamento d'equilibrio, baricentro G (rosso), centro di carena B (blu),
    /// masse (arancio, grandezza ∝ massa); rapporto idrostatico in <see cref="Hydrostatics"/>. Stesso
    /// codice del gioco (<see cref="HydroHull"/>, <see cref="BoatMassModel"/>): cio' che si vede qui e'
    /// cio' che fara' la barca in acqua ferma. Nodi senza owner: non finiscono nella scena.
    /// </summary>
    private void BuildPreview(HydroHull? hydro)
    {
        Hydrostatics = "";
        if (GetParent() is not Node boat || GetTree()?.EditedSceneRoot != boat || MassModel == null || _config == null)
            return;
        Hydrostatics = DescribeHydrostatics(hydro, out Transform3D rest, out Vector3 buoyancy);
        if (hydro == null)
            return;

        // --- Nodi d'anteprima (nello spazio della barca: l'Assembly sta all'origine) ----------
        Aabb box = hydro.Bounds;
        Transform3D inv = rest.AffineInverse();
        float size = Mathf.Max(box.Size.Z, 2f);
        var water = new MeshInstance3D
        {
            Name = GeneratedPrefix + "PreviewWater",
            Mesh = new PlaneMesh { Size = new Vector2(box.Size.X * 2.5f + 1f, box.Size.Z * 1.4f + 1f) },
            MaterialOverride = PreviewMaterial(new Color(0.15f, 0.45f, 0.8f, 0.35f), false),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // Il piano dell'acqua (y = 0 nel mondo) visto dalla barca alla posa d'equilibrio.
            Transform = new Transform3D(inv.Basis, inv * Vector3.Zero),
        };
        AddChild(water);
        AddMarker("PreviewG", MassModel.CenterOfMass, size * 0.02f, new Color(1f, 0.15f, 0.1f));
        // Del centro di carena contano X e Z: lo si disegna sul piano dell'acqua.
        Vector3 bWorld = rest * buoyancy;
        AddMarker("PreviewB", inv * new Vector3(bWorld.X, 0f, bWorld.Z), size * 0.018f, new Color(0.2f, 0.45f, 1f));
        int i = 0;
        foreach ((Vector3 position, float m) in _massMarkers)
        {
            float r = size * 0.008f * (1f + 2f * Mathf.Pow(m / MassModel.Mass, 1f / 3f));
            AddMarker($"PreviewMass{i++}", position, r, new Color(1f, 0.6f, 0.1f));
        }
    }

    /// <summary>
    /// Solo collaudo (<c>tools/boat_trials.gd statics</c>): lo stesso rapporto dell'anteprima.
    /// </summary>
    public string DebugHydrostatics() => DescribeHydrostatics(_hydro, out _, out _);

    private HydroHull? _hydro;

    /// <summary>
    /// Rapporto idrostatico (testo per chi tara la barca nell'editor, non testo di gioco: niente
    /// chiavi di traduzione) e posa d'equilibrio in acqua ferma.
    /// </summary>
    private string DescribeHydrostatics(HydroHull? hydro, out Transform3D rest, out Vector3 buoyancyCenter)
    {
        rest = Transform3D.Identity;
        buoyancyCenter = Vector3.Zero;
        if (MassModel == null || _config == null)
            return "";
        if (hydro == null)
            return "Manca la mesh HydroHull: la barca non galleggerebbe."; // i18n-ignore

        const float rho = 1025f, g = 9.81f;
        BoatMassModel mass = MassModel;
        float volume = mass.Mass / rho;
        hydro.SolveFloat(volume, mass.CenterOfMass, out float heave, out float trim);
        rest = new Transform3D(Basis.FromEuler(new Vector3(trim, 0f, 0f)), new Vector3(0f, heave, 0f));
        buoyancyCenter = hydro.ComputeStatics(rest).Buoyancy;

        // Lunghezza al galleggiamento, pescaggio, bordo libero dalla posa d'equilibrio.
        float minZ = float.MaxValue, maxZ = float.MinValue, bottom = float.MaxValue, top = float.MinValue;
        foreach (Vector3 v in hydro.Vertices)
        {
            Vector3 w = rest * v;
            bottom = Mathf.Min(bottom, w.Y);
            top = Mathf.Max(top, w.Y);
            if (w.Y < 0f)
            {
                minZ = Mathf.Min(minZ, w.Z);
                maxZ = Mathf.Max(maxZ, w.Z);
            }
        }
        float lwl = Mathf.Max(maxZ - minZ, 0f);
        float gm = hydro.RightingArm(volume, mass.CenterOfMass, Mathf.DegToRad(2f)) / Mathf.Sin(Mathf.DegToRad(2f));
        float gzMax = 0f, gzMaxAt = 0f, vanish = -1f;
        for (int a = 10; a <= 120; a += 10)
        {
            float gz = hydro.RightingArm(volume, mass.CenterOfMass, Mathf.DegToRad(a));
            if (gz > gzMax)
            {
                gzMax = gz;
                gzMaxAt = a;
            }
            if (vanish < 0f && gz < 0f)
                vanish = a;
        }
        HydroCoefficients k = _config.Hydro ?? new HydroCoefficients();
        Vector3 inertia = mass.Inertia;
        float rollPeriod = gm > 0f
            ? Mathf.Tau * Mathf.Sqrt(inertia.Z * (1f + k.AddedInertia.X) / (mass.Mass * g * gm))
            : 0f;
        Aabb box = hydro.Bounds;
        float kRoll = Mathf.Sqrt(inertia.Z / mass.Mass) / Mathf.Max(box.Size.X, 0.01f);
        float kPitch = Mathf.Sqrt(inertia.X / mass.Mass) / Mathf.Max(box.Size.Z, 0.01f);

        string bollard = "";
        EngineDefinition? engine = _config.Motor?.Engine ?? _config.Inboard?.Engine;
        GearboxDefinition? gearbox = _config.Motor?.Gearbox ?? _config.Inboard?.Gearbox;
        PropellerDefinition? prop = _config.Motor?.Propeller ?? _config.Inboard?.Propeller;
        if (engine != null && gearbox != null && prop != null)
        {
            float pull = PropulsionUnit.EstimateBollard(engine, gearbox, prop, rho, out float rpm);
            float pitchSpeed = engine.RatedRpm / 60f / gearbox.RatioForward * prop.Pitch / 0.514444f;
            bollard = $"\nPunto fisso stimato {pull / g:0} kgf a {rpm:0} giri/min · velocita' di passo {pitchSpeed:0.0} nodi"; // i18n-ignore
        }

        Vector3 c = mass.CenterOfMass;
        return // i18n-ignore (rapporto per chi tara la barca nell'editor, non testo di gioco)
            $"Massa {mass.Mass:0} kg · G ({c.X:+0.00;-0.00}, {c.Y:+0.00;-0.00}, {c.Z:+0.00;-0.00}) m\n" +
            $"Galleggiamento: pescaggio {-bottom:0.00} m, bordo libero max {top:0.00} m, assetto {Mathf.RadToDeg(trim):+0.0;-0.0}° (+ = prua su)\n" +
            $"Al galleggiamento {lwl:0.0} m → velocita' di carena {2.43f * Mathf.Sqrt(lwl):0.0} nodi\n" +
            $"GM {gm:0.00} m · rollio stimato {rollPeriod:0.00} s · GZ max {gzMax:0.00} m a {gzMaxAt:0}° · {(vanish > 0f ? $"si capovolge oltre ~{vanish:0}°" : "raddrizzante fino a 120°")}\n" +
            $"Raggi d'inerzia: rollio {kRoll:0.00}·B, beccheggio {kPitch:0.00}·L (tipici 0,35-0,45·B e 0,22-0,28·L)\n" +
            $"Mesh idrodinamica: {hydro.TriangleCount} triangoli, volume {hydro.Volume:0.00} m³ (riserva ×{hydro.Volume / volume:0.0})" +
            bollard;
    }

    private void AddMarker(string name, Vector3 position, float radius, Color color)
    {
        AddChild(new MeshInstance3D
        {
            Name = GeneratedPrefix + name,
            Mesh = new SphereMesh { Radius = radius, Height = radius * 2f, RadialSegments = 12, Rings = 6 },
            MaterialOverride = PreviewMaterial(color, true),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Position = position,
        });
    }

    private static StandardMaterial3D PreviewMaterial(Color color, bool onTop) => new()
    {
        AlbedoColor = color,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = color.A < 1f ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        NoDepthTest = onTop,
    };

    private void Clear(RigidBody3D boat, Node3D visual)
    {
        foreach (Node child in GetChildren())
        {
            if (child.Name.ToString().StartsWith(GeneratedPrefix))
            {
                RemoveChild(child);
                child.QueueFree();
            }
        }
        foreach (Node child in visual.GetChildren())
        {
            if (child.Name.ToString().StartsWith(GeneratedPrefix))
            {
                visual.RemoveChild(child);
                child.QueueFree();
            }
        }
        if (boat.GetNodeOrNull<Node>("Deck") is Node deck)
        {
            foreach (Node child in deck.GetChildren())
            {
                if (child.Name.ToString().StartsWith(GeneratedPrefix))
                {
                    deck.RemoveChild(child);
                    child.QueueFree();
                }
            }
        }
        Motor = null;
        _rudderNode = null;
        _inboardProp = null;
    }
}
