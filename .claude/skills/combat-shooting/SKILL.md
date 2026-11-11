---
name: combat-shooting
description: Tiro e danno. Carica quando tocchi combat/ o CollisionLayers, oppure si parla di armi, sparare, danno, salute/HP, hitbox, mira, reticolo, munizioni, ricarica, rinculo, dispersione, traccianti, collision layer 3D (WeaponController, WeaponInput, WeaponDefinition, WeaponVisual, HealthComponent, HitboxComponent, AimResolver, ShotEffects, CrosshairOverlay, WeaponHudPanel, TargetDummy).
---

# Tiro e danno

Armi **hitscan**, mira al centro dello schermo (prima persona), danno host-authoritative.
`combat/scripts/` + `core/Utils/CollisionLayers.cs`.

## 1. Un colpo: intento dal client, tutto il resto dall'host

| Passo | Chi | Come |
|---|---|---|
| Intento | client | `WeaponInput` → `AimResolver.ResolveAimPoint(camera, propriaHitbox)` → `WeaponController.SubmitFire(aimPoint)`. Solo il punto mirato: mai bersaglio, origine o danno. |
| Calcolo | host | `RequestFire`: origine = `CharacterMotor.ResolvedEyePosition`, dispersione con RNG host, `AimResolver.TraceShot`, falloff. |
| Danno | host | `HealthComponent.ApplyDamage(amount, attacker, hitDirection)`: metodo normale, **non RPC**. |
| Stato | replica | `CurrentHealth`, `MagazineAmmo`, `ReserveAmmo`, `RecoilSpread`, `Reloading`, `HeldItemId` → **tutti** i peer. |
| Estetica | host → tutti | `BroadcastShot`, `BroadcastHitReaction`: RPC `Authority`, `Unreliable`. |

Validazioni di `RequestFire`, in ordine: mittente = proprietario · arma ancora nel suo slot
(`HoldStillValid`, altrimenti rinfodera) · non in ricarica · caricatore > 0 · mani libere · cadenza
(10% di tolleranza) · `aimPoint` finito · oltre `MaxRange` si **clampa**, non si rifiuta.

- **L'origine viene sempre dallo stato replicato**: mai `GlobalPosition` (interpolata sui remoti), mai
  `SyncPosition` grezza (su una barca è locale allo scafo → `ResolvedSyncPosition`).
- `HandsFree` legge `SyncVaulting` **replicato**, non `Vaulting`: il movimento è client-authoritative,
  l'host non esegue il parkour di un tiratore remoto.
- `RequestHold` **non** consulta `HandsFree`: rifiutare un cambio d'arma lascerebbe host e client in
  disaccordo su cosa si impugna.
- `WeaponInput.Suppressed` (modale, inventario, parkour) è il lato locale delle stesse regole: la vampa
  è immediata, e senza si vedrebbe partire un colpo che l'host non conta.
- `hitDirection` è la direzione di volo **dopo** la dispersione e alimenta il flinch. Viaggia la
  direzione, mai l'ammontare del danno. La RPC parte dopo `ApplyDamage`: un colpo rifiutato non reagisce.
- Triade delle RPC: `SubmitX` (host locale / client `RpcId(host)`) → `RequestX` (`AnyPeer` +
  `ValidateSender`) → proprietà replicate.

## 2. Prima persona

- **Mira = asse della camera.** `ResolveAimPoint` traccia dal centro dello schermo; nel vuoto ritorna
  un punto lontano sullo stesso asse. Mira e colpo usano la **stessa** maschera (`ShotMask`).
- **Il colpo parte dagli occhi**, non dalla canna: ciò che sta sotto al reticolo è ciò che viene
  colpito. L'host usa il punto nominale (`EyeHeight` 1.62 / `CrouchEyeHeight` 1.04 sopra i piedi); la
  camera del client segue la testa animata e gli sta a pochi cm. Se cambi rig o clip di crouch,
  **rimisura** quelle due quote.
- **Tracciante e vampa partono dalla bocca dell'arma** (`ShotEffects.MuzzleOffset` nel frame della
  presa): è solo estetica. Dall'origine vera sarebbe un segmento visto di punta.
- **Rinculo**: `RecoilSpread` (host, replicato) allarga il cono; `WeaponDefinition.CameraKick` (gradi)
  ruota la visuale in locale e rientra (`FirstPersonCamera.AddKick`). Il calcio sposta davvero il punto
  mirato finché non rientra: è voluto.
- **Tasto `aim`**: stringe solo il FOV, è locale. `SyncAiming` (arma alzata) vale "armato e non in
  corsa" e lo decide `PlayerController`.

## 3. Autorità sotto `Player`

| Nodo | Autorità |
|---|---|
| `Player` root + `Synchronizer` | peer proprietario (movimento e sguardo) |
| `Inventory`, `Health` + `Sync`, `Weapon` + `Sync` | **host** |
| `WeaponInput`, `Hud`, `PlayerCamera` | solo locali |

`PlayerController._EnterTree` marchia tutto il sottoalbero col peer proprietario; i nodi host-autoritativi
si riprendono l'autorità nel **proprio** `_EnterTree`. Per questo i loro `MultiplayerSynchronizer` devono
essere nodi **figli**, non fratelli.

## 4. Collision layer

`[layer_names]` in `project.godot`, specchiati da `CollisionLayers.cs`. Le scene scrivono i letterali.

| # | Nome | Chi | Mask |
|---|---|---|---|
| 1 | `world` | geometria statica, solai compresi | — |
| 2 | `players` | `Player` (`CharacterBody3D`) | 39 = 1+2+4+32 |
| 3 | `enemies` | `TargetDummy`, NPC | 1 |
| 4 | `hitbox` | `HitboxComponent` (`Area3D`) | 0: viene solo interrogata |
| 5 | `vehicles` | scafo (`RigidBody3D`) | 1 — **mai** nella maschera dei player |
| 6 | `vehicle_deck` | ponte e parapetti | 0 |

`ShotMask = world | hitbox | vehicles | vehicle_deck`, con `CollideWithAreas = true` ed esclusione del RID
della propria hitbox. Un raggio non colpisce mai il **corpo** di un personaggio, solo la hitbox: fuoco
amico e immunità a sé stessi sono esatti. Nuova entità danneggiabile = corpo sul proprio layer +
`HitboxComponent` su layer 4 con `HealthPath`.

## 5. Dispersione

`WeaponDefinition.SpreadDegrees(aimDistance, recoil)` è l'**unica** formula: la usano l'host per il dado
e `CrosshairOverlay` per l'anello (angolo → pixel col FOV corrente). Si replica solo `RecoilSpread`; il
termine di distanza lo aggiunge ogni client dalla propria mira. **Il dado si tira solo sull'host**:
nessun tracciante predittivo, arriva con `BroadcastShot`.

## 6. Limiti accettati

- **Nessuna lag compensation**: l'host ri-traccia da uno stato vecchio fino a ~1 RTT. Va fatta insieme
  alla validazione anti-cheat del movimento.
- Nessuna conseguenza della morte (`IsDead` e basta). Fuoco amico attivo, voluto.
- Il caricatore è stato host-side (`_magazines` per `InstanceId`), non un item di griglia.
- `ProjectileSpeed`, `Penetration`, `Caliber` sono dichiarati e non usati.

## 7. Verifica

Multi-istanza ENet: il client spara a un manichino e gli HP calano uguali su tutte le finestre. Niente
autocolpo guardandosi i piedi; l'anello si allarga col fuoco automatico; R ricarica solo a inventario chiuso.
