namespace Lagoon;

/// <summary>
/// Comandi agli attuatori della barca, decisi dall'host dall'intento del pilota (CLAUDE.md §3). Gli
/// attuatori hanno poi i loro limiti di velocita' (sterzo, timone, trim, invertitore).
/// </summary>
public struct BoatCommands
{
    /// Leva unica −1..1 (±10% = folle), posizione della manetta (vedi ThrottleNotches).
    public float Throttle;
    /// Sterzo/timone −1..1: positivo = vira a dritta.
    public float Steer;
    /// Trim del fuoribordo: −1 giu' (trim in), +1 su (trim out), 0 fermo.
    public float Trim;
    /// Elica di prua −1..1: positivo = prua a dritta.
    public float Thruster;
    /// Trim lasciato all'assistenza (funzione della velocita').
    public bool AutoTrim;
    /// Motore acceso (c'e' un pilota).
    public bool Running;
}
