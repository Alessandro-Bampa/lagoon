"""Libreria condivisa del personaggio: corpo base (`build_character_base.py`) e capi indossabili
(`wearables/build_wearable.py`). Nessun codice a livello di modulo: si importa e basta.

Contiene cio' che corpo e capi devono avere IDENTICO, altrimenti un capo non combacia col corpo:
nomi degli ossi, regioni del corpo, percorsi, rasterizzazione in spazio UV e la resa PSX delle texture.

ASSI (come gli altri script): Blender -Y = davanti del personaggio = +Z di Godot, +X di Blender =
sinistra del personaggio = +X di Godot, +Z = su. Unita' in metri, origine ai piedi.
"""

import math
import os

import bpy
import numpy as np
from mathutils import Matrix, Vector

# ---------------------------------------------------------------------------------------------
# Percorsi
# ---------------------------------------------------------------------------------------------

BODY_GLB = "assets/models/characters/Character_Base.glb"
BODY_FP_GLB = "assets/models/characters/Character_Base_FP.glb"
MANNEQUIN_GLB = "assets/models/characters/Mannequin.glb"
WEARABLES_DIR = "assets/models/wearables"

# ---------------------------------------------------------------------------------------------
# Regioni del corpo. L'ORDINE e' quello di `BodyRegion` in C# (wearables/scripts/BodyRegion.cs):
# il colore di vertice R di ogni mesh del personaggio vale indice/255. Si appende in coda.
# ---------------------------------------------------------------------------------------------

REGIONS = ["Scalp", "Face", "Jaw", "Neck", "Chest", "Abdomen", "Pelvis",
           "UpperArm", "Forearm", "Hand", "Thigh", "Calf", "Ankle", "Foot"]
REGION = {name: i for i, name in enumerate(REGIONS)}

# Osso dominante -> regione (le regioni della testa e della gamba bassa si rifiniscono con la
# geometria, vedi `classify_regions`).
BONE_REGION = {
    "Head": "Scalp", "neck_01": "Neck",
    "spine_03": "Chest", "clavicle_l": "Chest", "clavicle_r": "Chest",
    "spine_02": "Abdomen", "spine_01": "Abdomen",
    "pelvis": "Pelvis", "root": "Pelvis",
    "upperarm_l": "UpperArm", "upperarm_r": "UpperArm",
    "lowerarm_l": "Forearm", "lowerarm_r": "Forearm",
    "thigh_l": "Thigh", "thigh_r": "Thigh",
    "calf_l": "Calf", "calf_r": "Calf",
    "foot_l": "Foot", "foot_r": "Foot", "ball_l": "Foot", "ball_r": "Foot",
}
HAND_PREFIXES = ("hand_", "thumb_", "index_", "middle_", "ring_", "pinky_")


def region_of_bone(name):
    if name.startswith(HAND_PREFIXES):
        return "Hand"
    return BONE_REGION.get(name, "Chest")


def project_path(project_dir, rel):
    return os.path.join(project_dir, rel).replace("\\", "/")


# ---------------------------------------------------------------------------------------------
# Scena e oggetti
# ---------------------------------------------------------------------------------------------

def reset_scene():
    bpy.ops.wm.read_homefile(use_empty=True)
    scene = bpy.context.scene
    scene.unit_settings.system = "METRIC"
    scene.unit_settings.scale_length = 1.0
    return scene


def view3d_override():
    """Contesto per operatori con poll() quando lo script gira da un timer (estensione MCP).
    In background non c'e' finestra: si restituisce un dict vuoto e gli operatori usati qui
    funzionano lo stesso."""
    wm = bpy.context.window_manager
    for window in wm.windows if wm else []:
        for area in window.screen.areas:
            if area.type == "VIEW_3D":
                return {"window": window, "area": area}
    return {}


def select_only(objects, active=None):
    # dopo bpy.data.objects.remove il view layer ha voci vuote finche' non si aggiorna
    bpy.context.view_layer.update()
    for o in bpy.context.view_layer.objects:
        if o is not None:
            o.select_set(False)
    for o in objects:
        o.select_set(True)
    bpy.context.view_layer.objects.active = active or (objects[0] if objects else None)


def import_glb(path):
    before = set(bpy.data.objects)
    with bpy.context.temp_override(**view3d_override()):
        bpy.ops.import_scene.gltf(filepath=path)
    return [o for o in bpy.data.objects if o not in before]


def export_glb(path, objects, animations=False):
    """Esporta solo `objects`. Colore di vertice ATTIVO sempre esportato: porta le regioni."""
    select_only(objects)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with bpy.context.temp_override(**view3d_override()):
        bpy.ops.export_scene.gltf(
            filepath=path, export_format="GLB", use_selection=True,
            export_yup=True, export_apply=False, export_skins=True,
            export_def_bones=False, export_animations=animations,
            export_animation_mode="ACTIONS", export_optimize_animation_size=True,
            export_vertex_color="ACTIVE", export_all_vertex_colors=False,
            export_image_format="AUTO", export_materials="EXPORT")


def mesh_world_coords(obj):
    m = obj.matrix_world
    return np.array([tuple(m @ v.co) for v in obj.data.vertices], dtype=np.float64)


# ---------------------------------------------------------------------------------------------
# Pesi
# ---------------------------------------------------------------------------------------------

def vertex_weights(obj):
    """Lista per vertice di [(nome gruppo, peso)] ordinata per peso decrescente."""
    names = {g.index: g.name for g in obj.vertex_groups}
    out = []
    for v in obj.data.vertices:
        ws = sorted(((names[g.group], g.weight) for g in v.groups if g.weight > 1e-5),
                    key=lambda t: -t[1])
        out.append(ws)
    return out


def limit_and_normalize(obj, limit=4, keep=None):
    """Massimo `limit` influenze per vertice, somma 1. `keep`: nomi di gruppo deformanti
    (gli altri gruppi, es. di servizio, non contano e restano intatti)."""
    groups = {g.index: g for g in obj.vertex_groups}
    for v in obj.data.vertices:
        ws = [(g.group, g.weight) for g in v.groups
              if keep is None or groups[g.group].name in keep]
        ws.sort(key=lambda t: -t[1])
        drop, kept = ws[limit:], [w for w in ws[:limit] if w[1] > 1e-4]
        for gi, _ in drop:
            groups[gi].remove([v.index])
        for gi, w in ws[:limit]:
            if w <= 1e-4:
                groups[gi].remove([v.index])
        total = sum(w for _, w in kept)
        if total > 0:
            for gi, w in kept:
                groups[gi].add([v.index], w / total, "REPLACE")


def dominant_bone(weights):
    return weights[0][0] if weights else None


# ---------------------------------------------------------------------------------------------
# Colore di vertice delle regioni
# ---------------------------------------------------------------------------------------------

def write_region_colors(obj, regions, name="Region"):
    """`regions`: indice di regione per vertice. Attributo colore per punto, byte, attivo."""
    me = obj.data
    for a in list(me.color_attributes):
        me.color_attributes.remove(a)
    attr = me.color_attributes.new(name=name, type="BYTE_COLOR", domain="POINT")
    cols = np.zeros((len(me.vertices), 4), dtype=np.float32)
    cols[:, 0] = np.asarray(regions, dtype=np.float32) / 255.0
    cols[:, 3] = 1.0
    attr.data.foreach_set("color", cols.ravel())
    me.color_attributes.active_color = attr
    me.color_attributes.render_color_index = 0
    return attr


def read_region_colors(obj, name="Region"):
    me = obj.data
    # reimportato da glTF l'attributo si chiama "Color"/"Col": si ripiega sul primo
    attr = me.color_attributes.get(name) or (me.color_attributes[0] if len(me.color_attributes) else None)
    if attr is None:
        return None
    cols = np.zeros(len(attr.data) * 4, dtype=np.float32)
    attr.data.foreach_get("color", cols)
    # `color` e' gia' il valore lineare scritto (indice/255), anche per BYTE_COLOR; il byte sRGB
    # interno lo sporca di qualche decimo: si arrotonda. Lo stesso valore arriva a Godot in COLOR.
    r = cols.reshape(-1, 4)[:, 0]
    idx = np.rint(r * 255).astype(int)
    if attr.domain == "CORNER":         # il glTF reimportato lo mette per angolo
        per_vertex = np.zeros(len(me.vertices), dtype=int)
        lv = np.zeros(len(me.loops), dtype=int)
        me.loops.foreach_get("vertex_index", lv)
        per_vertex[lv] = idx
        return per_vertex
    return idx


# ---------------------------------------------------------------------------------------------
# Rasterizzazione in spazio UV: "position map" (posizione 3D e normale per texel)
# ---------------------------------------------------------------------------------------------

def position_map(obj, size, uv_name=None, dilate=4, extra=None):
    """Rasterizza i triangoli di `obj` (gia' triangolato o no) nella griglia UV `size`x`size`.
    Restituisce (pos HxWx3, nrm HxWx3, mask HxW bool, extra HxW) in coordinate MONDO.
    `extra`: valore per vertice da interpolare (es. regione, preso dal vertice piu' pesante).
    Il bordo delle isole viene allargato di `dilate` texel: niente cuciture col filtro nearest."""
    me = obj.data
    me.calc_loop_triangles()
    uv_layer = me.uv_layers[uv_name] if uv_name else me.uv_layers.active
    mw = obj.matrix_world
    nmw = mw.to_3x3().inverted().transposed()
    vco = np.array([tuple(mw @ v.co) for v in me.vertices])
    vno = np.array([tuple((nmw @ v.normal).normalized()) for v in me.vertices])
    uvs = np.zeros(len(me.loops) * 2)
    uv_layer.data.foreach_get("uv", uvs)
    uvs = uvs.reshape(-1, 2)
    pos = np.zeros((size, size, 3))
    nrm = np.zeros((size, size, 3))
    ext = np.full((size, size), -1, dtype=np.int32)
    mask = np.zeros((size, size), dtype=bool)
    for tri in me.loop_triangles:
        lo = tri.loops
        vi = tri.vertices
        p = uvs[list(lo)] * size - 0.5          # centri dei texel
        x0, x1 = int(max(0, math.floor(p[:, 0].min()))), int(min(size - 1, math.ceil(p[:, 0].max())))
        y0, y1 = int(max(0, math.floor(p[:, 1].min()))), int(min(size - 1, math.ceil(p[:, 1].max())))
        if x1 < x0 or y1 < y0:
            continue
        xs, ys = np.meshgrid(np.arange(x0, x1 + 1), np.arange(y0, y1 + 1))
        (ax, ay), (bx, by), (cx, cy) = p
        den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy)
        if abs(den) < 1e-12:
            continue
        w0 = ((by - cy) * (xs - cx) + (cx - bx) * (ys - cy)) / den
        w1 = ((cy - ay) * (xs - cx) + (ax - cx) * (ys - cy)) / den
        w2 = 1 - w0 - w1
        inside = (w0 >= -1e-4) & (w1 >= -1e-4) & (w2 >= -1e-4)
        if not inside.any():
            continue
        yy, xx = ys[inside], xs[inside]
        bw = np.stack([w0[inside], w1[inside], w2[inside]], axis=1)
        pos[yy, xx] = bw @ vco[list(vi)]
        nrm[yy, xx] = bw @ vno[list(vi)]
        if extra is not None:
            e = np.array([extra[i] for i in vi])
            ext[yy, xx] = e[np.argmax(bw, axis=1)]
        mask[yy, xx] = True
    # allargamento del bordo
    for _ in range(dilate):
        grow = ~mask
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            src = np.roll(np.roll(mask, dy, 0), dx, 1) & grow
            if not src.any():
                continue
            pos[src] = np.roll(np.roll(pos, dy, 0), dx, 1)[src]
            nrm[src] = np.roll(np.roll(nrm, dy, 0), dx, 1)[src]
            ext[src] = np.roll(np.roll(ext, dy, 0), dx, 1)[src]
            mask = mask | src
            grow = ~mask
    # righe in basso = v piccolo: l'immagine di Blender parte dal basso, come qui.
    return pos, nrm, mask, ext


# ---------------------------------------------------------------------------------------------
# Texture: rumore, resa PSX, I/O (stesse ricette di build_venice_kit.py)
# ---------------------------------------------------------------------------------------------

BAYER = (np.array([[0, 8, 2, 10], [12, 4, 14, 6], [3, 11, 1, 9], [15, 7, 13, 5]]) + 0.5) / 16 - 0.5


def vnoise(w, h, cells_x, cells_y, seed):
    rng = np.random.default_rng(seed)
    g = rng.random((cells_y, cells_x))
    xs = np.arange(w) * cells_x / w
    ys = np.arange(h) * cells_y / h
    x0 = np.floor(xs).astype(int)
    y0 = np.floor(ys).astype(int)
    fx = xs - x0
    fy = ys - y0
    fx = fx * fx * (3 - 2 * fx)
    fy = fy * fy * (3 - 2 * fy)
    x1 = (x0 + 1) % cells_x
    y1 = (y0 + 1) % cells_y
    a = g[np.ix_(y0, x0)]
    b = g[np.ix_(y0, x1)]
    c = g[np.ix_(y1, x0)]
    d = g[np.ix_(y1, x1)]
    top = a + (b - a) * fx[None, :]
    bot = c + (d - c) * fx[None, :]
    return top + (bot - top) * fy[:, None]


def fbm(w, h, cx, cy, seed, octaves=3):
    total = np.zeros((h, w))
    amp, norm = 1.0, 0.0
    for o in range(octaves):
        total += amp * vnoise(w, h, min(w, cx * 2 ** o), min(h, cy * 2 ** o), seed + o * 17)
        norm += amp
        amp *= 0.5
    return total / norm


def psx(img, levels=32):
    """5 bit per canale con dithering ordinato: la grana delle texture PSX."""
    h, w = img.shape[:2]
    t = np.tile(BAYER, (h // 4 + 1, w // 4 + 1))[:h, :w, None]
    return np.clip(np.floor(img * (levels - 1) + 0.5 + t * 0.9) / (levels - 1), 0, 1)


def wear(img, seed, amount=0.5):
    """Sporco leggero da Zona (versione morbida di `grime()` del kit veneziano): toglie un po'
    di colore e scurisce a chiazze. Per corpo e capi: niente colature ne' muffa."""
    h, w = img.shape[:2]
    grey = img.mean(axis=2, keepdims=True)
    img = img * (1 - 0.25 * amount) + grey * 0.25 * amount
    blot = np.clip((fbm(w, h, max(2, w // 24), max(2, h // 24), seed, 5) - 0.5) * 3, 0, 1)
    return img * (1 - 0.30 * amount * blot)[..., None]


def blend(img, color, alpha):
    """Mescola un colore (3,) o un'immagine (H,W,3) con opacita' per texel (H,W)."""
    color = np.asarray(color, dtype=np.float64)
    if color.ndim == 1:
        color = color[None, None, :]
    a = np.clip(alpha, 0, 1)[..., None]
    return img * (1 - a) + color * a


def srgb_to_linear(c):
    c = np.asarray(c, dtype=np.float64)
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def load_image_array(path):
    """PNG -> array HxWx4 float in sRGB (righe dal basso, come Blender)."""
    img = bpy.data.images.load(path, check_existing=False)
    img.colorspace_settings.name = "sRGB"
    w, h = img.size
    px = np.empty(w * h * 4, dtype=np.float32)
    img.pixels.foreach_get(px)
    bpy.data.images.remove(img)
    return px.reshape(h, w, 4)


def resize_area(arr, size):
    """Riduzione a media di blocchi (fattore intero) a `size`x`size`."""
    h, w = arr.shape[:2]
    fy, fx = h // size, w // size
    arr = arr[:fy * size, :fx * size]
    return arr.reshape(size, fy, size, fx, -1).mean(axis=(1, 3))


def make_image(name, rgb, path=None):
    """Immagine Blender da array HxWx3 sRGB in [0,1]. Con `path` la salva anche su disco (PNG)."""
    h, w = rgb.shape[:2]
    img = bpy.data.images.new(name, w, h, alpha=False)
    rgba = np.ones((h, w, 4), dtype=np.float32)
    rgba[..., :3] = np.clip(rgb, 0, 1)
    img.pixels.foreach_set(rgba.ravel())
    if path:
        img.filepath_raw = path
        img.file_format = "PNG"
        img.save()
    img.pack()
    return img


def psx_material(name, image):
    """Materiale per l'export: solo la texture, filtro nearest, nessun riflesso. In Godot lo
    sostituisce lo shader del personaggio, ma l'import deve gia' avere texture e nome giusti."""
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nt = mat.node_tree
    bsdf = nt.nodes["Principled BSDF"]
    tex = nt.nodes.new("ShaderNodeTexImage")
    tex.image = image
    tex.interpolation = "Closest"
    nt.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
    bsdf.inputs["Roughness"].default_value = 1.0
    bsdf.inputs["Metallic"].default_value = 0.0
    if "Specular IOR Level" in bsdf.inputs:
        bsdf.inputs["Specular IOR Level"].default_value = 0.0
    return mat


# ---------------------------------------------------------------------------------------------
# Allineamento di vettori
# ---------------------------------------------------------------------------------------------

def frame_from(a, b):
    """Terna ortonormale dalla direzione `a` e da un secondo vettore `b` non parallelo."""
    x = a.normalized()
    z = x.cross(b).normalized()
    y = z.cross(x)
    return Matrix((x, y, z)).transposed()


def rotation_between(cur_a, tgt_a, cur_b=None, tgt_b=None):
    """Rotazione (3x3) che porta cur_a su tgt_a; con i vettori secondari anche il rollio."""
    if cur_b is None:
        return cur_a.rotation_difference(tgt_a).to_matrix()
    return frame_from(tgt_a, tgt_b) @ frame_from(cur_a, cur_b).inverted()
