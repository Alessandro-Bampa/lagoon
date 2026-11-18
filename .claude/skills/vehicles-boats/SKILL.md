---
name: vehicles-boats
description: Veicoli e imbarcazioni. Carica quando tocchi vehicles/, resources/boats/, oppure si parla di barche, galleggiamento, fisica delle barche (scafo per triangoli, planata, resistenze, impatti, massa aggiunta), motore, elica, invertitore, trim, timone, chiglia, elica di prua, pesi a bordo, guida, passeggeri, ponte calpestabile, piattaforme mobili, coordinate locali a un veicolo, SyncAnchorId, il trasporto del giocatore in PlayerController, i layer 5/6 (BoatController, BoatPhysics, HydroSolver, HydroHull, PropulsionUnit, OutboardDrive, ShaftDrive, Foil, BoatMassModel, VehicleInput, VehicleRegistry, Barchino.tscn, Boat.tscn, tools/boat_trials.gd).
---

# Veicoli e imbarcazioni

Barche nel livello (la scatola di prova `Boat.tscn` a galleggiamento per punti; le barche su misura
`Barchino.tscn` col nucleo fisico per triangoli, §9-§13), un pilota e passeggeri che camminano sul
ponte in movimento. `vehicles/` + `resources/boats/` + il trasporto in `PlayerController`.

## 1. `SyncPosition` è nel riferimento di `SyncAnchorId`

`0` = mondo, altrimenti il `VehicleId` della barca su cui si sta. **Ogni calcolo host-side usa
`ResolvedSyncPosition`** (o `ResolvedEyePosition`), mai `SyncPosition`.

- `SyncAnchorId` sta nello **stesso** `SceneReplicationConfig` di `SyncPosition`: viaggiano nello stesso
  pacchetto. Separati, a ogni imbarco ci sarebbe un frame di teletrasporto.
- L'interpolazione remota avviene **nello spazio dell'ancora** (`_remoteLocal`): in mondo i passeggeri
  scivolerebbero verso poppa (~0.5 m a 7.5 m/s). Al cambio d'ancora ci si riaggancia senza interpolare.
- La trasformata locale la calcola il peer proprietario: il movimento è client-authoritative.

## 2. Il trasporto è `CarryWithAnchor`, non il ponte

`GlobalPosition = boatNow * (lastFrame⁻¹ * GlobalPosition)`: delta di trasformata, esatto anche in
virata. **Trasporta anche lo sguardo** (`FirstPersonCamera.AddYaw` del delta di prua): in prima persona,
su un ponte che vira, si resta rivolti allo stesso punto dello scafo.

> **Invariante fragile.** La platform velocity dell'engine funziona con Jolt e si sommerebbe (velocità
> doppia). È spenta in `Player.tscn` con `platform_floor_layers = 4294967263` (tutti i layer meno il 6).
> Se torna al default il trasporto si applica due volte.

| Nodo | Tipo | Layer | Ruolo |
|---|---|---|---|
| `Boat/Hull` | shape sul `RigidBody3D` | 5 `vehicles` | massa, collide con fondale e molo |
| `Boat/Deck` | `AnimatableBody3D` | 6 `vehicle_deck` | ponte e parapetti |

Un `CharacterBody3D` che tocca un `RigidBody3D` lo **spinge** (Jolt lo tratta da massa infinita), e la
collisione è simmetrica (`A.layer & B.mask || B.layer & A.mask`). Quindi la maschera del Player (39) non
contiene **mai** il layer 5. L'ancora si riconosce col `RayCast3D` `GroundProbe` (mask 32), non con
`GetLastSlideCollision()`, che è null da fermi.

## 3. Autorità e simulazione

`BoatController` è **host** (`SetMultiplayerAuthority` in `_EnterTree`, `Sync` figlio). Host: fisica
vera. Client: `Freeze` cinematico, solo presentazione verso `SyncBodyPosition + velocità × età`.

- Mai `CustomIntegrator`: il corpo continuerebbe a integrare la velocità, cioè a simulare in parallelo.
  Criterio: **se l'host tace, la barca del client si ferma**.
- "È arrivato uno stato" si legge da `MultiplayerSynchronizer.Synchronized`, non confrontando i valori
  (all'ormeggio sono identici a quelli dell'editor: la barca restava invisibile ma solida). C'è un
  timeout di 2 s di sicurezza.
- L'estrapolazione è limitata (`MaxExtrapolationSeconds = 0.15`).
- `Main.tscn` si carica **prima** che esista un peer: `IsMultiplayerAuthority()` è vero anche su un
  futuro client. `ApplySimulationMode()` va richiamata su `ConnectedToServer`/`PeerJoined`/`NetworkError`.
- Rotazione replicata come `Quaternion`.

## 4. Timone

- `PilotPeerId` è **replicato**, non broadcastato. La modalità del giocatore (`PlayerController.Mode`)
  si **deduce** su ogni peer da `VehicleRegistry.FindByPilot`: il pilota pubblica `SyncAnchorId` +
  `HelmLocalPosition`, cioè passa dallo stesso meccanismo dei passeggeri.
- Due validatori: `RequestTakeHelm` (chiunque: timone libero + distanza dalla posizione replicata) e
  `RequestControls`/`RequestLeaveHelm` (`ValidatePilot`: il mittente è uno **stato dinamico** dell'host).
- Comandi: RPC `Unreliable` a **20 Hz fissi e incondizionati** (un "gas a zero" perso lascerebbe la
  barca a tutta forza); l'host azzera dopo `InputTimeoutSeconds = 0.5`. Rifiuto dei non finiti, poi clamp ±1.
- Comandi **relativi al veicolo**, non allo sguardo: al timone ci si guarda attorno liberamente (entro
  `HelmLookLimitDegrees` dalla prua) e il timone non deve cambiare significato. Lo sguardo vira con la barca.

## 5. Arbitraggio di F

`PlayerHud._Input` interroga `VehicleInteraction.VehicleWins` **alla pressione**: al timone vince sempre
il veicolo; altrimenti il più vicino fra oggetto guardato (`PickupRange` 3.5) e timone (`HelmRange` 3.0);
nessun candidato → nessuno consuma. La decisione si memorizza (`_interactClaimed`) perché anche il
**rilascio** segua lo stesso proprietario: altrimenti `_holdingInteract` resta appeso.

## 6. Acqua e laguna

- L'acqua (onde, marea, `WaterVolume`, `Buoyancy`) è un sistema a sé: skill **`water-ocean`**.
  Solo la scatola di prova `Boat.tscn` usa ancora `Buoyancy.Apply` sui 6 `Floaters` (galleggiamento
  per punti, auto-bilanciato, `ThrustForce`/`SteerTorque` "a scatola"); le barche su misura usano il
  nucleo per triangoli (§10). I `Float_*` dei modelli restano solo per quel ripiego.
- `WaterVolume` non ha corpo fisico: niente layer `water`, niente da escludere dalle maschere.
- Percorso senza scalini: `Floor 0 → Ramp (20°, piede interrato) → Dock 1.25 → Plank → Deck 1.25`.
  `Seabed` a −4 è il fondale su cui si cammina nel bacino; `water_respawn` resta solo come rete di
  sicurezza contro il vuoto (sotto `SurfaceY − 40`): in acqua si nuota (skill `water-ocean` §8).
- **Nuoto attorno alla barca**: lo scafo del `RigidBody` (layer 5) è fuori dalla maschera del
  giocatore, quindi chi nuota gli passerebbe attraverso. Per questo `Deck` ha anche `HullSides`, la
  stessa scatola dello scafo alta fino al ponte (1,05), sul layer 6. A sinistra, nel varco della
  murata, c'è una scaletta (`Visual/BoardingLadder`, gruppo `swim_ladder`): da lì si sale a bordo con
  Spazio. Se il bordo di una manovra di parkour è una barca, la traiettoria si sposta con lei
  (`PlayerController.CarryManeuver`, `CharacterMotor.ShiftManeuver`) e l'ancora dei piedi si
  riacquista a manovra finita.
- `CollisionLayers.cs` è uno specchio: le scene scrivono i letterali.

## 7. Collaudo

`DebugAutoDrive` + `DebugThrottle`/`DebugSteer`/`DebugTrimCommand`/`DebugAutoTrim`/`DebugThruster` su
`BoatController` (motore acceso senza pilota). Si collauda anche headless con due processi e un nodo
che chiama `NetworkManager.HostGame`/`JoinGame`. In multi-istanza: il passeggero remoto è fermo rispetto
al ponte; chiudendo la finestra del pilota la barca si arresta entro 0.5 s.

> **Un solo mittente di comandi.** Un collaudo che chiama `SubmitControls` deve spegnere il
> `VehicleInput` del giocatore (`set_physics_process(false)`): i due flussi a 20 Hz si alternano, la
> leva salta fra folle e marcia e l'invertitore (§11) non innesta mai — la barca resta ferma.

Prove fisiche: `tools/boat_trials.gd` (§13).

## 8. Lacune volute

Una sola barca (nessuno spawner: NodePath identico su ogni peer). Niente inerzia sul passeggero,
danno, HUD del veicolo, `ItemPickup` sul ponte (vivono a `y = 0` in mondo). **Nessun reparent** del
`Player` sotto la barca: romperebbe spawner e NodePath di RPC.

## 9. Barche su misura (`Barchino.tscn`)

`Barchino.tscn` e' la scena GENERICA di ogni barca su misura: la monta a runtime una `BoatConfig`
(`resources/boats/*.tres`): scafo, propulsione (`OutboardDefinition` fuoribordo **oppure**
`InboardDefinition` entrobordo + timone, piu' `BowThrusterDefinition`), `FixedFoils` (chiglie, skeg),
`MassItems`, `HydroCoefficients`, governo (`HelmType.Tiller` barra / `Wheel` volante + consolle),
`BoatAccessory[]` su attacchi, `BoatLivery`. `BoatAssembly` (figlio `Assembly`, `[Tool]`: la barca si
vede anche nell'editor) costruisce tutto nel PROPRIO `_Ready`, prima di quello di `BoatController`,
e crea anche `BoatPhysics` e `BoatMassModel` (solo in gioco). Allestimenti:

| Config | Scafo | Propulsione | Misure (prove §13) |
|---|---|---|---|
| `barchino_tiller` | barchino 4,5 m, 410 kg | fuoribordo 25 cv a barra | ~21 kn, GM 1,1 m, rollio 1,1 s |
| `barchino_console` | idem, 482 kg | fuoribordo 40 cv, volante | ~26 kn, punto fisso ~250 kgf |
| `gozzo` | **placeholder** 7 m, 2,9 t | diesel 35 cv, asse + timone, chiglia | ~6,8 kn, GM 0,57 m, rollio 2,3 s |
| `peschereccio` | **placeholder** 20 m, 152 t | diesel 500 cv, mantello Kort, timone idraulico 4°/s, elica di prua | ~9,9 kn, ~6,7 t a punto fisso, rollio 6,6 s |

- **Modelli**: `tools/blender/build_lagoon_boats.py` (skill blender-pipeline) → `assets/models/boats/`.
  Nomi letti dal codice: `Seat_Helm`, `Mount_*`, `HydroHull` (mesh idrodinamica, §10), `Float_*`
  (solo ripiego), `HullCollision` (convesso del corpo), `DeckCollision` (trimesh del ponte, layer 6);
  motore `Swivel`/`Propeller`/`Thrust`/`Tiller`/`TillerGrip`; consolle `Wheel`/`Hand_L`/`Hand_R`;
  entrobordo `Mount_Prop` (−Z = spinta, asse inclinato), `Mount_Rudder` (Y = asse della pala),
  `Mount_Engine`, `Mount_Thruster`, `Mount_Keel`/`Mount_Skeg`, parti mobili `Rudder`/`Propeller`.
  Materiali per nome ricolorati dalla livrea su una COPIA (i materiali del .glb sono condivisi).
- **Placeholder** (`Gozzo_Placeholder.glb`, `Peschereccio_Placeholder.glb`): scafo parametrico a
  superellisse con origine sulla linea d'acqua di progetto, pagliolo/coperta, impavesata, timoneria
  aperta dietro (peschereccio), varco a poppa per salire dall'acqua con la scaletta. Servono alla
  fisica e a salire a bordo: da sostituire con modelli veri tenendo gli stessi nomi.
- **`HelmSeat` = `Seat_Helm` + 1 m** lungo la verticale della barca (`PilotBodyCenter`): l'origine del
  giocatore e' il CENTRO della capsula. Col posto di guida sul pagliolo il pilota finiva nel fondo.
- **Replica**: `SyncThrottle`, `SyncMotorAngle` (sterzo del fuoribordo o angolo del timone), `SyncTrim`,
  `SyncEngineRpm`, `SyncPropRpm` (con segno), decisi dall'host, nello stesso `Sync` della posa;
  `BoatAssembly.ApplyHelmPose` disegna motore (sterzo + trim), timone, eliche e volante su ogni peer.
  I giri servono anche al suono del motore (che manca). Verificato con due processi
  (`tools/playtest_boats_net.gd`).
- **Pilota** (skill character-animation §3): seduto (`Sitting_Idle` a barra, `Driving` al volante),
  mani in IK su `TillerGrip` / `Hand_L`+`Hand_R`, corpo allineato a beccheggio e rollio dello scafo
  (`PlayerController.AlignVisualWithBoat`), occhi a `FirstPersonCamera.SeatedEyeHeight`.
- **Visuale in terza persona al timone** (`BoatChaseCamera`, V alterna, C torna verso la prua): pura
  resa locale, creata da `VehicleInput._Ready` solo sull'avatar proprietario (figlia sua: niente
  modifiche a `Player.tscn`). Distanza = raggio in pianta del riquadro delle mesh visibili ×
  `DistanceFactor` + `DistanceOffset` (barchino ~8 m, peschereccio ~26 m), rimisurata a ogni
  attivazione; perno al centro in pianta, a 3/4 dell'altezza. L'orbita e' uno scarto dalla prua
  smorzata (solo imbardata: beccheggio e rollio non entrano), raggio anti-ostacolo su mondo e ALTRI
  veicoli (la propria barca esclusa per RID), mai sotto l'acqua. Mentre e' attiva lo sguardo del
  pilota segue la sua imbardata (`FirstPersonCamera.SetLook`, poi il solito limite
  `HelmLookLimitDegrees`), e `FirstPersonCamera` ignora il mouse perche' non e' corrente. La scelta
  si ricorda per la sessione: lasciato il timone si torna in prima persona, ripreso si riparte da li'.
- **Livello di prova** `world/scenes/MainWaveTest.tscn` (+ `Levels/WaveTestLevel.tscn`): mare aperto
  3 km, pontile, i due barchini, gozzo (id 13) e peschereccio (id 14), casse; `SeaStateDebugControls`:
  PagSu/PagGiu' stato del mare, Home laguna/mare aperto (solo host), e al timone una riga con nodi,
  giri, marcia, trim, assetto, sbandamento (dallo stato replicato). Non ci sono cime d'ormeggio: in
  tempesta una barca lasciata accanto al pontile fisso ci finisce sotto.

## 10. Nucleo fisico: scafo per triangoli (`vehicles/scripts/Hydro/`)

Derivato dalle specifiche "Fisica delle imbarcazioni a motore" (modello di Kerner/Just Cause 3 per lo
scafo, MMG per timone ed elica). **Forze, non cinematica**: nessuna scrittura di posa o velocita'
durante la simulazione; ogni comportamento (planata, sbandamento in virata, effetto evolutivo, prua
che sale in accelerazione, motore su di giri con l'elica fuori dall'acqua) nasce dalle forze.

- **Pipeline** (`BoatPhysics.Step`, in `_IntegrateForces`, solo host e non `Freeze`): gravita'
  esplicita (corpo con `gravity_scale = 0`, damp `Replace` 0, CCD) → `HydroSolver` → propulsione →
  appendici → limite di non inversione delle resistenze → massa aggiunta → sanificazione (NaN scartati
  e contati, |F| ≤ 50·m·g) → UNA `ApplyCentralForce` + UNA `ApplyTorque`. La coppia si accumula a mano
  (`BoatForces.AddAt`, τ += (p − G) × F): il punto di `apply_force` di Godot e' uno spostamento
  dall'origine in assi globali, fonte classica di errori.
- **`HydroHull`**: mesh `HydroHull` del modello, chiusa, low-poly (barchino 500 triangoli, gozzo 848,
  peschereccio 1136), saldata al mm; l'orientamento si verifica col volume con segno (si girano i
  triangoli se serve). Coperta chiusa alla FALCHETTA (non al pagliolo): senza un modello d'allagamento
  e' l'approssimazione giusta, ma la barca aperta non si riempie mai d'acqua (lacuna).
- **`HydroSolver`**, per passo: acqua campionata una volta per vertice (`WaterVolume.SampleNear`,
  inversione di Gerstner ripartendo dal passo prima, 2 iterazioni) con stiramento di Wheeler
  (profondita' di pressione e velocita' orbitale attenuate con e^(−k·d), k del picco dello spettro:
  senza, il peschereccio rollava il triplo); taglio di Sutherland-Hodgman continuo; spinta SOLO
  verticale nel **centro di pressione** (mai il baricentro del triangolo: la barca s'inclinerebbe in
  acqua piatta); attrito ITTC 1957; pressione/suzione con limite di ventilazione (e' la planata);
  impatti (slamming) per triangolo con tetto `SlamMaxG`; aria/vento sui pezzi emersi; resistenza d'onda
  globale (curva dei dislocanti × `WaveResistanceScale`, dissolta col Froude volumetrico nei plananti);
  smorzamento di sicurezza in frazioni di massa/inerzia × area immersa.
- **Massa aggiunta**: F' = F·m/(m + A) per asse in assi barca, A × (area immersa / area statica).
  L'area statica e il galleggiamento d'equilibrio (`HydroHull.SolveHeave`, bisezione sulla stessa
  idrostatica del runtime) si ricalcolano se la massa cambia di oltre il 5%.
- **Idrostatica come strumento**: `HydroHull.ComputeStatics`/`SolveHeave` e
  `BoatController.DebugRightingArm(φ)` (GZ a dislocamento costante). Del centro di carena contano solo
  X e Z: con forze solo verticali la quota del punto d'applicazione non e' definita.
- **Coefficienti** (`HydroCoefficients`, `resources/boats/hydro_barchino.tres`, sotto-risorse di
  `gozzo`/`peschereccio`): plananti `c_p2` 0,4 (esponente 1); dislocanti `c_p2` 0,1-0,15 e `c_s2`
  0,05-0,1, perche' la pressione per triangoli sulla prua conta due volte la resistenza d'onda (con
  0,55 il gozzo faceva 6,3 kn col 62% di regresso). Vettori: lineari (avanzo, deriva, sussulto),
  angolari (rollio, beccheggio, imbardata).

## 11. Propulsione e appendici (`vehicles/scripts/Propulsion/`)

- **`MarineEngine`** (+ `EngineDefinition`, `GearboxDefinition`): regime integrato a 6 sotto-passi
  contro la coppia dell'elica. Benzina = farfalla diretta + PI del minimo, curva con picco +12% al
  70% dei giri; diesel = leva → regime obiettivo, PI (il regime tiene sotto carico). Limitatore con
  isteresi 2%, perdite di pompaggio a farfalla chiusa (senza, il motore restava su di giri 5 s e il
  cambio aspettava). **Leva unica** −1..1: ±10% folle, poi marcia al minimo, poi gas; il cambio passa
  dal folle, aspetta `ShiftSeconds` (+ `NeutralDwellSeconds` se inverte) e giri ≤ `MaxShiftRpm`.
- **`PropulsionUnit`** (+ `PropellerDefinition`): K_T/K_Q lineari in J (tabella P/D se 0, tarabili),
  retromarcia, mantello Kort, immersione del disco, ventilazione con isteresi (perde spinta, coppia
  cala, motore su di giri), effetto evolutivo f(velocita') e reazione di coppia (elica destrorsa:
  avanti sbanda a sinistra, indietro poppa a sinistra = prua a dritta).
- **`OutboardDrive`**: R = Rx(−trim)·Ry(sterzo) sul perno dello specchio (origine del modello del
  motore). Sterzo positivo = vira a dritta; trim positivo = trim out (spinta verso il basso, prua su).
  Piede (`LowerUnit`) come profilo nel piano del motore. **Trim**: assistenza di default
  (`AutoTrim*`: trim in all'uscita, crociera sopra ~9 m/s); i tasti Q/E (§12) lo passano a mano fino a
  quando si lascia il timone.
- **`ShaftDrive`**: asse fisso da `Mount_Prop`, timone a `Mount_Rudder` con velocita' d'attuatore
  (`RudderRateDegrees`: 40 a mano, 4 idraulico) e scia dell'elica dal disco attuatore solo con spinta
  avanti. Senza flusso il timone non fa nulla; col colpo d'elica gira la poppa da fermi.
- **`Foil`**: forza normale di Fujii, stallo, flusso inverso, area immersa per frazione d'apertura.
  Chiglie lunghe in `ChordSegments` lungo `Chord`: un profilo in un punto solo non smorza l'imbardata
  (il gozzo virava in 1,7 L con 45° di deriva).
- **`BowThruster`**: forza laterale nel tunnel, efficace solo sotto ~2 kn e col tunnel immerso.
- Convenzioni di segno verificate da `HydroSelfTest`: timone a dritta → forza a sinistra; sterzo a
  dritta → spinta a sinistra; trim out → spinta verso il basso.

## 12. Masse, pesi a bordo, comandi

- **`BoatMassModel`**: scafo come guscio sulla mesh idrodinamica (inerzia realistica) spostato sul
  baricentro di progetto `HullCenterOfMass`, motore, `MassItems`, piu' masse DINAMICHE: ogni giocatore
  con `SyncAnchorId` = questa barca pesa `CrewBodyMass` (80 kg) + inventario (host), ai piedi
  (`SyncPosition − PilotBodyCenter`). Ricalcolo a 10 Hz, scritto sul corpo solo se cambia (0,5% di
  massa o 1 cm di baricentro); l'origine non si muove. Un passeggero di 90 kg a 0,7 m sbanda il
  barchino di ~7° (idrostatica 7,3°). `DebugSetMass` per le prove.
- **Baricentro e carena**: il barchino ha il centro di carena molto a poppa (specchio largo, prua
  fine): con `HullCenterOfMass` a z 0 la barca vuota stava appruata di 4°. Spostando la stellatura o
  le forme, ricontrollare l'assetto a riposo con `statics`.
- **Comandi** (`VehicleInput`, `SubmitControls(leva, governo, trim, elica di prua)`, tutti −1..1,
  validati e saturati dall'host): `vehicle_trim_up`/`down` (E/Q), `vehicle_thruster_left`/`right`
  (Z/X), riassegnabili (gruppo "Barche" di `InputBindings`). Il trim e' una velocita' (3°/s).

## 13. Prove (`tools/boat_trials.gd`)

```
Godot --path . --headless --fixed-fps 60 --script tools/boat_trials.gd -- <preset|all> [prove]
```
`--fixed-fps` = tempo simulato: riproducibile e veloce. Prove: `self` (centro di pressione, taglio
continuo, cubo, chiatta GZ = formula wall-sided, segni, elica 30 kW = 410 kgf), `statics`
(massa, G, GM, curva GZ, raggi d'inerzia, ms/passo, equilibrio), `drop`, `roll` (periodo e ζ),
`heel` (peso a bordo contro l'idrostatica), `bollard`, `speed` (gobba, planata, regresso), `trim`,
`turn` (diametro in L, sbandamento, deriva), `stop`, `reverse`, `kick`, `thruster`, `waves` (mare 3 e
4, prua 135°/traverso/poppa: nessun NaN). Bersagli in `TARGETS` (dalle specifiche §14). Acqua FERMA
(esposizione 0) per la statica: anche il mare calmo aperto ha onda lunga.

Fatti utili: il mare 3 (Hs 2,5 m, λ 66 m, ~6,5 s) e' in risonanza col rollio del peschereccio
(6,6 s): al traverso 30°+ e' il comportamento atteso (E4), non un errore. Costo per passo (build
Debug): barchino ~0,45 ms, gozzo ~0,8, peschereccio ~1,2 (budget §18.4: 0,3-0,5).

## 14. Lacune volute (fisica)

Fuori dal primo giro, da fare quando servono: allagamento e acqua imbarcata (la coperta e' chiusa
alla falchetta: le barche aperte non si riempiono), carichi non rizzati e liquidi con superficie
libera, LOD a sonde e "parcheggio" per barche lontane (oggi ogni barca costa il modello completo),
griglia d'acqua ("patch") e multithread per le barche grandi, bimotore e passo variabile, rete a
strascico/ancora/rimorchio/danni, debug draw 3D delle forze e grafici di telemetria, predizione lato
client del pilota. **Taratura aperta**: il trim del fuoribordo cambia poco assetto e velocita'
(C5 vuole 5-15%), assetto in planata ~2° (bersaglio 3-5°), deriva in virata ~30° (bersaglio 5-20°).
In tempesta improvvisa (mare 4, Hs 5 m) a tutta forza il barchino puo' capovolgersi e, a coperta
chiusa, raddrizzarsi da solo. Niente cime d'ormeggio; allestimenti non cambiabili a partita in corso;
niente suono del motore; i passeggeri non si siedono.

