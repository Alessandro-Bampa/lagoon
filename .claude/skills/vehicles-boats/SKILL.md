---
name: vehicles-boats
description: Veicoli e imbarcazioni. Carica quando tocchi vehicles/, resources/boats/, oppure si parla di barche, galleggiamento, fisica delle barche (scafo per triangoli, planata, resistenze, impatti, massa aggiunta), motore, elica, invertitore, trim, timone, chiglia, elica di prua, pesi a bordo, guida, passeggeri, ponte calpestabile, piattaforme mobili, coordinate locali a un veicolo, SyncAnchorId, il trasporto del giocatore in PlayerController, i layer 5/6 (BoatController, BoatPhysics, HydroSolver, HydroHull, PropulsionUnit, OutboardDrive, ShaftDrive, Foil, BoatMassModel, VehicleInput, VehicleRegistry, BoatGates e cancelli scorrevoli, Boat.tscn, vehicles/scenes/boats/, tools/boat_trials.gd), o si crea una barca nuova.
---

# Veicoli e imbarcazioni

Barche su misura col nucleo fisico per triangoli (§9-§13): la scena generica `vehicles/scenes/Boat.tscn`
e una scena per barca in `vehicles/scenes/boats/`. Un pilota e passeggeri che camminano sul ponte in
movimento. `vehicles/` + `resources/boats/` + il trasporto in `PlayerController`. Barca nuova: §9b.


> Barca nuova da un modello sorgente: si parte dalla skill `asset-ingest` (categoria `boat`,
> `tools/blender/ingest/boat.py`), che semplifica il modello, ne porta la texture nello stile del
> gioco e produce da solo i passi 1-2 di §9b (`.glb` con `HydroHull`, collisioni, vuoti e materiali
> per nome; `BoatConfig` da un modello di configurazione con le masse bilanciate sull'LCB). Restano
> statica e dinamica (§9b passi 4-5) e il livello.

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

- L'acqua (onde, marea, `WaterVolume`) è un sistema a sé: skill **`water-ocean`**. Le barche usano
  solo il nucleo per triangoli (§10); `Buoyancy` per punti resta per le casse (`FloatingBody`).
- `WaterVolume` non ha corpo fisico: niente layer `water`, niente da escludere dalle maschere.
- `TestLevel`: `Floor 0 → Ramp (20°) → Dock 1.25`, accanto il barchino a volante (id 1, a x 31,
  scostato dalla scaletta del molo). Dal molo ci si cala a bordo; dall'acqua si sale dalla scaletta
  di poppa. `Seabed` a −4; `water_respawn` resta solo come rete di sicurezza contro il vuoto.
- **Nuoto attorno alla barca**: lo scafo del `RigidBody` (layer 5) è fuori dalla maschera del
  giocatore, quindi chi nuota gli passerebbe attraverso. Per questo ogni modello ha `SwimCollision`
  (fasciame esterno aperto in alto) che `BoatAssembly` mette come trimesh su `Deck`, layer 6: parete
  per chi nuota, invisibile a chi sta dentro. La scaletta e' un accessorio (`accessory_ladder.tres`,
  `Groups = swim_ladder`): accanto ci si issa fino a 1,6 m. La salita a nuoto
  (`CharacterMotor.TryStartSwimClimb`) cerca la parete a piu' quote dal pelo dell'acqua in su (lo
  specchio di un barchino pesca 0,1 m e con l'onda si alza) e su un bordo sottile con spazio oltre
  (falchetta) SCAVALCA invece di issarsi in cima. Serve anche un piano calpestabile subito dentro il
  bordo (ponticello di poppa nei `DeckCollision`): senza, la sonda trovava il fondo interno dello
  scafo. `GroundProbe` e' lungo 1,5 m: chi sta sullo spigolo di una panca deve comunque agganciare
  l'ancora. Se il bordo di una manovra è una barca, la traiettoria si sposta con lei
  (`PlayerController.CarryManeuver`, `CharacterMotor.ShiftManeuver`) e l'ancora si riacquista a
  manovra finita.
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

## 9. Barche su misura (`Boat.tscn` + `vehicles/scenes/boats/`)

`vehicles/scenes/Boat.tscn` e' la scena GENERICA (corpo rigido, `Sync`, `Deck`, `Visual`, `HelmSeat`,
`Assembly`); ogni barca ha la sua scena ereditata in `vehicles/scenes/boats/<Nome>.tscn` che imposta
solo `Assembly.Config`. I livelli istanziano quelle e cambiano solo posa e `VehicleId` (unico nel
livello). Le proprieta' del `RigidBody3D` nell'ispettore (massa, smorzamenti, gravita') non contano:
le scrivono `BoatMassModel` e `BoatController._Ready`. La barca la monta a runtime una `BoatConfig`
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
| `vaporetto` / `vaporetto_worn` | vaporetto 23,9 × 4,2 m, 24,9 t, corpo 0,66 m + skeg a 1,44 m, cancelli scorrevoli | diesel 147 kW, asse, timone idraulico 12°/s, ruota a raggi | ~10,1 kn, 1754 kgf a punto fisso, GM 2,3 m, rollio 2,5 s, virata 3,3 L |
| `lancia_cabinata` | lancia da lavoro 11,9 × 3,5 m, 9 t, cabina a mezza nave, pozzetto a poppa; da modello sorgente (`asset-ingest` §5, manifesto `lancia_cabinata.json`: il `.tres` lo RISCRIVE la pipeline) | diesel 55 kW, asse + timone (modello del gozzo con `config.set`), ruota del modello senza consolle | ~8,7 kn, 625 kgf a punto fisso, GM 0,94 m, rollio 2,8 s, virata 2,7 L |

- **Modelli**: `tools/blender/build_lagoon_boats.py` (skill blender-pipeline) → `assets/models/boats/`.
  Nomi letti dal codice: `Seat_Helm`, `Mount_*`, `HydroHull` (mesh idrodinamica, §10),
  `HullCollision` (convesso del corpo), `DeckCollision` (trimesh del ponte, layer 6), `SwimCollision`
  (fasciame aperto per chi nuota, layer 6, §6);
  motore `Swivel`/`Propeller`/`Thrust`/`Tiller`/`TillerGrip`; consolle `Wheel`/`Hand_L`/`Hand_R`;
  entrobordo `Mount_Prop` (−Z = spinta, asse inclinato), `Mount_Rudder` (Y = asse della pala),
  `Mount_Engine`, `Mount_Thruster`, `Mount_Keel`/`Mount_Skeg`, parti mobili `Rudder`/`Propeller`;
  cancelli `Gate_*` + `Gate_*_Open` (vedi sotto).
  Materiali per nome ricolorati dalla livrea su una COPIA (i materiali del .glb sono condivisi).
- **Vaporetto** (`tools/blender/build_vaporetto.py`, da un modello Sketchfab CC-BY-4.0 fuori dal
  repo; dati e fonti nella docstring): due varianti con la STESSA fisica (`vaporetto_hydro`,
  `vaporetto_inboard`, `vaporetto_skeg` condivisi) e solo modello e consolle diversi; `HullMass` e
  `HullCenterOfMass` sono ripetuti nelle due `.tres`: tenerli uguali. Variante rovinata = stessi nomi
  di materiale, altre immagini: la livrea moltiplica, quindi su uno scafo scuro la ruggine si vede
  poco. Murate VERTICALI dalla DWL al ponte: con la V fino in coperta la murata sporgeva sopra l'acqua
  e chi nuota non trovava la parete per issarsi. Pilota su una **pedana** di 0,50 m (gradino 0,25 m):
  seduto sul pagliolo aveva gli occhi (Seat_Helm + 1,20) sotto il davanzale del parabrezza;
  `check_boat_model.py --console` ora lo controlla per ogni barca (pipeline §4.1 "Visuale al timone"). Si sale dall'acqua solo dai varchi a cancello aperto
  (bordo libero 0,5 m, niente scaletta). Texture incorporate non compresse
  (`gltf/embedded_image_handling=3`, come le armi): col default estratto e compresso i pixel PSX
  si impastano.
- **Cancelli scorrevoli** (`BoatGates`): nel modello `Gate_<lato>_<pannello>` nella posa chiusa e
  accanto il vuoto `<nome>_Open`; i pannelli con lo stesso lato sono un cancello (un bit, lati in
  ordine alfabetico). Stato `SyncGateOpenMask` (host, replicato on-change nel `Sync` della barca),
  intento `SubmitToggleGate`/`RequestToggleGate` validato su portata ORIZZONTALE dalla posizione
  replicata (`GateDistance`: chi nuota ha la capsula 0,5 m sotto l'acqua e il varco ~1,7 m sopra, in
  3D non arrivava mai a portata), dislivello ≤ 2,6 m, 0,6 s fra due comandi. Ogni peer fa scorrere
  pannelli e una `BoxShape3D` per pannello sul `Deck` dallo stato replicato (`TravelSeconds` 2).
  F: `VehicleInteraction.Target` sceglie il piu' vicino fra timone e cancello, contro i pickup come
  prima. Nessun prompt a video (come il timone). Collaudi: `playtest_boats.gd -- <dir> Vaporetto`
  (salita dal varco chiuso/aperto), `playtest_gates_net.gd` host + client.
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
  3 km, pontile, i due barchini, gozzo (id 13), peschereccio (id 14), vaporetto (id 15), lancia
  cabinata (id 16, a est del pontile fra barchino a volante e peschereccio), casse; `SeaStateDebugControls`:
  PagSu/PagGiu' stato del mare, Home laguna/mare aperto (solo host), e al timone una riga con nodi,
  giri, marcia, trim, assetto, sbandamento (dallo stato replicato). Non ci sono cime d'ormeggio: in
  tempesta una barca lasciata accanto al pontile fisso ci finisce sotto.

## 9b. Anteprima nell'editor e barca nuova

**Anteprima.** Aprendo la scena di una barca (`vehicles/scenes/boats/X.tscn`, NON dentro un livello)
`BoatAssembly` (`[Tool]`) mostra lo scafo montato, il piano dell'acqua al galleggiamento d'equilibrio
in acqua ferma, G (rosso), il centro di carena (blu, sul piano dell'acqua) e le masse (arancio); nel
gruppo "Anteprima (editor)" dell'`Assembly` il rapporto `Hydrostatics` (massa, G, pescaggio, assetto,
bordo libero, velocita' di carena, GM, rollio stimato, curva GZ, raggi d'inerzia, punto fisso
stimato, velocita' di passo) e il pulsante **Ricalcola anteprima**. Stesso codice del gioco
(`HydroHull.SolveFloat`/`RightingArm`, `BoatMassModel`, `PropulsionUnit.EstimateBollard`): quello che
dice e' quello che fa la barca in acqua ferma. Le modifiche DENTRO le risorse (coefficienti, masse,
motore) non avvisano il nodo: dopo averle cambiate si preme il pulsante. Nodi d'anteprima senza
owner: non finiscono nella scena. Taratura: ispettore → `Assembly` → `Config` (risorse condivise:
cambiare `hydro_barchino.tres` cambia entrambi i barchini) → anteprima → `boat_trials.gd` per la
dinamica.

**Barca nuova** (stesso codice, solo dati). Specifica completa — dati reali e fonti, modello da CAD
(GrabCAD), geometrie per ruolo, tolleranze, criteri d'accettazione — in
[`pipeline-nuove-barche.md`](pipeline-nuove-barche.md); controllo del .glb con
`tools/blender/check_boat_model.py`. In breve:
1. **Modello** in `tools/blender/build_lagoon_boats.py` (o un .glb fatto a mano con gli stessi nomi):
   assi +Y Blender = prua (−Z Godot), origine sulla linea d'acqua di progetto. Obbligatori: mesh
   `HydroHull` CHIUSA low-poly (lato del fasciame bagnabile ≤ L/10: oggi 0,44 / 0,66 / 1,35 m;
   coperta chiusa alla falchetta),
   `HullCollision`, `DeckCollision` (pagliolo, panche, murate e un piano subito dentro il punto dove si
   sale dall'acqua), `SwimCollision`, `Seat_Helm` + panca a +0,46 m. Poi, secondo la propulsione:
   `Mount_Motor` (fuoribordo) oppure `Mount_Prop`/`Mount_Rudder`/`Mount_Engine` + parti `Rudder` e
   `Propeller`; `Mount_Console` per il volante; `Mount_Ladder` (−Z verso l'interno); `Mount_Thruster`,
   `Mount_Keel`. Uno scafo parametrico si fa come `build_gozzo`/`build_trawler` (`HullSpec`).
2. **Configurazione** `resources/boats/<nome>.tres` (`BoatConfig`): `Hull`, `HullMass`,
   `HullCenterOfMass`, `Hydro` (partire da `hydro_barchino.tres` per un planante o dalla sotto-risorsa
   del gozzo per un dislocante), `Motor` **oppure** `Inboard` (motore, invertitore, elica, timone),
   `Thruster`, `FixedFoils` (chiglia con `ChordSegments`), `MassItems`, `Helm`/`Console`,
   `Accessories` (scaletta), `Livery`. Le sotto-risorse si creano dall'ispettore o si copiano da
   `gozzo.tres`/`peschereccio.tres`.
3. **Scena** `vehicles/scenes/boats/<Nome>.tscn`: Nuova scena ereditata da `Boat.tscn`, `Assembly.Config`
   = la configurazione. Aprirla: l'anteprima dice se galleggia dritta e quanto pesca.
4. **Statica**: con l'anteprima si sistemano masse e `HullCenterOfMass` (non i coefficienti) fino a
   pescaggio, assetto (~0°) e GM della classe (specifiche §14).
5. **Dinamica**: `boat_trials.gd -- res://vehicles/scenes/boats/<Nome>.tscn` (bersagli assenti = solo
   misure; per i bersagli aggiungere una voce in `TARGETS`). Ordine di taratura: punto fisso (kt0/kq0
   dell'elica), velocita' e assetto (`c_p2`, `WaveResistanceScale`), virata (timone/chiglia,
   `LateralPressureScale`), rollio (inerzia, `SafetyAngular`), onde (`SlamK`).
6. **Livello**: istanziare la scena, `VehicleId` unico; per vederla in gioco, `playtest_boats.gd`
   (aggiungere il nome del nodo alla lista) prova timone, guida e salita dall'acqua.

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
- **`HydroSolver`**, per passo: acqua campionata una volta per vertice (`WaterVolume.SampleLocal`
  su un `WaveLocalFrame` preparato al baricentro: fase grande in double una volta per onda, il resto
  in float come lo shader, onde spente saltate, esposizione unica per lo scafo; inversione di
  Gerstner ripartendo dal passo prima, 2 iterazioni) con stiramento di Wheeler
  (profondita' di pressione e velocita' orbitale attenuate con e^(−k·d), k del picco dello spettro:
  senza, il peschereccio rollava il triplo); taglio di Sutherland-Hodgman continuo; spinta SOLO
  verticale nel **centro di pressione** (mai il baricentro del triangolo: la barca s'inclinerebbe in
  acqua piatta); attrito ITTC 1957; pressione/suzione con limite di ventilazione (e' la planata);
  impatti (slamming) per triangolo con tetto `SlamMaxG`; aria/vento sui pezzi emersi; resistenza d'onda
  globale (curva dei dislocanti × `WaveResistanceScale`, dissolta col Froude volumetrico nei plananti);
  smorzamento di sicurezza in frazioni di massa/inerzia × area immersa.
- **Massa aggiunta**: F' = (F − ω × (M v)) ⊘ (1 + a) + m·(ω × v) per asse in assi barca (M = m + A,
  v relativa all'acqua), A × (area immersa / area statica). Il termine in ω e' essenziale: la sola
  F·m/(m + A) applicava la massa aggiunta di DERIVA anche all'accelerazione centripeta di una virata
  a regime (che invece dipende da quella d'avanzo): deriva gonfiata, punto di rotazione davanti alla
  prua. Il momento di Munk (v × A v) e' escluso di proposito.
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
- **`LateralPressureScale`** (gozzo 4, peschereccio 5,5, plananti 1): moltiplica la parte di
  pressione/suzione dovuta alla DERIVA, con forza solo laterale. Il `c_p2` basso dei dislocanti
  toglieva anche la presa laterale: la barca scivolava finche' il timone non si allineava al flusso
  (deriva ≈ angolo di barra, ~30°, su ogni barca). Riporta il laterale a ~0,55/0,3 (lastra, §8.3).
  NON scalare la pressione lungo la normale delle facce: sulle sentine darebbe forze verticali
  spurie e sbandamento verso l'interno (provato).
- **Plananti** (`hydro_barchino.tres`): `PressureLeadingEdge` 1 = pressione dinamica del FONDO
  lineare da 0 sullo specchio a 2× al bordo d'attacco (centro di pressione a 2/3 della lunghezza
  bagnata, Savitsky ~0,7); `TransomRelief` 0,5 = specchio ventilato: in planata l'idrostatica cala
  verso lo specchio (pesata col Froude volumetrico, come la resistenza d'onda). Con la pressione
  uniforme la barca planava a 1,4° su una superficie troppo lunga, un terzo del peso su Archimede.
  `TransomRelief` 1 e' troppo: sopra +6° di trim la barca salta a 8° di assetto.

## 11. Propulsione e appendici (`vehicles/scripts/Propulsion/`)

- **`MarineEngine`** (+ `EngineDefinition`, `GearboxDefinition`): regime integrato a 6 sotto-passi
  contro la coppia dell'elica. Benzina = farfalla diretta + PI del minimo, curva con picco +12% al
  70% dei giri; diesel = leva → regime obiettivo, PI (il regime tiene sotto carico). Limitatore con
  isteresi 2%, perdite di pompaggio a farfalla chiusa (senza, il motore restava su di giri 5 s e il
  cambio aspettava). **Leva unica** −1..1: ±10% folle (`NeutralBand`), poi marcia al minimo, poi gas;
  il cambio passa dal folle e innesta `ShiftSeconds` dopo aver LASCIATO la marcia (il tempo in folle
  conta), senza aspettare che i giri calino: all'innesto la frizione riporta il regime a
  `MaxShiftRpm` se e' piu' alto. Dalla crociera a R l'elica gira all'indietro 0,07 s (barchino) -
  0,6 s (peschereccio) dopo l'arrivo in R1. Prima c'erano permanenza in folle e attesa dei giri: la
  retromarcia "non prendeva".
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
- **Manetta a tacche** (`ThrottleNotches`): al timone `move_up`/`move_down` (W/S) spostano la leva di
  UNA tacca per pressione (niente ripetizione), e la leva resta li' anche a inventario aperto.
  Tacche R2 · R1 · N · 1 · 2 · 3 · 4: avanti minimo in marcia (manovra) / 35% / 65% / tutta forza,
  indietro minimo / tutta forza. Lo sterzo (A/D) resta a pressione. La tacca vive SOLO sul client
  (`VehicleInput._notch`, torna N a ogni cambio di timone): l'host riceve la leva continua di sempre,
  cioe' la posizione della manetta (un intento), e non conosce le tacche — collaudi e
  `DebugThrottle` possono chiedere una leva qualunque. Leva di una tacca = `MarineEngine.LeverFor`
  (inversa esatta di `Command`: la tacca 1 vale proprio `NeutralBand`). A regime (acqua ferma):
  barchino a barra 2,2 / 5,8 / 17,3 / 25,4 kn (la 2 sta sotto la gobba, la 3 gia' in planata),
  gozzo 1,7 / 4,2 / 5,6 / 6,8, vaporetto 3,7 / 6,3 / 8,2 / 10,1, peschereccio 3,4 / 6,3 / 8,3 / 10,0.
  La tacca si vede solo nella riga di prova di `SeaStateDebugControls` ("manetta 3 (F)", dalla leva
  replicata `SyncThrottle`); nessun indicatore nella HUD vera (lacuna §8).

## 13. Prove (`tools/boat_trials.gd`)

```
Godot --path . --headless --fixed-fps 60 --script tools/boat_trials.gd -- <preset|all|percorso> [prove]
```
Percorso = una scena di barca (`res://vehicles/scenes/boats/X.tscn`) o una `BoatConfig` (`.tres`,
montata su `Boat.tscn`): una barca nuova si prova senza toccare lo script. Le prove di trim, colpo
d'elica ed elica di prua si scelgono dal tipo di propulsione (telemetria `drive`, `thruster`).
`statics` stampa anche il rapporto dell'anteprima (`BoatAssembly.DebugHydrostatics`).
`--fixed-fps` = tempo simulato: riproducibile e veloce. Prove: `self` (centro di pressione, taglio
continuo, cubo, chiatta GZ = formula wall-sided, segni, elica 30 kW = 410 kgf), `statics`
(massa, G, GM, curva GZ, raggi d'inerzia, ms/passo, equilibrio), `drop`, `roll` (periodo e ζ),
`heel` (peso a bordo contro l'idrostatica), `bollard`, `speed` (gobba, planata, regresso), `trim`,
`turn` (diametro in L, sbandamento, deriva), `stop`, `reverse`, `kick`, `thruster`, `waves` (mare 3 e
4, prua 135°/traverso/poppa: nessun NaN). Bersagli in `TARGETS` (dalle specifiche §14). Acqua FERMA
(esposizione 0) per la statica: anche il mare calmo aperto ha onda lunga.

Fatti utili: il mare 3 (Hs 2,5 m, λ 66 m, ~6,5 s) e' in risonanza col rollio del peschereccio
(6,6 s): al traverso 30°+ e' il comportamento atteso (E4), non un errore. Costo per passo (media
sulle prove `waves`, build Debug): barchino ~0,45 ms, gozzo ~0,75, peschereccio ~1,0 (budget §18.4:
0,3-0,5). In acqua ferma ~0,4/0,55/0,8. Ora domina il ciclo sui triangoli (taglio, idrostatica,
aria), non piu' l'acqua: un'ottimizzazione JIT (`-p:Optimize=true`) non cambia quasi nulla.
Sonda `_probe_boat.gd` della sessione di taratura: rimossa; il punto di rotazione in virata si
misura come v_laterale/ω_imbardata in assi barca.

## 14. Lacune volute (fisica)

Fuori dal primo giro, da fare quando servono: allagamento e acqua imbarcata (la coperta e' chiusa
alla falchetta: le barche aperte non si riempiono), carichi non rizzati e liquidi con superficie
libera, LOD a sonde e "parcheggio" per barche lontane (oggi ogni barca costa il modello completo),
griglia d'acqua ("patch") e multithread per le barche grandi, bimotore e passo variabile, rete a
strascico/ancora/rimorchio/danni, debug draw 3D delle forze e grafici di telemetria, predizione lato
client del pilota. **Taratura aperta**: il trim del fuoribordo sposta l'assetto di ~0,6° e la
velocita' di <1% (C5 vuole 5-15%, prova `trim` in FAIL). Non e' un errore di segno: il momento del
trim (~500 N·m su tutta l'escursione, spinta ~930 N) sposta il centro della superficie bagnata di
~12 cm; anche Savitsky per questo skiff leggero e poco motorizzato da' solo 2-3%. Per avere un
trim che "si sente" servono forme (rocker, stellatura, prua a V) o una barca piu' potente.
Barchino in virata a tutta barra e tutto gas: deriva ~30° e perdita di velocita' ~50% (fondo
piatto senza spigoli: quasi niente superficie laterale immersa in planata; `LateralPressureScale`
2,5 la porta solo a 26°). Rollio del barchino a barra 0,99 s (bersaglio 1,0-1,6): GM 1,58 m in
cima alla banda e k_rollio 0,32·B sotto il tipico; si corregge dalle masse, non dall'acqua.
In tempesta improvvisa (mare 4, Hs 5 m) a tutta forza il barchino puo' capovolgersi e, a coperta
chiusa, raddrizzarsi da solo. Niente cime d'ormeggio; allestimenti non cambiabili a partita in corso;
niente suono del motore; i passeggeri non si siedono.

