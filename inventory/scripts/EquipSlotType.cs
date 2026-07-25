namespace Lagoon;

/// <summary>
/// Slot di equipaggiamento del giocatore. Gli slot arma (Fase 3, shooting) sono definiti qui ma
/// restano riservati e non funzionali in Fase 2: esistono solo come destinazioni di equip valide.
///
/// ATTENZIONE: i valori interi sono serializzati sulla rete (chiavi equipment / RPC di equip).
/// Non riordinare gli enum senza aggiornare eventuali risorse .tres che ne salvano il valore int.
/// </summary>
public enum EquipSlotType
{
    None = 0,
    Head = 1,
    Torso = 2,
    Legs = 3,
    Feet = 4,
    Vest = 5,
    Backpack = 6,
    WeaponPrimary = 7,
    WeaponSecondary = 8,
    Sidearm = 9,

    /// Contenitore sicuro (stile Tarkov): piccola griglia sempre indossata.
    /// Aggiunto in coda proprio perche' i valori sono serializzati.
    SecureContainer = 10,
}
