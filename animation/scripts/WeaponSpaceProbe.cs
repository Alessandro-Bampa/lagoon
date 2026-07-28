using Godot;

namespace Lagoon;

/// <summary>
/// Misura quanto spazio ha la canna davanti a se'.
///
/// Serve a non far attraversare i muri all'arma. Con la camera isometrica ci si accosta di
/// continuo agli spigoli, e un'arma lunga rigidamente attaccata alla mano entra nella geometria:
/// e' il difetto piu' visibile che resta quando presa e mira funzionano gia'. La reazione — alzare
/// la canna e ritrarre l'arma, cioe' il "port arms" — e' procedurale apposta: dipende dalla
/// distanza dall'ostacolo, che nessuna clip puo' conoscere.
///
/// Non e' un nodo e non tocca niente: MISURA e basta. Chi applica il risultato e' l'unico
/// proprietario della trasformata di presa, <see cref="WeaponGripRig"/> — due scrittori sulla
/// stessa trasformata si cancellerebbero a vicenda senza dare errori.
///
/// Pura resa (CLAUDE.md §3): gira su ogni peer, non produce stato di gioco.
/// </summary>
public sealed class WeaponSpaceProbe
{
    private readonly Node3D _owner;
    private float _blocked;

    /// <param name="owner">Nodo da cui si ricava il mondo fisico da interrogare.</param>
    public WeaponSpaceProbe(Node3D owner) => _owner = owner;

    /// <summary>
    /// Quanto e' ostruita la canna, da 0 (libera) a 1 (a contatto). E' smorzato: senza, passando
    /// davanti a uno spigolo l'arma scatterebbe su e giu' a ogni frame.
    /// </summary>
    public float Blocked => _blocked;

    /// <summary>
    /// Aggiorna la misura lanciando un raggio dal punto di presa lungo l'asse dell'arma.
    /// </summary>
    /// <param name="grip">Punto di presa: origine e direzione (+Z locale) del raggio.</param>
    /// <param name="length">Lunghezza dell'arma in metri, cioe' quanto sporge la canna.</param>
    /// <param name="clearance">Margine oltre la volata entro cui si considera gia' ostruito.</param>
    /// <param name="responseSpeed">Velocita' di reazione, in frazione al secondo.</param>
    public void Update(Node3D grip, float length, float clearance, float responseSpeed, float dt)
    {
        float reach = length + clearance;
        float target = 0f;

        if (reach > 0.01f)
        {
            Vector3 origin = grip.GlobalPosition;
            var query = PhysicsRayQueryParameters3D.Create(
                origin,
                origin + grip.GlobalBasis.Z.Normalized() * reach,
                CollisionLayers.World | CollisionLayers.Vehicles | CollisionLayers.VehicleDeck);

            Godot.Collections.Dictionary hit = _owner.GetWorld3D().DirectSpaceState.IntersectRay(query);
            if (hit.Count > 0)
            {
                // Quanto e' vicino l'ostacolo rispetto alla lunghezza dell'arma: a contatto vale 1,
                // al limite della portata 0. Continuo, non a soglia, cosi' l'arma si alza pian piano
                // avvicinandosi al muro invece di scattare quando lo tocca.
                float distance = origin.DistanceTo((Vector3)hit["position"]);
                target = Mathf.Clamp(1f - distance / reach, 0f, 1f);
            }
        }

        _blocked = Mathf.Lerp(_blocked, target, 1f - Mathf.Exp(-responseSpeed * dt));
    }
}
