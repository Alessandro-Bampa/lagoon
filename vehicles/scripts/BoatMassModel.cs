using System.Collections.Generic;
using Godot;

namespace Lagoon;

/// <summary>
/// Massa, baricentro e inerzia della barca come somma di componenti (skill vehicles-boats §12):
/// scafo (guscio distribuito sulla mesh idrodinamica, con baricentro di progetto), motore, masse
/// fisse della configurazione e masse DINAMICHE (chi sta a bordo, col suo equipaggiamento).
///
/// Un personaggio sul ponte non trasmette il proprio peso al corpo rigido (il giocatore e' su un layer
/// che non spinge la barca, skill vehicles-boats §2): il peso lo si registra qui, e cosi' sbanda la
/// barca, la appoppa, ne cambia il periodo di rollio. Solo host. Tutto nello spazio della barca.
/// </summary>
public sealed class BoatMassModel
{
    private readonly struct Item
    {
        public readonly float Mass;
        public readonly Vector3 Position;
        /// Inerzia propria attorno al proprio centro (diagonale, assi della barca).
        public readonly Vector3 OwnInertia;

        public Item(float mass, Vector3 position, Vector3 ownInertia)
        {
            Mass = mass;
            Position = position;
            OwnInertia = ownInertia;
        }
    }

    private readonly List<Item> _fixed = new();
    private readonly Dictionary<int, Item> _dynamic = new();

    public float Mass { get; private set; }
    public Vector3 CenterOfMass { get; private set; }
    public Vector3 Inertia { get; private set; }
    /// Massa delle sole parti fisse (barca vuota).
    public float FixedMass { get; private set; }

    private float _appliedMass = -1f;
    private Vector3 _appliedCom;

    /// Massa puntiforme o a scatola (<paramref name="size"/> = lati).
    public void AddFixed(float mass, Vector3 position, Vector3 size = default)
    {
        if (mass <= 0f)
            return;
        _fixed.Add(new Item(mass, position, BoxInertia(mass, size)));
        Recompute();
    }

    /// <summary>
    /// Scafo come guscio: la massa si distribuisce sui triangoli della mesh idrodinamica in proporzione
    /// all'area (inerzia realistica senza lavoro a mano), poi si sposta tutta perche' il suo baricentro
    /// cada su <paramref name="center"/> (fondo, madieri e pagliolo pesano piu' delle murate).
    /// </summary>
    public void AddHullShell(float mass, HydroHull hull, Vector3 center)
    {
        if (mass <= 0f)
            return;
        Vector3 c = Vector3.Zero;
        for (int i = 0; i < hull.TriangleCount; i++)
            c += hull.Centroids[i] * hull.Areas[i];
        c /= hull.TotalArea;
        Vector3 inertia = Vector3.Zero;
        for (int i = 0; i < hull.TriangleCount; i++)
        {
            float m = mass * hull.Areas[i] / hull.TotalArea;
            Vector3 r = hull.Centroids[i] - c;
            inertia += m * new Vector3(r.Y * r.Y + r.Z * r.Z, r.X * r.X + r.Z * r.Z, r.X * r.X + r.Y * r.Y);
        }
        _fixed.Add(new Item(mass, center, inertia));
        Recompute();
    }

    /// Massa dinamica identificata da <paramref name="key"/> (es. il peer di chi sta a bordo).
    public void SetDynamic(int key, float mass, Vector3 position)
    {
        if (mass <= 0f)
            _dynamic.Remove(key);
        else
            _dynamic[key] = new Item(mass, position, Vector3.Zero);
    }

    public void RemoveDynamic(int key) => _dynamic.Remove(key);

    /// Toglie le masse dinamiche le cui chiavi non sono in <paramref name="keep"/>.
    public void RetainDynamic(HashSet<int> keep)
    {
        if (_dynamic.Count == 0)
            return;
        var stale = new List<int>();
        foreach (int key in _dynamic.Keys)
            if (!keep.Contains(key))
                stale.Add(key);
        foreach (int key in stale)
            _dynamic.Remove(key);
    }

    public void Recompute()
    {
        float m = 0f, fixedMass = 0f;
        Vector3 moment = Vector3.Zero;
        foreach (Item it in _fixed)
        {
            m += it.Mass;
            fixedMass += it.Mass;
            moment += it.Position * it.Mass;
        }
        foreach (Item it in _dynamic.Values)
        {
            m += it.Mass;
            moment += it.Position * it.Mass;
        }
        FixedMass = fixedMass;
        Mass = Mathf.Max(m, 1f);
        CenterOfMass = moment / Mass;

        // Huygens-Steiner, solo la diagonale (Godot accetta i momenti principali sugli assi locali).
        Vector3 inertia = Vector3.Zero;
        foreach (Item it in _fixed)
            inertia += it.OwnInertia + Steiner(it.Mass, it.Position - CenterOfMass);
        foreach (Item it in _dynamic.Values)
            inertia += it.OwnInertia + Steiner(it.Mass, it.Position - CenterOfMass);
        Inertia = new Vector3(Mathf.Max(inertia.X, 1e-3f), Mathf.Max(inertia.Y, 1e-3f), Mathf.Max(inertia.Z, 1e-3f));
    }

    /// <summary>
    /// Scrive massa, baricentro e inerzia sul corpo se sono cambiati abbastanza (0,5% di massa o 1 cm
    /// di baricentro), o sempre con <paramref name="force"/>. L'origine del corpo resta ferma: cambia
    /// solo il punto attorno a cui ruota.
    /// </summary>
    public bool Apply(RigidBody3D body, bool force = false)
    {
        Recompute();
        bool changed = force || _appliedMass < 0f
            || Mathf.Abs(Mass - _appliedMass) > 0.005f * _appliedMass
            || CenterOfMass.DistanceTo(_appliedCom) > 0.01f;
        if (!changed)
            return false;
        body.Mass = Mass;
        body.CenterOfMassMode = RigidBody3D.CenterOfMassModeEnum.Custom;
        body.CenterOfMass = CenterOfMass;
        body.Inertia = Inertia;
        _appliedMass = Mass;
        _appliedCom = CenterOfMass;
        return true;
    }

    private static Vector3 Steiner(float m, Vector3 r)
        => m * new Vector3(r.Y * r.Y + r.Z * r.Z, r.X * r.X + r.Z * r.Z, r.X * r.X + r.Y * r.Y);

    private static Vector3 BoxInertia(float m, Vector3 s)
        => m / 12f * new Vector3(s.Y * s.Y + s.Z * s.Z, s.X * s.X + s.Z * s.Z, s.X * s.X + s.Y * s.Y);
}
