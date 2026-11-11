---
name: character-animation
description: Animazione del personaggio — AnimationTree a layer, pose d'impugnatura, clip delta, layer procedurali e IK. Carica quando tocchi animation/, CharacterRig, il BlendTree, AdditiveClips/WeaponHoldPoses/CrouchClips.tres, CharacterAnimator, i bridge di animazione, SpineAimModifier, AimRig, FootIkRig, WeaponGripRig, VaultIkRig, FirstPersonBodyView, WeaponAnimationSet, quando aggiungi o rinomini una clip, agganci un'arma alla mano, il personaggio va in T-pose, le animazioni si fermano, un SkeletonModifier3D "non applica", o lavori su mira, rinculo, piedi a terra, reazioni ai colpi, parkour, ragdoll, corpo visto in prima persona.
---

# Animazione del personaggio

`animation/` + i due bridge (`PlayerAnimationBridge`, `NpcAnimationBridge`) + i tool `tools/build_*.gd` e
`tools/verify_*.gd`. Rig e clip vengono da `blender-pipeline`, il movimento da `CharacterMotor`.

## 1. Trappole mute

Nessuna dà errori a runtime. Escludile prima di ipotizzare altro.

1. **Godot 4.7 ripristina le pose dopo la passata dei modificatori.** `GetBoneGlobalPose` dal codice
   di gioco legge la posa **prima** dell'IK. Il risultato si legge solo dentro
   `_ProcessModificationWithDelta` o nel segnale `Skeleton3D.SkeletonUpdated` (così fa
   `FirstPersonCamera`). Scrivere pose da fuori un modificatore non ha effetto. `BoneAttachment3D`
   invece segue i modificatori.
2. **`TwoBoneIK3D` senza `pole_node` non risolve** (`pole_direction` da sola non basta). Le proprietà
   vanno impostate **dopo** `AddChild`.
3. **Creare un modificatore dentro `_Ready` pianta il processo**: sempre `CallDeferred`.
4. **L'ordine dei modificatori è l'ordine dei figli dello scheletro**, e nasce da `CallDeferred`. Un
   vincolo va risolto **dopo** tutto ciò che muove la sua catena: `WeaponGripRig` si riporta in coda da
   solo (`EnsureModifierRunsLast`), altrimenti la mano di supporto resta fino a 36 cm fuori dall'arma in mira.
5. **BlendSpace2D senza triangolo → T-pose.** Due punti, punti collineari, o posizione fuori
   dall'inviluppo: con i quattro punti cardinali la regione è il **rombo** `|x|+|y| ≤ velocità`, da cui
   `ClampToDiamond`. `auto_triangles` deve restare `true` (i triangoli espliciti non si serializzano).
   Le velocità dell'animatore devono combaciare con quelle del controller: le allineano i bridge.
6. **`loop_mode`** si imposta solo in `CharacterAnimations.glb.import`
   (`_subresources/animations/<clip>/settings/loop_mode = 1`). Clip nuova = riga lì + categoria in
   `_verify_loop_modes`. Non chiamare una clip `*_loop`: il suffisso viene strippato dal nome.
7. **Ogni `Blend2`/`OneShot` filtrato deve avere `sync = true`**, o le parti fuori filtro si congelano.
8. **I delta additivi non passano da glTF** (l'export bakea tutte le ossa; il rest della libreria non è
   quello di `Body_Base`). Tutto ciò che si consuma contro il rest dello scheletro **si calcola in Godot**.
9. **La chiave neutra di un delta è il REST, non l'identità**: `risultato = Base × (Rest⁻¹ × Chiave)`,
   authoring `Chiave = Rest × Riferimento⁻¹ × Target`. L'identità sulle clavicole (rest 115°) scambia
   le braccia di lato. `_neutral()` e `_key_for()` in `build_additive_clips.gd` sono gli unici produttori.
10. **Una posa d'impugnatura si valida sulla DIREZIONE della canna**, non su dove stanno le mani: la
    canna punta lungo mano destra → mano sinistra, e nessun layer di mira corregge uno scarto (ruotano
    il busto, le braccia seguono in blocco). `rifle_idle` è un porto di traverso (85° di scarto): la
    posa di mira deriva da `rifle_fire` con le **clavicole** riallineate. Anche il porto rilassato si
    ottiene ruotando le clavicole, non i bracci (la presa resta chiusa).
11. **Il polo del gomito di supporto vive nel frame dell'arma** (`SupportElbowHint` è figlio di
    `GripPoint`): cambiando `GripRotationDegrees` va rimisurato, o il gomito si piega al contrario.
12. **Eulero YXZ riavvolge oltre 90°.** Misura la direzione (`basis.z`), non l'angolo; il beccheggio
    dell'arma si applica attorno a `canna × alto` (`PitchMuzzleUp`), mai sommando gradi alla X.
13. **Le `crouch_*` di Mixamo hanno le braccia ferme e il busto piegato fino a 59°.** Le braccia le
    rigenera `build_crouch_clips.gd`; l'arma fuori dalle gambe la tiene `WeaponGripRig.CrouchLift`
    (clavicole 35° + canna 25°, solo fuori mira). Raddrizzare il busto è stato provato e scartato.
14. **A terra `Velocity.Y = 0`**: scriverla positiva su una rampa spegne lo snap al pavimento e la posa
    di caduta lampeggia. `SyncGrounded` ha un'isteresi (`GroundedGraceSeconds`).
15. Sonde: in headless il frame dura ~7 ms (usa `_settle_seconds()`); `intersect_ray` non vede le forme
    da dentro; l'effetto di un layer si misura sul suo **scatto**, non a regime.

## 2. Il BlendTree

`animation/resources/CharacterBlendTree.tres` è **generato** da `tools/build_animation_tree.gd`: non
editarlo a mano. `CharacterAnimator.cs` è l'unico accoppiamento C# ↔ nomi dei nodi (i `const` in cima).

```
WalkSpace ─┐
           ├ MoveBlend ─┐
RunSpace ──┘            ├ CrouchBlend ─┐
CrouchSpace ────────────┘              ├ AirBlend ─┐
FallClip ──────────────────────────────┘           ├ HoldMask (Blend2 FILTRATO, 8 ossa delle braccia)
WeaponPose (Transition: rifle_lowered | rifle_aim | pistol | pistol_aim) ┘        │
AimSpace (5 pose add/aim_*) ── AimAdd (Add2) ──────────────────────────────────────┤
FirePose ── Fire (OneShot ADD) ─ HitPose ── Hit (OneShot ADD) ─────────────────────┤
JumpScale ── Jump · LandPose ── Land · VaultPose ── Vault (OneShot BLEND full body) ┴ output
```

- **La locomozione è una sola e non sa nulla delle armi.** Mai una clip per combinazione arma ×
  movimento: se un'arma "richiede" una locomozione, la risposta è una posa d'impugnatura migliore o un
  delta upper-body in più.
- **`HoldMask` è una posa ASSOLUTA mascherata, non un delta**: la presa è un vincolo geometrico (due
  mani sulla stessa arma) e un delta lo rispetta solo sulla clip di riferimento. Busto e gambe restano
  alla locomozione; le braccia li seguono tenendo la presa.
- **Nessun filtro sui nodi additivi**: la maschera sta nelle CLIP (le `add/*` hanno solo le track di
  busto e braccia). L'unico nodo filtrato è `HoldMask`.
- Le richieste dei `Transition` (`FirePose`, `HitPose`, `LandPose`, `VaultPose`) vanno impostate
  **prima** di far partire il one-shot.
- Camminata e corsa sono due spazi (sullo stesso asse sarebbero collineari). Le diagonali si sintetizzano.
- `AimYawRangeDeg` (60) / `AimPitchRangeDeg` (45) devono combaciare con `AIM_YAW_DEG`/`AIM_PITCH_DEG`
  di `build_additive_clips.gd`.

### Librerie e rigenerazione

| Libreria | Contenuto | Generata da |
|---|---|---|
| `""` | clip assolute full body (Mixamo + procedurali) | Blender → `CharacterAnimations.glb` |
| `add` | delta additivi (`aim_*`, `hit_*`, `*_fire`) | `tools/build_additive_clips.gd` |
| `hold` | pose assolute delle sole braccia | `tools/build_weapon_poses.gd` |
| `crouch` | `crouch_*` con le braccia animate | `tools/build_crouch_clips.gd` |

Ordine, quando cambiano le clip (ogni passo è `Godot --path . --headless --script <tool>`):
`build_procedural_clips.py` (Blender) → `--import` → `build_weapon_poses` → `build_additive_clips` →
`build_crouch_clips` → `build_animation_tree`. Saltare il reimport genera delta contro clip vecchie.
Senza toccare Blender bastano gli ultimi quattro.

Nel `.glb` restano clip che l'albero **non usa più** (`rifle_walk_*`, `rifle_run_*`, `rifle_*_idle`,
`pistol_aim_idle`): cancellarle è irreversibile (le FBX non sono nel repo). Non ricablarle.

## 3. Stato in ingresso e contratti

`CharacterAnimator` è un **ricevitore puro**: non conosce `PlayerController` né il `Multiplayer`. I
bridge leggono **solo stato già replicato** e girano identici su ogni peer. Non aggiungere proprietà
replicate per l'animazione; le uniche eccezioni dichiarate sono lo sguardo e la stance:

| Proprietà | Significato |
|---|---|
| `SyncLocalVelocity` | **X = destra**, Y = avanti. Il Visual guarda +Z e la sua sinistra è +X: `WorldToLocalVelocity` nega la X. Senza, strafe e lean sono specchiati. |
| `SyncAimYaw` / `SyncAimPitch` | lo SGUARDO (per il giocatore: `FirstPersonCamera`), sempre pubblicato. Direzione = `CharacterAnimator.AimVector(yaw, pitch)`. |
| `SyncAiming` | arma alzata sullo sguardo: armato e non in corsa (latch `HipFireAimSeconds` se si spara correndo). Sceglie `*_aim` contro porto rilassato e accende aim offset + `SpineAimModifier`. |
| `SyncFacing` | imbardata del corpo: insegue lo sguardo smorzata. La sua derivata (`TurnRate`) alimenta il **passo sintetico** del turn-in-place: da fermi, mentre il corpo ruota, le gambe fanno lo strafe. |

Eventi one-shot (`Jumped`, `Landed`, `Vaulted`, `ShotResolved`, `HitReaction`): RPC riemesse come segnale
locale, con payload solo **geometrico** (velocità d'impatto, bordo/normale/altezza, direzione di volo).
`TriggerLand` ha tre regimi alternativi (`land_hard` ≥ `HardLandingSpeed`, `land_soft` ≥ 6.5, sotto solo
dip del bacino); `TriggerVault` sceglie vault/mantle dall'altezza (`MantleHeight` = `VaultMaxHeight`).

Lo smorzamento è sempre `1 − exp(−k·dt)`: l'animatore gira in `_Process`, il movimento in fisica.

## 4. Layer procedurali

Sotto `CharacterRig.tscn`, costruiti **da codice** (il rig è un `.glb` rigenerabile), pura resa.

| Nodo | Fa |
|---|---|
| `AimRig` + `SpineAimModifier` | ruota il rachide sulla mira **rimisurando l'errore prima di ogni vertebra** (a colpo unico restano 20°); lean laterale |
| `FootIkRig` | piedi a terra, **solo da quasi fermi** (`DisableAboveSpeed = 0.6`); risomma `PelvisDrop` per non assestarsi a metà |
| `WeaponGripRig` (+ `ShoulderLiftModifier`, `WeaponSpaceProbe`) | `GripPoint` sulla mano destra, IK della mano di supporto (**sempre** attivo, tranne nel parkour), rinculo, "port arms" contro i muri, `CrouchLift`, `Stowed` |
| `VaultIkRig` | mani sul bordo misurato durante vault/mantle; `ParkourActive` copre l'intera manovra |

- **Il bacino ha un solo scrittore** (`CharacterAnimator.UpdatePelvisOffset`): atterraggio + piedi.
- **Due IK sulle stesse ossa vogliono un arbitro**: per le mani decide `CharacterAnimator` (nel parkour
  vince il bordo).
- Il "port arms" spegne anche la mira del busto.
- I rig trovano lo scheletro con `SkeletonLocator` (il primo fra i fratelli).

## 5. Prima persona

- La camera segue l'osso `Head` (letto in `SkeletonUpdated`, smorzato relativo al corpo): **ogni clip
  muove la visuale**. Una clip con molto movimento di testa (atterraggio duro, mantle) si sente.
- La mesh è unica e skinnata, quindi la testa del giocatore locale si nasconde **sdoppiando la mesh**
  (`FirstPersonBodyView`, solo su quel peer): l'originale diventa `ShadowsOnly` e fa l'ombra intera,
  una copia `<nome>FirstPerson` con i vertici della testa collassati sull'origine dell'osso è l'unica
  che si vede. **Non nascondere la testa agendo sullo scheletro** (scala dell'osso): sparisce anche
  dall'ombra. Chi cambia materiale o visibilità della mesh del corpo a runtime deve farlo su entrambe.
- Il corpo segue sempre lo sguardo: non esiste più "guardare dove si va". La zona morta del
  turn-in-place (`PlanAimFacing`) è stata rimossa.
- Braccia e arma in vista sono quelle del rig in terza persona: la posa di mira è il viewmodel.
  Migliorarla = lavorare su `hold/*` e `GripRotationDegrees`, non aggiungere un secondo modello.

## 6. Aggiungere un'arma

1. `WeaponAnimationSet` in `animation/resources/` (bastano `two_handed.tres` e `one_handed.tres`);
   `FirePose` = nome di un ingresso del Transition `FirePose`.
2. `WeaponDefinition.AnimationSet` (+ `VisualScene` col modello nel frame della presa, o placeholder).
3. Non toccare albero, clip, `CharacterAnimator`.

`GripRotationDegrees` e `SupportGripOffset` **si misurano**: li stampa `build_weapon_poses.gd`, e
rigenerando le pose vanno ricopiati nei `.tres`. Oggi: fucile `(-66.1, 179.0, 0)` / `(0, 0, 0.254)`,
pistola `(-59.0, -158.8, 0)` / zero. Riferimento d'allineamento: `Spine2`, mai il bacino.

## 7. Parkour

`CharacterMotor.TryStartParkour` misura con `ObstacleProbe` e applica solo soglie: < 0.5 m gradino
(`TryStepUp`), 0.5–1.2 m **vault** (si passa oltre), fino a 3 m **mantle** (si resta in cima). Poi
`StepVault` **scrive** la posizione lungo la traiettoria warpata. `VaultDuration` (0.9) e
`MantleDuration` (1.4) devono combaciare con le clip, come `HardLandingLockSeconds` (2.03) con
`land_hard`. Durante la manovra: corpo raddrizzato sulla parete (non segue lo sguardo), niente tiro
(`HandsFree`, host-side), arma nascosta ma **non** rinfoderata.

## 8. Verifica

```
Godot --path . --headless --script tools/verify_godot_import.gd       # struttura
Godot --path . --headless --script tools/verify_animation_runtime.gd  # comportamento: "0 controlli falliti"
```

Da lanciare dopo ogni modifica ad albero, clip, rig o `CharacterMotor`. Se Godot mono crasha al
caricamento di hostfxr, un SDK vecchio in `~/.dotnet` sta avendo precedenza nel PATH.

## 9. Lavoro aperto

- **Morte**: non iniziata. Contratto deciso: `BroadcastDeath(Vector3)` con la sola direzione,
  `PhysicalBoneSimulator3D` inizializzato sulla posa corrente, nessun bone replicato.
- Clip di turn-in-place (oggi passo sintetico); fase di camminata e corsa non allineata nel crossfade.
- `CancelParkour()` esiste ma nessuno lo chiama.
- `W_Rifle.glb` ha l'astina a 0.391 m ma `SupportGripOffset.Z` oggi vale 0.254: va rigenerato
  (`SUPPORT_Z` in `tools/blender/build_weapon_assets.py`).
