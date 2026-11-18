"""Genera le barche da laguna modulari ("barchino" da 4,5 m con fuoribordo) e i loro pezzi.

Tutto procedurale (bmesh da coordinate), nessun pack esterno. Uso (Blender aperto con l'estensione
MCP, skill blender-pipeline):

    python tools/blender/blender_client.py tools/blender/build_lagoon_boats.py            # tutto
    python tools/blender/blender_client.py tools/blender/build_lagoon_boats.py preview    # + render

Produce in `assets/models/boats/` un .glb per pezzo (skill `vehicles-boats` §9):

  Barchino_Tiller.glb    scafo aperto da guidare con la barra del motore: prua coperta, panca di poppa
  Barchino_Console.glb   stesso scafo, allestimento con consolle: panca a U, sedute a prua
  Gozzo_Placeholder.glb, Peschereccio_Placeholder.glb   dislocanti PROVVISORI (scafo parametrico,
                         origine sulla linea d'acqua di progetto) per entrobordo, timone, elica di prua
  Outboard_25.glb        fuoribordo piccolo (con barra), Outboard_40.glb grande
  Console_Helm.glb       consolle con parabrezza e volante
  Acc_Ladder.glb         scaletta di poppa; Acc_NavLight.glb   fanale di poppa

ASSI. Blender +Y = prua = -Z di Godot, +X = dritta = +X di Godot, +Z = su. Unita' in metri.
Scafo: origine a mezza lunghezza, chiglia a z = -0,30 (il centro di massa dell'host e' l'origine).

NOMI CHE GODOT LEGGE (BoatAssembly / OutboardMotor / HelmConsole):
  scafo:    vuoti `Mount_Motor`, `Mount_Console`, `Mount_Ladder`, `Mount_NavLight`,
            `Seat_Helm` (piedi del pilota: piano di seduta 0,46 m sopra, centro 0,04 m dietro; il corpo
            seduto e' avanzato di 0,25 m da CharacterAnimator.SeatedForwardShift, cosi' la testa sta
            sopra i piedi, dove c'e' la camera), `Seat_P*` (passeggeri),
            mesh `HydroHull` (carena CHIUSA
            low-poly per la fisica per triangoli, coperta alla falchetta), `HullCollision` (involucro
            convesso), `DeckCollision` (pavimento, panche, bordi: trimesh calpestabile) e `SwimCollision`
            (fasciame esterno aperto: parete per chi nuota), nascoste;
            profili fissi `Mount_Skeg` / `Mount_Keel`.
  entrobordo (placeholder): vuoti `Mount_Prop` (-Z di Godot = spinta, asse inclinato), `Mount_Rudder`
            (Y = asse della pala, origine nel centro di pressione), `Mount_Engine`, `Mount_Thruster`;
            parti mobili `Rudder` (ruota attorno a Y) e `Propeller` (attorno a Z).
  motore:   vuoto `Swivel` (asse di sterzo = Y locale di Godot), figli `Propeller` (ruota attorno a Z
            locale di Godot = asse dell'elica), `Tiller` (mesh), `TillerGrip` (mano del pilota),
            `Thrust` (punto e verso della spinta: -Z locale di Godot = avanti).
  consolle: vuoto `Wheel` (ruota attorno a Y locale di Godot = piantone), figli `Hand_L`, `Hand_R`.
MATERIALI per nome, ricolorati dalla livrea in Godot (BoatLivery): Hull, Stripe, Deck, Interior,
Cushion, Rubber, Metal, Motor, MotorTrim, Glass, Gauge.
Niente marchi: forme di un barchino da laguna generico, livree e loghi li decide il gioco.
"""

import math

import bpy
import bmesh
from mathutils import Matrix, Vector

OUT_DIR = PROJECT_DIR + "/assets/models/boats"
PREVIEW = "preview" in ARGV

L = 4.5                 # lunghezza
Y0, Y1 = -L / 2, L / 2  # poppa, prua
FLOOR = -0.05           # pagliolo: sopra la linea di galleggiamento (pescaggio ~0,15 m)
SEAT = FLOOR + 0.46     # piano di seduta (Sitting_Idle / Driving di UAL: bacino a 0,54 m)
N_ST = 48               # stazioni longitudinali

MATERIALS = {
    "Hull": (0.36, 0.40, 0.45, 0.35),
    "Stripe": (0.08, 0.08, 0.09, 0.4),
    "Deck": (0.62, 0.65, 0.68, 0.55),
    "Interior": (0.86, 0.87, 0.88, 0.6),
    "Cushion": (0.92, 0.92, 0.90, 0.85),
    "Rubber": (0.05, 0.05, 0.05, 0.9),
    "Metal": (0.80, 0.80, 0.82, 0.25),
    "Motor": (0.18, 0.20, 0.23, 0.3),
    "MotorTrim": (0.06, 0.06, 0.07, 0.5),
    "Glass": (0.55, 0.65, 0.75, 0.05),
    "Gauge": (0.02, 0.02, 0.02, 0.3),
}


# =====================================================================================
# Infrastruttura
# =====================================================================================

def reset_scene():
    bpy.ops.wm.read_homefile(use_empty=True)
    bpy.context.scene.unit_settings.system = "METRIC"
    bpy.context.scene.unit_settings.scale_length = 1.0
    for name, (r, g, b, rough) in MATERIALS.items():
        mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
        try:
            mat.use_nodes = True
        except AttributeError:
            pass
        bsdf = mat.node_tree.nodes.get("Principled BSDF")
        bsdf.inputs["Base Color"].default_value = (r, g, b, 1.0)
        bsdf.inputs["Roughness"].default_value = rough
        bsdf.inputs["Metallic"].default_value = 0.9 if name == "Metal" else 0.0
        if name == "Glass":
            bsdf.inputs["Alpha"].default_value = 0.35
            mat.surface_render_method = "BLENDED"


class Builder:
    """Accumula vertici e facce con materiale; `poly` accetta poligoni in qualunque verso."""

    def __init__(self):
        self.verts = []
        self.faces = []
        self.mats = []
        self.smooth = []

    def v(self, p):
        self.verts.append(tuple(p))
        return len(self.verts) - 1

    def poly(self, pts, mat, smooth=False):
        idx = [self.v(p) for p in pts]
        self.faces.append(idx)
        self.mats.append(mat)
        self.smooth.append(smooth)

    def quad_strip(self, a, b, mat, smooth=False, flip=False):
        """Facce fra due polilinee con lo stesso numero di punti."""
        for i in range(len(a) - 1):
            q = [a[i], a[i + 1], b[i + 1], b[i]]
            self.poly(q[::-1] if flip else q, mat, smooth)

    def box(self, x0, x1, y0, y1, z0, z1, mat, sides="xXyYzZ", smooth=False):
        p = lambda x, y, z: (x, y, z)
        if "x" in sides: self.poly([p(x0, y0, z0), p(x0, y0, z1), p(x0, y1, z1), p(x0, y1, z0)], mat, smooth)
        if "X" in sides: self.poly([p(x1, y0, z0), p(x1, y1, z0), p(x1, y1, z1), p(x1, y0, z1)], mat, smooth)
        if "y" in sides: self.poly([p(x0, y0, z0), p(x1, y0, z0), p(x1, y0, z1), p(x0, y0, z1)], mat, smooth)
        if "Y" in sides: self.poly([p(x0, y1, z0), p(x0, y1, z1), p(x1, y1, z1), p(x1, y1, z0)], mat, smooth)
        if "z" in sides: self.poly([p(x0, y0, z0), p(x0, y1, z0), p(x1, y1, z0), p(x1, y0, z0)], mat, smooth)
        if "Z" in sides: self.poly([p(x0, y0, z1), p(x1, y0, z1), p(x1, y1, z1), p(x0, y1, z1)], mat, smooth)

    def cylinder(self, a, b, r, mat, seg=10, caps=True):
        a, b = Vector(a), Vector(b)
        axis = (b - a).normalized()
        u = axis.orthogonal().normalized()
        w = axis.cross(u)
        ra = [a + (u * math.cos(t) + w * math.sin(t)) * r for t in (i / seg * math.tau for i in range(seg))]
        rb = [p + (b - a) for p in ra]
        for i in range(seg):
            j = (i + 1) % seg
            self.poly([ra[i], ra[j], rb[j], rb[i]], mat, True)
        if caps:
            self.poly(ra[::-1], mat)
            self.poly(rb, mat)

    def tube(self, path, r, mat, seg=8):
        for a, b in zip(path, path[1:]):
            self.cylinder(a, b, r, mat, seg, caps=False)


def to_object(name, builder, parent=None, location=(0, 0, 0), sharp_deg=35.0):
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(builder.verts, [], builder.faces)
    mats = sorted(set(builder.mats), key=list(MATERIALS).index)
    for m in mats:
        mesh.materials.append(bpy.data.materials[m])
    for poly, m, s in zip(mesh.polygons, builder.mats, builder.smooth):
        poly.material_index = mats.index(m)
        poly.use_smooth = s
    bm = bmesh.new()
    bm.from_mesh(mesh)
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=0.0005)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bm.to_mesh(mesh)
    bm.free()
    mesh.set_sharp_from_angle(angle=math.radians(sharp_deg))
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    obj.location = location
    if parent is not None:
        obj.parent = parent
    return obj


def empty(name, location, parent=None, rotation=(0, 0, 0)):
    obj = bpy.data.objects.new(name, None)
    obj.empty_display_size = 0.08
    obj.location = location
    obj.rotation_euler = rotation
    bpy.context.scene.collection.objects.link(obj)
    if parent is not None:
        obj.parent = parent
    return obj


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


def export(name):
    objects = list(bpy.context.scene.collection.objects)
    bpy.context.view_layer.update()
    for o in bpy.context.view_layer.objects:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    path = "{}/{}.glb".format(OUT_DIR, name)
    with view3d_override(object=objects[0], active_object=objects[0],
                         selected_objects=objects, selected_editable_objects=objects):
        bpy.ops.export_scene.gltf(
            filepath=path, export_format="GLB", use_selection=True, export_yup=True,
            export_animations=False, export_skins=False, export_materials="EXPORT",
            export_cameras=False, export_lights=False, export_extras=False)
    return path


# =====================================================================================
# Scafo
# =====================================================================================

def half_beam(t):
    """Mezza larghezza alla falchetta: specchio largo, massimo a 0,4, prua a zero."""
    if t < 0.4:
        return 0.80 + 0.075 * math.sin(t / 0.4 * math.pi / 2)
    u = (t - 0.4) / 0.6
    return 0.875 * max(0.0, 1.0 - u ** 2.2) ** 0.5


def keel_z(t):
    return -0.30 if t < 0.55 else -0.30 + 0.78 * ((t - 0.55) / 0.45) ** 2.0


def sheer_z(t):
    return 0.50 + 0.18 * t * t      # specchio alto 0,80 m dalla chiglia


def half_section(t):
    """Mezza sezione (x, z) dalla chiglia alla falchetta, lato dritta."""
    bs, zk, zs = half_beam(t), keel_z(t), sheer_z(t)
    # Stellatura costante (12°) nella meta' poppiera, la superficie che plana; poi cresce fino a
    # 35° a prua. Con la stellatura che cresce gia' dallo specchio lo spigolo saliva verso prua e il
    # fondo "attaccava" l'acqua di 3-4° con lo scafo in piano: planava piatto e il trim non contava.
    dead = math.radians(12.0 + 23.0 * max(0.0, (t - 0.45) / 0.55) ** 1.3)
    bc = 0.80 * bs
    zc = min(zk + bc * math.tan(dead), zs - 0.10 * (bs / 0.875) - 0.02)
    s = bs / 0.875
    pts = [(0.0, zk), (bc * 0.5, zk + (zc - zk) * 0.48), (bc, zc), (bc + 0.035 * s, zc + 0.004)]
    x3, z3 = pts[-1]
    for u in (0.33, 0.66, 1.0):
        pts.append((x3 + (bs - x3) * u + 0.012 * s * math.sin(math.pi * u), z3 + (zs - z3) * u))
    return pts


def station_t(i):
    # Piu' fitte a prua, dove la forma cambia in fretta.
    u = i / (N_ST - 1)
    return 1.0 - (1.0 - u) ** 1.35


def hull_x_at(t, z):
    """Larghezza (mezza) dello scafo alla quota z nella stazione t."""
    pts = half_section(t)
    for (xa, za), (xb, zb) in zip(pts, pts[1:]):
        if za <= z <= zb and zb > za:
            return xa + (xb - xa) * (z - za) / (zb - za)
    return pts[-1][0] if z > pts[-1][1] else 0.0


def build_hull_shell(b):
    rings = []
    for i in range(N_ST):
        t = station_t(i)
        y = Y0 + t * L
        half = half_section(t)
        ring = [(-x, y, z) for x, z in reversed(half[1:])] + [(x, y, z) for x, z in half]
        rings.append(ring)
    # Fasciame: smussato. La linea di spigolo (chine, falchetta) la tagliano gli spigoli vivi.
    for a, c in zip(rings, rings[1:]):
        b.quad_strip(a, c, "Hull", smooth=True, flip=True)
    # Specchio di poppa.
    b.poly(rings[0], "Hull")
    return rings


def build_rubrail(b):
    """Bottazzo di gomma lungo la falchetta e fascia scura sopra la linea d'acqua."""
    for side in (1, -1):
        top, bottom, stripe_hi, stripe_lo = [], [], [], []
        for i in range(N_ST):
            t = station_t(i)
            y = Y0 + t * L
            bs, zs = half_beam(t), sheer_z(t)
            off = 0.025 * min(1.0, bs / 0.2)
            top.append((side * (bs + off), y, zs + 0.01))
            bottom.append((side * (bs + off), y, zs - 0.05))
            z_hi, z_lo = zs - 0.17, zs - 0.21
            if z_lo < keel_z(t) + 0.02:
                continue    # a prua la fascia finirebbe nel vuoto davanti alla ruota di prua
            stripe_hi.append((side * (hull_x_at(t, z_hi) + 0.003), y, z_hi))
            stripe_lo.append((side * (hull_x_at(t, z_lo) + 0.003), y, z_lo))
        inner_top = [(p[0] - side * 0.03 * min(1.0, half_beam(station_t(i)) / 0.2), p[1], p[2]) for i, p in enumerate(top)]
        flip = side < 0
        b.quad_strip(top, bottom, "Rubber", True, flip=not flip)
        b.quad_strip(inner_top, top, "Rubber", True, flip=not flip)
        b.quad_strip(stripe_hi, stripe_lo, "Stripe", True, flip=not flip)


def inner_wall_x(t, z, gunwale):
    return max(0.0, hull_x_at(t, z) - gunwale)


def build_liner(b, gunwale, t_cockpit0, t_cockpit1):
    """Falchetta (cimasa), murate interne e pagliolo della parte aperta dello scafo."""
    ts = [t for t in (station_t(i) for i in range(N_ST)) if t_cockpit0 <= t <= t_cockpit1]
    ts = [t_cockpit0] + [t for t in ts if t_cockpit0 < t < t_cockpit1] + [t_cockpit1]
    for side in (1, -1):
        outer, inner_top, inner_bot = [], [], []
        for t in ts:
            y = Y0 + t * L
            bs, zs = half_beam(t), sheer_z(t)
            outer.append((side * bs, y, zs))
            inner_top.append((side * max(0.0, bs - gunwale), y, zs))
            inner_bot.append((side * inner_wall_x(t, FLOOR, 0.05), y, FLOOR))
        flip = side < 0
        b.quad_strip(outer, inner_top, "Deck", flip=flip)
        b.quad_strip(inner_top, inner_bot, "Interior", flip=flip)
    # Pagliolo.
    for t0, t1 in zip(ts, ts[1:]):
        y0, y1 = Y0 + t0 * L, Y0 + t1 * L
        x0, x1 = inner_wall_x(t0, FLOOR, 0.05), inner_wall_x(t1, FLOOR, 0.05)
        b.poly([(-x0, y0, FLOOR), (x0, y0, FLOOR), (x1, y1, FLOOR), (-x1, y1, FLOOR)], "Interior")
    return ts


def build_bow_deck(b, t0, gunwale, mat="Deck"):
    """Coperta di prua bombata da t0 alla prora, e la paratia che la chiude verso poppa."""
    ts = [t0] + [t for t in (station_t(i) for i in range(N_ST)) if t > t0]
    rows = []
    for t in ts:
        y = Y0 + t * L
        bs, zs = half_beam(t), sheer_z(t)
        row = [(-bs, y, zs), (-bs * 0.5, y, zs + 0.03 * bs / 0.875), (0.0, y, zs + 0.045 * bs / 0.875),
               (bs * 0.5, y, zs + 0.03 * bs / 0.875), (bs, y, zs)]
        rows.append(row)
    for a, c in zip(rows, rows[1:]):
        b.quad_strip(a, c, mat, smooth=True)
    y = Y0 + t0 * L
    xin = inner_wall_x(t0, FLOOR, 0.05)
    xtop = half_beam(t0) - gunwale
    top = rows[0]
    b.poly([(-xin, y, FLOOR), (xin, y, FLOOR), (xtop, y, sheer_z(t0)), top[3], top[2], top[1],
            (-xtop, y, sheer_z(t0))], "Interior")
    # Gavone di prua con il portello.
    b.box(-0.22, 0.22, y + 0.25, y + 0.62, sheer_z(t0) + 0.035, sheer_z(t0) + 0.05, "Interior")


def build_transom_deck(b, t1, gunwale):
    """Piano di poppa (pozzetto del motore) dallo specchio a t1."""
    y0, y1 = Y0, Y0 + t1 * L
    z = sheer_z(0.0) - 0.06
    x0, x1 = half_beam(0.0) - gunwale, half_beam(t1) - gunwale
    b.poly([(-x0, y0, z), (x0, y0, z), (x1, y1, z), (-x1, y1, z)], "Deck")
    xb0, xb1 = inner_wall_x(t1, FLOOR, 0.05), x1
    b.poly([(-xb0, y1, FLOOR), (xb0, y1, FLOOR), (xb1, y1, z), (-xb1, y1, z)], "Interior")


def prism(b, t0, t1, x_from, x_to, z0, z1, mat, inset=0.0):
    """Blocco fra due stazioni e due quote, coi fianchi che seguono la murata (None = fino alla
    murata): una scatola dritta, dove lo scafo si stringe a V, uscirebbe dal fasciame."""
    def corner(t, z, side):
        wall = inner_wall_x(t, z, 0.06) - inset
        if side < 0:
            return -wall if x_from is None else max(x_from + inset, -wall)
        return wall if x_to is None else min(x_to - inset, wall)
    c = {}
    for t, ty in ((t0, "a"), (t1, "b")):
        y = Y0 + t * L + (inset if ty == "a" else -inset)
        for z, tz in ((z0, "0"), (z1, "1")):
            for side, ts in ((-1, "l"), (1, "r")):
                c[ty + tz + ts] = (corner(t, z, side), y, z)
    b.poly([c["a1l"], c["a1r"], c["b1r"], c["b1l"]], mat)   # sopra
    b.poly([c["b0l"], c["b0r"], c["b1r"], c["b1l"]], mat)   # davanti
    b.poly([c["a0l"], c["a1l"], c["a1r"], c["a0r"]], mat)   # dietro
    b.poly([c["a0l"], c["b0l"], c["b1l"], c["a1l"]], mat)   # sinistra
    b.poly([c["a0r"], c["a1r"], c["b1r"], c["b0r"]], mat)   # destra


def bench(b, t0, t1, x_from, x_to, mat_top="Cushion", cushion=True):
    """Panca dal pagliolo al piano di seduta fra due stazioni, col cuscino; None = fino alla murata."""
    prism(b, t0, t1, x_from, x_to, FLOOR, SEAT - (0.06 if cushion else 0.0), "Interior")
    if cushion:
        prism(b, t0, t1, x_from, x_to, SEAT - 0.06, SEAT, mat_top, inset=0.012)


def collision_hull(parent):
    """Involucro grossolano: in Godot ne nasce un ConvexPolygonShape3D."""
    b = Builder()
    rings = []
    for t in (0.0, 0.25, 0.5, 0.7, 0.85, 1.0):
        y = Y0 + t * L
        half = half_section(t)
        sel = [half[0], half[2], half[-1]]
        rings.append([(-x, y, z) for x, z in reversed(sel[1:])] + [(x, y, z) for x, z in sel])
    for a, c in zip(rings, rings[1:]):
        b.quad_strip(a, c, "Hull", flip=True)
    obj = to_object("HullCollision", b, parent)
    obj.hide_render = True
    return obj


def hydro_hull(parent, rings_fn, n_st, name="HydroHull"):
    """Mesh idrodinamica (skill vehicles-boats §10): carena CHIUSA low-poly, coperta piatta alla
    falchetta, specchio se c'e'. Normali verso l'esterno (Godot le verifica comunque col volume).
    `rings_fn(t)` = sezione completa (da falchetta sinistra a falchetta dritta) alla stazione t."""
    b = Builder()
    rings = []
    for i in range(n_st):
        u = i / (n_st - 1)
        rings.append(rings_fn(1.0 - (1.0 - u) ** 1.15))
    for a, c in zip(rings, rings[1:]):
        b.quad_strip(a, c, "Hull", flip=True)
    if abs(rings[0][0][0]) > 0.01:
        b.poly(rings[0], "Hull")                      # specchio
    if abs(rings[-1][0][0]) > 0.01:
        b.poly(rings[-1][::-1], "Hull")
    for a, c in zip(rings, rings[1:]):
        b.poly([a[0], a[-1], c[-1], c[0]], "Hull")    # coperta (chiude il volume)
    obj = to_object(name, b, parent, sharp_deg=180.0)
    obj.hide_render = True
    return obj


def swim_shell(parent, rings_fn, n_st):
    """Fasciame esterno APERTO in alto (`SwimCollision`, trimesh sul layer 6 in Godot): parete per chi
    nuota attorno alla barca (il corpo dello scafo, layer 5, il giocatore non lo vede) e appoggio per la
    sonda d'arrampicata. Senza coperta: chi sta dentro la barca non lo tocca."""
    b = Builder()
    rings = [rings_fn(1.0 - (1.0 - i / (n_st - 1)) ** 1.15) for i in range(n_st)]
    for a, c in zip(rings, rings[1:]):
        b.quad_strip(a, c, "Hull", flip=True)
    if abs(rings[0][0][0]) > 0.01:
        b.poly(rings[0], "Hull")
    obj = to_object("SwimCollision", b, parent, sharp_deg=180.0)
    obj.hide_render = True
    return obj


def barchino_ring(t):
    y = Y0 + t * L
    half = half_section(t)
    return [(-x, y, z) for x, z in reversed(half[1:])] + [(x, y, z) for x, z in half]


def collision_deck(parent, solids):
    """Superfici calpestabili semplificate (trimesh in Godot): una scatola per pezzo."""
    b = Builder()
    for (x0, x1, y0, y1, z0, z1) in solids:
        b.box(x0, x1, y0, y1, z0, z1, "Interior")
    obj = to_object("DeckCollision", b, parent)
    obj.hide_render = True
    return obj


def side_walls(gunwale):
    """Murate interne come scatole sottili (collisione): 4 tratti per lato."""
    out = []
    for t0, t1 in ((0.02, 0.25), (0.25, 0.45), (0.45, 0.62), (0.62, 0.78)):
        for side in (1, -1):
            x = side * (half_beam((t0 + t1) / 2) - gunwale * 0.5)
            out.append((min(x, x - side * 0.06), max(x, x - side * 0.06), Y0 + t0 * L, Y0 + t1 * L, FLOOR, sheer_z(t1)))
    return out


def build_barchino(variant):
    reset_scene()
    root = empty("Barchino_" + variant, (0, 0, 0))
    b = Builder()
    build_hull_shell(b)
    build_rubrail(b)

    tiller = variant == "Tiller"
    gunwale = 0.08 if tiller else 0.14
    t_bow = 0.64 if tiller else 0.84
    t_stern = 0.06
    build_liner(b, gunwale, t_stern, t_bow)
    build_bow_deck(b, t_bow, gunwale)
    build_transom_deck(b, t_stern, gunwale)

    solids = [(-0.75, 0.75, Y0 + t_stern * L, Y0 + t_bow * L, FLOOR - 0.05, FLOOR)]
    solids.append((-0.6, 0.6, Y0 + t_bow * L, Y1 - 0.3, sheer_z(t_bow) - 0.05, sheer_z(t_bow) + 0.02))
    solids += side_walls(gunwale)

    if tiller:
        # Panca di poppa a tutta larghezza: il pilota siede a dritta e tiene la barra con la sinistra.
        bench(b, t_stern, 0.17, None, None)
        solids.append((-0.7, 0.7, Y0 + t_stern * L, Y0 + 0.17 * L, FLOOR, SEAT))
        # Panca centrale (barra di traverso) per un passeggero.
        bench(b, 0.40, 0.48, None, None)
        solids.append((-0.75, 0.75, Y0 + 0.40 * L, Y0 + 0.48 * L, FLOOR, SEAT))
        seat_x, seat_t = 0.36, (t_stern + 0.17) / 2
        empty("Seat_P1", (0.0, Y0 + 0.44 * L + 0.04, FLOOR), root)
        empty("Seat_P2", (-0.36, Y0 + seat_t * L + 0.04, FLOOR), root)
    else:
        # Panca a U di poppa, seduta del pilota dietro la consolle, sedute laterali a prua.
        bench(b, t_stern, 0.15, None, None)
        for side in (1, -1):
            xw = inner_wall_x(0.25, SEAT, 0.06)
            bench(b, 0.15, 0.30, *(sorted((side * (xw - 0.42), side * xw))))
            bench(b, 0.66, 0.80, *(sorted((side * (inner_wall_x(0.73, SEAT, 0.06) - 0.38), side * inner_wall_x(0.73, SEAT, 0.06)))))
        bench(b, 0.36, 0.43, 0.05, 0.62)
        solids += [(-0.7, 0.7, Y0 + t_stern * L, Y0 + 0.15 * L, FLOOR, SEAT),
                   (0.05, 0.62, Y0 + 0.36 * L, Y0 + 0.43 * L, FLOOR, SEAT)]
        seat_x, seat_t = 0.33, 0.395
        empty("Seat_P1", (-0.36, Y0 + 0.38 * L + 0.04, FLOOR), root)
        empty("Seat_P2", (0.0, Y0 + 0.10 * L + 0.04, FLOOR), root)
        # Consolle: il volante cade davanti alle mani di Driving_Loop (0,29 m avanti, 0,91 m su).
        empty("Mount_Console", (seat_x, Y0 + seat_t * L + 0.04 + 0.25, FLOOR), root)

    # Piedi nominali del pilota (vedi docstring): 0,04 m davanti al centro del sedile.
    empty("Seat_Helm", (seat_x, Y0 + seat_t * L + 0.04, FLOOR), root)
    # Motore sullo specchio, al centro; scaletta e fanale.
    empty("Mount_Motor", (0.0, Y0, sheer_z(0.0)), root)
    empty("Mount_Ladder", (0.55, Y0, sheer_z(0.0) - 0.06), root)
    empty("Mount_NavLight", (-0.62, Y0 + 0.08, sheer_z(0.0) - 0.06), root)
    # Pattino di chiglia (profilo fisso della fisica, FixedFoils): presa laterale in virata.
    empty("Mount_Skeg", (0.0, Y0 + 0.28 * L, keel_z(0.28) - 0.03), root)
    sk = Builder()
    sk.box(-0.012, 0.012, Y0 + 0.04 * L, Y0 + 0.55 * L, keel_z(0.3) - 0.06, keel_z(0.3) + 0.01, "Rubber")
    to_object("Skeg", sk, root)

    to_object("Hull", b, root)
    collision_hull(root)
    collision_deck(root, solids)
    hydro_hull(root, barchino_ring, 20)
    swim_shell(root, barchino_ring, 20)
    return export("Barchino_" + variant)


# =====================================================================================
# Placeholder dislocanti (gozzo, peschereccio): forme semplici per provare entrobordo e timone
# =====================================================================================
#
# Scafo parametrico: sezioni a superellisse dalla chiglia alla falchetta, origine a mezza lunghezza
# sulla LINEA D'ACQUA di progetto (z = 0). Non sono modelli finali: servono alla fisica (mesh
# idrodinamica, attacchi di elica, timone, motore, elica di prua) e a salire a bordo.

class HullSpec:
    def __init__(self, **kw):
        self.__dict__.update(kw)


def ph_half_beam(s, t):
    if t >= 0.5:
        u = (t - 0.5) / 0.5
        return s.B / 2 * max(0.0, 1.0 - u ** 2.2) ** 0.5
    u = (0.5 - t) / 0.5
    if s.canoe:
        return s.B / 2 * max(0.0, 1.0 - u ** 2.6) ** 0.5
    return s.B / 2 * (s.stern_beam + (1.0 - s.stern_beam) * (1.0 - u ** 2))


def ph_sheer(s, t):
    if t >= 0.45:
        u = (t - 0.45) / 0.55
        return s.sheer_mid + (s.sheer_bow - s.sheer_mid) * u * u
    u = (0.45 - t) / 0.45
    return s.sheer_mid + (s.sheer_stern - s.sheer_mid) * u * u


def ph_keel(s, t):
    z = -s.T
    if t > s.forefoot:
        u = (t - s.forefoot) / (1.0 - s.forefoot)
        z = -s.T + (ph_sheer(s, 1.0) - 0.05 + s.T) * u ** 1.8
    if t < s.afterfoot:
        u = (s.afterfoot - t) / s.afterfoot
        z = max(z, -s.T + s.T * s.stern_rise * u ** 1.6)
    return z


def ph_half_section(s, t, n=9):
    hb, zk, zs = ph_half_beam(s, t), ph_keel(s, t), ph_sheer(s, t)
    full = s.full_end + (s.full_mid - s.full_end) * (1.0 - abs(2 * t - 1) ** 2)
    pts = []
    for i in range(n):
        th = i / (n - 1) * math.pi / 2
        x = hb * math.sin(th) ** (2.0 / full)
        z = zs - (zs - zk) * math.cos(th) ** (2.0 / full)
        pts.append((x, z))
    return pts


def ph_ring(s, t):
    y = -s.L / 2 + t * s.L
    half = ph_half_section(s, t)
    return [(-x, y, z) for x, z in reversed(half[1:])] + [(x, y, z) for x, z in half]


def ph_x_at(s, t, z):
    pts = ph_half_section(s, t)
    for (xa, za), (xb, zb) in zip(pts, pts[1:]):
        if za <= z <= zb and zb > za:
            return xa + (xb - xa) * (z - za) / (zb - za)
    return pts[-1][0] if z > pts[-1][1] else 0.0


def ph_shell(b, s, floor):
    """Fasciame esterno (liscio), murate interne sopra il pagliolo, capodibanda."""
    n_st = 40
    ts = [i / (n_st - 1) for i in range(n_st)]
    rings = [ph_ring(s, t) for t in ts]
    for a, c in zip(rings, rings[1:]):
        b.quad_strip(a, c, "Hull", smooth=True, flip=True)
    if not s.canoe:
        b.poly(rings[0], "Hull")
    # Murata interna: dal pagliolo alla falchetta, rientrata di 5 cm, faccia verso l'interno.
    for side in (1, -1):
        prev = None
        for t in ts:
            zs = ph_sheer(s, t)
            if ph_keel(s, t) > floor:
                prev = None
                continue
            y = -s.L / 2 + t * s.L
            xf = max(ph_x_at(s, t, floor) - 0.05, 0.0)
            xs = max(ph_half_beam(s, t) - 0.05, 0.0)
            cur = [(side * xf, y, floor), (side * xs, y, zs)]
            if prev is not None:
                q = [prev[0], prev[1], cur[1], cur[0]]
                b.poly(q if side < 0 else q[::-1], "Interior")
            prev = cur
    # Capodibanda: striscia piatta sulla falchetta.
    for side in (1, -1):
        for t0, t1 in zip(ts, ts[1:]):
            y0, y1 = -s.L / 2 + t0 * s.L, -s.L / 2 + t1 * s.L
            h0, h1 = ph_half_beam(s, t0), ph_half_beam(s, t1)
            z0, z1 = ph_sheer(s, t0) + 0.01, ph_sheer(s, t1) + 0.01
            q = [(side * h0, y0, z0), (side * h1, y1, z1), (side * max(h1 - 0.06, 0), y1, z1), (side * max(h0 - 0.06, 0), y0, z0)]
            b.poly(q if side > 0 else q[::-1], "Stripe")


def ph_deck_plate(b, s, floor, t0, t1, mat="Deck"):
    """Pagliolo/coperta piatta fra le stazioni t0 e t1, larga quanto lo scafo a quella quota."""
    for i in range(16):
        ta, tb = t0 + (t1 - t0) * i / 16, t0 + (t1 - t0) * (i + 1) / 16
        ya, yb = -s.L / 2 + ta * s.L, -s.L / 2 + tb * s.L
        xa, xb = max(ph_x_at(s, ta, floor) - 0.05, 0.0), max(ph_x_at(s, tb, floor) - 0.05, 0.0)
        b.poly([(-xa, ya, floor), (xa, ya, floor), (xb, yb, floor), (-xb, yb, floor)], mat)


def ph_deck_solids(s, z0, z1, t0, t1, n=10):
    """Scatole calpestabili che seguono la larghezza dello scafo (niente pavimento fuori bordo)."""
    out = []
    for i in range(n):
        ta, tb = t0 + (t1 - t0) * i / n, t0 + (t1 - t0) * (i + 1) / n
        x = min(ph_x_at(s, ta, z1), ph_x_at(s, tb, z1)) - 0.05
        if x > 0.1:
            out.append((-x, x, -s.L / 2 + ta * s.L, -s.L / 2 + tb * s.L, z0, z1))
    return out


def ph_rudder(name, parent, location, span, chord):
    """Pala del timone: origine sull'asse (centro di pressione), ruota attorno alla verticale."""
    b = Builder()
    b.box(-0.02 * chord / 0.3, 0.02 * chord / 0.3, -chord * 0.75, chord * 0.25, -span / 2, span / 2, "MotorTrim")
    b.cylinder((0, 0, span / 2), (0, 0, span / 2 + 0.4 * span), 0.025 * max(chord / 0.4, 1.0), "Metal", seg=8)
    return to_object(name, b, parent, location)


def ph_propeller(name, parent, location, radius, blades=4, nozzle=False):
    b = Builder()
    b.cylinder((0, -0.06 * radius / 0.2, 0), (0, 0.06 * radius / 0.2, 0), radius * 0.18, "Metal", seg=10)
    for k in range(blades):
        a = k / blades * math.tau
        c, sn = math.cos(a), math.sin(a)
        r0, r1 = radius * 0.18, radius * 0.95
        w = radius * 0.28
        pts = [(c * r0 - sn * w * 0.5, -0.02, sn * r0 + c * w * 0.5), (c * r1 - sn * w * 0.4, 0.02, sn * r1 + c * w * 0.4),
               (c * r1 + sn * w * 0.4, -0.02, sn * r1 - c * w * 0.4), (c * r0 + sn * w * 0.5, 0.02, sn * r0 - c * w * 0.5)]
        b.poly(pts, "Metal")
        b.poly(pts[::-1], "Metal")
    obj = to_object(name, b, parent, location)
    if nozzle:
        n = Builder()
        seg = 16
        ra = [(math.cos(i / seg * math.tau) * radius * 1.05, -radius * 0.35, math.sin(i / seg * math.tau) * radius * 1.05) for i in range(seg)]
        rb = [(x, radius * 0.35, z) for x, _, z in ra]
        for i in range(seg):
            j = (i + 1) % seg
            n.poly([ra[i], ra[j], rb[j], rb[i]], "MotorTrim")
            n.poly([rb[i], rb[j], ra[j], ra[i]], "MotorTrim")
        to_object(name + "Nozzle", n, parent, location)
    return obj


def helm_seat(b, solids, root, s, x, y, floor):
    """Sedile del pilota + consolle davanti (come il barchino: piedi a Seat_Helm, consolle +0,29 m)."""
    seat = floor + 0.46
    b.box(x - 0.25, x + 0.25, y - 0.28, y + 0.12, floor, seat, "Interior")
    b.box(x - 0.25, x + 0.25, y - 0.28, y + 0.12, seat, seat + 0.06, "Cushion")
    solids.append((x - 0.25, x + 0.25, y - 0.28, y + 0.12, floor, seat))
    empty("Seat_Helm", (x, y + 0.04, floor), root)
    empty("Mount_Console", (x, y + 0.29, floor), root)


def build_gozzo():
    reset_scene()
    s = HullSpec(L=7.0, B=2.4, T=0.55, canoe=True, stern_beam=0.0, sheer_stern=0.95, sheer_mid=0.75,
                 sheer_bow=1.10, forefoot=0.78, afterfoot=0.14, stern_rise=0.75, full_mid=2.6, full_end=1.7)
    root = empty("Gozzo_Placeholder", (0, 0, 0))
    floor = 0.22
    b = Builder()
    ph_shell(b, s, floor)
    ph_deck_plate(b, s, floor, 0.12, 0.86)
    ph_deck_plate(b, s, ph_sheer(s, 0.86) - 0.04, 0.86, 0.985)          # coperta di prua
    # Cofano del motore a meta' barca, panche di traverso.
    b.box(-0.4, 0.4, -1.3, -0.3, floor, floor + 0.55, "Motor")
    b.box(-0.95, 0.95, 0.9, 1.2, floor, floor + 0.42, "Interior")
    solids = ph_deck_solids(s, floor - 0.05, floor, 0.12, 0.86) + [
              (-0.4, 0.4, -1.3, -0.3, floor, floor + 0.55),
              (-0.95, 0.95, 0.9, 1.2, floor, floor + 0.42)]
    for t0, t1 in ((0.12, 0.3), (0.3, 0.5), (0.5, 0.7), (0.7, 0.86)):
        for side in (1, -1):
            x = side * (ph_half_beam(s, (t0 + t1) / 2) - 0.08)
            solids.append((min(x, x - side * 0.08), max(x, x - side * 0.08), -s.L / 2 + t0 * s.L, -s.L / 2 + t1 * s.L, floor, ph_sheer(s, t1)))
    helm_seat(b, solids, root, s, 0.45, -1.9, floor)
    to_object("Hull", b, root)

    # Entrobordo: elica davanti al dritto di poppa, asse inclinato di 6°, timone sul dritto.
    empty("Mount_Prop", (0.0, -2.75, -0.42), root, rotation=(math.radians(6.0), 0, 0))
    empty("Mount_Rudder", (0.0, -3.12, -0.35), root)
    empty("Mount_Engine", (0.0, -0.8, 0.0), root)
    empty("Mount_Keel", (0.0, 0.0, -0.62), root)
    empty("Mount_Ladder", (ph_half_beam(s, 0.2), -2.1, ph_sheer(s, 0.2) - 0.06), root, rotation=(0, 0, math.radians(90)))
    ph_rudder("Rudder", root, (0.0, -3.12, -0.35), 0.5, 0.32)
    ph_propeller("Propeller", root, (0.0, -2.75, -0.42), 0.20, blades=3)
    # Chiglia visibile (la fisica la tratta come profilo, FixedFoils).
    k = Builder()
    k.box(-0.05, 0.05, -2.4, 2.2, -0.70, -0.45, "MotorTrim")
    to_object("Keel", k, root)

    hydro_hull(root, lambda t: ph_ring(s, t), 26)
    hydro_hull(root, lambda t: ph_ring(s, t), 10, name="HullCollision")
    swim_shell(root, lambda t: ph_ring(s, t), 26)
    collision_deck(root, solids)
    return export("Gozzo_Placeholder")


def build_trawler():
    reset_scene()
    s = HullSpec(L=20.0, B=6.5, T=2.3, canoe=False, stern_beam=0.82, sheer_stern=1.45, sheer_mid=1.40,
                 sheer_bow=2.9, forefoot=0.80, afterfoot=0.20, stern_rise=0.55, full_mid=2.8, full_end=1.6)
    root = empty("Peschereccio_Placeholder", (0, 0, 0))
    deck = 1.40
    b = Builder()
    ph_shell(b, s, deck)
    ph_deck_plate(b, s, deck, 0.0, 0.97)
    # Murate (impavesata) alte 1 m tranne un varco a poppa per salire dall'acqua.
    solids = ph_deck_solids(s, deck - 0.2, deck, 0.0, 0.97, 20)
    for t0, t1 in [(i / 10, (i + 1) / 10) for i in range(10)]:
        for side in (1, -1):
            y0, y1 = -10.0 + t0 * 20.0, -10.0 + t1 * 20.0
            x = side * (ph_half_beam(s, (t0 + t1) / 2) - 0.12)
            z1 = ph_sheer(s, t1) + 1.0
            b.box(min(x, x - side * 0.1), max(x, x - side * 0.1), y0, y1, deck, z1, "Hull")
            solids.append((min(x, x - side * 0.1), max(x, x - side * 0.1), y0, y1, deck, z1))
    # Specchio: due tratti ai lati, varco di 2 m al centro.
    for x0, x1 in ((-3.0, -1.0), (1.0, 3.0)):
        b.box(x0, x1, -10.0, -9.85, deck, deck + 1.0, "Hull")
        solids.append((x0, x1, -10.0, -9.85, deck, deck + 1.0))
    # Timoneria: aperta dietro, tetto, finestre come vetro.
    wy0, wy1, wx = 1.5, 5.5, 1.9
    roof = deck + 2.5
    b.box(-wx, wx, wy1 - 0.1, wy1, deck, roof, "Interior")
    b.box(-wx, -wx + 0.1, wy0, wy1, deck, roof, "Interior")
    b.box(wx - 0.1, wx, wy0, wy1, deck, roof, "Interior")
    b.box(-wx, wx, wy0, wy1, roof, roof + 0.12, "Deck")
    b.box(-wx + 0.15, wx - 0.15, wy1, wy1 + 0.01, deck + 1.2, roof - 0.2, "Glass")
    solids += [(-wx, wx, wy1 - 0.1, wy1, deck, roof), (-wx, -wx + 0.1, wy0, wy1, deck, roof),
               (wx - 0.1, wx, wy0, wy1, deck, roof), (-wx, wx, wy0, wy1, roof, roof + 0.12)]
    # Portale di poppa (gantry) e cassone della stiva.
    for x in (-2.6, 2.6):
        b.box(x - 0.12, x + 0.12, -9.4, -9.15, deck, deck + 4.0, "Metal")
    b.box(-2.6, 2.6, -9.4, -9.15, deck + 3.8, deck + 4.0, "Metal")
    b.box(-1.2, 1.2, -4.0, -1.5, deck, deck + 0.5, "Deck")
    solids.append((-1.2, 1.2, -4.0, -1.5, deck, deck + 0.5))
    helm_seat(b, solids, root, s, 0.6, 3.9, deck)
    to_object("Hull", b, root)

    empty("Mount_Prop", (0.0, -8.4, -1.45), root, rotation=(math.radians(3.0), 0, 0))
    empty("Mount_Rudder", (0.0, -9.35, -1.40), root)
    empty("Mount_Engine", (0.0, -3.0, -0.9), root)
    empty("Mount_Keel", (0.0, -1.0, -2.45), root)
    empty("Mount_Thruster", (0.0, 7.6, -1.4), root)
    empty("Mount_Ladder", (0.0, -10.0, deck - 0.06), root)
    ph_rudder("Rudder", root, (0.0, -9.35, -1.40), 2.0, 1.1)
    ph_propeller("Propeller", root, (0.0, -8.4, -1.45), 0.8, blades=4, nozzle=True)
    k = Builder()
    k.box(-0.15, 0.15, -7.6, 6.0, -2.6, -2.25, "MotorTrim")
    to_object("Keel", k, root)

    hydro_hull(root, lambda t: ph_ring(s, t), 34)
    hydro_hull(root, lambda t: ph_ring(s, t), 12, name="HullCollision")
    swim_shell(root, lambda t: ph_ring(s, t), 34)
    collision_deck(root, solids)
    return export("Peschereccio_Placeholder")


# =====================================================================================
# Fuoribordo
# =====================================================================================

def rounded_box(b, cx, cy, z0, w, l, h, mat, bevel=0.04, seg=3, top_round=0.08):
    """Scatola con spigoli verticali e bordo superiore arrotondati (cofano)."""
    rings = []
    levels = [z0, z0 + h - top_round] + [z0 + h - top_round + top_round * math.sin((k + 1) / seg * math.pi / 2) for k in range(seg)]
    insets = [0.0, 0.0] + [top_round * (1 - math.cos((k + 1) / seg * math.pi / 2)) for k in range(seg)]
    for z, inset in zip(levels, insets):
        ring = []
        hw, hl = w / 2 - inset, l / 2 - inset
        r = min(bevel, hw - 0.005, hl - 0.005)
        for cxs, cys, a0 in ((hw - r, hl - r, 0), (-(hw - r), hl - r, 90), (-(hw - r), -(hl - r), 180), (hw - r, -(hl - r), 270)):
            for k in range(seg + 1):
                a = math.radians(a0 + 90 * k / seg)
                ring.append((cx + cxs + r * math.cos(a), cy + cys + r * math.sin(a), z))
        rings.append(ring)
    for a, c in zip(rings, rings[1:]):
        b.quad_strip(a + a[:1], c + c[:1], mat, smooth=True)
    b.poly(rings[0][::-1], mat)
    b.poly(rings[-1], mat)


def propeller(name, parent, location, radius, blades=3):
    b = Builder()
    hub_l = radius * 0.9
    b.cylinder((0, -hub_l * 0.5, 0), (0, hub_l * 0.5, 0), radius * 0.22, "MotorTrim", seg=10)
    for k in range(blades):
        a = k / blades * math.tau
        pts = []
        for r_frac, chord, twist in ((0.22, 0.55, 0.9), (0.6, 0.95, 0.6), (0.92, 0.75, 0.4), (1.0, 0.25, 0.35)):
            r = radius * r_frac
            c = radius * chord
            for s in (-1, 1):
                ang = a + s * c / max(r, 0.01) * 0.5
                pts.append((ang, r, s * c * 0.5 * math.sin(twist)))
        lead = [(math.cos(an) * r, dy, math.sin(an) * r) for an, r, dy in pts[0::2]]
        trail = [(math.cos(an) * r, dy, math.sin(an) * r) for an, r, dy in pts[1::2]]
        for i in range(len(lead) - 1):
            q = [lead[i], lead[i + 1], trail[i + 1], trail[i]]
            b.poly(q, "Metal", True)
            b.poly(q[::-1], "Metal", True)
    return to_object(name, b, parent, location, sharp_deg=60)


def build_outboard(name, scale, tiller=True):
    """Fuoribordo: staffa fissa allo specchio, il resto ruota attorno al piantone (`Swivel`)."""
    reset_scene()
    s = scale
    root = empty(name, (0, 0, 0))
    # Staffa a morsetto sul bordo dello specchio (origine = bordo superiore dello specchio).
    b = Builder()
    b.box(-0.09 * s, 0.09 * s, -0.04, 0.03, -0.20, 0.03, "MotorTrim")
    b.box(-0.06 * s, 0.06 * s, -0.12, -0.04, -0.16, 0.02, "MotorTrim")
    to_object("Bracket", b, root)

    swivel = empty("Swivel", (0.0, -0.12, 0.0), root)
    # Cofano (testata) sopra lo specchio, dietro il piantone.
    b = Builder()
    rounded_box(b, 0.0, -0.18 * s, 0.10, 0.38 * s, 0.56 * s, 0.42 * s, "Motor", bevel=0.09 * s, top_round=0.10 * s)
    rounded_box(b, 0.0, -0.17 * s, 0.02, 0.36 * s, 0.50 * s, 0.10, "MotorTrim", bevel=0.08 * s, top_round=0.02)
    to_object("Cowling", b, swivel)
    # Gambo e piede: piastra antiventilazione all'altezza della chiglia (0,80 sotto lo specchio).
    b = Builder()
    shaft_bottom = -0.78    # gambo lungo: piastra antiventilazione all'altezza della chiglia
    b.box(-0.06 * s, 0.06 * s, -0.22, -0.02, shaft_bottom, 0.04, "Motor")
    b.box(-0.15 * s, 0.15 * s, -0.36 * s, 0.04, shaft_bottom - 0.02, shaft_bottom, "Motor")
    prop_z = shaft_bottom - 0.13 * s
    gear_r = 0.055 * s
    b.cylinder((0, -0.30 * s, prop_z), (0, 0.10, prop_z), gear_r, "Motor", seg=12)
    b.box(-0.025, 0.025, -0.16 * s, 0.06, prop_z - 0.20 * s, prop_z, "Motor")
    to_object("LowerUnit", b, swivel)
    propeller("Propeller", swivel, (0.0, -0.34 * s, prop_z), 0.13 * s)
    # Spinta: dall'elica, verso prua (+Y Blender = -Z Godot).
    empty("Thrust", (0.0, -0.34 * s, prop_z), swivel)

    if tiller:
        b = Builder()
        grip_y, grip_z = 0.86, 0.12
        b.cylinder((0, 0.05, 0.30), (0, grip_y - 0.16, grip_z + 0.04), 0.022, "MotorTrim", seg=8)
        b.cylinder((0, grip_y - 0.18, grip_z + 0.045), (0, grip_y + 0.02, grip_z), 0.028, "Rubber", seg=10)
        to_object("Tiller", b, swivel)
        empty("TillerGrip", (0.0, grip_y - 0.08, grip_z + 0.02), swivel)
    return export(name)


# =====================================================================================
# Consolle e accessori
# =====================================================================================

def build_console():
    """Consolle con parabrezza e volante. Origine = Mount_Console (0,25 m davanti ai piedi del pilota)."""
    reset_scene()
    root = empty("Console_Helm", (0, 0, 0))
    b = Builder()
    y0, y1 = 0.16, 0.62
    # Corpo: sale verso prua, cruscotto inclinato verso il pilota.
    prof = [(y0, 0.0), (y0, 0.62), (y0 + 0.18, 0.86), (y1, 0.90), (y1, 0.0)]
    for side in (-1, 1):
        x = side * 0.27
        b.poly([(x, y, z) for y, z in (prof if side > 0 else prof[::-1])], "Interior")
    for (ya, za), (yb, zb) in zip(prof, prof[1:] + prof[:1]):
        b.poly([(-0.27, ya, za), (0.27, ya, za), (0.27, yb, zb), (-0.27, yb, zb)], "Interior")
    # Strumenti sul cruscotto.
    for x in (-0.12, 0.12):
        b.cylinder((x, y0 + 0.10, 0.76), (x, y0 + 0.085, 0.775), 0.045, "Gauge", seg=12)
    # Parabrezza curvo.
    glass = []
    for k in range(9):
        a = math.radians(-70 + 140 * k / 8)
        glass.append((0.30 * math.sin(a), y1 - 0.02 - 0.12 * (1 - math.cos(a)), 0.90))
    top = [(x * 0.92, y + 0.05, z + 0.32) for x, y, z in glass]
    b.quad_strip(glass, top, "Glass", smooth=True)
    b.quad_strip(top, glass, "Glass", smooth=True)
    b.tube([(p[0], p[1] + 0.005, p[2] + 0.005) for p in top], 0.012, "Metal")
    to_object("Console", b, root)

    # Volante: piantone inclinato di 30° verso il pilota; le mani a "10 e 10".
    hub = Vector((0.0, 0.07, 0.83))
    wheel = empty("Wheel", hub, root, rotation=(math.radians(-60), 0, 0))
    b = Builder()
    r = 0.17
    ring = [(r * math.cos(a), r * math.sin(a), 0.0) for a in (k / 24 * math.tau for k in range(25))]
    b.tube(ring, 0.014, "Rubber", seg=8)
    for k in range(3):
        a = math.radians(90 + k * 120)
        b.cylinder((0, 0, 0.02), (r * math.cos(a), r * math.sin(a), 0.0), 0.008, "Metal", seg=6)
    b.cylinder((0, 0, -0.06), (0, 0, 0.03), 0.03, "Metal", seg=10)
    to_object("WheelMesh", b, wheel)
    # Godot: Y locale della ruota = piantone (Z Blender). Mani a ±60° dall'alto.
    # Y locale della corona (Blender) punta verso il fondo del volante: la cima e' a 270°.
    for nm, a in (("Hand_L", 210), ("Hand_R", 330)):
        e = empty(nm, (r * math.cos(math.radians(a)), r * math.sin(math.radians(a)), 0.0), wheel)
    return export("Console_Helm")


def build_ladder():
    reset_scene()
    root = empty("Acc_Ladder", (0, 0, 0))
    b = Builder()
    for x in (-0.11, 0.11):
        b.tube([(x, 0.02, 0.05), (x, -0.10, 0.10), (x, -0.16, 0.0), (x, -0.16, -0.55)], 0.012, "Metal")
    for z in (-0.12, -0.30, -0.48):
        b.box(-0.11, 0.11, -0.19, -0.13, z - 0.012, z + 0.012, "Metal")
    to_object("Ladder", b, root)
    return export("Acc_Ladder")


def build_navlight():
    reset_scene()
    root = empty("Acc_NavLight", (0, 0, 0))
    b = Builder()
    b.cylinder((0, 0, 0), (0, 0, 0.75), 0.012, "Metal", seg=8)
    b.cylinder((0, 0, 0.75), (0, 0, 0.82), 0.03, "Cushion", seg=10)
    to_object("NavLight", b, root)
    empty("Lamp", (0, 0, 0.785), root)
    return export("Acc_NavLight")


def render_preview(path, objects_from):
    """Anteprima Workbench di un .glb appena scritto (controllo a occhio della forma)."""
    reset_scene()
    with view3d_override():
        bpy.ops.import_scene.gltf(filepath=objects_from)
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.display.shading.light = "STUDIO"
    scene.display.shading.color_type = "MATERIAL"
    scene.render.resolution_x, scene.render.resolution_y = 1200, 800
    cam = bpy.data.objects.new("Cam", bpy.data.cameras.new("Cam"))
    scene.collection.objects.link(cam)
    big = "Barchino" in objects_from
    cam.location = (4.2, -3.4, 2.6) if big else (1.4, -1.2, 0.6)
    direction = (Vector((0, 0.2, 0.1)) if big else Vector((0, -0.2, 0.0))) - cam.location
    cam.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()
    cam.data.lens = 35
    scene.camera = cam
    for o in scene.objects:
        if o.name.endswith("Collision"):
            o.hide_render = True
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)


import os
os.makedirs(OUT_DIR, exist_ok=True)
written = [
    build_barchino("Tiller"),
    build_barchino("Console"),
    build_gozzo(),
    build_trawler(),
    build_outboard("Outboard_25", 1.0, tiller=True),
    build_outboard("Outboard_40", 1.15, tiller=True),
    build_console(),
    build_ladder(),
    build_navlight(),
]
previews = []
if PREVIEW:
    # Le anteprime non vanno nel progetto: cartella passata dopo "preview".
    preview_dir = ARGV[ARGV.index("preview") + 1]
    for name in ("Barchino_Tiller", "Barchino_Console", "Outboard_25", "Console_Helm"):
        p = "{}/preview_{}.png".format(preview_dir, name)
        render_preview(p, "{}/{}.glb".format(OUT_DIR, name))
        previews.append(p)
result = {"written": written, "previews": previews}
