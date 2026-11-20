namespace Lagoon;

/// <summary>
/// I 15 slot di equipaggiamento in stile Tarkov (i tre "special" contano come uno nell'elenco
/// canonico). Quali categorie accetta ogni slot e' deciso da <see cref="EquipRules"/>.
///
/// ATTENZIONE: i valori interi viaggiano in rete (indirizzi, <c>WeaponController.HeldSlot</c>) e
/// sono usati da <c>tools/playtest_fps.gd</c>. Si appende in coda, non si riordina. I valori 3 e 4
/// (gambe/piedi del vecchio POC) sono ritirati e non vanno riusati.
/// </summary>
public enum EquipSlotType
{
    None = 0,
    Headwear = 1,
    BodyArmor = 2,
    // 3, 4: ritirati.
    TacticalRig = 5,
    Backpack = 6,
    OnSling = 7,
    OnBack = 8,
    Holster = 9,
    SecureContainer = 10,
    Earpiece = 11,
    FaceCover = 12,
    Armband = 13,
    Eyewear = 14,
    Scabbard = 15,
    Pockets = 16,
    Special1 = 17,
    Special2 = 18,
    Special3 = 19,
    // Abbigliamento: solo estetico, senza griglie (colonna a destra degli slot in InventoryScreen).
    Top = 20,
    Legs = 21,
    Footwear = 22,
    Gloves = 23,
}
