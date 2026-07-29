---
name: character-animation
description: Sistema di animazione del personaggio — AnimationTree a LAYER (locomozione unica agnostica dall'arma, impugnatura come posa assoluta mascherata sulle braccia, aim offset e one-shot additivi), clip delta, layer procedurali (mira sul rachide, piedi a terra, presa dell'arma, mani sul bordo, reazione ai muri). Carica questa skill quando tocchi animation/, il CharacterRig, il BlendTree, le clip additive o AdditiveClips.tres, le pose d'impugnatura o WeaponHoldPoses.tres, HoldMask, CharacterAnimator, PlayerAnimationBridge, SpineAimModifier, AimRig, FootIkRig, WeaponGripRig, VaultIkRig, WeaponSpaceProbe, WeaponAnimationSet, quando aggiungi o rinomini una clip, quando il personaggio va in T-pose o le animazioni si fermano, quando un SkeletonModifier3D "non applica", quando agganci un'arma alla mano, o quando lavori su rinculo, mira verticale, piedi a terra, reazione ai colpi, scavalcamento o ragdoll.
---

# Animazione del personaggio

Ambito: `animation/`, piu' `player/scripts/PlayerAnimationBridge.cs`, `ai/scripts/NpcAnimationBridge.cs`,
i tre tool di generazione (`build_weapon_poses.gd`, `build_additive_clips.gd`,
`build_animation_tree.gd`) e i due di verifica. Le clip e il rig vengono da `blender-pipeline`; le armi da `combat-shooting`;
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

### 1.6 I delta additivi NON possono passare da glTF

Il primo tentativo authorava le clip delta in Blender e le faceva viaggiare nel `.glb` della
libreria. **Non funziona**, per due motivi indipendenti, entrambi muti e entrambi misurati:

- **L'esportatore glTF con `export_bake_animation=True` campiona TUTTE le ossa dell'armatura**, non
  solo quelle con una fcurve. Le ossa che il delta non tocca — bacino e gambe — venivano esportate
  con la posa **residua** del pose bone, diversa da una clip all'altra. Misurato: `add_aim_up` e
  `add_aim_center`, che devono differire solo sul rachide, differivano di **0,23 e 0,33 rad sui due
  femori**. Un delta di mira che muove le gambe è esattamente il difetto che l'architettura a layer
  esiste per eliminare.
- **Il rest pose di `CharacterAnimations.glb` non coincide con quello di `Body_Base.glb`**: la
  libreria viene esportata con un'azione assegnata, quindi il TRS dei nodi-osso non è la posa di
  riposo. Ma il delta additivo Godot lo calcola contro il rest dello **scheletro**, che viene da
  `Body_Base`. Misurato: una posa authorata come identità esatta arrivava a **0,07 rad** dall'identità
  — cioè il "centro" dell'aim offset teneva il busto storto.

Da qui la divisione del lavoro, che è una regola e non un dettaglio: **ciò che si consuma contro il
rest dello scheletro si calcola in Godot**, dove quel rest è quello vero. Vale per i delta additivi
(aritmetica pura) e anche per le pose d'impugnatura, che sono assolute ma **derivate** da due clip
Mixamo con una rotazione misurata. In Blender restano le pose che richiedono giudizio artistico e
non hanno una derivazione (`land_soft`, `vault_low`). Vedi §2.1.

### 1.6bis La chiave NEUTRA di un delta additivo è il REST, non l'identità

Dalla semantica additiva (§2.1) `risultato = Base × (Rest⁻¹ × Chiave)` discende che il contributo
**nullo** si ottiene per `Chiave = Rest`. Scrivere `Quaternion.IDENTITY` sulle track che un delta non
deve toccare moltiplica l'osso per **`Rest⁻¹`**, cioè lo ruota dell'inverso della propria posa di
riposo. Non dà errori, e su gran parte del rachide il rest vale 2-6° e non si nota.

Sulle **clavicole di `Body_Base` il rest vale 115°** (misurato). Risultato: accendere l'aim offset
storceva spalle e braccia fino a **scambiarle di lato** — con la presa del fucile, la mano destra
passava da `x = −0,12` a `x = +0,29` e le mani salivano di 0,6 m. È il difetto "quando si mira le
braccia e le spalle sono tutte storte, e peggiora muovendo la mira" (peggiorava perché
`AimSpace` interpola fra cinque pose e il peso di ciascuna cambia col bersaglio).

`_neutral()` in `tools/build_additive_clips.gd` è l'unico posto che deve produrre quella chiave.
Il controllo `_verify_delta_clips` ora misura `Rest⁻¹ × Chiave` su **ogni** track di `add/aim_center`,
non solo su `Spine2`: guardare una vertebra sola non bastava, perché lì il rest è sotto soglia.

### 1.6ter Su una rampa, scrivere `Velocity.Y` a terra spegne lo snap al pavimento

Non è animazione, ma è il modo in cui il rig finisce in posa di **caduta continua camminando**.
Godot applica lo snap al pavimento (`floor_snap_length`) **solo quando la velocità non punta in
alto**. `CharacterMotor` proiettava a mano la velocità sul piano della pendenza
(`planar.Slide(GetFloorNormal())`) e ne scriveva la componente verticale in `Velocity.Y`: salendo
una rampa quella componente è positiva, lo snap salta, il corpo si stacca di frazioni di millimetro
e `IsOnFloor()` lampeggia. Misurato sulla rampa a 20° del livello di prova: **100 frame su 420** con
`SyncGrounded` falso, cioè un quarto del tempo in posa di volo mentre si cammina.

La pendenza la gestisce il motore: `Velocity.Y = 0` a terra, `FloorConstantSpeed = true` (la salita
non costa velocità), `FloorSnapLength` alzato a 0,3 m — il default 0,1 non copre il dislivello di un
tick a 7 m/s. In più `SyncGrounded` ha un'**isteresi** di 0,12 s (`GroundedGraceSeconds`), che copre
spigoli e gradini; la gravità continua a leggere `IsOnFloor()` nudo, quindi la fisica non cambia.
Copre `_verify_slope` nella suite runtime, che fa salire e scendere la rampa a un NPC vero.

### 1.6quater Una posa d'impugnatura può reggere l'arma benissimo e non PUNTARLA

L'arma non ha una direzione propria: pende dalla mano destra con `GripRotationDegrees` e la mano di
supporto le si aggancia a `SupportGripOffset` lungo il suo +Z. Ne segue che **la canna punta lungo la
congiungente mano destra → mano sinistra**, cioè la posa d'impugnatura decide da sola dove va a
finire il colpo — e nessun layer di mira può rimediare, perché aim offset e `SpineAimModifier`
ruotano il **busto** e le braccia lo seguono in blocco portandosi dietro lo scarto.

`rifle_idle`, da cui derivavano entrambe le pose del fucile, **non è una posa di mira**: è un porto
con l'arma di traverso sul petto. Misurato, angolo fra la canna e l'avanti del busto:

| posa | scarto dalla mira |
|---|---|
| `rifle_idle` (era `hold/rifle_aim` e `hold/rifle_lowered`) | **85°** |
| `rifle_fire`, braccia a t = 0 (posa spallata, ma busto girato di 40°) | 43° |
| `hold/rifle_aim` di oggi | **0,03°** |

È il difetto "impugno il fucile e miro, le braccia non vanno in puntamento". La pistola non lo aveva
perché non ha mano di supporto: la sua canna dipende dalla sola `GripRotationDegrees`, che era
misurata contro un asse ragionevole (restavano comunque 25° di scarto, ora azzerati).

**L'unica posa spallata nella libreria è `rifle_fire`**, authorata però su un busto girato: siccome
le braccia sono figlie di `Spine2` quella rotazione se la portano dietro. `build_weapon_poses.gd` ne
prende le braccia e le **riallinea in blocco** ruotando le due clavicole finché la congiungente fra
le mani coincide con l'avanti del busto (iterativo per lo stesso motivo di `SpineAimModifier`: le due
clavicole hanno origini diverse, quindi non è esattamente una rotazione rigida).

Corollario che vale per ogni arma futura: **la posa di mira si valida sulla DIREZIONE della canna,
non su dove finiscono le mani.** Lo copre ora `_verify_aim` con sei casi (`col fucile la canna punta
sulla mira`); con la presa disallineata segna 35,8° e fallisce.

Corollario sul porto rilassato: si ottiene ruotando le **clavicole**, non i bracci. Le due clavicole
nascono quasi nello stesso punto, quindi la rotazione è quasi rigida e la distanza fra le mani —
il vincolo dell'astina — resta invariata al millimetro; ruotando i bracci, che nascono a mezzo metro
l'uno dall'altro, la presa si apriva di 4,5 cm e per giunta la canna quasi non si inclinava
(misurato: clavicole 30° → canna giù di 31° e presa invariata; bracci 42°+10° → canna giù di 9° e
presa da 0,254 a 0,299 m).

### 1.6quinquies L'ORDINE dei modificatori è un accoppiamento invisibile

I `SkeletonModifier3D` girano nell'ordine dei figli dello `Skeleton3D`, e ogni rig procedurale crea
il proprio in `CallDeferred` (§1.3): **chi arriva ultimo dipende dall'ordine dei nodi nella scena**,
che nessuno dichiara e che si cambia spostando un nodo nell'editor.

`SupportHandIk` nasceva **prima** di `SpineAim`. L'IK chiudeva la mano sinistra sull'astina, e subito
dopo il rachide ruotava portandosi dietro tutto il braccio: mirando, la mano restava fino a **36 cm**
fuori dall'arma (misurato: 0,24 m fermi, 0,28 in corsa, 0,36 in strafe). Fuori mira non si vedeva,
perché lì `SpineAimModifier` ha influenza nulla — motivo per cui la sonda che girava con la mira
spenta misurava 1 mm e dichiarava tutto a posto.

Regola: **un vincolo si risolve DOPO tutto ciò che muove la catena su cui insiste.** `WeaponGripRig`
si riporta in coda da solo a ogni frame (`EnsureModifierRunsLast`) invece di sperare nell'ordine
della scena, e la suite verifica l'ordine di esecuzione con un messaggio che lo stampa per intero.

Corollario: due IK sulle stesse ossa vogliono **un arbitro solo**, come il bacino (§4). Le mani le
vogliono `WeaponGripRig` (sull'arma) e `VaultIkRig` (sul bordo): decide `CharacterAnimator`, e
durante lo scavalcamento vince il bordo — `SupportActive` va a false, e un modificatore a influenza
nulla non scrive niente qualunque sia il suo posto in coda.

Corollario 2: **la mano di supporto sta sull'arma sempre**, non solo in mira. Era legata ad `Aiming`
perché il polo del gomito, misurato sulla posa di mira, nel porto rilassato flippava; oggi il porto è
derivato dalla mira ruotando le braccia in blocco, quindi arma e polo ci restano dentro. Misurato su
porto e mira, fermi, in camminata, in corsa e in strafe: la mano resta entro **5 mm** dall'astina.

### 1.6sexies Il polo del gomito vive nel frame dell'ARMA, non del corpo

`WeaponGripRig.SupportElbowHint` è figlio di `GripPoint`, quindi **ruota con
`GripRotationDegrees`**. Cambiare la presa senza rimisurare il polo lo manda dalla parte opposta e il
gomito si piega **al contrario** — e nessun controllo di distanza se ne accorge, perché la mano
continua a raggiungere l'astina lo stesso. È il difetto che si è visto solo a schermo, dopo che la
presa nuova ha ruotato il frame dell'arma di ~180° attorno a Y.

Il valore si misura insieme alla presa: lo stampa `tools/build_weapon_poses.gd`, ed è la posizione
del gomito **nella posa animata**, che è il lato giusto per definizione. Lo copre
`il gomito di supporto si piega dal lato della posa`, che confronta il gomito con IK acceso e con IK
spento: col polo vecchio segna 110° (rovesciato), con quello misurato 4°.

### 1.7 `AnimationNodeSync.sync = false` su un nodo filtrato → gambe congelate

`sync = false` ferma i frame dell'ingresso con peso 0. Su un nodo **filtrato** e' un bug: l'ingresso
a peso 0 resta **visibile** sulle parti che il filtro non copre. Regola: **ogni `Blend2`/`OneShot`
con `filter_enabled = true` deve avere `sync = true`.** E' verificato automaticamente — e oggi il
controllo ha di nuovo qualcosa da controllare, perché `HoldMask` è filtrato (§2).

### 1.8 Eulero YXZ riavvolge oltre i 90 gradi

`Node3D.rotation` e `Basis.GetEuler()` usano l'ordine YXZ, in cui la componente X e' la rotazione di
mezzo e vive in `[-90°, +90°]`. La presa del fucile e' gia' a **-76°**: sommarci i 55° del "port arms"
supera il limite e la decomposizione restituisce un triplo equivalente ma diverso. La `Basis` e'
giusta, l'angolo letto no. **Misura la direzione (`basis.z`), non l'angolo.**

Costo storico: il rinculo ruotava il muso **in basso** invece che in alto, e nessuno se n'era accorto
perche' il controllo misurava di quanto si sposta la presa, non in che direzione punta la canna.

**Il beccheggio non si somma più all'Eulero**, ed è per questo. Sommare i gradi alla X della presa
sembra equivalente e non lo è: nell'ordine YXZ l'effetto di quella somma **cambia segno con la Y
della presa**. Con `GripRotationDegrees.Y = 37` alzava; alla prima presa nuova (`Y = 179`, misurata
sulla posa di mira vera del fucile) abbassava, senza che nulla nel codice lo lasciasse vedere.
Oggi `WeaponGripRig.PitchMuzzleUp` ruota attorno all'asse **`canna × alto`**, che porta la canna
verso l'alto del mondo per costruzione: nessuna arma futura può più invertirne il segno. Vale sia
per il rinculo sia per il "port arms".

### 1.9 Il frame in headless dura ~7 ms, non 16,7

Riguarda le sonde. Senza vsync, contare i frame per aspettare la fine di una clip da' un'attesa lunga
meno della meta' del previsto. Dove conta la durata REALE si usa `_settle_seconds()`.

### 1.10 `intersect_ray` non vede le forme da dentro

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
WalkSpace ─┐                                    (rombo WalkSpeed)
           ├─ MoveBlend ─┐                      (Blend2 sync)
RunSpace ──┘             ├─ CrouchBlend ─┐
CrouchSpace ─────────────┘               ├─ AirBlend ─┐
FallClip (fall_idle) ────────────────────┘            │
                                            ├─ HoldMask (Blend2 FILTRATO) ─┐
RifleLowered ─┐                                       │                    │
RifleAim     ─┤                                       │                    │
PistolIdle   ─┼─ WeaponPose (Transition a 4) ─────────┘                    │
PistolAim    ─┘  [clip hold/*]                                             │
                                                                         │
AimSpace (BlendSpace2D di 5 pose add/aim_*) ──── AimAdd (Add2) ──────────┘
                                                      │
RifleFireClip ─┐                                      │
               ├─ FirePose (Transition) ── Fire (OneShot ADD) ────┐
PistolFireClip ┘  [clip add/*]                                    │
                                                                  │
HitFront/Back/Left/Right ─ HitPose (Transition a 4) ─ Hit (OneShot ADD) ─┐
  [clip add/*]                                                          │
                                                                        │
JumpClip ─ JumpScale ─── Jump (OneShot BLEND) ──────────────────────────┤
LandHardClip ─┐                                                         │
              ├─ LandPose (Transition) ─ Land (OneShot BLEND) ──────────┤
LandSoftClip ─┘                                                         │
VaultClip ────────────────── Vault (OneShot BLEND) ─────────────────────┴─ output
```

**La locomozione e' UNA SOLA e non sa nulla delle armi.** E' il cambiamento piu' importante rispetto
alla versione precedente, che aveva due set completi di clip armate (otto clip) piu' un
`StanceBlend` full-body per sceglierli. Reggere un fucile non cambia come si cammina: cambia cosa
fanno busto e braccia, e quello e' un **delta additivo**.

- **`HoldMask` = impugnatura**, `Blend2` **filtrato sulle otto ossa delle braccia** (clavicola,
  braccio, avambraccio, mano ×2), acceso con qualunque arma in mano. La posa la sceglie il
  `Transition` `WeaponPose` a 4 ingressi (`rifle_lowered` / `rifle_aim` / `pistol` / `pistol_aim`),
  da arma + stato di mira. **La mira e' uno STATO (`Aiming`, da RMB)**, non una conseguenza
  dell'essere armati.

  **Perché una maschera e non un `Add2`, ed è misurato.** L'impugnatura è stata additiva per una
  versione, e non poteva funzionare: un delta costante applica al braccio una rotazione
  *relativa*, quindi riproduce la posa giusta solo quando la base coincide con la clip di
  riferimento. Distanza fra le due mani, che reggendo un fucile **deve** restare costante (la
  lunghezza dell'astina, su cui è misurato `SupportGripOffset`):

  | base | delta additivo | maschera assoluta |
  |---|---|---|
  | `idle_neutral` (= riferimento) | 0,392 m | 0,392 m |
  | `walk_fwd` | **0,58 m** | 0,392 m |
  | `run_fwd` | 0,39 m (per caso) | 0,392 m |
  | `crouch_fwd` | mani all'altezza del **bacino** | 0,392 m |

  (Misure fatte quando la posa derivava da `rifle_idle`; oggi la posa di mira è un'altra e la
  costante vale **0,254 m** — §1.6quater. Ciò che conta qui è la colonna, non il numero: la
  maschera lo tiene fermo su qualunque base, il delta no.)

  L'oscillazione delle braccia della camminata resta nella base e si somma alla presa: le mani
  ballano attorno all'arma invece di stringerla. La presa è un **vincolo geometrico** — due mani
  sulla stessa arma — non uno scarto da sommare, e i vincoli si esprimono con una posa assoluta e
  una maschera. `Spine*`, `Hips` e le gambe **non** sono nella maschera, quindi il busto continua
  a respirare, a oscillare in corsa e ad accovacciarsi, e le braccia — figlie dello stesso
  `Spine2` — lo seguono in blocco tenendo la presa. Lo copre `_verify_hold_mask`, che rimisura
  quella distanza su ogni asse di locomozione, accovacciati e in mira a fondo corsa.

  Corollario: **da armati le braccia non oscillano più in corsa.** È voluto (è come si porta
  un'arma) e va corretto, se mai servisse, con un *layer additivo di bob* sopra la maschera, mai
  rimettendo la locomozione dentro le braccia.
- **`AimAdd` + `AimSpace` = aim offset**, la sfera di mira continua a 5 pose additive
  (centro/su/giu'/sinistra/destra) pilotata da yaw e pitch della mira **relativi al corpo**,
  normalizzati su `AimYawRangeDeg`/`AimPitchRangeDeg`. Mette il grosso della posa; l'errore residuo
  — che dipende da quale clip sta girando e con che peso — lo chiude `SpineAimModifier` rimisurando
  ogni frame (§4).
- **`Fire` e `Hit` sono `OneShot` in `MIX_MODE_ADD`** su clip delta: rinculo e flinch si sommano a
  qualunque cosa stiano facendo locomozione, impugnatura e mira. Un colpo incassato si vede identico
  in piedi, accovacciati, in corsa o mentre si mira, e sparare mirando in alto non riabbassa l'arma.
- **`Jump`, `Land`, `Vault` restano `MIX_MODE_BLEND` full body**: sono movimenti che coinvolgono
  tutto il corpo e sostituiscono la locomozione, non la modificano.
- **`FirePose`, `HitPose`, `LandPose`** sono `Transition` a xfade 0 davanti ai one-shot: la clip di
  sparo la dichiara l'ARMA (`WeaponAnimationSet.FirePose`), la direzione del flinch la mappa
  `TriggerHitReaction`, la clip d'atterraggio la sceglie `TriggerLand` dai regimi. **La richiesta va
  impostata PRIMA di far partire il one-shot.**

**Niente filtri sui nodi ADDITIVI, e non e' una dimenticanza.** La maschera upper-body vive nelle
CLIP: quelle delta hanno solo le 13 track del busto e delle braccia, quindi un bone senza track non
riceve nulla, qualunque cosa faccia l'albero. E' piu' robusto di un filtro (che si mantiene a mano e
si sbaglia in silenzio) ed e' verificato da due lati — strutturale, "nessuna track sotto il bacino",
e comportamentale, "accendere il layer non muove le gambe". **L'unico nodo filtrato è `HoldMask`**,
dove il filtro serve per forza: la posa d'impugnatura *sostituisce* le braccia invece di sommarcisi.
Anche lì la maschera è doppia — filtro dell'albero **e** clip prive di track fuori dalle braccia — e
`verify_godot_import` controlla che il filtro copra quelle otto ossa e nient'altro.

Chi usa cosa: **qualunque arma, qualunque postura** → la stessa locomozione + `HoldMask` con la posa
giusta. Il crouch armato, che prima era un caso speciale, e' semplicemente il crouch con le braccia
mascherate sopra. Il turn-in-place resta il passo sintetico di `UpdateLocomotion` (§3).

Il resto delle scelte:
- **camminata e corsa sono due spazi**, non uno: la corsa messa come punto in piu' sull'asse Y
  sarebbe collineare con `idle` e `walk_fwd` (§1.4).
- **le diagonali non hanno clip proprie e non devono averne**: cadono dentro i triangoli e vengono
  sintetizzate. Mixamo non ha diagonali affidabili e il blend delle cardinali e' lo standard.
- **`WeaponPose` e' un `Transition`**: fra "reggi fucile" e "reggi pistola" non c'e' una via di mezzo.
  Sui DELTA l'xfade di 0,15 s interpola verso/da identita' senza artefatti.
- **la maschera upper-body include le clavicole**: senza, la spalla resta alla posa di corsa.
- **`AirBlend` sta prima dei layer additivi**, cosi' cadendo si continua a impugnare e a mirare.
- **`JumpScale`**: `jump` e' un arco di 1,03 s ma il volo vero dura `2·v/g` (~0,6 s). Senza
  riscalare, si atterra a clip ancora in aria.

### 2.1 Le clip generate: tre librerie, tre sorgenti

L'`AnimationTree` monta **tre** `AnimationLibrary`, ed e' una divisione di responsabilita', non un
dettaglio di packaging:

| Libreria | Contenuto | Generata da |
|---|---|---|
| `""` (senza prefisso) | clip **assolute** full body: Mixamo + procedurali (`walk_fwd`, `rifle_idle`, `vault_low`, …) | Blender → `.glb` |
| `"add"` | clip **delta** additive (`add/aim_up`, `add/hit_front`, `add/rifle_fire`) | `tools/build_additive_clips.gd`, **in Godot** |
| `"hold"` | pose **assolute delle sole braccia** (`hold/rifle_aim`, `hold/pistol`, …) | `tools/build_weapon_poses.gd`, **in Godot** |

Le pose `hold/*` derivano da **due** clip Mixamo: `rifle_fire` per il fucile e `pistol_idle` per la
pistola. Non da `rifle_idle`, che è un porto con l'arma di traverso e non punta: il perché, con le
misure, è §1.6quater. Le braccia del fucile vengono **riallineate** finché la canna coincide con
l'avanti del busto, quindi `GripRotationDegrees` e `SupportGripOffset` vanno **rimisurati** — è il
tool stesso a calcolarli e stamparli nella forma da ricopiare nei `.tres` (§7).

Il porto rilassato si ottiene ruotando le braccia verso il basso di un angolo **tarato sulle misure
che il tool stampa** (quota delle mani e inclinazione della canna), non scelto a occhio: la versione
authorata in Blender usava 35°+15° e portava le mani a `y = +0,01 m` e `z = +0,04 m` dal bacino —
braccia lungo il corpo e mani dentro i fianchi, il difetto segnalato. Oggi la rotazione va sulle
**clavicole** (§1.6quater): 30° per il fucile, 34°+10° di avambraccio per la pistola, con le mani
davanti al corpo e la canna inclinata verso terra.

Di conseguenza `rifle_idle`, `rifle_lowered_idle`, `rifle_aim_idle` e `pistol_aim_idle` restano nel
`.glb` ma **l'albero non le usa più**, come le otto clip di locomozione armata: non costano nulla e
cancellarle dal `.glb` sarebbe irreversibile. Le tre procedurali `rifle_*_idle` sono per giunta
**derivate da `rifle_idle`**, quindi ereditano il difetto di §1.6quater: non ricablarle.

I delta non passano da Blender: il perche' e' §1.6, ed e' stato misurato. Il tool li calcola contro
il rest pose vero di `Body_Base.glb` usando la semantica additiva di Godot, anch'essa **misurata**
con una sonda headless:

> **`risultato = Base × (Rest⁻¹ × Chiave)`** — riferimento = rest pose, composizione
> post-moltiplicata in spazio locale, per-track.

Da cui la formula di authoring: per ottenere `Target` quando la base vale `Riferimento`, la chiave e'
`Chiave = Rest × Riferimento⁻¹ × Target`. E' quello che fa `_key_for()`.

Ne discendono due proprieta' che valgono solo perche' i delta sono calcolati e non disegnati:
- il **centro** dell'aim offset e' l'identita' **esatta**, quindi mirare dritto davanti non storce il
  busto di qualche grado;
- l'impugnatura sommata sull'idle riproduce **esattamente** la posa assoluta da cui e' derivata —
  quella su cui sono misurati `GripRotationDegrees` e il polo del gomito (§7).

**Ordine di rigenerazione, quando cambiano le clip:**
```
python tools/blender/blender_client.py tools/blender/build_procedural_clips.py   # pose assolute
Godot --path . --headless --import                                               # reimport del .glb
Godot --path . --headless --script tools/build_weapon_poses.gd                   # pose d'impugnatura
Godot --path . --headless --script tools/build_additive_clips.gd                 # delta
Godot --path . --headless --script tools/build_animation_tree.gd                 # albero
```
I tre tool Godot non richiedono Blender: se si tocca solo l'impugnatura, la mira o il rinculo bastano
gli ultimi tre comandi.
Saltare il reimport in mezzo e' il modo tipico di generare delta contro clip vecchie.

## 3. Parametri esposti

`CharacterAnimator.cs` e' l'**unico** punto di accoppiamento fra C# e struttura dell'albero: se
rinomini un nodo, i `const` in cima a quel file vanno aggiornati (e cosi' i due tool).

| Parametro | Significato |
|---|---|
| `WalkSpace` / `RunSpace` / `CrouchSpace` `blend_position` | la **stessa** velocita' locale (X = destra, Y = avanti), proiettata sul rombo di ciascuno; da fermi in mira ci si somma il **passo sintetico del turn-in-place** (`TurnRate · TurnStepScale` su X) |
| `MoveBlend/blend_amount` | peso della corsa: `clamp01((‖v‖−Walk)/(Run−Walk))` |
| `CrouchBlend` · `AirBlend` `blend_amount` | accovacciato, in aria — smorzati |
| `HoldMask/blend_amount` | peso della maschera d'impugnatura: acceso con qualunque arma in mano |
| `WeaponPose/transition_request` | `"rifle_lowered"` / `"rifle_aim"` / `"pistol"` / `"pistol_aim"` |
| `AimSpace/blend_position` | aim offset **normalizzato** in `[-1, 1]`: X = yaw (positivo = destra), Y = pitch (positivo = su), da `AimDirection` portata nel riferimento del rig e divisa per `AimYawRangeDeg` / `AimPitchRangeDeg` |
| `AimAdd/add_amount` | peso dell'aim offset: acceso in mira, spento fuori |
| `FirePose/transition_request` | `"rifle_fire"` / `"pistol_fire"` — da `WeaponAnimationSet.FirePose`, al cambio d'arma |
| `HitPose/transition_request` | `"front"` / `"back"` / `"left"` / `"right"` — da `TriggerHitReaction`, prima del one-shot |
| `LandPose/transition_request` | `"land_hard"` / `"land_soft"` — impostata da `TriggerLand` prima del one-shot |
| `Fire/request` · `Hit/request` · `Jump/request` · `Land/request` · `Vault/request` | one-shot |
| `JumpScale/scale` | `durata clip / JumpFlightTime` |

**`AimYawRangeDeg` (60) e `AimPitchRangeDeg` (45) di `CharacterAnimator` devono combaciare con
`AIM_YAW_DEG` e `AIM_PITCH_DEG` di `tools/build_additive_clips.gd`**: le pose additive sono generate
a quegli angoli e la `blend_position` e' normalizzata su di essi. Se divergono, la mira e' scalata
male — sintomo muto, perche' il busto si muove comunque, solo della quantita' sbagliata.

Stato in ingresso rilevante oltre alla velocita': `Aiming` (stance di mira, da `SyncAiming`),
`AimDirection` (mondo, da `AimVector(SyncAimYaw, SyncAimPitch)`) e `TurnRate` (rad/s, derivata
smorzata di `SyncFacing` calcolata dai bridge — positiva = sinistra).
`TriggerLand` ha TRE regimi alternativi: `>= HardLandingSpeed` → `land_hard`; `>= SoftLandingSpeed`
(default 6.5, sopra l'impatto di un salto normale) → `land_soft`; sotto → solo dip procedurale.
`TriggerHitReaction(Vector3)` riceve la direzione di **volo** del proiettile in coordinate mondo e la
mappa su uno dei quattro ingressi nel riferimento del rig (che guarda +Z, sinistra +X).
`TriggerVault(Vector3)` riceve il punto del bordo e avvia insieme il one-shot e `VaultIkRig`.

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
| `VaultIkRig` | durante lo scavalcamento porta le mani sul bordo VERO, dentro la finestra di contatto della clip | l'altezza e la distanza dell'ostacolo le conosce solo il raycast di quell'istante: e' cio' che permette **una sola** clip di vault |
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
- **La mano di supporto insegue l'astina SEMPRE** (`WeaponGripRig.SupportActive`, scritto da
  `CharacterAnimator`), non solo in mira: e' un vincolo fisico, non un effetto. Cede le mani solo
  allo scavalcamento. Perche' regga anche nel porto rilassato, e perche' il polo del gomito non
  flippi, vedi §1.6quinquies e §1.6sexies.
- **`SupportHandIk` deve girare per ULTIMO**: e' un vincolo che si chiude sull'arma, e qualunque
  modificatore successivo glielo porta via (§1.6quinquies).
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
risulta specchiato in tutti i blend space e il lean si inclina dal lato sbagliato — e' successo,
senza un solo errore, perche' nessuna sonda misurava la DIREZIONE. Ora la misurano:
`_verify_strafe_direction` (albero) e i test di `WorldToLocalVelocity` (motore) nella suite runtime.
Lo stesso contratto vale per l'aim offset e per la direzione del flinch, che `CharacterAnimator`
porta nel riferimento del rig con `GlobalTransform.Basis.Inverse()`.

Gli eventi one-shot (`Jumped`, `Landed`, `Vaulted`, `ShotResolved`, `HitReaction`) arrivano da RPC
che ogni peer riemette come segnale **locale**. Nel payload non viaggia mai un esito di gioco
(CLAUDE.md §3), solo **grandezze fisiche o geometriche**:

| Evento | Payload | Emesso da |
|---|---|---|
| `Landed` | velocita' d'impatto (m/s) — decide solo quanto flette il bacino | `CharacterMotor` → RPC del derivato |
| `Vaulted` | punto del bordo (mondo) — misurato dai raycast, alimenta l'IK delle mani | `CharacterMotor` → RPC del derivato |
| `HitReaction` | direzione di **volo** del proiettile (mondo) — **mai** l'ammontare di danno | `HealthComponent.BroadcastHitReaction`, host → tutti |

`HitReaction` parte **dopo** che il danno e' stato applicato, dentro `ApplyDamage`: cosi' un colpo
rifiutato dalla validazione non produce mai una reazione. E' `Unreliable` di proposito — un flinch
perso non desincronizza nulla, lo stato vero e' la salute replicata.

## 6. Verifiche automatiche

```
Godot --path . --headless --script tools/verify_godot_import.gd       # struttura
Godot --path . --headless --script tools/verify_animation_runtime.gd  # comportamento
```

`verify_godot_import.gd`: scheletro, skin, scala, track, **`loop_mode` per categoria**, esistenza dei
parametri, **copertura a triangoli di ogni BlendSpace2D**, **`sync` su ogni nodo filtrato** e
**che cosa** filtra `HoldMask` (le otto ossa delle braccia e nient'altro: una maschera allargata al
rachide o al bacino spegnerebbe la locomozione senza dare un solo errore).

`verify_animation_runtime.gd` fa girare l'albero e misura le ossa (**161 controlli**): le
combinazioni che non devono essere la T-pose (ogni asse di camminata, corsa e crouch, aria, gli
stessi assi con l'impugnatura accesa, i cinque estremi dell'aim offset), dieci secondi di
camminata che non deve congelarsi, le gambe che continuano a muoversi con impugnatura e mira accese
e sotto una raffica, **l'isolamento dei layer additivi** (§ sotto), **la presa che regge su ogni
locomozione** (`_verify_hold_mask`, § sotto), le quattro direzioni del flinch
distinte a coppie opposte, il vault che e' full body e rientra, i one-shot che rientrano, la presa
che segue la mano, il rinculo che **alza** il muso, la mano di supporto che raggiunge l'astina,
l'asse dell'arma che passa fra le due mani, il busto che punta sulla mira anche in strafe, **la
canna che punta DOVE SI MIRA** su sei fra direzioni e locomozioni (§1.6quater), i piedi
che riproducono un dislivello di 12 cm, l'arma che si alza contro un muro, l'NPC che si anima
sullo stesso rig, e **una rampa a 20 gradi percorsa in salita e in discesa senza mai staccarsi da
terra** (`_verify_slope`, §1.6ter).

**Due controlli hanno le stesse mani come sonda, e non e' ridondanza.** `_verify_hold_mask` misura
la **distanza fra le mani** (deve restare 0,254 m: e' il vincolo dell'astina) su fermi, camminata,
strafe, corsa, accovacciati e mira a fondo corsa; `_verify_grip` misura **dove** finisce l'arma. Il
primo prende i difetti dell'albero (base sbagliata, filtro sbagliato), il secondo quelli dei rig
procedurali. Il difetto dell'impugnatura additiva passava il secondo e falliva solo il primo — che
allora non c'era.

**L'isolamento dei layer additivi e' l'invariante centrale** e va verificato da due lati, perche' un
solo lato non basta:
- *strutturale* (`_verify_delta_clips`): nessuna clip `add/*` ha track sotto il bacino, e il centro
  dell'aim offset e' l'identita' esatta. Legge le track, non le pose.
- *comportamentale* (`_verify_additive_isolation`): accendere un layer cambia il busto e **non** le
  gambe. Misurato: impugnatura 1,23 rad sul busto contro 0,0007 sulle gambe, cioe' esattamente la
  deriva della clip di base.

*Trappola di misura*: l'effetto di un layer si misura sul suo **scatto**, non a regime. Confrontare
pose lontane nel tempo non funziona perche' la clip di base avanza, e la sua deriva **dipende dalla
fase del ciclo** — misurato: 0,034 rad in una finestra e 0,064 in un'altra, senza che nulla fosse
cambiato. I pesi si scrivono diretti sull'albero (senza lo smorzamento di `CharacterAnimator`),
quindi l'effetto e' immediato: si confronta lo scatto col drift di **due frame** nella stessa fase.

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

**`GripRotationDegrees` e `SupportGripOffset` si MISURANO dalla posa, non si indovinano.** Non serve
più una sonda a parte: li calcola e li stampa `tools/build_weapon_poses.gd` ogni volta che rigenera
le pose, nella forma da ricopiare nei `.tres`. **Rigenerando le pose vanno riportati**, altrimenti
posa e presa divergono in silenzio.

| | `GripRotationDegrees` | `SupportGripOffset` |
|---|---|---|
| `two_handed.tres` (da `rifle_fire`, riallineata) | `(-66.1, 179.0, 0)` | `(0, 0, 0.254)` |
| `one_handed.tres` (da `pistol_idle`) | `(-59.0, -158.8, 0)` | `(0, 0, 0)` |

La presa si allinea alla **congiungente fra le mani** per le armi a due mani (la mano di supporto sta
sull'astina, e quello è il vincolo) e all'**avanti del busto** per quelle a una mano, dove l'altra
mano non vincola nulla. Nella posa di mira del fucile le due coincidono per costruzione: è ciò che
il riallineamento di §1.6quater impone.

Allineare l'arma al **bacino** sarebbe sbagliato: sotto l'impugnatura c'e' una locomozione qualsiasi,
che il bacino lo ruota di decine di gradi (misurato: `pistol_idle` **54°**), e l'arma finirebbe di
traverso. L'osso di riferimento è `Spine2`, cioè il vertice della catena di `SpineAimModifier` —
l'osso che i layer di mira portano davvero sul bersaglio. Il placeholder in `WeaponVisual` e'
costruito **attorno alla presa** — calcio dietro, canna avanti, astina esattamente su
`SupportGripOffset` — proprio perche' un disallineamento si veda a colpo d'occhio.

Le misure restano valide dopo il passaggio ai layer additivi **perche' il delta e' costruito proprio
da quelle pose**: `add/rifle_aim` sommato sull'idle riproduce `rifle_aim_idle` esattamente (§2.1),
che e' la posa su cui presa e polo del gomito sono stati misurati.

## 8. Lacune volute e lavoro aperto

Sono scelte, non dimenticanze. Non "sistemarle" senza leggere il motivo.

**Budget clip: 34 assolute + 11 delta + 4 pose d'impugnatura.** Le assolute stanno nel `.glb`:
28 Mixamo + 6 **procedurali** (`rifle_aim_idle`, `rifle_lowered_idle`, `pistol_aim_idle`,
`pistol_fire`, `land_soft`, `vault_low`), generate da `tools/blender/build_procedural_clips.py`
(skill `blender-pipeline`) — Mixamo non e' piu' una sorgente disponibile, le clip nuove si
costruiscono campionando pose da clip esistenti. Gli 11 delta stanno in `AdditiveClips.tres` e le 4
pose d'impugnatura in `WeaponHoldPoses.tres`: tutte **calcolate** (§2.1), non authorate.

Le **otto clip di locomozione armata** (`rifle_walk_*`, `rifle_run_*`) sono ancora nel `.glb` ma
**l'albero non le usa piu'**: le ha sostituite la maschera d'impugnatura. Restano perche' cancellarle
dal `.glb` e' irreversibile (le FBX non sono nel repo) e non costano nulla se non referenziate. Non
ricablarle: se una posa armata in movimento non convince, la risposta e' una posa d'impugnatura
migliore o un layer additivo in piu' sopra la maschera, non un set di clip per arma.

**Mancano ancora**: posa iniziale di morte, clip di turn-in-place (oggi passo sintetico; da
rivalutare se non legge bene a schermo). Il crouch armato, l'atterraggio morbido, lo scavalcamento e
le reazioni direzionali ai colpi NON mancano piu'. **Mai una clip per combinazione arma × movimento**:
se un'arma "richiede" una locomozione nuova, la risposta e' quasi sempre un delta upper-body in piu'.

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

**Atterraggio a TRE regimi.** Oltre `HardLandingSpeed` parte `land_hard`; fra `SoftLandingSpeed` e
Hard parte `land_soft`; sotto resta la sola flessione procedurale del bacino. Sono **alternativi**,
non sommati: la clip contiene gia' la propria flessione, e sommarci quella procedurale farebbe
sprofondare il personaggio nel pavimento.

**Fase di camminata e corsa non allineate.** `walk_fwd` dura 1,067 s e `run_fwd` 0,667 s: nel
crossfade i piedi possono risultare fuori fase per un istante. `sync = true` li fa avanzare ma **non**
li mette in fase — servirebbe un ritaglio delle clip. Accettato.

**Scavalcamento: una clip, il resto e' codice.** `CharacterMotor.TryStartVault` aggancia il vault a
partire dalla richiesta di SALTO — stesso tasto, decide la geometria — con tre raycast: parete
davanti entro `VaultReach`, sommita' dentro `[VaultMinHeight, VaultMaxHeight]` (0,5–1,2 m), e un
punto d'atterraggio oltre il bordo. Se una qualunque delle tre misure manca, non e' un vault ed e' un
salto normale. Poi `StepVault` **scrive** la posizione (movimento scriptato: `MoveAndSlide`
combatterebbe contro l'ostacolo che si sta scavalcando) su una traiettoria start → bordo →
atterraggio, e `VaultIkRig` mette le mani sul bordo vero.

**`VaultDuration` (0,9 s) deve combaciare con la durata di `vault_low`**: e' il tempo su cui il
warping distribuisce la traiettoria, e se diverge dalla clip le pose arrivano prima o dopo i punti di
contatto. La suite lo verifica.

**Danno direzionale: FATTO.** `HealthComponent.ApplyDamage` acquisisce la **direzione** del colpo,
calcolata host-side in `WeaponController.RequestFire` (e' `shotDir`, dopo la dispersione);
`BroadcastHitReaction(Vector3)` e' una RPC estetica separata dalla validazione, sul modello di
`BroadcastShot`; **nessun ammontare di danno nel payload**.

**Morte: non iniziata.** Il contratto resta quello deciso: `BroadcastDeath(Vector3)` con la sola
direzione; disattivare l'`AnimationTree`, attivare `PhysicalBoneSimulator3D`, applicare l'impulso;
**nessun bone sincronizzato via rete**. `PhysicalBoneSimulator3D` **non espone un peso di blend**
animato→fisico: la transizione senza scatti si ottiene inizializzando i `PhysicalBone3D` sulla posa
animata corrente. Serve anche generare la catena di physical bone per `Body_Base`, che non esiste.

**I rig cercano lo `Skeleton3D` scendendo nell'albero dei fratelli** (`SkeletonLocator`). Se un
giorno un avatar avesse due scheletri, prendono il primo.
