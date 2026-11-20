using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Lagoon;

/// <summary>
/// Cancelli scorrevoli del modello (vaporetto: i varchi d'imbarco). Nel .glb ogni pannello e' un nodo
/// <c>Gate_&lt;lato&gt;_&lt;pannello&gt;</c> nella posa CHIUSA con accanto il vuoto <c>&lt;nome&gt;_Open</c>
/// nella posa APERTA; i pannelli con lo stesso lato formano un cancello (un bit dello stato).
///
/// Solo resa e collisione, uguali su ogni peer: lo stato aperto/chiuso lo decide l'host
/// (<see cref="BoatController.SyncGateOpenMask"/>, replicato) e qui lo si insegue. Ogni pannello ha
/// una scatola sul corpo <c>Deck</c> (layer 6) che si sposta con lui: cancello chiuso = parapetto.
/// </summary>
public sealed class BoatGates
{
    public const string Prefix = "Gate_";
    public const string OpenSuffix = "_Open";

    /// Secondi per aprire o chiudere del tutto (i pannelli telescopici fanno corse diverse nello stesso tempo).
    public const float TravelSeconds = 2.0f;

    private sealed class Panel
    {
        public required Node3D Node;
        public required Transform3D Closed;
        public required Transform3D Open;
        public required int Group;
        public CollisionShape3D? Collider;
        public Aabb Box;
        public Transform3D ParentToDeck;
    }

    private readonly List<Panel> _panels = new();
    private readonly List<string> _groups = new();
    private readonly List<Vector3> _centers = new();
    private float[] _progress = System.Array.Empty<float>();

    /// Numero di cancelli (bit usati dello stato).
    public int Count => _groups.Count;

    /// Punto di manovra del cancello nello spazio della barca (centro del varco, ~1 m sopra il ponte:
    /// la quota del centro della capsula del giocatore).
    public Vector3 Center(int gate) => _centers[gate];

    public string GroupName(int gate) => _groups[gate];

    /// <summary>
    /// Raccoglie i pannelli dallo scafo istanziato. <paramref name="toBoat"/> porta lo spazio del
    /// genitore dei pannelli in quello della barca. Null se il modello non ha cancelli.
    /// </summary>
    public static BoatGates? Collect(Node3D hull, System.Func<Node3D, Transform3D> parentToBoat)
    {
        var found = new List<(Node3D Node, Node3D Open, string Group)>();
        foreach (Node node in hull.FindChildren(Prefix + "*", "Node3D", true, false))
        {
            if (node is not Node3D panel || panel.Name.ToString().EndsWith(OpenSuffix))
                continue;
            if (panel.GetParent()?.GetNodeOrNull<Node3D>(panel.Name + OpenSuffix) is not Node3D open)
            {
                GD.PushWarning($"[BoatGates] '{panel.Name}' senza '{panel.Name}{OpenSuffix}': pannello fisso.");
                continue;
            }
            string[] parts = panel.Name.ToString().Split('_');
            found.Add((panel, open, parts.Length > 1 ? parts[1] : panel.Name.ToString()));
        }
        if (found.Count == 0)
            return null;

        var gates = new BoatGates();
        // Ordine deterministico (per nome): il bit N e' lo stesso cancello su ogni peer.
        gates._groups.AddRange(found.Select(f => f.Group).Distinct().OrderBy(g => g, System.StringComparer.Ordinal));
        foreach ((Node3D node, Node3D open, string group) in found.OrderBy(f => f.Node.Name.ToString(), System.StringComparer.Ordinal))
        {
            var panel = new Panel
            {
                Node = node,
                Closed = node.Transform,
                Open = open.Transform,
                Group = gates._groups.IndexOf(group),
            };
            panel.Box = node is MeshInstance3D mesh && mesh.Mesh != null ? mesh.Mesh.GetAabb() : new Aabb(Vector3.Zero, new Vector3(0.05f, 1.0f, 1.1f));
            gates._panels.Add(panel);
        }
        foreach (string group in gates._groups)
        {
            int g = gates._groups.IndexOf(group);
            Vector3 sum = Vector3.Zero;
            int n = 0;
            foreach (Panel p in gates._panels.Where(p => p.Group == g))
            {
                Transform3D toBoat = parentToBoat(p.Node) * p.Closed;
                sum += toBoat * p.Box.GetCenter();
                n++;
            }
            gates._centers.Add(sum / Mathf.Max(n, 1) + Vector3.Up * 0.45f);
        }
        gates._progress = new float[gates._groups.Count];
        return gates;
    }

    /// <summary>
    /// Una scatola di collisione per pannello sul corpo <paramref name="deck"/> (solo in gioco).
    /// <paramref name="parentToDeck"/> porta lo spazio del genitore del pannello in quello del Deck.
    /// </summary>
    public void CreateColliders(AnimatableBody3D deck, System.Func<Node3D, Transform3D> parentToDeck, string namePrefix)
    {
        foreach (Panel p in _panels)
        {
            // Un po' piu' spessa del tubo: la capsula non deve infilarsi fra le sbarre.
            Vector3 size = p.Box.Size;
            size.X = Mathf.Max(size.X, 0.08f);
            p.ParentToDeck = parentToDeck(p.Node);
            p.Collider = new CollisionShape3D
            {
                Name = namePrefix + p.Node.Name,
                Shape = new BoxShape3D { Size = size },
            };
            deck.AddChild(p.Collider);
            Place(p);
        }
    }

    /// <summary>
    /// Insegue lo stato replicato: bit acceso = aperto. Ogni peer muove pannelli e scatole da se'.
    /// </summary>
    public void Apply(int openMask, double delta)
    {
        float step = (float)delta / TravelSeconds;
        for (int g = 0; g < _progress.Length; g++)
        {
            float target = (openMask & (1 << g)) != 0 ? 1f : 0f;
            _progress[g] = Mathf.MoveToward(_progress[g], target, step);
        }
        foreach (Panel p in _panels)
        {
            float t = Mathf.SmoothStep(0f, 1f, _progress[p.Group]);
            Transform3D pose = p.Closed.InterpolateWith(p.Open, t);
            if (p.Node.Transform.IsEqualApprox(pose))
                continue;
            p.Node.Transform = pose;
            Place(p);
        }
    }

    private static void Place(Panel p)
    {
        if (p.Collider != null)
            p.Collider.Transform = p.ParentToDeck * p.Node.Transform * new Transform3D(Basis.Identity, p.Box.GetCenter());
    }
}
