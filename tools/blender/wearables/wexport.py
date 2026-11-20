"""Uscite comuni dei capi indossabili: corpo di riferimento, regioni coperte, export dei glb,
`.import`, `.tres`, traduzioni, varianti in prima persona e anteprima a mosaico.

La usano le due strade che producono un capo (skill `character-wearables`, `asset-ingest`):
  - `build_wearable.py`: capo costruito da una ricetta di template parametrici;
  - `tools/blender/ingest/outfit.py`: capo ricavato da un modello sorgente semplificato.

Prima dell'uso: `configure(PROJECT_DIR)`.
"""

import math
import os
import re

import bpy
import numpy as np
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree

import charlib as C
import wlib as W

PROJECT_DIR = ""
PREVIEW_DIR = os.path.join(os.environ.get("TEMP", "/tmp"), "wearables")


def configure(project_dir):
    global PROJECT_DIR
    PROJECT_DIR = project_dir


# Categorie di ItemCategory per slot (stesso ordine di ItemCategory.cs)
CATEGORY = {"Headwear": 13, "Earpiece": 14, "FaceCover": 15, "Eyewear": 16, "Armband": 17,
            "BodyArmor": 18, "ChestRig": 19, "ArmoredRig": 20, "Backpack": 21,
            "Top": 31, "Legs": 32, "Footwear": 33, "Gloves": 34}
ARM_REGIONS = {C.REGION["UpperArm"], C.REGION["Forearm"], C.REGION["Hand"]}


def region_mask(names):
    m = 0
    for n in names:
        m |= 1 << C.REGION[n]
    return m


def load_body():
    imported = C.import_glb(C.project_path(PROJECT_DIR, C.BODY_GLB))
    arm = next(o for o in imported if o.type == "ARMATURE")
    mesh = next(o for o in imported if o.type == "MESH")
    arm.name = "Armature"
    mesh.name = "Body"
    arm.animation_data_create()
    arm.animation_data.action = None
    for pb in arm.pose.bones:
        pb.matrix_basis.identity()
    bpy.context.view_layer.update()
    return W.Body(arm, mesh)


def compute_covers(obj, body, threshold=0.95, reach=0.08):
    """Una regione e' coperta se quasi tutti i suoi vertici, guardando lungo la normale, trovano
    il capo entro `reach`. Le regioni parzialmente coperte NON si nascondono: si vedrebbe un buco."""
    me = obj.data
    bvh = BVHTree.FromPolygons([obj.matrix_world @ v.co for v in me.vertices],
                               [tuple(p.vertices) for p in me.polygons])
    covered = []
    fractions = {}
    for name, idx in C.REGION.items():
        sel = np.where(body.region == idx)[0]
        if len(sel) == 0:
            continue
        hits = 0
        for i in sel:
            p, n = Vector(body.co[i]), Vector(body.no[i])
            hit = bvh.ray_cast(p + n * 0.001, n, reach)[0]
            if hit is not None:
                hits += 1
        frac = hits / len(sel)
        fractions[name] = round(frac, 3)
        if frac >= threshold:
            covered.append(name)
    return covered, fractions


IMPORT_TEMPLATE = """[remap]

importer="scene"
importer_version=1
type="PackedScene"

[deps]

source_file="res://{path}"

[params]

nodes/root_type=""
nodes/root_name=""
nodes/root_script=null
nodes/apply_root_scale=true
nodes/root_scale=1.0
nodes/import_as_skeleton_bones=false
nodes/use_name_suffixes=true
nodes/use_node_type_suffixes=true
meshes/ensure_tangents=true
meshes/generate_lods=true
meshes/create_shadow_meshes=true
meshes/light_baking=1
meshes/lightmap_texel_size=0.2
meshes/force_disable_compression=false
skins/use_named_skins=true
animation/import=false
import_script/path=""
materials/extract=0
materials/extract_format=0
materials/extract_path=""
_subresources={subresources}
gltf/naming_version=2
gltf/embedded_image_handling=3
gltf/texture_map_mode=1
"""
BONE_MAP_SUBRESOURCES = ('{\n"nodes": {\n"PATH:Armature/Skeleton3D": {\n'
                         '"retarget/bone_map": Resource("res://animation/resources/ual_bone_map.tres")\n}\n}\n}')


def write_import(rel_path, bone_map):
    """`.import` accanto al glb, se manca. Il capo indossato va importato con la STESSA BoneMap del
    corpo: gli ossi diventano Hips, Spine... e la Skin si lega per nome allo scheletro del corpo."""
    path = C.project_path(PROJECT_DIR, rel_path + ".import")
    if os.path.exists(path):
        return
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(IMPORT_TEMPLATE.format(path=rel_path, subresources=BONE_MAP_SUBRESOURCES if bone_map else "{}"))


def export_worn(obj, armature, item_id):
    rel = f"{C.WEARABLES_DIR}/{item_id}.glb"
    C.export_glb(C.project_path(PROJECT_DIR, rel), [armature, obj])
    write_import(rel, bone_map=True)


def export_ground(obj, item_id, pose):
    """Modello a terra: mesh statica. 'lay' = disteso sul dorso (capi morbidi), 'stand' = come
    indossato, appoggiato al suolo (oggetti rigidi)."""
    me = obj.data.copy()
    ground = bpy.data.objects.new(item_id + "_ground", me)
    bpy.context.scene.collection.objects.link(ground)
    ground.vertex_groups.clear()
    co = np.array([tuple(v.co) for v in me.vertices])
    center = co.mean(axis=0)
    m = Matrix.Translation(-Vector(center))
    if pose == "lay":
        m = Matrix.Rotation(math.radians(90), 4, "X") @ m     # davanti in su
    me.transform(m)
    co = np.array([tuple(v.co) for v in me.vertices])
    me.transform(Matrix.Translation((0, 0, -co[:, 2].min())))
    C.export_glb(C.project_path(PROJECT_DIR, f"assets/models/items/{item_id}.glb"), [ground])
    bpy.data.objects.remove(ground)


def update_tres(recipe, covers_mask):
    item = recipe["item_id"]
    path = C.project_path(PROJECT_DIR, f"resources/items/{item}.tres")
    info = recipe.get("item", {})
    if os.path.exists(path):
        s = open(path, encoding="utf-8").read()
        s = s.replace('script_class="ItemDefinition"', 'script_class="WearableDefinition"')
        s = s.replace("res://inventory/scripts/ItemDefinition.cs", "res://inventory/scripts/data/WearableDefinition.cs")
        for key, val in (("Layer", recipe["layer"]), ("CoversRegions", covers_mask)):
            if re.search(rf"^{key} = .*$", s, re.M):
                s = re.sub(rf"^{key} = .*$", f"{key} = {val}", s, flags=re.M)
            else:
                s = s.rstrip() + f"\n{key} = {val}\n"
    else:
        s = ('[gd_resource type="Resource" script_class="WearableDefinition" load_steps=2 format=3]\n\n'
             '[ext_resource type="Script" path="res://inventory/scripts/data/WearableDefinition.cs" id="1"]\n\n'
             '[resource]\nscript = ExtResource("1")\n'
             f'ItemId = "{item}"\nWidth = {info.get("width", 2)}\nHeight = {info.get("height", 2)}\n'
             f'Weight = {info.get("weight", 0.5)}\nCategory = {CATEGORY[recipe["slot"]]}\n'
             f'Layer = {recipe["layer"]}\nCoversRegions = {covers_mask}\n')
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(s)


def update_locales(recipe):
    names = recipe.get("names")
    if not names:
        return
    path = C.project_path(PROJECT_DIR, "locales/items.csv")
    text = open(path, encoding="utf-8", newline="").read()
    nl = "\r\n" if "\r\n" in text else "\n"
    key = recipe["item_id"].upper()
    rows = []
    for suffix in ("NAME", "SHORT", "DESC"):
        if suffix.lower() in names and f"ITEM_{key}_{suffix}," not in text:
            en, it = names[suffix.lower()]
            q = lambda t: f'"{t}"' if "," in t else t
            rows.append(f"ITEM_{key}_{suffix},{q(en)},{q(it)}")
    if rows:
        if not text.endswith(nl):
            text += nl
        text += nl.join(rows) + nl
        with open(path, "w", encoding="utf-8", newline="") as f:
            f.write(text)


# =============================================================================================
# Anteprima
# =============================================================================================

def preview(body, obj, item_id, covers):
    os.makedirs(PREVIEW_DIR, exist_ok=True)
    # corpo con le regioni coperte nascoste, come fa lo shader in gioco
    hidden = {C.REGION[n] for n in covers}
    g = body.mesh.vertex_groups.new(name="_visible")
    g.add([i for i, r in enumerate(body.region) if r not in hidden], 1.0, "REPLACE")
    mask = body.mesh.modifiers.new("Mask", "MASK")
    mask.vertex_group = "_visible"
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_EEVEE"
    scene.render.resolution_x = scene.render.resolution_y = 420
    cam_data = bpy.data.cameras.new("cam")
    cam_data.type = "ORTHO"
    cam = bpy.data.objects.new("cam", cam_data)
    scene.collection.objects.link(cam)
    scene.camera = cam
    sun = bpy.data.objects.new("sun", bpy.data.lights.new("sun", "SUN"))
    scene.collection.objects.link(sun)
    sun.rotation_euler = (math.radians(50), 0, math.radians(-30))
    sun.data.energy = 3.0
    fill = bpy.data.objects.new("fill", bpy.data.lights.new("fill", "SUN"))
    scene.collection.objects.link(fill)
    fill.rotation_euler = (math.radians(60), 0, math.radians(160))
    fill.data.energy = 1.5
    world = bpy.data.worlds.new("w")
    scene.world = world
    world.color = (0.55, 0.55, 0.57)
    # il materiale importato moltiplica il colore di vertice (le regioni): lo si rifa' pulito
    tex = next((n.image for m in body.mesh.data.materials if m and m.use_nodes
                for n in m.node_tree.nodes if n.type == "TEX_IMAGE"), None)
    if tex is not None:
        body.mesh.data.materials.clear()
        body.mesh.data.materials.append(C.psx_material("BodyPreview", tex))
    co = np.array([tuple(obj.matrix_world @ v.co) for v in obj.data.vertices])
    lo, hi = co.min(axis=0), co.max(axis=0)
    center = Vector(((lo[0] + hi[0]) / 2 * 0, (lo[1] + hi[1]) / 2, (lo[2] + hi[2]) / 2))
    span = max(hi[2] - lo[2], hi[0] - lo[0], 0.25) * 1.25
    shots = [("front", (0, -1, 0), 0), ("side", (1, 0, 0), 0), ("back", (0, 1, 0), 0),
             ("34", (-0.7, -0.7, 0.25), 0)]
    clips = [("Jog_Fwd", 6), ("Crouch_Idle", 1), ("Pistol_Aim_Neutral", 1), ("Sitting_Idle", 1),
             ("Swim_Fwd", 6)]
    frames = []
    arm = body.armature

    def shoot(tag, dirv, sc=None):
        d = Vector(dirv).normalized()
        cam.location = center - d * 4
        cam.rotation_euler = d.to_track_quat("-Z", "Y").to_euler()
        cam_data.ortho_scale = sc or span
        path = os.path.join(PREVIEW_DIR, f"{item_id}_{tag}.png")
        scene.render.filepath = path
        bpy.ops.render.render(write_still=True)
        frames.append(path)

    for tag, d, _ in shots:
        shoot(tag, d)
    for clip, frame in clips:
        act = next((a for a in bpy.data.actions if a.name.startswith(clip)), None)
        if act is None:
            continue
        arm.animation_data.action = act
        if len(act.slots):
            arm.animation_data.action_slot = act.slots[0]
        scene.frame_set(frame)
        shoot("pose_" + clip, (-0.7, -0.7, 0.15), 2.3)
    arm.animation_data.action = None
    # mosaico
    tiles = [C.load_image_array(p)[..., :3] for p in frames]
    cols = 5
    rows = (len(tiles) + cols - 1) // cols
    h, w = tiles[0].shape[:2]
    sheet = np.ones((rows * h, cols * w, 3)) * 0.2
    for k, t in enumerate(tiles):
        r, c = divmod(k, cols)
        sheet[(rows - 1 - r) * h:(rows - r) * h, c * w:(c + 1) * w] = t
    C.make_image("sheet", sheet, os.path.join(PREVIEW_DIR, f"{item_id}.png"))
    for p in frames:
        os.remove(p)
    body.mesh.modifiers.remove(mask)


def finalize(obj, body, spec, export=True, preview_on=False):
    """Coperture, export (indossato, a terra, prima persona), `.tres`, traduzioni, anteprima.
    `spec`: item_id, slot, layer, ground, covers (facoltativo), item, names, first_person."""
    item_id = spec["item_id"]
    if "covers" in spec:
        covers, fractions = spec["covers"], {}
    else:
        covers, fractions = compute_covers(obj, body)
    mask = region_mask(covers)
    tris = sum(len(p.vertices) - 2 for p in obj.data.polygons)
    report = {"item": item_id, "tris": tris, "verts": len(obj.data.vertices),
              "covers": covers, "covers_mask": mask, "coverage": fractions}
    touches_arms = any(r in ARM_REGIONS for r in set(int(x) for x in C.read_region_colors(obj)))
    if export:
        export_worn(obj, body.armature, item_id)
        export_ground(obj, item_id, spec.get("ground", "lay"))
        update_tres(spec, mask)
        update_locales(spec)
        if touches_arms and spec.get("first_person", True):
            import fp_arms
            report["fp"] = fp_arms.export_variant(PROJECT_DIR, obj, item_id)
            if report["fp"]:
                write_import(report["fp"], bone_map=False)
    if preview_on:
        preview(body, obj, item_id, covers)
        report["preview"] = os.path.join(PREVIEW_DIR, f"{item_id}.png")
    return report
