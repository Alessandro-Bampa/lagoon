using System;
using Godot;

namespace Lagoon;

/// <summary>
/// Dati spaziali dell'acqua di una mappa, generati da <c>addons/venice_builder/tools/water_map.py</c>
/// a partire dalle classi OSM (skill venice-terrain §2):
/// <list type="bullet">
/// <item><b>zone</b> (16 m/px): id della classe d'acqua prevalente (mare, laguna, rio, ...). Le legge
/// solo la presentazione (<see cref="WaterSurface"/>) per scegliere la tavolozza dei colori.</item>
/// <item><b>esposizione</b> (32 m/px, 0..1): quanto un punto e' esposto alle onde. La leggono IN MODO
/// IDENTICO lo shader e <see cref="WaterVolume.Sample"/>: bilineare, centri dei texel a +0,5, bordi
/// bloccati. Se le due letture divergono, le barche galleggiano su onde diverse da quelle disegnate.</item>
/// </list>
/// Classe pura (non un nodo): la possiede <see cref="WaterVolume"/>.
/// </summary>
public sealed class WaterMap
{
    /// Classe che non e' acqua (o fuori dalla mappa): vedi <see cref="IsWaterClass"/>.
    public const int NoClass = 255;

    /// Id d'acqua della tabella CLASSES di osm_terrain.py.
    public static bool IsWaterClass(int id) => id is 0 or 2 or 3 or 4 or 10 or 11 or 12 or 13;

    /// Rettangolo mondo coperto: (x0, z0) e dimensioni in metri.
    public Rect2 WorldRect { get; }

    /// Immagine dell'esposizione (L8): lo shader la riceve come <see cref="ExposureTexture"/>.
    public Image ExposureImage { get; }

    private readonly byte[] _zones;
    private readonly int _zoneW, _zoneH;
    private readonly float[] _exposure;
    private readonly int _expW, _expH;

    private ImageTexture? _exposureTexture;

    private WaterMap(Rect2 rect, Image zones, Image exposure)
    {
        WorldRect = rect;
        _zoneW = zones.GetWidth();
        _zoneH = zones.GetHeight();
        _zones = zones.GetData();

        ExposureImage = exposure;
        _expW = exposure.GetWidth();
        _expH = exposure.GetHeight();
        byte[] raw = exposure.GetData();
        _exposure = new float[raw.Length];
        // Stessa quantizzazione della GPU: un R8 unorm vale byte / 255.
        for (int i = 0; i < raw.Length; i++)
            _exposure[i] = raw[i] / 255f;
    }

    /// Texture per lo shader, creata alla prima richiesta (sulla CPU dell'host senza schermo non serve).
    public ImageTexture ExposureTexture => _exposureTexture ??= ImageTexture.CreateFromImage(ExposureImage);

    /// <summary>
    /// Carica <c>water_map.json</c>, <c>water_zones.png</c> e <c>water_exposure.png</c> dalla cartella.
    /// Null (con un avviso) se mancano: il livello funziona lo stesso, con esposizione uniforme.
    /// </summary>
    public static WaterMap? Load(string directory)
    {
        if (string.IsNullOrEmpty(directory))
            return null;

        string jsonPath = directory.PathJoin("water_map.json");
        if (!FileAccess.FileExists(jsonPath))
        {
            GD.PushWarning($"[WaterMap] {jsonPath} non trovato: esposizione uniforme. "
                + "Rigenera con addons/venice_builder/tools/water_map.py.");
            return null;
        }

        using FileAccess file = FileAccess.Open(jsonPath, FileAccess.ModeFlags.Read);
        var json = Json.ParseString(file.GetAsText()).AsGodotDictionary();
        var r = json["world_rect"].AsFloat32Array();
        var rect = new Rect2(r[0], r[1], r[2] - r[0], r[3] - r[1]);

        Image? zones = LoadImage(directory.PathJoin("water_zones.png"));
        Image? exposure = LoadImage(directory.PathJoin("water_exposure.png"));
        if (zones == null || exposure == null)
            return null;

        if (zones.GetFormat() != Image.Format.L8)
            zones.Convert(Image.Format.L8);
        if (exposure.GetFormat() != Image.Format.L8)
            exposure.Convert(Image.Format.L8);

        GD.Print($"[WaterMap] Caricata {directory}: zone {zones.GetWidth()}x{zones.GetHeight()}, "
            + $"esposizione {exposure.GetWidth()}x{exposure.GetHeight()}.");
        return new WaterMap(rect, zones, exposure);
    }

    /// Prima come risorsa importata (<c>importer="image"</c>, funziona anche esportato), poi dal file.
    private static Image? LoadImage(string path)
    {
        if (ResourceLoader.Exists(path, "Image") && ResourceLoader.Load<Image>(path) is Image img)
            return (Image)img.Duplicate();

        if (FileAccess.FileExists(path))
        {
            Image fromFile = Image.LoadFromFile(ProjectSettings.GlobalizePath(path));
            if (fromFile != null && !fromFile.IsEmpty())
                return fromFile;
        }

        GD.PushWarning($"[WaterMap] Impossibile leggere {path}.");
        return null;
    }

    /// <summary>
    /// Esposizione alle onde nel punto mondo (x, z), bilineare. DEVE restare identica a
    /// <c>exposure_at</c> di water.gdshader (filter_linear, repeat_disable).
    /// </summary>
    public float ExposureAt(double x, double z)
    {
        double u = (x - WorldRect.Position.X) / WorldRect.Size.X * _expW - 0.5;
        double v = (z - WorldRect.Position.Y) / WorldRect.Size.Y * _expH - 0.5;
        int x0 = (int)Math.Floor(u), y0 = (int)Math.Floor(v);
        float fx = (float)(u - x0), fy = (float)(v - y0);

        float e00 = TexelExposure(x0, y0), e10 = TexelExposure(x0 + 1, y0);
        float e01 = TexelExposure(x0, y0 + 1), e11 = TexelExposure(x0 + 1, y0 + 1);
        return Mathf.Lerp(Mathf.Lerp(e00, e10, fx), Mathf.Lerp(e01, e11, fx), fy);
    }

    private float TexelExposure(int x, int y)
    {
        x = Math.Clamp(x, 0, _expW - 1);
        y = Math.Clamp(y, 0, _expH - 1);
        return _exposure[y * _expW + x];
    }

    /// Classe (id di osm_terrain.py) della cella da 16 m che contiene il punto; <see cref="NoClass"/> fuori.
    public int ZoneAt(double x, double z)
    {
        int px = (int)Math.Floor((x - WorldRect.Position.X) / WorldRect.Size.X * _zoneW);
        int py = (int)Math.Floor((z - WorldRect.Position.Y) / WorldRect.Size.Y * _zoneH);
        if (px < 0 || py < 0 || px >= _zoneW || py >= _zoneH)
            return NoClass;
        return _zones[py * _zoneW + px];
    }
}
