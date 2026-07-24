# CLAUDE.md — Guida operativa per Claude Code

Questo file è il punto di riferimento permanente per qualunque sessione di Claude Code su questo progetto. Va letto per intero prima di iniziare a scrivere codice. Se una decisione presa qui viene cambiata durante lo sviluppo, **aggiorna questo file nello stesso commit**: è la fonte di verità architetturale del progetto, non un documento statico.

---

## 1. Cos'è questo progetto

RPG open world cooperativo, visuale isometrica dall'alto, ambientazione survival/action in stile **S.T.A.L.K.E.R.** (atmosfera, IA nemica, zona ostile) con un sistema di **inventario a griglia in stile Escape from Tarkov** (peso, ingombro, slot dedicati). Il gioco include:

- Movimento e combattimento in tempo reale (shooting).
- Inventario a griglia (dimensioni oggetti, peso, container annidati tipo zaino/tasche).
- Sistema di quest.
- Sistema di costruzione e personalizzazione di una base.
- Multiplayer cooperativo fino a 4 giocatori, architettura **Listen Server / Host-Player** (nessun server dedicato nella fase attuale).

Questo documento copre **solo la fase di prototipazione iniziale**: movimento, inventario, shooting. Quest e base-building sono previsti nello scaffolding (cartelle riservate) ma non vanno implementati finché le fondamenta non sono solide — vedi §9.

> Nota di design ereditata da una sessione di analisi precedente: l'architettura Client-Server unificata è stata scelta deliberatamente per poter passare in futuro a un server dedicato (per una modalità con più giocatori) senza riscritture, pagando fin da subito un ~15-20% di codice in più per feature. Questo principio guida ogni decisione tecnica in questo file: **si scrive sempre codice "server-authoritative", anche quando in singleplayer il client E il server sono lo stesso processo.**

---

## 2. Stack tecnico

| Componente | Scelta | Note |
|---|---|---|
| Engine | **Godot 4.7** (build .NET/Mono) | Non la build standard: serve la build con supporto C#. |
| Linguaggio | **C#** | Nessuna logica di gameplay in GDScript, per coerenza e tipizzazione forte. GDScript va usato solo per micro-script editor/tooling se strettamente necessario. |
| Runtime | **.NET SDK 8.0+** | Requisito minimo di Godot 4.7. Verifica con `dotnet --version` prima di iniziare. |
| Rendering | Forward+ (desktop) | Renderer di default per progetti desktop non-mobile. |
| Fisica | **Jolt Physics** | Motore fisico di default da Godot 4.6+, necessario per veicoli e fisica dei corpi rigidi (vedi §9.3 e rischi in §12). |
| Networking | **GodotSteam** (GDExtension) + **GodotSteam C# Bindings** (addon di terze parti, non ufficiale) | Vedi §8: GodotSteam upstream NON supporta C# nativamente, serve un layer di binding separato. |
| Piattaforma target | Windows/Linux desktop via Steam | Nessun target mobile/web per ora: irrilevante con GodotSteam. |

### Setup ambiente (una tantum)
1. Installa Godot 4.7 **.NET**, non la build standard.
2. Installa .NET SDK 8.0 o superiore.
3. Installa GodotSteam (GDExtension) nella versione compatibile con Godot 4.7.
4. Installa l'addon `GodotSteam C# Bindings` (repo `LauraWebdev/GodotSteam_CSharpBindings`) in `addons/`.
5. Crea `steam_appid.txt` nella root con **480** (AppID di test "Spacewar") finché non esiste un AppID reale registrato su Steamworks. Questo file **non va committato** con un AppID di produzione: vedi `.gitignore`.
6. `Project > Tools > C# > Create C# Solution` per generare la `.sln`/`.csproj`.

---

## 3. Architettura di rete: Listen Server / Host-Player

Questa è la regola più importante del progetto. Vale per ogni sistema (movimento, inventario, shooting, IA, quest, base building) senza eccezioni.

### Principi non negoziabili
1. **Un solo processo autoritativo.** Il giocatore che ospita la partita (Host) esegue anche il ruolo di server. Non esistono "due giochi" paralleli sullo stesso PC: un solo calcolo, un solo stato di verità.
2. **Il client non calcola mai la logica di gioco, solo la resa.** Danno, inventario, posizione autoritativa dei nemici, esito delle azioni: si calcolano SOLO dove `IsMultiplayerAuthority()` è vero (cioè sull'host). I client remoti ricevono lo stato aggiornato e lo "rispecchiano" (property replication), non lo ricalcolano.
3. **Scrivi sempre come se fosse singleplayer, poi avvolgi in autorità.** Il flusso di lavoro per ogni feature:
   - Scrivi la logica come la scriveresti in un gioco a giocatore singolo.
   - Racchiudila in un controllo `IsMultiplayerAuthority()`.
   - Sincronizza solo il risultato (posizione, stato, HP) con `MultiplayerSynchronizer` o proprietà `[Export]` sincronizzate; sincronizza eventi estetici (spari, animazioni, suoni) con RPC dedicate.
4. **Mai fidarsi ciecamente dell'input del client.** Ogni richiesta che arriva dal client (es. "ho raccolto l'oggetto X", "ho sparato in direzione Y") viene validata lato host prima di essere applicata (distanza, cooldown, plausibilità). Anche in fase di prototipo con soli amici fidati, scrivi il codice così: è più facile mantenerlo corretto che rifattorizzarlo dopo.

### Pattern di riferimento in C#

```csharp
public partial class EnemyController : CharacterBody3D
{
    [Export] public int Health { get; set; } = 100;

    public override void _PhysicsProcess(double delta)
    {
        // La logica "pesante" (IA, pathfinding, fisica) gira SOLO sull'host.
        if (!IsMultiplayerAuthority())
            return;

        RunAiAndMovement(delta);
    }

    // Chiamata dal client quando spara: SOLO l'host esegue il calcolo del danno.
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void RequestHit(int damage)
    {
        if (!IsMultiplayerAuthority())
            return; // solo l'host esegue davvero il calcolo

        Health -= damage; // la property replicata propaga il nuovo valore ai client
        if (Health <= 0)
            HandleDeath();
    }
}
```

- Usa `MultiplayerSynchronizer` per posizione/rotazione/velocità e per variabili di stato "continue" (HP, munizioni).
- Usa `[Rpc]` per eventi discreti "estetici o d'intento" (spara, riproduci animazione, riproduci suono), mai per calcolare direttamente il risultato sul client.
- In singleplayer/dev locale, l'host È sempre l'autorità (`IsMultiplayerAuthority()` è sempre vero se non c'è multiplayer attivo): il codice non cambia tra singleplayer e multiplayer, per costruzione.

### Cosa NON fare in questa fase (rimandato volutamente)
- Lag compensation / rewind per gli hitbox: da introdurre solo quando lo shooting prototype è stabile (vedi rischio in §12).
- Host migration: non previsto nel prototipo. Se l'host esce, la sessione termina. Documentare questa limitazione nel README, non nasconderla.
- Server dedicato / scaling oltre 4 giocatori: fuori scope.

---

## 4. Struttura del progetto (scaffolding)

Crea questa struttura alla root del progetto Godot. I nomi delle cartelle in `scenes/` e `scripts/` sono **per sistema di gameplay**, non per tipo di file: mantiene vicino ciò che cambia insieme.

```
/
├── CLAUDE.md
├── README.md
├── .gitignore
├── project.godot
├── GameName.sln / GameName.csproj
│
├── addons/
│   ├── godotsteam/                  # GDExtension GodotSteam
│   └── godotsteam_csharp_bindings/  # Binding C# di terze parti
│
├── autoload/                        # Singleton globali (Autoload in Project Settings)
│   ├── GameManager.cs               # Stato di gioco globale, fase corrente
│   ├── NetworkManager.cs            # Setup lobby Steam, host/join, gestione peer
│   └── EventBus.cs                  # Segnali/eventi globali disaccoppiati tra sistemi
│
├── core/                            # Codice condiviso, non specifico di un sistema
│   ├── Authority/                   # Helper per pattern server-authoritative (base class, extension methods)
│   └── Utils/
│
├── player/
│   ├── scenes/
│   │   ├── Player.tscn
│   │   └── PlayerCamera.tscn         # Camera isometrica (ortogonale, angolo fisso)
│   └── scripts/
│       ├── PlayerController.cs       # Movimento (fase 1 del prototipo)
│       ├── PlayerInput.cs            # Solo raccolta input locale, nessuna logica di stato
│       └── PlayerNetworkSync.cs
│
├── combat/
│   ├── scenes/
│   │   ├── Weapons/
│   │   └── Projectiles/
│   └── scripts/
│       ├── WeaponController.cs       # Shooting (fase 3 del prototipo)
│       ├── HitboxComponent.cs
│       └── HealthComponent.cs
│
├── inventory/
│   ├── scenes/
│   │   ├── InventoryGridUI.tscn      # UI a griglia stile Tarkov
│   │   └── ItemPickup.tscn
│   └── scripts/
│       ├── InventoryGrid.cs          # Fase 2 del prototipo: logica griglia/peso/ingombro
│       ├── ItemDefinition.cs         # Resource (.tres) per definire un item
│       └── ItemDatabase.cs
│
├── ai/
│   ├── scenes/
│   └── scripts/
│       ├── EnemyController.cs
│       └── NavigationAgentSetup.cs
│
├── world/
│   ├── scenes/
│   │   └── Levels/
│   └── scripts/
│
├── quests/                          # Riservato, non implementare ora (post-prototipo)
│   ├── scenes/
│   └── scripts/
│
├── building/                        # Riservato, non implementare ora (post-prototipo)
│   ├── scenes/
│   └── scripts/
│
├── resources/                       # Dati puri: .tres, ItemDefinition, EnemyDefinition, QuestDefinition
│   ├── items/
│   └── enemies/
│
├── ui/
│   ├── scenes/
│   └── scripts/
│
└── assets/
    ├── models/
    ├── textures/
    ├── audio/
    └── placeholders/                 # Primitive/asset temporanei da sostituire in seguito
```

### Regole sullo scaffolding
- **Un sistema, una cartella.** `inventory/`, `combat/`, `ai/` sono verticali: scena + script + risorse specifiche restano insieme. Non creare una cartella globale `scripts/` separata dalle scene.
- **`autoload/` contiene solo singleton veri.** Se un manager non deve essere unico e globale, non è un autoload.
- **`resources/` contiene solo dati (`Resource`/`.tres`), mai logica con side-effect.** Un `ItemDefinition` descrive un oggetto, non lo istanzia da solo.
- **`quests/` e `building/` restano vuote (con un `.gitkeep`) fino a quando i tre prototipi non sono conclusi.** Non generare codice speculativo per sistemi non ancora prototipati: porta a rework inutile.

---

## 5. Convenzioni di codice C#

- **Naming**: `PascalCase` per classi, metodi, proprietà pubbliche; `_camelCase` con underscore per campi privati; `camelCase` per variabili locali e parametri. Segue lo style guide ufficiale C# di Godot, non convertire in stile GDScript.
- **Un file = una classe**, nome file identico al nome della classe.
- **Nodi**: recupera i riferimenti con `GetNode<T>("Path")` in `_Ready()` e mettili in campi tipizzati; evita `GetNode` sparsi in tutto il codice a runtime.
- **Segnali**: usa `[Signal] public delegate void XxxEventHandler(...)` per comunicazione tra nodi nella stessa gerarchia; usa `EventBus` (autoload) solo per comunicazione tra sistemi non collegati direttamente nella scene tree.
- **Niente logica di gameplay in `_Ready`/`_Process` senza guardia di autorità** quando il nodo è multiplayer-aware (vedi §3).
- **Evita singleton "statici" C# per lo stato di gioco.** Usa Autoload di Godot: sono già singleton, si integrano con la scene tree e sono ispezionabili dal debugger.
- **Ogni oggetto raccoglibile/equipaggiabile è un `Resource` (`ItemDefinition`), non una scena duplicata per oggetto**, salvo che serva un comportamento visivo unico nel mondo.
- **Commenti in italiano o inglese, a scelta, ma coerenti all'interno dello stesso file.** Non mischiare le due lingue nello stesso blocco di commento.

---

## 6. GodotSteam + C#: cose da sapere prima di iniziare

- GodotSteam **non ha bindings C# ufficiali**: il supporto C# è fornito da un addon di terze parti (`GodotSteam_CSharpBindings`), compatibile con Godot 4.4+ e specifiche versioni di GodotSteam (verifica compatibilità con la versione installata prima di aggiornare l'uno o l'altro).
- Aggiornamenti di Godot, di GodotSteam e dei bindings C# **non sono garantiti sincroni**: quando aggiorni una delle tre parti, verifica changelog e issue tracker del binding C# prima di aggiornare in produzione.
- Per test locali senza un AppID Steam reale, usa `steam_appid.txt` con **480** (Spacewar, AppID di test ufficiale Valve). Serve comunque un client Steam locale in esecuzione per inizializzare l'API.
- La rete Steam (`SteamNetworkingSockets`) gestisce NAT traversal/relay automaticamente: non serve occuparsi di port-forwarding manuale come con ENet puro — è uno dei motivi per cui è stata scelta.

### Versioni e approccio C# (deciso in Fase 1)
- Versioni di riferimento: **GodotSteam GDExtension 4.20.1** (Steamworks SDK 1.64, variante Godot 4.7) e **binding C# `LauraWebdev/GodotSteam_CSharpBindings` 1.1.0**. Procedura d'installazione in `addons/README-STEAM.md`.
- **Gap di versione noto**: i binding 1.1.0 sono generati contro GodotSteam 4.6.1, la GDExtension per 4.7 è la 4.20.1 (differenze quasi sempre additive — verificare a runtime).
- **`NetworkManager.cs` non dipende dai binding a compile-time**: chiama Steam in modo *late-bound* (`Engine.GetSingleton("Steam")` + `ClassDB.Instantiate("SteamMultiplayerPeer")`). Vantaggi: il progetto compila anche senza addon installati, e resta robusto al gap di versione. I nomi di metodi/segnali Steam sono isolati come costanti in `NetworkManager` per adattarli facilmente alla versione installata.
- **Fallback ENet locale**: `NetworkManager` espone `TransportMode.LocalEnet` (ENet su `127.0.0.1`) come trasporto di **sviluppo** per il test multi-istanza sullo stesso PC (§9/§10), dato che il P2P Steam mono-macchina è scomodo. Steam resta il trasporto primario/di produzione. Tutto il gameplay è agnostico al trasporto (lavora sull'API Multiplayer di alto livello), quindi lo switch non tocca la logica.

---

## 7. Camera e visuale isometrica — decisione di default

Non è stato specificato se il mondo sia 3D con camera isometrica o 2D con proiezione isometrica via tile/sprite. **Assunzione di lavoro**: mondo **3D reale** (necessario per veicoli, fisica Jolt, occlusione naturale, coerente con l'ambientazione S.T.A.L.K.E.R.), con `Camera3D` in **proiezione ortogonale**, angolo fisso (es. 45° yaw / ~35-40° pitch, stile Diablo/Path of Exile), non una camera libera. Se questa assunzione è sbagliata rispetto alla visione del gioco, correggila subito in `player/scenes/PlayerCamera.tscn` prima di costruire altri sistemi sopra: il tipo di camera influenza il design degli hitbox e della UI dell'inventario.

---

## 8. Roadmap dei prototipi (ordine vincolante)

Non passare alla fase successiva finché quella corrente non gira in **multi-istanza locale** (vedi §10) con almeno 2 finestre, una Host e una Client.

### Fase 1 — Movimento  ✅ implementata
- `CharacterBody3D` per il player, input locale solo sul proprio peer.
- Sincronizzazione posizione/rotazione via `MultiplayerSynchronizer`.
- Camera isometrica fissa che segue il player locale (§7).
- Criterio di completamento: 2+ istanze locali si vedono muovere reciprocamente senza scatti evidenti (interpolazione base sui client remoti).
- **Scelta implementativa**: il movimento è *client-authoritative* — ogni peer è autorità del PROPRIO avatar e ne replica lo stato (coerente con "input locale solo sul proprio peer"). Non viola §3: danno/inventario/nemici (Fasi 2/3) restano server-authoritative. La validazione server dell'input di movimento è rimandata come la lag-compensation (§12).
- **Test**: usa il trasporto `LocalEnet` dal menu (Host/Join Locale) per verificare le 2 istanze sullo stesso PC; il path Steam richiede gli addon installati (`addons/README-STEAM.md`). File principali: `autoload/NetworkManager.cs`, `player/`, `world/scenes/Main.tscn`, `ui/scenes/MainMenu.tscn`.

### Fase 2 — Inventario (stile Tarkov)
- Griglia con dimensioni item (celle occupate), non solo slot singoli.
- Peso totale e limite di carico.
- Almeno un container annidato (es. zaino) oltre all'inventario base.
- Logica di stato (aggiungi/rimuovi/sposta item) **autoritativa lato host**, UI aggiornata via replicazione, seguendo lo stesso pattern di §3.
- Criterio di completamento: raccogliere/spostare/droppare un item funziona correttamente per un client non-host senza desync visibile.

### Fase 3 — Shooting
- Arma semplice hitscan (no proiettile fisico per iniziare).
- Danno calcolato **solo** lato host (`RequestHit` pattern di §3).
- Hitbox su `CharacterBody3D`/`Area3D` del nemico placeholder (cubo/capsula, nessun asset finale necessario).
- Criterio di completamento: un client non-host spara e vede l'HP del bersaglio aggiornarsi coerentemente per tutti i peer.

> Solo dopo che le tre fasi sono complete e testate in multi-istanza, si passa a IA nemica avanzata, quest e base building — che richiedono l'inventario e il combattimento già stabili come fondamenta.

---

## 9. Testing durante lo sviluppo (nessuna CI in questa fase)

Setup scelto: scaffolding essenziale, niente pipeline CI/CD per ora. Il testing è manuale ma va fatto in modo sistematico:

1. **Multi-istanza locale**: `Project Settings > Debug > Run Multiple Instances` impostato su almeno 2-3. Ogni feature multiplayer va verificata con più finestre della stessa build, non solo in singola istanza.
2. **Test manuale per ogni fase**: prima di considerare una fase conclusa, verifica esplicitamente il criterio di completamento elencato in §8, non solo "sembra funzionare da host".
3. Non introdurre test automatici/GdUnit in questa fase: è stato deciso di privilegiare velocità di prototipazione. Rivalutare quando i tre sistemi base sono stabili.

---

## 10. Git e workflow

- `.gitignore` deve includere almeno: `.godot/`, `.mono/`, `bin/`, `obj/`, `*.csproj.user`, cartelle di export, e **`steam_appid.txt`** (contiene un AppID che può differire tra ambienti dev/produzione).
- Commit piccoli e per sistema (un commit che tocca `inventory/` non dovrebbe toccare anche `combat/`, salvo refactor condivisi dichiarati come tali nel messaggio).
- Messaggi di commit in italiano o inglese, purché descrivano il *comportamento* cambiato, non il file toccato (es. "Aggiunge validazione host su pickup item", non "Modifica InventoryGrid.cs").

---

## 11. Come deve comportarsi Claude Code su questo progetto

- **Non violare mai il principio di §3** (autorità server-side) per "far funzionare prima una demo": è la decisione architetturale fondante del progetto, discussa e validata esplicitamente. Se una scorciatoia sembra necessaria, segnalalo esplicitamente invece di implementarla silenziosamente.
- **Non generare codice per `quests/` o `building/`** finché non richiesto esplicitamente: sono cartelle riservate, vedi §4 e §8.
- **Non introdurre nuove dipendenze/addon** (altri plugin di rete, asset store, librerie di terze parti) senza segnalarlo prima: lo stack è stato scelto deliberatamente (§2) e ogni aggiunta va valutata per compatibilità con GodotSteam C# bindings.
- **Preferisci placeholder geometrici** (cubi, capsule, sfere) ad asset finali per tutta la fase di prototipazione: nessuna richiesta di arte/asset in questa fase.
- **Aggiorna questo file** quando una decisione qui documentata cambia (es. cambio di camera, cambio libreria di rete, nuova fase aggiunta alla roadmap).
- **Quando un'implementazione richiede una scelta di design non specificata qui**, applica lo stesso approccio del §7: dichiara l'assunzione esplicitamente nel codice/commit, poi procedi — non bloccarti per ogni dettaglio minore.
- **Se una feature richiesta rischia di violare uno dei "rischi noti" in §12, segnalalo prima di procedere**, anche se non è stato esplicitamente richiesto di evitarlo.

---

## 12. Rischi noti e debiti tecnici accettati consapevolmente

Da tenere presente per non essere sorpresi in fasi successive (derivati da un'analisi architetturale precedente al prototipo):

- **Vantaggio strutturale dell'host nello shooting**: l'host ha latenza zero verso se stesso, i client remoti no. Nella Fase 3 questo sarà visibile come "colpi persi" percepiti dai client non-host. La lag compensation (rewind lato server al momento dello sparo) è rimandata volutamente: da implementare solo dopo che il pattern base di §3 è stabile, non prima.
- **Host migration non implementata**: se l'host chiude il gioco, la sessione termina per tutti. Accettato come limite del prototipo.
- **Fisica dei veicoli non affrontata in questa fase**: quando arriverà (fuori scope per Fasi 1-3), la sincronizzazione andrà pensata con interpolazione/estrapolazione lato client, perché la fisica di Jolt non è garantita deterministica al 100% tra macchine diverse — non si può semplicemente "calcolarla sull'host e sincronizzare lo stato" come per un danno scalare.
- **Nessun test automatico**: scelta esplicita per velocità in questa fase (§9). Da rivalutare quando il codice di rete diventa più complesso (es. prima di Fase 3 avanzata o di IA/quest).
- **Movimento client-authoritative in Fase 1**: ogni peer calcola e replica la posizione del proprio avatar; l'host non rivalida l'input di movimento. Accettato per un prototipo co-op fidato; da irrobustire (validazione/riconciliazione lato host) se/quando servirà. Non intacca l'autorità server su danno, inventario e IA.
