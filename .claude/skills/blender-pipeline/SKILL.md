---
name: blender-pipeline
description: Pipeline asset 3D Blender → .glb → Godot. Carica quando tocchi assets/models/ o tools/blender/, Body_Base o Armature_Character, quando generi o rigeneri un personaggio, un'arma o una clip procedurale, importi animazioni Mixamo, lavori su scala/unità di un modello importato, o parli con Blender via MCP (estensione Blender Lab, porta 9876, blender_execute).
---

# Pipeline asset: Blender → .glb → Godot

Produce asset veri solo per `assets/models/`: `Body_Base.glb` (corpo unico per giocatori e NPC),
`animations/CharacterAnimations.glb`, `weapons/W_*.glb`. Il resto del gioco resta a placeholder.

## 1. Parlare con Blender

Estensione **ufficiale Blender Lab `mcp`** (non `ahujasid/blender-mcp`: protocollo incompatibile, non
installare i pacchetti PyPI `blender-mcp*`). TCP `127.0.0.1:9876`, JSON terminato da null byte:
`{"type":"execute","code":"<python>","strict_json":true}`. **Il codice deve assegnare `result`** (dict
serializzabile). L'I/O su file è permesso.

| Via | Quando |
|---|---|
| `python tools/blender/blender_client.py <script.py> [args]` | script lunghi e ripetibili (è così che si costruisce tutto) |
| tool MCP `blender_execute` (`.mcp.json` → `mcp_bridge.py`) | query veloci. Il bridge usa l'API `FastMCP` di **mcp 1.x**: tenere il pin `mcp<2` |

- Il codice gira da un timer, **fuori da ogni area**: gli operatori con `poll()` falliscono (`context is
  incorrect`). Preferisci `bmesh`/depsgraph; dove serve `bpy.ops` (unwrap, pesi, render, **export e import
  glTF**) avvolgi in `view3d_override(...)`. Per `import_scene.gltf` **senza** passare `object=`.
- **Niente percorsi assoluti negli script**: `blender_client.py` inietta `PROJECT_DIR`, `sys.path` e `ARGV`.
- Blender tiene i moduli in `sys.modules`: dopo aver toccato `mixamo_common.py` serve `importlib.reload`.

## 2. Il corpo

`tools/blender/build_character.py` è la **sorgente di verità**, idempotente. Mai modificare il `.blend`.

```
python tools/blender/blender_client.py tools/blender/build_character.py
python tools/blender/verify_glb.py
Godot --path . --headless --script tools/verify_godot_import.gd
```

Mesh = Skin modifier su un grafo parametrico (`SKIN_NODES`) + Catmull-Clark 2. Il nodo `chest` a 4 rami
fa scalini sulla spalla se i raggi vicini divergono: **guarda i render** dopo ogni ritocco. La
subdivision restringe il volume: `fit_transform()` corregge e si applica **anche alla tabella dei bone**.

Invarianti imposti da `stage_scale_gate()` (non aggirarlo): `scale_length = 1`, scala oggetto `(1,1,1)`,
origine a terra, altezza 1.78 m, 6–10k tris tutti quad, ≤ 4 influenze per vertice, UV non sovrapposte,
twist fra bone < 20°. Il "fattore 0.01" è un problema dell'**FBX**, non del glTF.

In Godot: `Skeleton3D` a 25 bone + una `MeshInstance3D` skinnata con **una sola superficie** (per questo
in prima persona la testa si nasconde sdoppiando la mesh: skill `character-animation` §5). `assets/models/source/` ha un `.gdignore`.

## 3. Il rig: nomi Mixamo, generato in Blender

Non passa dall'auto-rigger Mixamo (upload manuale, scala 0.01, pesi oltre le 4 influenze). Se ne
conservano **nomi e gerarchia**, così le clip restano applicabili drop-in:

```
Hips ─ Spine ─ Spine1 ─ Spine2 ─┬─ Neck ─ Head ─ HeadTop_End
                                └─ L/R Shoulder ─ Arm ─ ForeArm ─ Hand
Hips ─ L/R UpLeg ─ Leg ─ Foot ─ ToeBase ─ Toe_End
```

- Clavicole obbligatorie. Bind pose in **T-pose** esatta. Nessun prefisso `mixamorig:`.
- Il personaggio guarda **−Y** in Blender (→ **+Z** in Godot), sinistra = +X.
- **Roll**: mai `calculate_roll(GLOBAL_POS_Z)` (mal condizionato sui bone verticali). Si usa
  `align_roll()` verso `MIXAMO_Z_AXES`, **misurati** (`measure_mixamo_rolls.py`): fronte per spina, collo
  e gambe; **in basso** per le braccia. `roll_conformance()` è bloccante e confronta col target
  proiettato sul piano perpendicolare all'osso; il twist si misura col trasporto minimo, non
  confrontando gli assi Z grezzi.

## 4. Clip

Tutte in `CharacterAnimations.glb`. **Solo pose assolute**: delta additivi, pose d'impugnatura e crouch
armato si calcolano in Godot (skill `character-animation`). Via glTF non si può esportare una clip
"parziale": l'export bakea tutte le ossa.

**Mixamo** (FBX Binary, Without Skin, In Place): riga in `CLIPS` di `build_animation_library.py` →
rigenera → `loop_mode` nel `.import` → categoria in `_verify_loop_modes` → albero → le due suite.
- Il prefisso è `mixamorig\d*:` (regex) e si toglie **rinominando i bone**.
- La traslazione del bacino va scalata `0.01 × (1.78/1.96)`.
- Controllo "in place" bloccante (> 25 cm = root motion: combatterebbe con `SyncPosition`).
- **Le FBX non stanno nel repo**: il rebuild è **additivo** e recupera le clip mancanti dal `.glb` già
  esportato. Il log riporta `recovered` e `lost`: **`lost` non vuoto = clip perse per sempre, fermarsi**.

**Procedurali** (`build_procedural_clips.py`, elencate in `PROCEDURAL`): `land_soft`, `vault_low`,
`mantle_high`, `pistol_fire`, più tre `rifle_*_idle`/`pistol_aim_idle` che l'albero non usa più.
- Si **campionano pose da clip esistenti** (`sample_pose` richiede `assign_action` + `frame_set`), non
  si animano cicli a mano.
- Rotazioni in spazio mondo (`rotate_bone_world`, `aim_bone_at`); keyframe via `pb.keyframe_insert`.
- Una clip rimossa va scartata **esplicitamente**, o il recupero la ripesca a ogni rebuild.

## 5. Armi

`build_weapon_assets.py` → `W_Rifle.glb`, `W_Pistol.glb` (< 150 tris), nel **frame della presa**: origine
sull'impugnatura, canna lungo **+Z di Godot** (in Blender −Y), astina a `SUPPORT_Z` =
`SupportGripOffset.Z` di `two_handed.tres`. **Da riallineare**: lo script ha 0.391, il `.tres` oggi 0.254.

## 6. Lacune volute

Nessuna texture (UV da `smart_project`, da rifare con seam vere), niente viso né dita, un solo corpo
unisex (corporature diverse = scale sui bone), nessuno sculpt anatomico.
