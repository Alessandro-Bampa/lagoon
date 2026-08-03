namespace Lagoon;

/// <summary>
/// Maschere dei collision layer 3D, speculari alla sezione <c>[layer_names]</c> di
/// <c>project.godot</c>. Nessuno stato: solo costanti, per non spargere numeri magici nelle scene
/// e nelle query fisiche.
///
/// Lo schema esiste per il sistema di tiro (Fase 3): con tutto sul layer 1 un raycast di mira
/// colpirebbe la capsula di chi spara e qualunque corpo di passaggio. Separando le superfici
/// colpibili (<see cref="Hitbox"/>, <c>Area3D</c> dedicate) dai corpi fisici, la mira interroga solo
/// <see cref="CursorMask"/> e l'immunita' a se' stessi si ottiene escludendo la propria hitbox.
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

    /// <summary>
    /// Solai, soffitti e tetti degli edifici: le superfici ORIZZONTALI della struttura (skill
    /// <c>building-cutaway</c>). I muri restano su <see cref="World"/>.
    ///
    /// Esiste per separare due domande che prima ne erano una sola: "cosa sto guardando" e "cosa c'e'
    /// davvero". Una copertura e' materia solida per i proiettili (<see cref="ShotMask"/>) ma non
    /// deve mai fermare il raggio del cursore (<see cref="CursorMask"/>), altrimenti si mira al
    /// soffitto sopra la propria testa invece che al pavimento della stanza.
    /// </summary>
    public const uint BuildingCover = 1 << 6;

    /// <summary>
    /// Su cosa si posa il CURSORE. Si ferma sul mondo e sui veicoli, aggancia le hitbox. L'acqua non
    /// compare perche' non ha alcun corpo fisico (vedi WaterVolume); <see cref="BuildingCover"/> non
    /// compare perche' e' sempre sopra la testa di chi guarda.
    ///
    /// Non basta da sola: il cursore non deve nemmeno agganciare i MURI dei piani superiori, che
    /// stanno su <see cref="World"/> e sono solidi anche mentre il cutaway li rende invisibili. Ci
    /// pensa <c>AimResolver.ResolveAimPoint</c> facendo partire il raggio sotto il soffitto del
    /// piano corrente, invece di catalogare geometria layer per layer.
    /// </summary>
    public const uint CursorMask = World | Hitbox | Vehicles | VehicleDeck;

    /// <summary>
    /// Cosa ferma un COLPO (host-side, <c>AimResolver.TraceShot</c>). E' <see cref="CursorMask"/>
    /// piu' le coperture: un solaio e' fisico, e un proiettile sparato al piano terra non deve
    /// arrivare al primo.
    ///
    /// La differenza fra le due maschere e' voluta e non va richiusa: il cursore risponde a cio' che
    /// il giocatore VEDE — che dipende dal cutaway, quindi dal singolo peer — mentre il colpo
    /// risponde a cio' che ESISTE, che deve essere identico su tutti i peer. Sono due domande
    /// diverse e da qui in poi hanno due risposte.
    /// </summary>
    public const uint ShotMask = CursorMask | BuildingCover;

    // NOTA STORICA: qui stava ViewOccluderMask, "cosa puo' finire fra la camera e cio' che devi
    // vedere". E' sparita insieme alle sonde di occlusione: oggi nessuna query di fisica cerca piu'
    // gli occlusori della camera, perche' quali superfici si aprono lo decide il MATERIALE per
    // frammento, interrogando la maschera del campo visivo (skill `building-cutaway`). Chi volesse
    // reintrodurre una maschera del genere sta probabilmente per reintrodurre anche le sonde.

    /// <summary>
    /// Cosa blocca la LINEA DI VISTA (sistema di visione, vedi skill <c>vision-fog</c>).
    ///
    /// Volutamente DIVERSA da <see cref="ShotMask"/>, per due ragioni entrambe di gioco:
    /// <list type="bullet">
    /// <item><see cref="Hitbox"/> non c'e': un nemico non deve nascondere un altro nemico, altrimenti
    /// due sagome in fila diventano una sola e la visibilita' dipende dall'ordine di attraversamento.</item>
    /// <item><see cref="VehicleDeck"/> non c'e': i raggi corrono all'altezza del petto e il parapetto
    /// della barca sta su quel layer, quindi includerlo renderebbe CIECHI stando al timone.</item>
    /// </list>
    /// <see cref="BuildingCover"/> invece c'e', ed e' obbligatorio: un SOLAIO e' opaco. Senza,
    /// l'unica cosa che separa due piani sarebbe la distanza orizzontale — che fra chi sta sopra e
    /// chi sta sotto e' quasi zero — e si vedrebbe attraverso il pavimento. E' lo stesso layer che
    /// ferma i colpi (<see cref="ShotMask"/>) e per la stessa ragione fisica.
    ///
    /// Ne discende un limite dichiarato: cio' che ferma un proiettile e cio' che ferma lo sguardo
    /// coincidono per mondo, solai e scafi, ma non sono lo stesso concetto. Una rete metallica (si
    /// vede attraverso, ferma i colpi) richiedera' un layer proprio, non un ritocco a questa costante.
    /// </summary>
    public const uint VisionBlockerMask = World | Vehicles | BuildingCover;

    /// Maschera di collisione del corpo di un giocatore: mondo, altri corpi, ponte dei veicoli e
    /// solai degli edifici — <b>mai</b> lo scafo. <see cref="BuildingCover"/> serve qui perche' e'
    /// la superficie su cui si cammina ai piani superiori: senza, si cadrebbe attraverso il solaio.
    /// NOTA: le scene scrivono il valore come letterale (oggi 103), quindi cambiare qui non basta,
    /// va aggiornato anche <c>collision_mask</c> in <c>player/scenes/Player.tscn</c> e
    /// <c>ai/scenes/NpcCharacter.tscn</c>.
    public const uint PlayerBodyMask = World | Players | Enemies | VehicleDeck | BuildingCover;
}
