"""Passi comuni dell'ingresso di un modello sorgente (skill `asset-ingest`).

Ogni asset del gioco che parte da un modello "vero" (molti poligoni, texture PBR ad alta risoluzione)
passa di qui prima della sotto-pipeline della sua categoria:

  1. `load_source`  carica il file (.blend, .glb/.gltf, .fbx, .obj) in una scena vuota;
  2. `normalize`    applica le trasformazioni, orienta secondo la convenzione della categoria e
                    porta il modello alla misura REALE dichiarata nel manifesto;
  3. `simplify`     tiene le parti che contano (decimate a un budget di triangoli, ripartito per
                    parte) e lascia le minuterie (fibbie, cerniere, viti) SOLO alla texture;
  4. `bake_color`   cuoce sul modello leggero il COLORE ORIGINALE (base color, non illuminato) e
                    l'occlusione del modello pesante: nessun colore inventato;
  5. `stylize`      trasforma quella texture nello stile del gioco: risoluzione bassa, filtro nearest,
                    colori smorzati, occlusione leggera, 5 bit per canale con dithering (come
                    `charlib.psx` e il kit veneziano).

Convenzioni di asse (Blender, Z in su): `FRONT` per categoria. Unita' in metri.
"""

import math
import os

import bmesh
import bpy
import numpy as np
from mathutils import Matrix, Vector

import charlib as C

# Dove deve guardare il "davanti" di un asset di ogni categoria, in Blender.
#   outfit: il davanti di chi lo indossa (il personaggio guarda -Y = +Z di Godot);
#   boat:   la prua (+Y, come build_lagoon_boats.py);
#   weapon: la bocca di fuoco (+Y, come i viewmodel: -Z di Godot).
FRONT = {"outfit": Vector((0, -1, 0)), "boat": Vector((0, 1, 0)), "weapon": Vector((0, 1, 0))}

AXES = {"+X": Vector((1, 0, 0)), "-X": Vector((-1, 0, 0)), "+Y": Vector((0, 1, 0)),
        "-Y": Vector((0, -1, 0)), "+Z": Vector((0, 0, 1)), "-Z": Vector((0, 0, -1))}


# =============================================================================================
# 1. Caricamento
# =============================================================================================

def load_source(path):
    """Oggetti mesh del file sorgente, collegati alla scena. Un .blend si APPENDE (niente scene,
    niente script): si prendono solo gli oggetti, con materiali e immagini (anche impacchettate)."""
    ext = os.path.splitext(path)[1].lower()
    before = set(bpy.data.objects)
    if ext == ".blend":
        with bpy.data.libraries.load(path, link=False) as (src, dst):
            dst.objects = [name for name in src.objects]
        for o in dst.objects:
            if o is not None and o.type == "MESH":
                bpy.context.scene.collection.objects.link(o)
    elif ext in (".glb", ".gltf"):
        C.import_glb(path)
    elif ext == ".fbx":
        with bpy.context.temp_override(**C.view3d_override()):
            bpy.ops.import_scene.fbx(filepath=path)
    elif ext == ".obj":
        with bpy.context.temp_override(**C.view3d_override()):
            bpy.ops.wm.obj_import(filepath=path)
    else:
        raise ValueError("formato sorgente non gestito: " + ext)
    objs = [o for o in bpy.data.objects if o not in before and o.type == "MESH"
            and o.name in bpy.context.scene.objects]
    for img in bpy.data.images:
        if img.packed_file is None and img.source == "FILE" and not os.path.exists(bpy.path.abspath(img.filepath)):
            print("[ingest] ATTENZIONE: immagine mancante", img.filepath)
    return objs


# =============================================================================================
# 2. Normalizzazione
# =============================================================================================

def normalize(objs, category, source_front="-Y", source_up="+Z", size=None, scale=None):
    """Trasformazioni applicate, davanti della categoria, misura reale. `size`: {"axis": "z",
    "meters": 0.55} (ingombro totale lungo l'asse, DOPO l'orientamento) oppure `scale` diretto.
    Ritorna il fattore di scala usato."""
    for o in objs:
        o.data = o.data.copy() if o.data.users > 1 else o.data
        o.data.transform(o.matrix_world)
        o.parent = None
        o.matrix_world = Matrix.Identity(4)
    up = AXES[source_up]
    front = AXES[source_front]
    # terna sorgente (front, up) -> terna di gioco (FRONT[category], +Z)
    src = C.frame_from(front, up)
    dst = C.frame_from(FRONT[category], Vector((0, 0, 1)))
    rot = (dst @ src.inverted()).to_4x4()
    for o in objs:
        o.data.transform(rot)
    lo, hi = bounds(objs)
    if size:
        axis = "xyz".index(size.get("axis", "z"))
        s = size["meters"] / max(hi[axis] - lo[axis], 1e-6)
    else:
        s = scale or 1.0
    for o in objs:
        o.data.transform(Matrix.Scale(s, 4))
        o.data.update()
    return s


def bounds(objs):
    co = np.concatenate([np.array([tuple(o.matrix_world @ v.co) for v in o.data.vertices]) for o in objs])
    return co.min(axis=0), co.max(axis=0)


def tris(obj):
    return sum(len(p.vertices) - 2 for p in obj.data.polygons)


# =============================================================================================
# 3. Semplificazione
# =============================================================================================

def part_of(obj, parts):
    """Nome della parte del manifesto a cui appartiene un oggetto sorgente (sottostringa del nome)."""
    for name in parts:
        if name.lower() in obj.name.lower():
            return name
    return None


def simplify(objs, parts, budget, symmetry=False):
    """Copia leggera delle parti `keep`: ognuna decimata a `share * budget` triangoli, poi unite.
    Ogni faccia ricorda la sua parte nell'attributo intero `part` (indice in `parts`).
    Le parti `bake` restano solo come sorgente della texture. Ritorna l'oggetto leggero."""
    names = list(parts)
    lows = []
    for o in objs:
        name = part_of(o, parts)
        if name is None or parts[name].get("role", "keep") != "keep":
            continue
        low = o.copy()
        low.data = o.data.copy()
        low.name = "low_" + name
        bpy.context.scene.collection.objects.link(low)
        target = max(12, int(budget * parts[name].get("share", 0.5)))
        have = tris(low)
        if have > target:
            mod = low.modifiers.new("Decimate", "DECIMATE")
            mod.decimate_type = "COLLAPSE"
            mod.ratio = target / have
            mod.use_collapse_triangulate = True
            mod.use_symmetry = symmetry
            mod.symmetry_axis = "X"
            with bpy.context.temp_override(object=low, active_object=low, **C.view3d_override()):
                bpy.ops.object.modifier_apply(modifier=mod.name)
        attr = low.data.attributes.new("part", "INT", "FACE")
        attr.data.foreach_set("value", [names.index(name)] * len(low.data.polygons))
        low.data.materials.clear()
        lows.append(low)
    if not lows:
        raise ValueError("nessuna parte 'keep' nel manifesto corrisponde agli oggetti sorgente")
    C.select_only(lows, active=lows[0])
    with bpy.context.temp_override(active_object=lows[0], selected_editable_objects=lows,
                                   **C.view3d_override()):
        bpy.ops.object.join()
    low = lows[0]
    low.name = low.data.name = "Low"
    for p in low.data.polygons:
        p.use_smooth = True
    return low


# =============================================================================================
# 4. Cottura del colore originale
# =============================================================================================

def _emission_from_base_color(mat):
    """Il materiale sorgente emette il proprio base color (texture o valore): la cottura EMIT
    lo copia tale e quale, senza luci ne' metallicita' a sporcarlo."""
    if not mat or not mat.use_nodes:
        return
    nt = mat.node_tree
    out = next((n for n in nt.nodes if n.type == "OUTPUT_MATERIAL" and n.is_active_output), None) \
        or next((n for n in nt.nodes if n.type == "OUTPUT_MATERIAL"), None)
    bsdf = next((n for n in nt.nodes if n.type == "BSDF_PRINCIPLED"), None)
    if out is None or bsdf is None:
        return
    emit = nt.nodes.new("ShaderNodeEmission")
    sock = bsdf.inputs["Base Color"]
    if sock.is_linked:
        nt.links.new(sock.links[0].from_socket, emit.inputs["Color"])
    else:
        emit.inputs["Color"].default_value = sock.default_value
    nt.links.new(emit.outputs["Emission"], out.inputs["Surface"])


def unwrap(obj, margin=0.006):
    C.select_only([obj])
    with bpy.context.temp_override(**C.view3d_override()):
        bpy.ops.object.mode_set(mode="EDIT")
        bpy.ops.mesh.select_all(action="SELECT")
        bpy.ops.uv.smart_project(angle_limit=math.radians(70), island_margin=margin)
        # le parti decimate (cinghie) fanno centinaia di isole minuscole: si reimpacchetta
        bpy.ops.uv.pack_islands(rotate=True, margin=margin)
        bpy.ops.object.mode_set(mode="OBJECT")


def _bake(high, low, image, kind, extrusion, distance, samples):
    mat = low.data.materials[0]
    nt = mat.node_tree
    tex = next(n for n in nt.nodes if n.type == "TEX_IMAGE")
    tex.image = image
    nt.nodes.active = tex
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = samples
    scene.render.bake.margin = 6
    C.select_only(high + [low], active=low)
    with bpy.context.temp_override(active_object=low, object=low, selected_objects=high + [low],
                                   **C.view3d_override()):
        bpy.ops.object.bake(type=kind, use_selected_to_active=True, cage_extrusion=extrusion,
                            max_ray_distance=distance, use_clear=True, margin=6)
    px = np.empty(image.size[0] * image.size[1] * 4, dtype=np.float32)
    image.pixels.foreach_get(px)
    return px.reshape(image.size[1], image.size[0], 4)[..., :3].astype(np.float64)


def bake_color(high, low, size=1024, extrusion=0.012, distance=0.05, ao=True):
    """Colore base (e occlusione) del modello pesante sulle UV nuove del leggero. Ritorna array
    HxWx3 (sRGB) e HxW (occlusione, 1 = libero)."""
    for o in high:
        for m in o.data.materials:
            _emission_from_base_color(m)
    unwrap(low)
    mat = bpy.data.materials.new("bake_target")
    mat.use_nodes = True
    mat.node_tree.nodes.new("ShaderNodeTexImage")
    low.data.materials.clear()
    low.data.materials.append(mat)
    color_img = bpy.data.images.new("bake_color", size, size, alpha=False)
    color = _bake(high, low, color_img, "EMIT", extrusion, distance, 1)
    occlusion = np.ones(color.shape[:2])
    if ao:
        ao_img = bpy.data.images.new("bake_ao", size, size, alpha=False, float_buffer=True)
        ao_img.colorspace_settings.name = "Non-Color"
        occlusion = _bake(high, low, ao_img, "AO", extrusion, distance, 16)[..., 0]
    return color, occlusion


# =============================================================================================
# 5. Stile del gioco
# =============================================================================================

def stylize(color, occlusion, out_size=256, saturation=0.82, value=0.92, contrast=1.05,
            ao_strength=0.35, levels=32):
    """Dalla texture originale cotta a quella del gioco: riduzione a `out_size` (media di blocchi,
    niente reinterpretazione), saturazione e luminosita' un po' giu' (palette spenta della Zona),
    contrasto appena su, occlusione leggera, 5 bit per canale con dithering ordinato."""
    img = C.resize_area(color, out_size)
    occ = C.resize_area(occlusion[..., None], out_size)[..., 0]
    grey = img.mean(axis=2, keepdims=True)
    img = grey + (img - grey) * saturation
    img = (img - 0.5) * contrast + 0.5
    img = img * value
    img = img * (1 - ao_strength * (1 - np.clip(occ, 0, 1)))[..., None]
    return C.psx(np.clip(img, 0, 1), levels)


def finish_material(low, name, rgb):
    image = C.make_image(name, rgb)
    low.data.materials.clear()
    low.data.materials.append(C.psx_material(name, image))
    return image


# =============================================================================================
# Confronto prima/dopo
# =============================================================================================

def compare_render(high, low, path, directions=((0.7, -0.7, 0.3), (-0.7, 0.7, 0.3))):
    """Render affiancati del sorgente e del risultato dalle stesse viste: si controlla che forma e
    colori siano quelli dell'originale."""
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_EEVEE"
    scene.render.resolution_x = scene.render.resolution_y = 420
    cam_data = bpy.data.cameras.new("cmp")
    cam_data.type = "ORTHO"
    cam = bpy.data.objects.new("cmp", cam_data)
    scene.collection.objects.link(cam)
    scene.camera = cam
    for rot, e in (((50, 0, -30), 3.0), ((60, 0, 160), 1.5)):
        s = bpy.data.objects.new("l", bpy.data.lights.new("l", "SUN"))
        scene.collection.objects.link(s)
        s.rotation_euler = [math.radians(a) for a in rot]
        s.data.energy = e
    world = bpy.data.worlds.new("w")
    scene.world = world
    world.color = (0.5, 0.5, 0.52)
    lo, hi = bounds(high)
    center = Vector(((lo + hi) / 2).tolist())
    span = float(max(hi - lo)) * 1.3
    cam_data.ortho_scale = span
    tiles = []
    for group in (high, [low]):
        for o in high + [low]:
            o.hide_render = o not in group
        for d in directions:
            d = Vector(d).normalized()
            cam.location = center + d * 4
            cam.rotation_euler = (-d).to_track_quat("-Z", "Y").to_euler()
            tmp = path + f".tmp{len(tiles)}.png"
            scene.render.filepath = tmp
            bpy.ops.render.render(write_still=True)
            tiles.append(C.load_image_array(tmp)[..., :3])
            os.remove(tmp)
    for o in high + [low]:
        o.hide_render = False
    h, w = tiles[0].shape[:2]
    n = len(directions)
    sheet = np.zeros((2 * h, n * w, 3))
    for k, t in enumerate(tiles):
        r, c = divmod(k, n)
        sheet[(1 - r) * h:(2 - r) * h, c * w:(c + 1) * w] = t     # sopra il sorgente, sotto il risultato
    C.make_image("compare", sheet, path)
    bpy.data.objects.remove(cam)
