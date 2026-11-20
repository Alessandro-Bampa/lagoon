"""Controllo di un modello di barca (.glb) prima dell'import in Godot (skill vehicles-boats,
pipeline-nuove-barche.md §6.2).

Uso (Blender SEPARATO, in background: lo script svuota la scena e importa il .glb; non va eseguito
nel Blender dell'utente via MCP):

    blender --background --factory-startup --python-exit-code 3 \
        --python tools/blender/check_boat_model.py -- <file.glb> [--dwl Z] [--band M] [--console C.glb]

--dwl = quota (Blender Z, metri) della linea d'acqua di progetto nello spazio del modello (default 0).
--band = fascia sopra la linea d'acqua in cui si misura la finezza del fasciame (default 0,3 m).
--console = consolle da montare su Mount_Console per il controllo della visuale (barche a volante).
Stampa: nodi obbligatori/attacchi, integrita' di HydroHull (chiusa, manifold, verso coerente,
nessuna autointersezione), triangoli e lati, dimensioni, volume e carena alla linea d'acqua, e la
visuale del pilota seduto (raggi dagli occhi oltre la timoneria, vetri esclusi).
Esce con codice 1 se un controllo bloccante fallisce.
"""

import math
import sys

import bpy
import bmesh
from mathutils import Vector
from mathutils.bvhtree import BVHTree

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
if not argv:
    print("uso: blender --background --factory-startup --python check_boat_model.py -- <file.glb> [--dwl Z]")
    sys.exit(2)
PATH = argv[0]
DWL = float(argv[argv.index("--dwl") + 1]) if "--dwl" in argv else 0.0
# Fascia sopra la linea d'acqua che onde, sbandamento e assetto bagnano (m).
BAND = float(argv[argv.index("--band") + 1]) if "--band" in argv else 0.3
# Consolle (.glb) da montare su Mount_Console per il controllo della visuale al timone.
CONSOLE = argv[argv.index("--console") + 1] if "--console" in argv else ""
RHO = 1025.0

ROLE_MESHES = ("HydroHull", "HullCollision", "DeckCollision", "SwimCollision")
GODOT_SUFFIXES = ("-col", "-colonly", "-convcol", "-convcolonly", "-noimp", "-rigid", "-navmesh",
                  "-occ", "-occonly", "-vehicle", "-wheel", "-loop", "-cycle")

errors, warnings = [], []


def base(name):
    """Nome senza il suffisso .001 che Blender aggiunge ai duplicati."""
    return name.split(".")[0]


bpy.ops.wm.read_homefile(use_empty=True)
try:
    bpy.ops.import_scene.gltf(filepath=PATH)
except RuntimeError as exc:
    print("[ERRORE] import di '%s' fallito: %s" % (PATH, exc))
    sys.exit(2)
objects = list(bpy.context.scene.objects)
by_name = {}
for o in objects:
    by_name.setdefault(base(o.name), []).append(o)

# --- Nomi --------------------------------------------------------------------------------
for name, objs in by_name.items():
    if len(objs) > 1 and (name in ROLE_MESHES or name.startswith(("Mount_", "Seat_", "Bay_"))):
        errors.append("nome duplicato '%s' (%d volte): Godot ne trova solo il primo" % (name, len(objs)))
    low = name.lower()
    for suf in GODOT_SUFFIXES:
        if low.endswith(suf):
            errors.append("'%s' finisce con il suffisso d'import di Godot '%s'" % (name, suf))

for required in ("HydroHull", "HullCollision", "DeckCollision", "SwimCollision", "Seat_Helm"):
    if required not in by_name:
        errors.append("manca il nodo obbligatorio '%s'" % required)

mounts = sorted(n for n in by_name if n.startswith(("Mount_", "Seat_", "Bay_")))
print("[nodi] attacchi:", ", ".join(mounts) if mounts else "(nessuno)")
if "Mount_Motor" not in by_name and "Mount_Prop" not in by_name:
    warnings.append("ne' Mount_Motor (fuoribordo) ne' Mount_Prop (entrobordo): barca senza propulsione")
for under in ("Mount_Prop", "Mount_Rudder", "Mount_Keel", "Mount_Skeg", "Mount_Thruster"):
    if under in by_name:
        z = by_name[under][0].matrix_world.translation.z
        if z >= DWL:
            errors.append("%s e' sopra la linea d'acqua (z %.3f >= %.3f)" % (under, z, DWL))


# --- Mesh in coordinate mondo, saldata come fa Godot (HydroHull.FromFaces: 1 mm) ------------
def world_bmesh(obj, weld=0.0005):
    bm = bmesh.new()
    deps = bpy.context.evaluated_depsgraph_get()
    bm.from_mesh(obj.evaluated_get(deps).to_mesh())
    bm.transform(obj.matrix_world)
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=weld)
    bmesh.ops.triangulate(bm, faces=bm.faces)
    bm.verts.ensure_lookup_table()
    bm.faces.ensure_lookup_table()
    return bm


def tri_count(obj):
    deps = bpy.context.evaluated_depsgraph_get()
    mesh = obj.evaluated_get(deps).to_mesh()
    return sum(len(p.vertices) - 2 for p in mesh.polygons)


def check_hydro(obj):
    bm = world_bmesh(obj)
    tris = len(bm.faces)
    degenerate = [f for f in bm.faces if f.calc_area() < 1e-6]
    non_manifold = [e for e in bm.edges if len(e.link_faces) != 2]
    boundary = [e for e in bm.edges if len(e.link_faces) == 1]
    loose = [v for v in bm.verts if not v.link_faces]

    # Verso coerente: su un lato condiviso le due facce lo percorrono in versi opposti.
    flipped = 0
    for e in bm.edges:
        if len(e.link_faces) != 2:
            continue
        a, b = e.verts
        dirs = []
        for f in e.link_faces:
            vs = list(f.verts)
            i = vs.index(a)
            dirs.append(vs[(i + 1) % len(vs)] == b)
        if dirs[0] == dirs[1]:
            flipped += 1

    # Gusci (componenti connesse) e caratteristica di Eulero.
    seen, shells = set(), 0
    for f in bm.faces:
        if f.index in seen:
            continue
        shells += 1
        stack = [f]
        seen.add(f.index)
        while stack:
            g = stack.pop()
            for e in g.edges:
                for h in e.link_faces:
                    if h.index not in seen:
                        seen.add(h.index)
                        stack.append(h)
    euler = len(bm.verts) - len(bm.edges) + len(bm.faces)

    # Autointersezioni: coppie di triangoli che si toccano senza condividere vertici.
    tree = BVHTree.FromBMesh(bm, epsilon=0.0)
    intersections = 0
    for i, j in tree.overlap(tree):
        if i >= j:
            continue
        if set(bm.faces[i].verts) & set(bm.faces[j].verts):
            continue
        intersections += 1

    # Volume con segno (divergenza): positivo = normali verso l'esterno.
    volume = sum(f.calc_center_median().dot(f.normal) * f.calc_area() for f in bm.faces) / 3.0

    # Lati e qualita' dei triangoli. Si misura il FASCIAME BAGNABILE (facce con un vertice sotto
    # DWL + BAND): coperta e opera morta alta non toccano l'acqua, e le facce piane di specchio e
    # dritto (normale ~±Y) possono essere ventagli lunghi, perche' su una faccia piana il taglio
    # alla linea d'acqua e' esatto.
    max_edge = max(e.calc_length() for e in bm.edges)
    shell_edge, slivers = 0.0, 0
    for f in bm.faces:
        n = f.normal
        if n.z > 0.98 or abs(n.y) > 0.98 or min(v.co.z for v in f.verts) > DWL + BAND:
            continue
        ls = sorted(e.calc_length() for e in f.edges)
        shell_edge = max(shell_edge, ls[2])
        area = f.calc_area()
        # Qualita' 4·√3·A / Σl²: 1 equilatero, ~0 ago.
        if area > 1e-9:
            q = 4.0 * math.sqrt(3.0) * area / (ls[0] ** 2 + ls[1] ** 2 + ls[2] ** 2)
            if q < 0.1:
                slivers += 1

    xs = [v.co.x for v in bm.verts]
    ys = [v.co.y for v in bm.verts]
    zs = [v.co.z for v in bm.verts]

    # Carena alla linea d'acqua di progetto (acqua a z = DWL): taglio dei triangoli e
    # V = somma A·n_z·(z − DWL) sui pezzi immersi (il coperchio sul piano non contribuisce),
    # momento longitudinale ∫ y (z − DWL) n_z dA con l'integrale esatto di due funzioni lineari.
    sign = 1.0 if volume >= 0.0 else -1.0
    sub_v, sub_my, sub_mx, wet_area = 0.0, 0.0, 0.0, 0.0
    y_min, y_max = float("inf"), float("-inf")
    for f in bm.faces:
        pts = [v.co.copy() for v in f.verts]
        d = [p.z - DWL for p in pts]
        poly = []
        for k in range(3):
            p, q = pts[k], pts[(k + 1) % 3]
            dp, dq = d[k], d[(k + 1) % 3]
            if dp <= 0.0:
                poly.append(p)
            if (dp < 0.0) != (dq < 0.0):
                t = dp / (dp - dq)
                poly.append(p + (q - p) * t)
        if len(poly) < 3:
            continue
        n = f.normal * sign
        for k in range(1, len(poly) - 1):
            a, b, c = poly[0], poly[k], poly[k + 1]
            area = (b - a).cross(c - a).length * 0.5
            if area < 1e-12:
                continue
            wet_area += area
            za, zb, zc = a.z - DWL, b.z - DWL, c.z - DWL
            sub_v += area * n.z * (za + zb + zc) / 3.0
            for (fa, fb, fc), acc in (((a.y, b.y, c.y), "y"), ((a.x, b.x, c.x), "x")):
                integral = area / 12.0 * (fa * za + fb * zb + fc * zc + (fa + fb + fc) * (za + zb + zc))
                if acc == "y":
                    sub_my += n.z * integral
                else:
                    sub_mx += n.z * integral
            for p in (a, b, c):
                if abs(p.z - DWL) < 1e-6:
                    y_min, y_max = min(y_min, p.y), max(y_max, p.y)

    report = {
        "triangoli": tris,
        "gusci": shells,
        "eulero": euler,
        "lati_aperti": len(boundary),
        "lati_non_manifold": len(non_manifold),
        "verso_incoerente": flipped,
        "degeneri": len(degenerate),
        "vertici_isolati": len(loose),
        "autointersezioni": intersections,
        "volume_m3": round(abs(volume), 4),
        "lato_max_m": round(max_edge, 3),
        "lato_max_fasciame_m": round(shell_edge, 3),
        "aghi_fasciame": slivers,
        "L_m": round(max(ys) - min(ys), 3),
        "B_m": round(max(xs) - min(xs), 3),
        "H_m": round(max(zs) - min(zs), 3),
        "fondo_z": round(min(zs), 3),
    }
    if sub_v > 1e-9:
        report.update({
            "carena_dwl_m3": round(sub_v, 4),
            "dislocamento_dwl_kg": round(sub_v * RHO),
            "LCB_y_m": round(sub_my / sub_v, 3),
            "TCB_x_m": round(sub_mx / sub_v, 4),
            "LWL_m": round(y_max - y_min, 3) if y_max > y_min else 0.0,
            "pescaggio_m": round(DWL - min(zs), 3),
            "superficie_bagnata_m2": round(wet_area, 3),
        })
    bm.free()

    if boundary or non_manifold:
        errors.append("HydroHull non chiusa: %d lati aperti, %d non manifold" % (len(boundary), len(non_manifold)))
    if flipped:
        errors.append("HydroHull: %d lati con le facce in verso opposto (normali incoerenti)" % flipped)
    if intersections:
        errors.append("HydroHull: %d coppie di triangoli che si intersecano" % intersections)
    if volume < 0.0:
        warnings.append("HydroHull con normali verso l'interno (Godot le gira tutte: va bene se coerenti)")
    if euler != 2 * shells:
        warnings.append("HydroHull: Eulero %d con %d gusci (atteso %d): buchi o maniglie" % (euler, shells, 2 * shells))
    if degenerate:
        warnings.append("HydroHull: %d triangoli degeneri (Godot li scarta)" % len(degenerate))
    return report


if "HydroHull" in by_name:
    hydro = check_hydro(by_name["HydroHull"][0])
    print("[HydroHull]", " · ".join("%s %s" % (k, v) for k, v in hydro.items()))

# --- Triangoli per ruolo e della resa ----------------------------------------------------
visual = 0
for o in objects:
    if o.type != "MESH":
        continue
    n = tri_count(o)
    if base(o.name) in ROLE_MESHES:
        print("[ruolo] %-14s %6d triangoli" % (base(o.name), n))
    else:
        visual += n
print("[resa] %d triangoli visibili" % visual)

if "HullCollision" in by_name:
    o = by_name["HullCollision"][0]
    verts = len(o.data.vertices)
    if verts > 255:
        warnings.append("HullCollision ha %d vertici: Godot ne fa un convesso, tenerla sotto ~100" % verts)

# --- Visuale del pilota (pipeline §4.1, "Visuale al timone") -------------------------------
# Occhi del pilota seduto = Seat_Helm + SEATED_EYE in verticale (FirstPersonCamera.SeatedEyeHeight,
# fissato dalla posa seduta del gioco). Si lancia un ventaglio di raggi in avanti contro TUTTA la resa
# (consolle compresa, se passata con --console) tranne i vetri: un raggio e' "libero" se non trova
# nulla di opaco entro VIEW_CLEAR metri (oltre la timoneria). Il vaporetto, senza pedana, aveva gli
# occhi sotto il davanzale del parabrezza: 0 raggi liberi.
SEATED_EYE = 1.20
VIEW_CLEAR = 2.0
VIEW_YAWS = (-30, -20, -10, 0, 10, 20, 30)
VIEW_PITCHES = (0, -8, -16)


def check_helm_view():
    seat = by_name.get("Seat_Helm", [None])[0]
    if seat is None:
        return
    if CONSOLE:
        mount = by_name.get("Mount_Console", [None])[0]
        before = set(bpy.data.objects)
        bpy.ops.import_scene.gltf(filepath=CONSOLE)
        added = [o for o in bpy.data.objects if o not in before]
        if mount is None:
            warnings.append("--console passato ma lo scafo non ha Mount_Console: consolle ignorata")
        else:
            for o in added:
                if o.parent is None:
                    o.matrix_world = mount.matrix_world @ o.matrix_world
            bpy.context.view_layer.update()
    verts, tris = [], []
    for o in bpy.context.scene.objects:
        if o.type != "MESH" or base(o.name) in ROLE_MESHES:
            continue
        deps = bpy.context.evaluated_depsgraph_get()
        mesh = o.evaluated_get(deps).to_mesh()
        mw = o.matrix_world
        offset = len(verts)
        verts.extend(mw @ v.co for v in mesh.vertices)
        for p in mesh.polygons:
            slot = o.material_slots[p.material_index].material if o.material_slots else None
            if slot is not None and base(slot.name) == "Glass":
                continue
            vs = list(p.vertices)
            for k in range(1, len(vs) - 1):
                tris.append((offset + vs[0], offset + vs[k], offset + vs[k + 1]))
    tree = BVHTree.FromPolygons(verts, tris)
    eye = seat.matrix_world.translation + Vector((0.0, 0.0, SEATED_EYE))
    clear, blocked = 0, {}
    for pitch in VIEW_PITCHES:
        for yaw in VIEW_YAWS:
            a, b = math.radians(yaw), math.radians(pitch)
            d = Vector((math.sin(a) * math.cos(b), math.cos(a) * math.cos(b), math.sin(b)))
            hit, _n, _i, dist = tree.ray_cast(eye, d, VIEW_CLEAR)
            if hit is None:
                clear += 1
            else:
                blocked["%+d°/%+d°" % (yaw, pitch)] = round(dist, 2)
    total = len(VIEW_YAWS) * len(VIEW_PITCHES)
    frac = clear / total
    print("[visuale] occhi a z %.2f (Seat_Helm %.2f + %.2f): %d/%d raggi liberi oltre %.1f m%s" % (
        eye.z, seat.matrix_world.translation.z, SEATED_EYE, clear, total, VIEW_CLEAR,
        "" if CONSOLE else " (senza consolle: passare --console)"))
    if blocked:
        print("[visuale] bloccati (imbardata/beccheggio: distanza m):",
              ", ".join("%s %s" % kv for kv in sorted(blocked.items())))
    if frac < 0.3:
        errors.append("visuale al timone: solo %d/%d raggi liberi, il pilota non vede fuori (alzare "
                      "Seat_Helm con una pedana, abbassare consolle o davanzali)" % (clear, total))
    elif frac < 0.6:
        warnings.append("visuale al timone: %d/%d raggi liberi" % (clear, total))


check_helm_view()

for w in warnings:
    print("[avviso]", w)
for e in errors:
    print("[ERRORE]", e)
print("[esito]", "FALLITO" if errors else "OK", "(%d errori, %d avvisi)" % (len(errors), len(warnings)))
sys.exit(1 if errors else 0)
