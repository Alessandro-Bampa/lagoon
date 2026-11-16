---
name: combat-shooting
description: Tiro e danno. Carica quando tocchi combat/ o CollisionLayers, oppure si parla di armi, sparare, danno, salute/HP, hitbox, mira, reticolo, munizioni, ricarica, estrazione, rinculo, dispersione, bloom, traccianti, collision layer 3D (WeaponController, WeaponInput, WeaponDefinition, HealthComponent, HitboxComponent, AimResolver, ShotEffects, HitMarkerOverlay, WeaponHudPanel, TargetDummy).
---

# Tiro e danno

Armi **hitscan**, mira al centro dello schermo (prima persona), danno host-authoritative, **nessun
reticolo**. `combat/scripts/` + `core/Utils/CollisionLayers.cs`. Come l'arma si vede e si anima
(viewmodel, posa di mira, rinculo visivo) è nella skill `character-animation`.

## 1. Un colpo: intento dal client, tutto il resto dall'host

| Passo | Chi | Come |
|---|---|---|
| Intento | client | `WeaponInput` → `AimResolver.ResolveAimPoint(camera, propriaHitbox)` → `WeaponController.SubmitFire(aimPoint)`. Solo il punto mirato: mai bersaglio, origine o danno. |
| Calcolo | host | `RequestFire`: origine = `CharacterMotor.ResolvedEyePosition`, dispersione con RNG host, `AimResolver.TraceShot`, falloff. |
| Danno | host | `HealthComponent.ApplyDamage(amount, attacker, hitDirection)`: metodo normale, **non RPC**. |
| Stato | replica | `CurrentHealth`, `MagazineAmmo`, `ReserveAmmo`, `Reloading`, `HeldItemId` → **tutti** i peer. |
| Estetica | host → tutti | `BroadcastShot`, `BroadcastHitReaction`: RPC `Authority`, `Unreliable`. |

Validazioni di `RequestFire`, in ordine: mittente = proprietario · arma ancora nel suo slot
(`HoldStillValid`, altrimenti rinfodera) · non in ricarica · estrazione finita (`DrawSeconds`) ·
caricatore > 0 · mani libere · cadenza (10% di tolleranza) · `aimPoint` finito · oltre `MaxRange` si
**clampa**, non si rifiuta.

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
- **Il colpo parte dagli occhi**, non dalla canna: ciò che sta al centro dello schermo è ciò che
  viene colpito. Host e client usano lo **stesso** punto nominale (`EyeHeight` 1.62 /
  `CrouchEyeHeight` 1.04 sopra i piedi): la camera ci sta sopra, non segue la testa animata.
- **Niente reticolo.** Dal fianco si spara a sentimento con un cono largo; in mira (tasto `aim`) le
  tacche del viewmodel cadono al centro dello schermo e il cono si stringe. `HitMarkerOverlay` disegna
  solo la conferma del colpo a segno.
- **`SyncAiming` = arma all'occhio**, replicato: serve all'host per scegliere il cono e agli altri
  peer per la posa. Lo pubblica il client (come tutto il suo movimento, client-autoritativo in questa
  fase): mentire vale un cono più stretto, mai un colpo che l'host non avrebbe tracciato. In mira ci
  si muove a `AimMoveFactor` della camminata e non si corre.
- **Tracciante e vampa partono dalla bocca dell'arma che si vede**: quella del viewmodel per chi
  spara, quella del corpo in terza persona per gli altri (`BroadcastShot`). È solo estetica.

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

## 5. Dispersione e rinculo: tre cose distinte

| | Dove | Chi | Cosa fa |
|---|---|---|---|
| **Dispersione** | `WeaponDefinition.SpreadDegrees(...)` | host | cono in cui il colpo devia: `AimSpread` in mira, `HipSpread` dal fianco, + `MoveSpread` (in proporzione alla velocità), + `AirSpread` in aria, × `CrouchSpreadFactor`, + **bloom** che si accumula sparando di fila (`BloomPerShot`, `×AimBloomFactor` in mira) |
| **Rinculo della visuale** | `Recoil*` di `WeaponDefinition` → `FirstPersonCamera.AddRecoil` | tiratore locale | la camera sale (`RecoilPitch`), scarta (`RecoilYaw` ± casuale + `RecoilYawBias`) e rolla; il bersaglio del rinculo rientra a `RecoilReturnSpeed`, la visuale lo insegue a `RecoilSnappiness` |
| **Rinculo visivo dell'arma** | `Kick*` di `WeaponAnimationSet` | ogni peer | l'arma arretra e impenna su una molla; nessun effetto sul colpo |

- `SpreadDegrees` è l'**unica** formula e la usa solo l'host, a partire da stato replicato del
  tiratore (`SyncAiming`, `SyncCrouching`, `SyncGrounded`, `SyncLocalVelocity`). Il bloom è stato
  privato dell'host (`_bloom`): non si replica perché nessun client lo disegna.
- Il rinculo della visuale **sposta davvero il centro dello schermo**, cioè il punto mirato del colpo
  dopo: è ciò che si compensa col mouse. A raffica i colpi si sommano più in fretta di quanto
  rientrino e la visuale sale. L'host non ne sa nulla: riceve solo la mira.
- **Il dado si tira solo sull'host**: nessun tracciante predittivo, arriva con `BroadcastShot`.
- `ReloadSeconds` e `DrawSeconds` devono combaciare con le clip `reload`/`draw` del viewmodel: sono
  l'unico punto in cui gioco e animazione si accordano.

## 6. Limiti accettati

- **Nessuna lag compensation**: l'host ri-traccia da uno stato vecchio fino a ~1 RTT. Va fatta insieme
  alla validazione anti-cheat del movimento.
- Nessuna conseguenza della morte (`IsDead` e basta). Fuoco amico attivo, voluto.
- Il caricatore è un **item** montato nello slot dell'arma (skill `inventory-tarkov`): `MagazineAmmo` sono i
  suoi colpi, `ReserveAmmo` i colpi nei caricatori di scorta in rig e tasche. Ricaricare = scambiare col
  caricatore di scorta più pieno (`InventoryOps.DropOnItem`); senza scorta non si ricarica. Le cartucce
  sfuse non contano. Mai verificato in gioco dopo la riscrittura dell'inventario.
- `ProjectileSpeed`, `Penetration`, `Caliber` sono dichiarati e non usati.

## 7. Verifica

`tools/playtest_fps.gd` (skill `character-animation` §8) copre impugnatura, raffica, ricarica e
l'allineamento della mira. Poi multi-istanza ENet: il client spara a un manichino e gli HP calano
uguali su tutte le finestre; niente autocolpo guardandosi i piedi; in mira i colpi vanno sulla punta
del mirino, dal fianco si allargano; R ricarica solo a inventario chiuso.
