using Godot;

namespace Lagoon;

/// <summary>
/// Arbitro dell'azione <c>interact</c> (F): l'UNICO posto che decide se il tasto spetta a un oggetto a
/// terra o a un veicolo. Nessuno stato.
///
/// Esiste perche' F era gia' di <c>PlayerHud</c>, che lo consuma in <c>_Input</c>. La regola della skill
/// ui-hud §4 e' che chi ha il contesto piu' specifico consuma SOLO quando quel contesto e' attivo: qui
/// si stabilisce quando lo e'. Nessun tasto nuovo, come per R (rotate_item / reload).
/// </summary>
public static class VehicleInteraction
{
    /// <summary>
    /// True se F deve andare al veicolo invece che al pickup.
    ///
    /// <paramref name="pickupDistance"/> e' la distanza dal pickup candidato, oppure
    /// <see cref="float.MaxValue"/> se non ce n'e' nessuno a portata.
    /// </summary>
    public static bool VehicleWins(PlayerController player, float pickupDistance)
    {
        // Al timone vince sempre il veicolo: nessun oggetto a terra puo' rubare l'azione "scendi".
        if (player.Mode == PlayerMode.Driving)
            return true;

        BoatController? helm = VehicleRegistry.NearestHelm(
            player, player.GlobalPosition, out float helmDistance);

        // A parita' non esiste: senza candidato veicolo vince il pickup; con entrambi vince il piu'
        // vicino. Se nessuno dei due e' a portata, nessuno consuma l'evento — e F a vuoto non viene
        // piu' ingoiato dalla HUD.
        return helm != null && helmDistance < pickupDistance;
    }
}
