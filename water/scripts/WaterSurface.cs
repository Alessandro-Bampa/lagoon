using System;
using Godot;

namespace Lagoon;

/// <summary>Qualita' della resa dell'acqua: costo della griglia e dei riflessi.</summary>
public enum WaterQuality
{
    /// ~100k vertici, niente riflessi in screen space (solo cielo).
    Low,
    /// ~170k vertici, SSR a 12 passi.
    Medium,
    /// ~280k vertici, SSR a 24 passi.
    High,
}

/// <summary>
/// Resa dell'acqua (solo presentazione, su ogni peer): la griglia che segue la camera, le uniformi
/// dello shader a ogni frame, il colore della zona e il velo sott'acqua. Non decide nulla: legge lo
/// stato da <see cref="WaterVolume"/> (fratello o padre). Nessuna replica.
///
/// <para><b>Griglia agganciata.</b> La mesh (<see cref="WaterGridMesh"/>) si sposta con la camera a
/// multipli del passo interno: i vertici del centro cadono sempre sugli stessi punti del mondo, cosi'
/// le onde non "nuotano" vicino al giocatore. Lontano i vertici scivolano, ma li' lo shader ha gia'
/// spento le onde troppo corte per la griglia.</para>
///
/// <para><b>Colore per zona.</b> Ogni mezzo secondo si guarda la zona sotto la camera
/// (<see cref="WaterMap.ZoneAt"/>) e si sceglie la <see cref="WaterPalette"/> che la elenca; tutte le
/// uniformi di colore vi sfumano in <see cref="PaletteBlendSeconds"/>. Sulla terraferma resta quella
/// dell'ultima acqua.</para>
/// </summary>
[Tool]
public partial class WaterSurface : Node3D
{
    [Export] public NodePath WaterPath { get; set; } = "..";
    [Export] public ShaderMaterial? SurfaceMaterial { get; set; }
    [Export] public ShaderMaterial? UnderwaterMaterial { get; set; }

    [Export] public WaterQuality Quality
    {
        get => _quality;
        set
        {
            if (_quality == value)
                return;
            _quality = value;
            if (IsInsideTree())
                RebuildMesh();
        }
    }

    /// Fin dove arriva la griglia attorno alla camera (m). Oltre il <c>far</c> della camera non serve.
    [Export] public float Extent { get; set; } = 30000f;

    [ExportGroup("Colore per zona")]
    [Export] public WaterPalette[] Palettes { get; set; } = Array.Empty<WaterPalette>();
    /// Tavolozza senza mappa delle zone, o finche' la camera non ha mai visto acqua mappata.
    [Export] public WaterPalette? DefaultPalette { get; set; }
    [Export] public float PaletteBlendSeconds { get; set; } = 6f;

    private WaterQuality _quality = WaterQuality.High;

    private WaterVolume? _water;
    // Copie dei materiali: le uniformi cambiano a ogni frame e non devono finire nella scena salvata
    // (nell'editor si scriverebbero nel .tscn a ogni salvataggio).
    private ShaderMaterial? _surfaceMat;
    private ShaderMaterial? _underMat;
    private MeshInstance3D _mesh = null!;
    private MeshInstance3D _underwater = null!;
    private float _snapStep = 0.5f;

    private WaterPalette? _targetPalette;
    private Color _scatter;
    private Vector3 _absorption;
    private float _turbidity;
    private float _foamPalette;
    private bool _paletteInitialized;
    private double _zoneTimer;

    /// Solo editor: frequenza massima degli aggiornamenti dovuti all'orologio (onde animate).
    private const double EditorRefreshHz = 30.0;
    private double _editorOriginX = double.NaN, _editorOriginZ = double.NaN;
    private int _editorState;
    private ulong _editorLastUpdateMsec;

    private readonly Vector4[] _waveA = new Vector4[WaveSpectrum.MaxWaves];
    private readonly Vector4[] _waveB = new Vector4[WaveSpectrum.MaxWaves];

    private static readonly StringName PWaveCount = "wave_count", PWaveA = "wave_a", PWaveB = "wave_b",
        PLevel = "water_level", PGridOrigin = "grid_origin", PTileOrigin = "tile_origin", PClip = "clip_rect",
        PHasMap = "has_exposure_map", PMap = "exposure_map", PMapRect = "map_rect", PDefaultExposure = "default_exposure",
        PScatter = "scatter_color", PAbsorption = "absorption", PTurbidity = "turbidity", PFoamPalette = "foam_palette",
        PDetailScroll = "detail_scroll", PDetailStrength = "detail_strength", PDetailTiling = "detail_tiling",
        PCrestFoam = "crest_foam_amount", PSsrSteps = "ssr_steps", PCameraUnderwater = "camera_underwater";

    public override void _Ready() => Initialize();

    /// <summary>
    /// Crea griglia, velo e copie dei materiali. Si richiama anche a meta' vita: nell'editor, quando
    /// l'assembly C# si ricarica (a ogni build), i campi privati dei nodi <c>[Tool]</c> si azzerano e
    /// <c>_Ready</c> non riparte; i figli creati prima invece restano, quindi si tolgono.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_mesh))]
    private void Initialize()
    {
        foreach (Node child in GetChildren())
            if (child.Name == "Grid" || child.Name == "Underwater")
            {
                RemoveChild(child);
                child.QueueFree();
            }

        _water = GetNodeOrNull<WaterVolume>(WaterPath);
        _surfaceMat = SurfaceMaterial?.Duplicate() as ShaderMaterial;
        _underMat = UnderwaterMaterial?.Duplicate() as ShaderMaterial;

        _mesh = new MeshInstance3D
        {
            Name = "Grid",
            TopLevel = true,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = _surfaceMat,
        };
        AddChild(_mesh);

        _underwater = new MeshInstance3D
        {
            Name = "Underwater",
            TopLevel = true,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Mesh = new QuadMesh { Size = new Vector2(2f, 2f) },
            MaterialOverride = _underMat,
            // Sempre a schermo intero (POSITION nello shader): l'AABB non deve mai farlo scartare.
            ExtraCullMargin = 16384f,
            Visible = false,
        };
        AddChild(_underwater);

        RebuildMesh();
        if (_water?.Map is WaterMap map && _surfaceMat != null)
        {
            Rect2 r = map.WorldRect;
            _surfaceMat.SetShaderParameter(PHasMap, true);
            _surfaceMat.SetShaderParameter(PMap, map.ExposureTexture);
            _surfaceMat.SetShaderParameter(PMapRect, new Vector4(r.Position.X, r.Position.Y, 1f / r.Size.X, 1f / r.Size.Y));
        }
    }

    private void RebuildMesh()
    {
        if (_mesh == null)
            return;
        var layout = _quality switch
        {
            WaterQuality.Low => new WaterGridMesh.Layout(1.0f, 20f, 1.06f, Extent),
            WaterQuality.Medium => new WaterGridMesh.Layout(0.5f, 20f, 1.05f, Extent),
            _ => new WaterGridMesh.Layout(0.5f, 20f, 1.035f, Extent),
        };
        _snapStep = layout.InnerSpacing;
        _mesh.Mesh = WaterGridMesh.Build(layout);
        _surfaceMat?.SetShaderParameter(PSsrSteps, _quality switch
        {
            WaterQuality.Low => 0,
            WaterQuality.Medium => 12,
            _ => 24,
        });
    }

    public override void _Process(double delta)
    {
        if (_mesh == null)
            Initialize();
        if (_water == null || _surfaceMat == null || _water.Spectrum == null)
            return;

        Camera3D? cam = ActiveCamera();
        if (cam == null)
            return;

        Vector3 camPos = cam.GlobalPosition;
        double ox = Math.Floor(camPos.X / _snapStep) * _snapStep;
        double oz = Math.Floor(camPos.Z / _snapStep) * _snapStep;
        if (Engine.IsEditorHint() && !EditorNeedsUpdate(ox, oz))
            return;
        _mesh.GlobalTransform = new Transform3D(Basis.Identity, new Vector3((float)ox, 0f, (float)oz));

        double time = _water.RenderTime;
        WaveSpectrum spectrum = _water.Spectrum;
        UpdateWaves(spectrum, ox, oz, time);
        UpdateDetail(spectrum, ox, oz, time);
        UpdatePalette(camPos, delta);
        UpdateUnderwater(cam, camPos, time);
    }

    private Camera3D? ActiveCamera()
    {
#if TOOLS
        if (Engine.IsEditorHint())
            return EditorInterface.Singleton.GetEditorViewport3D(0)?.GetCamera3D();
#endif
        return GetViewport().GetCamera3D();
    }

    /// <summary>
    /// Solo editor. Ogni scrittura su un materiale fa ridisegnare la vista 3D, e l'editor ridisegna
    /// solo se qualcosa cambia: riscrivendo le uniformi a ogni frame la vista restava a ~140 fps fissi
    /// (misura del 19/11/2026, MainLaguna: GPU all'80 % con l'editor da solo, al 100 % con il gioco).
    /// Si aggiorna quindi quando la griglia scatta (camera spostata: subito, sennò si vedrebbe
    /// rincorrere la camera) o quando cambia lo stato che si vede (orologio, livello, mare, banco
    /// d'onde, rettangolo dell'acqua), al piu' a <see cref="EditorRefreshHz"/>. Con le onde ferme
    /// (<see cref="WaterVolume.AnimateInEditor"/> spento, o gioco avviato) l'orologio non cambia e la
    /// vista resta immobile finche' non la si tocca.
    /// </summary>
    private bool EditorNeedsUpdate(double ox, double oz)
    {
        bool moved = ox != _editorOriginX || oz != _editorOriginZ;
        WaterVolume water = _water!;
        int state = HashCode.Combine(water.RenderTime, water.WaterLevel, water.SeaState,
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(water.Spectrum),
            water.DefaultExposure, water.ExtentXZ, water.GlobalPosition);
        ulong now = Time.GetTicksMsec();
        bool due = state != _editorState && now - _editorLastUpdateMsec >= 1000.0 / EditorRefreshHz;
        if (!moved && !due)
            return false;

        _editorOriginX = ox;
        _editorOriginZ = oz;
        _editorState = state;
        _editorLastUpdateMsec = now;
        return true;
    }

    private void UpdateWaves(WaveSpectrum spectrum, double ox, double oz, double time)
    {
        int count = spectrum.Count;
        for (int i = 0; i < WaveSpectrum.MaxWaves; i++)
        {
            if (i >= count)
            {
                _waveA[i] = Vector4.Zero;
                _waveB[i] = Vector4.Zero;
                continue;
            }
            ref readonly WaveSpectrum.Component c = ref spectrum.Components[i];
            _waveA[i] = new Vector4((float)c.DirX, (float)c.DirZ, (float)c.K, spectrum.ShaderPhase(i, ox, oz, time));
            _waveB[i] = new Vector4(spectrum.OpenAmplitude[i], spectrum.OpenHorizontal[i],
                spectrum.ShelteredAmplitude[i], spectrum.ShelteredHorizontal[i]);
        }

        Vector3 origin = _water!.GlobalPosition;
        Vector2 half = _water.ExtentXZ * 0.5f;
        _surfaceMat!.SetShaderParameter(PWaveCount, count);
        _surfaceMat.SetShaderParameter(PWaveA, _waveA);
        _surfaceMat.SetShaderParameter(PWaveB, _waveB);
        _surfaceMat.SetShaderParameter(PLevel, _water.WaterLevel);
        _surfaceMat.SetShaderParameter(PGridOrigin, new Vector2((float)ox, (float)oz));
        _surfaceMat.SetShaderParameter(PTileOrigin, new Vector2((float)PositiveMod(ox, 1024.0), (float)PositiveMod(oz, 1024.0)));
        _surfaceMat.SetShaderParameter(PClip, new Vector4(origin.X - half.X, origin.Z - half.Y, origin.X + half.X, origin.Z + half.Y));
        if (_water.Map == null)
            _surfaceMat.SetShaderParameter(PDefaultExposure, _water.DefaultExposure);
    }

    /// <summary>
    /// Scorrimento delle texture di dettaglio: offset gia' avvolti, calcolati in double (il TIME dello
    /// shader ricomincia da 0 ogni ora e perde precisione). Le due texture vanno in versi diversi,
    /// lungo e di traverso al vento: il moto non si legge come un nastro che scorre.
    /// </summary>
    private void UpdateDetail(WaveSpectrum spectrum, double ox, double oz, double time)
    {
        Vector2 tiling = (Vector2)_surfaceMat!.GetShaderParameter(PDetailTiling);
        ref readonly WaveSpectrum.Component lead = ref spectrum.Components[spectrum.Count - 1];
        double wx = lead.DirX, wz = lead.DirZ;
        double ax = PositiveMod(wx * time * 0.35 / tiling.X, 1.0);
        double az = PositiveMod(wz * time * 0.35 / tiling.X, 1.0);
        double bx = PositiveMod((-wz * 0.6 + wx * 0.4) * time * 0.9 / tiling.Y, 1.0);
        double bz = PositiveMod((wx * 0.6 + wz * 0.4) * time * 0.9 / tiling.Y, 1.0);
        _surfaceMat.SetShaderParameter(PDetailScroll, new Vector4((float)ax, (float)az, (float)bx, (float)bz));
        _surfaceMat.SetShaderParameter(PDetailStrength, spectrum.Current.DetailStrength);
        _surfaceMat.SetShaderParameter(PCrestFoam, spectrum.Current.CrestFoam);
    }

    private void UpdatePalette(Vector3 camPos, double delta)
    {
        if (Engine.IsEditorHint())
        {
            // Nell'editor niente sfumatura (sarebbero altri frame da ridisegnare): la zona si guarda a
            // ogni aggiornamento, che arriva solo quando la camera si sposta, e il colore scatta.
            _zoneTimer = 0.0;
            _paletteInitialized = false;
        }
        _zoneTimer -= delta;
        if (_zoneTimer <= 0.0 || _targetPalette == null)
        {
            _zoneTimer = 0.5;
            _targetPalette = PaletteAt(camPos) ?? _targetPalette ?? DefaultPalette;
        }
        if (_targetPalette == null)
            return;

        if (!_paletteInitialized)
        {
            _scatter = _targetPalette.ScatterColor;
            _absorption = _targetPalette.Absorption;
            _turbidity = _targetPalette.Turbidity;
            _foamPalette = _targetPalette.FoamAmount;
            _paletteInitialized = true;
        }
        else
        {
            // Avvicinamento esponenziale: a PaletteBlendSeconds si e' al 95%.
            float t = 1f - Mathf.Exp(-3f * (float)delta / Mathf.Max(PaletteBlendSeconds, 0.01f));
            _scatter = _scatter.Lerp(_targetPalette.ScatterColor, t);
            _absorption = _absorption.Lerp(_targetPalette.Absorption, t);
            _turbidity = Mathf.Lerp(_turbidity, _targetPalette.Turbidity, t);
            _foamPalette = Mathf.Lerp(_foamPalette, _targetPalette.FoamAmount, t);
        }

        foreach (ShaderMaterial? mat in new[] { _surfaceMat, _underMat })
        {
            if (mat == null)
                continue;
            mat.SetShaderParameter(PScatter, _scatter);
            mat.SetShaderParameter(PAbsorption, _absorption);
            mat.SetShaderParameter(PTurbidity, _turbidity);
        }
        _surfaceMat!.SetShaderParameter(PFoamPalette, _foamPalette);
    }

    /// <summary>
    /// Tavolozza della zona sotto la camera. Se la camera non e' sull'acqua (un'isola, una fondamenta)
    /// si cerca l'acqua piu' vicina su due anelli (40 e 120 m); null = nessuna acqua: resta la corrente.
    /// </summary>
    private WaterPalette? PaletteAt(Vector3 p)
    {
        WaterMap? map = _water?.Map;
        if (map == null)
            return null;

        int zone = map.ZoneAt(p.X, p.Z);
        if (!WaterMap.IsWaterClass(zone))
        {
            zone = WaterMap.NoClass;
            foreach (float radius in new[] { 40f, 120f })
            {
                for (int k = 0; k < 8 && zone == WaterMap.NoClass; k++)
                {
                    float a = k * Mathf.Tau / 8f;
                    int z = map.ZoneAt(p.X + Mathf.Cos(a) * radius, p.Z + Mathf.Sin(a) * radius);
                    if (WaterMap.IsWaterClass(z))
                        zone = z;
                }
                if (zone != WaterMap.NoClass)
                    break;
            }
        }
        if (zone == WaterMap.NoClass)
            return null;

        foreach (WaterPalette palette in Palettes)
            if (palette != null && Array.IndexOf(palette.ClassIds, zone) >= 0)
                return palette;
        return DefaultPalette;
    }

    private void UpdateUnderwater(Camera3D cam, Vector3 camPos, double time)
    {
        if (_underMat == null || Engine.IsEditorHint())
            return;

        bool inside = _water!.Contains(camPos);
        float surface = inside ? _water.Sample(camPos, time).Height : float.NegativeInfinity;
        // Un margine pari al piano vicino: con la camera a filo, meta' schermo e' gia' sott'acqua.
        bool under = camPos.Y < surface - cam.Near;
        _underwater.Visible = under;
        _surfaceMat?.SetShaderParameter(PCameraUnderwater, under);
        if (!under)
            return;

        _underwater.GlobalPosition = camPos;
        _underMat.SetShaderParameter(PLevel, surface);
    }

    private static double PositiveMod(double value, double period)
    {
        double m = value % period;
        return m < 0 ? m + period : m;
    }
}
