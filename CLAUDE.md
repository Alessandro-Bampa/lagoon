# CLAUDE.md — Guida operativa per Claude Code

Questo file è il punto di riferimento permanente per qualunque sessione di Claude Code su questo progetto. Va letto per intero prima di iniziare a scrivere codice. Se una decisione presa qui viene cambiata durante lo sviluppo, **aggiorna questo file nello stesso commit**: è la fonte di verità architetturale del progetto, non un documento statico. Se ci sono soluzioni migliori che vanno contro questo documento, interroga l'utente su come agire.

> **Regola di manutenzione di questo documento.** Modificalo solo con ciò che è **essenziale e trasversale** a tutto il progetto. Le feature **non** vanno aggiunte qui mano a mano che vengono implementate: la documentazione di un singolo ambito va in una **Skill** (§8). Un CLAUDE.md che cresce a ogni fase diventa un costo fisso pagato da ogni sessione futura, anche da quelle che lavorano su tutt'altro.

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
| Fisica | **Jolt Physics** | Motore fisico di default da Godot 4.6+, necessario per veicoli e fisica dei corpi rigidi. |
| Networking | **GodotSteam** (GDExtension) + **GodotSteam C# Bindings** (addon di terze parti, non ufficiale) | GodotSteam upstream NON supporta C# nativamente, serve un layer di binding separato. Dettagli nella skill `steam-networking`. |
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
4. **Mai fidarsi ciecamente dell'input del client.** Ogni richiesta che arriva dal client viene validata lato host prima di essere applicata (distanza, cooldown, plausibilità). Anche in fase di prototipo con soli amici fidati, scrivi il codice così: è più facile mantenerlo corretto che rifattorizzarlo dopo.

### La regola che ne discende

> **Una RPC `AnyPeer` accetta un *intento*, non un *risultato*.**
> Se il payload contiene già la conseguenza — l'ammontare di danno, il bersaglio ucciso, l'oggetto ottenuto — la firma è sbagliata: stai lasciando al client il calcolo che deve fare l'host.

Il client dice "sto mirando qui", non "ho fatto 25 danni a Tizio". L'host ricostruisce l'azione dal proprio stato replicato e ne calcola l'esito. Questo vale per ogni sistema futuro (IA, quest, base building) allo stesso modo.

### Forma canonica in C#

```csharp
// Submit: chiamato dal proprietario. Host = locale; client = RpcId(host).
public void SubmitAction(Vector3 intent)
{
    if (IsMultiplayerAuthority())
        RequestAction(intent);
    else
        RpcId(NetworkConstants.HostPeerId, MethodName.RequestAction, intent);
}

// Request: eseguita SOLO sull'host, sempre validata.
[Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
public void RequestAction(Vector3 intent)
{
    if (!ValidateSender())
        return;
    // ... validazioni di plausibilità, poi calcolo autoritativo.
    // Il risultato si propaga da solo via proprietà replicate.
}

private bool ValidateSender()
{
    if (!IsMultiplayerAuthority())
        return false;
    int sender = Multiplayer.GetRemoteSenderId();
    return sender == 0 || sender == _ownerPeerId; // 0 = chiamata locale dell'host
}
```

- Usa `MultiplayerSynchronizer` per posizione/rotazione/velocità e per variabili di stato "continue" (HP, munizioni).
- Usa `[Rpc]` per eventi discreti "estetici o d'intento" (spara, riproduci animazione, riproduci suono), mai per calcolare direttamente il risultato sul client.
- **`_EnterTree` e autorità**: `PlayerController` marchia *ricorsivamente* il proprio sottoalbero col peer proprietario. Un nodo che deve essere host-autoritativo (inventario, salute, arma) sovrascrive con `SetMultiplayerAuthority(HostPeerId)` nel **proprio** `_EnterTree` — e i suoi `MultiplayerSynchronizer` devono essere nodi *figli*, non fratelli, per ereditare l'autorità giusta.
- In singleplayer/dev locale, l'host È sempre l'autorità (`IsMultiplayerAuthority()` è sempre vero se non c'è multiplayer attivo): il codice non cambia tra singleplayer e multiplayer, per costruzione.

---

## 4. Struttura del progetto (scaffolding)

I nomi delle cartelle in `scenes/` e `scripts/` sono **per sistema di gameplay**, non per tipo di file: mantiene vicino ciò che cambia insieme.

```
/
├── addons/          # GDExtension GodotSteam + binding C# di terze parti
├── autoload/        # Singleton globali (GameManager, NetworkManager, EventBus, SettingsService)
├── core/            # Codice condiviso, non specifico di un sistema
│   ├── Authority/   # Helper per pattern server-authoritative
│   └── Utils/       # NetworkConstants, CollisionLayers
├── animation/       # Rig, AnimationTree, IK e procedurale       -> skill `character-animation`
├── player/          # Movimento, input locale, camera isometrica
├── combat/          # Armi, tiro, salute, hitbox        -> skill `combat-shooting`
├── inventory/       # Griglia, item, equipaggiamento, HUD -> skill `inventory-tarkov`
├── vehicles/        # Barche, acqua, galleggiamento           -> skill `vehicles-boats`
├── ai/              # Nemici, navigazione (non ancora implementato)
├── world/           # Livelli, spawn di giocatori e oggetti
├── quests/          # Riservato, non implementare ora (post-prototipo)
├── building/        # Riservato, non implementare ora (post-prototipo)
├── resources/       # Dati puri: .tres (items/, enemies/)
├── ui/              # Menu principale, menu di pausa      -> skill `ui-hud`
└── assets/          # models/, textures/, audio/, placeholders/
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
- **Commenti e documentazione in italiano**, coerentemente con tutto il codice esistente.
- **Nodi**: recupera i riferimenti con `GetNode<T>("Path")` in `_Ready()` e mettili in campi tipizzati; evita `GetNode` sparsi in tutto il codice a runtime.
- **Segnali**: usa `[Signal] public delegate void XxxEventHandler(...)` per comunicazione tra nodi nella stessa gerarchia; usa `EventBus` (autoload) solo per comunicazione tra sistemi non collegati direttamente nella scene tree.
- **Niente logica di gameplay in `_Ready`/`_Process` senza guardia di autorità** quando il nodo è multiplayer-aware (vedi §3).
- **Nessuna stringa visibile all'utente è scritta nel codice.** Ogni testo mostrato a video passa da una chiave di traduzione (`Loc.T("UI_...")`, oppure la chiave scritta direttamente in un `.tscn` e risolta dall'auto-translate). I testi vivono in `locales/`; i nomi degli oggetti si derivano dall'`ItemId`, quindi i `.tres` non contengono testo. Sono esclusi solo i messaggi di sola console (`GD.Print*`), che restano in italiano. Verifica con `tools/check-i18n.ps1`; dettagli e casi particolari nella skill `i18n-localization`.
- **Evita singleton "statici" C# per lo stato di gioco.** Usa Autoload di Godot: sono già singleton, si integrano con la scene tree e sono ispezionabili dal debugger.
- **Ogni oggetto raccoglibile/equipaggiabile è un `Resource` (`ItemDefinition`), non una scena duplicata per oggetto**, salvo che serva un comportamento visivo unico nel mondo. Quando una categoria ha attributi propri, **estendi con una sottoclasse `[GlobalClass]`** invece di aggiungere campi opzionali alla base: `WeaponDefinition : ItemDefinition` porta danno/portata/cadenza, che su un medkit non avrebbero senso. Il `.tres` dichiara `script_class="WeaponDefinition"`; `ItemDatabase` non cambia, perché `ResourceLoader.Load<ItemDefinition>` restituisce già l'istanza derivata.

---

## 6. Testing durante lo sviluppo (nessuna CI in questa fase)

Setup scelto: scaffolding essenziale, niente pipeline CI/CD per ora. Il testing è manuale ma va fatto in modo sistematico:

1. **Multi-istanza locale**: `Project Settings > Debug > Run Multiple Instances` impostato su almeno 2-3. Ogni feature multiplayer va verificata con più finestre della stessa build, non solo in singola istanza. Per il test sullo stesso PC usa il trasporto **ENet locale** dal menu di avvio, non Steam.
2. **Test manuale per ogni fase**: prima di considerare una fase conclusa, verifica esplicitamente il criterio di completamento della feature o del fix.
3. Non introdurre test automatici/GdUnit in questa fase: è stato deciso di privilegiare velocità di prototipazione. Rivalutare quando i tre sistemi base sono stabili.

---

## 7. Come deve comportarsi Claude Code su questo progetto

- **Non violare mai il principio di §3** (autorità server-side) per "far funzionare prima una demo": è la decisione architetturale fondante del progetto, discussa e validata esplicitamente. Se una scorciatoia sembra necessaria, segnalalo esplicitamente invece di implementarla silenziosamente.
- **Carica la Skill dell'ambito prima di lavorarci** (§8). Contiene invarianti che non sono deducibili leggendo un file alla volta.
- **Non gonfiare questo documento.** Se al termine di un lavoro c'è documentazione da lasciare, va nella Skill dell'ambito. Qui si scrive solo ciò che vale per tutto il progetto. Se un ambito non ha ancora una Skill e la merita, creala.
- **Puoi introdurre nuove dipendenze/addon se disponibili senza sviluppare da zero una feature** (altri plugin, asset store, librerie di terze parti) segnalandolo; ogni aggiunta va valutata per compatibilità con Godot 4.7, C# e le altre librerie già installate.
- **Quando un'implementazione richiede una scelta di design non specificata qui**, dichiara l'assunzione esplicitamente nel codice/commit, poi procedi — non bloccarti per ogni dettaglio minore.

---

## 8. Skill di progetto

La documentazione d'ambito vive in `.claude/skills/<nome>/SKILL.md`. **Carica la skill pertinente prima di modificare quell'ambito**: contengono decisioni già prese, invarianti da non rompere e lacune volute che rileggere il codice non rivela.

| Skill | Caricala quando tocchi |
|---|---|
| `combat-shooting` | `combat/` — armi, danno, HP, hitbox, mira, reticolo, munizioni, ricarica, rinculo, dispersione, collision layer 3D |
| `inventory-tarkov` | `inventory/` — griglia, item, equipaggiamento, contenitori annidati, peso, raccolta/drop, casse, drag & drop, hotbar |
| `ui-hud` | `ui/`, la HUD, menu e popup, scala UI, risoluzione, audio, **o l'assegnazione di un tasto/azione di input** |
| `steam-networking` | `NetworkManager`, `addons/godotsteam*`, lobby, trasporti, errori di GDExtension Steam |
| `vehicles-boats` | `vehicles/` — barche, acqua, galleggiamento, timone, passeggeri, ponte calpestabile, piattaforme mobili, coordinate locali a un veicolo (`SyncAnchorId`) |
| `i18n-localization` | `locales/`, `Loc`, **qualunque testo visibile all'utente**: nuove label, nuovi item, dialoghi, cambio lingua, chiavi grezze a video |
| `blender-pipeline` | `assets/models/`, `tools/blender/` — pipeline Blender→`.glb`→Godot, `Body_Base`, `Armature_Character`, rig e animazioni Mixamo, scala/unità di un modello importato, dialogo con Blender via MCP |
| `character-animation` | `animation/` — AnimationTree e BlendSpace, layer e filtri, clip e loop, aggancio dell'arma alla mano, IK, rinculo e procedurale, T-pose o animazioni ferme |

Ambiti ancora senza skill perché non implementati: IA/nemici, quest, base building. Quando uno di questi viene prototipato, la sua documentazione va in una skill nuova, non qui.
