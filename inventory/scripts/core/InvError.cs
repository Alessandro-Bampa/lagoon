namespace Lagoon;

/// <summary>
/// Esito di un'operazione di inventario. Il modello non traduce: restituisce un codice, la UI lo
/// converte nella chiave <c>UI_INV_ERR_&lt;NOME&gt;</c>. Viaggia in rete come int: si appende in coda.
/// </summary>
public enum InvError
{
    Ok = 0,
    OutOfBounds,
    NoSpace,
    SelfContainment,
    Filter,
    FilterNested,
    Nesting,
    NoAccess,
    SlotBlocked,
    Incompatible,
    Caliber,
    MagFull,
    SwapImpossible,
    CannotEquip,
    NotFound,
    Fixed,
    NothingToDo,
}
