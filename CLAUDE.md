# CLAUDE.md — Guida operativa per Claude Code

Questo file è il punto di riferimento permanente per qualunque sessione di Claude Code su questo progetto. Va letto per intero prima di iniziare a scrivere codice. Se una decisione presa qui viene cambiata durante lo sviluppo, **aggiorna questo file nello stesso commit**: è la fonte di verità architetturale del progetto, non un documento statico. Se ci sono soluzioni migliori che vanno contro questo documento interroga l'utente su come agire.

---

## 1. Cos'è questo progetto

RPG open world cooperativo, visuale isometrica dall'alto, ambientazione survival/action in stile **S.T.A.L.K.E.R.** (atmosfera, IA nemica, zona ostile) con un sistema di **inventario a griglia in stile Escape from Tarkov** (peso, ingombro, slot dedicati). Il gioco include:

- Movimento e combattimento in tempo reale (shooting).
- Inventario a griglia (dimensioni oggetti, peso, container annidati tipo zaino/tasche).
- Sistema di quest.
- Sistema di costruzione e personalizzazione di una base.
- Multiplayer cooperativo fino a 4 giocatori, architettura **Listen Server / Host-Player** (nessun server dedicato nella fase attuale).

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

## 7. Sistema di scala della UI e menu impostazioni

Regola: **la UI ha dimensione in pixel fissa; sono gli ancoraggi a riposizionarla, non lo scaling automatico dell'engine.**

### 7.1 Pixel fissi + ancoraggio dinamico
- Risoluzione base di riferimento **1920×1080**, dichiarata in `project.godot` insieme a `window/stretch/mode="disabled"`, `window/stretch/aspect="expand"`, `window/stretch/scale=1.0`. Con lo stretch disabilitato un pannello di 100×100 resta 100×100 pixel fisici a qualunque risoluzione: passando a 2K/4K la finestra guadagna spazio e gli elementi si ridistribuiscono lungo i bordi anziché ingrandirsi.
- Di conseguenza **ogni elemento va ancorato, non posizionato**: `LayoutPreset` (`TopLeft`, `BottomWide`, `FullRect`, `CenterContainer` per i pannelli modali) + `MarginContainer` per la distanza costante dal bordo + `HBox`/`VBox` per l'ordinamento interno. Niente coordinate assolute — unica eccezione dichiarata: le finestre pop-up trascinabili (`FloatingWindow`), posizionabili dall'utente per definizione, che vengono comunque *clampate* dentro l'area visibile.
- Le griglie in pixel (`GridPanelView.CellSize = 48`, `HotbarSlotView.SlotSize = 56`) dichiarano solo `CustomMinimumSize`: non scrivere mai `Size` a mano su un `Control` dentro un container o con anchor impostati.
- L'unica leva di scala è **`Window.ContentScaleFactor`** sulla finestra root, esposta come slider **"Scala UI"** (0.75×–2.0×). È il meccanismo documentato da Godot per questo scenario: con stretch `disabled`, `scale = 2.0` significa "1 unità della scena = 2×2 pixel". Poiché agisce sul viewport, **tutto l'albero 2D continua a lavorare in coordinate logiche 1:1**: la matematica in pixel dell'inventario (hit-test in `GridPanelView.CellAt`, rect di `PlayerHud.SyncHudRect`) resta valida senza modifiche.
- **Eccezione da ricordare**: i `PopupMenu` sono `Window` separate e non ereditano il fattore della root. Ogni popup creato in codice va passato a `SettingsService.ApplyToPopup(...)`.

### 7.2 Menu di pausa (ESC)
- **ESC** (azione `toggle_menu`) apre `ui/scenes/PauseMenu.tscn`: pagina radice (Riprendi / Impostazioni / Esci dalla partita / Esci dal gioco) e sotto-pagina Impostazioni con **Scala UI, Schermo intero, VSync, Volumi (Master/Musica/Effetti)**. ESC dentro le Impostazioni torna alla pagina radice invece di chiudere tutto.
- Vive nel `CanvasLayer` `UI` di `world/scenes/Main.tscn`, portato a `layer = 20` perché `PlayerHud` crea a runtime il proprio `CanvasLayer` a `layer = 10`. Lo stesso pannello è raggiungibile dal `MainMenu` (`Open(settingsOnly: true)`), così le opzioni esistono anche prima di entrare in partita.
- `autoload/SettingsService.cs` è l'unico proprietario dei valori: li carica/salva su `user://settings.cfg` (`ConfigFile`) e li applica (`ContentScaleFactor`, `DisplayServer`, `AudioServer`). La UI non tocca mai direttamente i server. I bus audio `Master/Music/SFX` sono definiti in `default_bus_layout.tres` (nessuna sorgente sonora esiste ancora: i bus servono perché gli slider abbiano un bersaglio reale).
- **Il menu NON mette in pausa l'albero.** `GetTree().Paused` fermerebbe solo il peer locale desincronizzandolo dagli altri (§3). Il mondo continua a girare; è sospeso solo l'input locale di gameplay, tramite il flag `GameManager.UiModalOpen` letto da `PlayerInput.ReadMovement()` e `PlayerHud._Input()`.
- **Limite noto accettato**: "Esci dalla partita" chiude il processo. Il `NetworkManager` non supporta una disconnessione pulita con ritorno al menu principale (coerente con l'assenza di host migration); da implementare insieme a quella.

---

## 9. Testing durante lo sviluppo (nessuna CI in questa fase)

Setup scelto: scaffolding essenziale, niente pipeline CI/CD per ora. Il testing è manuale ma va fatto in modo sistematico:

1. **Multi-istanza locale**: `Project Settings > Debug > Run Multiple Instances` impostato su almeno 2-3. Ogni feature multiplayer va verificata con più finestre della stessa build, non solo in singola istanza.
2. **Test manuale per ogni fase**: prima di considerare una fase conclusa, verifica esplicitamente il criterio di completamento per la feature o fix da implementare.
3. Non introdurre test automatici/GdUnit in questa fase: è stato deciso di privilegiare velocità di prototipazione. Rivalutare quando i tre sistemi base sono stabili.

---

## 11. Come deve comportarsi Claude Code su questo progetto

- **Non violare mai il principio di §3** (autorità server-side) per "far funzionare prima una demo": è la decisione architetturale fondante del progetto, discussa e validata esplicitamente. Se una scorciatoia sembra necessaria, segnalalo esplicitamente invece di implementarla silenziosamente.
- **Puoi introdurre nuove dipendenze/addon se disponibili senza sviluppare da zero una feature** (altri plugin, asset store, librerie di terze parti) segnalandolo; ogni aggiunta va valutata per compatibilità con Godot 4.7, C# e le altre librerie già installate.
- **Preferisci placeholder geometrici** (cubi, capsule, sfere) ad asset finali per tutta la fase di prototipazione: nessuna richiesta di arte/asset in questa fase.
- **Quando un'implementazione richiede una scelta di design non specificata qui**, applica lo stesso approccio del §7: dichiara l'assunzione esplicitamente nel codice/commit, poi procedi — non bloccarti per ogni dettaglio minore.

---
