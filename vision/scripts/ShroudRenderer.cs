using Godot;

namespace Lagoon;

/// <summary>
/// Disegna lo shroud: scurisce a schermo cio' che sta fuori dal campo visivo dell'avatar locale.
/// Va montato come figlio della <c>PlayerCamera</c>, cosi' il quad la segue senza calcoli.
///
/// SOLO AVATAR LOCALE, e non e' un'ottimizzazione ma una condizione di correttezza: un
/// <c>MeshInstance3D</c> sotto la camera di un avatar REMOTO verrebbe comunque disegnato dalla MIA
/// camera. Con quattro giocatori si otterrebbero quattro quad fullscreen sovrapposti, ciascuno con
/// lo shroud centrato su un altro giocatore.
///
/// Non nasconde nulla: scurire non e' nascondere. Un nemico dentro l'ombra resta visibile, solo
/// piu' scuro. A farlo sparire e' <see cref="VisibilityGate"/>, che e' un sistema separato e
/// interroga la stessa <see cref="VisionSource"/>.
///
/// NOTA su <c>PlayerNetworkSync</c>: quello sovrascrive <c>MaterialOverride</c> su tutte le mesh
/// del rig per colorare gli avatar. Oggi itera solo il sottoalbero di <c>Visual</c>, quindi questo
/// quad e' al sicuro — ma se un giorno passasse a iterare l'intero Player, colorerebbe lo shroud.
/// </summary>
public partial class ShroudRenderer : Node3D
{
    /// Percorso dello shader. Unico punto in cui compare.
    private const string ShaderPath = "res://vision/shaders/Shroud.gdshader";

    /// Quanto si scurisce fuori dal campo visivo (vedi la uniform omonima nello shader).
    [Export(PropertyHint.Range, "0,1,0.01")] public float Darkness { get; set; } = 0.55f;

    /// Sfumatura del bordo, in metri.
    [Export] public float EdgeSoftness { get; set; } = 0.9f;

    /// Tinta della zona in ombra (moltiplicatore lineare, non un colore sRGB).
    [Export] public Color ShroudTint { get; set; } = new(0.80f, 0.88f, 1.0f);

    /// Spegnibile a caldo per confrontare con/senza durante la taratura.
    [Export] public bool Enabled { get; set; } = true;

    private VisionSource _vision = null!;
    private CharacterMotor _motor = null!;
    private ShaderMaterial _material = null!;
    private MeshInstance3D _quad = null!;
    private ImageTexture _radiiTexture = null!;
    private Image _radiiImage = null!;
    private float[] _pixels = [];

    public override void _Ready()
    {
        // Il root del Player, non il genitore immediato (che e' la camera).
        Node3D player = GetParent<Node3D>().GetParent<Node3D>();

        if (!player.IsMultiplayerAuthority())
        {
            SetProcess(false);
            return;
        }

        _motor = (CharacterMotor)player;
        _vision = player.GetNode<VisionSource>("Vision");

        BuildMask();
        BuildQuad();
    }

    /// <summary>
    /// Texture polare: una riga di raggi, larga quanto il ventaglio. Si crea UNA volta; poi si
    /// aggiorna in place. Ricrearla ogni frame allocherebbe una RID nuova a ogni frame.
    /// </summary>
    private void BuildMask()
    {
        // Si legge RayCount e non Radii.Length: RayCount e' un [Export], quindi valido subito,
        // mentre Radii viene allocato in VisionSource._Ready e questo nodo non deve dipendere
        // dall'ordine di _Ready fra due rami diversi della scena.
        int width = Mathf.Max(_vision.RayCount, 8);
        _pixels = new float[width];

        _radiiImage = Image.CreateEmpty(width, 1, false, Image.Format.Rf);
        _radiiTexture = ImageTexture.CreateFromImage(_radiiImage);
    }

    private void BuildQuad()
    {
        _material = new ShaderMaterial
        {
            Shader = GD.Load<Shader>(ShaderPath),
            // Ultimo nella passata trasparente: sopra acqua, traccianti e particelle.
            RenderPriority = 127,
        };
        _material.SetShaderParameter("radii_tex", _radiiTexture);
        _material.SetShaderParameter("radius_scale", _vision.MaxRange);

        _quad = new MeshInstance3D
        {
            Name = "ShroudQuad",
            // Size (1,1): lo shader moltiplica VERTEX.xy per 2 per coprire il clip space.
            Mesh = new QuadMesh { Size = Vector2.One },
            MaterialOverride = _material,
            // Obbligatorio: la passata d'ombra non esegue lo stesso vertex() e produrrebbe
            // un'ombra vagante proiettata dal quad.
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            // Con POSITION sovrascritta la geometria non sta dove Godot crede: senza questi il
            // quad viene cullato e sparisce a intermittenza.
            ExtraCullMargin = 16384f,
            IgnoreOcclusionCulling = true,
            CustomAabb = new Aabb(new Vector3(-1e4f, -1e4f, -1e4f), new Vector3(2e4f, 2e4f, 2e4f)),
        };

        AddChild(_quad);
    }

    /// <summary>
    /// In <c>_Process</c> e non in <c>_PhysicsProcess</c>: l'origine deve combaciare con la
    /// posizione che la camera usa per disegnare QUESTO frame, altrimenti la maschera vibra a
    /// 60 Hz rispetto alla geometria. Il ventaglio, invece, resta nel passo di fisica.
    /// </summary>
    public override void _Process(double delta)
    {
        Vector3 eye = _motor.ResolvedSyncPosition;
        _material.SetShaderParameter("origin_xz", new Vector2(eye.X, eye.Z));
        _material.SetShaderParameter("darkness", Darkness);
        _material.SetShaderParameter("edge_softness", EdgeSoftness);
        _material.SetShaderParameter("shroud_tint", new Vector3(ShroudTint.R, ShroudTint.G, ShroudTint.B));
        _material.SetShaderParameter("enabled", Enabled);

        UploadRadii();
    }

    private void UploadRadii()
    {
        float[] radii = _vision.Radii;
        if (radii.Length != _pixels.Length)
            return;

        float scale = _vision.MaxRange;
        if (scale <= 0f)
            return;

        for (int i = 0; i < radii.Length; i++)
            _pixels[i] = radii[i] / scale;

        _radiiImage.SetData(_pixels.Length, 1, false, Image.Format.Rf, FloatsToBytes(_pixels));
        _radiiTexture.Update(_radiiImage);
    }

    private static byte[] FloatsToBytes(float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        System.Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
