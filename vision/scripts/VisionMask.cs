using Godot;

namespace Lagoon;

/// <summary>
/// Pubblica il campo visivo dell'avatar LOCALE verso gli shader, sotto forma di maschera polare.
/// Va montato come figlio del root del giocatore, accanto a <c>Vision</c>.
///
/// E' l'unico proprietario della texture e l'unico scrittore delle globali di
/// <see cref="VisionGlobals"/>. I consumatori sono due e nessuno dei due la possiede:
/// <see cref="ShroudRenderer"/> la usa per sapere COSA E' AL BUIO, <c>WorldSurface.gdshader</c> per
/// sapere COSA SI APRE. Tenerla qui e non dentro lo shroud e' deliberato: la trasparenza delle
/// superfici non deve dipendere dall'esistenza di un quad di post-process.
///
/// MASCHERA POLARE, NON UN SUBVIEWPORT. I dati di partenza SONO polari — un raggio per settore
/// angolare — e rasterizzarli in una texture cartesiana per poi ricampionarli perde informazione due
/// volte, oltre a introdurre ricentraggio della finestra, texel swimming e una scelta di risoluzione.
/// Il numero che decide: 256x256 su una finestra di 64 m fa 0.25 m/texel, cioe' ~19 pixel a schermo
/// per texel, e il filtro bilineare su texel cosi' grossi fa colare la luce oltre gli spigoli dei
/// muri — proprio l'informazione tattica che il sistema esiste per negare.
///
/// Funziona perche' la forma e' STAR-SHAPED: un solo raggio per angolo. L'unione cono+bolla lo e', e
/// la linea di vista lo e' per definizione.
///
/// RETE: nessuna. Legge stato gia' replicato, non decide nulla di gioco, e le globali sono per
/// processo — legittimo perche' il progetto assume un solo avatar locale per processo, la stessa
/// invariante su cui poggia il gruppo <c>local_vision</c>.
/// </summary>
public partial class VisionMask : Node
{
    private VisionSource _vision = null!;
    private CharacterMotor _motor = null!;

    private ImageTexture _texture = null!;
    private Image _image = null!;
    private float[] _pixels = [];

    public override void _Ready()
    {
        var player = GetParent<Node3D>();

        // Correttezza, non ottimizzazione: le globali sono per processo, quindi quattro avatar che
        // le scrivono si sovrascriverebbero a vicenda e il mondo si aprirebbe attorno a un giocatore
        // a caso.
        if (!player.IsMultiplayerAuthority())
        {
            SetProcess(false);
            return;
        }

        _motor = (CharacterMotor)player;
        _vision = player.GetNode<VisionSource>("Vision");

        // Si legge RayCount e non Radii.Length: RayCount e' un [Export], quindi valido subito,
        // mentre Radii viene allocato in VisionSource._Ready e questo nodo non deve dipendere
        // dall'ordine di _Ready fra due rami diversi della scena.
        int width = Mathf.Max(_vision.RayCount, 8);
        _pixels = new float[width];

        // CreateFromImage UNA VOLTA SOLA, poi Update() in place ogni frame: ricrearla allocherebbe
        // una RID nuova a ogni frame.
        _image = Image.CreateEmpty(width, 1, false, Image.Format.Rf);
        _texture = ImageTexture.CreateFromImage(_image);

        RenderingServer.GlobalShaderParameterSet(VisionGlobals.Mask, _texture);
        RenderingServer.GlobalShaderParameterSet(VisionGlobals.Range, _vision.MaxRange);
    }

    /// <summary>
    /// In <c>_Process</c> e non in <c>_PhysicsProcess</c>: l'origine deve combaciare con la posizione
    /// che la camera usa per disegnare QUESTO frame, altrimenti la maschera vibra a 60 Hz rispetto
    /// alla geometria — e con lei il bordo dello shroud e i buchi nei muri. Il ventaglio, invece,
    /// resta nel passo di fisica: e' li' che si fanno i raycast.
    /// </summary>
    public override void _Process(double delta)
    {
        Vector3 feet = _motor.ResolvedSyncPosition;

        RenderingServer.GlobalShaderParameterSet(VisionGlobals.Origin, new Vector2(feet.X, feet.Z));
        RenderingServer.GlobalShaderParameterSet(VisionGlobals.Ground, _motor.ResolvedFeetY);

        Upload();
    }

    private void Upload()
    {
        float[] radii = _vision.Radii;
        if (radii.Length != _pixels.Length)
            return;

        float scale = _vision.MaxRange;
        if (scale <= 0f)
            return;

        for (int i = 0; i < radii.Length; i++)
            _pixels[i] = radii[i] / scale;

        _image.SetData(_pixels.Length, 1, false, Image.Format.Rf, FloatsToBytes(_pixels));
        _texture.Update(_image);
    }

    private static byte[] FloatsToBytes(float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        System.Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
