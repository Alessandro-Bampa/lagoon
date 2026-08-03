---
name: vehicles-boats
description: Sistema veicoli e imbarcazioni (Fase 4). Carica questa skill quando tocchi vehicles/, oppure quando si parla di barche, acqua, galleggiamento/buoyancy, timone, guida, passeggeri, ponte calpestabile, piattaforme mobili, coordinate locali a un veicolo, SyncAnchorId, o i file BoatController, VehicleInput, VehicleRegistry, VehicleInteraction, WaterVolume, Boat.tscn. Serve anche quando modifichi il trasporto del giocatore in PlayerController o il layer 5 "vehicles".
---

# Veicoli e imbarcazioni (Fase 4)

Una sola imbarcazione, statica nel livello, con galleggiamento per punti su acqua piatta. Un pilota al
timone e passeggeri che camminano liberamente sul ponte in movimento.

Vive in `vehicles/` (5 script + `Boat.tscn`), più la laguna in `world/scenes/Levels/TestLevel.tscn` e
le modifiche a `PlayerController` / `PlayerInput` / `PlayerHud` / `CollisionLayers`.

---

## 1. La regola centrale: `SyncPosition` non è più in coordinate mondo

Dalla Fase 4 `PlayerController.SyncPosition` è espressa nel **sistema di riferimento indicato da
`SyncAnchorId`**: 0 = mondo, altrimenti il `BoatController.VehicleId` del veicolo su cui si sta.

**Chi ha bisogno della posizione autoritativa in coordinate mondo usa `ResolvedSyncPosition`.** È già
così in `WeaponController.RequestFire`; qualunque nuovo calcolo host-side deve fare lo stesso, altrimenti
per un giocatore sul ponte otterrà un punto vicino all'origine dello scafo.

Due decisioni da non ribaltare senza capirne il motivo:

- **`SyncAnchorId` sta nello STESSO `SceneReplicationConfig` di `SyncPosition`** (`Player/Synchronizer`),
  non in un canale a parte. Le proprietà di un solo config viaggiano nello stesso pacchetto: l'atomicità
  della coppia `(SyncPosition, SyncAnchorId)` è garantita dall'engine. Separandole ci sarebbe un frame in
  cui l'ancora è cambiata e la posizione no, cioè un teletrasporto visibile a ogni imbarco/sbarco.
- **L'interpolazione remota avviene nello spazio dell'ancora**, non nel mondo (`RemoteInterpolation`,
  campo `_remoteLocal`). Non è stile: interpolando in mondo verso un bersaglio che si muove a `v` m/s
  l'errore stazionario è `v / InterpolationSpeed` ≈ 0.5 m a 7.5 m/s, cioè i passeggeri remoti
  scivolerebbero visibilmente verso poppa. Al cambio di `SyncAnchorId` ci si riaggancia **senza**
  interpolare, altrimenti si interpolerebbe fra due punti espressi in sistemi di coordinate diversi.

**Chi calcola la trasformata locale è il peer proprietario del player, non l'host.** L'host non possiede
la posizione del giocatore: il movimento è client-autoritativo (eccezione dichiarata della Fase 1).
Cambiare il *sistema di riferimento* di una grandezza non cambia chi ne è autorità, quindi CLAUDE.md §3
non è violato — ma se un giorno il movimento diventerà host-autoritativo, questo va rivisto insieme.

---

## 2. Il ponte NON trasporta nessuno: lo fa `CarryWithAnchor`

```csharp
GlobalPosition = boatNow * (_lastAnchorFrame.AffineInverse() * GlobalPosition);
```

Il trasporto è il **delta di trasformata** del veicolo applicato al giocatore. È esatto anche in
rotazione (si ruota attorno al pivot dello scafo, non lungo la corda). Verificato in headless: su una
virata di 90° a piena velocità l'offset boat-local deriva di **3·10⁻⁵ m**; in accelerazione rettilinea di
**5·10⁻⁷ m** su 160 tick.

### L'invariante più fragile del sistema

> La platform velocity dell'engine **funziona** con Jolt e con l'`AnimatableBody3D` del ponte.

Questo contraddice l'ipotesi di partenza ed è stato misurato: senza disattivarla il giocatore viaggiava
al **doppio** della velocità della barca, perché i due trasporti si sommavano. È disattivata in
`player/scenes/Player.tscn` con:

```
platform_floor_layers = 4294967263     # 0xFFFFFFFF senza il bit del layer 6 "vehicle_deck"
```

**Se quel valore torna al default (`0xFFFFFFFF`), il trasporto si applica due volte.** Nota collegata:
`platform_floor_layers` non ha nulla a che vedere col far *collidere* il giocatore col ponte — quello
dipende dal `collision_mask` del Player, che deve includere il layer 6.

Si è scelto il trasporto esplicito e non quello dell'engine perché quest'ultimo è un'approssimazione del
primo ordine e ricostruisce il termine `ω × r` attorno all'origine del **corpo di appoggio** (il `Deck`,
che ha una trasformata locale non identità) anziché a quella dello scafo.

### Perché scafo e ponte stanno su DUE layer diversi

| Nodo | Tipo | Layer | Mask | Ruolo |
|---|---|---|---|---|
| `Boat/Hull` | `CollisionShape3D` sul `RigidBody3D` | **5** `vehicles` | `World` | massa e inerzia, collide col fondale e col molo |
| `Boat/Deck` | `AnimatableBody3D` (`sync_to_physics = false`) | **6** `vehicle_deck` | **0** | superficie calpestabile e parapetti. Non interroga nulla. |

Un `CharacterBody3D` che tocca un `RigidBody3D` lo **spinge**: Jolt lo tratta come massa infinita e
risolve la compenetrazione muovendo il rigid body. Quindi il giocatore non deve poter toccare lo scafo,
mai.

> **In Godot la collisione è simmetrica**: due corpi interagiscono se
> `(A.layer & B.mask) || (B.layer & A.mask)`. Non basta che lo scafo non mascheri `Players` — se il
> Player ha in maschera il layer dello scafo, si toccano lo stesso.

È esattamente il bug emerso al primo test manuale: con scafo e ponte sullo stesso layer, la barca veniva
spinta via dal molo appena un giocatore la sfiorava. Con i due layer separati la maschera del Player
(`collision_mask = 39` = mondo + player + nemici + `vehicle_deck`) **non contiene** il layer 5, quindi lo
scafo è intoccabile e nessuna forza gli torna addosso.

Il `GroundProbe` del Player interroga il solo layer 6 (`collision_mask = 32`).

### Riconoscimento dell'ancora

Si usa un `RayCast3D` figlio del Player (`GroundProbe`, giù 1.2 m, mask = solo `vehicles`), **non**
`GetLastSlideCollision()`: quello ritorna `null` quando nel tick non c'è stato movimento, quindi un
giocatore **fermo** sul ponte perderebbe e riacquisterebbe l'ancora a intermittenza — e con essa
`_lastAnchorFrame`, cioè resterebbe indietro a scatti.

---

## 3. Autorità e simulazione

| Nodo | Autorità | Come |
|---|---|---|
| `Boat` (`BoatController : RigidBody3D`) | **host** | `SetMultiplayerAuthority(HostPeerId)` nel proprio `_EnterTree` |
| `Boat/Sync` | **host** | **figlio** di `Boat`, `root_path=".."` → eredita |
| `Player/VehicleInput` | peer proprietario | nodo di solo input locale, come `WeaponInput` |
| `Player` root + `Synchronizer` | peer proprietario | invariato (movimento client-auth) |

**Host**: `Freeze = false`, fisica vera. **Client**: `Freeze = true`, `FreezeMode = Kinematic`, e
`_PhysicsProcess` fa solo presentazione (lerp verso `SyncBodyPosition + SyncLinearVelocity * età`, slerp
del quaternione).

`CustomIntegrator` sarebbe la scelta sbagliata: disattiva solo l'integrazione delle **forze**, il corpo
continua a integrare la velocità e quindi **deriva da solo** — una violazione di §3 mascherata. Il
discriminante operativo del design attuale è verificabile: **se l'host smette di inviare stato, la barca
si ferma invece di divergere**, perché il bersaglio dell'interpolazione è funzione esclusiva dei dati
ricevuti.

### Due trappole già pagate in multi-macchina

**1. "È arrivato uno stato" si legge dal segnale, non confrontando i valori.** Il primo pacchetto si
rileva con `MultiplayerSynchronizer.Synchronized`. Dedurlo da un cambio di `SyncBodyPosition` **non
funziona**: con la barca all'ormeggio lo stato ricevuto è identico a quello dell'editor, quindi il
confronto non scatta mai. Bug osservato: la barca restava invisibile — ma **solida** — sul client finché
qualcuno non la muoveva. C'è anche un timeout di 2 s che la rivela comunque, perché una barca invisibile
e solida è un guasto peggiore di una disegnata male per un istante.

**2. L'extrapolazione va limitata.** Il termine `SyncLinearVelocity * età del pacchetto` cresce senza
limite: con l'host silenzioso la barca del client se ne andava all'infinito, cioè proprio la deriva che
il design dichiara di non avere. `MaxExtrapolationSeconds = 0.15` (3 intervalli di replica) fa assestare
il bersaglio. Verificato: staccando l'host la barca del client si ferma di netto e non si muove più di un
millimetro per oltre 10 s.

**Trappola da non reintrodurre**: `Main.tscn` (e quindi la barca) viene caricata **prima** che esista un
peer di rete. Con `MultiplayerPeer == null` `IsMultiplayerAuthority()` è **vero** anche su un futuro
client. Per questo `ApplySimulationMode()` è richiamata in `_Ready` **e** su `EventBus.ConnectedToServer`
/ `PeerJoined` / `NetworkError`, mai una volta sola.

**Rotazione replicata come `Quaternion`**, non come angoli di Eulero: col beccheggio del galleggiamento
interpolare Eulero è sbagliato vicino al wrap.

---

## 4. Il timone

`PilotPeerId` è **replicato dall'host**, non broadcastato: è il *risultato* di una decisione, e così
anche un late-joiner e un peer che ha perso un pacchetto vedono lo stato corretto (CLAUDE.md §3).

Ne discende che **la modalità del giocatore non è uno stato locale**: `PlayerController.Mode` si deduce
da `VehicleRegistry.FindByPilot(...)` su ogni peer. È il motivo per cui l'avatar del pilota risulta
incollato al timone anche sulle finestre degli altri **senza un secondo meccanismo di aggancio**: il
pilota pubblica `SyncAnchorId = VehicleId` e `SyncPosition = HelmLocalPosition` (una costante), cioè
passa dallo stesso identico meccanismo dei passeggeri.

### Due validatori distinti, non uno

```csharp
// Chiunque puo' CHIEDERE il timone: si valida timone libero + distanza dalla posizione REPLICATA.
RequestTakeHelm()  ->  ResolveSender() + VehicleRegistry.FindPlayer() + ResolvedSyncPosition
// Guidare e scendere: il mittente DEVE essere il pilota registrato lato host.
RequestControls() / RequestLeaveHelm()  ->  ValidatePilot()
```

Differenza da `WeaponController.ValidateSender()`: là il mittente atteso è un `_ownerPeerId` **fisso**,
qui è uno **stato dinamico posseduto dall'host**. È esattamente il punto di validazione che un
`MultiplayerSynchronizer` con autorità sul pilota non avrebbe — ed è la ragione per cui l'input di guida
**non** passa da un Synchronizer.

### Canale dei comandi

RPC `AnyPeer` **Unreliable a 20 Hz fissi**, inviata **incondizionatamente** anche quando i comandi non
cambiano: il canale non ha ack, e un pacchetto "acceleratore a zero" perso lascerebbe la barca a tutta
forza. L'host applica l'ultimo intento e lo **azzera dopo `InputTimeoutSeconds = 0.5`** — copre insieme
perdita di pacchetti, client bloccato e client morto prima che ENet lo rilevi. `EventBus.PeerLeft` che
libera il timone è una seconda rete di sicurezza, indipendente.

Sanificazione obbligatoria in `RequestControls`: rifiuto se `!float.IsFinite(...)`, poi `Clamp(±1)`.

### Comandi relativi al veicolo

`VehicleInput` mappa `throttle = -motion.Y`, `steer = motion.X` **senza ruotare dello yaw della
camera**, a differenza del camminare che è screen-relative. Asimmetria voluta: è la convenzione
standard per un veicolo e rende la barca guidabile allo stesso modo qualunque direzione stia tenendo.

Da quando la camera **ruota** (Q/E, skill `building-cutaway` §1) non è più solo una convenzione ma
l'unica scelta possibile: uno sterzo relativo allo schermo cambierebbe significato a ogni scatto di
visuale, e si girerebbe il timone premendo E. Chi cammina invece si allinea a
`IsometricCamera.CurrentYawDegrees`, non a una costante — quella costante non esiste più.

---

## 5. Arbitraggio di F

F (`interact`) era già di `PlayerHud._Input`, che lo consumava **incondizionatamente**. Nessun tasto
nuovo: si applica il meccanismo di `ui-hud` §4 già usato per R.

`PlayerHud` resta il proprietario in `_Input` ma **alla pressione** interroga
`VehicleInteraction.VehicleWins(...)`; se il veicolo vince non consuma, e l'evento scende a
`VehicleInput._UnhandledInput`.

1. Al timone → vince **sempre** il veicolo (F = scendi). Nessun pickup può rubare l'azione.
2. Altrimenti vince il candidato più vicino fra pickup (`PickupRange = 3.5`) e timone (`HelmRange = 3.0`).
3. Nessun candidato → nessuno consuma. Effetto collaterale desiderabile: F a vuoto non viene più ingoiato.

**Bug da non reintrodurre**: la decisione va presa **una volta sola sulla pressione** e memorizzata in
`PlayerHud._interactClaimed`, perché anche il **rilascio** deve seguire lo stesso proprietario. Se il
press scende al veicolo e il release lo prende `PlayerHud`, `_holdingInteract` resta appeso e il menu
contestuale si apre da solo al pickup successivo.

---

## 6. Acqua e geometria della laguna

`WaterVolume` è un `Node3D` con `SurfaceY` e un'estensione XZ: **nessun corpo fisico, di proposito**.
Senza collider non serve un layer per l'acqua, non c'è niente da escludere da `AimMask` e un raggio di
mira non può agganciare il piano d'acqua. **Non aggiungere un layer `water`**: servirebbe solo per poi
doverlo escludere.

Il galleggiamento è auto-bilanciato e non ha numeri da tarare a mano per la quota di riposo:

```csharp
float perFloater = Mass * gravity / _floaters.Length * 2f;  // 50% di immersione = spinta == peso
```

Cambiare `Mass`, il numero di floater o `MaxSubmerge` non richiede ritarature. Verificato: la barca resta
a `y = 0.2000` con velocità verticale ~10⁻⁷, senza oscillazione.

### Quote della laguna (in `TestLevel.tscn`, `Lagoon`)

Scelte perché **non ci sia nessuno scalino da superare a piedi**: `CharacterBody3D` in Godot 4 non ha
step-up automatico.

| Elemento | Quota superficie | Note |
|---|---|---|
| `Floor` (terraferma, invariato) | 0 | x,z ∈ [-20, 20] |
| `Ramp` | 0 → 1.25 | 20°. **Il piede è interrato**: la faccia superiore attraversa y=0 a x≈14.6, così la rampa emerge dal pavimento come un cuneo. Un gradino anche di 6 cm si comporterebbe da muro. |
| `Dock` | 1.25 | x ∈ [18, 27] |
| `Plank` | 1.25 | sovrapposta al molo per 1 m e al ponte per 0.3 m; passa 0.35 m sopra il tetto dello scafo |
| `Boat/Deck` | 1.25 (a riposo) | scafo a `y = 0.2`, tetto a 0.8 |
| `Seabed` | -4 | pavimento di sicurezza: niente cade all'infinito |

Il parapetto di **sinistra è diviso in due** (`RailingPortFore`/`RailingPortAft`) per lasciare un varco
di 2 m dove arriva la passerella. Il resto del ponte è chiuso: è una garanzia **geometrica** contro il
camminare fuori bordo, non logica.

Modo rapido per verificare la continuità del percorso dopo aver toccato una quota: un `IntersectRay`
verso il basso ogni 0.5 m lungo X con maschera `1 | 32` stampa il profilo calpestabile e rende evidente
qualunque gradino o buco. Il percorso corretto è `Floor 0 → Ramp → Dock 1.25 → Plank 1.25 → Deck 1.25`
senza discontinuità.

Chi finisce in acqua rientra al `Marker3D` del gruppo `water_respawn` sul molo
(`PlayerController.CheckWaterFallback`, soglia `SurfaceY - 2`), quindi **non** si tocca il fondale
camminando: il fondale è solo la rete di sicurezza sotto la soglia.

---

## 7. Collision layer

Aggiunti due layer: **5 `vehicles`** (scafo) e **6 `vehicle_deck`** (ponte e parapetti). Entrambi in
`AimMask`, **solo il ponte** in `PlayerBodyMask` (vedi §2 per il perché).

Conseguenza voluta: **la barca ferma i proiettili**, chi sta dietro la murata è al coperto. Coerente con
`combat-shooting` §4, dove un raggio non colpisce mai il *corpo* di un giocatore (solo la hitbox), quindi
lo scafo è un ostacolo pulito.

**`CollisionLayers.cs` è solo uno specchio**: le scene scrivono i valori come letterali. `Player.tscn`
porta `collision_mask = 39` (1+2+4+32) — cambiare la costante senza aggiornare la scena non ha effetto.

Limite dichiarato: il piano di ripiego di `AimResolver` è a `ChestHeight = 1.1` in coordinate **mondo**,
mentre su un ponte a y ≈ 1.25 il petto sta a ≈ 2.35. Irrilevante in pratica perché con `Vehicles` in
`AimMask` il ripiego scatta solo puntando l'orizzonte oltre il fondale. **Non toccare `ChestHeight`.**

---

## 8. Collaudo

`DebugAutoDrive` e `DebugSteer` sono export di collaudo su `BoatController` (default: spenti). Servono a
riprodurre i criteri senza un secondo giocatore, e in particolare il più importante:

> Con `DebugAutoDrive` attivo sull'host, spegnerlo deve fermare la barca **anche sul client** entro poche
> decine di ms, e lasciarla ferma indefinitamente senza derivare. Se il client derivasse, starebbe
> simulando in parallelo.

Si può collaudare la rete **anche in headless**, senza toccare la UI: `NetworkManager.HostGame` /
`JoinGame(LocalEnet, "127.0.0.1")` sono pubbliche, quindi un nodo temporaneo che legga
`OS.GetCmdlineUserArgs()` permette di lanciare due processi Godot `--headless` e registrare lo stato
della barca sui due lati. È così che sono state trovate entrambe le trappole del §3.

Altri controlli utili in multi-istanza (ENet locale, A host e B client): un passeggero remoto sul ponte
deve apparire **fermo rispetto al ponte** e non scivolare verso poppa; un non-pilota che tiene W non deve
produrre nessun intento accettato; chiudendo la finestra del pilota la barca si arresta entro 0.5 s.

---

## 9. Lacune volute

- **Una sola barca** (`VehicleId = 1`). Il codice regge N veicoli (lookup per id, gruppo `vehicle`), ma
  non se ne aggiungono e non c'è nessun `MultiplayerSpawner` per i veicoli: statica nel livello significa
  NodePath identico su ogni peer, quindi il `MultiplayerSynchronizer` interno funziona senza spawner.
- **Nessuna onda**: `SurfaceY` è una costante. Le onde richiederebbero un tempo d'onda condiviso fra i
  peer, che oggi non esiste.
- **Nessuna inerzia sul passeggero**: il delta di trasformata conserva esattamente la posizione
  boat-local, quindi le virate non sbalzano nessuno. È voluto.
- **Niente nuoto, annegamento, ancora, ormeggio, danno alla barca, affondamento, carburante, sedili
  passeggeri, armi di bordo.** Nessun `VehicleBody3D`/ruote: non serve per una barca.
- **Nessun `ItemPickup` sul ponte**: i pickup vivono in coordinate mondo (`GameWorld.SpawnPickupNode`
  forza `y = 0`), quindi scivolerebbero via. Ancorarli a un veicolo è un lavoro separato che riuserà
  `SyncAnchorId`.
- **Nessun HUD del veicolo** (velocità, prompt "[F] Timone"). Il prompt `Label3D` dei pickup è il
  precedente da riusare quando servirà.
- **Nessuna lag compensation** per chi spara dal ponte, e **nessuna validazione anti-cheat** della
  posizione boat-local del passeggero: restano client-autoritative come tutto il movimento della Fase 1.
  È il limite già dichiarato in `combat-shooting` §7, non un debito nuovo.
- **Nessun reparent** del nodo `Player` sotto la barca, e non va introdotto: romperebbe il bookkeeping
  del `MultiplayerSpawner` e la stabilità dei NodePath di RPC e Synchronizer esistenti.
