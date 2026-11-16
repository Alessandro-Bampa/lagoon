---
name: character-animation
description: Animazione del personaggio in prima e terza persona — viewmodel (braccia + arma), corpo UAL, alberi di blend costruiti da codice, IK delle mani sull'arma, pose di mira. Carica quando tocchi animation/, CharacterRig, CharacterAnimator, ViewmodelAnimator, WeaponHoldRig, ArmIkModifier, BodyAimModifier, FirstPersonBodyView, WeaponAnimationSet, HumanoidBones, i bridge di animazione, quando aggiungi o rinomini una clip, aggiungi un'arma, il personaggio va in T-pose, le animazioni si fermano, un SkeletonModifier3D "non applica", le tacche di mira non cadono al centro, o lavori su mira (ADS), rinculo visivo, corpo visto in prima persona, reazioni ai colpi, morte.
---

# Animazione del personaggio

`animation/` + i due bridge (`PlayerAnimationBridge`, `NpcAnimationBridge`) + `tools/playtest_fps.gd`.
Gli asset vengono da `blender-pipeline`, il movimento da `CharacterMotor`, il tiro da `combat-shooting`.

## 1. Due rig, due animatori

| | Corpo (terza persona) | Viewmodel (prima persona) |
|---|---|---|
| Animatore | `CharacterAnimator` su `CharacterRig.tscn` | `ViewmodelAnimator`, figlio di `PlayerCamera` |
| Modello | `Mannequin.glb` (Universal Animation Library, 43 clip) | `VM_<arma>.glb` (Retro Weapon Pack: braccia + arma, clip autoriali) |
| Chi lo vede | gli altri giocatori; il locale ne vede **gambe e ombra** | solo il giocatore locale |
| Esiste | su ogni peer, anche per gli NPC | solo sul peer proprietario (sui remoti `FirstPersonCamera` lo libera) |
| Arma | `WeaponHoldRig` + IK delle mani | parte del modello, osso `Gun` |

Entrambi sono **ricevitori puri**: non conoscono `PlayerController` né il `Multiplayer`. I bridge leggono
**solo stato già replicato** e scrivono nelle loro proprietà; girano identici su ogni peer. Non
aggiungere proprietà replicate per l'animazione.

Gli alberi di blend si costruiscono **da codice** (`BuildTree` nei due animatori): non esistono `.tres`
generati né tool da rilanciare. Nomi di clip e di nodo stanno nello stesso file che li usa.

## 2. Trappole mute

Nessuna dà errori a runtime. Escludile prima di ipotizzare altro.

1. **Un `AnimationNodeTransition` non produce nulla finché non riceve la prima richiesta.** Se è
   l'ultimo nodo (`Life`) l'intero albero resta muto → T-pose. Le richieste iniziali si impostano
   subito dopo `Active = true`.
2. **Godot ripristina le pose dopo la passata dei modificatori.** `GetBoneGlobalPose` dal codice di
   gioco legge la posa **prima** dell'IK; il risultato si legge solo dentro
   `_ProcessModificationWithDelta` o in `Skeleton3D.SkeletonUpdated`. Scrivere pose da fuori un
   modificatore non ha effetto.
3. **Creare un modificatore dentro `_Ready` pianta il processo**: sempre `CallDeferred` (`Build`).
4. **L'ordine dei modificatori è l'ordine dei figli dello scheletro**: `BodyAim` poi `ArmIk`. Le mani
   sono un vincolo e vanno risolte dopo tutto ciò che muove le spalle.
5. **I nomi d'osso sono quelli del profilo umanoide di Godot**, non quelli del `.glb`: li rinomina la
   `BoneMap` nell'import (skill `blender-pipeline` §2). Le tracce sono `%GeneralSkeleton:<Osso>` e ogni
   filtro deve usare quel percorso esatto. Tutti i nomi passano da `HumanoidBones`.
6. **L'import toglie le tracce "immutabili"**: una clip può non avere la traccia di un osso (es.
   `Spine`). Mai dare per scontato che una traccia esista.
7. **Loop**: nelle clip UAL lo imposta l'import dal suffisso `_Loop`, che viene **tolto dal nome**
   (`Idle_Loop` → `Idle`, `Jump_Loop` → `Jump`). Nei viewmodel lo imposta `ViewmodelAnimator`
   (`LoopingClips`): clip ciclica nuova = aggiungerla lì.
8. **Ogni clip del viewmodel contiene TUTTI gli ossi** (l'export campiona lo scheletro intero): la
   clip `empty`, che è una posa della sola arma, porta anche le braccia a riposo. Un filtro "sulle
   tracce della clip" le trascinerebbe: si filtra sui discendenti dell'osso `Gun`.
9. **Un vertice collassato va ri-pesato sull'osso su cui collassa** (`FirstPersonBodyView`): portato
   sul bacino ma ancora pesato sulla mano, viene trascinato via a ogni movimento del braccio (spuntoni).
10. **La correzione di mira è una ROTAZIONE attorno alla camera, non una traslazione**
    (`AimPitchDegrees`): ruotare sposta tutta l'immagine dell'arma della stessa quantità; alzare
    l'arma muove la tacca vicina molto più del mirino lontano e rompe l'allineamento.
11. **Il viewmodel sta a pochi centimetri dalla camera**: senza `ExtraCullMargin` il culling lo scarta
    quando l'AABB di riposo esce dal frustum durante una clip.
12. **Le velocità degli animatori devono combaciare con quelle del controller** (sono i punti degli
    spazi di blend): le riallineano i bridge in `_Ready`, e `CharacterAnimator.Build` è differito
    apposta per venire dopo.

## 3. Corpo in terza persona

```
Move (Idle/Walk/Jog_Fwd/Sprint, per velocità) ─ MoveScale ─┐
Crouch (Crouch_Idle/Crouch_Fwd) ─ CrouchScale ─────────────┴ Stance ─┐
Jump (posa di volo) ─────────────────────────────────────────────────┴ Air ─ Land ─ Hit ─┐
Death01 ─────────────────────────────────────────────────────────────────────────────────┴ Life
```

- **La libreria ha solo clip in AVANTI.** Di lato `BodyAimModifier` gira il bacino verso il moto e
  contro-ruota la spina (`MaxHipSwivelDegrees`); all'indietro la clip gira al contrario (`*Scale = -1`)
  col bacino dalla parte opposta. L'isteresi 80°/110° evita che il verso si inverta a ogni frame.
- `Hit` è filtrato su busto e braccia: le gambe continuano a camminare.
- `TriggerLand` fa partire `Jump_Land` solo sugli atterraggi **duri** (movimento bloccato per
  `CharacterMotor.HardLandingLockSeconds`); sotto, le gambe devono ripartire subito.
- Scavalcamento e arrampicata non hanno clip: `Vaulting` mostra la posa di volo e ripone l'arma.
- `Dead` porta a `Death01`, spegne i modificatori e toglie l'arma.

### L'arma guida le mani

UAL (Standard) non ha clip da fucile. `WeaponHoldRig` colloca `W_<arma>.glb` nello **spazio dello
sguardo** e `ArmIkModifier` ci porta le mani: canna allineata e due mani sull'arma valgono per
costruzione, con qualunque locomozione sotto.

- La posa non è inventata: i marcatori `EyeHip`/`EyeAim` dicono dove sta la camera rispetto all'arma
  nelle pose del viewmodel. Farli coincidere con gli occhi nominali del corpo (`EyeHeight` /
  `CrouchEyeHeight`) mostra agli altri l'arma dove la vede chi la impugna. `Grip` e `Support` sono i
  polsi, `Muzzle` la bocca.
- L'orientamento della mano si ricava dalla **geometria della rest pose** (dita, asse delle nocche),
  non dagli assi locali dell'osso: regge un corpo diverso sullo stesso profilo. Il terzo asse è il
  **dorso** per la destra e il **palmo** per la sinistra, e il verso di chiusura delle dita è opposto.
- Le dita le chiude `ArmIkModifier` (`GripCurl`, `TriggerCurl`): la posa da pistola di UAL ha la
  sinistra aperta.
- `IsTwoHanded` decide come sta la sinistra: sotto l'astina (armi lunghe) o attorno alla destra.
- In corsa l'arma si abbassa ruotando attorno all'impugnatura (`Lowered*`); i gomiti piegano nel
  riferimento dello **sguardo**, non dell'arma.
- Sull'avatar locale (`FirstPersonOwner`) l'arma in terza persona è `ShadowsOnly`.

## 4. Viewmodel in prima persona

```
Hip (idle/walk/run) ──────────────┐
Crouch (idle_crouch/walk_crouch) ─┴ Stance ─┐
Aim (idle_aim/walk_aim) ────────────────────┴ Sight ─ Fire ─ Reload ─ Draw ─ Hide [─ Empty]
```

- Clip con **nomi uniformi** per ogni arma (li fissa `build_weapon_viewmodels.py`): per questo
  cambiare arma è sostituire l'istanza, senza toccare l'albero. `Hide` → scambio → `Draw`.
- **La posa di mira è quella del pack**, costruita attorno all'origine della camera: le tacche cadono
  al centro dello schermo, cioè dove l'host fa passare il colpo. Lo scarto residuo lo chiude
  `AimPitchDegrees`, che **si misura**: screenshot `fp_*_ads` di `tools/playtest_fps.gd`, scarto in
  pixel fra il puntino verde e la punta del mirino, poi `atan(px / (h/2) · tan(AimFov/2))`. Oggi:
  fucile 0,93°, pistola 0,64°. Cambiando `AimFovDegrees` il valore resta valido (è un angolo).
- `AimWeight` avanza a tempo costante (`AimSeconds`), non smorzato: è un tempo di gioco.
- L'osso `camera` del modello porta lo **scossone autoriale** di ricarica ed estrazione
  (`CameraShake`): `FirstPersonCamera` lo applica alla visuale, il viewmodel lo toglie a sé stesso,
  così braccia e arma restano ferme rispetto alla visuale come nel pack.
- Sopra le clip: sway sullo sguardo, rinculo visivo a molla (`Kick*` del `WeaponAnimationSet`),
  abbassamento d'atterraggio, arma fuori vista durante il parkour (`Lowered`).
- Niente ombre: quella dell'arma la fa il corpo, dalla posizione giusta.

## 5. Camera e proprio corpo

- `FirstPersonCamera` sta sul punto di vista **nominale** del motore, non sulla testa animata: è
  esattamente l'origine dei colpi lato host (`ResolvedEyePosition`). Il dondolio lo danno le clip
  del viewmodel. Se cambi corpo, ricontrolla `EyeHeight`/`CrouchEyeHeight`.
- `FirstPersonBodyView` sdoppia la mesh: l'originale diventa `ShadowsOnly` (ombra intera), una copia
  con tutto da `Spine` in su collassato è l'unica visibile → guardando in basso si vedono le gambe.
  **Non nascondere parti agendo sullo scheletro**: sparirebbero anche dall'ombra. Chi cambia
  materiale o visibilità della mesh del corpo a runtime deve farlo su entrambe.

## 6. Stato in ingresso

| Proprietà replicata | Significato |
|---|---|
| `SyncLocalVelocity` | **X = destra**, Y = avanti. Il Visual guarda +Z e la sua sinistra è +X: `WorldToLocalVelocity` nega la X. |
| `SyncAimYaw` / `SyncAimPitch` | lo SGUARDO, sempre pubblicato. Per il corpo è la terna in cui sta l'arma. |
| `SyncAiming` | **arma all'occhio** (tasto `aim` con un'arma in mano). Decide la posa sugli altri peer e, lato host, il cono di tiro. |
| `SyncFacing` | imbardata del corpo: insegue lo sguardo smorzata; lo scarto lo chiude `BodyAimModifier`. |
| `SyncVaulting`, `HeldItemId`, `Reloading`, `IsDead` | parkour, arma, ricarica, morte. |

"Arma abbassata in corsa" **non** è replicato: lo deduce `CharacterAnimator` dalla velocità.
Eventi one-shot (`Jumped`, `Landed`, `ShotResolved`, `HitReaction`): RPC riemesse come segnale locale,
con payload solo geometrico.

## 7. Aggiungere un'arma

1. Riga in `WEAPONS` di `tools/blender/build_weapon_viewmodels.py` → `VM_*.glb` e `W_*.glb`.
2. `.tres` di `WeaponAnimationSet` in `animation/resources/` (i due modelli, `IsTwoHanded`, tempi).
3. `WeaponDefinition.AnimationSet`; `ReloadSeconds`/`DrawSeconds` uguali alle clip `reload`/`draw`.
4. Misurare `AimPitchDegrees` (§4).

Non si toccano alberi né codice. Un'arma con ricarica a colpi (fucile a pompa: `ReloadStart`/`Step`/
`End` nel pack) richiede invece logica nuova, in `combat/` prima che qui.

## 8. Verifica

```
Godot --path . --resolution 1280x720 --script tools/playtest_fps.gd -- <cartella> [fp|tp|all]
```

Partita locale, armi, mira, raffica, ricarica, più una fila di manichini pilotati a mano; uno
screenshot per passo. Con `--headless` gira senza immagini e controlla errori e numeri (colpi scalati,
ricarica completata). Poi sempre due istanze ENet: l'avatar dell'altro deve avere l'arma in mano e
alzarla quando mira.

## 9. Lavoro aperto

- Passo sul posto mentre il corpo ruota da fermo (oggi i piedi scivolano); piedi su pendenze.
- Clip di scavalcamento/arrampicata e di ricarica in terza persona (oggi l'arma si abbassa e basta).
- Il viewmodel compenetra i muri da molto vicino (nessuna camera/viewport dedicata).
- Le clip `aim_start`/`aim_end`/`interact`/`pose_*` sono nei `.glb` ma l'albero non le usa: l'entrata
  in mira è un blend, che resta interrompibile.
- Morte: la clip c'è, le conseguenze di gioco no (un giocatore morto si muove ancora).
