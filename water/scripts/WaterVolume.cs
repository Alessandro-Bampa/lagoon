using Godot;

namespace Lagoon;

/// <summary>
/// Superficie d'acqua PIATTA a quota costante (Fase 4): nessuna onda, e soprattutto **nessun corpo
/// fisico**.
///
/// L'assenza di collider e' una scelta, non una semplificazione da colmare: senza corpo non serve un
/// collision layer per l'acqua, non c'e' niente da escludere da <see cref="CollisionLayers.ShotMask"/>
/// e un raggio di mira non puo' agganciare il piano d'acqua. Il galleggiamento ha bisogno di un solo
/// numero, <see cref="SurfaceY"/>, e la mesh trasparente e' pura presentazione.
///
/// Nessuno stato replicato: la superficie e' identica su ogni peer per costruzione (sta nel livello).
/// </summary>
public partial class WaterVolume : Node3D
{
    /// Gruppo per raggiungere l'acqua senza path fragili (come <see cref="BoatController.GroupName"/>).
    public const string GroupName = "water";

    /// Quota della superficie in coordinate MONDO. La posizione del nodo non conta: conta questo valore.
    [Export] public float SurfaceY { get; set; }

    /// Estensione orizzontale (larghezza X, profondita' Z) centrata sul nodo. Serve solo a
    /// <see cref="Contains"/>: fuori da questa area la barca non riceve spinta.
    [Export] public Vector2 ExtentXZ { get; set; } = new(60f, 60f);

    public override void _Ready()
    {
        AddToGroup(GroupName);
    }

    /// <summary>
    /// True se il punto cade dentro l'area orizzontale d'acqua. La quota non viene controllata:
    /// la profondita' di immersione la calcola il chiamante da <see cref="SurfaceY"/>.
    /// </summary>
    public bool Contains(Vector3 globalPoint)
    {
        Vector3 origin = GlobalPosition;
        return Mathf.Abs(globalPoint.X - origin.X) <= ExtentXZ.X * 0.5f
            && Mathf.Abs(globalPoint.Z - origin.Z) <= ExtentXZ.Y * 0.5f;
    }

    /// <summary>
    /// Prima superficie d'acqua del livello (o null). Con piu' laghi andra' cercata per contenimento;
    /// in questa fase ce n'e' una sola.
    /// </summary>
    public static WaterVolume? Find(Node context)
    {
        foreach (Node node in context.GetTree().GetNodesInGroup(GroupName))
        {
            if (node is WaterVolume water)
                return water;
        }
        return null;
    }
}
