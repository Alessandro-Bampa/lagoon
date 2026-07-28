"""Genera Body_Base: base mesh nuda + rig Mixamo-compatibile, esportata in .glb.

SORGENTE DI VERITA' dell'asset: non modificare il .blend a mano, modifica questo
file e rigenera. Va eseguito dentro Blender tramite:

    python tools/blender/blender_client.py tools/blender/build_character.py

Convenzioni geometriche:
  - Blender Z-up, 1 unita' = 1 metro, origine ai piedi a terra.
  - Il personaggio guarda verso -Y; la sua SINISTRA e' +X (bone "Left*").
  - Bind pose: T-pose esatta (compatibilita' Mixamo / SkeletonProfileHumanoid).
"""

import math
import os
import tempfile

import bmesh
import bpy
from mathutils import Vector

# --------------------------------------------------------------------------
# Parametri
# --------------------------------------------------------------------------

HEIGHT = 1.78            # Altezza totale in metri.
TRI_BUDGET = (6000, 10000)
SUBSURF_LEVELS = 2

# Dichiarata da blender_client.py in testa allo script.
EXPORT_PATH = PROJECT_DIR + "/assets/models/Body_Base.glb"
BLEND_PATH = PROJECT_DIR + "/assets/models/source/Body_Base.blend"
# Cartella dei render di controllo. Era il percorso di scratch di UNA sessione, ormai
# inesistente: si usa la temp di sistema, che c'e' sempre e ovunque.
RENDER_DIR = os.environ.get("LAGOON_RENDER_DIR") or tempfile.gettempdir().replace("\\", "/")

MESH_NAME = "Body_Base"
ARMATURE_NAME = "Armature_Character"
MATERIAL_NAME = "M_Body_Base"

# --------------------------------------------------------------------------
# Grafo dello scheletro di skin
#
# Ogni nodo: (id, x, y, z, radius_x, radius_y, parent_id).
# Descritto solo per la meta' sinistra (x >= 0); la destra e' generata per
# specchiatura in X. I nodi con x == 0 restano condivisi.
# I raggi sono i semiassi della sezione trasversale, in metri.
# --------------------------------------------------------------------------

SKIN_NODES = [
    # id                x      y       z      rx     ry     parent
    ("pelvis",        0.000,  0.008, 0.985, 0.140, 0.112, None),
    ("spine_1",       0.000, -0.010, 1.095, 0.122, 0.098, "pelvis"),
    ("spine_2",       0.000, -0.018, 1.200, 0.130, 0.104, "spine_1"),
    ("spine_3",       0.000, -0.012, 1.300, 0.148, 0.114, "spine_2"),
    # Hub a 4 rami (spine_3, collo, due clavicole). E' inevitabile in un torso
    # umanoide; per evitare che lo Skin modifier ci generi uno scalino, i raggi
    # dei nodi adiacenti restano vicini tra loro e branch_smoothing e' alto.
    ("chest",         0.000, -0.004, 1.390, 0.142, 0.110, "spine_3"),
    ("neck_base",     0.000,  0.006, 1.470, 0.066, 0.068, "chest"),
    ("neck_top",      0.000,  0.010, 1.530, 0.055, 0.057, "neck_base"),
    ("head_low",      0.000, -0.004, 1.596, 0.086, 0.096, "neck_top"),
    ("head_mid",      0.000, -0.010, 1.672, 0.090, 0.102, "head_low"),
    ("head_top",      0.000, -0.004, 1.762, 0.052, 0.058, "head_mid"),

    # Braccio sinistro (T-pose: si estende lungo +X a z costante).
    ("clav_l",        0.112, -0.004, 1.398, 0.126, 0.106, "chest"),
    ("deltoid_l",     0.205,  0.000, 1.408, 0.084, 0.086, "clav_l"),
    ("shoulder_l",    0.255,  0.000, 1.412, 0.066, 0.068, "deltoid_l"),
    ("uparm_mid_l",   0.360,  0.000, 1.414, 0.056, 0.058, "shoulder_l"),
    ("elbow_l",       0.480,  0.000, 1.416, 0.049, 0.051, "uparm_mid_l"),
    ("forearm_mid_l", 0.595,  0.000, 1.416, 0.046, 0.048, "elbow_l"),
    ("wrist_l",       0.730,  0.000, 1.418, 0.031, 0.039, "forearm_mid_l"),
    ("hand_l",        0.805,  0.000, 1.418, 0.039, 0.059, "wrist_l"),
    ("hand_tip_l",    0.876,  0.000, 1.418, 0.023, 0.046, "hand_l"),

    # Gamba sinistra.
    ("hip_l",         0.092,  0.000, 0.930, 0.098, 0.098, "pelvis"),
    ("thigh_up_l",    0.098,  0.000, 0.810, 0.086, 0.090, "hip_l"),
    ("thigh_mid_l",   0.100,  0.000, 0.655, 0.073, 0.077, "thigh_up_l"),
    ("knee_l",        0.100,  0.006, 0.500, 0.057, 0.059, "thigh_mid_l"),
    ("calf_l",        0.100, -0.010, 0.365, 0.061, 0.062, "knee_l"),
    ("shin_l",        0.100, -0.004, 0.225, 0.043, 0.045, "calf_l"),
    ("ankle_l",       0.100,  0.015, 0.095, 0.040, 0.050, "shin_l"),
    ("foot_l",        0.100, -0.060, 0.048, 0.046, 0.062, "ankle_l"),
    ("toe_l",         0.100, -0.165, 0.034, 0.041, 0.050, "foot_l"),
]

ROOT_NODE = "pelvis"

# --------------------------------------------------------------------------
# Definizione dell'armature (nomi e gerarchia Mixamo, senza prefisso)
#
# Ogni bone: (nome, head, tail, parent, connect).
# Le coordinate sono derivate dallo stesso schema proporzionale della mesh, in
# modo che i giunti cadano dentro i loop generati dallo Skin modifier.
# --------------------------------------------------------------------------

def _bone_table():
    """Costruisce la tabella dei bone. Restituisce una lista ordinata (i parent precedono i figli)."""
    b = []

    def add(name, head, tail, parent, connect=True):
        b.append((name, Vector(head), Vector(tail), parent, connect))

    # Colonna centrale.
    add("Hips",   (0.0, 0.0, 0.985), (0.0, -0.006, 1.100), None, False)
    add("Spine",  (0.0, -0.006, 1.100), (0.0, -0.014, 1.225), "Hips")
    add("Spine1", (0.0, -0.014, 1.225), (0.0, -0.010, 1.345), "Spine")
    add("Spine2", (0.0, -0.010, 1.345), (0.0, 0.000, 1.440), "Spine1")
    add("Neck",   (0.0, 0.000, 1.440), (0.0, 0.006, 1.545), "Spine2")
    add("Head",   (0.0, 0.006, 1.545), (0.0, 0.000, 1.660), "Neck")
    add("HeadTop_End", (0.0, 0.000, 1.660), (0.0, 0.000, 1.780), "Head")

    for side, sx in (("Left", 1.0), ("Right", -1.0)):
        # Catena del braccio (T-pose: lungo l'asse X).
        add(side + "Shoulder", (sx * 0.030, 0.0, 1.400), (sx * 0.180, 0.0, 1.418), "Spine2", False)
        add(side + "Arm",      (sx * 0.180, 0.0, 1.418), (sx * 0.480, 0.0, 1.418), side + "Shoulder")
        add(side + "ForeArm",  (sx * 0.480, 0.0, 1.418), (sx * 0.735, 0.0, 1.418), side + "Arm")
        add(side + "Hand",     (sx * 0.735, 0.0, 1.418), (sx * 0.860, 0.0, 1.418), side + "ForeArm")

        # Catena della gamba.
        add(side + "UpLeg", (sx * 0.092, 0.0, 0.930), (sx * 0.100, 0.006, 0.500), "Hips", False)
        add(side + "Leg",   (sx * 0.100, 0.006, 0.500), (sx * 0.100, 0.012, 0.090), side + "UpLeg")
        add(side + "Foot",  (sx * 0.100, 0.012, 0.090), (sx * 0.100, -0.090, 0.030), side + "Leg")
        add(side + "ToeBase", (sx * 0.100, -0.090, 0.030), (sx * 0.100, -0.165, 0.025), side + "Foot")
        add(side + "Toe_End", (sx * 0.100, -0.165, 0.025), (sx * 0.100, -0.215, 0.025), side + "ToeBase")

    return b


BONES = _bone_table()
DEFORM_BONES = [n for (n, _h, _t, _p, _c) in BONES if not n.endswith("_End")]

# --------------------------------------------------------------------------
# Convenzione dei roll
#
# Il roll definisce l'orientamento dell'asse Z locale del bone, e quindi lo
# spazio in cui vengono espresse le rotazioni di animazione. Roll incoerenti tra
# bone consecutivi torcono gli arti quando si applica un'animazione esterna.
#
# NON usare bpy.ops.armature.calculate_roll(GLOBAL_POS_Z): sui bone quasi
# verticali (spina, gambe) l'asse di riferimento e' quasi parallelo al bone,
# il calcolo e' mal condizionato e produce ribaltamenti di 180 gradi a meta'
# catena. Qui ogni bone punta il proprio Z verso un target esplicito, scelto
# perpendicolare all'asse del bone.
# --------------------------------------------------------------------------

FRONT = Vector((0.0, -1.0, 0.0))   # Direzione in cui guarda il personaggio.

# Assi Z MISURATI su uno scheletro Mixamo reale (clip "Walking", 65 bone,
# prefisso "mixamorig10:"), in coordinate mondo. Congelati qui per non dipendere
# dall'FBX in fase di build.
#
# Allinearsi a questi azzera il delta di roll verso Mixamo, che e' cio' che rende
# le sue clip applicabili direttamente. Nota il risultato della misura: spina,
# collo e gambe usano gia' il fronte (0,-1,0) — ma le BRACCIA hanno lo Z rivolto
# verso il BASSO. Non era deducibile a tavolino: prima di misurare avevo provato
# sia "fronte" (delta 84 gradi) sia "alto" (delta ~6 gradi), entrambi sbagliati.
#
# Per rimisurarli con un'altra clip: tools/blender/measure_mixamo_rolls.py
MIXAMO_Z_AXES = {
    "Hips":          (0.0, -1.0, 0.0),
    "Spine":         (0.0, -0.994183, 0.107699),
    "Spine1":        (0.0, -0.994183, 0.107699),
    "Spine2":        (0.0, -0.994183, 0.107699),
    "Neck":          (0.0, -1.0, 0.0),
    "Head":          (0.0, -1.0, 0.0),
    "HeadTop_End":   (0.0, -1.0, 0.0),
    "LeftShoulder":  (-0.184622, -0.109594, -0.97668),
    "LeftArm":       (0.0, -0.101946, -0.99479),
    "LeftForeArm":   (0.0, -0.106149, -0.99435),
    "LeftHand":      (-0.01822, -0.022191, -0.999588),
    "RightShoulder": (0.184293, -0.110158, -0.976679),
    "RightArm":      (0.0, -0.102774, -0.994705),
    "RightForeArm":  (0.0, -0.106956, -0.994264),
    "RightHand":     (0.022881, -0.010059, -0.999688),
    "LeftUpLeg":     (-0.006633, -0.996396, -0.084562),
    "LeftLeg":       (-0.006495, -0.996948, -0.077791),
    "LeftFoot":      (-0.013869, -0.593627, 0.804621),
    "LeftToeBase":   (-0.06927, 0.043866, 0.996633),
    "LeftToe_End":   (-0.06927, 0.043866, 0.996633),
    "RightUpLeg":    (0.006209, -0.996843, -0.079154),
    "RightLeg":      (0.006199, -0.996908, -0.078331),
    "RightFoot":     (0.016181, -0.580976, 0.81376),
    "RightToeBase":  (0.070528, 0.041436, 0.996649),
    "RightToe_End":  (0.070528, 0.041436, 0.996649),
}


def roll_target(name):
    """Target dell'asse Z locale del bone, in coordinate mondo.

    L'armature ha trasformata identita' (lo garantisce il gate di scala), quindi
    spazio armature e spazio mondo coincidono e i valori misurati si usano
    direttamente in align_roll().
    """
    return Vector(MIXAMO_Z_AXES.get(name, FRONT))

# --------------------------------------------------------------------------
# Utility
# --------------------------------------------------------------------------

def _report(log, key, value):
    log[key] = value
    print("[build] {}: {}".format(key, value))


def _select_only(obj):
    for o in bpy.context.view_layer.objects:
        o.select_set(False)
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj


def view3d_override(**extra):
    """Costruisce un temp_override con un'area VIEW_3D.

    Serve perche' il codice arriva dal server MCP fuori da qualunque area: gli
    operatori con poll() sul contesto (unwrap, pesi, render viewport) fallirebbero.
    """
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


def bake_modifiers(obj):
    """Applica tutti i modifier valutando il depsgraph (nessuna dipendenza dal contesto)."""
    depsgraph = bpy.context.evaluated_depsgraph_get()
    baked = bpy.data.meshes.new_from_object(obj.evaluated_get(depsgraph))
    old = obj.data
    obj.modifiers.clear()
    obj.data = baked
    baked.name = old.name
    bpy.data.meshes.remove(old)
    return baked


# --------------------------------------------------------------------------
# Fase 1 - Scena pulita e contratto di unita'
# --------------------------------------------------------------------------

def stage_scene(log):
    bpy.ops.wm.read_homefile(use_empty=True)
    scene = bpy.context.scene
    scene.unit_settings.system = "METRIC"
    scene.unit_settings.scale_length = 1.0
    scene.unit_settings.length_unit = "METERS"
    scene.frame_set(0)

    coll = bpy.data.collections.new("Character")
    scene.collection.children.link(coll)
    _report(log, "unit_scale", scene.unit_settings.scale_length)
    return coll


# --------------------------------------------------------------------------
# Fase 2 - Grafo di skin -> mesh quad
# --------------------------------------------------------------------------

def _mirrored_nodes():
    """Espande SKIN_NODES nella lista completa (sinistra + destra specchiata)."""
    nodes = []
    for (nid, x, y, z, rx, ry, parent) in SKIN_NODES:
        nodes.append((nid, x, y, z, rx, ry, parent))
    for (nid, x, y, z, rx, ry, parent) in SKIN_NODES:
        if abs(x) < 1e-9:
            continue  # Nodo sulla mediana: condiviso, non duplicare.
        mid = nid[:-2] + "_r" if nid.endswith("_l") else nid + "_r"
        mparent = parent
        if parent is not None and not _is_centerline(parent):
            mparent = parent[:-2] + "_r" if parent.endswith("_l") else parent + "_r"
        nodes.append((mid, -x, y, z, rx, ry, mparent))
    return nodes


def _is_centerline(nid):
    for (n, x, _y, _z, _rx, _ry, _p) in SKIN_NODES:
        if n == nid:
            return abs(x) < 1e-9
    return False


def stage_mesh(log, coll):
    nodes = _mirrored_nodes()

    mesh = bpy.data.meshes.new(MESH_NAME)
    obj = bpy.data.objects.new(MESH_NAME, mesh)
    coll.objects.link(obj)

    bm = bmesh.new()
    index_of = {}
    verts = []
    for i, (nid, x, y, z, _rx, _ry, _p) in enumerate(nodes):
        v = bm.verts.new((x, y, z))
        index_of[nid] = i
        verts.append(v)
    bm.verts.ensure_lookup_table()

    for (nid, _x, _y, _z, _rx, _ry, parent) in nodes:
        if parent is None:
            continue
        bm.edges.new((verts[index_of[nid]], verts[index_of[parent]]))

    bm.to_mesh(mesh)
    bm.free()

    # Skin modifier: raggi per-vertice, root sul bacino.
    _select_only(obj)
    skin_mod = obj.modifiers.new("Skin", "SKIN")
    skin_mod.use_smooth_shade = True
    skin_mod.use_x_symmetry = True
    skin_mod.branch_smoothing = 0.55

    skin_layer = mesh.skin_vertices[0].data
    for (nid, _x, _y, _z, rx, ry, _p) in nodes:
        sv = skin_layer[index_of[nid]]
        sv.radius = (rx, ry)
        sv.use_root = False
    skin_layer[index_of[ROOT_NODE]].use_root = True

    subsurf = obj.modifiers.new("Subdivision", "SUBSURF")
    subsurf.subdivision_type = "CATMULL_CLARK"
    subsurf.levels = SUBSURF_LEVELS
    subsurf.render_levels = SUBSURF_LEVELS

    baked = bake_modifiers(obj)

    # Pulizia: doppioni e normali coerenti, senza passare dagli operatori.
    bm = bmesh.new()
    bm.from_mesh(baked)
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-5)
    # Lo Skin modifier puo' lasciare vertici sciolti (senza faccia) ai nodi di
    # ramificazione: esportati diventerebbero vertici orfani nel .glb.
    stray = [v for v in bm.verts if not v.link_faces]
    if stray:
        bmesh.ops.delete(bm, geom=stray, context="VERTS")
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bm.to_mesh(baked)
    bm.free()
    for poly in baked.polygons:
        poly.use_smooth = True

    return obj


def fit_transform(obj):
    """Calcola scala+offset per portare la mesh a HEIGHT con i piedi a z=0.

    La subdivision Catmull-Clark restringe il volume rispetto alla cage, quindi
    l'altezza finale non coincide con quella dei nodi. Invece di inseguirla a
    mano, la misuro e la correggo qui: lo STESSO fit viene poi applicato alla
    tabella dei bone, cosi' mesh e scheletro restano allineati per costruzione.
    Restituisce (scale, dz) e li applica ai vertici (nessuna scala sull'oggetto).
    """
    zs = [v.co.z for v in obj.data.vertices]
    scale = HEIGHT / (max(zs) - min(zs))
    dz = -min(zs) * scale

    bm = bmesh.new()
    bm.from_mesh(obj.data)
    for v in bm.verts:
        v.co = Vector((v.co.x * scale, v.co.y * scale, v.co.z * scale + dz))
    bm.to_mesh(obj.data)
    bm.free()
    return scale, dz


def mesh_stats(obj):
    mesh = obj.data
    mesh.calc_loop_triangles()
    counts = {}
    for poly in mesh.polygons:
        counts[poly.loop_total] = counts.get(poly.loop_total, 0) + 1
    bm = bmesh.new()
    bm.from_mesh(mesh)
    non_manifold = sum(1 for e in bm.edges if not e.is_manifold)
    loose = sum(1 for v in bm.verts if not v.link_edges)
    bm.free()
    zs = [(obj.matrix_world @ v.co).z for v in mesh.vertices]
    xs = [(obj.matrix_world @ v.co).x for v in mesh.vertices]
    return {
        "verts": len(mesh.vertices),
        "faces": len(mesh.polygons),
        "tris": len(mesh.loop_triangles),
        "face_sides": counts,
        "quad_only": set(counts.keys()) == {4},
        "non_manifold_edges": non_manifold,
        "loose_verts": loose,
        "height": round(max(zs) - min(zs), 4),
        "min_z": round(min(zs), 4),
        "width": round(max(xs) - min(xs), 4),
    }


# --------------------------------------------------------------------------
# Fase 4 - Render di controllo
# --------------------------------------------------------------------------

VIEWS = {
    "front": ((0.0, -4.0, 0.9), (math.pi / 2, 0.0, 0.0), 2.0),
    "side":  ((4.0, 0.0, 0.9), (math.pi / 2, 0.0, math.pi / 2), 2.0),
    # Posizione calcolata per inquadrare (0, 0, 0.9) con la stessa inclinazione
    # della camera isometrica di gioco (yaw 45, pitch 50).
    "iso":   ((2.167, -2.167, 3.472), (math.radians(50.0), 0.0, math.radians(45.0)), 2.3),
}


def stage_render(log, prefix="body"):
    scene = bpy.context.scene
    cam_data = bpy.data.cameras.get("RenderCam") or bpy.data.cameras.new("RenderCam")
    cam = bpy.data.objects.get("RenderCam")
    if cam is None:
        cam = bpy.data.objects.new("RenderCam", cam_data)
        scene.collection.objects.link(cam)
    cam.data.type = "ORTHO"
    scene.camera = cam

    scene.render.resolution_x = 700
    scene.render.resolution_y = 1000
    scene.render.image_settings.file_format = "PNG"
    scene.render.film_transparent = False

    written = []
    for name, (loc, rot, scale) in VIEWS.items():
        cam.location = loc
        cam.rotation_euler = rot
        cam.data.ortho_scale = scale
        path = "{}/{}_{}.png".format(RENDER_DIR, prefix, name)
        scene.render.filepath = path
        with view3d_override():
            bpy.ops.render.opengl(write_still=True, view_context=False)
        written.append(path)
    _report(log, "renders", written)
    return written


# --------------------------------------------------------------------------
# Fase 5 - UV
# --------------------------------------------------------------------------

def stage_uv(log, obj):
    _select_only(obj)
    tool_settings = bpy.context.scene.tool_settings
    # Con il sync attivo la selezione UV segue quella della mesh (tutta
    # selezionata) e select_overlap non sarebbe misurabile.
    sync_was = tool_settings.use_uv_select_sync
    tool_settings.use_uv_select_sync = False
    tool_settings.uv_select_mode = "FACE"
    try:
        with view3d_override(object=obj, active_object=obj, selected_objects=[obj]):
            bpy.ops.object.mode_set(mode="EDIT")
            bpy.ops.mesh.select_all(action="SELECT")
            bpy.ops.uv.smart_project(angle_limit=math.radians(66.0), island_margin=0.02,
                                     correct_aspect=True, scale_to_bounds=False)
            bpy.ops.uv.select_all(action="DESELECT")
            bpy.ops.uv.select_overlap()
            bpy.ops.object.mode_set(mode="OBJECT")
    finally:
        tool_settings.use_uv_select_sync = sync_was

    # In Blender 4.1+ la selezione UV vive in attributi booleani della mesh,
    # non piu' come flag sui MeshUVLoop.
    uv_layer = obj.data.uv_layers.active
    sel = obj.data.attributes.get(".uv_select_face")
    overlapping = sum(1 for d in sel.data if d.value) if sel is not None else -1
    coords = [d.uv for d in uv_layer.data]
    stats = {
        "uv_layer": uv_layer.name,
        "overlapping_faces": overlapping,
        "u_range": [round(min(c.x for c in coords), 4), round(max(c.x for c in coords), 4)],
        "v_range": [round(min(c.y for c in coords), 4), round(max(c.y for c in coords), 4)],
    }
    stats["ok"] = overlapping == 0
    _report(log, "uv", stats)
    return stats


# --------------------------------------------------------------------------
# Fase 6 - Armature con nomi e gerarchia Mixamo
# --------------------------------------------------------------------------

def stage_armature(log, coll, scale, dz):
    arm_data = bpy.data.armatures.new(ARMATURE_NAME)
    arm_obj = bpy.data.objects.new(ARMATURE_NAME, arm_data)
    coll.objects.link(arm_obj)

    def fit(v):
        # Stesso fit applicato alla mesh: i giunti restano dentro i loop giusti.
        return Vector((v.x * scale, v.y * scale, v.z * scale + dz))

    _select_only(arm_obj)
    with view3d_override(object=arm_obj, active_object=arm_obj, selected_objects=[arm_obj]):
        bpy.ops.object.mode_set(mode="EDIT")
        created = {}
        for (name, head, tail, parent, connect) in BONES:
            bone = arm_data.edit_bones.new(name)
            bone.head = fit(head)
            bone.tail = fit(tail)
            bone.use_deform = not name.endswith("_End")
            if parent is not None:
                bone.parent = created[parent]
                bone.use_connect = connect
            created[name] = bone
        for name, bone in created.items():
            bone.align_roll(roll_target(name))
        rolls = {n: round(math.degrees(b.roll), 2) for n, b in created.items()}
        bpy.ops.object.mode_set(mode="OBJECT")

    names = [b.name for b in arm_data.bones]
    _report(log, "armature", {
        "bones": len(names),
        "deform": sum(1 for b in arm_data.bones if b.use_deform),
        "names": names,
        "hips_z": round(arm_data.bones["Hips"].head_local.z, 4),
        "head_z": round(arm_data.bones["Head"].head_local.z, 4),
        "rolls": rolls,
    })
    _report(log, "roll_conformance", roll_conformance(arm_data))
    _report(log, "roll_continuity", roll_continuity(arm_data))
    return arm_obj


def roll_conformance(arm_data, threshold_deg=1.0):
    """Verifica che il roll di ogni bone sia OTTIMO rispetto al target Mixamo.

    Attenzione a cosa si misura. L'angolo grezzo fra il nostro Z e quello di
    Mixamo mescola due cose diverse:

      1. il roll, che possiamo scegliere;
      2. la direzione dell'osso, che NON possiamo scegliere perche' deve stare
         dentro la nostra mesh, e le nostre proporzioni non sono quelle di
         Mixamo (clavicola 17 gradi, spina 10, piedi 6).

    Il roll puo' solo allineare Z al target PROIETTATO sul piano perpendicolare
    all'osso. Quindi qui si confronta con la proiezione: cosi' il check dice
    "il roll e' quello giusto" senza farsi sporcare dalle differenze di
    proporzione, che sono un limite noto e riportato a parte.
    """
    worst = 0.0
    off = []
    residual = []
    for bone in arm_data.bones:
        target = MIXAMO_Z_AXES.get(bone.name)
        if target is None:
            continue
        m = bone.matrix_local.to_3x3()
        axis = (m @ Vector((0.0, 1.0, 0.0))).normalized()
        z = m @ Vector((0.0, 0.0, 1.0))
        t = Vector(target)
        projected = (t - axis * t.dot(axis))
        if projected.length < 1e-4:
            continue  # Target parallelo all'osso: roll indeterminato.
        angle = math.degrees(z.angle(projected.normalized()))
        worst = max(worst, angle)
        if angle > threshold_deg:
            off.append({"bone": bone.name, "deg": round(angle, 2)})
        raw = math.degrees(z.angle(t))
        if raw > 3.0:
            residual.append({"bone": bone.name, "deg": round(raw, 2)})

    return {
        "max_roll_error_deg": round(worst, 2),
        "off_target": off,
        # Differenza residua dovuta alle proporzioni, non correggibile col roll.
        "proportion_residual": sorted(residual, key=lambda r: -r["deg"]),
        "ok": not off,
    }


def roll_continuity(arm_data, threshold_deg=20.0):
    """Misura il TWIST RESIDUO fra bone consecutivi.

    Confrontare direttamente gli assi Z di padre e figlio non dice nulla: dove
    l'osso cambia direzione (la caviglia, per esempio) gli Z divergono anche con
    una convenzione perfettamente coerente. Quello che rompe le animazioni e'
    invece la torsione ATTORNO all'asse dell'osso.

    Qui si trasporta l'asse Z del padre lungo la rotazione minima che porta la
    direzione del padre su quella del figlio, e si misura quanto resta. Zero
    significa convenzione coerente, indipendentemente da quanto l'osso piega.
    """
    jumps = []
    degenerate = []
    worst = 0.0
    for bone in arm_data.bones:
        if bone.parent is None:
            continue
        pair = "{}->{}".format(bone.parent.name, bone.name)
        m_parent = bone.parent.matrix_local.to_3x3()
        m_child = bone.matrix_local.to_3x3()
        dir_parent = (m_parent @ Vector((0.0, 1.0, 0.0))).normalized()
        dir_child = (m_child @ Vector((0.0, 1.0, 0.0))).normalized()

        bend = math.degrees(dir_parent.angle(dir_child))
        if bend > 150.0:
            # Inversione di direzione (bacino -> femore): il trasporto minimo
            # non e' definito in modo univoco, la misura non avrebbe senso.
            degenerate.append({"pair": pair, "bend_deg": round(bend, 2)})
            continue

        transported = dir_parent.rotation_difference(dir_child) @ (m_parent @ Vector((0.0, 0.0, 1.0)))
        twist = math.degrees(transported.angle(m_child @ Vector((0.0, 0.0, 1.0))))
        worst = max(worst, twist)
        if twist > threshold_deg:
            jumps.append({"pair": pair, "twist_deg": round(twist, 2), "bend_deg": round(bend, 2)})

    return {"max_twist_deg": round(worst, 2), "jumps_over_threshold": jumps,
            "direction_reversals": degenerate, "ok": not jumps}


# --------------------------------------------------------------------------
# Fase 7 - Skinning con vincolo di 4 influenze
# --------------------------------------------------------------------------

def stage_skinning(log, body, arm_obj):
    for o in bpy.context.view_layer.objects:
        o.select_set(False)
    body.select_set(True)
    arm_obj.select_set(True)
    bpy.context.view_layer.objects.active = arm_obj

    with view3d_override(object=arm_obj, active_object=arm_obj,
                         selected_objects=[body, arm_obj],
                         selected_editable_objects=[body, arm_obj]):
        bpy.ops.object.parent_set(type="ARMATURE_AUTO")

    _select_only(body)
    with view3d_override(object=body, active_object=body, selected_objects=[body]):
        # Vincolo del progetto: massimo 4 influenze per vertice.
        bpy.ops.object.vertex_group_limit_total(limit=4)
        bpy.ops.object.vertex_group_normalize_all(lock_active=False)

    # Verifica numerica sui dati, non sull'esito dell'operatore.
    max_inf = 0
    unskinned = 0
    bad_sum = 0
    for v in body.data.vertices:
        weights = [g.weight for g in v.groups if g.weight > 1e-6]
        max_inf = max(max_inf, len(weights))
        total = sum(weights)
        if not weights:
            unskinned += 1
        elif abs(total - 1.0) > 1e-3:
            bad_sum += 1

    stats = {
        "vertex_groups": len(body.vertex_groups),
        "max_influences": max_inf,
        "unskinned_verts": unskinned,
        "unnormalized_verts": bad_sum,
        "ok": max_inf <= 4 and unskinned == 0 and bad_sum == 0,
    }
    _report(log, "skinning", stats)
    return stats


# --------------------------------------------------------------------------
# Fase 8 - Materiale, naming, gate di scala
# --------------------------------------------------------------------------

def stage_material(body):
    mat = bpy.data.materials.new(MATERIAL_NAME)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    if bsdf is not None:
        bsdf.inputs["Base Color"].default_value = (0.55, 0.52, 0.50, 1.0)
        bsdf.inputs["Roughness"].default_value = 0.75
        bsdf.inputs["Metallic"].default_value = 0.0
    body.data.materials.clear()
    body.data.materials.append(mat)
    return mat


def stage_scale_gate(log, body, arm_obj):
    scene = bpy.context.scene
    zs = [(body.matrix_world @ v.co).z for v in body.data.vertices]
    height = max(zs) - min(zs)
    bones = arm_obj.data.bones

    checks = {
        "unit_scale_is_1": scene.unit_settings.scale_length == 1.0,
        "body_scale_is_1": tuple(round(c, 6) for c in body.scale) == (1.0, 1.0, 1.0),
        "armature_scale_is_1": tuple(round(c, 6) for c in arm_obj.scale) == (1.0, 1.0, 1.0),
        "body_origin_at_zero": body.location.length < 1e-6,
        "armature_origin_at_zero": arm_obj.location.length < 1e-6,
        "height_in_range": 1.75 <= height <= 1.80,
        "feet_on_ground": abs(min(zs)) < 1e-3,
        "hips_plausible": 0.50 * HEIGHT <= bones["Hips"].head_local.z <= 0.58 * HEIGHT,
        "head_plausible": 0.82 * HEIGHT <= bones["Head"].head_local.z <= 0.90 * HEIGHT,
    }
    gate = {"checks": checks, "height": round(height, 5),
            "min_z": round(min(zs), 6), "passed": all(checks.values())}
    _report(log, "scale_gate", gate)
    return gate


# --------------------------------------------------------------------------
# Fase 9 - Export
# --------------------------------------------------------------------------

def stage_export(log, body, arm_obj):
    os.makedirs(os.path.dirname(BLEND_PATH), exist_ok=True)
    os.makedirs(os.path.dirname(EXPORT_PATH), exist_ok=True)

    # .gdignore: impedisce a Godot di importare anche il .blend accanto al .glb.
    with open(os.path.join(os.path.dirname(BLEND_PATH), ".gdignore"), "w") as handle:
        handle.write("")

    for o in bpy.context.view_layer.objects:
        o.select_set(False)
    body.select_set(True)
    arm_obj.select_set(True)
    bpy.context.view_layer.objects.active = arm_obj

    # L'esportatore glTF legge context.active_object: senza un contesto con
    # area 3D quell'attributo non esiste proprio e l'export esplode.
    with view3d_override(object=arm_obj, active_object=arm_obj,
                         selected_objects=[body, arm_obj],
                         selected_editable_objects=[body, arm_obj]):
        bpy.ops.export_scene.gltf(
            filepath=EXPORT_PATH,
            export_format="GLB",
            use_selection=True,
            export_yup=True,
            export_apply=True,
            export_skins=True,
            export_animations=False,
            export_materials="EXPORT",
            export_tangents=False,
            export_cameras=False,
            export_lights=False,
        )
    bpy.ops.wm.save_as_mainfile(filepath=BLEND_PATH)

    _report(log, "export", {
        "glb": EXPORT_PATH,
        "glb_bytes": os.path.getsize(EXPORT_PATH),
        "blend": BLEND_PATH,
    })


# --------------------------------------------------------------------------
# Esecuzione
# --------------------------------------------------------------------------

log = {}
coll = stage_scene(log)
body = stage_mesh(log, coll)
FIT_SCALE, FIT_DZ = fit_transform(body)
_report(log, "fit", {"scale": round(FIT_SCALE, 5), "dz": round(FIT_DZ, 5)})
log["mesh"] = mesh_stats(body)
lo, hi = TRI_BUDGET
log["mesh"]["in_budget"] = lo <= log["mesh"]["tris"] <= hi

stage_uv(log, body)
armature = stage_armature(log, coll, FIT_SCALE, FIT_DZ)
stage_skinning(log, body, armature)
stage_material(body)
gate = stage_scale_gate(log, body, armature)
stage_render(log, "body")

blockers = []
if not gate["passed"]:
    blockers.append("gate di scala")
if not log["roll_conformance"]["ok"]:
    blockers.append("roll non conformi a Mixamo")
if not log["skinning"]["ok"]:
    blockers.append("skinning")
if not log["uv"]["ok"]:
    blockers.append("UV sovrapposte")

if blockers:
    log["export"] = "SALTATO: " + ", ".join(blockers)
else:
    stage_export(log, body, armature)

result = log
