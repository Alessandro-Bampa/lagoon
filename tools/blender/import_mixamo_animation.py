"""Applica una clip Mixamo ad Armature_Character ed esporta un .glb di sola animazione.

Prerequisito: il .blend dell'asset deve essere aperto in Blender (viene ricaricato
da assets/models/source/Body_Base.blend, quindi lo stato precedente non conta).

Uso:
    python tools/blender/blender_client.py tools/blender/import_mixamo_animation.py <clip.fbx> [nome]

Impostazioni con cui scaricare da Mixamo:
    Format = FBX Binary, Skin = Without Skin, In Place = attivo.
    Root motion NO: la posizione la calcola l'host (CLAUDE.md §3).

Cosa fa e perche':
  - toglie il prefisso `mixamorig<N>:` rinominando i bone (Blender aggiorna da
    solo i data path dell'azione), cosi' i nomi coincidono con i nostri;
  - riscala la traslazione del bacino, che arriva nelle unita' di Mixamo
    (oggetto a scala 0.01) e su uno scheletro piu' alto del nostro;
  - esporta solo armature + azione, senza mesh: in Godot diventa una
    AnimationLibrary da agganciare a Body_Base.
"""

import math
import os
import re
import bpy

PREFIX = re.compile(r"^mixamorig\d*:")

PROJECT_DIR = "c:/repositories/lagoon"
BLEND_PATH = PROJECT_DIR + "/assets/models/source/Body_Base.blend"
ANIM_DIR = PROJECT_DIR + "/assets/models/animations"

ARMATURE_NAME = "Armature_Character"
MESH_NAME = "Body_Base"
OUR_HEIGHT = 1.78

# Traslazione residua del bacino oltre la quale la clip non e' "in place".
IN_PLACE_MAX_M = 0.25


def view3d_override(**extra):
    ctx = {}
    for window in bpy.context.window_manager.windows:
        for area in window.screen.areas:
            if area.type != "VIEW_3D":
                continue
            region = next((r for r in area.regions if r.type == "WINDOW"), None)
            if region is None:
                continue
            ctx = {"window": window, "screen": window.screen, "area": area,
                   "region": region, "space_data": area.spaces.active}
            break
        if ctx:
            break
    ctx.update(extra)
    return bpy.context.temp_override(**ctx)


def action_fcurves(action):
    """Blender 5.x: le fcurve stanno in layers/strips/channelbag."""
    if hasattr(action, "fcurves"):
        return list(action.fcurves)
    curves = []
    for layer in action.layers:
        for strip in layer.strips:
            for bag in strip.channelbags:
                curves.extend(bag.fcurves)
    return curves


fbx_path = ARGV[0].replace("\\", "/")
clip_name = ARGV[1] if len(ARGV) > 1 else os.path.splitext(os.path.basename(fbx_path))[0]
log = {"fbx": fbx_path, "clip": clip_name}

# Riparti sempre dall'asset su disco: nessuna dipendenza dallo stato di Blender.
bpy.ops.wm.open_mainfile(filepath=BLEND_PATH)
ours = bpy.data.objects[ARMATURE_NAME]

before = set(bpy.data.objects.keys())
with view3d_override():
    bpy.ops.import_scene.fbx(filepath=fbx_path)
imported = [bpy.data.objects[n] for n in bpy.data.objects.keys() if n not in before]
theirs = next(o for o in imported if o.type == "ARMATURE")

zs = [(theirs.matrix_world @ b.head_local).z for b in theirs.data.bones]
zs += [(theirs.matrix_world @ b.tail_local).z for b in theirs.data.bones]
their_height = max(zs) - min(zs)
log["their_height"] = round(their_height, 4)

renamed = 0
for bone in theirs.data.bones:
    clean = PREFIX.sub("", bone.name)
    if clean != bone.name:
        bone.name = clean
        renamed += 1
log["renamed_bones"] = renamed

action = theirs.animation_data.action
action.name = clip_name
curves = action_fcurves(action)
log["fcurves"] = len(curves)

# I nostri bone che la clip non anima, e viceversa.
animated = {c.data_path.split('"')[1] for c in curves if '"' in c.data_path}
our_bones = {b.name for b in ours.data.bones}
log["our_bones_not_animated"] = sorted(our_bones - animated)
log["extra_animated_bones"] = len(animated - our_bones)

factor = 0.01 * (OUR_HEIGHT / their_height) if their_height else 0.01
log["hips_location_factor"] = round(factor, 6)
spans = {}
for curve in curves:
    if curve.data_path.endswith(".location") and '"Hips"' in curve.data_path:
        for kp in curve.keyframe_points:
            kp.co[1] *= factor
            kp.handle_left[1] *= factor
            kp.handle_right[1] *= factor
        curve.update()
        vals = [kp.co[1] for kp in curve.keyframe_points]
        spans["xyz"[curve.array_index]] = round(max(vals) - min(vals), 4)
log["hips_span_m"] = spans

# Una clip "In Place" oscilla di pochi centimetri IN ORIZZONTALE; se trasla molto ha root motion e
# combatterebbe contro SyncPosition.
#
# ATTENZIONE ALL'ASSE. La traslazione di un pose bone e' nello spazio LOCALE dell'osso, e l'osso
# Hips punta verso l'ALTO: il canale Y e' quindi la VERTICALE, non una direzione orizzontale.
# L'orizzontale sono X e Z. Sbagliarsi qui fa scartare come "root motion" qualunque salto, il cui
# unico peccato e' staccarsi da terra: Jump.fbx ha y = 0.91 m (l'altezza del salto) e x/z ~ 5 cm.
horizontal = max(spans.get("x", 0.0), spans.get("z", 0.0))
log["in_place"] = horizontal <= IN_PLACE_MAX_M
log["horizontal_span_m"] = round(horizontal, 4)
log["vertical_span_m"] = round(spans.get("y", 0.0), 4)

animation_data = ours.animation_data_create()
animation_data.action = action
for slot in action.slots:
    try:
        animation_data.action_slot = slot
        break
    except (TypeError, RuntimeError):
        continue

# Il rig Mixamo ha finito il suo lavoro: via, o finisce nell'export.
for obj in imported:
    bpy.data.objects.remove(obj, do_unlink=True)

scene = bpy.context.scene
scene.frame_start, scene.frame_end = (int(v) for v in action.frame_range)
log["frame_range"] = [scene.frame_start, scene.frame_end]

os.makedirs(ANIM_DIR, exist_ok=True)
out_path = "{}/{}.glb".format(ANIM_DIR, clip_name)

# Solo l'armature: la mesh sta gia' in Body_Base.glb, qui serve la sola animazione.
for obj in bpy.context.view_layer.objects:
    obj.select_set(False)
ours.select_set(True)
bpy.context.view_layer.objects.active = ours

if not log["in_place"]:
    log["export"] = "SALTATO: la clip trasla di {} m, non e' in place".format(log["horizontal_span_m"])
else:
    with view3d_override(object=ours, active_object=ours, selected_objects=[ours],
                         selected_editable_objects=[ours]):
        bpy.ops.export_scene.gltf(
            filepath=out_path,
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
    log["export"] = {"glb": out_path, "bytes": os.path.getsize(out_path)}

result = log
