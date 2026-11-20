"""Sotto-pipeline OUTFIT (skill `asset-ingest` §4): il modello leggero e stilizzato dalla base diventa
un capo indossabile sul corpo del gioco (skill `character-wearables`).

  1. montaggio: si colloca rispetto al corpo a riposo secondo `mount`
       back   sulla schiena (zaini): centrato in x, cima alla quota `top`, spinto indietro finche'
              le parti rigide stanno a `clearance` dal corpo;
       head   sulla testa (elmetti, cuffie): centrato sul cranio, `top` sopra il vertice;
       chest  sul petto (rig, corpetti): spinto in avanti finche' stanno a `clearance`;
       none   gia' nella posizione giusta (modelli fatti sul nostro corpo).
  2. conformazione delle parti morbide (`conform`: spallacci, cinghie, maniche): ogni vertice va sulla
     superficie del corpo + `clearance`, CONSERVANDO lo spessore locale (distanza dal lato interno
     della parte), con un peso che sale allontanandosi dal blocco rigido; poi un po' di levigatura;
  3. pesi: parti rigide su `rigid_bone` (con `rigidity`), parti conformate dai vertici del corpo
     vicini; regioni dal vertice del corpo piu' vicino;
  4. uscite comuni del vestiario (`wearables/wexport.finalize`).
"""

import bpy
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree
from mathutils.kdtree import KDTree

import charlib as C
import wexport as X


def run(low, manifest, project_dir, export=True, preview_on=False):
    cfg = manifest["outfit"]
    X.configure(project_dir)
    body = X.load_body()
    names = list(manifest["parts"])
    conform_parts = {names.index(n) for n in cfg.get("conform", []) if n in names}

    mount(low, body, cfg, soft_vertices(low, conform_parts))
    if conform_parts:
        # solo cio' che sta fra il blocco rigido e il corpo va infittito (le fettucce sul retro no)
        co = _coords(low)
        rigid = co[~soft_vertices(low, conform_parts)]
        kind = cfg.get("mount", "back")
        contact = rigid[:, 1].min() if kind == "back" else rigid[:, 1].max()
        front = (lambda y: y < contact) if kind == "back" else (lambda y: y > contact)
        densify(low, conform_parts, cfg.get("max_edge", 0.03), front)
    soft = soft_vertices(low, conform_parts)
    me = low.data
    w_soft = conform(low, body, cfg, soft) if soft.any() else np.zeros(len(me.vertices))
    weights, regions = skin(low, body, cfg, w_soft)

    obj = make_worn(low, body, weights, regions, manifest["id"])
    spec = {"item_id": manifest["id"], "slot": cfg["slot"], "layer": cfg["layer"],
            "ground": cfg.get("ground", "stand"), "covers": cfg.get("covers", []),
            "item": cfg.get("item", {}), "names": manifest.get("names"),
            "first_person": cfg.get("first_person", False)}
    report = X.finalize(obj, body, spec, export=export, preview_on=preview_on)
    report["soft_vertices"] = int(soft.sum())
    report["conformed_vertices"] = int((w_soft > 0).sum())
    return report


# =============================================================================================
# 1. Montaggio
# =============================================================================================

def _coords(obj):
    return np.array([tuple(v.co) for v in obj.data.vertices])


def _move(obj, delta):
    for v in obj.data.vertices:
        v.co += Vector(delta)


def mount(low, body, cfg, soft):
    co = _coords(low)
    rigid = co[~soft] if (~soft).any() else co
    kind = cfg.get("mount", "none")
    if kind == "none":
        return
    if kind == "head":
        head = body.co[np.isin(body.region, [C.REGION["Scalp"], C.REGION["Face"]])]
        cx, cy = 0.0, float(head[:, 1].mean())
        top = float(head[:, 2].max()) + body.scalar(cfg.get("top", 0.0), 2)
        _move(low, ((cx - (rigid[:, 0].min() + rigid[:, 0].max()) / 2),
                    cy - (rigid[:, 1].min() + rigid[:, 1].max()) / 2, top - rigid[:, 2].max()))
        return
    # schiena / petto: centrato in x, cima alla quota richiesta
    dz = body.z(cfg["top"]) - rigid[:, 2].max() if "top" in cfg else 0.0
    dx = -(rigid[:, 0].min() + rigid[:, 0].max()) / 2
    _move(low, (dx, 0.0, dz))
    co = _coords(low)
    rigid = co[~soft] if (~soft).any() else co
    clearance = cfg.get("clearance", 0.04)
    bvh = BVHTree.FromPolygons([Vector(p) for p in body.co], body.faces)
    # raggio da ogni vertice rigido verso il corpo: lo spazio libero davanti a lui
    toward = Vector((0, -1, 0)) if kind == "back" else Vector((0, 1, 0))
    if kind == "back":
        start_y = body.co[:, 1].max() + 0.5           # tutto dietro la schiena
        place = lambda p: Vector((p[0], start_y, p[2]))
    else:
        start_y = body.co[:, 1].min() - 0.5           # tutto davanti al petto
        place = lambda p: Vector((p[0], start_y, p[2]))
    # prima si porta il blocco rigido fuori dal corpo, poi alla distanza giusta
    need = -1e9
    for p in rigid:
        hit = bvh.ray_cast(place(p), toward, 2.0)[0]
        if hit is None:
            continue
        # gap > 0: il vertice e' dalla parte giusta del corpo
        gap = (p[1] - hit.y) if kind == "back" else (hit.y - p[1])
        need = max(need, clearance - gap)
    if need > -1e9:
        _move(low, (0.0, need if kind == "back" else -need, 0.0))


# =============================================================================================
# 2. Conformazione delle parti morbide
# =============================================================================================

TORSO = ("Chest", "Abdomen", "Pelvis", "Neck")


def soft_vertices(low, conform_parts):
    """Vertici delle parti da conformare (un vertice condiviso con una parte rigida resta rigido)."""
    me = low.data
    part = np.zeros(len(me.polygons), dtype=int)
    me.attributes["part"].data.foreach_get("value", part)
    vpart = np.full(len(me.vertices), -1)
    for p, pi in zip(me.polygons, part):
        for v in p.vertices:
            if vpart[v] == -1 or pi not in conform_parts:
                vpart[v] = pi
    return np.array([vp in conform_parts for vp in vpart])


def densify(low, conform_parts, max_edge, where, rounds=4):
    """Le parti da conformare, decimate, hanno spigoli lunghi: un tratto di cinghia che deve
    girare sopra la spalla sarebbe un solo segmento dritto attraverso il busto. Si dividono gli
    spigoli piu' lunghi di `max_edge` che stanno dove `where(y)` (l'attributo `part` delle facce
    passa alle figlie)."""
    import bmesh
    bm = bmesh.new()
    bm.from_mesh(low.data)
    layer = bm.faces.layers.int.get("part")
    for _ in range(rounds):
        edges = [e for e in bm.edges if e.calc_length() > max_edge
                 and any(where(v.co.y) for v in e.verts)
                 and any(f[layer] in conform_parts for f in e.link_faces)]
        if not edges:
            break
        bmesh.ops.subdivide_edges(bm, edges=edges, cuts=1, use_grid_fill=True)
    bmesh.ops.triangulate(bm, faces=[f for f in bm.faces if len(f.verts) > 4])
    bm.to_mesh(low.data)
    bm.free()
    low.data.update()


def _spine_core(body, z):
    """Punto sull'asse della colonna alla quota z (fra bacino e collo)."""
    # fermo alla quota di spine_03: da li' un punto sopra la spalla ha una direzione obliqua che
    # entra dal trapezio; un asse piu' alto manderebbe i raggi dentro il collo, aperto in cima
    chain = [body.joints[n] for n in ("pelvis", "spine_01", "spine_02", "spine_03")]
    z = min(max(z, chain[0].z), chain[-1].z)
    for a, b in zip(chain, chain[1:]):
        if a.z <= z <= b.z:
            t = (z - a.z) / max(b.z - a.z, 1e-6)
            return Vector((0.0, a.y + (b.y - a.y) * t, z))
    return Vector((0.0, chain[-1].y, z))


def conform(low, body, cfg, soft):
    """Parti morbide sul BUSTO + `strap_clearance`, spessore conservato.

    Il modello sorgente avvolge le cinghie attorno a un busto generico, di misure diverse dal nostro:
    si conserva la loro POSIZIONE ANGOLARE attorno alla colonna. Ogni vertice si proietta dall'asse
    della colonna verso l'esterno (raggio dal punto dell'asse alla sua quota, tenuta fra bacino e
    collo: sopra le spalle il raggio sale e trova il trapezio) e va sulla superficie del busto
    (braccia e testa escluse: niente cinghie avvolte attorno alle braccia in T-pose) + distanza +
    spessore locale. Il peso della conformazione cresce allontanandosi dal piano d'appoggio del
    blocco rigido (`blend_zone`): le radici delle cinghie restano attaccate allo zaino."""
    me = low.data
    clearance = cfg.get("strap_clearance", cfg.get("clearance", 0.04) * 0.6)
    zone = cfg.get("blend_zone", 0.06)
    torso = {C.REGION[n] for n in TORSO}
    faces = [f for f in body.faces if all(body.region[i] in torso for i in f)]
    bvh = BVHTree.FromPolygons([Vector(p) for p in body.co], faces)
    co = _coords(low)
    rigid = co[~soft]
    kind = cfg.get("mount", "back")
    # piano d'appoggio: la faccia del blocco rigido verso il corpo
    contact = rigid[:, 1].min() if kind == "back" else rigid[:, 1].max()

    # Il busto per cui e' stato fatto il modello non e' il nostro: l'anello delle cinghie si allunga
    # in profondita', in proporzione, finche' la sua parte piu' avanzata arriva al petto (o alla
    # schiena per un capo sul petto). Le radici, sul piano d'appoggio, non si muovono.
    torso_pts = body.co[np.isin(body.region, list(torso))]
    if kind == "back":
        far_body = torso_pts[:, 1].min() - clearance          # davanti al petto
        far_soft = co[soft][:, 1].min()
        span_soft, span_body = contact - far_soft, contact - far_body
    else:
        far_body = torso_pts[:, 1].max() + clearance
        far_soft = co[soft][:, 1].max()
        span_soft, span_body = far_soft - contact, far_body - contact
    stretch = span_body / span_soft if span_soft > 0.02 else 1.0
    if stretch > 1.0:
        for i in np.where(soft)[0]:
            depth = (contact - co[i][1]) if kind == "back" else (co[i][1] - contact)
            if depth > 0:
                co[i][1] = contact - depth * stretch if kind == "back" else contact + depth * stretch
    print(f"[outfit] anello delle cinghie allungato x{stretch:.2f}")

    hits = {}
    radius = np.zeros(len(co))
    for i in np.where(soft)[0]:
        p = Vector(co[i])
        core = _spine_core(body, p.z)
        d = p - core
        if d.length < 1e-4:
            continue
        d.normalize()
        hit, nrm, _, _ = bvh.ray_cast(core + d * 1.0, -d, 1.0)
        if hit is None or nrm.dot(d) < 0.0:
            # niente superficie esterna lungo il raggio: si ripiega sul punto piu' vicino
            hit, nrm, _, _ = bvh.find_nearest(p)
            if hit is None:
                continue
            d = nrm.normalized()
        hits[i] = (hit, nrm.normalized(), d)
        radius[i] = (p - core).length
    # spessore: distanza radiale dal lato interno della parte, vicino per vicino
    kd = KDTree(len(co))
    for i, p in enumerate(co):
        kd.insert(p, i)
    kd.balance()
    thick = np.zeros(len(co))
    for i in hits:
        local = [radius[j] for _, j, _ in kd.find_range(co[i], cfg.get("thickness_radius", 0.02)) if j in hits]
        thick[i] = radius[i] - min(local)
    thick = np.clip(thick, 0, cfg.get("max_thickness", 0.025))

    print(f"[outfit] contatto y={contact:.3f}  morbidi={int(soft.sum())}  con proiezione={len(hits)}  "
          f"y morbidi {co[soft][:, 1].min():.3f}..{co[soft][:, 1].max():.3f}")
    w = np.zeros(len(co))
    for i, (hit, nrm, d) in hits.items():
        out = (contact - co[i][1]) if kind == "back" else (co[i][1] - contact)
        w[i] = np.clip(out / zone, 0, 1)
        target = np.array(hit + d * (clearance + thick[i]))
        co[i] = co[i] * (1 - w[i]) + target * w[i]
    # levigatura dei vertici spostati
    nbrs = [set() for _ in co]
    for e in me.edges:
        a, b = e.vertices
        nbrs[a].add(b)
        nbrs[b].add(a)
    for _ in range(cfg.get("smooth", 2)):
        new = co.copy()
        for i in np.where(w > 0)[0]:
            if nbrs[i]:
                avg = co[list(nbrs[i])].mean(axis=0)
                new[i] = co[i] * 0.5 + avg * 0.5
        co = new
    for v, p in zip(me.vertices, co):
        v.co = Vector(p)
    me.update()
    return w


# =============================================================================================
# 3. Pesi e regioni
# =============================================================================================

def skin(low, body, cfg, w_soft):
    rigid_bone = cfg.get("rigid_bone", "spine_03")
    rigidity = cfg.get("rigidity", 0.85)
    weights, regions = [], []
    for i, v in enumerate(low.data.vertices):
        near, region = body.weights_at(v.co)
        r = rigidity * (1 - w_soft[i])          # le parti conformate seguono il corpo
        w = {b: x * (1 - r) for b, x in near.items()}
        w[rigid_bone] = w.get(rigid_bone, 0.0) + r
        weights.append(w)
        regions.append(region)
    return weights, regions


def make_worn(low, body, weights, regions, item_id):
    low.name = low.data.name = item_id
    low.vertex_groups.clear()
    groups = {}
    for i, w in enumerate(weights):
        for bone, x in w.items():
            if x <= 1e-4:
                continue
            if bone not in groups:
                groups[bone] = low.vertex_groups.new(name=bone)
            groups[bone].add([i], x, "REPLACE")
    C.limit_and_normalize(low, 4)
    C.write_region_colors(low, regions)
    if "part" in low.data.attributes:
        low.data.attributes.remove(low.data.attributes["part"])
    low.parent = body.armature
    mod = low.modifiers.new("Armature", "ARMATURE")
    mod.object = body.armature
    return low
