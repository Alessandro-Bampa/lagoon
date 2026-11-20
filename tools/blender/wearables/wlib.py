"""Template parametrici dei capi indossabili e loro texture. Li usa `build_wearable.py`.

Ogni template costruisce una o piu' mesh nello spazio del corpo A RIPOSO (T-pose UAL, metri, -Y =
davanti) e ne calcola pesi e regioni dal corpo stesso:

  shell   guscio ricavato dalle facce del corpo (regioni + tagli), gonfiato lungo le normali.
          Pesi e regioni COPIATI vertice per vertice: si deforma esattamente come la pelle sotto.
  band    anello attorno a un osso o alla testa (fasce, polsini, colletti, elastici).
  panel   pannello conformato alla superficie (piastre, pannello del rig, schienale dello zaino).
  box     scatola smussata appoggiata alla superficie (tasche, pouch, sacca dello zaino).
  strap   cinghia che segue la superficie lungo una polilinea (spallacci, bretelle).
  cap     calotta che segue il cranio (elmetti, cuffie della testa).
  cup     cilindro schiacciato (padiglioni delle cuffie, lenti).
  sole    suola sotto il piede.

Le posizioni si scrivono come espressioni sui giunti del corpo: "pelvis+0.05" (quota z del giunto),
"eye", "eye-0.03", oppure numeri. Le parti non-shell prendono i pesi dai 4 vertici del corpo piu'
vicini, eventualmente irrigiditi verso un osso (`rigid_bone`, `rigidity`).
"""

import math
import re

import bmesh
import bpy
import numpy as np
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree
from mathutils.kdtree import KDTree

import charlib as C

# Gonfiaggio di default per strato (metri): ogni strato sta fuori da quello sotto.
LAYER_OFFSET = {1: 0.006, 2: 0.014, 3: 0.024, 4: 0.034}


# =============================================================================================
# Contesto: il corpo a riposo
# =============================================================================================

class Body:
    """Corpo base reimportato da Character_Base.glb, con tutto cio' che i template interrogano."""

    def __init__(self, armature, mesh):
        self.armature = armature
        self.mesh = mesh
        me = mesh.data
        mw = mesh.matrix_world
        nm = mw.to_3x3().inverted().transposed()
        self.co = np.array([tuple(mw @ v.co) for v in me.vertices])
        self.no = np.array([tuple((nm @ v.normal).normalized()) for v in me.vertices])
        self.weights = C.vertex_weights(mesh)
        self.region = C.read_region_colors(mesh)
        self.joints = {b.name: armature.matrix_world @ b.head_local for b in armature.data.bones}
        self.tails = {b.name: armature.matrix_world @ b.tail_local for b in armature.data.bones}
        face = self.co[self.region == C.REGION["Face"]]
        self.eye = float(np.median(face[:, 2])) if len(face) else self.joints["Head"].z + 0.04
        # profondita' della fascia degli occhi: il punto piu' avanzato (davanti = -Y)
        self.eye_front = float(face[:, 1].min()) if len(face) else self.joints["Head"].y - 0.1
        self.kd = KDTree(len(self.co))
        for i, p in enumerate(self.co):
            self.kd.insert(p, i)
        self.kd.balance()
        self.faces = [tuple(p.vertices) for p in me.polygons]
        self._shells = {}

    # ---- espressioni di quota/posizione -------------------------------------------------------

    def z(self, expr):
        return self.scalar(expr, axis=2)

    def scalar(self, expr, axis=2):
        if isinstance(expr, (int, float)):
            return float(expr)
        m = re.fullmatch(r"\s*([A-Za-z_0-9]+)(?:\.([xyz]))?\s*([+-]\s*[0-9.]+)?\s*", str(expr))
        if not m:
            raise ValueError("espressione non valida: " + str(expr))
        name, ax, off = m.group(1), m.group(2), m.group(3)
        a = "xyz".index(ax) if ax else axis
        if name == "eye":
            base = self.eye if a == 2 else self.eye_front if a == 1 else 0.0
        elif name == "ground":
            base = 0.0
        else:
            base = self.joints[name][a]
        return base + (float(off.replace(" ", "")) if off else 0.0)

    # ---- superfici di riferimento ----------------------------------------------------------

    def shell_bvh(self, offset):
        """BVH del corpo gonfiato di `offset`: superficie su cui appoggiare pannelli e tasche."""
        key = round(offset, 4)
        if key not in self._shells:
            verts = [Vector(p + n * offset) for p, n in zip(self.co, self.no)]
            self._shells[key] = BVHTree.FromPolygons(verts, self.faces)
        return self._shells[key]

    def project(self, p, direction, offset):
        """Punto della superficie a `offset` dal corpo, cercato lungo `direction` partendo da
        lontano (fuori dal corpo). Restituisce (punto, normale) o (None, None)."""
        bvh = self.shell_bvh(offset)
        d = Vector(direction).normalized()
        start = Vector(p) - d * 1.0
        hit, nrm, _, _ = bvh.ray_cast(start, d, 2.0)
        return hit, nrm

    # ---- pesi per punti liberi ----------------------------------------------------------------

    def weights_at(self, p, k=4, allowed_regions=None):
        """Pesi (dict osso -> peso) e regione per un punto qualunque, dai k vertici piu' vicini."""
        found = self.kd.find_n(p, k * 4 if allowed_regions else k)
        if allowed_regions:
            found = [f for f in found if self.region[f[1]] in allowed_regions][:k] or found[:k]
        acc = {}
        total = 0.0
        for _, idx, dist in found:
            w = 1.0 / max(dist, 1e-4) ** 2
            total += w
            for bone, bw in self.weights[idx]:
                acc[bone] = acc.get(bone, 0.0) + bw * w
        acc = {b: v / total for b, v in acc.items()}
        region = int(self.region[found[0][1]])
        return acc, region


# =============================================================================================
# Parte costruita: geometria + pesi + regioni + id della parte
# =============================================================================================

class Part:
    def __init__(self, name, spec):
        self.name = name
        self.spec = spec
        self.verts = []      # Vector
        self.faces = []      # tuple di indici
        self.weights = []    # dict osso -> peso, per vertice
        self.regions = []    # int, per vertice

    def add_vert(self, p, weights=None, region=0):
        self.verts.append(Vector(p))
        self.weights.append(weights or {})
        self.regions.append(region)
        return len(self.verts) - 1


def finish_weights(part, body, rigid_bone=None, rigidity=0.0):
    """Pesi dai vicini per i vertici che non li hanno; irrigidimento verso `rigid_bone`."""
    for i, p in enumerate(part.verts):
        if not part.weights[i]:
            part.weights[i], part.regions[i] = body.weights_at(p)
        if rigid_bone and rigidity > 0:
            w = {b: v * (1 - rigidity) for b, v in part.weights[i].items()}
            w[rigid_bone] = w.get(rigid_bone, 0.0) + rigidity
            part.weights[i] = w


def rigid_weights(part, bone, region):
    for i in range(len(part.verts)):
        part.weights[i] = {bone: 1.0}
        part.regions[i] = region


# =============================================================================================
# Template
# =============================================================================================

def _region_set(names):
    return {C.REGION[n] for n in names}


def _hole_box(body, h):
    """Foro frontale simmetrico: {"x": mezza larghezza, "z": [basso, alto], "y_max": profondita'}.
    Toglie i vertici con |x| < x, z fra i due limiti e y < y_max (davanti = -Y)."""
    return {"x": body.scalar(h["x"], 0), "z0": body.z(h["z"][0]), "z1": body.z(h["z"][1]),
            "y_max": body.scalar(h.get("y_max", 0.0), 1)}


def _in_hole(p, h):
    return abs(p[0]) < h["x"] and h["z0"] < p[2] < h["z1"] and p[1] < h["y_max"]


def _clip_ok(p, clips, body):
    for c in clips or []:
        axis = c.get("axis", "z")
        if axis == "x_abs":
            v = abs(p[0])
        else:
            v = p["xyz".index(axis)]
        if "min" in c and v < body.scalar(c["min"], "xyz".index(axis[-1]) if axis != "x_abs" else 0):
            return False
        if "max" in c and v > body.scalar(c["max"], "xyz".index(axis[-1]) if axis != "x_abs" else 0):
            return False
    return True


def t_shell(body, spec, layer):
    """Guscio dalle facce del corpo. spec: regions, clip[], offset, loosen[], smooth."""
    part = Part(spec.get("name", "shell"), spec)
    regions = _region_set(spec["regions"])
    offset = spec.get("offset", LAYER_OFFSET[layer])
    holes = [_hole_box(body, h) for h in spec.get("holes", [])]
    keep = np.array([body.region[i] in regions and _clip_ok(body.co[i], spec.get("clip"), body)
                     and not any(_in_hole(body.co[i], h) for h in holes)
                     for i in range(len(body.co))])
    faces = [f for f in body.faces if all(keep[i] for i in f)]
    used = sorted({i for f in faces for i in f})
    remap = {}
    for i in used:
        p = body.co[i] + body.no[i] * offset
        remap[i] = part.add_vert(p, dict(body.weights[i]), int(body.region[i]))
    part.faces = [tuple(remap[i] for i in f) for f in faces]
    # orli dritti: il taglio per facce lascia un bordo a gradini; i vertici di bordo vicini a un
    # piano di taglio orizzontale si portano sul piano
    boundary = _boundary_verts(part)
    snap = spec.get("hem_snap", 0.045)
    for c in spec.get("clip") or []:
        axis = c.get("axis", "z")
        for key in ("min", "max"):
            if key not in c:
                continue
            if axis == "z":
                plane = body.z(c[key])
                for i in boundary:
                    if abs(part.verts[i].z - plane) < snap:
                        part.verts[i].z = plane
            elif axis == "x_abs":          # polsini: taglio lungo il braccio
                plane = body.scalar(c[key], 0)
                for i in boundary:
                    if abs(abs(part.verts[i].x) - plane) < snap:
                        part.verts[i].x = math.copysign(plane, part.verts[i].x)
    # fori: i vertici di bordo vicini a un foro si portano sul suo contorno (rettangolo in x/z)
    for h in holes:
        for i in boundary:
            p = part.verts[i]
            if p.y > h["y_max"] or abs(p.x) > h["x"] + snap or not (h["z0"] - snap < p.z < h["z1"] + snap):
                continue
            dz0, dz1, dx = abs(p.z - h["z0"]), abs(p.z - h["z1"]), abs(abs(p.x) - h["x"])
            m = min(dz0, dz1, dx)
            if m == dx:
                p.x = math.copysign(h["x"], p.x)
            elif m == dz0:
                p.z = h["z0"]
            else:
                p.z = h["z1"]
    # allargamenti locali: il capo si stacca dal corpo (gambe dei pantaloni, gambale degli stivali)
    for lo in spec.get("loosen", []):
        bone = lo["bone"]
        a, b = body.joints[bone], body.tails[bone]
        axis = (b - a).normalized()
        z0, z1 = body.z(lo.get("z_from", -10)), body.z(lo.get("z_to", 10))
        amount = lo["amount"]
        side = 1 if bone.endswith("_l") else -1 if bone.endswith("_r") else 0
        for i, p in enumerate(part.verts):
            if not (min(z0, z1) <= p.z <= max(z0, z1)):
                continue
            if side and p.x * side < 0:
                continue
            t = (p.z - z0) / (z1 - z0) if z1 != z0 else 1.0
            t = max(0.0, min(1.0, t))
            q = a + axis * (p - a).dot(axis)
            radial = p - q
            if radial.length > 1e-5:
                part.verts[i] = p + radial.normalized() * amount * t
    if spec.get("smooth", 0) > 0:
        _smooth(part, spec["smooth"])
    return [part]


def _smooth(part, iterations, factor=0.5):
    nbrs = [set() for _ in part.verts]
    for f in part.faces:
        for a in f:
            for b in f:
                if a != b:
                    nbrs[a].add(b)
    boundary = _boundary_verts(part)
    for _ in range(int(iterations)):
        new = list(part.verts)
        for i, ns in enumerate(nbrs):
            if not ns or i in boundary:
                continue
            avg = sum((part.verts[j] for j in ns), Vector()) / len(ns)
            new[i] = part.verts[i].lerp(avg, factor)
        part.verts = new


def _boundary_verts(part):
    count = {}
    for f in part.faces:
        for k in range(len(f)):
            e = tuple(sorted((f[k], f[(k + 1) % len(f)])))
            count[e] = count.get(e, 0) + 1
    out = set()
    for e, c in count.items():
        if c == 1:
            out.update(e)
    return out


def _ring_profile(body, center, axis, ref, radius_from, side, offset, segments, allowed=None):
    """Raggi del profilo del corpo attorno a `axis` passando per `center`: per ogni angolo, la
    distanza del vertice del corpo piu' esterno vicino al piano. Restituisce i punti dell'anello."""
    axis = axis.normalized()
    u = ref - axis * ref.dot(axis)
    u.normalize()
    v = axis.cross(u)
    near = []
    for i, p in enumerate(body.co):
        pv = Vector(p)
        d = (pv - center).dot(axis)
        if abs(d) > 0.02:
            continue
        if allowed is not None and body.region[i] not in allowed:
            continue
        if side and pv.x * side < -0.01:
            continue
        r = pv - center - axis * d
        if r.length < radius_from:
            near.append(r)
    pts = []
    for s in range(segments):
        ang = 2 * math.pi * s / segments
        dirv = u * math.cos(ang) + v * math.sin(ang)
        best = 0.0
        for r in near:
            if r.length < 1e-5:
                continue
            c = r.normalized().dot(dirv)
            if c > 0.9:
                best = max(best, r.dot(dirv))
        pts.append(dirv * (best + offset))
    # riempie i buchi del profilo con la media dei vicini
    lens = [p.length for p in pts]
    mean = sum(lens) / len(lens) if lens else 0.05
    for s in range(segments):
        if lens[s] < offset + 0.005:
            pts[s] = pts[s].normalized() * mean if pts[s].length > 0 else u * mean
    return pts


def t_band(body, spec, layer):
    """Anello. spec: bone + at (0..1 lungo l'osso) oppure z (anello orizzontale, es. testa o vita);
    width, offset, segments, regions (per il profilo), thickness."""
    part = Part(spec.get("name", "band"), spec)
    offset = spec.get("offset", LAYER_OFFSET[layer])
    width = spec.get("width", 0.04)
    seg = spec.get("segments", 12)
    thick = spec.get("thickness", 0.004)
    allowed = _region_set(spec["regions"]) if "regions" in spec else None
    side = 0
    if "bone" in spec:
        bone = spec["bone"]
        a, b = body.joints[bone], body.tails[bone]
        child = spec.get("to")
        if child:
            b = body.joints[child]
        axis = (b - a).normalized()
        center = a.lerp(b, spec.get("at", 0.5))
        ref = Vector((0, 0, 1)) if abs(axis.z) < 0.9 else Vector((0, -1, 0))
        side = 1 if bone.endswith("_l") else -1 if bone.endswith("_r") else 0
    else:
        z = body.z(spec["z"])
        center = Vector((0.0, body.scalar(spec.get("y", 0.0), 1), z))
        if "center_bone" in spec:
            j = body.joints[spec["center_bone"]]
            center = Vector((j.x, j.y + spec.get("y_shift", 0.0), z))
        axis = Vector((0, 0, 1))
        tilt = math.radians(spec.get("tilt", 0.0))       # inclinazione attorno a X (testa)
        axis = Matrix.Rotation(tilt, 3, "X") @ axis
        ref = Vector((0, -1, 0))
    prof = _ring_profile(body, center, axis, ref, spec.get("search_radius", 0.3), side, offset, seg, allowed)
    rings = []
    for h in (-width / 2, width / 2):
        ring_o = [part.add_vert(center + axis * h + p) for p in prof]
        ring_i = [part.add_vert(center + axis * h + p - p.normalized() * thick) for p in prof]
        rings.append((ring_o, ring_i))
    (o0, i0), (o1, i1) = rings
    for s in range(seg):
        t = (s + 1) % seg
        part.faces.append((o0[s], o0[t], o1[t], o1[s]))      # esterno
        part.faces.append((i0[t], i0[s], i1[s], i1[t]))      # interno
        part.faces.append((i0[s], i0[t], o0[t], o0[s]))      # bordo basso
        part.faces.append((o1[s], o1[t], i1[t], i1[s]))      # bordo alto
    return [part]


def _surface_frame(body, x, z, face, offset):
    """Punto e normale sulla superficie gonfiata, davanti (face='front'), dietro ('back') o di
    lato ('left'/'right': +X/-X)."""
    dirs = {"front": (Vector((x, -1, z)), Vector((0, 1, 0))),
            "back": (Vector((x, 1, z)), Vector((0, -1, 0))),
            "left": (Vector((1, x, z)), Vector((-1, 0, 0))),
            "right": (Vector((-1, x, z)), Vector((1, 0, 0)))}
    start, d = dirs[face]
    if face in ("front", "back"):
        p0 = Vector((x, 0, z))
    else:
        p0 = Vector((0, x, z))
    return body.project(p0 - d * 0.0, d, offset)


def t_panel(body, spec, layer):
    """Pannello conformato. spec: face, center [x,z], size [w,h], depth, offset, res [nx,nz],
    corner (smusso, 0..0.5 della misura minore), curve (0 = segue il corpo, 1 = piano)."""
    part = Part(spec.get("name", "panel"), spec)
    face = spec.get("face", "front")
    offset = spec.get("offset", LAYER_OFFSET[layer])
    cx, cz = body.scalar(spec["center"][0], 0), body.z(spec["center"][1])
    w, h = spec["size"]
    depth = spec.get("depth", 0.012)
    nx, nz = spec.get("res", [6, 6])
    flat = spec.get("curve", 0.3)
    corner = spec.get("corner", 0.15) * min(w, h)
    grid_o, grid_i = {}, {}
    hits = {}
    for ix in range(nx + 1):
        for iz in range(nz + 1):
            u, v = ix / nx - 0.5, iz / nz - 0.5
            x, z = cx + u * w, cz + v * h
            # smusso degli angoli: si rientra lungo la diagonale
            ex, ez = abs(u * w) - (w / 2 - corner), abs(v * h) - (h / 2 - corner)
            if ex > 0 and ez > 0:
                k = corner / max(math.hypot(ex, ez), 1e-6)
                if k < 1:
                    x = cx + math.copysign(w / 2 - corner + ex * k, u)
                    z = cz + math.copysign(h / 2 - corner + ez * k, v)
            hit, nrm = _surface_frame(body, x, z, face, offset)
            hits[(ix, iz)] = (hit, nrm, x, z)
    # piano medio per l'appiattimento
    valid = [hv for hv in hits.values() if hv[0] is not None]
    if not valid:
        raise ValueError(f"pannello {part.name}: nessun punto sulla superficie")
    axis = 1 if face in ("front", "back") else 0
    sign = -1 if face in ("front", "right") else 1
    if face == "right":
        sign = -1
    extreme = (min if sign < 0 else max)(hv[0][axis] for hv in valid)
    for key, (hit, nrm, x, z) in hits.items():
        if hit is None:
            hit = Vector((x, extreme, z)) if axis == 1 else Vector((extreme, x, z))
            nrm = Vector((0, sign, 0)) if axis == 1 else Vector((sign, 0, 0))
        p = hit.copy()
        p[axis] = p[axis] * (1 - flat) + extreme * flat
        out = Vector((0, 0, 0))
        out[axis] = sign
        grid_i[key] = part.add_vert(p)
        grid_o[key] = part.add_vert(p + out * depth)
    # (u, z) -> normale: davanti -Y e a sinistra +X vengono gia' giusti, dietro e a destra no
    flip = face in ("back", "right")
    for ix in range(nx):
        for iz in range(nz):
            a, b, c, d = (ix, iz), (ix + 1, iz), (ix + 1, iz + 1), (ix, iz + 1)
            q_out = (grid_o[a], grid_o[b], grid_o[c], grid_o[d])
            q_in = (grid_i[d], grid_i[c], grid_i[b], grid_i[a])
            if flip:
                q_out, q_in = q_out[::-1], q_in[::-1]
            part.faces += [q_out, q_in]
    # bordi
    loop = [(ix, 0) for ix in range(nx)] + [(nx, iz) for iz in range(nz)] + \
           [(ix, nz) for ix in range(nx, 0, -1)] + [(0, iz) for iz in range(nz, 0, -1)]
    for k in range(len(loop)):
        a, b = loop[k], loop[(k + 1) % len(loop)]
        f = (grid_i[a], grid_i[b], grid_o[b], grid_o[a])
        part.faces.append(f[::-1] if flip else f)
    return [part]


def _box_mesh(part, center, size, frame, bevel=0.15):
    """Scatola smussata: frame = (x, y, z) assi; size = (sx, sy, sz) lungo gli assi."""
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    if bevel > 0:
        bmesh.ops.bevel(bm, geom=list(bm.edges), offset=bevel * 0.5, segments=1, affect="EDGES")
    sx, sy, sz = size
    xa, ya, za = frame
    m = Matrix((xa * sx, ya * sy, za * sz)).transposed()
    base = len(part.verts)
    for v in bm.verts:
        part.add_vert(center + m @ v.co)
    for f in bm.faces:
        part.faces.append(tuple(base + v.index for v in f.verts))
    bm.free()


def t_box(body, spec, layer):
    """Scatola appoggiata alla superficie. spec: face, at [x,z], size [larghezza, altezza,
    profondita'], offset, bevel, tilt (gradi attorno alla normale)."""
    part = Part(spec.get("name", "box"), spec)
    face = spec.get("face", "front")
    offset = spec.get("offset", LAYER_OFFSET[layer])
    x, z = body.scalar(spec["at"][0], 0), body.z(spec["at"][1])
    hit, nrm = _surface_frame(body, x, z, face, offset)
    if hit is None:
        raise ValueError(f"box {part.name}: nessuna superficie in {x:.3f},{z:.3f} ({face})")
    out = {"front": Vector((0, -1, 0)), "back": Vector((0, 1, 0)),
           "left": Vector((1, 0, 0)), "right": Vector((-1, 0, 0))}[face]
    n = out.lerp(nrm.normalized(), spec.get("follow", 0.4)).normalized()
    up = Vector((0, 0, 1))
    side = up.cross(n).normalized()
    up = n.cross(side).normalized()
    tilt = math.radians(spec.get("tilt", 0))
    rot = Matrix.Rotation(tilt, 3, n)
    side, up = rot @ side, rot @ up
    w, h, d = spec["size"]
    _box_mesh(part, hit + n * (d / 2), (w, h, d), (side, up, n), spec.get("bevel", 0.2))
    return [part]


def t_strap(body, spec, layer):
    """Cinghia lungo una polilinea di punti [x, z, faccia] o [x, y, z]; la si appoggia alla
    superficie gonfiata. spec: points, width, thickness, offset, samples."""
    part = Part(spec.get("name", "strap"), spec)
    offset = spec.get("offset", LAYER_OFFSET[layer])
    width = spec.get("width", 0.045)
    thick = spec.get("thickness", 0.006)
    pts = []
    for p in spec["points"]:
        if len(p) == 3 and p[2] in ("front", "back", "left", "right"):
            hit, _ = _surface_frame(body, body.scalar(p[0], 0), body.z(p[1]), p[2], offset)
            if hit is None:
                raise ValueError(f"strap {part.name}: punto fuori dal corpo {p}")
            pts.append(hit)
        else:
            pts.append(Vector((body.scalar(p[0], 0), body.scalar(p[1], 1), body.z(p[2]))))
    # ricampiona e riappoggia ogni campione sulla superficie piu' vicina
    bvh = body.shell_bvh(offset)
    samples = []
    n_per = spec.get("samples", 6)
    for k in range(len(pts) - 1):
        for s in range(n_per):
            samples.append(pts[k].lerp(pts[k + 1], s / n_per))
    samples.append(pts[-1])
    surf = []
    for q in samples:
        loc, nrm, _, _ = bvh.find_nearest(q)
        surf.append((loc, nrm.normalized()))
    left, right = [], []
    for k, (p, n) in enumerate(surf):
        t = (surf[min(k + 1, len(surf) - 1)][0] - surf[max(k - 1, 0)][0]).normalized()
        side = t.cross(n).normalized() * (width / 2)
        left.append((part.add_vert(p - side), part.add_vert(p - side + n * thick)))
        right.append((part.add_vert(p + side), part.add_vert(p + side + n * thick)))
    for k in range(len(surf) - 1):
        li, lo = left[k]
        ri, ro = right[k]
        li2, lo2 = left[k + 1]
        ri2, ro2 = right[k + 1]
        part.faces += [(lo, ro, ro2, lo2), (li2, ri2, ri, li), (li, lo, lo2, li2), (ri2, ro2, ro, ri)]
    return [part]


def t_cap(body, spec, layer):
    """Calotta sul cranio (elmetto). spec: offset (dal cuoio capelluto), rim_front, rim_back
    (quote del bordo, espressioni), rings, segments, lip (sporgenza del bordo)."""
    part = Part(spec.get("name", "cap"), spec)
    offset = spec.get("offset", 0.025)
    head = [Vector(p) for p, r in zip(body.co, body.region)
            if r in (C.REGION["Scalp"], C.REGION["Face"]) and p[2] > body.eye - 0.02]
    cx = 0.0
    cy = sum(p.y for p in head) / len(head)
    top = max(p.z for p in head)
    rim_f, rim_b = body.z(spec.get("rim_front", "eye+0.04")), body.z(spec.get("rim_back", "eye-0.03"))
    seg = spec.get("segments", 16)
    rings = spec.get("rings", 6)
    center = Vector((cx, cy + spec.get("y_shift", 0.0), min(rim_f, rim_b)))
    bvh = BVHTree.FromPolygons([Vector(p) for p in body.co], body.faces)
    grid = []
    for r in range(rings + 1):
        elev = (math.pi / 2) * r / rings          # 0 = bordo, pi/2 = sommita'
        row = []
        for s in range(seg):
            az = 2 * math.pi * s / seg
            dirv = Vector((math.cos(elev) * math.sin(az), -math.cos(elev) * math.cos(az), math.sin(elev)))
            # quota del bordo che va dal davanti (az=0) al dietro
            rim = rim_f + (rim_b - rim_f) * (1 - math.cos(az)) / 2
            origin = Vector((center.x, center.y, rim)) if r == 0 else Vector((center.x, center.y, rim + (top - rim) * 0.35))
            start = origin + dirv * 0.4
            hit, nrm, _, _ = bvh.ray_cast(start, -dirv, 0.6)
            p = (hit + dirv * offset) if hit is not None else origin + dirv * 0.12
            if r == 0:
                p.z = rim
            row.append(part.add_vert(p))
        grid.append(row)
    apex = part.add_vert(Vector((center.x, center.y, top + offset)))
    for r in range(rings):
        for s in range(seg):
            t = (s + 1) % seg
            part.faces.append((grid[r][s], grid[r][t], grid[r + 1][t], grid[r + 1][s]))
    # la riga piu' alta chiude sull'apice
    for s in range(seg):
        t = (s + 1) % seg
        part.faces.append((grid[rings][s], grid[rings][t], apex))
    # bordo con spessore (lip): anello verso l'esterno/basso
    lip = spec.get("lip", 0.01)
    inner = []
    for s in range(seg):
        p = part.verts[grid[0][s]]
        d = Vector((p.x - center.x, p.y - center.y, 0)).normalized()
        inner.append(part.add_vert(p + d * lip - Vector((0, 0, 0.006))))
    for s in range(seg):
        t = (s + 1) % seg
        part.faces.append((inner[s], inner[t], grid[0][t], grid[0][s]))
    rigid_weights(part, "Head", C.REGION["Scalp"])
    return [part]


def t_cup(body, spec, layer):
    """Cilindro (padiglione delle cuffie, lente). spec: center [x, y, z], axis [x, y, z],
    radius, depth, segments, bone (rigido)."""
    part = Part(spec.get("name", "cup"), spec)
    c = Vector((body.scalar(spec["center"][0], 0), body.scalar(spec["center"][1], 1), body.z(spec["center"][2])))
    axis = Vector(spec.get("axis", [1, 0, 0])).normalized()
    r = spec.get("radius", 0.04)
    d = spec.get("depth", 0.03)
    seg = spec.get("segments", 10)
    sx, sy = spec.get("scale", [1.0, 1.0])
    ref = Vector((0, 0, 1)) if abs(axis.z) < 0.9 else Vector((0, 1, 0))
    u = (ref - axis * ref.dot(axis)).normalized()
    v = axis.cross(u)
    caps = []
    for h in (0.0, d):
        ring = [part.add_vert(c + axis * h + (u * math.cos(2 * math.pi * s / seg) * sy +
                                              v * math.sin(2 * math.pi * s / seg) * sx) * r)
                for s in range(seg)]
        caps.append(ring)
    for s in range(seg):
        t = (s + 1) % seg
        part.faces.append((caps[0][s], caps[0][t], caps[1][t], caps[1][s]))
    part.faces.append(tuple(caps[1]))
    part.faces.append(tuple(reversed(caps[0])))
    bone = spec.get("bone", "Head")
    rigid_weights(part, bone, C.REGION[spec.get("region", "Scalp")])
    return [part]


def t_sole(body, spec, layer):
    """Suola sotto ogni piede: impronta del piede (vertici sotto `height`) allargata, spessa
    `thickness`. Pesi sul piede/punta come i vertici del corpo vicini."""
    parts = []
    for side in (1, -1):
        part = Part(f"{spec.get('name', 'sole')}_{'l' if side > 0 else 'r'}", spec)
        pts = [Vector(p) for p, r in zip(body.co, body.region)
               if r == C.REGION["Foot"] and p[0] * side > 0 and p[2] < spec.get("height", 0.03)]
        if not pts:
            continue
        cx = sum(p.x for p in pts) / len(pts)
        cy = sum(p.y for p in pts) / len(pts)
        seg = spec.get("segments", 14)
        grow = spec.get("grow", 0.012)
        outline = []
        for s in range(seg):
            ang = 2 * math.pi * s / seg
            d = Vector((math.sin(ang), -math.cos(ang), 0))
            best = max(((p - Vector((cx, cy, p.z))).dot(d) for p in pts), default=0.04)
            outline.append(Vector((cx, cy, 0)) + d * (best + grow))
        lo, hi = spec.get("bottom", -0.004), spec.get("thickness", 0.025)
        bot = [part.add_vert(Vector((p.x, p.y, lo))) for p in outline]
        top = [part.add_vert(Vector((p.x, p.y, hi))) for p in outline]
        for s in range(seg):
            t = (s + 1) % seg
            part.faces.append((bot[s], bot[t], top[t], top[s]))
        part.faces.append(tuple(top))
        part.faces.append(tuple(reversed(bot)))
        parts.append(part)
    return parts


TEMPLATES = {"shell": t_shell, "band": t_band, "panel": t_panel, "box": t_box, "strap": t_strap,
             "cap": t_cap, "cup": t_cup, "sole": t_sole}


# =============================================================================================
# Texture procedurali (in spazio 3D, cotte nell'UV): niente cuciture
# =============================================================================================

def hex_color(c):
    if isinstance(c, str):
        c = c.lstrip("#")
        return np.array([int(c[i:i + 2], 16) / 255 for i in (0, 2, 4)])
    return np.array(c[:3], dtype=float)


def material_texture(kind, base, size, pos, nrm, mask, seed):
    """Colore per texel di un materiale. `base` sRGB (3,). pos/nrm: mappe HxWx3."""
    h = w = size
    n1 = C.fbm(w, h, max(2, w // 16), max(2, h // 16), seed, 4)
    grain = np.random.default_rng(seed).random((h, w))
    col = np.ones((h, w, 3)) * base[None, None, :]
    if kind in ("canvas", "denim", "cotton"):
        weave = (np.sin(pos[..., 0] * 900) * np.sin(pos[..., 2] * 900)) * 0.03
        col *= (0.88 + 0.22 * n1 + weave)[..., None]
        col *= (0.96 + 0.06 * grain)[..., None]
    elif kind == "knit":
        rib = 0.9 + 0.1 * (np.sin(np.arctan2(pos[..., 0], -pos[..., 1]) * 60) > 0)
        col *= (rib * (0.9 + 0.15 * n1))[..., None]
    elif kind in ("nylon", "cordura"):
        grid = 0.94 + 0.06 * ((np.floor(pos[..., 0] * 160) + np.floor(pos[..., 2] * 160)) % 2)
        col *= (grid * (0.9 + 0.15 * n1))[..., None]
    elif kind == "rubber":
        col *= (0.92 + 0.12 * n1 + 0.03 * grain)[..., None]
    elif kind == "leather":
        wr = C.fbm(w, h, max(2, w // 6), max(2, h // 6), seed + 5, 3)
        col *= (0.8 + 0.3 * n1 - 0.15 * (wr > 0.62))[..., None]
    elif kind in ("paint", "metal_paint"):
        chips = C.fbm(w, h, max(2, w // 10), max(2, h // 10), seed + 9, 4) > 0.68
        col *= (0.9 + 0.12 * n1)[..., None]
        col[chips] = col[chips] * 0.55 + np.array([0.42, 0.42, 0.40]) * 0.45
    elif kind == "plastic":
        col *= (0.95 + 0.08 * n1)[..., None]
    elif kind == "lens":
        col *= (0.85 + 0.2 * (nrm[..., 2] > 0.3))[..., None]
    elif kind == "metal":
        col *= (0.8 + 0.35 * n1)[..., None]
    elif kind == "camo":
        pal = [base, base * 0.62, np.array([0.30, 0.27, 0.20]), np.array([0.17, 0.16, 0.12])]
        f1 = C.fbm(w, h, max(2, w // 20), max(2, h // 20), seed + 1, 3)
        f2 = C.fbm(w, h, max(2, w // 14), max(2, h // 14), seed + 2, 3)
        col[:] = pal[0]
        col[f1 > 0.55] = pal[1]
        col[f2 > 0.62] = pal[2]
        col[(f1 < 0.35) & (f2 > 0.5)] = pal[3]
        col *= (0.93 + 0.1 * grain)[..., None]
    return col


def paint_details(img, pos, nrm, mask, part_map, details, body, colors):
    """Dettagli disegnati per posizione 3D: rect (tasche, toppe), line (cuciture, zip), image
    (proiezione di una foto di riferimento)."""
    x, y, z = pos[..., 0], pos[..., 1], pos[..., 2]
    for d in details:
        sel = mask.copy()
        if "parts" in d:
            sel &= np.isin(part_map, d["parts"])
        face = d.get("face")
        if face == "front":
            sel &= nrm[..., 1] < -0.2
        elif face == "back":
            sel &= nrm[..., 1] > 0.2
        elif face == "left":
            sel &= nrm[..., 0] > 0.2
        elif face == "right":
            sel &= nrm[..., 0] < -0.2
        color = hex_color(colors.get(d.get("color"), d.get("color", "#000000")))
        alpha = d.get("alpha", 1.0)
        kind = d["kind"]
        if kind == "rect":
            hx = body.scalar(d["at"][0], 0)
            hz = body.z(d["at"][1])
            w, h = d["size"]
            u = x if face in ("front", "back", None) else y
            mirror = d.get("mirror", False)
            inside = (np.abs((np.abs(u) if mirror else u) - hx) < w / 2) & (np.abs(z - hz) < h / 2)
            area = sel & inside
            img[area] = img[area] * (1 - alpha) + color * alpha
            if d.get("outline"):
                oc = hex_color(colors.get(d["outline"], d["outline"]))
                edge = area & ((np.abs((np.abs(u) if mirror else u) - hx) > w / 2 - 0.006) |
                               (np.abs(z - hz) > h / 2 - 0.006))
                img[edge] = oc
        elif kind == "line":
            axis = "xyz".index(d.get("axis", "x"))
            val = pos[..., axis]
            at = body.scalar(d["at"], axis)
            dist = np.abs((np.abs(val) if d.get("mirror") else val) - at)
            area = sel & (dist < d.get("width", 0.006) / 2)
            if "range" in d:
                r_axis = "xyz".index(d.get("range_axis", "z"))
                lo, hi = (body.scalar(v, r_axis) for v in d["range"])
                area &= (pos[..., r_axis] > lo) & (pos[..., r_axis] < hi)
            if d.get("dashed"):
                other = pos[..., 2] if axis != 2 else pos[..., 0]
                area &= (np.floor(other / 0.012) % 2) == 0
            img[area] = img[area] * (1 - alpha) + color * alpha
        elif kind == "image":
            ref = C.load_image_array(d["path"])[..., :3]
            hh, ww = ref.shape[:2]
            x0, z0, x1, z1 = (body.scalar(d["rect"][0], 0), body.z(d["rect"][1]),
                              body.scalar(d["rect"][2], 0), body.z(d["rect"][3]))
            u = (x - x0) / (x1 - x0)
            if face == "back":
                u = 1 - u
            v = (z - z0) / (z1 - z0)
            area = sel & (u >= 0) & (u <= 1) & (v >= 0) & (v <= 1)
            px = np.clip((u * (ww - 1)).astype(int), 0, ww - 1)
            py = np.clip((v * (hh - 1)).astype(int), 0, hh - 1)
            sample = ref[py, px]
            img[area] = img[area] * (1 - alpha) + sample[area] * alpha
    return img
