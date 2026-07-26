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

    /// <summary>
    /// Scafo dei veicoli (il <c>RigidBody3D</c>). Ferma i proiettili come la geometria statica, ma
    /// <b>non deve entrare in <see cref="PlayerBodyMask"/></b>: in Godot la collisione e' simmetrica
    /// (basta che UNA delle due maschere veda il layer dell'altro), quindi un giocatore che vedesse
    /// questo layer urterebbe lo scafo e Jolt — che tratta un <c>CharacterBody3D</c> come massa
    /// infinita — spingerebbe via la barca. Per questo il ponte sta su un layer separato.
    /// </summary>
    public const uint Vehicles = 1 << 4;

    /// Ponte e parapetti dei veicoli (<c>AnimatableBody3D</c>, cinematico): la superficie su cui i
    /// giocatori camminano. Separato da <see cref="Vehicles"/> proprio per la ragione sopra.
    public const uint VehicleDeck = 1 << 5;

    /// Maschera usata da ogni query di mira/tiro: si ferma sul mondo e sui veicoli, danneggia sulle
    /// hitbox. L'acqua non compare qui perche' non ha alcun corpo fisico (vedi WaterVolume).
    public const uint AimMask = World | Hitbox | Vehicles | VehicleDeck;

    /// Maschera di collisione del corpo di un giocatore: mondo, altri corpi e ponte dei veicoli —
    /// <b>mai</b> lo scafo. NOTA: le scene scrivono il valore come letterale, quindi cambiare qui non
    /// basta, va aggiornato anche <c>collision_mask</c> in <c>player/scenes/Player.tscn</c>.
    public const uint PlayerBodyMask = World | Players | Enemies | VehicleDeck;
}
