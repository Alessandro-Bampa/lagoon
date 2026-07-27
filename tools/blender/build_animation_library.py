"""Costruisce la libreria di animazioni UNICA del personaggio.

Importa tutte le clip Mixamo di una cartella e le esporta in un solo
`assets/models/animations/CharacterAnimations.glb`, che Godot importa come una
sola `AnimationLibrary`.

Perche' una libreria unica e non un .glb per clip: con file separati ogni clip
diventa una libreria a se' e nell'AnimationTree andrebbe referenziata come
`walk_fwd/walk_fwd`. Con la libreria unica il nome e' semplicemente `walk_fwd`,
e aggiungere una clip non aggiunge una voce in `AnimationPlayer.libraries`.

Uso:
    python tools/blender/blender_client.py tools/blender/build_animation_library.py <cartella_fbx>

La mappa nome-file -> nome-clip sta in CLIPS: e' la CONVENZIONE DI NAMING del
progetto. I nomi dei file Mixamo sono in inglese naturale ("Left Strafe Walking"),
i nomi delle clip sono tecnici e brevi ("walk_left"). Aggiungendo una clip nuova,
aggiungi la riga qui.
"""

import importlib
import os
import sys

import bpy

sys.path.append("c:/repositories/lagoon/tools/blender")
import mixamo_common as mx  # noqa: E402

# Blender resta aperto fra un'esecuzione e l'altra e tiene i moduli in sys.modules: senza
# reload, una modifica a mixamo_common.py verrebbe IGNORATA e si continuerebbe a girare
# con la versione vecchia, senza il minimo segnale.
importlib.reload(mx)

# Nome del file FBX (senza estensione) -> nome della clip nella libreria.
CLIPS = {
    # --- pose upper-body (layer arma) ---
    "Rifle Idle": "rifle_idle",
    "Pistol Idle": "pistol_idle",
    "Firing Rifle": "rifle_fire",
    # Idle NEUTRA disarmata: e' il centro dello spazio di camminata. Prima quel posto
    # lo teneva rifle_idle, quindi da disarmato si stava in posa "reggi fucile".
    "Breathing Idle": "idle_neutral",
    # --- camminata: 4 assi ---
    "Walking": "walk_fwd",
    "Walking Backwards": "walk_back",
    "Left Strafe Walking": "walk_left",
    "Right Strafe Walking": "walk_right",
    # --- corsa: 4 assi. Attenzione ai nomi Mixamo, "Left Strafe" e' la CORSA,
    #     "Left Strafe Walking" e' la camminata. ---
    "Running": "run_fwd",
    "Running Backward": "run_back",
    "Left Strafe": "run_left",
    "Right Strafe": "run_right",
    # --- accovacciato: 4 assi + idle. Tutte dallo STESSO set: mischiare famiglie
    #     diverse cambia l'altezza dell'accovacciamento fra un punto di blend e
    #     l'altro, e nelle direzioni intermedie il bacino scatta. Per questo la
    #     vecchia "Crouched Walking" non e' piu' usata. ---
    "Idle Crouching": "crouch_idle",
    "Walk Crouching Forward": "crouch_fwd",
    "Crouch Walking Backwards": "crouch_back",
    "Walk Crouching Left": "crouch_left",
    "Walk Crouching Right": "crouch_right",
    # --- aria ---
    "Jump": "jump",
    "Falling Idle": "fall_idle",
    "Hard Landing": "land_hard",
}

# Clip da APPIATTIRE invece che scartare quando portano root motion.
#
# Ci si mette solo cio' che su Mixamo non offre la spunta *In Place* ma che serve lo
# stesso. "Hard Landing" avanza di 34 cm perche' l'atterraggio e' sbilanciato: e' una
# posa, non uno spostamento voluto, e la posizione la decide comunque PlayerController
# (CLAUDE.md §3). Vedi mx.flatten_root_motion per i limiti.
FORCE_IN_PLACE = {"land_hard"}

OUT_PATH = mx.ANIM_DIR + "/CharacterAnimations.glb"

source_dir = ARGV[0].replace("\\", "/")
log = {"source": source_dir, "clips": [], "skipped": [], "flattened": [], "missing": []}

bpy.ops.wm.open_mainfile(filepath=mx.BLEND_PATH)
ours = bpy.data.objects[mx.ARMATURE_NAME]

actions = []
for file_stem, clip_name in sorted(CLIPS.items(), key=lambda kv: kv[1]):
    fbx = "{}/{}.fbx".format(source_dir, file_stem)
    if not os.path.exists(fbx):
        log["missing"].append(file_stem)
        continue

    action, imported, info = mx.import_clip(fbx, clip_name)
    mx.discard(imported)

    if not info["in_place"]:
        # Root motion: combatterebbe contro SyncPosition (CLAUDE.md §3), la posizione
        # la calcola il controller di movimento, non l'animazione.
        if clip_name in FORCE_IN_PLACE:
            removed = mx.flatten_root_motion(action)
            log["flattened"].append({"clip": clip_name,
                                     "horizontal_m": info["horizontal_span_m"],
                                     "rimosso": removed})
        else:
            log["skipped"].append({"clip": clip_name,
                                   "horizontal_m": info["horizontal_span_m"]})
            continue

    actions.append(action)
    log["clips"].append({
        "name": clip_name,
        "frames": info["frame_range"],
        "horizontal_m": info["horizontal_span_m"],
        "vertical_m": info["vertical_span_m"],
    })

# Tutte le azioni vanno esportate insieme: l'esportatore glTF prende quelle presenti
# nel file (use_fake_user le tiene in vita) quando export_animation_mode = "ACTIONS".
if actions:
    mx.assign_action(ours, actions[0])

for obj in bpy.context.view_layer.objects:
    obj.select_set(False)
ours.select_set(True)
bpy.context.view_layer.objects.active = ours

os.makedirs(mx.ANIM_DIR, exist_ok=True)
with mx.view3d_override(object=ours, active_object=ours, selected_objects=[ours],
                        selected_editable_objects=[ours]):
    bpy.ops.export_scene.gltf(
        filepath=OUT_PATH,
        export_format="GLB",
        use_selection=True,
        export_yup=True,
        export_skins=True,
        export_animations=True,
        export_animation_mode="ACTIONS",
        export_bake_animation=True,
        export_materials="NONE",
        export_cameras=False,
        export_lights=False,
    )

log["output"] = {"glb": OUT_PATH, "bytes": os.path.getsize(OUT_PATH), "count": len(actions)}
result = log
