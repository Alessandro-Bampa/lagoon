namespace Lagoon;

/// <summary>
/// Maschere dei collision layer 3D, speculari alla sezione <c>[layer_names]</c> di
/// <c>project.godot</c>. Nessuno stato: solo costanti, per non spargere numeri magici nelle scene
/// e nelle query fisiche.
///
/// Lo schema esiste per il sistema di tiro (Fase 3): con tutto sul layer 1 un raycast di mira
/// colpirebbe la capsula di chi spara e qualunque corpo di passaggio. Separando le superfici
/// colpibili (<see cref="Hitbox"/>, <c>Area3D</c> dedicate) dai corpi fisici, la mira interroga solo
/// <see cref="AimMask"/> e l'immunita' a se' stessi si ottiene escludendo la propria hitbox.
/// </summary>
public static class CollisionLayers
{
    /// Geometria statica del livello (pavimento, muri, ostacoli). Ferma i proiettili.
    public const uint World = 1 << 0;

    /// Corpi fisici dei giocatori (<c>CharacterBody3D</c>). NON colpibili dai raggi: servono solo
    /// alla collisione di movimento.
    public const uint Players = 1 << 1;

    /// Corpi fisici dei nemici/manichini. Come sopra: collisione, non danno.
    public const uint Enemies = 1 << 2;

    /// Volumi danneggiabili (<see cref="HitboxComponent"/>). Non interrogano mai nulla: vengono solo
    /// interrogati dai raggi di tiro.
    public const uint Hitbox = 1 << 3;

    /// Maschera usata da ogni query di mira/tiro: si ferma sul mondo, danneggia sulle hitbox.
    public const uint AimMask = World | Hitbox;

    /// Maschera di collisione del corpo di un giocatore (si scontra col mondo e con gli altri corpi).
    public const uint PlayerBodyMask = World | Players | Enemies;
}
