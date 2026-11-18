---
name: vehicles-boats
description: Veicoli e imbarcazioni. Carica quando tocchi vehicles/, oppure si parla di barche, acqua, galleggiamento, timone, guida, passeggeri, ponte calpestabile, piattaforme mobili, coordinate locali a un veicolo, SyncAnchorId, il trasporto del giocatore in PlayerController, i layer 5/6 (BoatController, VehicleInput, VehicleRegistry, VehicleInteraction, WaterVolume, Boat.tscn).
---

# Veicoli e imbarcazioni

Una barca statica nel livello, galleggiamento per punti sulle onde, un pilota e passeggeri che
camminano sul ponte in movimento. `vehicles/` + la laguna in `TestLevel.tscn` + il trasporto in
`PlayerController`.

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
  La barca chiama `Buoyancy.Apply` sui 6 `Floaters` (prua, mezzo, poppa): spinta sulla superficie
  con le onde, smorzamento `VerticalDampFactor` relativo all'acqua (3,0 = il vecchio 1,5 globale).
- `WaterVolume` non ha corpo fisico: niente layer `water`, niente da escludere dalle maschere.
- Galleggiamento auto-bilanciato (`Mass * g / floater * 2`): nessuna quota da ritarare.
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

`DebugAutoDrive`/`DebugSteer` su `BoatController`. Si collauda anche headless con due processi e un nodo
che chiama `NetworkManager.HostGame`/`JoinGame`. In multi-istanza: il passeggero remoto è fermo rispetto
al ponte; chiudendo la finestra del pilota la barca si arresta entro 0.5 s.

## 8. Lacune volute

Una sola barca (nessuno spawner: NodePath identico su ogni peer). Niente inerzia sul passeggero,
danno, HUD del veicolo, `ItemPickup` sul ponte (vivono a `y = 0` in mondo). **Nessun reparent** del
`Player` sotto la barca: romperebbe spawner e NodePath di RPC.
