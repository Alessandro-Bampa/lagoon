---
name: blender-pipeline
description: Pipeline degli asset 3D Blender -> .glb -> Godot. Carica questa skill quando tocchi assets/models/, tools/blender/, Body_Base o Armature_Character, quando devi generare o rigenerare un personaggio/NPC, quando importi animazioni Mixamo, quando lavori su scala/unita' di un modello importato, oppure quando parli con Blender via MCP (estensione Blender Lab, porta 9876, blender_execute).
---

# Pipeline asset 3D: Blender -> .glb -> Godot

Eccezione dichiarata alla regola dei placeholder (CLAUDE.md §7): questa pipeline produce asset veri.
Vale **solo** per `assets/models/`; gli altri sistemi restano a placeholder geometrici.

Primo asset prodotto: `Body_Base`, base mesh nuda del personaggio, usata sia per il player sia per gli
NPC.

---

## 1. Come si parla con Blender

Blender ha installata l'estensione **ufficiale Blender Lab `mcp` v1.0.0** (Blender 5.2, richiede >= 5.1),
non l'addon `ahujasid/blender-mcp` che circola online. I due hanno protocolli **diversi e incompatibili**:
non registrare i pacchetti PyPI `blender-mcp` / `blender-mcp-server`, parlano l'altro protocollo.

Protocollo dell'estensione ufficiale — TCP `127.0.0.1:9876`, JSON delimitato da null byte:

```
richiesta:  {"type":"execute","code":"<python>","strict_json":true}\0
risposta:   {"status":"ok","result":{...},"stdout":"..."}\0
            {"status":"error","message":"<traceback>"}\0
```

**Il codice inviato DEVE assegnare `result` come dict JSON-serializzabile**, altrimenti l'estensione
risponde con un errore. Il sandbox blocca solo `sys.exit` e 4 operatori `wm.*`: **l'I/O su file e'
permesso**, quindi export e render su disco funzionano.

Due modi per usarlo, entrambi versionati nel repo:

| Percorso | Quando |
|---|---|
| `python tools/blender/blender_client.py <script.py>` | Script lunghi e ripetibili. E' il modo usato per costruire l'asset. |
| Tool MCP `blender_execute` (`.mcp.json` -> `tools/blender/mcp_bridge.py`) | Query veloci e interattive. Richiede riavvio di Claude Code per comparire. |

### Trappola: il contesto degli operatori

Il codice arriva da un timer, **fuori da qualunque area dell'editor**. Tutti gli operatori con un
`poll()` sul contesto falliscono con `context is incorrect`, e l'esportatore glTF esplode ancora prima
con `'Context' object has no attribute 'active_object'`.

- Dove possibile evita `bpy.ops`: usa `bmesh` e `evaluated_get(depsgraph)` (vedi `bake_modifiers`).
- Dove non e' possibile (unwrap, pesi, render, **export glTF**), avvolgi in
  `view3d_override(...)` di `build_character.py`, che costruisce un `temp_override` con una vera area
  `VIEW_3D`.

---

## 2. Rigenerare l'asset

`tools/blender/build_character.py` e' la **sorgente di verita'**. Non modificare il `.blend` a mano:
si perde e viene sovrascritto alla rigenerazione successiva.

```
python tools/blender/blender_client.py tools/blender/build_character.py   # genera + esporta
python tools/blender/verify_glb.py                                        # valida il .glb (15 check)
Godot_console.exe --path . --headless --script tools/verify_godot_import.gd   # valida in Godot
```

Lo script e' idempotente: parte da `read_homefile(use_empty=True)`, quindi rieseguirlo non accumula
stato. Sovrascrive il `.blend` e il `.glb` senza chiedere.

### Come e' costruita la mesh

Non e' una cage modellata a mano: e' uno **Skin modifier su un grafo di edge parametrico** (tabella
`SKIN_NODES`, raggi ellittici per-nodo) seguito da **Catmull-Clark livello 2**. La ragione e' che le
giunzioni ramificate (spalla, anca, inguine) le risolve il modifier invece di una tabella di facce
scritta a mano.

Conseguenze da conoscere:

- **Un nodo a 4 rami e' inevitabile** nel torso (`chest`: spine, collo, due clavicole). Se i raggi dei
  nodi adiacenti divergono troppo, lo Skin modifier ci genera uno **scalino o una cavita' sulla spalla**.
  Mitigazione gia' applicata: raggi vicini tra loro intorno all'hub e `branch_smoothing = 0.55`.
  Se ritocchi quei raggi, **guarda i render** prima di fidarti dei numeri.
- Lo Skin modifier lascia **vertici sciolti** ai nodi di ramificazione: vengono cancellati
  esplicitamente in `stage_mesh`. Il controllo `loose_verts` deve restare a 0.
- La subdivision **restringe** il volume: l'altezza finale non e' quella della tabella. Non inseguirla
  a mano — `fit_transform()` la misura e la corregge, e **lo stesso fit viene applicato alla tabella
  dei bone**, cosi' i giunti restano dentro i loop giusti. Se tocchi uno dei due, tocchi entrambi.

Loop di lavoro: modifica i parametri -> rigenera -> **leggi i PNG** in scratchpad (`body_front`,
`body_side`, `body_iso`). La vista `iso` usa la stessa inclinazione della camera di gioco: e' quella
che decide se la silhouette funziona. In vista laterale il braccio in T-pose punta verso la camera e
appare come un'ellisse scura: **non e' un buco**.

---

## 3. Invarianti che non vanno rotti

`stage_scale_gate()` blocca l'export se uno di questi salta. Non aggirarlo.

| Invariante | Valore |
|---|---|
| `scene.unit_settings.scale_length` | esattamente `1.0` (1 unita' = 1 metro) |
| Scala di `Body_Base` e `Armature_Character` | `(1,1,1)`, mai una scala sull'oggetto |
| Origine di entrambi gli oggetti | `(0,0,0)` |
| Altezza | 1.75 - 1.80 m (attuale: **1.78 esatti**) |
| Piedi | `min z == 0` (origine a terra) |
| Budget | 6.000 - 10.000 tris (attuale: **7.456**) |
| Topologia | 100% quad, 0 edge non-manifold, 0 vertici sciolti |
| Influenze per vertice | <= 4, nessun vertice non skinnato, pesi normalizzati |
| UV | 0 facce sovrapposte |
| Twist residuo fra bone consecutivi | < 20 gradi (attuale: **5,33 max**) |

### Sul famoso fattore 0.01

**E' un problema dell'FBX, non del glTF.** glTF 2.0 e' metrico per definizione e il suo esportatore non
ha alcun moltiplicatore di unita'. I rischi reali sono: `scale_length != 1`, una scala non applicata
sull'oggetto, o un **round-trip via FBX/Mixamo**. I primi due sono coperti dal gate; il terzo e' escluso
per costruzione perche' il rig non passa da Mixamo (vedi §4). `verify_glb.py` ricontrolla comunque tutto
leggendo il binario, senza fidarsi di cosa Blender dichiara di aver esportato.

---

## 4. Il rig e' generato in Blender, NON da Mixamo

Scelta deliberata. L'auto-rigger di Mixamo richiede un upload manuale su un servizio Adobe (non
automatizzabile, non riproducibile), restituisce **FBX con scala 0.01** e la propria gerarchia, e i suoi
pesi automatici non rispettano il vincolo delle 4 influenze.

Quello che si conserva di Mixamo sono **i nomi e la gerarchia**, quindi le sue animazioni restano
retargetabili. 25 bone (22 deform + 3 leaf `_End`):

```
Hips ─ Spine ─ Spine1 ─ Spine2 ─┬─ Neck ─ Head ─ HeadTop_End
                                ├─ LeftShoulder ─ LeftArm ─ LeftForeArm ─ LeftHand
                                └─ RightShoulder ─ RightArm ─ RightForeArm ─ RightHand
Hips ─┬─ LeftUpLeg ─ LeftLeg ─ LeftFoot ─ LeftToeBase ─ LeftToe_End
      └─ RightUpLeg ─ RightLeg ─ RightFoot ─ RightToeBase ─ RightToe_End
```

- **`LeftShoulder`/`RightShoulder` sono obbligatorie.** Non erano nella lista iniziale, ma senza
  clavicole la gerarchia Mixamo si spezza (`Spine2 -> LeftShoulder -> LeftArm`) e il retarget fallisce.
- **Bind pose: T-pose esatta.** Non A-pose: sia Mixamo sia `SkeletonProfileHumanoid` di Godot la
  assumono. La deformazione della spalla ne soffre un po', la compatibilita' di pipeline vince.
- **Nessun prefisso `mixamorig:`.** Le animazioni scaricate da Mixamo ce l'hanno: va rimosso in fase di
  import dell'animazione (rinomina delle track), non aggiunto qui.
- Personaggio rivolto verso **-Y** in Blender; la sua sinistra e' **+X**.

### Importare una clip Mixamo

```
python tools/blender/blender_client.py tools/blender/import_mixamo_animation.py <clip.fbx> [nome]
```

Scarica da Mixamo con **Format = FBX Binary, Skin = Without Skin, In Place = attivo**. Lo script
riapre il `.blend` dell'asset (quindi lo stato di Blender non conta), importa, e produce
`assets/models/animations/<nome>.glb` con la sola armature — la mesh sta gia' in `Body_Base.glb`.
In Godot diventa una AnimationLibrary da agganciare a `Body_Base`.

Tre cose che lo script fa e che non sono ovvie:

- **Il prefisso non e' `mixamorig:`.** Sulla clip di prova era `mixamorig10:`: Mixamo ci mette un
  numero. Va tolto con la regex `^mixamorig\d*:`, non con una sostituzione letterale. Si toglie
  **rinominando i bone**, perche' cosi' Blender aggiorna da solo i data path dell'azione.
- **La traslazione del bacino e' nelle unita' di Mixamo.** L'oggetto importato ha scala **0.01** (il
  famoso fattore, che via FBX esiste eccome) e il loro scheletro e' alto 1,96 m contro i nostri 1,78.
  Applicata cosi' com'e' scaglierebbe il personaggio a metri di distanza. Fattore: `0.01 * (1.78/1.96)`.
- **Verifica "in place" bloccante**: se dopo la scalatura il bacino trasla in orizzontale piu' di 25 cm,
  la clip ha root motion e l'export viene saltato. Root motion combatterebbe contro `SyncPosition`
  (CLAUDE.md §3): la posizione la calcola l'host, l'animazione e' solo resa.

I warning `Animation target pose.bones["...Pinky3"] not found` sono attesi: le clip Mixamo animano 52
bone incluse le dita, il nostro rig ne ha 22 deform. Le track in eccesso vengono scartate.

### I roll: la cosa che rompe le animazioni

Il roll definisce l'asse Z locale del bone, cioe' **lo spazio in cui sono espresse le rotazioni di
animazione**. Roll incoerenti torcono gli arti quando si applica una clip esterna.

**Non usare `bpy.ops.armature.calculate_roll(type="GLOBAL_POS_Z")`.** Sui bone quasi verticali (spina,
gambe) l'asse di riferimento e' quasi parallelo all'osso: il calcolo e' mal condizionato. La prima
versione del rig ne era uscita con `Hips 180 / Spine 180 / Spine1 0 / Spine2 0` e `UpLeg 52 / Leg 0` —
ribaltamenti di 180 gradi e salti di 52 gradi a meta' catena.

I roll sono impostati con `edit_bone.align_roll()` verso i target in **`MIXAMO_Z_AXES`**, misurati su
uno scheletro Mixamo reale. **Non indovinarli.** Prima di misurare erano stati tentati due valori
plausibili, entrambi sbagliati:

| Ipotesi per le braccia | Delta da Mixamo |
|---|---|
| Z verso il fronte, come la spina | **84 gradi** |
| Z verso l'alto | **~6 gradi** |
| Z verso il **basso** (misurato) | **0** |

Spina, collo e gambe usano il fronte `(0,-1,0)`; le braccia hanno lo Z rivolto in basso. Per rimisurare
con un'altra clip esiste `tools/blender/measure_mixamo_rolls.py`.

### Come si misura la conformita' (e come NON si misura)

Attenzione a due trappole, entrambe gia' cadute e corrette:

1. **Twist contro bend.** Confrontare direttamente gli assi Z di due bone consecutivi segnalava la
   caviglia a 60 gradi, ma quel valore era *tutta piegatura dell'osso, zero torsione*. Il metodo giusto
   trasporta l'asse Z del padre lungo la rotazione minima verso la direzione del figlio e misura cosa
   resta. Aggiungere un'eccezione per la caviglia avrebbe nascosto il problema invece di misurarlo.
2. **Roll contro proporzioni.** L'angolo grezzo fra il nostro Z e quello di Mixamo mescola il roll (che
   scegliamo) con la direzione dell'osso (che non possiamo scegliere: deve stare dentro la nostra mesh).
   `roll_conformance()` confronta col target **proiettato** sul piano perpendicolare all'osso, quindi
   dice solo "il roll e' ottimo". Attualmente **0,00 gradi su ogni bone**.

Il residuo dovuto alle proporzioni e' riportato a parte e **non e' correggibile col roll**: clavicola
17,4 gradi, spina 9,8, piedi ~6. Deriva dall'avere proporzioni diverse da Mixamo e non ha impedito alla
clip di prova di funzionare. Se un domani desse fastidio, la via e' il retarget di Godot
(`BoneMap` + `SkeletonProfileHumanoid`), che normalizza le rest pose.

Le **inversioni di direzione** (`Hips -> UpLeg`, 177 gradi: il bacino punta in su, il femore in giu')
sono riportate a parte e non misurate: li' il trasporto minimo non e' univoco. Sono anatomiche, le ha
anche Mixamo.

`roll_conformance` e' **bloccante**: insieme al gate di scala, allo skinning e alle UV impedisce
l'export.

---

## 5. Godot

- `assets/models/Body_Base.glb` e' l'unico deliverable importato. Il working file sta in
  `assets/models/source/`, che contiene un **`.gdignore`**: senza, Godot importerebbe anche il `.blend`
  nativamente e ti ritroveresti due asset per lo stesso modello.
- Importato produce: `Skeleton3D` (25 bone), `MeshInstance3D` figlio con `skin` assegnata, una sola
  superficie, scala unitaria, AABB alto 1.78 con base a y=0.
- glTF e' Y-up: l'altezza e' lungo **Y**, non lungo Z come in Blender. Ne tengono conto sia
  `verify_glb.py` sia lo script di verifica Godot.
- `tools/verify_godot_import.gd` e' GDScript: e' uno dei casi di tooling ammessi da CLAUDE.md §2, non
  un'eccezione alla regola del C# per il gameplay.

---

## 6. Cosa NON c'e' (lacune volute)

- **Nessuna texture**, un solo materiale `M_Body_Base` grigio neutro. Il layout UV e' generato con
  `smart_project`: e' garantito non sovrapposto, ma **non e' un layout curato a mano**. Quando inizia il
  texturing va rifatto con seam esplicite.
- **Niente tratti del viso, niente dita** (mani a mitten). In isometrica non si leggono; il volto andra'
  semmai in texture. Coerente con l'assenza di bone delle dita.
- **Nessuna variante M/F.** Un solo `Body_Base` unisex per player e NPC: massimizza il riuso
  dell'equipaggiamento modulare (un solo fitting per pezzo). Corporature diverse per gli NPC si
  otterranno con scale non uniforme sui bone, non con mesh alternative.
- **Nessuna animazione dentro `Body_Base.glb`** (`export_animations=False`): le clip stanno in
  `assets/models/animations/*.glb`, una per file, sola armature. Al momento c'e' solo `Walking`,
  verificata end-to-end (1,067 s, 23 track, tutte su bone esistenti). Il retarget Mixamo funziona
  **drop-in**: nessun `BoneMap`, nessuna infrastruttura, solo rimozione del prefisso e scalatura del
  bacino.
- **Nessun `AnimationTree` ne' collegamento al gameplay.** L'asset e' pronto ma non e' agganciato a
  `player/`: nessuna scena di gioco lo usa ancora. Quando si fara', l'`AnimationTree` gira su ogni peer
  guidato dallo stato replicato (velocita', flag), non da RPC che decidono l'esito: l'animazione e'
  cosmetica, come gli effetti di sparo.
- Non e' uno sculpt anatomico: proporzioni corrette (canone 7.5 teste) e silhouette credibile, senza
  dettaglio muscolare.

## Percorsi e rebuild parziale (aggiornato)

**Niente percorsi assoluti negli script.** Gli script vengono spediti a Blender come sorgente, non
come file: dentro Blender non esiste `__file__`. Prima ogni script conteneva
`PROJECT_DIR = "c:/repositories/lagoon"`, quindi la pipeline girava su una macchina e un sistema
operativo soli. Ora e' **`blender_client.py`** a iniettare, in testa a ogni script, `PROJECT_DIR`
(ricavato da dove sta il client), `LAGOON_PROJECT_DIR` nell'ambiente, `tools/blender` nel `sys.path`
remoto e `ARGV`. Gli script non devono dichiarare percorsi propri.

**`build_animation_library.py` e' ADDITIVO.** Le FBX Mixamo non stanno nel repo (sono grosse e si
riscaricano), quindi la cartella sorgente e' quasi sempre parziale: chi aggiunge due clip ha in mano
quelle due, non tutte. Le clip la cui FBX manca vengono **recuperate reimportando il `.glb` gia'
esportato** — le sue azioni sono state esportate da questa stessa armatura, quindi i data path
puntano gia' ai nostri nomi di osso e si riusano senza ritargeting.

Senza quel recupero, un rebuild con cartella parziale cancellerebbe in silenzio tutto il resto. Il
log riporta `recovered` e `lost`: **`lost` non vuoto significa clip perse per sempre** — fermarsi e
recuperare le FBX prima di committare il `.glb`.

**Trappola del contesto.** `bpy.ops.import_scene.gltf` legge `bpy.context.object` aspettandosi
l'armatura che ha appena creato lui. Va avvolto in `mx.view3d_override()` **senza** passare
`object=`: senza override esplode a meta' (`'Context' object has no attribute 'object'`), con
l'oggetto sbagliato va a rimuovere dalla scena l'armatura di base.

## Clip procedurali (Mixamo non e' piu' una sorgente)

`tools/blender/build_procedural_clips.py` genera le clip che non hanno (e mai avranno) un FBX:
`rifle_aim_idle`, `rifle_lowered_idle`, `pistol_aim_idle`, `pistol_fire`, `land_soft`, `vault_low`.
Rieseguirlo e' idempotente: recupera TUTTE le azioni dal `.glb` (stessa tecnica di
`recover_from_library`), ricrea le procedurali e riesporta l'intera libreria. `lost` non vuoto =
fermarsi, come sempre.

> **Qui si generano solo pose ASSOLUTE.** Le clip **delta additive** del sistema di animazione a
> layer non passano da Blender: vivono in `animation/resources/AdditiveClips.tres` e le genera
> `tools/build_additive_clips.gd` dentro Godot. Non e' una preferenza — la via glTF e' stata
> provata e **non funziona**, vedi la sezione qui sotto.

Le regole che lo tengono robusto:

- **Si campionano pose da clip esistenti, non si animano cicli a mano.** La posa di mira del fucile
  E' `rifle_idle` tale e quale (+respiro): e' la posa su cui sono stati misurati presa e polo IK, e
  campionare altro (provato con `rifle_fire` a meta' colpo) cambia la distanza fra le mani e fa
  flippare il gomito della mano di supporto SOPRA la canna. Il porto basso e' `rifle_idle` con le
  braccia ruotate; l'assorbimento di `land_soft` prende le gambe da `crouch_idle`. Un ciclo di
  passo keyframato a mano e' l'asset piu' fragile che esista: non farlo.
- **Lo script rilassa anche le braccia delle 5 clip `crouch_*`** (il set Mixamo "Crouching" e' in
  guardia da combattimento, sbagliato da disarmati): fcurve delle braccia sostituite con una chiave
  costante da `idle_neutral`. Da armati non cambia nulla (overlay upper-body). Le versioni combat
  originali vivono solo nella storia git del `.glb`.
- **Niente angoli a occhio.** Le rotazioni si costruiscono in spazio MONDO su `pb.matrix`
  (`rotate_bone_world`) o con la rotazione minima fra direzioni misurate (`aim_bone_at`, via
  `rotation_difference`). Il personaggio guarda **-Y** in Blender: rotazione POSITIVA attorno a +X
  = pitch in giu'.
- **Keyframe via `pb.keyframe_insert`, mai fcurve a mano**: con le azioni a slot di Blender 5.x e'
  lui a creare layer/strip/channelbag. `bake_clip` verifica che le fcurve esistano davvero.
- **`sample_pose` richiede `mx.assign_action` + `frame_set`**: senza assegnare azione E slot i pose
  bone restano alla posa precedente, senza errori.
- Le clip procedurali sono elencate in **`PROCEDURAL`** dentro `build_animation_library.py`: e' cio'
  che le fa recuperare dal `.glb` a ogni rebuild Mixamo. Clip procedurale nuova = riga li', builder
  in `build_procedural_clips.py`, categoria di loop in `_verify_loop_modes`, `loop_mode` nel
  `.import` se cicla.
- **Le clip che non appartengono piu' alla libreria vanno scartate ESPLICITAMENTE.** Il recupero le
  ripescherebbe dal `.glb` a ogni rebuild, tenendole in vita per sempre: c'e' un filtro dichiarato
  (oggi `n.startswith("add_")`, residuo del tentativo additivo) e il log riporta quante ne toglie.

## Perche' i delta additivi NON possono passare da glTF

Vale la pena saperlo prima di riprovarci: authorare in Blender clip "delta" (pose espresse come
scarto da un riferimento, per `AnimationNodeAdd2`) e farle viaggiare nel `.glb` **non funziona**, per
due motivi indipendenti, entrambi muti e entrambi misurati.

**1. L'export bakea TUTTE le ossa, non solo quelle con una fcurve.** Con
`export_bake_animation=True` — che serve, perche' senza le azioni non escono — l'esportatore campiona
l'intera armatura a ogni frame. Le ossa che il delta non tocca (bacino, gambe) finiscono nella clip
con la **posa residua** del pose bone, cioe' quello che ci aveva lasciato l'ultima `apply_pose`.
Misurato: due clip di aim offset che dovevano differire solo sul rachide differivano di **0,23 e
0,33 rad sui due femori**.

Corollario generale, valido anche fuori dal caso additivo: **non esiste un modo di esportare una
clip "parziale" via glTF.** Una clip che deve toccare solo una parte del corpo va mascherata a
destinazione (filtro nell'albero) o costruita a destinazione.

**2. Il rest pose del `.glb` della libreria non e' quello di `Body_Base.glb`.** La libreria viene
esportata con un'azione assegnata all'armatura, quindi il TRS dei nodi-osso e' la posa di
quell'azione, non la posa di riposo. Non e' un problema per le clip assolute — le track sono
comunque pose assolute e il rig che le consuma ha il proprio rest — ma lo e' per qualunque cosa
dipenda dalla differenza fra clip e rest. Misurato: una posa authorata come identita' esatta
arrivava a **0,07 rad** dall'identita'.

La divisione che ne discende: **pose assolute in Blender** (richiedono giudizio artistico e il
contesto della mesh), **delta in Godot** (sono aritmetica, e vanno calcolati contro il rest che li
consumera'). Dettagli in `character-animation` §1.6 e §2.1.

## Asset delle armi (frame della presa)

`tools/blender/build_weapon_assets.py` genera `assets/models/weapons/W_Rifle.glb` e `W_Pistol.glb`
(<150 tris l'uno), referenziati da `WeaponDefinition.VisualScene`. Sono costruiti nel **frame della
presa**: origine sull'impugnatura della mano destra, canna lungo il **+Z di Godot** (in Blender si
modella con la canna lungo **-Y** e l'export `export_yup=True` converte), calcio dietro, e l'astina
del fucile ESATTAMENTE a `SupportGripOffset.Z = 0.391` di `two_handed.tres` — valore MISURATO dalla
posa: se cambia la posa va rimisurato, non ritoccato a occhio. Cosi' `WeaponVisual` li aggancia al
`GripPoint` senza offset e un disallineamento presa/posa si vede subito.
