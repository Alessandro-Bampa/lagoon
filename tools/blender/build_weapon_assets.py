"""Genera i .glb prototipo delle armi: W_Rifle e W_Pistol.

Sono i sostituti del placeholder a box di WeaponVisual, costruiti NELLO STESSO FRAME
DELLA PRESA: origine sull'impugnatura della mano destra, canna lungo il +Z di Godot,
calcio dietro (-Z), astina del fucile ESATTAMENTE a SupportGripOffset.Z = 0.391 m
(two_handed.tres — valore MISURATO dalla posa, non toccarlo). Cosi' l'asset si aggancia
a GripPoint senza offset e un disallineamento fra presa e posa si vede a colpo d'occhio,
come col placeholder.

Assi: le quote qui sotto sono nel frame di GODOT (X destra, Y su, Z canna). In Blender
si costruisce con la canna lungo -Y e l'alto su +Z; l'export con export_yup=True fa la
conversione (x, y, z)godot = (x, z, -y)blender.

Uso:
    python tools/blender/blender_client.py tools/blender/build_weapon_assets.py
"""

import importlib
import math
import os

import bpy
import bmesh
from mathutils import Matrix, Vector

import mixamo_common as mx

importlib.reload(mx)

OUT_DIR = mx.PROJECT_DIR + "/assets/models/weapons"


def godot_to_blender(v):
    """(x, y, z) Godot -> (x, -z, y) Blender."""
    return Vector((v[0], -v[2], v[1]))


def add_box(bm, size_godot, pos_godot):
    """Box con dimensioni e centro espressi nel frame di Godot."""
    size = Vector((size_godot[0], size_godot[2], size_godot[1]))  # (sx, sz, sy)
    center = godot_to_blender(pos_godot)
    matrix = Matrix.Translation(center) @ Matrix.Diagonal(size).to_4x4()
    bmesh.ops.create_cube(bm, size=1.0, matrix=matrix)


def add_barrel(bm, radius, length, pos_godot):
    """Cilindro lungo la canna (-Y Blender), centro nel frame di Godot."""
    center = godot_to_blender(pos_godot)
    matrix = (Matrix.Translation(center)
              @ Matrix.Rotation(math.radians(90.0), 4, "X")
              @ Matrix.Diagonal(Vector((radius, radius, length / 2.0))).to_4x4())
    bmesh.ops.create_cone(bm, cap_ends=True, segments=10,
                          radius1=1.0, radius2=1.0, depth=2.0, matrix=matrix)


def build_weapon(name, parts, barrel):
    mesh = bpy.data.meshes.new(name)
    bm = bmesh.new()
    for size, pos in parts:
        add_box(bm, size, pos)
    add_barrel(bm, *barrel)
    bm.to_mesh(mesh)
    bm.free()

    material = bpy.data.materials.new("M_" + name)
    material.use_nodes = True
    bsdf = material.node_tree.nodes.get("Principled BSDF")
    if bsdf is not None:
        bsdf.inputs["Base Color"].default_value = (0.09, 0.09, 0.11, 1.0)
        bsdf.inputs["Roughness"].default_value = 0.55
        if "Metallic" in bsdf.inputs:
            bsdf.inputs["Metallic"].default_value = 0.6
    mesh.materials.append(material)

    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    return obj


def export_glb(obj, path):
    for other in bpy.context.view_layer.objects:
        other.select_set(False)
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    with mx.view3d_override(object=obj, active_object=obj, selected_objects=[obj],
                            selected_editable_objects=[obj]):
        bpy.ops.export_scene.gltf(
            filepath=path,
            export_format="GLB",
            use_selection=True,
            export_yup=True,
            export_animations=False,
            export_cameras=False,
            export_lights=False,
        )


# Scena pulita: lo script e' idempotente e non dipende da Body_Base.blend.
bpy.ops.wm.read_homefile(use_empty=True)

# Quote nel frame di Godot, derivate dal placeholder di WeaponVisual.Build (le stesse
# proporzioni che le verifiche gia' accettano). Fucile: Width 5 -> length 0.6.
SUPPORT_Z = 0.391  # SupportGripOffset.Z di two_handed.tres: l'astina cade QUI.

rifle = build_weapon("W_Rifle", parts=[
    ((0.035, 0.10, 0.045), (0.0, -0.055, 0.0)),          # impugnatura, sotto l'origine
    ((0.05, 0.075, 0.56), (0.0, 0.0, 0.08)),             # castello
    ((0.042, 0.085, 0.16), (0.0, -0.02, -0.25)),         # calcio
    ((0.045, 0.05, 0.09), (0.0, -0.02, SUPPORT_Z)),      # astina (mano di supporto)
    ((0.012, 0.05, 0.012), (0.0, 0.065, 0.30)),          # mirino anteriore
    ((0.03, 0.03, 0.10), (0.0, 0.055, 0.02)),            # tacca di mira
], barrel=(0.013, 0.30, (0.0, 0.01, 0.51)))

pistol = build_weapon("W_Pistol", parts=[
    ((0.035, 0.10, 0.045), (0.0, -0.055, 0.0)),          # impugnatura
    ((0.05, 0.075, 0.194), (0.0, 0.0, 0.047)),           # carrello
    ((0.012, 0.03, 0.012), (0.0, 0.05, 0.13)),           # mirino
], barrel=(0.011, 0.12, (0.0, 0.01, 0.204)))

os.makedirs(OUT_DIR, exist_ok=True)
result = {"weapons": []}
for obj in (rifle, pistol):
    path = "{}/{}.glb".format(OUT_DIR, obj.name)
    export_glb(obj, path)
    result["weapons"].append({
        "name": obj.name,
        "glb": path,
        "bytes": os.path.getsize(path),
        "tris": sum(len(p.vertices) - 2 for p in obj.data.polygons),
    })
