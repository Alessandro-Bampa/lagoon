"""Misura gli assi Z dei bone di uno scheletro Mixamo e li confronta coi nostri.

Serve a rigenerare la tabella MIXAMO_Z_AXES di build_character.py, o a
verificare che una nuova clip usi la stessa convenzione di quella su cui la
tabella e' stata congelata.

Uso:
    python tools/blender/blender_client.py tools/blender/measure_mixamo_rolls.py <clip.fbx>

Stampa, pronta da incollare, la tabella dei target; e per ogni bone condiviso:
  - bend:  differenza di DIREZIONE dell'osso (dovuta alle proporzioni diverse,
           non correggibile col roll);
  - twist: differenza di ROLL vera. Se e' ~0 la tabella e' gia' allineata.
"""

import math
import re
import bpy
from mathutils import Vector

PREFIX = re.compile(r"^mixamorig\d*:")
BLEND_PATH = "c:/repositories/lagoon/assets/models/source/Body_Base.blend"
ARMATURE_NAME = "Armature_Character"


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


def bone_axes(arm_obj, strip=False):
    """Direzione e asse Z di ogni bone, in coordinate mondo."""
    out = {}
    m_world = arm_obj.matrix_world.to_3x3()
    for bone in arm_obj.data.bones:
        name = PREFIX.sub("", bone.name) if strip else bone.name
        m = m_world @ bone.matrix_local.to_3x3()
        out[name] = ((m @ Vector((0.0, 1.0, 0.0))).normalized(),
                     (m @ Vector((0.0, 0.0, 1.0))).normalized())
    return out


fbx_path = ARGV[0].replace("\\", "/")

bpy.ops.wm.open_mainfile(filepath=BLEND_PATH)
ours = bpy.data.objects[ARMATURE_NAME]

before = set(bpy.data.objects.keys())
with view3d_override():
    bpy.ops.import_scene.fbx(filepath=fbx_path)
imported = [bpy.data.objects[n] for n in bpy.data.objects.keys() if n not in before]
theirs = next(o for o in imported if o.type == "ARMATURE")

a = bone_axes(ours)
b = bone_axes(theirs, strip=True)

rows = []
worst_twist = 0.0
worst_bend = 0.0
for name in a:
    if name not in b:
        continue
    dir_a, z_a = a[name]
    dir_b, z_b = b[name]
    bend = math.degrees(dir_a.angle(dir_b))
    if bend > 150.0:
        rows.append({"bone": name, "bend": round(bend, 2), "twist": None,
                     "note": "direzione invertita, twist non definito"})
        continue
    twist = math.degrees((dir_a.rotation_difference(dir_b) @ z_a).angle(z_b))
    worst_twist = max(worst_twist, twist)
    worst_bend = max(worst_bend, bend)
    rows.append({"bone": name, "bend": round(bend, 2), "twist": round(twist, 2)})
rows.sort(key=lambda r: -(r["twist"] if r["twist"] is not None else 999.0))

print("\n# Da incollare in MIXAMO_Z_AXES di build_character.py:")
for name in a:
    if name in b:
        z = b[name][1]
        print('    "{}": ({}, {}, {}),'.format(
            name, round(z.x, 6), round(z.y, 6), round(z.z, 6)))

for obj in imported:
    bpy.data.objects.remove(obj, do_unlink=True)

result = {
    "fbx": fbx_path,
    "shared_bones": len(rows),
    "not_in_clip": sorted(set(a) - set(b)),
    "max_bend_deg": round(worst_bend, 2),
    "max_twist_deg": round(worst_twist, 2),
    "aligned": worst_twist < 1.0,
    "per_bone": rows,
}
