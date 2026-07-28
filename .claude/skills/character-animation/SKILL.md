---
name: character-animation
description: Sistema di animazione del personaggio — AnimationTree a stance, BlendSpace, one-shot, layer procedurali (mira sul rachide, piedi a terra, presa dell'arma, reazione ai muri). Carica questa skill quando tocchi animation/, il CharacterRig, il BlendTree, CharacterAnimator, PlayerAnimationBridge, SpineAimModifier, AimRig, FootIkRig, WeaponGripRig, WeaponSpaceProbe, WeaponAnimationSet, quando aggiungi o rinomini una clip, quando il personaggio va in T-pose o le animazioni si fermano, quando un SkeletonModifier3D "non applica", quando agganci un'arma alla mano, o quando lavori su rinculo, mira verticale, piedi a terra, scavalcamento o ragdoll.
---

# Animazione del personaggio

Ambito: `animation/`, piu' `player/scripts/PlayerAnimationBridge.cs`, `ai/scripts/NpcAnimationBridge.cs`
e i due tool di verifica. Le clip e il rig vengono da `blender-pipeline`; le armi da `combat-shooting`;
il movimento che alimenta tutto da `core/Motion/CharacterMotor.cs`.

## 1. Le trappole mute

Sono i modi in cui questo sistema si rompe **senza un solo errore a runtime**. Tutti sono gia'
costati almeno una sessione: prima di ipotizzare qualsiasi altra causa, escludi questi.

### 1.1 Un SkeletonModifier3D "non applica" — quasi sempre e' la MISURA

**Godot 4.7 ripristina le pose sorgente al termine della passata dei modificatori.** Un
`SkeletonModifier3D` scrive, il risultato va allo skinning, e subito dopo lo scheletro rimette le
pose animate. Quindi:

- `get_bone_pose*()` e `get_bone_global_pose()` chiamati dal normale codice di gioco restituiscono
  la posa **prima** dell'IK. Sempre. Anche quando l'IK funziona benissimo.
- Il risultato e' leggibile **solo** dentro `_process_modification()` o nel segnale
  `Skeleton3D.skeleton_updated`, che scatta dentro la passata.

Costo storico: una fase intera con un IK dichiarato "rotto" — spostamento misurato 0,002 m verso un
bersaglio a 0,111 m — che invece girava. Il modificatore veniva eseguito (31 chiamate su 31 frame) e
calcolava giusto; era la sonda a guardare il buffer sbagliato.

Nei tool si usa `_watch_bone()` / `_bone_after_modifiers()` di `verify_animation_runtime.gd`, che si
agganciano a `skeleton_updated`. In gioco, chi ha bisogno del risultato post-IK deve fare lo stesso.
**`BoneAttachment3D` invece segue i modificatori** (verificato: la presa dell'arma si sposta di 0,26 m
fra mira alta e mira bassa), quindi per agganciare oggetti alle ossa non serve alcun accorgimento.

### 1.2 `TwoBoneIK3D` senza `pole_node` non risolve affatto

Non da' errori, non da' warning, e `influence` resta al valore che gli hai messo. Semplicemente la
catena non si muove: misurato, la mano si sposta di 3 mm e l'errore al bersaglio **non cala**.
Dichiarare la sola `pole_direction` **non basta**: serve un `Node3D` come polo.

Con il polo, l'errore residuo va a **0,000 m**. Vale per ogni catena: mano di supporto
(`WeaponGripRig.SupportElbowHint`) e gambe (`FootIkRig`, un polo davanti a ciascun ginocchio).

Corollario: le impostazioni vanno dichiarate **dopo** `AddChild`, perche' il modificatore risolve gli
indici di osso contro lo scheletro padre e prima di essere nell'albero non ne ha uno.

### 1.3 Costruire un modificatore dentro `_Ready` blocca il processo

Creare un `TwoBoneIK3D` mentre lo scheletro sta ancora entrando nell'albero **pianta il processo**:
nessun errore, nessun crash, si ferma e basta (riprodotto in headless). Tutti i rig procedurali
costruiscono i propri modificatori con `CallDeferred`. Un frame di ritardo non ha alcun costo
visibile: finche' il modificatore non c'e', il corpo resta alla posa animata.

### 1.4 BlendSpace2D senza triangoli → T-pose

Un `AnimationNodeBlendSpace2D` interpolato funziona per **triangolazione**: dove non esiste un
triangolo il nodo non produce nulla e lo scheletro ricade sulla rest pose, che per `Body_Base` e' la
**T-pose**. Non e' un errore e `blend_position` resta scrivibile: un controllo che verifica solo
l'esistenza dei parametri **passa**.

Cause viste davvero: due soli punti di blend (collineari per definizione); un punto collineare con
altri due; **posizione richiesta fuori dall'inviluppo** — con i quattro punti direzionali a distanza
`WalkSpeed` la regione coperta e' il **rombo** `|x| + |y| <= WalkSpeed`, non il quadrato. Per questo
`CharacterAnimator.ClampToDiamond` proietta sulla palla L1 **prima** di scrivere.

**I triangoli espliciti non si possono usare.** Con `auto_triangles = false`, `ResourceSaver`
serializza `triangles` prima di `blend_point_N/pos`: al caricamento ogni indice viene rifiutato e lo
spazio arriva a zero triangoli. Si usa `auto_triangles = true` su un rombo ben condizionato.

### 1.5 `loop_mode = LOOP_NONE` → animazioni ferme

L'importatore glTF mette `LOOP_NONE` su tutto cio' che non ha un suffisso `-loop`, e le clip Mixamo
non ce l'hanno. La clip parte, arriva in fondo e **congela sull'ultimo fotogramma**.

Il loop si imposta **solo** in `assets/models/animations/CharacterAnimations.glb.import`, sotto
`_subresources/animations/<clip>/settings/loop_mode` (`1` = lineare). **Aggiungendo una clip, aggiungi
la sua riga li'** e la sua categoria in `_verify_loop_modes`, altrimenti la verifica fallisce apposta.

**Non chiamare una clip `*_loop`**: con `nodes/use_name_suffixes = true` il suffisso viene
interpretato come comando e **strippato dal nome**, e ogni riferimento punta a un nome che non esiste.

### 1.6 `AnimationNodeSync.sync = false` su un nodo filtrato → gambe congelate

`sync = false` ferma i frame dell'ingresso con peso 0. Su un nodo **filtrato** e' un bug: l'ingresso
a peso 0 resta **visibile** sulle parti che il filtro non copre. Regola: **ogni `Blend2`/`OneShot`
con `filter_enabled = true` deve avere `sync = true`.** E' verificato automaticamente.

### 1.7 Eulero YXZ riavvolge oltre i 90 gradi

`Node3D.rotation` e `Basis.GetEuler()` usano l'ordine YXZ, in cui la componente X e' la rotazione di
mezzo e vive in `[-90°, +90°]`. La presa del fucile e' gia' a **-76°**: sommarci i 55° del "port arms"
supera il limite e la decomposizione restituisce un triplo equivalente ma diverso. La `Basis` e'
giusta, l'angolo letto no. **Misura la direzione (`basis.z`), non l'angolo.**

Costo storico: il rinculo ruotava il muso **in basso** invece che in alto, e nessuno se n'era accorto
perche' il controllo misurava di quanto si sposta la presa, non in che direzione punta la canna.

### 1.8 Il frame in headless dura ~7 ms, non 16,7

Riguarda le sonde. Senza vsync, contare i frame per aspettare la fine di una clip da' un'attesa lunga
meno della meta' del previsto. Dove conta la durata REALE si usa `_settle_seconds()`.

### 1.9 `intersect_ray` non vede le forme da dentro

`hit_from_inside` e' false di default: un raggio che parte dentro un collider non riporta nulla. In
una sonda che parte dal petto o dalla presa e' facilissimo finirci dentro, e il sintomo — nessun
colpo — si scambia per "la sonda non funziona".

## 2. Struttura del BlendTree

`animation/resources/CharacterBlendTree.tres` e' **generato**, non scritto a mano:

```
Godot --path . --headless --script tools/build_animation_tree.gd
```

Modificalo **solo** in `tools/build_animation_tree.gd`. Editare il `.tres` si perde alla
rigenerazione, e i path delle track scritti a mano si sbagliano in silenzio.

```
WalkSpace     ──┐                          (disarmato, rombo WalkSpeed)
                ├─ MoveBlend ──────┐
RunSpace      ──┘                  │
                                   ├─ StanceBlend ──┐   (Blend2 sync, FULL BODY)
RifleWalkSpace ─┐                  │                │
                ├─ ArmedMoveBlend ─┘                │
RifleRunSpace ──┘                                   ├─ CrouchBlend ──┐
CrouchSpace ────────────────────────────────────────┘                │
                                                                     ├─ AirBlend ──┐
FallClip (fall_idle) ────────────────────────────────────────────────┘             │
                       AirBlend ──┐                                                │
RifleLowered ─┐                   ├─ WeaponBlend (FILTRATO upper-body, sync) ──────┘
RifleAim     ─┤                   │
PistolIdle   ─┼─ WeaponPose ──────┘
PistolAim    ─┘  (Transition a 4)
RifleFireClip ─┐
               ├─ FirePose (Transition) ─┐
PistolFireClip ┘                         │
                    ... ─ Fire (OneShot FILTRATO) ─ Jump (OneShot) ─ Land (OneShot) ─ output
LandHardClip ─┐                                                        │
              ├─ LandPose (Transition) ────────────────────────────────┘
LandSoftClip ─┘
```

**La mira e' uno STATO (`Aiming`, da RMB), non una conseguenza dell'essere armati.** E' il cambiamento
piu' importante rispetto alla versione precedente, dove il corpo armato inseguiva sempre il cursore:
- **`StanceBlend` = mira a due mani.** Il set di locomozione armato full-body si accende SOLO in mira
  (`twoHanded && Aiming`), perche' e' in mira che il corpo punta al bersaglio e si strafa davvero.
- **`WeaponBlend` = armato, sempre.** L'overlay upper-body e' acceso con qualunque arma in mano; la
  POSA la sceglie il `Transition` `WeaponPose` a 4 ingressi: `rifle_lowered` / `rifle_aim` /
  `pistol` / `pistol_aim`, da arma + stato di mira.
- **`FirePose` e `LandPose`** sono `Transition` a xfade 0 davanti ai one-shot: la clip di sparo la
  dichiara l'ARMA (`WeaponAnimationSet.FirePose` — prima era cablata `rifle_fire` e la pistola
  sparava con l'animazione del fucile), la clip d'atterraggio la sceglie `TriggerLand` dai regimi.
  La richiesta va impostata PRIMA di far partire il one-shot.

Il motivo storico del set armato completo resta valido: la posa "reggi fucile" e' authored su un
bacino neutro, ma le clip di strafe il bacino lo ruotano di decine di gradi (misurato:
`rifle_walk_left` **49°**, `pistol_idle` **54°**). L'overlay di mira sopra il set armato in movimento
funziona perche' `SpineAimModifier` rimisura e chiude l'errore residuo sulla mira vera, ogni frame.

Chi usa cosa:
- **fucile IN MIRA, in piedi** → `StanceBlend = 1` (set armato full body) + overlay `rifle_aim`;
- **fucile SENZA mira** → set disarmato + overlay `rifle_lowered` (porto basso). Come per la
  pistola: un'arma portata rilassata non cambia come si cammina;
- **pistola** → set disarmato + overlay (`pistol` o `pistol_aim`);
- **accovacciato, con qualunque arma** → clip di crouch disarmate + overlay. Il "crouch armato" e'
  RISOLTO cosi', via overlay + SpineAim: un set di clip dedicato e' stato valutato e scartato
  (cicli di passo fragili per un guadagno marginale in isometrica).

Il resto delle scelte:
- **camminata e corsa sono due spazi**, non uno: la corsa messa come punto in piu' sull'asse Y
  sarebbe collineare con `idle` e `walk_fwd` (§1.4).
- **le diagonali non hanno clip proprie e non devono averne**: cadono dentro i triangoli e vengono
  sintetizzate. Mixamo non ha diagonali affidabili e il blend delle cardinali e' lo standard.
- **`WeaponPose` e' un `Transition`**: fra "reggi fucile" e "reggi pistola" non c'e' una via di mezzo.
- **`WeaponBlend` e' un `Blend2` filtrato, non un `Add2`**: senza clip-delta un additivo sommerebbe
  due pose assolute.
- **la maschera upper-body include le clavicole**: senza, la spalla resta alla posa di corsa.
- **`AirBlend` sta prima del layer arma**, cosi' cadendo si continua a impugnare.
- **`JumpScale`**: `jump` e' un arco di 1,03 s ma il volo vero dura `2·v/g` (~0,6 s). Senza
  riscalare, si atterra a clip ancora in aria.

## 3. Parametri esposti

`CharacterAnimator.cs` e' l'**unico** punto di accoppiamento fra C# e struttura dell'albero: se
rinomini un nodo, i `const` in cima a quel file vanno aggiornati (e cosi' i due tool).

| Parametro | Significato |
|---|---|
| `WalkSpace` / `RunSpace` / `RifleWalkSpace` / `RifleRunSpace` / `CrouchSpace` `blend_position` | la **stessa** velocita' locale (X = destra, Y = avanti), proiettata sul rombo di ciascuno; da fermi in mira ci si somma il **passo sintetico del turn-in-place** (`TurnRate · TurnStepScale` su X) |
| `MoveBlend` / `ArmedMoveBlend` `blend_amount` | peso della corsa: `clamp01((‖v‖−Walk)/(Run−Walk))` |
| `StanceBlend/blend_amount` | mira a due mani (`twoHanded && Aiming`), spenta accovacciati |
| `CrouchBlend` · `AirBlend` `blend_amount` | accovacciato, in aria — smorzati |
| `WeaponBlend/blend_amount` | overlay upper-body: acceso con qualunque arma in mano |
| `WeaponPose/transition_request` | `"rifle_lowered"` / `"rifle_aim"` / `"pistol"` / `"pistol_aim"` |
| `FirePose/transition_request` | `"rifle_fire"` / `"pistol_fire"` — da `WeaponAnimationSet.FirePose`, al cambio d'arma |
| `LandPose/transition_request` | `"land_hard"` / `"land_soft"` — impostata da `TriggerLand` prima del one-shot |
| `Fire/request` · `Jump/request` · `Land/request` | one-shot |
| `JumpScale/scale` | `durata clip / JumpFlightTime` |

Stato in ingresso rilevante oltre alla velocita': `Aiming` (stance di mira, da `SyncAiming`) e
`TurnRate` (rad/s, derivata smorzata di `SyncFacing` calcolata dai bridge — positiva = sinistra).
`TriggerLand` ha TRE regimi alternativi: `>= HardLandingSpeed` → `land_hard`; `>= SoftLandingSpeed`
(default 6.5, sopra l'impatto di un salto normale) → `land_soft`; sotto → solo dip procedurale.

Lo smorzamento e' **esponenziale** (`1 − exp(−k·dt)`), non `clamp(k·dt)`: `CharacterAnimator` gira in
`_Process` (render) mentre il movimento gira in `_PhysicsProcess` (tick fisso), e con la forma ingenua
la locomozione risulterebbe piu' o meno reattiva a seconda del frame rate.

## 4. I layer procedurali

Sono la ragione per cui non serve una clip per ogni stato. Vivono tutti sotto `CharacterRig.tscn`,
costruiscono i propri nodi **da codice** (il rig arriva da un `.glb` rigenerabile) e sono **pura
resa**: girano su ogni peer da stato gia' replicato, non producono stato di gioco, non richiedono
autorita' e non vanno replicati.

| Nodo | Cosa fa | Perche' non e' una clip |
|---|---|---|
| `AimRig` + `SpineAimModifier` | misura dove punta il busto nella posa di **questo** frame e lo ruota sulla mira, spalmando l'errore su `Spine`/`Spine1`/`Spine2` | l'errore dipende da quale clip sta girando e con che peso: nessuna posa registrata puo' conoscerlo |
| `FootIkRig` | ogni piede cerca il suolo con un raggio, l'IK ce lo porta, il bacino scende verso il piede piu' basso | dipende dalla geometria sotto i piedi in quell'istante |
| `WeaponGripRig` | aggancio dell'arma alla mano, IK della mano di supporto, rinculo, "port arms" | idem |
| `WeaponSpaceProbe` | misura lo spazio davanti alla canna | idem |
| lean (dentro `AimRig`) | inclina il busto contro l'accelerazione laterale | dipende da come si sta guidando il personaggio |

Note che costano se ignorate:

- **`SpineAimModifier` rimisura l'errore prima di ogni vertebra.** Calcolarlo una volta e spalmarlo
  non converge: ruotando un osso ruota tutto il suo sottoalbero, quindi dopo il primo passo il
  residuo non e' piu' quello di partenza. Misurato: a colpo unico restavano **20°** di scarto; a
  correzione iterativa restano **2°**.
- **Il bacino ha UN SOLO scrittore.** Ammortizzazione d'atterraggio e abbassamento dei piedi vogliono
  entrambi abbassarlo: li somma `CharacterAnimator.UpdatePelvisOffset`. Due nodi che scrivono la
  stessa `Position` si cancellano a vicenda senza dare errori, e il sintomo e' "l'IK dei piedi ogni
  tanto non funziona".
- **`FootIkRig` corregge la retroazione del bacino.** Le pose lette sono quelle del rig gia'
  abbassato: senza risommare `PelvisDrop`, il sistema si assesta a meta' strada (misurato: il piede
  restava stabilmente 4 cm sopra il terreno).
- **L'IK dei piedi e' attivo SOLO da (quasi) fermi** (`DisableAboveSpeed = 0.6`): senza curve di
  contatto nelle clip non si distingue il piede che appoggia da quello che vola. Il valore
  precedente (5) lo lasciava acceso in camminata, e su una rampa i raycast inchiodavano i piedi a
  quote diverse a ogni passo: il personaggio "scattava" in salita.
- **La mano di supporto insegue l'astina solo in mira** (`WeaponGripRig.SupportActive`, scritto da
  `CharacterAnimator` con `Aiming`): nel porto rilassato l'arma e' inclinata verso terra, il
  bersaglio IK ruota con lei e il polo del gomito (misurato sulla posa di mira) puo' flippare,
  portando il gomito sopra la canna.
- **Il "port arms" abbassa anche la mira**: un'arma alzata contro un muro non sta piu' puntando il
  bersaglio, e inseguirlo col busto lo torcerebbe verso un punto che l'arma non guarda.

## 5. Chi parla con chi

```
CharacterMotor (core/Motion)
   ├─ PlayerController ──> PlayerAnimationBridge ──┐
   └─ NpcController    ──> NpcAnimationBridge   ───┴──> CharacterAnimator ──> AnimationTree
                                                              └───────────> AimRig / FootIkRig / GripRig
```

**`CharacterAnimator` e' un ricevitore puro** e va tenuto tale: non conosce `PlayerController`, non
interroga il `Multiplayer`, non valida niente. E' cosi' che lo stesso rig serve giocatori e NPC senza
che `animation/` dipenda da `player/`. L'esistenza di `NpcAnimationBridge` e' il collaudo di questo
invariante — se qualcosa in `animation/` prendesse una dipendenza da `player/`, e' li' che si
romperebbe.

**I bridge leggono SOLO stato gia' replicato** e girano identici sul peer proprietario e su quelli
remoti. Non aggiungere proprieta' al `SceneReplicationConfig` per l'animazione: quasi sempre si
derivano da quelle esistenti.

**TRE eccezioni dichiarate: `SyncAimPitch`, `SyncAimYaw`, `SyncAiming`.** Il punto di mira lo
calcola solo il peer proprietario (`WeaponInput._Process` e' disattivato sugli altri), quindi
niente di mira e' derivabile da altro: senza pitch gli avatar remoti punterebbero all'orizzonte,
senza yaw il busto non potrebbe divergere dal corpo (e' `SyncFacing` a NON essere piu' la mira),
senza il flag i remoti non saprebbero se mostrare porto o mira. Stanno tutte in `CharacterMotor`
perche' serviranno agli NPC armati. I bridge ricostruiscono la direzione con
`CharacterAnimator.AimVector(SyncAimYaw, SyncAimPitch)`, mai da `WeaponInput.AimPoint`. Fuori mira
i controller tengono `SyncAimYaw = SyncFacing`, cosi' il busto non punta mai a un residuo stantio.

**Il facing in mira lo decide `CharacterMotor.PlanAimFacing`** (zona morta 55° con isteresi a 8° da
fermi, inseguimento continuo in movimento): mirare "dietro" non e' un caso speciale, lo scarto
supera la soglia e il corpo recupera per la via piu' corta (turn-in-place). Le gambe del
turn-in-place non hanno una clip: e' il passo sintetico di `UpdateLocomotion` (vedi §3).

**Il contratto degli assi e' "X = destra" e va difeso.** Il Visual guarda +Z e la sua sinistra e'
+X: `CharacterMotor.WorldToLocalVelocity` NEGA la X locale, e senza quella negazione lo strafe
risulta specchiato in tutti e cinque i blend space e il lean si inclina dal lato sbagliato — e'
successo, senza un solo errore, perche' nessuna sonda misurava la DIREZIONE. Ora la misurano:
`_verify_strafe_direction` (albero) e i test di `WorldToLocalVelocity` (motore) nella suite runtime.

Gli eventi one-shot (`Jumped`, `Landed`, `ShotResolved`) arrivano da RPC che ogni peer riemette come
segnale **locale**. Nel payload non viaggia mai un esito di gioco (CLAUDE.md §3): `Landed` porta la
velocita' d'impatto, che e' una grandezza fisica e decide solo quanto flette il bacino.

## 6. Verifiche automatiche

```
Godot --path . --headless --script tools/verify_godot_import.gd       # struttura
Godot --path . --headless --script tools/verify_animation_runtime.gd  # comportamento
```

`verify_godot_import.gd`: scheletro, skin, scala, track, **`loop_mode` per categoria**, esistenza dei
parametri, **copertura a triangoli di ogni BlendSpace2D**, **`sync` su ogni nodo filtrato**.

`verify_animation_runtime.gd` fa girare l'albero e misura le ossa: le combinazioni che non devono
essere la T-pose (disarmato, corsa, crouch, aria, pistola, e i quattro assi del set armato), dieci
secondi di camminata che non deve congelarsi, le gambe che non si fermano ne' con la pistola ne' col
fucile, la stance armata che deve produrre una posa **diversa** da quella disarmata, i one-shot che
rientrano, la presa che segue la mano, il rinculo che **alza** il muso, la mano di supporto che
raggiunge l'astina, l'asse dell'arma che passa fra le due mani, il busto che punta sulla mira anche
in strafe, i piedi che riproducono un dislivello di 12 cm, l'arma che si alza contro un muro, e
l'NPC che si anima sullo stesso rig.

Nota d'ambiente: su questa macchina `~/.dotnet` contiene un SDK 2.2 rotto che ha la precedenza nel
PATH e fa **crashare** Godot mono al caricamento di hostfxr. Lanciare con
`DOTNET_ROOT=/usr/lib/dotnet PATH=/usr/bin:$PATH`. Gli errori GodotSteam in headless sono attesi.

## 7. Aggiungere un'arma senza toccare la locomozione

1. Crea (o riusa) un `WeaponAnimationSet` in `animation/resources/`. Ne bastano due —
   `two_handed.tres` e `one_handed.tres` — perche' ogni fucile impugna come un fucile.
   `FirePose` deve essere il nome di un INGRESSO del Transition `FirePose` dell'albero
   (`rifle_fire` / `pistol_fire`).
2. Referenzialo da `WeaponDefinition.AnimationSet` nel `.tres` dell'arma; se l'arma ha un modello,
   metti il suo `.glb` (frame della presa, vedi skill `blender-pipeline`) in
   `WeaponDefinition.VisualScene` — senza, compare il placeholder.
3. Fine. **Non toccare l'albero, non aggiungere clip, non toccare `CharacterAnimator`.**

**`GripRotationDegrees` e `SupportGripOffset` si MISURANO dalla posa, non si indovinano.** I valori
attuali vengono da una sonda che, nella posa di riferimento, allinea l'asse della presa alla
congiungente fra le due mani e misura la loro distanza:

| | `GripRotationDegrees` | `SupportGripOffset` |
|---|---|---|
| `two_handed.tres` (da `rifle_idle`) | `(-76.2, 37.1, 0)` | `(0, 0, 0.391)` |
| `one_handed.tres` (da `pistol_idle`) | `(-61.8, 17.3, 0)` | `(0, 0, 0)` |

Allineare l'arma al **corpo** invece che alle mani sarebbe sbagliato: le pose armate ruotano il
bacino (§2) e l'arma finirebbe di traverso. Il placeholder in `WeaponVisual` e' costruito **attorno
alla presa** — calcio dietro, canna avanti, astina esattamente su `SupportGripOffset` — proprio
perche' un disallineamento si veda a colpo d'occhio.

## 8. Lacune volute e lavoro aperto

Sono scelte, non dimenticanze. Non "sistemarle" senza leggere il motivo.

**Budget clip: 33.** 28 Mixamo + 5 **procedurali** (`rifle_aim_idle`, `rifle_lowered_idle`,
`pistol_aim_idle`, `pistol_fire`, `land_soft`), generate da
`tools/blender/build_procedural_clips.py` (skill `blender-pipeline`): Mixamo non e' piu' una
sorgente disponibile, le clip nuove si costruiscono campionando pose da clip esistenti.
**Mancano ancora**: scavalcamento, 4 reazioni direzionali ai colpi, posa iniziale di morte,
clip di turn-in-place (oggi passo sintetico; da rivalutare se non legge bene a schermo). Il crouch
armato e l'atterraggio morbido NON mancano piu' (risolti via overlay e via `land_soft`). Mai una
clip per combinazione arma × movimento: se un'arma "richiede" una locomozione nuova, la risposta
e' quasi sempre una posa upper-body in piu'.

**Le FBX Mixamo non stanno nel repo.** Di conseguenza `build_animation_library.py` e' **additivo**:
le clip la cui FBX manca vengono **recuperate dalla libreria gia' esportata** reimportando il `.glb`.
Senza quel recupero, rigenerare con una cartella sorgente parziale cancellerebbe in silenzio tutto il
resto. Il log riporta `recovered` e `lost`: **`lost` non vuoto significa clip perse per sempre**.

Procedura per aggiungerne una: scaricare da Mixamo (*FBX Binary*, *Without Skin*, *In Place* dove
esiste) → riga in `CLIPS` di `tools/blender/build_animation_library.py` → rigenerare la libreria →
`loop_mode` nel `.import` (§1.5) → categoria in `_verify_loop_modes` → rigenerare l'albero → le due suite.

*Trappola dell'asse*: il controllo "in place" misura la traslazione **orizzontale** del bacino, che
nello spazio locale di `Hips` e' su **X e Z**, non su Y.

*Trappola del modulo*: Blender tiene i moduli in `sys.modules` fra un'esecuzione e l'altra. Senza
`importlib.reload(mx)`, una modifica a `mixamo_common.py` verrebbe ignorata senza il minimo segnale.

**Atterraggio a due regimi.** Oltre `HardLandingSpeed` parte `land_hard`; sotto, resta la sola
flessione procedurale del bacino. Sono **alternativi**, non sommati: la clip contiene gia' la propria
flessione. Manca ancora la clip di atterraggio morbido.

**Fase di camminata e corsa non allineate.** `walk_fwd` dura 1,067 s e `run_fwd` 0,667 s: nel
crossfade i piedi possono risultare fuori fase per un istante. `sync = true` li fa avanzare ma **non**
li mette in fase — servirebbe un ritaglio delle clip. Accettato.

**Ancora da fare**: motion warping per lo scavalcamento (Godot non ha un nodo nativo: `ShapeCast3D`
misura altezza e bordo, il codice deforma la traiettoria della radice, mani in IK sul bordo durante
la finestra di contatto — **una sola clip generica**, intervallo dichiarato 0,5–1,2 m).

**Fase E non iniziata.** Danno direzionale e morte. Il contratto e' gia' deciso:
`HealthComponent.ApplyDamage` acquisisce la **direzione** del colpo, calcolata host-side in
`WeaponController.RequestFire`; RPC one-shot separate dalla validazione, `BroadcastHitReaction(Vector3)`
e `BroadcastDeath(Vector3)`, sul modello di `BroadcastShot`; **nessun ammontare di danno nel payload**.
Morte: disattivare l'`AnimationTree`, attivare `PhysicalBoneSimulator3D`, applicare l'impulso;
**nessun bone sincronizzato via rete**. `PhysicalBoneSimulator3D` **non espone un peso di blend**
animato→fisico: la transizione senza scatti si ottiene inizializzando i `PhysicalBone3D` sulla posa
animata corrente. Serve anche generare la catena di physical bone per `Body_Base`, che non esiste.

**I rig cercano lo `Skeleton3D` scendendo nell'albero dei fratelli** (`SkeletonLocator`). Se un
giorno un avatar avesse due scheletri, prendono il primo.
