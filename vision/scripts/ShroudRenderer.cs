using Godot;

namespace Lagoon;

/// <summary>
/// Disegna lo shroud: scurisce a schermo cio' che sta fuori dal campo visivo dell'avatar locale.
/// Va montato come figlio della <c>PlayerCamera</c>, cosi' il quad la segue senza calcoli.
///
/// Non possiede nulla del campo visivo: legge le globali pubblicate da <see cref="VisionMask"/>.
/// Qui c'e' solo il quad e la sua taratura.
///
/// SOLO AVATAR LOCALE, e non e' un'ottimizzazione ma una condizione di correttezza: un
/// <c>MeshInstance3D</c> sotto la camera di un avatar REMOTO verrebbe comunque disegnato dalla MIA
/// camera. Con quattro giocatori si otterrebbero quattro quad fullscreen sovrapposti.
///
/// Non nasconde nulla: scurire non e' nascondere. A far sparire i personaggi non visti e'
/// <see cref="VisibilityGate"/>, che e' un sistema separato e interroga la stessa
/// <see cref="VisionSource"/>.
///
/// NOTA su <c>PlayerNetworkSync</c>: quello sovrascrive <c>MaterialOverride</c> su tutte le mesh del
/// rig per colorare gli avatar. Oggi itera solo il sottoalbero di <c>Visual</c>, quindi questo quad
/// e' al sicuro — ma se un giorno passasse a iterare l'intero Player, colorerebbe lo shroud.
/// </summary>
public partial class ShroudRenderer : Node3D
{
    /// Percorso dello shader. Unico punto in cui compare.
    private const string ShaderPath = "res://vision/shaders/Shroud.gdshader";

    /// <summary>
    /// Quanto si scurisce fuori dal campo visivo. Deve restare un'OMBRA, non un nero pieno: cio' che
    /// e' fuori dalla linea di vista si intravede in penombra e si legge come atmosfera, mentre a 1.0
    /// il mondo attorno diventa una parete nera e l'inquadratura si chiude addosso al giocatore.
    /// Provato a 1.0 e scartato.
    ///
    /// Conseguenza accettata: l'interno di un edificio che si intravede attraverso una superficie
    /// aperta non e' nero, e' in penombra. Si legge la pianta della stanza anche senza vederla
    /// davvero. Se un giorno la si volesse negare, la strada NON e' alzare questo valore — che vale
    /// per tutto il mondo — ma dare allo shroud il modo di sapere quali frammenti stanno dentro un
    /// edificio, che oggi non ha.
    ///
    /// Si tara A OCCHIO e non si calcola: il quad moltiplica in HDR lineare PRIMA del tonemap Filmic,
    /// che ricomprime, quindi un x0.7 lineare non si legge come "30% piu' scuro".
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float Darkness { get; set; } = 0.55f;

    /// <summary>
    /// Sfumatura del bordo, in METRI (non in texel: e' il vantaggio della forma polare). Va tenuta
    /// LARGA: qui si sfuma una luce vera, e un bordo netto sul limite del raggio si legge come un
    /// muro invisibile. E' l'opposto della morbidezza del dither sulle superfici, che va tenuta
    /// stretta.
    /// </summary>
    [Export] public float EdgeSoftness { get; set; } = 0.9f;

    /// Tinta della zona in ombra (moltiplicatore lineare, non un colore sRGB).
    [Export] public Color ShroudTint { get; set; } = new(0.80f, 0.88f, 1.0f);

    /// Spegnibile a caldo per confrontare con/senza durante la taratura.
    [Export] public bool Enabled { get; set; } = true;

    private ShaderMaterial _material = null!;
    private MeshInstance3D _quad = null!;

    public override void _Ready()
    {
        // Il root del Player, non il genitore immediato (che e' la camera).
        Node3D player = GetParent<Node3D>().GetParent<Node3D>();

        if (!player.IsMultiplayerAuthority())
        {
            SetProcess(false);
            return;
        }

        BuildQuad();
    }

    private void BuildQuad()
    {
        _material = new ShaderMaterial
        {
            Shader = GD.Load<Shader>(ShaderPath),
            // Ultimo nella passata trasparente: sopra acqua, traccianti e particelle.
            RenderPriority = 127,
        };

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

        // Nessun Layers impostato: il quad eredita il render layer 1 (RenderLayers.Always) e deve
        // restarci. Chi tocca il CullMask della camera deve tenere quel bit sempre acceso, o la
        // nebbia sparisce del tutto.
        AddChild(_quad);
    }

    public override void _Process(double delta)
    {
        _material.SetShaderParameter("darkness", Darkness);
        _material.SetShaderParameter("edge_softness", EdgeSoftness);
        _material.SetShaderParameter("shroud_tint", new Vector3(ShroudTint.R, ShroudTint.G, ShroudTint.B));
        _material.SetShaderParameter("enabled", Enabled);
    }
}
