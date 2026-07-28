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

import bpy

# `tools/blender` e' gia' nel sys.path remoto: lo mette blender_client.py insieme a
# PROJECT_DIR. Prima qui c'era un percorso assoluto Windows, e la pipeline girava su
# una macchina sola.
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
    # --- locomozione ARMATA: 4 assi per camminata e corsa ---
    #
    # Non e' un lusso. Il layer arma era un override del solo upper body sopra le clip
    # disarmate: il torso restava una posa FISSA mentre le gambe strafavano, e la posa
    # "reggi fucile" era authored su un bacino neutro mentre lo strafe il bacino lo ruota.
    # Un set di stance completo e' come lo risolvono gli sparatutto veri.
    #
    # Vale solo per le armi a DUE MANI: la pistola resta sul set disarmato con la posa
    # upper-body, che per un'arma tenuta bassa e' corretto e costa zero clip.
    "Rifle Walk": "rifle_walk_fwd",
    "Backwards Rifle Walk": "rifle_walk_back",
    "Strafe Left": "rifle_walk_left",
    "Strafe Right": "rifle_walk_right",
    "Rifle Run": "rifle_run_fwd",
    "Backwards Rifle Run": "rifle_run_back",
    "Run Left": "rifle_run_left",
    "Run Right": "rifle_run_right",
}

# Clip da APPIATTIRE invece che scartare quando portano root motion.
#
# Ci si mette solo cio' che su Mixamo non offre la spunta *In Place* ma che serve lo
# stesso. "Hard Landing" avanza di 34 cm perche' l'atterraggio e' sbilanciato: e' una
# posa, non uno spostamento voluto, e la posizione la decide comunque PlayerController
# (CLAUDE.md §3). Vedi mx.flatten_root_motion per i limiti.
FORCE_IN_PLACE = {"land_hard"}

# Clip PROCEDURALI: non hanno un FBX e non l'avranno mai, per costruzione — le genera
# tools/blender/build_procedural_clips.py con keyframe da codice. Sono diverse dalle
# clip "con FBX fuori repo" (che una FBX ce l'hanno, solo non versionata): queste vanno
# SEMPRE recuperate dal .glb precedente, altrimenti un rebuild Mixamo le cancellerebbe
# in silenzio. Aggiungendo una clip procedurale, aggiungila anche qui.
PROCEDURAL = {"rifle_aim_idle", "rifle_lowered_idle", "pistol_aim_idle",
              "pistol_fire", "land_soft"}

OUT_PATH = mx.ANIM_DIR + "/CharacterAnimations.glb"


def recover_from_library(names):
    """Recupera dalla libreria gia' esportata le azioni la cui FBX non c'e' piu'.

    Le FBX Mixamo non stanno nel repo (sono grosse e si riscaricano), quindi la cartella
    sorgente e' quasi sempre PARZIALE: chi aggiunge due clip ha in mano quelle due, non
    tutte e ventotto. Senza questo recupero un rebuild "additivo" cancellerebbe in
    silenzio tutto cio' che non ha piu' un FBX accanto — che e' esattamente il modo in cui
    si perde una libreria intera senza un solo errore.

    Le azioni dentro il .glb sono state esportate da QUESTA armatura, quindi i loro data
    path puntano gia' ai nostri nomi di osso e si possono riusare senza ritargeting.
    """
    if not names or not os.path.exists(OUT_PATH):
        return {}, []

    before = set(bpy.data.actions.keys())
    before_objects = set(bpy.context.scene.objects)

    # Serve un'area VIEW_3D, ma NON si deve forzare l'oggetto attivo: l'importatore glTF
    # legge bpy.context.object aspettandosi l'armatura che ha appena creato lui. Dargli la
    # nostra lo manda a rimuovere dalla scena l'oggetto sbagliato.
    with mx.view3d_override():
        bpy.ops.import_scene.gltf(filepath=OUT_PATH)

    imported = [o for o in bpy.context.scene.objects if o not in before_objects]

    # L'importatore glTF puo' anteporre il nome dell'oggetto ("Armature|walk_fwd"):
    # si indicizza per suffisso, non per uguaglianza.
    fresh = {}
    for key in bpy.data.actions.keys():
        if key in before:
            continue
        action = bpy.data.actions[key]
        fresh[key.split("|")[-1]] = action

    recovered = {}
    still_missing = []
    for name in names:
        action = fresh.get(name)
        if action is None:
            still_missing.append(name)
            continue
        action.name = name
        action.use_fake_user = True
        recovered[name] = action

    mx.discard(imported)
    return recovered, still_missing


source_dir = ARGV[0].replace("\\", "/")
log = {"source": source_dir, "clips": [], "skipped": [], "flattened": [],
       "missing": [], "recovered": [], "lost": []}

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

# Cio' che non aveva una FBX si recupera dalla libreria precedente, cosi' aggiungere due
# clip non ne cancella ventisei. Le procedurali si recuperano SEMPRE da li': un FBX loro
# non esiste.
missing_clips = [CLIPS[stem] for stem in log["missing"]] + sorted(PROCEDURAL)
recovered, lost = recover_from_library(missing_clips)
log["recovered"] = sorted(recovered.keys())
log["lost"] = sorted(lost)
actions.extend(recovered[name] for name in sorted(recovered))

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
