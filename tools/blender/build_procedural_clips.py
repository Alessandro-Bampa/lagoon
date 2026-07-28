"""Genera le clip PROCEDURALI della libreria: pose costruite da codice, senza FBX.

Mixamo non e' piu' una sorgente disponibile: le clip nuove si costruiscono qui,
campionando un fotogramma di una clip gia' esportata e modificandolo con rotazioni
CALCOLATE (mai angoli a occhio: si misura la direzione attuale dell'osso e si ruota
verso quella voluta con `rotation_difference`).

Clip prodotte (vedi PROCEDURAL_CLIPS in fondo):
  rifle_aim_idle     mira col fucile: E' la posa di rifle_idle + respiro sul torace.
                     DELIBERATO: rifle_idle e' la ready-stance su cui sono stati
                     MISURATI GripRotationDegrees, SupportGripOffset e il polo del
                     gomito. Campionare un'altra clip (provato con rifle_fire a meta'
                     colpo) cambia la distanza fra le mani e il polo IK flippa: il
                     gomito della mano di supporto finisce SOPRA la canna. Loop.
  rifle_lowered_idle porto rilassato ("low ready"): rifle_idle con le braccia ruotate
                     in basso. Loop.
  pistol_aim_idle    mira a una mano: pistol_idle col braccio destro esteso davanti.
                     Loop.
  pistol_fire        colpo di pistola: jolt breve sul braccio, poi rientro. No loop.
  land_soft          atterraggio morbido: caduta -> assorbimento (gambe da
                     crouch_idle) -> idle. No loop.

Inoltre RILASSA le braccia delle 5 clip crouch_*: il set Mixamo "Crouching" e' in
posa da combattimento (braccia alzate come se si mirasse), che da DISARMATI e'
sbagliato. Le braccia vengono sostituite con la posa di idle_neutral; da ARMATI non
cambia nulla, perche' l'overlay upper-body copre comunque le braccia. Le versioni
"combat" originali restano solo nella storia git del .glb.

Il flusso e' lo stesso di build_animation_library.py: si riapre Body_Base.blend, si
recuperano TUTTE le azioni esistenti dal .glb (sono state esportate da questa stessa
armatura, i data path combaciano), si aggiungono le procedurali e si riesporta tutto
insieme. `lost` non vuoto nel log = clip perse: fermarsi, non committare.

Uso:
    python tools/blender/blender_client.py tools/blender/build_procedural_clips.py
"""

import importlib
import math
import os

import bpy
from mathutils import Matrix, Quaternion, Vector

import mixamo_common as mx

# Blender tiene i moduli in sys.modules fra un'esecuzione e l'altra: senza reload una
# modifica a mixamo_common.py verrebbe ignorata in silenzio.
importlib.reload(mx)

OUT_PATH = mx.ANIM_DIR + "/CharacterAnimations.glb"

# Ossa da NON keyframare: le foglie *_End non sono animate nemmeno nelle clip Mixamo.
SKIP_BONES = {"HeadTop_End", "LeftToe_End", "RightToe_End"}


# ============================================================================
#  Recupero della libreria esistente (stessa tecnica di build_animation_library)
# ============================================================================

def recover_all_actions():
    """Importa il .glb della libreria e restituisce {nome_clip: action}.

    L'importatore glTF legge bpy.context.object aspettandosi l'armatura appena creata:
    serve view3d_override SENZA passare object=, altrimenti rimuove l'oggetto sbagliato.
    I nomi possono arrivare come "Armature|walk_fwd": si indicizza per suffisso.
    """
    if not os.path.exists(OUT_PATH):
        return {}

    before_actions = set(bpy.data.actions.keys())
    before_objects = set(bpy.context.scene.objects)

    with mx.view3d_override():
        bpy.ops.import_scene.gltf(filepath=OUT_PATH)

    imported = [o for o in bpy.context.scene.objects if o not in before_objects]

    recovered = {}
    for key in list(bpy.data.actions.keys()):
        if key in before_actions:
            continue
        action = bpy.data.actions[key]
        name = key.split("|")[-1]
        action.name = name
        action.use_fake_user = True
        recovered[name] = action

    mx.discard(imported)
    return recovered


# ============================================================================
#  Campionamento e cottura delle pose
# ============================================================================

def sample_pose(ours, action, frame):
    """Posa di `action` al fotogramma dato: {bone: {"quat": Quaternion, "loc": Vector}}.

    frame_set valuta l'azione assegnata e aggiorna i pose bone; la location si tiene
    solo per Hips (le altre ossa non traslano, come nelle clip Mixamo).
    """
    mx.assign_action(ours, action)
    bpy.context.scene.frame_set(frame)
    bpy.context.view_layer.update()

    pose = {}
    for pb in ours.pose.bones:
        if pb.name in SKIP_BONES:
            continue
        pb.rotation_mode = "QUATERNION"
        pose[pb.name] = {
            "quat": pb.rotation_quaternion.copy(),
            "loc": pb.location.copy() if pb.name == "Hips" else None,
        }
    return pose


def apply_pose(ours, pose):
    """Scrive una posa sui pose bone (senza keyframe)."""
    for name, data in pose.items():
        pb = ours.pose.bones[name]
        pb.rotation_mode = "QUATERNION"
        pb.rotation_quaternion = data["quat"].copy()
        if data["loc"] is not None:
            pb.location = data["loc"].copy()
    bpy.context.view_layer.update()


def snapshot_pose(ours):
    """Rilegge la posa corrente dai pose bone (dopo modifiche via pb.matrix)."""
    pose = {}
    for pb in ours.pose.bones:
        if pb.name in SKIP_BONES:
            continue
        pose[pb.name] = {
            "quat": pb.rotation_quaternion.copy(),
            "loc": pb.location.copy() if pb.name == "Hips" else None,
        }
    return pose


def rotate_bone_world(ours, bone_name, axis_world, degrees):
    """Ruota un pose bone attorno a un asse in coordinate MONDO, perno sulla testa.

    L'armatura ha trasformata identita' (gate di build_character), quindi lo spazio
    armatura E' il mondo. I figli seguono da soli; view_layer.update() dopo ogni
    modifica, perche' pb.matrix dei figli e' valida solo a depsgraph aggiornato.
    """
    pb = ours.pose.bones[bone_name]
    rot = Matrix.Rotation(math.radians(degrees), 4, axis_world.normalized())
    head = pb.matrix.to_translation()
    pb.matrix = Matrix.Translation(head) @ rot @ Matrix.Translation(-head) @ pb.matrix
    bpy.context.view_layer.update()


def aim_bone_at(ours, bone_name, target_dir):
    """Ruota un pose bone in modo che punti (testa->coda) nella direzione voluta.

    Niente angoli a occhio: si misura la direzione attuale e si applica la rotazione
    minima che la porta su quella voluta (rotation_difference). Il roll non cambia.
    """
    pb = ours.pose.bones[bone_name]
    current = (pb.tail - pb.head).normalized()
    swing = current.rotation_difference(target_dir.normalized())
    head = pb.matrix.to_translation()
    pb.matrix = (Matrix.Translation(head) @ swing.to_matrix().to_4x4()
                 @ Matrix.Translation(-head) @ pb.matrix)
    bpy.context.view_layer.update()


def blend_pose(a, b, t):
    """Interpolazione fra due pose (slerp sui quaternioni, lerp sulla location)."""
    out = {}
    for name in a:
        qa, qb = a[name]["quat"], b[name]["quat"]
        loc = None
        if a[name]["loc"] is not None and b[name]["loc"] is not None:
            loc = a[name]["loc"].lerp(b[name]["loc"], t)
        out[name] = {"quat": qa.slerp(qb, t), "loc": loc}
    return out


def merge_pose(base, donor, bones):
    """Posa `base` con le ossa elencate prese da `donor` (es. gambe da crouch_idle)."""
    out = {}
    for name in base:
        src = donor if name in bones else base
        out[name] = {"quat": src[name]["quat"].copy(),
                     "loc": src[name]["loc"].copy() if src[name]["loc"] is not None else None}
    return out


def bake_clip(ours, name, keyframes):
    """Crea un'azione nuova da {frame: pose} e la lascia in bpy.data con fake user.

    keyframe_insert crea da solo layer/strip/channelbag (azioni a slot di Blender 5.x):
    non si toccano le fcurve a mano.
    """
    if name in bpy.data.actions:
        bpy.data.actions.remove(bpy.data.actions[name])

    action = bpy.data.actions.new(name)
    action.use_fake_user = True
    animation_data = ours.animation_data_create()
    animation_data.action = action

    for frame in sorted(keyframes):
        apply_pose(ours, keyframes[frame])
        for pb_name in keyframes[frame]:
            pb = ours.pose.bones[pb_name]
            pb.keyframe_insert("rotation_quaternion", frame=frame)
            if pb_name == "Hips":
                pb.keyframe_insert("location", frame=frame)

    curve_count = len(mx.action_fcurves(action))
    if curve_count == 0:
        raise RuntimeError("bake_clip('%s'): nessuna fcurve creata" % name)
    return action, curve_count


ARM_BONES = ["LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand",
             "RightShoulder", "RightArm", "RightForeArm", "RightHand"]


def strip_bone_curves(action, bones):
    """Rimuove dalle azioni (a slot, Blender 5.x) le fcurve dei bone elencati."""
    removed = 0
    for layer in action.layers:
        for strip in layer.strips:
            for bag in strip.channelbags:
                doomed = [fc for fc in bag.fcurves
                          if any('"%s"' % b in fc.data_path for b in bones)]
                for fc in doomed:
                    bag.fcurves.remove(fc)
                    removed += 1
    return removed


def relax_arms(ours, action, relaxed_pose):
    """Sostituisce le braccia di una clip con una posa rilassata COSTANTE.

    Si tolgono le fcurve dei bone delle braccia e si mette una sola chiave al primo
    fotogramma: le gambe e la spina restano quelle della clip, le braccia smettono di
    "mirare". Idempotente: rieseguito, toglie la chiave costante e la rimette.
    """
    removed = strip_bone_curves(action, ARM_BONES)
    mx.assign_action(ours, action)
    for name in ARM_BONES:
        pb = ours.pose.bones[name]
        pb.rotation_mode = "QUATERNION"
        pb.rotation_quaternion = relaxed_pose[name]["quat"].copy()
        pb.keyframe_insert("rotation_quaternion", frame=int(action.frame_range[0]))
    return removed


def hand_probe(ours, pose):
    """Diagnostica: quota delle mani e direzione destra->sinistra, per capire la posa."""
    apply_pose(ours, pose)
    right = ours.pose.bones["RightHand"]
    left = ours.pose.bones["LeftHand"]
    axis = (left.head - right.head)
    return {
        "right_hand_z": round(right.head.z, 3),
        "left_hand_z": round(left.head.z, 3),
        "hands_axis": [round(v, 3) for v in axis.normalized()] if axis.length > 1e-4 else None,
        "hands_gap_m": round(axis.length, 3),
    }


def breathing(base, ours, fps, seconds=3.0, degrees=1.5):
    """Keyframe di respiro: il torace oscilla di +-degrees attorno all'asse laterale.

    Il personaggio guarda -Y (Blender), quindi l'asse laterale e' X. Primo e ultimo
    fotogramma identici alla base: il loop non salta.
    """
    length = max(int(round(seconds * fps)), 8)
    quarter = length // 4

    def swayed(sign):
        apply_pose(ours, base)
        rotate_bone_world(ours, "Spine2", Vector((1, 0, 0)), sign * degrees)
        return snapshot_pose(ours)

    return {
        0: base,
        quarter: swayed(+1),
        2 * quarter: base,
        3 * quarter: swayed(-1),
        length: base,
    }


# ============================================================================
#  Costruzione
# ============================================================================

log = {"created": [], "recovered": [], "lost": [], "probes": {}}

bpy.ops.wm.open_mainfile(filepath=mx.BLEND_PATH)
ours = bpy.data.objects[mx.ARMATURE_NAME]
fps = bpy.context.scene.render.fps

library = recover_all_actions()
log["recovered"] = sorted(library.keys())

REQUIRED_SOURCES = ["rifle_idle", "rifle_fire", "pistol_idle",
                    "fall_idle", "crouch_idle", "idle_neutral"]
missing_sources = [n for n in REQUIRED_SOURCES if n not in library]
if missing_sources:
    raise RuntimeError("clip sorgente mancanti nel .glb: %s" % missing_sources)

FORWARD = Vector((0, -1, 0))    # il personaggio guarda -Y in Blender
LATERAL = Vector((1, 0, 0))     # la sua sinistra e' +X

# --- pose sorgente -----------------------------------------------------------
rifle_idle = sample_pose(ours, library["rifle_idle"], 1)
# La posa di mira E' rifle_idle (vedi docstring: presa e polo IK sono misurati li').
# L'inseguimento del bersaglio non sta nella clip: lo fa SpineAimModifier.
rifle_aim = rifle_idle
pistol_idle = sample_pose(ours, library["pistol_idle"], 1)
fall_end = sample_pose(ours, library["fall_idle"],
                       int(library["fall_idle"].frame_range[1]) - 1)
crouch_idle = sample_pose(ours, library["crouch_idle"], 1)
idle_neutral = sample_pose(ours, library["idle_neutral"], 1)

# --- rifle_aim_idle ----------------------------------------------------------
log["probes"]["rifle_aim_idle"] = hand_probe(ours, rifle_aim)
rifle_aim_clip = breathing(rifle_aim, ours, fps)
_, n = bake_clip(ours, "rifle_aim_idle", rifle_aim_clip)
log["created"].append({"clip": "rifle_aim_idle", "fcurves": n, "loop": True})

# --- rifle_lowered_idle ------------------------------------------------------
# Porto rilassato: dalle spalle in giu' le braccia ruotano verso terra. Rotazione
# POSITIVA attorno a +X porta -Y (avanti) verso -Z (basso): e' il "pitch in giu'".
apply_pose(ours, rifle_idle)
for side in ("Right", "Left"):
    rotate_bone_world(ours, side + "Arm", LATERAL, 35.0)
    rotate_bone_world(ours, side + "ForeArm", LATERAL, 15.0)
rifle_lowered = snapshot_pose(ours)
log["probes"]["rifle_lowered_idle"] = hand_probe(ours, rifle_lowered)
_, n = bake_clip(ours, "rifle_lowered_idle", breathing(rifle_lowered, ours, fps))
log["created"].append({"clip": "rifle_lowered_idle", "fcurves": n, "loop": True})

# --- pistol_aim_idle ---------------------------------------------------------
# Mira a una mano: braccio destro esteso orizzontale davanti, leggera rotazione del
# torace verso il bersaglio. Le direzioni si MISURANO e si correggono con la
# rotazione minima, non si applicano angoli fissi.
apply_pose(ours, pistol_idle)
rotate_bone_world(ours, "Spine2", Vector((0, 0, 1)), -8.0)  # spalla destra avanti
aim_dir = (FORWARD * 0.97 + Vector((0, 0, 1)) * 0.05).normalized()
aim_bone_at(ours, "RightArm", aim_dir)
aim_bone_at(ours, "RightForeArm", aim_dir)
aim_bone_at(ours, "RightHand", aim_dir)
pistol_aim = snapshot_pose(ours)
log["probes"]["pistol_aim_idle"] = hand_probe(ours, pistol_aim)
_, n = bake_clip(ours, "pistol_aim_idle", breathing(pistol_aim, ours, fps))
log["created"].append({"clip": "pistol_aim_idle", "fcurves": n, "loop": True})

# --- pistol_fire -------------------------------------------------------------
# Jolt di rinculo: il braccio si alza di qualche grado e il torace arretra, poi tutto
# rientra sulla posa di mira. Rotazione NEGATIVA attorno a +X = muso in ALTO.
apply_pose(ours, pistol_aim)
rotate_bone_world(ours, "RightArm", LATERAL, -6.0)
rotate_bone_world(ours, "RightForeArm", LATERAL, -5.0)
rotate_bone_world(ours, "Spine2", LATERAL, -2.0)
pistol_kick = snapshot_pose(ours)
kick_frame = max(int(round(0.06 * fps)), 1)
end_frame = max(int(round(0.35 * fps)), kick_frame + 2)
_, n = bake_clip(ours, "pistol_fire", {
    0: pistol_aim,
    kick_frame: pistol_kick,
    end_frame: pistol_aim,
})
log["created"].append({"clip": "pistol_fire", "fcurves": n, "loop": False})

# --- land_soft ---------------------------------------------------------------
# Caduta -> assorbimento -> idle. L'assorbimento prende le GAMBE (e il bacino, quota
# compresa) da crouch_idle e tiene il busto della caduta portato leggermente avanti:
# e' la flessione che il dip procedurale solo abbozza.
absorb = merge_pose(fall_end, crouch_idle,
                    {"Hips", "LeftUpLeg", "LeftLeg", "LeftFoot", "LeftToeBase",
                     "RightUpLeg", "RightLeg", "RightFoot", "RightToeBase"})
apply_pose(ours, absorb)
rotate_bone_world(ours, "Spine", LATERAL, 10.0)   # busto in avanti sull'impatto
absorb = snapshot_pose(ours)

recover = blend_pose(absorb, idle_neutral, 0.6)
absorb_frame = max(int(round(0.12 * fps)), 1)
recover_frame = max(int(round(0.30 * fps)), absorb_frame + 2)
end_frame = max(int(round(0.45 * fps)), recover_frame + 2)
_, n = bake_clip(ours, "land_soft", {
    0: fall_end,
    absorb_frame: absorb,
    recover_frame: recover,
    end_frame: idle_neutral,
})
log["created"].append({"clip": "land_soft", "fcurves": n, "loop": False})

# --- crouch disarmato: braccia rilassate -------------------------------------
# Il set Mixamo "Crouching" tiene le braccia in guardia (posa da combattimento): da
# disarmati sembra di mirare senza arma. Le braccia si sostituiscono con quelle di
# idle_neutral; il resto della clip (gambe, bacino, spina) non si tocca. Da armati non
# cambia nulla: l'overlay upper-body copre comunque le braccia.
relaxed_arms = idle_neutral
log["relaxed_crouch"] = []
for crouch_name in ("crouch_idle", "crouch_fwd", "crouch_back",
                    "crouch_left", "crouch_right"):
    removed = relax_arms(ours, library[crouch_name], relaxed_arms)
    log["relaxed_crouch"].append({"clip": crouch_name, "curve_rimosse": removed})

# ============================================================================
#  Export: tutte le azioni insieme (recuperate + procedurali)
# ============================================================================

all_actions = dict(library)
for entry in log["created"]:
    all_actions[entry["clip"]] = bpy.data.actions[entry["clip"]]

expected = set(library.keys()) | {e["clip"] for e in log["created"]}
log["lost"] = sorted(expected - set(a.name for a in bpy.data.actions if a.use_fake_user))

mx.assign_action(ours, next(iter(all_actions.values())))
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

log["output"] = {"glb": OUT_PATH, "bytes": os.path.getsize(OUT_PATH),
                 "count": len(all_actions), "fps": fps}
result = log
