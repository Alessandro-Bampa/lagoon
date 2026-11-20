"""Costruisce un capo indossabile da una RICETTA JSON (skill `character-wearables`).

    "<blender>" --background --python tools/blender/wearables/build_wearable.py -- <ricetta.json> [preview] [noexport]

La ricetta la scrive Claude guardando le immagini di riferimento: sceglie slot, strato e template
(`wlib.py`), i colori campionati dalle foto e i dettagli. Lo script:

  1. reimporta il corpo base (`Character_Base.glb`): stesso scheletro, stesso riposo;
  2. costruisce le parti della ricetta, con pesi e regioni presi dal corpo;
  3. unisce tutto in UNA mesh con UNA texture (UV automatiche, colori cotti per posizione 3D);
  4. calcola le regioni del corpo che il capo racchiude (`CoversRegions`) se la ricetta non le fissa;
  5. esporta `assets/models/wearables/<id>.glb` (armatura + capo, niente clip),
     `assets/models/items/<id>.glb` (a terra) e, se il capo tocca le braccia, la variante in prima
     persona `assets/models/wearables/fp/<id>_FP.glb` (fp_arms.py);
  6. aggiorna `resources/items/<id>.tres` (Layer, CoversRegions) e `locales/items.csv`;
  7. con `preview`: mosaico di render (riposo + pose dalle clip) in %TEMP%/wearables/<id>.png.

Formato della ricetta: vedi tools/blender/wearables/recipes/*.json e la skill.
"""

import json
import math
import os
import sys

import bmesh
import bpy
import numpy as np
from mathutils import Vector

try:
    PROJECT_DIR
except NameError:
    PROJECT_DIR = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(
        os.path.abspath(__file__))))).replace("\\", "/")
try:
    ARGV
except NameError:
    ARGV = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
for p in (PROJECT_DIR + "/tools/blender", PROJECT_DIR + "/tools/blender/wearables"):
    if p not in sys.path:
        sys.path.append(p)

import charlib as C  # noqa: E402
import wlib as W  # noqa: E402
import wexport as X  # noqa: E402

X.configure(PROJECT_DIR)

PREVIEW = "preview" in ARGV
EXPORT = "noexport" not in ARGV
PREVIEW_DIR = X.PREVIEW_DIR

# =============================================================================================
# Costruzione
# =============================================================================================

def build_parts(body, recipe):
    layer = recipe["layer"]
    parts = []
    for spec in recipe["parts"]:
        made = W.TEMPLATES[spec["type"]](body, spec, layer)
        for part in made:
            W.finish_weights(part, body, spec.get("rigid_bone"), spec.get("rigidity", 0.0))
            parts.append(part)
        # specchiatura: "mirror": true duplica la parte con x -> -x e ossi _l <-> _r
        if spec.get("mirror"):
            for part in made:
                parts.append(mirror_part(part))
    return parts


def mirror_part(part):
    m = W.Part(part.name + "_mirror", part.spec)
    swap = lambda b: b[:-2] + ("_r" if b.endswith("_l") else "_l") if b.endswith(("_l", "_r")) else b
    for p, w, r in zip(part.verts, part.weights, part.regions):
        m.add_vert(Vector((-p.x, p.y, p.z)), {swap(b): v for b, v in w.items()}, r)
    m.faces = [tuple(reversed(f)) for f in part.faces]
    return m


def make_object(item_id, parts, armature):
    verts, faces, weights, regions, part_ids, smooth = [], [], [], [], [], []
    for pi, part in enumerate(parts):
        base = len(verts)
        bm = bmesh.new()
        bverts = [bm.verts.new(v) for v in part.verts]
        for f in part.faces:
            try:
                bm.faces.new([bverts[i] for i in f])
            except ValueError:
                pass
        if part.spec["type"] != "shell":
            bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
        bm.verts.index_update()
        verts += [tuple(v.co) for v in bm.verts]
        faces += [tuple(base + v.index for v in f.verts) for f in bm.faces]
        smooth += [part.spec.get("smooth_shade", part.spec["type"] in ("shell", "cap", "strap", "band"))] * len(bm.faces)
        bm.free()
        weights += part.weights
        regions += part.regions
        part_ids += [part.spec.get("_index", pi)] * len(part.verts)
    me = bpy.data.meshes.new(item_id)
    me.from_pydata(verts, [], faces)
    me.update()
    obj = bpy.data.objects.new(item_id, me)
    bpy.context.scene.collection.objects.link(obj)
    for p, s in zip(me.polygons, smooth):
        p.use_smooth = s
    groups = {}
    for i, w in enumerate(weights):
        for bone, v in w.items():
            if v <= 1e-4:
                continue
            if bone not in groups:
                groups[bone] = obj.vertex_groups.new(name=bone)
            groups[bone].add([i], v, "REPLACE")
    C.limit_and_normalize(obj, 4)
    C.write_region_colors(obj, regions)
    obj.parent = armature
    mod = obj.modifiers.new("Armature", "ARMATURE")
    mod.object = armature
    return obj, np.array(part_ids)


def unwrap(obj):
    C.select_only([obj])
    with bpy.context.temp_override(**C.view3d_override()):
        bpy.ops.object.mode_set(mode="EDIT")
        bpy.ops.mesh.select_all(action="SELECT")
        bpy.ops.uv.smart_project(angle_limit=math.radians(60), island_margin=0.015)
        bpy.ops.object.mode_set(mode="OBJECT")


def texture(obj, part_ids, recipe, body):
    size = recipe.get("texture_size", 256)
    pos, nrm, mask, pmap = C.position_map(obj, size, extra=part_ids)
    colors = recipe.get("colors", {})
    img = np.zeros((size, size, 3))
    for pi, spec in enumerate(recipe["parts"]):
        sel = pmap == pi
        if not sel.any():
            continue
        base = W.hex_color(colors.get(spec.get("color"), spec.get("color", "#808080")))
        mat = W.material_texture(spec.get("material", "canvas"), base, size, pos, nrm, mask, 17 + pi)
        img[sel] = mat[sel]
    img = W.paint_details(img, pos, nrm, mask, pmap, recipe.get("details", []), body, colors)
    # ombra di contatto: i texel piu' vicini al corpo un po' piu' scuri, il basso un po' piu' sporco
    low = np.clip(1 - (pos[..., 2] / 1.8), 0, 1)
    img *= (1 - 0.12 * recipe.get("dirt", 1.0) * low)[..., None]
    img = C.wear(img, 31, recipe.get("wear", 0.5))
    return C.psx(np.clip(img, 0, 1))


# =============================================================================================
# Principale
# =============================================================================================

def body_fp():
    """Braccia nude del corpo base sullo scheletro del viewmodel: Character_Base_FP.glb."""
    import fp_arms
    C.reset_scene()
    body = X.load_body()
    path = fp_arms.export_variant(PROJECT_DIR, body.mesh, "Character_Base", C.BODY_FP_GLB)
    if path:
        X.write_import(path, bone_map=False)
    return {"body_fp": path}


def main():
    if "body_fp" in ARGV:
        return body_fp()
    recipe_path = next(a for a in ARGV if a.endswith(".json"))
    if not os.path.isabs(recipe_path):
        recipe_path = os.path.join(PROJECT_DIR, recipe_path)
    recipe = json.load(open(recipe_path, encoding="utf-8"))
    for i, spec in enumerate(recipe["parts"]):
        spec["_index"] = i
    item_id = recipe["item_id"]

    C.reset_scene()
    body = X.load_body()
    parts = build_parts(body, recipe)
    obj, part_ids = make_object(item_id, parts, body.armature)
    unwrap(obj)
    rgb = texture(obj, part_ids, recipe, body)
    image = C.make_image(item_id, rgb, os.path.join(PREVIEW_DIR, f"{item_id}_tex.png") if PREVIEW else None)
    obj.data.materials.append(C.psx_material(item_id, image))
    return X.finalize(obj, body, recipe, export=EXPORT, preview_on=PREVIEW)


result = main()
print("RESULT", json.dumps(result))
