using System.Collections.Generic;
using Godot;
using TokisanGames;

namespace Lagoon;

/// <summary>
/// Streaming delle region di Terrain3D attorno ai bersagli: la mappa vera (1260 km², ~350 region
/// da 2 km) non sta in memoria, quindi si caricano solo le region entro <see cref="Radius"/> da un
/// bersaglio e si scaricano quelle che escono (con un anello di isteresi).
///
/// Terrain3D 1.1 non fa streaming da solo (arriverà in 1.2: PR #1004/#1059). Questo nodo usa la sua
/// API pubblica (<c>AddRegion</c>/<c>RemoveRegionl</c>), che dalla 1.1 non ricompila lo shader a ogni
/// cambio: si potrà sostituire con lo streaming ufficiale senza toccare i dati.
///
/// Bersagli: i Node3D nel gruppo <see cref="TargetGroup"/>; se non ce n'è nessuno, la camera attiva.
/// Nell'editor (è un [Tool]) segue la camera della vista 3D, con un raggio più piccolo.
///
/// Rete (CLAUDE.md §3): il terreno è un dato statico identico su ogni peer, nessuna replica. Ogni
/// peer carica attorno ai propri bersagli. L'host dovrà mettere nel gruppo anche i giocatori remoti
/// e gli NPC per averne le collisioni: oggi la collisione dinamica di Terrain3D segue UN solo
/// bersaglio (<c>collision_target</c>) e i corpi lontani da esso non hanno terreno sotto.
///
/// Mantiene anche la maschera delle region caricate che il terreno lontano (far_terrain.gdshader)
/// usa per non disegnarsi dove c'è già il terreno vero.
/// </summary>
[Tool]
public partial class TerrainStreamer : Node
{
    public const string TargetGroup = "terrain_stream_target";

    [Export] public NodePath TerrainPath { get; set; } = "../Terrain3D";
    /// Cartella dei <c>terrain3d*.res</c> generati da terrain_import.gd.
    [Export(PropertyHint.Dir)] public string RegionDirectory { get; set; } = "res://world/maps/laguna/terrain";
    /// Region caricate attorno a ogni bersaglio: un quadrato di (2r+1)² (1 → 3×3 = 6 km di lato;
    /// oltre si vede il terreno lontano). In gioco lo imposta <see cref="WorldRenderSettings"/> dalla
    /// distanza di visione (livelli e misure nella skill venice-terrain §5). La ricostruzione degli
    /// array di Terrain3D costa in proporzione alle region caricate. Cambiarlo a partita in corso
    /// carica/scarica al giro successivo, senza aspettare <see cref="UpdateInterval"/>.
    [Export(PropertyHint.Range, "0,6")]
    public int Radius
    {
        get => _radius;
        set
        {
            if (value == _radius)
                return;
            _radius = value;
            _timer = 0f;
        }
    }
    private int _radius = 1;
    [Export] public bool StreamInEditor { get; set; } = true;
    [Export(PropertyHint.Range, "0,4")] public int EditorRadius { get; set; } = 1;
    /// Region inserite al massimo per frame. Inserimenti e rimozioni dello stesso frame costano
    /// UNA sola ricostruzione degli array di Terrain3D (vedi <see cref="_mapsDirty"/>).
    [Export(PropertyHint.Range, "1,8")] public int MaxInsertionsPerFrame { get; set; } = 4;
    /// Caricamenti da disco in corso contemporaneamente (thread del ResourceLoader).
    [Export(PropertyHint.Range, "1,8")] public int MaxConcurrentLoads { get; set; } = 3;
    [Export] public float UpdateInterval { get; set; } = 0.25f;
    [Export] public NodePath FarTerrainPath { get; set; } = "../FarTerrain";
    /// Griglia di region coperta dalla maschera e dall'atlante del terreno lontano (estremi compresi).
    [Export] public Vector2I MaskRegionMin { get; set; } = new(-12, -12);
    [Export] public Vector2I MaskRegionMax { get; set; } = new(11, 11);
    /// Stampa in console bersagli, caricamenti e scaricamenti (collaudo).
    [Export] public bool DebugLog { get; set; }

    private Terrain3D? _terrain;
    private Terrain3DData? _data;
    private int _regionSize = 2048;
    private readonly Dictionary<Vector2I, string> _available = new();
    private readonly HashSet<Vector2I> _loaded = new();
    private readonly Dictionary<Vector2I, string> _pending = new();
    private readonly List<Vector2> _targets = new();
    private float _timer;
    private Image? _mask;
    private ImageTexture? _maskTexture;
    private bool _maskDirty;
    /// Terrain3D 1.1 ricostruisce TUTTI gli array (quote, controllo, colore di ogni region
    /// caricata: ~1 GB con 35 region) a ogni aggiunta o rimozione con update = true: decine di
    /// ricostruzioni per ogni confine attraversato davano scatti di secondi e picchi di VRAM
    /// fino a 16 GB. Si aggiunge e si toglie con update = false e si ricostruisce una volta
    /// a fine frame.
    private bool _mapsDirty;

    /// Region caricate adesso (per debug e collaudo).
    public int LoadedCount => _loaded.Count;
    public int PendingCount => _pending.Count;
    public int AvailableCount => _available.Count;

    public override void _Ready()
    {
        if (Engine.IsEditorHint() && !StreamInEditor)
            return;
        Node? node = GetNodeOrNull(TerrainPath);
        if (node == null)
        {
            GD.PushWarning($"[TerrainStreamer] Terrain3D non trovato in {TerrainPath}");
            return;
        }
        _terrain = Terrain3D.Bind(node);
        _data = _terrain.Data;
        // Terrain3D 1.1 nasce con region da 256 m: il nodo deve avere la stessa region_size
        // delle region su disco (2048), altrimenti le location non corrispondono.
        _regionSize = (int)_terrain.RegionSize;
        ScanRegions();
        // Color map compressa (S3TC) e senza la copia non compressa: 1/4 della memoria per
        // region. Come region_size, il valore della scena non sopravvive all'inizializzazione.
        _terrain.ColorCompressMode = (long)Terrain3D.CompressMode.S3tc;
        _terrain.KeepUncompressedColor = false;
        foreach (string path in _available.Values)
        {
            using Resource probe = ResourceLoader.Load(path, "", ResourceLoader.CacheMode.Ignore);
            int onDisk = probe.Get(RegionSizeProperty).AsInt32();
            if (onDisk != _regionSize && _data.RegionLocations.Count == 0)
            {
                // Il valore scritto nella scena non sopravvive all'inizializzazione senza
                // data_directory: lo si impone qui, prima di aggiungere qualunque region.
                _terrain.RegionSize = (Terrain3D.RegionSizeEnum)onDisk;
                _regionSize = (int)_terrain.RegionSize;
            }
            if (onDisk != _regionSize)
                GD.PushError($"[TerrainStreamer] region_size del nodo Terrain3D = {_regionSize}, region su disco = {onDisk}");
            break;
        }
        SetupMask();
        if (!Engine.IsEditorHint())
            LoadAroundTargetsNow(1);
        GD.Print($"[TerrainStreamer] {_available.Count} region disponibili in {RegionDirectory}");
    }

    public override void _Process(double delta)
    {
        if (_data == null)
            return;
        InsertFinishedLoads();
        FollowActiveCamera();
        _timer -= (float)delta;
        if (_timer <= 0f)
        {
            _timer = UpdateInterval;
            CollectTargets();
            if (_targets.Count > 0)
                UpdateWanted();
            else if (DebugLog)
                GD.Print("[TerrainStreamer] nessun bersaglio");
        }
        if (_mapsDirty)
        {
            _data.UpdateMaps();
            _mapsDirty = false;
        }
        if (_maskDirty && _mask != null && _maskTexture != null)
        {
            _maskTexture.Update(_mask);
            _maskDirty = false;
        }
    }

    /// <summary>
    /// Terrain3D prende la camera una volta sola e non la cambia finche' resta valida: con la camera
    /// d'anteprima della mappa ancora nell'albero, maglia di dettaglio e collisione dinamica
    /// restavano li' e l'avatar locale cadeva attraverso le isole. In gioco gli si passa sempre la
    /// camera attiva. (La collisione resta attorno a UN solo punto: vedi la lacuna multiplayer.)
    /// </summary>
    private void FollowActiveCamera()
    {
        if (Engine.IsEditorHint() || _terrain == null)
            return;
        if (GetViewport().GetCamera3D() is Camera3D cam && _terrain.GetCamera() != cam)
            _terrain.SetCamera(cam);
    }

    /// Elenco delle region su disco: il nome del file dice la location (terrain3d-04_08.res = (-4, 8)).
    private void ScanRegions()
    {
        _available.Clear();
        using DirAccess? dir = DirAccess.Open(RegionDirectory);
        if (dir == null)
        {
            GD.PushWarning($"[TerrainStreamer] Cartella delle region assente: {RegionDirectory}");
            return;
        }
        foreach (string file in dir.GetFiles())
        {
            // Nei pacchetti esportati i .res possono comparire come .res.remap.
            string name = file.EndsWith(".remap") ? file[..^6] : file;
            if (!name.StartsWith("terrain3d") || !name.EndsWith(".res") || name.Length != "terrain3d-00-00.res".Length)
                continue;
            if (!int.TryParse(name.Substring(10, 2), out int x) || !int.TryParse(name.Substring(13, 2), out int z))
                continue;
            if (name[9] == '-') x = -x;
            if (name[12] == '-') z = -z;
            _available[new Vector2I(x, z)] = RegionDirectory.PathJoin(name);
        }
    }

    private void SetupMask()
    {
        Vector2I size = MaskRegionMax - MaskRegionMin + Vector2I.One;
        _mask = Image.CreateEmpty(size.X, size.Y, false, Image.Format.R8);
        _maskTexture = ImageTexture.CreateFromImage(_mask);
        if (GetNodeOrNull(FarTerrainPath) is GeometryInstance3D far && far.MaterialOverride is ShaderMaterial mat)
            mat.SetShaderParameter("loaded_mask", _maskTexture);
        else if (GetNodeOrNull(FarTerrainPath) is MeshInstance3D mesh && mesh.Mesh?.SurfaceGetMaterial(0) is ShaderMaterial surfaceMat)
            surfaceMat.SetShaderParameter("loaded_mask", _maskTexture);
    }

    private void SetMask(Vector2I loc, bool loaded)
    {
        if (_mask == null)
            return;
        Vector2I p = loc - MaskRegionMin;
        if (p.X < 0 || p.Y < 0 || p.X >= _mask.GetWidth() || p.Y >= _mask.GetHeight())
            return;
        _mask.SetPixel(p.X, p.Y, loaded ? Colors.White : Colors.Black);
        _maskDirty = true;
    }

    private void CollectTargets()
    {
        _targets.Clear();
        if (Engine.IsEditorHint())
        {
#if TOOLS
            Camera3D? cam = EditorInterface.Singleton.GetEditorViewport3D(0)?.GetCamera3D();
            if (cam != null)
                _targets.Add(new Vector2(cam.GlobalPosition.X, cam.GlobalPosition.Z));
#endif
            return;
        }
        foreach (Node n in GetTree().GetNodesInGroup(TargetGroup))
        {
            if (n is Node3D t && t.IsInsideTree())
                _targets.Add(new Vector2(t.GlobalPosition.X, t.GlobalPosition.Z));
        }
        if (_targets.Count == 0 && GetViewport().GetCamera3D() is Camera3D active)
            _targets.Add(new Vector2(active.GlobalPosition.X, active.GlobalPosition.Z));
    }

    private Vector2I RegionOf(Vector2 p) =>
        new(Mathf.FloorToInt(p.X / _regionSize), Mathf.FloorToInt(p.Y / _regionSize));

    /// Distanza (in region, metrica di Chebyshev) dal bersaglio più vicino.
    private int RegionDistance(Vector2I loc)
    {
        int best = int.MaxValue;
        foreach (Vector2 t in _targets)
        {
            Vector2I r = RegionOf(t);
            best = Mathf.Min(best, Mathf.Max(Mathf.Abs(loc.X - r.X), Mathf.Abs(loc.Y - r.Y)));
        }
        return best;
    }

    private void UpdateWanted()
    {
        int radius = Engine.IsEditorHint() ? EditorRadius : Radius;

        // Scarica: fuori dal raggio più un anello di isteresi (avanti e indietro sul confine
        // non deve ricaricare la stessa region).
        var drop = new List<Vector2I>();
        foreach (Vector2I loc in _loaded)
            if (RegionDistance(loc) > radius + 1)
                drop.Add(loc);
        foreach (Vector2I loc in drop)
        {
            EvictRegion(loc);
            _loaded.Remove(loc);
            SetMask(loc, false);
        }
        if (drop.Count > 0)
        {
            // Le Variant C# con cui la region è passata a Terrain3D tengono un riferimento
            // all'oggetto nativo fino al loro finalizzatore: senza una raccolta la RAM sale di
            // ~50 MB per region attraversata (misurato: 1,4 -> 7,5 GB in 40 km e crash). Una
            // raccolta ogni gruppo di evict (circa ogni 2 km percorsi) costa pochi ms.
            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
            if (DebugLog)
                GD.Print($"[TerrainStreamer] - {drop.Count} region, caricate {_loaded.Count}, " +
                    $"RAM {OS.GetStaticMemoryUsage() / 1048576} MB");
        }

        // Carica: le mancanti dentro il raggio, dalla più vicina.
        var wanted = new List<(int dist, Vector2I loc)>();
        foreach (Vector2 t in _targets)
        {
            Vector2I c = RegionOf(t);
            for (int z = c.Y - radius; z <= c.Y + radius; z++)
                for (int x = c.X - radius; x <= c.X + radius; x++)
                {
                    var loc = new Vector2I(x, z);
                    if (_available.ContainsKey(loc) && !_loaded.Contains(loc) && !_pending.ContainsKey(loc))
                        wanted.Add((RegionDistance(loc), loc));
                }
        }
        wanted.Sort((a, b) => a.dist.CompareTo(b.dist));
        foreach ((int _, Vector2I loc) in wanted)
        {
            if (_pending.Count >= MaxConcurrentLoads)
                break;
            if (_pending.ContainsKey(loc))
                continue;
            string path = _available[loc];
            Error req = ResourceLoader.LoadThreadedRequest(path);
            if (req == Error.Ok)
                _pending[loc] = path;
            else if (DebugLog)
                GD.Print($"[TerrainStreamer] richiesta rifiutata {path}: {req}");
        }
    }

    private void InsertFinishedLoads()
    {
        if (_pending.Count == 0)
            return;
        int inserted = 0;
        var finished = new List<Vector2I>();
        foreach ((Vector2I loc, string path) in _pending)
        {
            ResourceLoader.ThreadLoadStatus status = ResourceLoader.LoadThreadedGetStatus(path);
            if (status == ResourceLoader.ThreadLoadStatus.InProgress)
                continue;
            finished.Add(loc);
            if (status != ResourceLoader.ThreadLoadStatus.Loaded)
            {
                GD.PushWarning($"[TerrainStreamer] Caricamento fallito: {path}");
                _available.Remove(loc);
                continue;
            }
            Resource res = ResourceLoader.LoadThreadedGet(path);
            // Se nel frattempo il bersaglio si è allontanato, la si butta senza inserirla.
            int radius = Engine.IsEditorHint() ? EditorRadius : Radius;
            if (RegionDistance(loc) <= radius + 1)
                InsertRegion(loc, res);
            else
                res.Dispose();
            if (++inserted >= MaxInsertionsPerFrame)
                break;
        }
        foreach (Vector2I loc in finished)
            _pending.Remove(loc);
    }

    /// <summary>
    /// Toglie la region dal terreno E dalla memoria. <c>remove_region</c> di Terrain3D 1.1 la segna
    /// solo come cancellata: resta in <c>_regions</c> finché un <c>save_region</c> non la toglie,
    /// cancellando però anche il suo file su disco. Il trucco: il salvataggio si fa in una cartella
    /// che non contiene region (<see cref="EvictDirectory"/>), così Terrain3D la libera e non trova
    /// nessun file da cancellare. Senza questo la RAM cresce a ogni region attraversata (7,5 GB e
    /// crash per memoria esaurita nel collaudo). Da rivedere con lo streaming ufficiale (1.2).
    /// </summary>
    private void EvictRegion(Vector2I loc)
    {
        _data!.RemoveRegionl(loc, false);
        _mapsDirty = true;
        _data.SaveRegion(loc, EvictDirectory, false, (long)Terrain3D.CompressMode.None);
    }

    private static readonly StringName LocationProperty = "location";
    private static readonly StringName RegionSizeProperty = "region_size";
    private static readonly StringName AddRegionMethod = "add_region";

    /// Cartella SENZA region: serve solo a <see cref="EvictRegion"/>. Non deve mai essere quella dei dati.
    private const string EvictDirectory = "user://terrain_evict_never_contains_regions";

    private void InsertRegion(Vector2I loc, Resource res)
    {
        // Niente Terrain3DRegion.Bind: il wrapper attacca uno script C# all'oggetto nativo e lo
        // tiene vivo anche dopo l'evict (RAM +50 MB per region attraversata nel collaudo). Sulla
        // region bastano Set e Call; il riferimento C# si rilascia subito, resta quello di Terrain3D.
        res.Set(LocationProperty, loc);   // non è salvata nel file: la dà il nome
        Error err = _data!.Call(AddRegionMethod, res, false).As<Error>();
        _mapsDirty = true;
        res.Dispose();
        if (DebugLog)
            GD.Print($"[TerrainStreamer] + {loc} ({err}), caricate {_loaded.Count + 1}");
        _loaded.Add(loc);
        SetMask(loc, true);
    }

    /// All'avvio: le region sotto i bersagli si caricano subito e in modo bloccante, così
    /// nessuno nasce sopra il vuoto. Il resto arriva in streaming.
    private void LoadAroundTargetsNow(int radius)
    {
        CollectTargets();
        if (DebugLog)
            GD.Print($"[TerrainStreamer] avvio: {_targets.Count} bersagli {string.Join(" ", _targets)}");
        bool any = false;
        foreach (Vector2 t in _targets)
        {
            Vector2I c = RegionOf(t);
            for (int z = c.Y - radius; z <= c.Y + radius; z++)
                for (int x = c.X - radius; x <= c.X + radius; x++)
                {
                    var loc = new Vector2I(x, z);
                    if (!_available.TryGetValue(loc, out string? path) || _loaded.Contains(loc))
                        continue;
                    InsertRegion(loc, ResourceLoader.Load(path));
                    any = true;
                }
        }
        if (any)
        {
            _data!.UpdateMaps();
            _mapsDirty = false;
        }
    }
}
