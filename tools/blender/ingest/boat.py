"""Sotto-pipeline BOAT dell'ingresso degli asset (skill `asset-ingest` §5, `vehicles-boats` §9-10).

Chiamata da `ingest.py` per `category: boat` DOPO i passi base: il modello leggero (`low`, prua a +Y,
misura reale, texture originale stilizzata) e il sorgente pesante (`high`, stessa terna) sono in
scena. Produce cio' che Godot legge da un modello di barca (`BoatAssembly`):

  1. INVOLUCRO dello scafo dal sorgente pesante: solo i pezzi lunghi (fasciame, falchetta, coperta),
     non cabina e minuterie. Per ogni stazione y la mezza sezione esterna si legge con raggi
     (orizzontali da fuori e verticali da sotto): niente dipende da come e' modellato l'interno.
  2. LINEA D'ACQUA di progetto: dal dislocamento del manifesto (bisezione sul volume delle sezioni)
     oppure dall'immersione. L'origine va li': a mezza lunghezza, in mezzeria, z = 0 sulla DWL.
  3. `HydroHull` (pipeline-nuove-barche §4.2, metodo A): stazioni piu' fitte a prua, mezze sezioni
     semplificate (Douglas-Peucker, cosi' gli spigoli restano spigoli) con un vertice sulla DWL,
     specchio e prua chiusi, coperta piatta alla falchetta, saldata, triangolata, normali fuori.
     `SwimCollision` = le stesse sezioni senza coperta; `HullCollision` = poche sezioni (convesso).
  4. `DeckCollision` = la resa decimata (pagliolo, cabina, murate, sedili come sono nel modello)
     piu' eventuali scatole del manifesto (un ponticello dove si sale dall'acqua).
  5. VUOTI `Seat_Helm`, `Seat_P*`, `Mount_*` dal manifesto (metri nella terna finale; `snap: floor`
     li cala sul piano calpestabile sotto). `Mount_Console` = `Seat_Helm` + 0,25 m se richiesto.
  6. MATERIALI PER NOME per la livrea (`Hull`, `Deck`, `Interior`, ...): ogni faccia del leggero
     prende il materiale sorgente della faccia pesante piu' vicina, rinominato dal manifesto; i vetri
     (`Glass`, trasparente) si riconoscono dal colore della texture sorgente. Tutti i materiali
     usano la stessa texture cotta: senza livrea la barca ha i colori originali.
  7. USCITE: `assets/models/boats/<Name>.glb` (+ `.import` con le texture non compresse),
     `resources/boats/<id>.tres` (`BoatConfig` da un modello di configurazione, masse bilanciate
     perche' il baricentro cada sulla verticale del centro di carena).
  8. CONTROLLI: carena chiusa e coerente, volume e dislocamento alla DWL, LCB, bordo libero, GM
     stimato, lato del fasciame bagnato, scarto resa/carena, attacchi subacquei sotto la DWL, posto
     di guida (pagliolo sotto i piedi, seduta a 0,46 m, testa sotto il tetto, visuale in avanti).

Assi come `build_lagoon_boats.py`: Blender +Y = prua (-Z Godot), +X = dritta, +Z = su. Metri.
"""

import math
import os
import re

import bmesh
import bpy
import numpy as np
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree

import charlib as C

RHO = 1025.0
OUT_DIR = os.path.join(os.environ.get("TEMP", "/tmp"), "ingest")
ROLE_MESHES = ("HydroHull", "HullCollision", "DeckCollision", "SwimCollision")
# Materiali che la livrea ricolora (BoatLivery.ColorFor): con `livery_grey` la loro texture diventa
# grigio chiaro (la livrea la moltiplica), come i modelli procedurali.
LIVERY_ROLES = ("Hull", "Stripe", "Deck", "Interior", "Cushion", "Motor", "MotorTrim")
SEAT_HEIGHT = 0.46          # piano di seduta sopra i piedi (Sitting_Idle / Driving di UAL)
SEATED_EYE = 1.20           # FirstPersonCamera.SeatedEyeHeight
CONSOLE_AHEAD = 0.25        # Mount_Console davanti a Seat_Helm
UNDERWATER_MOUNTS = ("Mount_Prop", "Mount_Rudder", "Mount_Keel", "Mount_Skeg", "Mount_Thruster")

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
_subresources={{}}
gltf/naming_version=2
gltf/embedded_image_handling=3
gltf/texture_map_mode=1
"""


# =============================================================================================
# 1. Involucro dello scafo e sezioni
# =============================================================================================

def hull_envelope(high, min_length):
    """BVH dei soli pezzi lunghi del sorgente (componenti connesse lunghe almeno `min_length` volte
    la barca): fasciame, falchetta, coperta. Cabina, sedili, bitte e bulloni restano fuori, cosi'
    i raggi trovano la pelle esterna e la falchetta non "sale" sulle bitte."""
    lo, hi = bounds_of(high)
    length = hi.y - lo.y
    verts, polys = [], []
    for o in high:
        bm = bmesh.new()
        bm.from_mesh(o.data)
        bm.transform(o.matrix_world)
        bm.verts.ensure_lookup_table()
        seen = set()
        for f in bm.faces:
            if f.index in seen:
                continue
            comp, stack = [], [f]
            seen.add(f.index)
            while stack:
                g = stack.pop()
                comp.append(g)
                for e in g.edges:
                    for h in e.link_faces:
                        if h.index not in seen:
                            seen.add(h.index)
                            stack.append(h)
            ys = [v.co.y for g in comp for v in g.verts]
            if max(ys) - min(ys) < min_length * length:
                continue
            for g in comp:
                base = len(verts)
                verts.extend(v.co.copy() for v in g.verts)
                polys.append(list(range(base, base + len(g.verts))))
        bm.free()
    if not polys:
        raise ValueError("nessun pezzo lungo quanto lo scafo: abbassare boat.hull_min_length")
    pts = np.array([tuple(v) for v in verts])
    return BVHTree.FromPolygons(verts, polys), Vector(pts.min(axis=0)), Vector(pts.max(axis=0))


def bounds_of(objs):
    lo, hi = Vector((1e9,) * 3), Vector((-1e9,) * 3)
    for o in objs:
        for v in o.data.vertices:
            p = o.matrix_world @ v.co
            lo = Vector(map(min, lo, p))
            hi = Vector(map(max, hi, p))
    return lo, hi


def front_hit(tree, origin, d, dist):
    """Primo punto in cui il raggio entra nella pelle dall'esterno: le facce colpite dal retro (il
    pagliolo del pozzetto visto da sotto, oltre lo spigolo arrotondato dello specchio) si saltano."""
    o = Vector(origin)
    while dist > 0.0:
        loc, n, _i, t = tree.ray_cast(o, d, dist)
        if loc is None:
            return None
        if n.dot(d) < 0.0:
            return loc
        o = loc + d * 1e-4
        dist -= t + 1e-4
    return None


def dense_section(tree, y, lo, hi, dz=0.01):
    """Mezza sezione esterna (lato dritta) alla stazione y, dalla chiglia (x = 0) alla falchetta,
    come lista densa di (x, z); None se la stazione e' fuori dallo scafo."""
    span = hi.z - lo.z + 2.0
    xmax = max(abs(lo.x), abs(hi.x)) + 1.0
    up, inward = Vector((0, 0, 1)), Vector((-1, 0, 0))
    hit = front_hit(tree, (1e-4, y, lo.z - 1.0), up, span)
    if hit is None:
        return None
    z_keel = hit.z
    # Raggi orizzontali da fuori, dal basso in su: la falchetta e' dove il raggio smette di trovare
    # il fasciame (manca o colpisce qualcosa molto piu' dentro: cabina, murata opposta, coperta).
    side, z, prev = [], z_keel + dz, None
    while z < hi.z + dz:
        h = front_hit(tree, (xmax, y, z), inward, 2 * xmax)
        if h is None or h.x <= 0.0:
            if prev is not None:
                break
        else:
            if prev is not None and h.x < prev - max(0.05, 0.08 * prev):
                break
            prev = h.x
            side.append((h.x, z))
        z += dz
    if not side:
        return None
    # Murata mai rientrante salendo: bottazzo e tumblehome diventano una parete verticale alla
    # larghezza massima. Per la carena e' volume emerso trascurabile; per chi nuota e' meglio (una
    # sporgenza e' un soffitto). Senza, lo scalino del bottazzo cadeva a perimetri diversi da una
    # stazione all'altra e i triangoli fra le due si ripiegavano (autointersezioni).
    widest = 0.0
    for i, (x, z) in enumerate(side):
        widest = max(widest, x)
        side[i] = (widest, z)
    z_sheer = side[-1][1]
    # Raggi verticali da sotto: la forma del fondo dove e' quasi piano (lo trovano male i raggi
    # orizzontali, che cambiano x di molto per pochi millimetri di quota).
    bottom, x = [], dz
    x_top = max(p[0] for p in side)
    while x < x_top:
        h = front_hit(tree, (x, y, lo.z - 1.0), up, span)
        if h is not None and h.z < z_sheer:
            bottom.append((x, h.z))
        x += max(dz, x_top / 60.0)
    pts = [(0.0, z_keel)] + bottom + side
    # Tutti sulla pelle esterna: in ordine d'angolo attorno a un punto interno in mezzeria (sezioni
    # "stellate", vero per uno scafo; uno scalino come il bottazzo non le rompe).
    zc = 0.5 * (z_keel + z_sheer)
    pts.sort(key=lambda p: math.atan2(p[0], zc - p[1]))
    out = [pts[0]]
    for p in pts[1:]:
        if math.hypot(p[0] - out[-1][0], p[1] - out[-1][1]) > 0.004:
            out.append(p)
    out[0] = (0.0, out[0][1])
    return out


def simplify_polyline(pts, k, split=0.3):
    """Douglas-Peucker a numero fisso di punti: si aggiunge ogni volta il punto piu' lontano dalla
    spezzata corrente. Gli spigoli (chine, falchetta) sono i primi a entrare. L'ultima frazione
    `split` dei punti spezza invece i lati piu' lunghi (un fondo piatto non ha scarti, e senza
    resterebbe un lato da chiglia a sentina lungo quanto mezza larghezza)."""
    if len(pts) <= k:
        return resample(pts, k)
    keep = [0, len(pts) - 1]
    arc = [0.0]
    for a, b in zip(pts, pts[1:]):
        arc.append(arc[-1] + math.hypot(b[0] - a[0], b[1] - a[1]))
    n_dp = max(2, k - int(round(split * k)))
    while len(keep) < k:
        if len(keep) >= n_dp:
            keep.sort()
            a, b = max(zip(keep, keep[1:]), key=lambda s: arc[s[1]] - arc[s[0]])
            mid = 0.5 * (arc[a] + arc[b])
            i = min(range(a + 1, b), key=lambda j: abs(arc[j] - mid)) if b - a > 1 else None
            if i is None:
                break
            keep.append(i)
            continue
        best, best_d = None, -1.0
        keep.sort()
        for a, b in zip(keep, keep[1:]):
            ax, az = pts[a]
            bx, bz = pts[b]
            dx, dzz = bx - ax, bz - az
            n = math.hypot(dx, dzz) or 1e-9
            for i in range(a + 1, b):
                px, pz = pts[i]
                d = abs(dx * (az - pz) - dzz * (ax - px)) / n
                if d > best_d:
                    best, best_d = i, d
        if best is None:
            break
        keep.append(best)
    keep.sort()
    out = [pts[i] for i in keep]
    return out if len(out) == k else resample(out, k)


def resample(pts, k):
    """Ricampionamento per lunghezza d'arco (sezioni con pochi punti)."""
    seg = [math.hypot(b[0] - a[0], b[1] - a[1]) for a, b in zip(pts, pts[1:])]
    total = sum(seg) or 1e-9
    out = []
    for i in range(k):
        s, j = total * i / (k - 1), 0
        while j < len(seg) - 1 and s > seg[j]:
            s -= seg[j]
            j += 1
        t = min(1.0, s / (seg[j] or 1e-9))
        a, b = pts[j], pts[j + 1]
        out.append((a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t))
    return out




def _arc(pts):
    arc = [0.0]
    for a, b in zip(pts, pts[1:]):
        arc.append(arc[-1] + math.hypot(b[0] - a[0], b[1] - a[1]))
    return arc


def _point_at(pts, arc, s):
    j = max(0, min(len(pts) - 2, next((i for i in range(len(arc) - 1) if arc[i + 1] >= s), len(arc) - 2)))
    t = (s - arc[j]) / max(arc[j + 1] - arc[j], 1e-12)
    a, b = pts[j], pts[j + 1]
    return (a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t)


def consistent_halves(denses, k, dwl=None):
    """Mezze sezioni a `k` punti con la STESSA corrispondenza in ogni stazione: il punto i sta alla
    stessa frazione di perimetro (mediana, stazione per stazione, delle frazioni scelte da
    `simplify_polyline`). Con scelte indipendenti il punto i di una sezione si collegava a un punto
    a un'altra quota nella sezione accanto (bottazzo, DWL inserita in posti diversi): quadrilateri
    attorcigliati e triangoli che si intersecano. Il vertice sulla DWL si ottiene SPOSTANDO lungo la
    curva il punto interno piu' vicino all'incrocio (l'ordine resta, nessun punto in piu')."""
    fracs = []
    for pts in denses:
        arc = _arc(pts)
        simp = simplify_polyline(pts, k)
        # frazione di ogni punto scelto: il punto denso coincidente piu' vicino
        f = sorted(arc[min(range(len(pts)), key=lambda i: (pts[i][0] - p[0]) ** 2 + (pts[i][1] - p[1]) ** 2)] / max(arc[-1], 1e-9)
                   for p in simp)
        if len(f) == k:
            fracs.append(f)
    g = [float(np.median([f[i] for f in fracs])) for i in range(k)]
    g[0], g[-1] = 0.0, 1.0
    for i in range(1, k):                       # strettamente crescenti
        g[i] = max(g[i], g[i - 1] + 0.2 / k)
    if g[-1] > 1.0:
        g = [x / g[-1] for x in g]
    out = []
    for pts in denses:
        arc = _arc(pts)
        half = [_point_at(pts, arc, x * arc[-1]) for x in g]
        half[0] = (0.0, half[0][1])
        if dwl is not None:
            for j in range(k - 1):
                a, b = half[j], half[j + 1]
                if (a[1] - dwl) * (b[1] - dwl) < 0.0:
                    t = (dwl - a[1]) / (b[1] - a[1])
                    p = (a[0] + (b[0] - a[0]) * t, dwl)
                    i = j + 1 if (t >= 0.5 or j == 0) and j + 1 < k - 1 else j
                    if 0 < i < k - 1:
                        half[i] = p
                    break
        out.append(half)
    return out


def station_ys(y0, y1, n, power=1.15):
    """Stazioni dallo specchio (y0) alla prua (y1), piu' fitte a prua come `hydro_hull()`."""
    return [y0 + (1.0 - (1.0 - i / (n - 1)) ** power) * (y1 - y0) for i in range(n)]


# =============================================================================================
# 2. Idrostatica dalle sezioni dense
# =============================================================================================

def clip_area(half, zc):
    """Area (mezza sezione chiusa sulla mezzeria) sotto la quota zc, e quota del suo baricentro."""
    poly = [(0.0, half[-1][1])] + list(half)
    out = []
    for a, b in zip(poly, poly[1:] + poly[:1]):
        ina, inb = a[1] <= zc, b[1] <= zc
        if ina:
            out.append(a)
        if ina != inb:
            t = (zc - a[1]) / (b[1] - a[1])
            out.append((a[0] + (b[0] - a[0]) * t, zc))
    if len(out) < 3:
        return 0.0, zc
    area, cz = 0.0, 0.0
    for a, b in zip(out, out[1:] + out[:1]):
        cr = a[0] * b[1] - b[0] * a[1]
        area += cr
        cz += (a[1] + b[1]) * cr
    area *= 0.5
    if abs(area) < 1e-12:
        return 0.0, zc
    return abs(area), cz / (6.0 * area)


def half_beam_at(half, zc):
    best = 0.0
    for a, b in zip(half, half[1:]):
        if (a[1] - zc) * (b[1] - zc) <= 0.0 and a[1] != b[1]:
            t = (zc - a[1]) / (b[1] - a[1])
            best = max(best, a[0] + (b[0] - a[0]) * t)
    return best


def hydrostatics(sections, dwl):
    """Volume, LCB, KB, galleggiamento (area, momento d'inerzia trasversale) alla quota dwl."""
    v = my = mz = awp = it = 0.0
    lwl = []
    for (ya, ha), (yb, hb) in zip(sections, sections[1:]):
        dy = yb - ya
        aa, za = clip_area(ha, dwl)
        ab, zb = clip_area(hb, dwl)
        ba, bb = half_beam_at(ha, dwl), half_beam_at(hb, dwl)
        seg_v = (aa + ab) * dy          # due meta' (x2) per la media (/2)
        v += seg_v
        my += (aa * ya + ab * yb) * dy
        mz += (aa * za + ab * zb) * dy
        awp += (ba + bb) * dy
        it += (ba ** 3 + bb ** 3) / 3.0 * dy
        if ba > 0 or bb > 0:
            lwl += [ya, yb]
    if v <= 1e-9:
        return {"volume": 0.0}
    # v, my, mz: scafo intero (due meta' per la media dei trapezi); it = I_T del galleggiamento
    # intero (2/3 ∫ b³ dy sulla mezza larghezza b).
    return {"volume": v, "lcb": my / v, "kb": mz / v,
            "awp": awp, "it": it, "lwl": (max(lwl) - min(lwl)) if lwl else 0.0}


def solve_waterline(sections, mass, lo_z, hi_z):
    target = mass / RHO
    a, b = lo_z, hi_z
    for _ in range(60):
        m = 0.5 * (a + b)
        if hydrostatics(sections, m)["volume"] < target:
            a = m
        else:
            b = m
    return 0.5 * (a + b)


# =============================================================================================
# 3. Geometrie di ruolo
# =============================================================================================

def rings_from(halves, ys):
    return [[(-x, y, z) for x, z in reversed(h[1:])] + [(x, y, z) for x, z in h] for y, h in zip(ys, halves)]


def mesh_from_rings(name, rings, deck=True, close_bow=True):
    """Fasciame fra sezioni consecutive (stesso numero di punti), specchio e prua chiusi se hanno
    larghezza, coperta piatta da falchetta a falchetta. Saldata a 0,5 mm, normali verso l'esterno,
    triangolata (i poligoni non triangolati li triangola l'export a modo suo)."""
    bm = bmesh.new()
    vr = [[bm.verts.new(p) for p in r] for r in rings]

    def face(vs):
        try:
            bm.faces.new(vs)
        except ValueError:
            pass    # faccia gia' esistente (punti saldati in una sezione degenere)
    for a, c in zip(vr, vr[1:]):
        for i in range(len(a) - 1):
            face([a[i], a[i + 1], c[i + 1], c[i]])
        if deck:
            face([a[0], c[0], c[-1], a[-1]])
    if abs(rings[0][0][0]) > 0.01:
        face(vr[0][::-1])
    if close_bow and abs(rings[-1][0][0]) > 0.01:
        face(vr[-1])
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=0.0005)
    degenerate = [f for f in bm.faces if f.calc_area() < 1e-7]
    if degenerate:
        bmesh.ops.delete(bm, geom=degenerate, context="FACES_ONLY")
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bmesh.ops.triangulate(bm, faces=bm.faces, quad_method="BEAUTY", ngon_method="BEAUTY")
    untangle(bm)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    for p in me.polygons:
        p.use_smooth = True     # vertici condivisi anche nel glTF (il convesso ne vuole pochi)
    obj = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(obj)
    return obj


def crossing_pairs(bm):
    """Coppie di triangoli che si intersecano senza condividere vertici (come check_boat_model.py)."""
    bm.verts.index_update()
    bm.faces.index_update()
    bm.faces.ensure_lookup_table()
    tree = BVHTree.FromBMesh(bm, epsilon=0.0)
    return [(i, j) for i, j in tree.overlap(tree)
            if i < j and not set(bm.faces[i].verts) & set(bm.faces[j].verts)]


def untangle(bm, rounds=40):
    """Un quadrilatero storto fra due sezioni (uno scalino orizzontale in una e non nell'altra) puo'
    ripiegarsi su quello accanto con una diagonale e non con l'altra: si gira la diagonale dei
    triangoli che si intersecano finche' le intersezioni calano."""
    tried = set()
    for _ in range(rounds):
        pairs = crossing_pairs(bm)
        if not pairs:
            return 0
        keys = []
        for i in {i for p in pairs for i in p}:
            for e in bm.faces[i].edges:
                key = tuple(sorted(v.index for v in e.verts))
                if key not in tried and len(e.link_faces) == 2 and all(len(g.verts) == 3 for g in e.link_faces):
                    keys.append(key)
        improved = False
        for key in keys:
            bm.verts.ensure_lookup_table()
            e = bm.edges.get((bm.verts[key[0]], bm.verts[key[1]]))
            if e is None:
                continue
            new = bmesh.ops.rotate_edges(bm, edges=[e], use_ccw=False)["edges"]
            if new and len(crossing_pairs(bm)) < len(pairs):
                improved = True
                break
            if new:
                bmesh.ops.rotate_edges(bm, edges=new, use_ccw=True)
            tried.add(key)
        if not improved:
            break
    return len(crossing_pairs(bm))


def mesh_submerged(obj, zc):
    """Volume della carena sotto la quota zc e sua ascissa y (LCB), come check_boat_model.py: taglio
    dei triangoli e V = Σ A·n_z·(z − zc) sui pezzi immersi (il coperchio sul piano non conta)."""
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bm.transform(obj.matrix_world)
    bmesh.ops.triangulate(bm, faces=bm.faces)
    total = sum(f.calc_center_median().dot(f.normal) * f.calc_area() for f in bm.faces)
    sign = 1.0 if total >= 0.0 else -1.0
    v = my = 0.0
    for f in bm.faces:
        pts = [vv.co.copy() for vv in f.verts]
        d = [p.z - zc for p in pts]
        poly = []
        for i in range(3):
            p, q, dp, dq = pts[i], pts[(i + 1) % 3], d[i], d[(i + 1) % 3]
            if dp <= 0.0:
                poly.append(p)
            if (dp < 0.0) != (dq < 0.0):
                poly.append(p + (q - p) * (dp / (dp - dq)))
        nz = f.normal.z * sign
        for i in range(1, len(poly) - 1):
            a, b, c = poly[0], poly[i], poly[i + 1]
            area = (b - a).cross(c - a).length * 0.5
            za, zb, zcc = a.z - zc, b.z - zc, c.z - zc
            v += area * nz * (za + zb + zcc) / 3.0
            my += nz * area / 12.0 * (a.y * za + b.y * zb + c.y * zcc + (a.y + b.y + c.y) * (za + zb + zcc))
    bm.free()
    return v, (my / v if v > 1e-9 else 0.0)


def mesh_waterline(obj, volume, lo_z, hi_z):
    """Quota dell'acqua alla quale la carena sposta `volume` (bisezione)."""
    a, b = lo_z, hi_z
    for _ in range(40):
        m = 0.5 * (a + b)
        if mesh_submerged(obj, m)[0] < volume:
            a = m
        else:
            b = m
    return 0.5 * (a + b)


def mesh_report(obj, dwl=0.0, band=0.3):
    """Integrita' della carena come `check_boat_model.py`: chiusa, verso coerente, volume con segno,
    lato massimo del fasciame bagnabile."""
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bm.transform(obj.matrix_world)
    open_edges = sum(1 for e in bm.edges if len(e.link_faces) != 2)
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
        flipped += dirs[0] == dirs[1]
    volume = sum(f.calc_center_median().dot(f.normal) * f.calc_area() for f in bm.faces) / 3.0
    shell, where = 0.0, None
    for f in bm.faces:
        n = f.normal
        if n.z > 0.98 or abs(n.y) > 0.98 or min(v.co.z for v in f.verts) > dwl + band:
            continue
        e = max(e.calc_length() for e in f.edges)
        if e > shell:
            shell, where = e, [round(c, 2) for c in f.calc_center_median()] + [round(c, 2) for c in n]
    bm.faces.ensure_lookup_table()
    tree = BVHTree.FromBMesh(bm, epsilon=0.0)
    crossings = [[round(c, 2) for c in bm.faces[i].calc_center_median()] for i, j in tree.overlap(tree)
                 if i < j and not set(bm.faces[i].verts) & set(bm.faces[j].verts)]
    rep = {"tris": len(bm.faces), "open_edges": open_edges, "flipped": flipped,
           "self_intersections": len(crossings), "self_intersections_at": crossings[:6],
           "euler": len(bm.verts) - len(bm.edges) + len(bm.faces),
           "volume_m3": round(volume, 3), "max_wet_edge_m": round(shell, 3), "max_wet_edge_at": where,
           "degenerate": sum(1 for f in bm.faces if f.calc_area() < 1e-6)}
    bm.free()
    return rep


def decimated_copy(src, name, tris):
    obj = src.copy()
    obj.data = src.data.copy()
    obj.name = obj.data.name = name
    bpy.context.scene.collection.objects.link(obj)
    have = sum(len(p.vertices) - 2 for p in obj.data.polygons)
    if have > tris:
        mod = obj.modifiers.new("Decimate", "DECIMATE")
        mod.decimate_type = "COLLAPSE"
        mod.ratio = tris / have
        mod.use_collapse_triangulate = True
        with bpy.context.temp_override(object=obj, active_object=obj, **C.view3d_override()):
            bpy.ops.object.modifier_apply(modifier=mod.name)
    obj.data.materials.clear()
    return obj


def boxes_object(name, boxes):
    """Scatole (x0, x1, y0, y1, z0, z1) chiuse, come `collision_deck()`."""
    bm = bmesh.new()
    for x0, x1, y0, y1, z0, z1 in boxes:
        bmesh.ops.create_cube(bm, size=1.0, matrix=Matrix.LocRotScale(
            Vector(((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2)), None, Vector((x1 - x0, y1 - y0, z1 - z0))))
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    obj = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(obj)
    return obj


def join(objs, name):
    C.select_only(objs, active=objs[0])
    with bpy.context.temp_override(active_object=objs[0], selected_editable_objects=objs, **C.view3d_override()):
        bpy.ops.object.join()
    objs[0].name = objs[0].data.name = name
    return objs[0]


# =============================================================================================
# 4. Materiali per nome
# =============================================================================================

def _image_of(mat):
    if mat is None or mat.node_tree is None:
        return None
    for n in mat.node_tree.nodes:
        if n.type == "TEX_IMAGE" and n.image is not None:
            return n.image
    return None


def source_face_info(high, glass):
    """Per ogni oggetto pesante: BVH, nome del materiale per faccia e, per il materiale dei vetri,
    il colore della texture sorgente al centro UV della faccia (riconoscimento dei vetri)."""
    out = []
    for o in high:
        bm = bmesh.new()
        bm.from_mesh(o.data)
        bm.transform(o.matrix_world)
        bm.faces.ensure_lookup_table()
        names = [o.data.materials[f.material_index].name if o.data.materials and o.data.materials[f.material_index] else ""
                 for f in bm.faces]
        is_glass = [False] * len(bm.faces)
        if glass:
            want = Vector(glass["color"])
            tol = glass.get("tolerance", 0.08)
            uv = bm.loops.layers.uv.active
            cache = {}
            for f in bm.faces:
                if names[f.index] not in glass["materials"] or uv is None:
                    continue
                mat = o.data.materials[f.material_index]
                if mat.name not in cache:
                    img = _image_of(mat)
                    if img is None:
                        cache[mat.name] = None
                    else:
                        px = np.empty(img.size[0] * img.size[1] * 4, dtype=np.float32)
                        img.pixels.foreach_get(px)
                        cache[mat.name] = (px.reshape(img.size[1], img.size[0], 4), img.size[0], img.size[1])
                data = cache[mat.name]
                if data is None:
                    continue
                arr, w, h = data
                u = sum(l[uv].uv.x for l in f.loops) / len(f.loops)
                v = sum(l[uv].uv.y for l in f.loops) / len(f.loops)
                col = arr[int((v % 1.0) * (h - 1)), int((u % 1.0) * (w - 1)), :3]
                is_glass[f.index] = (Vector(col.tolist()) - want).length < tol
        out.append((BVHTree.FromBMesh(bm), names, is_glass))
        bm.free()
    return out


def assign_materials(low, high, image, cfg):
    """Ruolo (nome di materiale di livrea) per faccia del leggero dal materiale sorgente piu' vicino."""
    mapping = cfg.get("materials", {})
    default = cfg.get("default_material", "Hull")
    glass = cfg.get("glass")
    info = source_face_info(high, glass)
    roles = []
    for p in low.data.polygons:
        c = low.matrix_world @ p.center
        best = (1e9, "", False)
        for tree, names, is_glass in info:
            hit = tree.find_nearest(c)
            if hit[0] is not None and hit[3] < best[0]:
                best = (hit[3], names[hit[2]], is_glass[hit[2]])
        roles.append("Glass" if best[2] else mapping.get(best[1], default))
    order = sorted(set(roles), key=lambda r: (LIVERY_ROLES + ("Rubber", "Metal", "Glass", "Gauge")).index(r)
                   if r in LIVERY_ROLES + ("Rubber", "Metal", "Glass", "Gauge") else 99)
    grey = None
    if cfg.get("livery_grey"):
        # Grigio chiaro con la stessa luminanza relativa: la livrea moltiplica (pipeline §4.1).
        w, h = image.size
        px = np.empty(w * h * 4, dtype=np.float32)
        image.pixels.foreach_get(px)
        px = px.reshape(h, w, 4)
        lum = px[..., :3] @ np.array([0.2126, 0.7152, 0.0722])
        lum = np.clip(0.55 + 0.45 * lum / max(float(lum.mean()) * 2.0, 1e-3), 0, 1)
        grey = C.make_image(image.name + "_grey", np.repeat(lum[..., None], 3, axis=2))
    low.data.materials.clear()
    for r in order:
        img = grey if (grey is not None and r in LIVERY_ROLES) else image
        mat = C.psx_material(r, img)
        mat.name = r
        if r == "Glass":
            bsdf = mat.node_tree.nodes["Principled BSDF"]
            bsdf.inputs["Alpha"].default_value = 0.35
            mat.surface_render_method = "BLENDED"
        low.data.materials.append(mat)
    for p, r in zip(low.data.polygons, roles):
        p.material_index = order.index(r)
    return {r: roles.count(r) for r in order}


# =============================================================================================
# 5. Vuoti e controlli del posto di guida
# =============================================================================================

def mesh_tree(objs, skip_material=None):
    verts, tris = [], []
    for o in objs:
        mw = o.matrix_world
        base = len(verts)
        verts.extend(mw @ v.co for v in o.data.vertices)
        for p in o.data.polygons:
            if skip_material and o.data.materials and o.data.materials[p.material_index] \
                    and o.data.materials[p.material_index].name == skip_material:
                continue
            vs = list(p.vertices)
            for k in range(1, len(vs) - 1):
                tris.append((base + vs[0], base + vs[k], base + vs[k + 1]))
    return BVHTree.FromPolygons(verts, tris)


def make_empty(name, loc, rot_deg, parent):
    e = bpy.data.objects.new(name, None)
    e.empty_display_size = 0.1
    e.location = loc
    e.rotation_euler = [math.radians(a) for a in rot_deg]
    bpy.context.scene.collection.objects.link(e)
    e.parent = parent
    return e


def floor_below(tree, p, depth=3.0):
    hit = tree.ray_cast(Vector(p), Vector((0, 0, -1)), depth)
    return hit[0]


def check_helm(seat, walk_tree, view_tree, errors, warnings):
    """Posto di guida: pagliolo sotto i piedi, seduta a 0,46 m (0,04 m dietro i piedi), testa sotto
    il tetto, visuale in avanti dagli occhi (raggi come check_boat_model.py, vetri esclusi)."""
    feet = seat.matrix_world.translation.copy()
    rep = {}
    floor = floor_below(walk_tree, feet + Vector((0, 0, 0.05)), 0.3)
    rep["floor_gap_m"] = round(feet.z - floor.z, 3) if floor else None
    if floor is None:
        errors.append("Seat_Helm: nessun pagliolo entro 0,25 m sotto i piedi")
    seat_hit = floor_below(walk_tree, feet + Vector((0, -0.04, 0.80)), 0.78)
    seat_h = round(seat_hit.z - feet.z, 3) if seat_hit else None
    rep["seat_height_m"] = seat_h
    if seat_h is None or abs(seat_h - SEAT_HEIGHT) > 0.08:
        warnings.append("Seat_Helm: seduta a %s m sopra i piedi (attesa %.2f): il pilota siede nel vuoto "
                        "o dentro un piano" % (seat_h, SEAT_HEIGHT))
    eye = feet + Vector((0, 0, SEATED_EYE))
    roof = walk_tree.ray_cast(eye, Vector((0, 0, 1)), 3.0)
    rep["headroom_m"] = round(roof[3], 3) if roof[0] is not None else None
    if roof[0] is not None and roof[3] < 0.15:
        errors.append("Seat_Helm: tetto %.2f m sopra gli occhi: la testa lo attraversa" % roof[3])
    clear, total, blocked = 0, 0, {}
    for pitch in (0, -8, -16):
        for yaw in (-30, -20, -10, 0, 10, 20, 30):
            a, b = math.radians(yaw), math.radians(pitch)
            d = Vector((math.sin(a) * math.cos(b), math.cos(a) * math.cos(b), math.sin(b)))
            total += 1
            hit = view_tree.ray_cast(eye, d, 2.0)
            if hit[0] is None:
                clear += 1
            else:
                blocked["%+d/%+d" % (yaw, pitch)] = round(hit[3], 2)
    rep["view_clear"] = "%d/%d" % (clear, total)
    rep["view_blocked_m"] = blocked
    if clear < 0.3 * total:
        errors.append("visuale al timone: %d/%d raggi liberi (pipeline §4.1)" % (clear, total))
    elif clear < 0.6 * total:
        warnings.append("visuale al timone: %d/%d raggi liberi" % (clear, total))
    return rep


# =============================================================================================
# 6. BoatConfig
# =============================================================================================

def _godot(v):
    """Blender (x, y, z) -> Godot (x, z, -y)."""
    return (v[0], v[2], -v[1])


def write_config(project_dir, cfg, boat_id, glb_rel, displacement, lcb, keel_z, sheer_z, mounts):
    """`BoatConfig` da un modello (`template`): stessa propulsione, idrodinamica e accessori; scafo
    nuovo; massa dello scafo = dislocamento - masse del modello (motore, MassItems); baricentro dello
    scafo messo perche' il baricentro TOTALE stia sulla verticale del centro di carena (assetto ~0)."""
    tpl_path = cfg["template"].replace("res://", project_dir + "/")
    text = open(tpl_path, encoding="utf-8").read()
    text = re.sub(r'(\[gd_resource[^\]]*?) uid="[^"]*"', r"\1", text, count=1)
    hull_id = re.search(r'^Hull = ExtResource\("([^"]+)"\)', text, re.M).group(1)
    text = re.sub(r'^\[ext_resource type="PackedScene"[^\n]*id="%s"\]$' % re.escape(hull_id),
                  '[ext_resource type="PackedScene" path="res://%s" id="%s"]' % (glb_rel, hull_id), text, flags=re.M)
    if cfg.get("drop_mass_items"):
        text = re.sub(r"^MassItems = .*\n", "", text, flags=re.M)
    if "console" in cfg and cfg["console"] is None:
        text = re.sub(r"^Console = .*\n", "", text, flags=re.M)
    if "helm" in cfg:
        # HelmType: 0 barra (default, riga assente), 1 volante.
        text = re.sub(r"^Helm = .*\n", "", text, flags=re.M)
        if cfg["helm"] == "wheel":
            text = re.sub(r"^(HullCenterOfMass = .*\n)", r"\1Helm = 1\n", text, count=1, flags=re.M)
    if "livery" in cfg and cfg["livery"] is None:
        text = re.sub(r"^Livery = .*\n", "", text, flags=re.M)
    # Campi delle sotto-risorse del modello da cambiare: {"Rudder.Area": 0.3, "Engine.MaxPowerKw": 55}.
    for key, value in cfg.get("set", {}).items():
        sid, field = key.split(".", 1)
        head = re.search(r'^\[sub_resource [^\]]*id="%s"\]\n' % re.escape(sid), text, re.M)
        if head is None:
            raise ValueError("config.set: il modello non ha la sotto-risorsa '%s'" % sid)
        end = text.find("\n[", head.end())
        end = len(text) if end < 0 else end
        body = text[head.end():end]
        val = ("%r" % float(value)) if isinstance(value, (int, float)) else str(value)
        line = "%s = %s" % (field, val)
        body = re.sub(r"^%s = .*$" % re.escape(field), line, body, flags=re.M) \
            if re.search(r"^%s = " % re.escape(field), body, re.M) else body.rstrip("\n") + "\n" + line + "\n"
        text = text[:head.end()] + body + text[end:]

    # Masse del modello: sotto-risorse con Mass (con Position = MassItems usati; senza = motore).
    used = re.search(r"^MassItems = \[(.*)\]$", text, re.M)
    used_ids = set(re.findall(r'SubResource\("([^"]+)"\)', used.group(1))) if used else set()
    others = []          # (massa, posizione Blender)
    for sid, body in re.findall(r'\[sub_resource [^\]]*id="([^"]+)"\]\n(.*?)(?=\n\[|\Z)', text, re.S):
        m = re.search(r"^Mass = ([\d.]+)", body, re.M)
        if not m:
            continue
        pos = re.search(r"^Position = Vector3\(([^)]*)\)", body, re.M)
        if pos:
            if sid not in used_ids:
                continue
            gx, gy, gz = (float(s) for s in pos.group(1).split(","))
            others.append((float(m.group(1)), (gx, -gz, gy)))
        else:
            at = mounts.get("Mount_Engine") or mounts.get("Mount_Motor") or (0.0, lcb, 0.0)
            others.append((float(m.group(1)), tuple(at)))
    motor = re.search(r'^Motor = ExtResource\("([^"]+)"\)', text, re.M)
    if motor:
        mpath = re.search(r'^\[ext_resource[^\n]*path="([^"]+)"[^\n]*id="%s"\]' % re.escape(motor.group(1)), text, re.M)
        if mpath:
            mtext = open(mpath.group(1).replace("res://", project_dir + "/"), encoding="utf-8").read()
            at = mounts.get("Mount_Motor", (0.0, lcb, 0.0))
            others += [(float(x), tuple(at)) for x in re.findall(r"^Mass = ([\d.]+)", mtext, re.M)]
    m_other = sum(m for m, _ in others)
    hull_mass = cfg.get("hull_mass", displacement - m_other)
    if hull_mass < 0.3 * displacement:
        raise ValueError("massa dello scafo %.0f kg: il modello di configurazione pesa troppo per "
                         "questo dislocamento" % hull_mass)
    y_hull = (displacement * lcb - sum(m * p[1] for m, p in others)) / hull_mass
    z_hull = cfg.get("hull_com_z", keel_z + cfg.get("hull_com_height", 0.4) * (sheer_z - keel_z))
    com = _godot((0.0, y_hull, z_hull))
    text = re.sub(r"^HullMass = .*$", "HullMass = %.1f" % hull_mass, text, flags=re.M)
    text = re.sub(r"^HullCenterOfMass = .*$", "HullCenterOfMass = Vector3(0, %.3f, %.3f)" % (com[1], com[2]),
                  text, flags=re.M)
    # Sotto-risorse e risorse esterne rimaste senza riferimenti (MassItems tolti, consolle tolta).
    for _ in range(3):
        for sid in re.findall(r'^\[sub_resource [^\]]*id="([^"]+)"\]', text, re.M):
            if 'SubResource("%s")' % sid not in text:
                text = re.sub(r'\[sub_resource [^\]]*id="%s"\]\n.*?(?=\n\[|\Z)\n?' % re.escape(sid), "", text, flags=re.S)
        for eid in re.findall(r'^\[ext_resource [^\]]*id="([^"]+)"\]', text, re.M):
            if 'ExtResource("%s")' % eid not in text:
                text = re.sub(r'^\[ext_resource [^\]]*id="%s"\]\n' % re.escape(eid), "", text, flags=re.M)
    text = re.sub(r"\n{3,}", "\n\n", text)
    rel = cfg.get("path", "resources/boats/%s.tres" % boat_id).replace("res://", "")
    with open(os.path.join(project_dir, rel), "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    kg = (hull_mass * z_hull + sum(m * p[2] for m, p in others)) / (hull_mass + m_other)
    return rel, {"hull_mass": round(hull_mass, 1), "other_mass": round(m_other, 1),
                 "hull_com_godot": [0.0, round(com[1], 3), round(com[2], 3)], "kg_z": round(kg, 3)}


# =============================================================================================
# Sotto-pipeline
# =============================================================================================

def run(high, low, manifest, project_dir, export=True, preview_on=False):
    cfg = manifest.get("boat", {})
    name = cfg.get("name", manifest["id"].title().replace("_", ""))
    report, errors, warnings = {"boat": name}, [], []

    # --- Involucro, sezioni dense, linea d'acqua ---------------------------------------------
    tree, lo, hi = hull_envelope(high, cfg.get("hull_min_length", 0.5))
    length = hi.y - lo.y
    cx = 0.5 * (lo.x + hi.x)
    end = cfg.get("end_inset", 0.004) * length
    dense_ys = station_ys(lo.y + end, hi.y - end, 90, 1.0)
    sections = [(y, s) for y in dense_ys if (s := dense_section(tree, y, lo, hi)) is not None]
    if len(sections) < 10:
        raise ValueError("lo scafo non si legge: %d sezioni valide su 90" % len(sections))
    y_stern, y_bow = sections[0][0], sections[-1][0]
    keel_z = min(min(p[1] for p in s) for _, s in sections)
    sheer_z = min(s[-1][1] for _, s in sections)
    wl = cfg.get("waterline", {})
    if "displacement_kg" in wl:
        dwl = solve_waterline(sections, wl["displacement_kg"], keel_z, max(s[-1][1] for _, s in sections))
    else:
        dwl = keel_z + wl["draft"]
    reserve = hydrostatics(sections, max(s[-1][1] for _, s in sections) + 0.01)["volume"]
    shift = Vector((-cx, -0.5 * (y_stern + y_bow), -dwl))

    # --- Tutto nella terna finale: origine a mezza lunghezza sulla DWL -----------------------
    def move(delta):
        nonlocal sections, keel_z, sheer_z, tree, lo, hi
        for o in high + [low]:
            o.data.transform(Matrix.Translation(delta))
            o.data.update()
        sections = [(y + delta.y, [(x, z + delta.z) for x, z in s]) for y, s in sections]
        keel_z, sheer_z = keel_z + delta.z, sheer_z + delta.z
        tree, lo, hi = hull_envelope(high, cfg.get("hull_min_length", 0.5))
    move(shift)
    y_stern, y_bow = sections[0][0], sections[-1][0]

    root = bpy.data.objects.new(name, None)
    bpy.context.scene.collection.objects.link(root)

    # --- HydroHull / SwimCollision / HullCollision -------------------------------------------
    hcfg = cfg.get("hydro", {})
    # Stazioni di base (pipeline §4.2: 20-26 fino a 8 m, 30-36 oltre) piu' quelle che aggiunge il
    # raffittimento alle estremita'; tetto dei triangoli 600 / 1000 / 1500.
    n_st = hcfg.get("stations", 20 if length <= 8 else (28 if length <= 15 else 34))
    tri_budget = 600 if length <= 8 else (1000 if length <= 15 else 1500)
    k = hcfg.get("points", 9 if length <= 15 else 11)
    ys = station_ys(y_stern, y_bow, n_st)

    def section_at(y):
        s = dense_section(tree, y, lo, hi)
        return s if s is not None else min(sections, key=lambda t: abs(t[0] - y))[1]

    def build_roles():
        # Stazioni in piu' dove la chiglia cambia quota in fretta (specchio arrotondato, piede di
        # prua): con il passo normale lo spigolo diventava uno smusso dritto lungo mezzo metro.
        st = [(y, section_at(y)) for y in ys]
        max_dk = hcfg.get("max_keel_step", 0.15)
        for _ in range(hcfg.get("max_extra_stations", 10)):
            gaps = [i for i in range(len(st) - 1)
                    if abs(st[i][1][0][1] - st[i + 1][1][0][1]) > max_dk and st[i + 1][0] - st[i][0] > 0.04]
            if not gaps:
                break
            i = max(gaps, key=lambda j: abs(st[j][1][0][1] - st[j + 1][1][0][1]))
            y = 0.5 * (st[i][0] + st[i + 1][0])
            st.insert(i + 1, (y, section_at(y)))
        sy = [y for y, _ in st]
        rings = rings_from(consistent_halves([s for _, s in st], k, 0.0), sy)
        cys = station_ys(y_stern, y_bow, hcfg.get("collision_stations", 9))
        chalves = consistent_halves([min(sections, key=lambda t: abs(t[0] - y))[1] for y in cys], 5)
        return (mesh_from_rings("HydroHull", rings), mesh_from_rings("SwimCollision", rings, deck=False),
                mesh_from_rings("HullCollision", rings_from(chalves, cys)))
    hydro, swim, hullcol = build_roles()
    # La DWL delle sezioni dense non e' quella della carena semplificata (le corde stanno dentro le
    # curve: ~5% di volume in meno). Si risolve sulla HydroHull, che e' cio' che fa galleggiare la
    # barca in Godot, e si ricostruisce col vertice sulla nuova DWL.
    if "displacement_kg" in wl:
        top = max(s[-1][1] for _, s in sections)
        for _ in range(4):
            dz = mesh_waterline(hydro, wl["displacement_kg"] / RHO, keel_z, top)
            if abs(dz) < 0.002:
                break
            for o in (hydro, swim, hullcol):
                me = o.data
                bpy.data.objects.remove(o)
                bpy.data.meshes.remove(me)
            move(Vector((0.0, 0.0, -dz)))
            top -= dz
            hydro, swim, hullcol = build_roles()
    sub_v, lcb = mesh_submerged(hydro, 0.0)
    displacement = sub_v * RHO
    hs = hydrostatics(sections, 0.0)
    kb = hs["kb"]
    bm_t = hs["it"] / hs["volume"]          # raggio metacentrico trasversale BM = I_T / V
    report.update({
        "LOA_m": round(y_bow - y_stern, 3), "B_m": round(hi.x - lo.x, 3),
        "draft_m": round(-keel_z, 3), "min_freeboard_m": round(min(s[-1][1] for _, s in sections), 3),
        "displacement_kg": round(displacement), "LCB_y_m": round(lcb, 3), "LWL_m": round(hs["lwl"], 3),
        "waterplane_m2": round(hs["awp"], 3), "KB_z_m": round(kb, 3), "BM_m": round(bm_t, 3),
        "reserve_buoyancy": round(reserve / max(hs["volume"], 1e-9), 2),
        "stern_y": round(y_stern, 3), "bow_y": round(y_bow, 3), "sheer_min_z": round(sheer_z, 3),
    })
    hrep = mesh_report(hydro)
    report["hydrohull"] = hrep
    if hrep["self_intersections"]:
        errors.append("HydroHull: %d coppie di triangoli che si intersecano (a %s)"
                      % (hrep["self_intersections"], hrep["self_intersections_at"]))
    if hrep["open_edges"] or hrep["flipped"]:
        errors.append("HydroHull non chiusa o incoerente: %d lati aperti, %d incoerenti" % (hrep["open_edges"], hrep["flipped"]))
    if hrep["euler"] != 2:
        warnings.append("HydroHull: Eulero %d (atteso 2)" % hrep["euler"])
    if hrep["degenerate"]:
        warnings.append("HydroHull: %d triangoli degeneri" % hrep["degenerate"])
    if hrep["tris"] > 1.5 * tri_budget:     # tetto indicativo della pipeline: conta il costo, non il numero
        warnings.append("HydroHull: %d triangoli oltre il tetto di %d (costo per passo): meno "
                        "boat.hydro.stations o points" % (hrep["tris"], tri_budget))
    if hrep["max_wet_edge_m"] > length / 10:
        warnings.append("HydroHull: lato del fasciame bagnato %.2f m > L/10: piu' stazioni" % hrep["max_wet_edge_m"])
    vol_err = abs(abs(hrep["volume_m3"]) - reserve) / max(reserve, 1e-9)
    if vol_err > 0.05:
        warnings.append("HydroHull: volume %.2f m³ contro %.2f delle sezioni dense (%.0f%%)"
                        % (abs(hrep["volume_m3"]), reserve, 100 * vol_err))
    # Scarto fra la carena di resa e la HydroHull nella fascia bagnata (pipeline §4.1: 1-3 cm).
    low_tree = mesh_tree([low])
    gap = [low_tree.find_nearest(v.co)[3] for v in hydro.data.vertices if -0.5 < v.co.z < 0.3]
    report["render_vs_hydro_m"] = round(float(np.percentile(gap, 90)), 3) if gap else None

    # --- Materiali per nome (prima della DeckCollision: la copia non li porta) ---------------
    image = next((n.image for m in low.data.materials if m and m.node_tree
                  for n in m.node_tree.nodes if n.type == "TEX_IMAGE" and n.image), None)
    report["materials"] = assign_materials(low, high, image, cfg)
    low.name = low.data.name = cfg.get("visual_name", "Hull")

    # --- DeckCollision -----------------------------------------------------------------------
    dcfg = cfg.get("deck", {})
    deck = decimated_copy(low, "DeckCollision", dcfg.get("tris", 2500))
    if dcfg.get("boxes"):
        deck = join([deck, boxes_object("DeckBoxes", dcfg["boxes"])], "DeckCollision")

    # --- Vuoti -------------------------------------------------------------------------------
    walk_tree = mesh_tree([deck])
    view_tree = mesh_tree([low], skip_material="Glass")
    mounts = {}
    for mname, m in cfg.get("mounts", {}).items():
        loc = Vector(m["at"])
        if m.get("snap") == "floor":
            hit = floor_below(walk_tree, loc, m.get("snap_depth", 3.0))
            if hit is None:
                errors.append("%s: nessun piano sotto %s" % (mname, tuple(round(c, 2) for c in loc)))
            else:
                loc.z = hit.z
        loc += Vector(m.get("offset", (0, 0, 0)))
        make_empty(mname, loc, m.get("rot", (0, 0, 0)), root)
        mounts[mname] = tuple(loc)
        if mname in UNDERWATER_MOUNTS and loc.z >= 0.0:
            errors.append("%s sopra la linea d'acqua (z %.3f)" % (mname, loc.z))
    if cfg.get("console_mount") and "Seat_Helm" in mounts:
        p = Vector(mounts["Seat_Helm"]) + Vector((0, CONSOLE_AHEAD, 0))
        make_empty("Mount_Console", p, (0, 0, 0), root)
        mounts["Mount_Console"] = tuple(p)
    report["mounts"] = {n: [round(c, 3) for c in p] for n, p in mounts.items()}
    if "Seat_Helm" not in mounts:
        errors.append("manca Seat_Helm (boat.mounts)")
    else:
        bpy.context.view_layer.update()
        report["helm"] = check_helm(bpy.data.objects["Seat_Helm"], walk_tree, view_tree, errors, warnings)

    for o in (low, hydro, swim, hullcol, deck):
        o.parent = root
        o.hide_render = o is not low

    # --- Uscite ------------------------------------------------------------------------------
    glb_rel = "assets/models/boats/%s.glb" % name
    if export:
        objs = [root] + list(root.children)
        C.export_glb(os.path.join(project_dir, glb_rel), objs)
        imp = os.path.join(project_dir, glb_rel + ".import")
        if not os.path.exists(imp):
            with open(imp, "w", encoding="utf-8", newline="\n") as f:
                f.write(IMPORT_TEMPLATE.format(path=glb_rel))
        report["glb"] = glb_rel
        if cfg.get("config"):
            rel, mass = write_config(project_dir, cfg["config"], manifest["id"], glb_rel, displacement,
                                     lcb, keel_z, sheer_z, mounts)
            report["config"] = rel
            report["mass"] = mass
            gm = kb + bm_t - mass["kg_z"]
            report["GM_est_m"] = round(gm, 3)
            if gm <= 0.0:
                errors.append("GM stimato %.2f m: la barca si rovescia (baricentro troppo alto)" % gm)
    if preview_on:
        report["preview"] = preview(root, low, hydro, os.path.join(OUT_DIR, "%s_boat.png" % manifest["id"]))
    report["warnings"], report["errors"] = warnings, errors
    for w in warnings:
        print("[boat] avviso:", w)
    for e in errors:
        print("[boat] ERRORE:", e)
    return report


def preview(root, low, hydro, path):
    """Fianco (sopra) e tre quarti (sotto) con il piano dell'acqua alla DWL e la HydroHull in rosso
    trasparente: la carena deve coincidere con la resa e la DWL cadere dove e' dipinta."""
    os.makedirs(os.path.dirname(path), exist_ok=True)
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.display.shading.light = "STUDIO"
    scene.display.shading.color_type = "TEXTURE"
    scene.render.resolution_x, scene.render.resolution_y = 900, 450
    lo, hi = bounds_of([low])
    size = max(hi - lo)
    water = bpy.data.objects.new("Water", bpy.data.meshes.new("Water"))
    bm = bmesh.new()
    bmesh.ops.create_grid(bm, x_segments=1, y_segments=1, size=size)
    bm.to_mesh(water.data)
    bm.free()
    scene.collection.objects.link(water)
    wm = bpy.data.materials.new("water_prev")
    wm.diffuse_color = (0.1, 0.3, 0.5, 0.45)
    water.data.materials.append(wm)
    red = bpy.data.materials.new("hydro_prev")
    red.diffuse_color = (1.0, 0.1, 0.1, 0.35)
    hcopy = hydro.copy()
    hcopy.data = hydro.data.copy()
    hcopy.data.materials.clear()
    hcopy.data.materials.append(red)
    hcopy.hide_render = False
    scene.collection.objects.link(hcopy)
    scene.display.shading.show_xray = False
    cam = bpy.data.objects.new("PrevCam", bpy.data.cameras.new("PrevCam"))
    cam.data.type = "ORTHO"
    cam.data.ortho_scale = size * 1.1
    scene.collection.objects.link(cam)
    scene.camera = cam
    center = (lo + hi) / 2
    tiles = []
    for d, show_hydro in (((1, 0, 0.12), True), ((0.8, -0.6, 0.45), False)):
        hcopy.hide_render = not show_hydro
        d = Vector(d).normalized()
        cam.location = center + d * size * 3
        cam.rotation_euler = (-d).to_track_quat("-Z", "Y").to_euler()
        tmp = path + ".tmp%d.png" % len(tiles)
        scene.render.filepath = tmp
        bpy.ops.render.render(write_still=True)
        tiles.append(C.load_image_array(tmp)[..., :3])
        os.remove(tmp)
    sheet = np.concatenate(tiles[::-1], axis=0)     # make_image scrive dal basso: il fianco sopra
    C.make_image("boat_preview", sheet, path)
    for o in (water, hcopy, cam):
        bpy.data.objects.remove(o)
    return path
