---
name: blender-pipeline
description: Pipeline asset 3D → .glb → Godot. Carica quando tocchi assets/models/ o tools/blender/, quando importi o sostituisci il corpo del personaggio (Mannequin.glb, Universal Animation Library, BoneMap umanoide), converti un'arma del Retro Weapon Pack (VM_*.glb, W_*.glb, build_weapon_viewmodels.py), aggiungi clip, lavori su scala/unità/assi di un modello importato, o parli con Blender via MCP (estensione Blender Lab, porta 9876, blender_execute).
---

# Pipeline asset: pack → Blender → .glb → Godot

Tutti gli asset di `assets/models/` vengono da due pack liberi (vedi `assets/models/LICENSES.md`):

| File | Da | Come |
|---|---|---|
| `characters/Character_Base.glb` | corpo MakeHuman (CC0) sullo scheletro UAL, con le 43 clip | `tools/blender/build_character_base.py` (skill `character-wearables`) |
| `characters/Mannequin.glb` | Universal Animation Library [Standard] (Quaternius, CC0) | copia di `Unreal-Godot/UAL1_Standard.glb`: oggi **solo sorgente** di scheletro e clip per `build_character_base.py`, nessuna scena lo usa |
| `wearables/*.glb`, `wearables/fp/*.glb` | capi indossabili | `tools/blender/wearables/build_wearable.py` (skill `character-wearables`) |
| `weapons/VM_<Arma>.glb`, `weapons/W_<Arma>.glb` | Retro Weapon Pack | `tools/blender/build_weapon_viewmodels.py` |

I pack **non stanno nel repo** (gli FBX delle animazioni pesano fino a 65 MB l'uno): si riscaricano e
si passa il percorso allo script. Chi consuma gli asset: skill `character-animation`.

Fa eccezione `buildings/venice/`: kit modulare generato per intero da `build_venice_kit.py`, senza
pack. Vedi la skill `venice-building-kit`. Anche `boats/` (barchino, gozzo e peschereccio placeholder,
fuoribordo, consolle, accessori) e' procedurale: `tools/blender/build_lagoon_boats.py [preview <cartella>]`,
nomi e convenzioni nella docstring e nella skill `vehicles-boats` §9-§10 (ogni scafo esporta anche la
mesh chiusa `HydroHull` per la fisica). Lo script RESETTA la scena: se l'utente ha Blender aperto con
lavoro suo, eseguirlo in un Blender separato in background (`blender --background --factory-startup
--python <runner>` che definisce `PROJECT_DIR` e `ARGV` come fa `blender_client.py`).

Il **vaporetto** invece parte da un modello scaricato (Sketchfab, CC-BY-4.0, fuori dal repo):
`tools/blender/build_vaporetto.py "<...>/2010_vaporetto/scene.gltf" [noexport]`. Resetta la scena,
rifà scafo/ponti/timoneria/texture, esporta `Vaporetto(_Worn).glb` e `Console_Vaporetto(_Worn).glb` e
lascia in Blender la revisione (copia rovinata a +7 m, piano dell'acqua, cilindro da 1,80 m).
Trappole di Blender 5.2 viste qui: `temp_override(selected_objects=...)` fa fallire l'export glTF
(passare solo finestra/area e selezionare prima); dopo `bpy.data.objects.remove` il view layer ha voci
vuote finche' non si chiama `view_layer.update()`; i poligoni non triangolati li triangola l'export a
modo suo (triangoli degeneri nella `HydroHull`): triangolare le geometrie di ruolo prima.

## 1. Parlare con Blender

Estensione **ufficiale Blender Lab `mcp`** (non `ahujasid/blender-mcp`: protocollo incompatibile, non
installare i pacchetti PyPI `blender-mcp*`). TCP `127.0.0.1:9876`, JSON terminato da null byte:
`{"type":"execute","code":"<python>","strict_json":true}`. **Il codice deve assegnare `result`** (dict
serializzabile). L'I/O su file è permesso.

| Via | Quando |
|---|---|
| `python tools/blender/blender_client.py <script.py> [args]` | script lunghi e ripetibili |
| tool MCP `blender_execute` (`.mcp.json` → `mcp_bridge.py`) | query veloci. Il bridge usa l'API `FastMCP` di **mcp 1.x**: tenere il pin `mcp<2` |

- Il codice gira da un timer, **fuori da ogni area**: gli operatori con `poll()` falliscono (`context is
  incorrect`). Import FBX ed export glTF vanno avvolti in `view3d_override(...)`.
- **Niente percorsi assoluti negli script**: `blender_client.py` inietta `PROJECT_DIR`, `sys.path` e `ARGV`.
- Il client ha un timeout di 600 s: una chiamata per arma, non tutte insieme.
- `bpy.ops.wm.read_homefile(use_empty=True)` e `open_mainfile` funzionano a metà script (l'estensione
  sopravvive). **Dopo aver aperto un `.blend` del pack la scena è in centimetri** (`scale_length`
  0.01): ripartire sempre da una scena vuota in metri prima di importare.

## 2. Il corpo: Character_Base.glb (scheletro e clip di Mannequin.glb)

Il corpo lo genera `build_character_base.py` (skill `character-wearables` §1); scheletro e clip
vengono dal manichino UAL, di cui valgono le regole qui sotto. Nel `.import` (anche di ogni capo
indossato: lo scrive la pipeline dei capi):

```
_subresources={"nodes": {"PATH:Armature/Skeleton3D": {
  "retarget/bone_map": Resource("res://animation/resources/ual_bone_map.tres")}}}
```

- La `BoneMap` (profilo `SkeletonProfileHumanoid`) **rinomina** gli ossi stile Unreal (`pelvis`,
  `spine_01`, `hand_r`…) in `Hips`, `Spine`, `RightHand`…, rinomina lo scheletro `GeneralSkeleton`
  (nome unico: le tracce diventano `%GeneralSkeleton:Hips`) e normalizza la rest pose. Tutto il C#
  usa quei nomi (`HumanoidBones`): **togliere la mappa rompe animazioni e IK in silenzio**.
- È ciò che permette di sostituire il corpo: un modello diverso mappato sullo stesso profilo riusa
  clip e codice. Ossi non mappati (`*_leaf_*`) restano col nome originale.
- Il personaggio guarda **+Z**, sinistra = +X, origine ai piedi, rest in T-pose, alto ~1,8 m.
- Il file senza `_RM` è quello **senza root motion**: è quello giusto, la posizione la decide
  `CharacterMotor`.
- Solo clip in avanti e solo pose da pistola: niente strafe, niente fucile. Come ci si convive è
  nella skill `character-animation` §3. La versione *Pro* della libreria li ha.

## 3. Le armi: build_weapon_viewmodels.py

```
python tools/blender/blender_client.py tools/blender/build_weapon_viewmodels.py \
    "<...>/RetroWeaponPack_V1/Assets/RetroWeaponsPack" rifle
```

Per ogni arma in `WEAPONS` produce:

- `VM_<Arma>.glb` — braccia + arma, **una** libreria di clip che anima entrambe. In Godot diventa
  uno scheletro solo (l'arma è il sottoalbero dell'osso `Gun`).
- `W_<Arma>.glb` — arma statica con i marcatori `Muzzle`, `Grip`, `Support`, `EyeHip`, `EyeAim`,
  misurati sulle pose del pack.

Convenzioni e trappole (tutte già risolte nello script: sono il motivo di come è scritto):

1. **Assi**: nel pack la camera guarda +X; tutto viene ruotato perché guardi +Y di Blender = **−Z di
   Godot**. Il viewmodel si appende alla `Camera3D` a identità e la canna punta a −Z.
2. **Unità**: gli FBX sono in cm con scala oggetto 0,01. Si porta tutto a metri con
   `Armature.transform` / `Mesh.transform` (non servono operatori) e oggetti a identità.
3. **Gli FBX di animazione non hanno mesh**, quindi la loro "rest" è la posa del momento dell'export:
   le azioni importate **non** si possono riassegnare all'armatura con la mesh. Si campiona la posa
   MONDO di ogni osso a ogni frame e la si riscrive come azione sull'armatura finale.
4. **L'arma segue l'osso `hand_item_r`**: nel pack il suo osso radice `Main` coincide con quell'osso
   in ogni posa (verificato sui `.blend`). Le clip dell'arma (`Fire`, `Reload`) animano solo le
   parti mobili. L'aggancio va fatto **a riposo, prima** di scrivere le tracce NLA.
5. **Export `NLA_TRACKS`**: tracce con lo stesso nome su oggetti diversi diventano una sola
   animazione glTF (braccia + arma sincronizzate). L'arma ha una traccia in **ogni** clip, col riposo
   dove non ha animazione propria.
6. **Traccia `RESET` per ultima**: la traccia NLA più in alto decide la posa valutata, e l'export
   cattura bind e riposo dallo stato corrente. Senza, il riposo dell'arma finisce catturato a metà
   di un'altra clip.
7. Nomi delle clip uniformi (`idle`, `walk`, `run`, `idle_aim`, `walk_aim`, `idle_crouch`,
   `walk_crouch`, `fire`, `fire_aim`, `reload`, `draw`, `hide`, `interact`, `aim_start`, `aim_end`,
   `pose_base`, `pose_aim`, più `empty` per la pistola): li fissa `ARM_CLIPS`. Il pack non è uniforme
   nei nomi dei file (`Interact` / `Interaction`).
8. Su Windows il file system non distingue le maiuscole: `W_rifle.glb` sovrascrive `W_Rifle.glb`
   tenendone il nome. Lo script scrive già con l'iniziale maiuscola.

Import in Godot: `gltf/embedded_image_handling=3` (texture incorporate e non compresse, come chiede il
Readme del pack). Filtro *nearest*, roughness 1 e specular 0 li applica `RetroMaterials` all'istanza.

Dopo aver rigenerato un'arma: `Godot --path . --headless --import`, poi rimisurare `AimPitchDegrees`
(skill `character-animation` §4) e riallineare `ReloadSeconds`/`DrawSeconds` alle nuove durate.

## 4. Verifica

Lo script stampa per ogni arma le clip (frame, braccia/arma), i marcatori e il controllo
`gun_local_to_hand_item` (dev'essere una rotazione pura, traslazione zero). In Godot:
`tools/playtest_fps.gd` (skill `character-animation` §8).

## 5. Lacune volute

- Dal pack sono convertiti solo fucile e pistola: SMG e fucile a pompa ci sono ma non sono in `WEAPONS`.
- Gli effetti del pack (`FX/`: vampa, fumo, detriti) e i proiettili non sono importati.
- La mesh delle braccia del pack (`FPS_Arms_Mesh`) e' ancora nei `VM_*.glb` ma a runtime si nasconde:
  al suo posto `ViewmodelOutfit` mette braccia e capi del corpo (skill `character-wearables`). Il pack
  non e' nel repo, quindi i `VM_*.glb` non sono stati rigenerati.
