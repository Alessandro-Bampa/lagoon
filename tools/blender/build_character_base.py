"""Corpo base del personaggio: uomo di 1,80 m in boxer, sullo scheletro della Universal Animation
Library (UAL), con le 43 clip del manichino riesportate. Sostituisce `Mannequin.glb`.

Richiede l'estensione MPFB2 (MakeHuman per Blender) con il pacchetto `makehuman_system_assets`
(asset CC0): vedi la skill `character-wearables` §1. Lo script RESETTA la scena, quindi gira in un
Blender separato, in background:

    "<blender>" --background --python tools/blender/build_character_base.py -- [preview] [noexport]

Uscite:
  assets/models/characters/Character_Base.glb   armatura UAL ("Armature") + mesh "Body" + clip
  %TEMP%/character_base/Character_Base.png      atlante del corpo (copia di quello incorporato)
  %TEMP%/character_base/*.png                   render di controllo (con `preview`)

Passi:
  1. MPFB: maschio, caucasico, muscolatura e peso medi; proxy `male_generic`, occhi low-poly,
     rig `game_engine` (nomi degli ossi alla Unreal: gli stessi di UAL).
  2. Il rig MakeHuman viene messo in posa perche' ogni segmento (giunto -> giunto figlio) abbia la
     direzione del corrispondente segmento UAL a riposo: braccia orizzontali, palmi in giu', dita
     tese. La posa si applica alla mesh, che diventa il nuovo riposo.
  3. Scala uniforme a 1,80 m esatti (pianta del piede -> vertice del cranio).
  4. Lo scheletro UAL (da Mannequin.glb, con le sue azioni) prende le POSIZIONI dei giunti
     MakeHuman tenendo direzione e rollio dei propri ossi: le clip UAL sono rotazioni locali e
     restano valide. Gli ossi foglia (`*_04_leaf`, `ball_leaf`) vanno sulle code MakeHuman.
  5. Decimazione (testa e mani protette), pesi a 4 influenze, regioni nel colore di vertice.
  6. Atlante 512 px: pelle MakeHuman ridotta, boxer, capelli rasati, barba corta, sopracciglia,
     occhi nell'angolo libero; sporco leggero e grana PSX.

CONVENZIONI che i capi indossabili (wearables/) danno per scontate: nomi `Armature`/`Body`,
colore di vertice `Region` (indice di `charlib.REGIONS` / 255), riposo identico a questo file.
"""

import math
import os
import sys

import bpy
import bmesh
import numpy as np
from mathutils import Matrix, Vector

try:
    PROJECT_DIR  # iniettato da blender_client.py
except NameError:
    PROJECT_DIR = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))).replace("\\", "/")
try:
    ARGV
except NameError:
    ARGV = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
if PROJECT_DIR + "/tools/blender" not in sys.path:
    sys.path.append(PROJECT_DIR + "/tools/blender")

import charlib as C  # noqa: E402

PREVIEW = "preview" in ARGV
EXPORT = "noexport" not in ARGV
PREVIEW_DIR = os.path.join(os.environ.get("TEMP", "/tmp"), "character_base")

HEIGHT = 1.80
ATLAS = 512
DECIMATE_RATIO = 0.30      # 27k -> ~8k triangoli

MACRO = dict(gender=1.0, age=0.5, muscle=0.68, weight=0.58, proportions=0.7, height=0.535,
             race={"asian": 0.0, "caucasian": 1.0, "african": 0.0})
PROXY = "male_generic"
SKIN = "skins/young_caucasian_male/young_lightskinned_male_diffuse.png"
EYE_TEX = "eyes/materials/brown_eye.png"

# Colori (sRGB)
BOXER = np.array([0.20, 0.22, 0.20])
BOXER_BAND = np.array([0.55, 0.55, 0.52])
HAIR = np.array([0.13, 0.10, 0.08])

# Segmento che definisce la direzione di ogni osso MakeHuman: (osso, figlio, vettore secondario).
# Il figlio "tail" = coda dell'osso stesso (dita finali, punta del piede).
# Solo gli ARTI: bacino, colonna, collo e clavicole restano nella postura MakeHuman. Nell'UAL i giunti
# del busto sono collocati diversamente (la clavicola parte 7 cm davanti al petto e va indietro di
# 14 cm): forzarne le direzioni spingerebbe le spalle dietro la schiena. Conta che gli arti stiano
# nella T-pose UAL; per il busto bastano gli assi degli ossi, che restano quelli UAL.
CHAIN = []
for s in ("l", "r"):
    CHAIN += [
        (f"upperarm_{s}", f"lowerarm_{s}", None),
        (f"lowerarm_{s}", f"hand_{s}", None),
        (f"hand_{s}", f"middle_01_{s}", (f"index_01_{s}", f"pinky_01_{s}")),
    ]
    for f in ("thumb", "index", "middle", "ring", "pinky"):
        CHAIN += [(f"{f}_01_{s}", f"{f}_02_{s}", None),
                  (f"{f}_02_{s}", f"{f}_03_{s}", None),
                  (f"{f}_03_{s}", "tail", None)]
    CHAIN += [
        (f"thigh_{s}", f"calf_{s}", None),
        (f"calf_{s}", f"foot_{s}", None),
        (f"foot_{s}", f"ball_{s}", None),
        (f"ball_{s}", "tail", None),
    ]


def ual_name(mh):
    return {"head": "Head", "Root": "root"}.get(mh, mh)


def ual_child_head(ual_bones, mh_bone, mh_child):
    """Testa del figlio UAL corrispondente; per 'tail' l'osso foglia UAL."""
    if mh_child != "tail":
        return ual_bones[ual_name(mh_child)]
    if mh_bone.startswith("ball_"):
        return ual_bones["ball_leaf_" + mh_bone[-1]]
    finger = mh_bone.split("_")[0]
    return ual_bones[f"{finger}_04_leaf_{mh_bone[-1]}"]


# =============================================================================================
# 1. MakeHuman
# =============================================================================================

def make_human():
    import addon_utils
    addon_utils.enable("bl_ext.user_default.mpfb", default_set=True)
    from bl_ext.user_default.mpfb.services.humanservice import HumanService
    from bl_ext.user_default.mpfb.services.targetservice import TargetService
    from bl_ext.user_default.mpfb.services.locationservice import LocationService

    data = LocationService.get_user_data()
    macro = TargetService.get_default_macro_info_dict()
    macro.update({k: v for k, v in MACRO.items()})
    basemesh = HumanService.create_human(macro_detail_dict=macro)
    rig = HumanService.add_builtin_rig(basemesh, "game_engine")
    body = HumanService.add_mhclo_asset(os.path.join(data, f"proxymeshes/{PROXY}/{PROXY}.proxy"),
                                        basemesh, asset_type="Proxymeshes", material_type="NONE")
    eyes = HumanService.add_mhclo_asset(os.path.join(data, "eyes/low-poly/low-poly.mhclo"),
                                        basemesh, asset_type="Eyes", material_type="NONE")
    for o in (body, eyes):
        for m in list(o.modifiers):
            if m.type != "ARMATURE":
                o.modifiers.remove(m)
        for m in o.modifiers:
            m.object = rig
    return data, basemesh, rig, body, eyes


# =============================================================================================
# 2. Posa MakeHuman -> direzioni UAL
# =============================================================================================

def ual_rest_heads(ual):
    return {b.name: ual.matrix_world @ b.head_local for b in ual.data.bones}


def pose_to_ual(rig, ual_heads):
    """Ruota ogni osso MakeHuman (dal bacino alle dita) perche' il segmento giunto -> giunto figlio
    abbia la direzione UAL. Lavora in spazio armatura, aggiornando i figli dopo ogni osso."""
    assert rig.matrix_world == Matrix.Identity(4) or rig.matrix_world.to_3x3() == Matrix.Identity(3)
    bpy.context.view_layer.objects.active = rig
    pb = rig.pose.bones
    for bone, child, second in CHAIN:
        bpy.context.view_layer.update()
        p = pb[bone]
        head = p.head.copy()
        cur = (p.tail if child == "tail" else pb[child].head) - head
        tgt = ual_child_head(ual_heads, bone, child) - ual_heads[ual_name(bone)]
        if second:
            a, b = second
            cur_b = pb[a].head - pb[b].head
            tgt_b = ual_heads[ual_name(a)] - ual_heads[ual_name(b)]
            rot = C.rotation_between(cur, tgt, cur_b, tgt_b)
        else:
            rot = C.rotation_between(cur, tgt)
        m = Matrix.Translation(head) @ rot.to_4x4() @ Matrix.Translation(-head)
        p.matrix = m @ p.matrix
    bpy.context.view_layer.update()


def apply_pose(obj):
    """Applica il modificatore armatura: la posa diventa la forma a riposo della mesh."""
    C.select_only([obj])
    for m in list(obj.modifiers):
        if m.type == "ARMATURE":
            with bpy.context.temp_override(object=obj, active_object=obj, **C.view3d_override()):
                bpy.ops.object.modifier_apply(modifier=m.name)


# =============================================================================================
# 4. Scheletro UAL sui giunti MakeHuman
# =============================================================================================

def fit_ual_skeleton(ual, mh_joints, mh_tails):
    """mh_joints/mh_tails: nome UAL -> posizione mondo. Direzione e rollio UAL invariati."""
    C.select_only([ual])
    bpy.ops.object.mode_set(mode="EDIT")
    inv = ual.matrix_world.inverted()
    ebs = ual.data.edit_bones
    for eb in ebs:
        eb.use_connect = False
    targets = {}
    for eb in ebs:
        if eb.name in mh_joints:
            targets[eb.name] = inv @ mh_joints[eb.name]
        elif eb.name.endswith(("_04_leaf_l", "_04_leaf_r")):
            finger, side = eb.name.split("_")[0], eb.name[-1]
            targets[eb.name] = inv @ mh_tails[f"{finger}_03_{side}"]
        elif eb.name.startswith("ball_leaf_"):
            targets[eb.name] = inv @ mh_tails["ball_" + eb.name[-1]]
    for eb in ebs:
        if eb.name not in targets:
            continue
        d = (eb.tail - eb.head)
        length = d.length
        # lunghezza: fino al giunto figlio corrispondente, se c'e'; altrimenti in scala
        kids = [k for k in eb.children if k.name in targets]
        new_head = targets[eb.name]
        if kids:
            length = max(0.01, min((targets[k.name] - new_head).length for k in kids))
        roll = eb.roll
        eb.head = new_head
        eb.tail = new_head + d.normalized() * length
        eb.roll = roll
    bpy.ops.object.mode_set(mode="OBJECT")


# =============================================================================================
# 5. Mesh: decimazione, pesi, regioni
# =============================================================================================

def decimate(body, iterations):
    """Un-subdivide: il proxy ha la topologia a quad del basemesh MakeHuman, quindi togliere un
    loop su due dimezza la risoluzione tenendo quad, UV (i vertici superstiti sono originali) e
    simmetria. La decimazione "collapse" su questa mesh appiattisce il busto."""
    mod = body.modifiers.new("Decimate", "DECIMATE")
    mod.decimate_type = "COLLAPSE"
    mod.ratio = iterations
    with bpy.context.temp_override(object=body, active_object=body, **C.view3d_override()):
        bpy.ops.object.modifier_apply(modifier=mod.name)
    return sum(len(p.vertices) - 2 for p in body.data.polygons)


def classify_regions(body, joints, eye_z):
    """Regione per vertice: osso dominante, poi geometria per testa, collo e caviglia."""
    wts = C.vertex_weights(body)
    head = joints["Head"]
    out = []
    for v, ws in zip(body.data.vertices, wts):
        p = body.matrix_world @ v.co
        reg = C.region_of_bone(C.dominant_bone(ws) or "pelvis")
        if reg in ("Scalp", "Neck") and p.z > head.z - 0.02:
            front = p.y < head.y - 0.045            # davanti = -Y
            if front and abs(p.z - eye_z) < 0.032:
                reg = "Face"
            elif front and p.z < eye_z - 0.032:
                reg = "Jaw"
            elif p.z < eye_z - 0.075:
                reg = "Neck" if not front else "Jaw"
            else:
                reg = "Scalp"
        elif reg == "Calf":
            ankle = joints["foot_l"].z
            if p.z < ankle + 0.10:
                reg = "Ankle"
        elif reg == "Foot":
            ankle = joints["foot_l"].z
            if p.z > ankle + 0.02:
                reg = "Ankle"
        out.append(C.REGION[reg])
    return out


# =============================================================================================
# 6. Atlante
# =============================================================================================

def free_square(mask, size):
    """Primo quadrato `size` libero (nessun texel coperto) cercando dagli angoli."""
    n = mask.shape[0]
    for y0, x0 in ((n - size, n - size), (0, n - size), (n - size, 0), (0, 0)):
        if not mask[y0:y0 + size, x0:x0 + size].any():
            return y0, x0
    # altrimenti la cella meno occupata di una griglia
    best = None
    for y0 in range(0, n - size + 1, size // 2):
        for x0 in range(0, n - size + 1, size // 2):
            c = mask[y0:y0 + size, x0:x0 + size].sum()
            if best is None or c < best[0]:
                best = (c, y0, x0)
    return best[1], best[2]


def build_atlas(body, eyes, data, joints, eye_z, regions):
    skin = C.load_image_array(os.path.join(data, SKIN))[..., :3]
    img = C.resize_area(skin, ATLAS).astype(np.float64)
    pos, nrm, mask, reg = C.position_map(body, ATLAS, extra=regions)
    if "debugmap" in ARGV:
        zz = (pos[..., 2] - 1.4) / 0.4
        dbg = np.stack([np.clip(zz, 0, 1), (reg % 5) / 4.0, mask * 0.5], axis=-1)
        C.make_image("dbg", dbg, os.path.join(PREVIEW_DIR, "posmap.png"))
        C.make_image("dbgskin", C.resize_area(skin, ATLAS), os.path.join(PREVIEW_DIR, "skin.png"))
    head = joints["Head"]
    rng = np.random.default_rng(7)
    noise = C.fbm(ATLAS, ATLAS, 64, 64, 11, 3)
    grain = rng.random((ATLAS, ATLAS))
    z, y, x = pos[..., 2], pos[..., 1], pos[..., 0]

    # boxer: dal sopra-anca a meta' coscia alta, elastico in vita
    pelvis_z = joints["pelvis"].z
    waist = pelvis_z + 0.075
    thigh_cut = joints["thigh_l"].z - 0.17
    leg = (np.abs(x) > 0.02)
    boxer = mask & (z < waist) & (z > thigh_cut - 0.02 * leg)
    boxer &= (reg != C.REGION["Hand"]) & (reg != C.REGION["Forearm"]) & (reg != C.REGION["UpperArm"])
    shade = 0.85 + 0.25 * noise
    img = C.blend(img, BOXER[None, None, :] * shade[..., None], boxer.astype(float))
    band = boxer & (z > waist - 0.035)
    img = C.blend(img, BOXER_BAND[None, None, :] * shade[..., None], band.astype(float))
    hem = boxer & (z < thigh_cut + 0.012)
    img = C.blend(img, BOXER * 0.6, hem.astype(float))

    # capelli rasati: calotta sopra la fronte, nuca piu' bassa, tempie scoperte sopra l'orecchio
    is_head = mask & ((reg == C.REGION["Scalp"]) | (reg == C.REGION["Face"]) | (reg == C.REGION["Neck"]))
    rel_y = y - head.y
    # attaccatura: 6,5 cm sopra gli occhi sulla fronte, scende verso la nuca; orecchie scoperte
    hairline = eye_z + 0.065 - np.clip(rel_y + 0.02, 0, 0.2) * 1.1
    ears = (np.abs(x) > 0.068) & (z < eye_z + 0.035) & (rel_y > -0.04) & (rel_y < 0.05)
    hair = is_head & (z > hairline) & ~ears & (z > eye_z - 0.075)
    hair_a = hair * (0.55 + 0.35 * (grain > 0.35))
    img = C.blend(img, HAIR, hair_a)

    # barba corta: mandibola e labbro superiore
    jaw = mask & (reg == C.REGION["Jaw"]) & (z < eye_z - 0.065)
    lips = jaw & (np.abs(x) < 0.025) & (z > eye_z - 0.105) & (z < eye_z - 0.085) & (rel_y < -0.09)
    stubble = jaw & ~lips & (grain > 0.55)
    img = C.blend(img, HAIR * 1.2, stubble * 0.45)

    # sopracciglia
    arch = np.abs(x) * 0.06
    brow = mask & (z > eye_z + 0.020 + arch) & (z < eye_z + 0.026 + arch) \
        & (np.abs(x) > 0.016) & (np.abs(x) < 0.048) & (rel_y < -0.07) & ~hair
    img = C.blend(img, HAIR * 1.6, brow * 0.45)

    # mani e piedi un po' sporchi
    dirty = mask & ((reg == C.REGION["Hand"]) | (reg == C.REGION["Foot"]))
    img = img * (1 - 0.18 * dirty * noise)[..., None]

    img = C.wear(img, 3, 0.35)

    # occhi: texture dell'occhio in un quadrato libero, UV degli occhi riscalate li'
    eye_px = 32
    y0, x0 = free_square(mask, eye_px)
    eye = C.load_image_array(os.path.join(data, EYE_TEX))[..., :3]
    img[y0:y0 + eye_px, x0:x0 + eye_px] = C.resize_area(eye, eye_px)
    uv = eyes.data.uv_layers.active.data
    for l in uv:
        u, v = l.uv
        l.uv = ((x0 + 0.5 + u * (eye_px - 1)) / ATLAS, (y0 + 0.5 + v * (eye_px - 1)) / ATLAS)

    img = C.psx(np.clip(img, 0, 1))
    return img


# =============================================================================================
# Anteprima
# =============================================================================================

def preview(objects, armature):
    os.makedirs(PREVIEW_DIR, exist_ok=True)
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_EEVEE"
    scene.render.resolution_x, scene.render.resolution_y = 900, 1000
    cam_data = bpy.data.cameras.new("cam")
    cam_data.type = "ORTHO"
    cam = bpy.data.objects.new("cam", cam_data)
    scene.collection.objects.link(cam)
    scene.camera = cam
    sun = bpy.data.objects.new("sun", bpy.data.lights.new("sun", "SUN"))
    scene.collection.objects.link(sun)
    sun.rotation_euler = (math.radians(50), 0, math.radians(30))
    sun.data.energy = 3
    world = bpy.data.worlds.new("w")
    scene.world = world
    world.color = (0.45, 0.45, 0.45)
    # cilindro di riferimento da 1,80 m accanto
    bpy.ops.mesh.primitive_cylinder_add(radius=0.03, depth=HEIGHT, location=(1.0, 0, HEIGHT / 2))
    shots = {
        "front": ((0.2, -6, 0.92), (90, 0, 0), 2.6),
        "side": ((6, 0, 0.92), (90, 0, 90), 2.6),
        "back": ((0.2, 6, 0.92), (90, 0, 180), 2.6),
        "face": ((0, -6, 1.66), (90, 0, 0), 0.42),
        "face34": ((-4.2, -4.2, 1.66), (90, 0, -45), 0.42),
        "hand": ((0.80, -6, 1.44), (90, 0, 0), 0.32),
    }
    for name, (loc, rot, sc) in shots.items():
        cam.location = loc
        cam.rotation_euler = [math.radians(a) for a in rot]
        cam_data.ortho_scale = sc
        scene.render.filepath = os.path.join(PREVIEW_DIR, name + ".png")
        bpy.ops.render.render(write_still=True)
    # due pose dalle clip, per vedere la deformazione
    for clip, frame in (("Crouch_Fwd", 10), ("Sprint", 8), ("Sitting_Idle", 1)):
        act = next((a for a in bpy.data.actions if a.name.startswith(clip)), None)
        if act is None:
            continue
        armature.animation_data.action = act
        if armature.animation_data.action_slot is None and len(act.slots):
            armature.animation_data.action_slot = act.slots[0]
        scene.frame_set(frame)
        cam.location, cam.rotation_euler = (-4.2, -4.2, 0.9), [math.radians(a) for a in (90, 0, -45)]
        cam_data.ortho_scale = 2.4
        scene.render.filepath = os.path.join(PREVIEW_DIR, "pose_" + clip + ".png")
        bpy.ops.render.render(write_still=True)
    armature.animation_data.action = None


# =============================================================================================
# Principale
# =============================================================================================

def main():
    C.reset_scene()
    report = {}

    # --- UAL: armatura + azioni dal manichino --------------------------------------------------
    imported = C.import_glb(C.project_path(PROJECT_DIR, C.MANNEQUIN_GLB))
    ual = next(o for o in imported if o.type == "ARMATURE")
    for o in imported:
        if o is not ual:
            bpy.data.objects.remove(o)
    ual.name = ual.data.name = "Armature"
    ual_heads = ual_rest_heads(ual)
    report["actions"] = len(bpy.data.actions)

    # --- MakeHuman --------------------------------------------------------------------------
    data, basemesh, rig, body, eyes = make_human()
    pose_to_ual(rig, ual_heads)
    for o in (body, eyes):
        apply_pose(o)
    mh_joints = {ual_name(p.name): rig.matrix_world @ p.head for p in rig.pose.bones}
    mh_tails = {p.name: rig.matrix_world @ p.tail for p in rig.pose.bones}
    mh_joints.pop("root", None)
    for o in (body, eyes):
        o.parent = None
        o.matrix_world = o.matrix_world.copy()
    bpy.data.objects.remove(basemesh)
    bpy.data.objects.remove(rig)

    # --- scala a 1,80 m ---------------------------------------------------------------------
    co = C.mesh_world_coords(body)
    zmin, zmax = co[:, 2].min(), co[:, 2].max()
    s = HEIGHT / (zmax - zmin)
    fix = Matrix.Scale(s, 4) @ Matrix.Translation((0, 0, -zmin))
    for o in (body, eyes):
        o.data.transform(o.matrix_world)
        o.matrix_world = Matrix.Identity(4)
        o.data.transform(fix)
    mh_joints = {k: fix @ v for k, v in mh_joints.items()}
    mh_tails = {k: fix @ v for k, v in mh_tails.items()}
    report["scale_fix"] = s

    # --- scheletro UAL sui giunti ------------------------------------------------------------
    fit_ual_skeleton(ual, mh_joints, mh_tails)
    joints = {b.name: ual.matrix_world @ b.head_local for b in ual.data.bones}

    # --- mesh: occhi pesati sulla testa, gruppi rinominati ----------------------------------
    for g in body.vertex_groups:
        g.name = ual_name(g.name)
    for g in list(eyes.vertex_groups):
        eyes.vertex_groups.remove(g)
    eyes.vertex_groups.new(name="Head").add(list(range(len(eyes.data.vertices))), 1.0, "REPLACE")
    eye_z = float(C.mesh_world_coords(eyes)[:, 2].mean())
    report["eye_z"] = eye_z
    report["head_top"] = float(C.mesh_world_coords(body)[:, 2].max())

    report["tris_before"] = sum(len(p.vertices) - 2 for p in body.data.polygons)
    report["tris"] = decimate(body, DECIMATE_RATIO)
    bones = {b.name for b in ual.data.bones}
    for g in list(body.vertex_groups):
        if g.name not in bones:
            body.vertex_groups.remove(g)
    C.limit_and_normalize(body, 4)

    regions = classify_regions(body, joints, eye_z)
    atlas = build_atlas(body, eyes, data, joints, eye_z, regions)
    tex_path = os.path.join(PREVIEW_DIR, "Character_Base.png")
    os.makedirs(PREVIEW_DIR, exist_ok=True)
    image = C.make_image("Character_Base", atlas, tex_path)
    mat = C.psx_material("Body", image)

    # occhi uniti al corpo: un solo materiale, una sola mesh
    C.write_region_colors(eyes, [C.REGION["Face"]] * len(eyes.data.vertices))
    C.write_region_colors(body, regions)
    for o in (body, eyes):
        o.data.materials.clear()
        o.data.materials.append(mat)
    C.select_only([body, eyes], active=body)
    bpy.ops.object.join()
    body.name = body.data.name = "Body"
    for p in body.data.polygons:
        p.use_smooth = True

    body.parent = ual
    mod = body.modifiers.new("Armature", "ARMATURE")
    mod.object = ual
    report["verts"] = len(body.data.vertices)
    report["tris_final"] = sum(len(p.vertices) - 2 for p in body.data.polygons)
    report["joints"] = {k: [round(c, 3) for c in joints[k]] for k in
                        ("pelvis", "spine_03", "neck_01", "Head", "upperarm_l", "lowerarm_l",
                         "hand_l", "middle_01_l", "thigh_l", "calf_l", "foot_l", "ball_l")}

    if EXPORT:
        C.export_glb(C.project_path(PROJECT_DIR, C.BODY_GLB), [ual, body], animations=True)
    if PREVIEW:
        preview([body], ual)
    return report


result = main()
print("RESULT", result)
