"""
Terreno della laguna dai SOLI dati OpenStreetMap (niente DEM).

Legge uno o piu' JSON prodotti da osm_extract.py (di solito la citta' completa
e la copertura del suolo scaricata con --landcover), assegna a ogni vertice di
terreno (1 m) una CLASSE (mare, laguna, acqua bassa, barena, velma, canale...)
e da quella una QUOTA "di gioco": non sono metri reali, sono livelli scelti
perche' terra e acqua si leggano e si giochino bene. Il mare e' a quota 0
(WaterVolume.SurfaceY): ogni classe di terra resta sopra +0,15, ogni classe
d'acqua sotto -0,1.

Mappa vera (map.json): legge <mappa>/osm/layers e scrive, per ogni region di
Terrain3D (2048 x 2048 vertici, origine loc*2048) toccata dal poligono:
    build/tiles/height_<x>_<z>.f32   float32 row-major (riga = Z, colonna = X), quote
    build/tiles/color_<x>_<z>.rgba   RGBA8: tinta della classe (rugosita' neutra)
    classes/classes_<x>_<z>.png      id della classe (tabella CLASSES), per texture e gameplay
    far/far_atlas.png (+ .json)      atlante del terreno lontano, 8 m/px, alfa = terra
    build/tiles/meta.json, build/preview.png
I tile si importano con terrain_import.gd (che poi li cancella).

Solo libreria standard + numpy; le region si calcolano in parallelo. Uso:
    python osm_terrain.py --map world/maps/laguna/map.json
    python osm_terrain.py --map world/maps/laguna/map.json "--only=-4,-4;-1,-7"   (collaudo)
    python osm_terrain.py --map world/maps/laguna/map.json --far-only           (solo atlante)
    (--only/--regions col segno uguale: argparse prende "-4" per un'opzione)
Senza --map: --input <JSON o cartelle di livelli> --out <cartella>.
"""

import argparse
import json
import math
import os
import re
import struct
import sys
import time
import zlib
from collections import defaultdict

import numpy as np

REGION = 2048
# Bordo calcolato in piu' attorno a ogni region: la sfumatura e il rumore
# vedono i vicini, quindi due region adiacenti combaciano senza cuciture.
MARGIN = 24
BLUR_RADIUS = 3          # box blur ripetuto 3 volte: transizione di ~6 m
BLUR_PASSES = 3
SHORE_RADIUS = 1         # rive delle isole: transizione di 2-3 m
SHORE_PASSES = 2
LAND_MIN = 0.15          # nessuna terra sotto questa quota
WATER_MAX = -0.1         # nessuna acqua sopra questa quota
PILE_RANGE = 80.0        # m: una bricola entro questa distanza da un canale lo segna

# id, nome, quota base, ampiezza del rumore, tinta (color map)
CLASSES = (
    (0, "mare", -8.0, 0.3, (47, 93, 124)),
    (1, "terraferma", 1.5, 0.12, (154, 143, 106)),
    (2, "laguna_aperta", -1.5, 0.25, (79, 127, 143)),
    (3, "acqua_bassa", -0.6, 0.12, (111, 154, 138)),
    (4, "valle_stagno", -1.0, 0.05, (76, 122, 106)),
    (5, "isola", 1.0, 0.0, (163, 154, 134)),
    (6, "prato", 1.1, 0.1, (127, 154, 79)),
    (7, "bosco", 1.2, 0.12, (63, 95, 47)),
    (8, "spiaggia", 0.6, 0.1, (216, 199, 154)),
    (9, "barena", 0.3, 0.06, (138, 154, 90)),
    (10, "velma", -0.15, 0.03, (122, 106, 80)),
    (11, "canale", -4.0, 0.2, (47, 95, 115)),
    (12, "rio", -2.5, 0.0, (58, 90, 102)),
    (13, "ghebo", -0.4, 0.0, (90, 106, 88)),
)
BASE = np.zeros(256, np.float32)
NOISE_AMP = np.zeros(256, np.float32)
COLOR = np.full((256, 4), 128, np.uint8)
for _id, _name, _h, _amp, _rgb in CLASSES:
    BASE[_id], NOISE_AMP[_id], COLOR[_id, :3] = _h, _amp, _rgb
COLOR[:, 3] = 128

LAND = (1, 5, 6, 7, 8, 9)
WATER = (0, 2, 3, 4, 10, 11, 12, 13)
CARVE_CANAL = (2, 3, 4, 10, 11, 12)   # un canale approfondisce solo l'acqua
CARVE_GHEBO = (5, 6, 7, 9, 10)        # un ghebo incide barene, velme e campi
SHARP_SHORE = (2, 11, 12)             # acqua che tocca un'isola: riva a muro

STAGNANT = ("pond", "reservoir", "basin", "moat", "fishpond", "wastewater", "ditch", "drain", "fish_pass")
GRASS_LANDUSE = ("grass", "meadow", "farmland", "vineyard", "orchard", "greenhouse_horticulture",
                 "allotments", "village_green", "plant_nursery")
GRASS_NATURAL = ("grassland", "heath")
WOOD = ("wood", "scrub")
MARSH = ("saltmarsh", "marsh", "reedbed", "swamp", "wet_meadow")
TIDAL = ("tidalflat",)


# ------------------- DATI -------------------
# Livelli che servono al terreno: da una cartella di livelli si leggono solo questi.
TERRAIN_LAYERS = ("land", "lagoon", "coast", "water", "waterways", "areas", "points")


def load_layers(paths):
    """Unisce i livelli di piu' JSON (o cartelle di livelli di osm_extract.py --map);
    un elemento presente in due file vale una volta."""
    layers = defaultdict(dict)
    metas = []
    files = []
    for path in paths:
        if os.path.isdir(path):
            files += [os.path.join(path, n + ".json") for n in TERRAIN_LAYERS if os.path.exists(os.path.join(path, n + ".json"))]
        else:
            files.append(path)
    for path in files:
        with open(path, "r", encoding="utf-8") as f:
            doc = json.load(f)
        for name, items in doc.items():
            if name == "meta":
                metas.append(items)
                continue
            for item in items:
                layers[name][item["id"]] = item
    return {k: list(v.values()) for k, v in layers.items()}, metas


def rings_np(rings):
    return [np.asarray(r, np.float64) for r in rings if len(r) >= 3]


def bounds(rings):
    allp = np.concatenate(rings)
    return allp[:, 0].min(), allp[:, 1].min(), allp[:, 0].max(), allp[:, 1].max()


def ring_area(r):
    p = r - r[0]
    return 0.5 * abs(np.sum(p[:, 0] * np.roll(p[:, 1], -1) - np.roll(p[:, 0], -1) * p[:, 1]))


class Poly:
    """Poligono (anelli pari/dispari) con la regola di pittura."""
    __slots__ = ("cls", "rings", "box", "rule", "prio", "src")

    def __init__(self, cls, rings, rule, prio, src):
        self.cls, self.rings, self.rule, self.prio, self.src = cls, rings, rule, prio, src
        self.box = bounds(rings)


class Line:
    __slots__ = ("pts", "half", "depth", "cls", "box", "src")

    def __init__(self, pts, half, depth, cls, src):
        self.pts, self.half, self.depth, self.cls, self.src = pts, half, depth, cls, src
        self.box = (pts[:, 0].min() - half, pts[:, 1].min() - half, pts[:, 0].max() + half, pts[:, 1].max() + half)


def area_rule(tags):
    """(classe, regola, priorita') di un'area OSM, o None se non interessa al terreno."""
    nat, lu, wet, wat, leis = (tags.get(k) for k in ("natural", "landuse", "wetland", "water", "leisure"))
    if wet in TIDAL or nat == "mud":
        return 10, "any", 60
    if wet in MARSH:
        return 9, "any", 50
    if nat == "wetland":
        # In laguna "natural=wetland" senza sottotipo indica SPECCHI D'ACQUA BASSA
        # (es. relazione "Laguna Sud": "fondali bassi ... che non emergono mai"),
        # non paludi emerse. Sulla terra invece e' una palude: barena.
        return (3, "water", 40) if wat == "shallow" else (3, "wetland", 40)
    if nat in ("beach", "sand", "shingle"):
        return 8, "beach", 20
    if nat in WOOD or lu == "forest":
        return 7, "land", 12
    if lu in GRASS_LANDUSE or nat in GRASS_NATURAL or leis in ("park", "garden"):
        return 6, "land", 10
    if lu == "aquaculture":
        return 4, "any", 30
    return None


def build_features(layers):
    polys, lines = [], []

    # Laguna: dalla piu' grande alla piu' piccola, cosi' una laguna minore dentro
    # il buco di un'altra la ridipinge. Esterno = acqua, buchi = isole.
    lagoon = [rings_np(it["rings"]) for it in layers.get("lagoon", [])]
    lagoon = [r for r in lagoon if r]
    for rings in sorted(lagoon, key=lambda r: -ring_area(r[0])):
        polys.append(Poly(2, rings, "lagoon", 0, "lagoon"))
    if not lagoon:
        # JSON vecchio, senza livello lagoon: acque "lagoon" + isole dal livello land
        for it in layers.get("water", []):
            if it.get("kind") == "lagoon":
                polys.append(Poly(2, rings_np(it["rings"]), "any", 0, "water/lagoon"))
        for it in layers.get("land", []):
            polys.append(Poly(5, rings_np(it["rings"]), "any", 1, "land"))

    for it in layers.get("areas", []):
        rule = area_rule(it.get("tags", {}))
        rings = rings_np(it["rings"])
        if rule and rings:
            polys.append(Poly(rule[0], rings, rule[1], rule[2], f"areas/{it.get('kind')}"))

    for it in layers.get("water", []):
        kind = it.get("kind") or "water"
        tags = it.get("tags", {})
        rings = rings_np(it["rings"])
        if kind == "lagoon" or not rings:
            continue
        if kind == "shallow":
            polys.append(Poly(3, rings, "water", 45, "water/shallow"))
        elif kind in STAGNANT or "fish" in tags.get("produce", ""):
            polys.append(Poly(4, rings, "any", 70, f"water/{kind}"))
        else:
            polys.append(Poly(11, rings, "any", 70, f"water/{kind}"))

    polys.sort(key=lambda p: p.prio)          # stabile: a parita' resta l'ordine dei dati

    # Linee d'acqua: canali (approfondiscono l'acqua) e ghebi (incidono la terra).
    widths = pile_widths(layers)
    for it in layers.get("waterways", []):
        kind = it.get("kind")
        tags = it.get("tags", {})
        if tags.get("tunnel") or tags.get("location") == "underground":
            continue
        pts = np.asarray(it["pts"], np.float64)
        if len(pts) < 2:
            continue
        name = it.get("name") or ""
        if kind in ("canal", "river", "fairway"):
            rio = bool(re.match(r"(?i)ri(o|elo|eto|etto)\b", name))
            w = it.get("w") or widths.get(it["id"]) or (8.0 if rio else 30.0 if name else 12.0)
            depth = -(2.0 + min(w, 40.0) / 10.0)
            lines.append(Line(pts, max(w, 2.0) / 2.0, depth, 12 if rio else 11, f"waterways/{kind}"))
        elif kind in ("stream", "drain", "ditch", "tidal_channel"):
            w = it.get("w") or {"stream": 5.0, "tidal_channel": 5.0, "drain": 3.0, "ditch": 2.0}[kind]
            lines.append(Line(pts, max(w, 1.5) / 2.0, -0.5 if kind in ("stream", "tidal_channel") else -0.4, 13, f"waterways/{kind}"))
    return polys, lines


def pile_widths(layers):
    """Larghezza dei canali segnati dalle bricole: 2 x mediana della distanza
    delle bricole dall'asse (le bricole stanno sul ciglio del canale)."""
    canals = [it for it in layers.get("waterways", [])
              if it.get("kind") in ("canal", "fairway") and not it.get("w") and len(it.get("pts", [])) >= 2]
    piles = np.asarray([p["p"] for p in layers.get("points", []) if p.get("kind") == "pile"], np.float64)
    if not canals or len(piles) == 0:
        return {}
    seg_a, seg_b, seg_way = [], [], []
    for i, it in enumerate(canals):
        p = np.asarray(it["pts"], np.float64)
        seg_a.append(p[:-1]); seg_b.append(p[1:]); seg_way.append(np.full(len(p) - 1, i))
    a, b, way = np.concatenate(seg_a), np.concatenate(seg_b), np.concatenate(seg_way)
    d = b - a
    dd = np.maximum((d * d).sum(1), 1e-9)
    best_d = np.full(len(piles), np.inf)
    best_w = np.full(len(piles), -1)
    for k in range(0, len(piles), 64):
        p = piles[k:k + 64, None, :]
        u = np.clip(((p - a) * d).sum(2) / dd, 0, 1)
        dist = np.linalg.norm(p - (a + u[..., None] * d), axis=2)
        j = dist.argmin(1)
        best_d[k:k + 64] = dist[np.arange(len(j)), j]
        best_w[k:k + 64] = way[j]
    out = {}
    for i, it in enumerate(canals):
        ds = best_d[(best_w == i) & (best_d < PILE_RANGE)]
        if len(ds) >= 3:
            out[it["id"]] = float(np.clip(2.0 * np.median(ds), 10.0, 120.0))
    return out


# ------------------- RASTER -------------------
def fill_mask(rings, x0, z0, w, h):
    """Vertici interi (x0+i, z0+j) dentro gli anelli, regola pari/dispari
    (un poligono con buchi si passa come [esterno, buco, ...]).
    Scanline vettoriale: incroci di ogni riga, ordinati, presi a coppie."""
    a = np.concatenate(rings)
    b = np.concatenate([np.roll(r, -1, axis=0) for r in rings])
    ax, az = a[:, 0] - x0, a[:, 1] - z0
    bx, bz = b[:, 0] - x0, b[:, 1] - z0
    lo, hi = np.minimum(az, bz), np.maximum(az, bz)
    # stessa regola di polish_terrain.gd: la riga z conta se (az <= z) != (bz <= z),
    # cioe' lo <= z < hi (i lati orizzontali non contano mai)
    r0 = np.maximum(np.ceil(lo).astype(np.int64), 0)
    r1 = np.minimum(np.ceil(hi).astype(np.int64) - 1, h - 1)
    n = np.maximum(r1 - r0 + 1, 0)
    mask = np.zeros((h, w), bool)
    total = int(n.sum())
    if total == 0:
        return mask
    e = np.repeat(np.arange(len(n)), n)
    rows = np.repeat(r0, n) + (np.arange(total) - np.repeat(np.cumsum(n) - n, n))
    x = ax[e] + (rows - az[e]) * (bx[e] - ax[e]) / (bz[e] - az[e])
    order = np.lexsort((x, rows))
    rows, x = rows[order], x[order]
    if len(rows) % 2:
        return mask                              # anello aperto: dato rotto, si salta
    r = rows[0::2]
    start = np.clip(np.ceil(x[0::2]).astype(np.int64), 0, w)
    end = np.clip(np.floor(x[1::2]).astype(np.int64) + 1, 0, w)
    diff = np.bincount(r * (w + 1) + start, minlength=h * (w + 1)) - np.bincount(r * (w + 1) + end, minlength=h * (w + 1))
    mask = np.cumsum(diff.reshape(h, w + 1), axis=1)[:, :w] > 0
    return mask


def window_of(box, x0, z0, size):
    """Sottofinestra intera (i0, j0, i1, j1) del riquadro box, o None."""
    i0, j0 = max(int(math.floor(box[0] - x0)), 0), max(int(math.floor(box[1] - z0)), 0)
    i1, j1 = min(int(math.ceil(box[2] - x0)) + 1, size), min(int(math.ceil(box[3] - z0)) + 1, size)
    return (i0, j0, i1, j1) if i0 < i1 and j0 < j1 else None


def stroke(line, x0, z0, size, t_arr, d_arr):
    """Distanza normalizzata (0 sull'asse, 1 sul ciglio) dalla linea; tiene la minima."""
    for a, b in zip(line.pts[:-1], line.pts[1:]):
        box = (min(a[0], b[0]) - line.half, min(a[1], b[1]) - line.half,
               max(a[0], b[0]) + line.half, max(a[1], b[1]) + line.half)
        win = window_of(box, x0, z0, size)
        if not win:
            continue
        i0, j0, i1, j1 = win
        xs = np.arange(i0, i1, dtype=np.float64) + x0
        zs = np.arange(j0, j1, dtype=np.float64) + z0
        d = b - a
        dd = max(float(d @ d), 1e-9)
        px, pz = xs[None, :] - a[0], zs[:, None] - a[1]
        u = np.clip((px * d[0] + pz * d[1]) / dd, 0, 1)
        t = np.hypot(px - u * d[0], pz - u * d[1]) / line.half
        sub_t = t_arr[j0:j1, i0:i1]
        closer = t < sub_t
        sub_t[closer] = t[closer]
        d_arr[j0:j1, i0:i1][closer] = line.depth


def coast_sea(coast, x0, z0, size):
    """Mare = lato destro della linea di costa (OSM: terra a sinistra).

    Riga per riga: lo stato di un vertice e' quello subito a destra
    dell'incrocio piu' vicino alla sua sinistra (o subito a sinistra del primo
    incrocio, se non ne ha). Non serve un poligono chiuso: la costa entra ed
    esce dal riquadro. Righe senza incroci: niente mare."""
    sea = np.zeros((size, size), bool)
    if not coast:
        return sea
    a = np.concatenate([c[:-1] for c in coast]) - (x0, z0)
    b = np.concatenate([c[1:] for c in coast]) - (x0, z0)
    lo, hi = np.minimum(a[:, 1], b[:, 1]), np.maximum(a[:, 1], b[:, 1])
    r0 = np.maximum(np.ceil(lo).astype(np.int64), 0)
    r1 = np.minimum(np.ceil(hi).astype(np.int64) - 1, size - 1)
    n = np.maximum(r1 - r0 + 1, 0)
    total = int(n.sum())
    if total == 0:
        return sea
    e = np.repeat(np.arange(len(n)), n)
    rows = np.repeat(r0, n) + (np.arange(total) - np.repeat(np.cumsum(n) - n, n))
    x = a[e, 0] + (rows - a[e, 1]) * (b[e, 0] - a[e, 0]) / (b[e, 1] - a[e, 1])
    # In Godot Z cresce verso SUD: un tratto con dz > 0 ha la terra a +X (est).
    east_is_sea = (b[e, 1] - a[e, 1]) < 0
    order = np.lexsort((x, rows))
    rows, x, east_is_sea = rows[order], x[order], east_is_sea[order]
    cols = np.arange(size)
    for r in np.unique(rows):
        sel = rows == r
        xr, er = x[sel], east_is_sea[sel]
        k = np.searchsorted(xr, cols, side="right")          # incroci a sinistra del vertice
        state = np.where(k > 0, er[np.maximum(k - 1, 0)], ~er[0])
        sea[r] = state
    return sea


def box_blur(h, radius, passes):
    out = h.astype(np.float32)
    k = 2 * radius + 1
    for _ in range(passes):
        for axis in (0, 1):
            p = np.pad(out, [(radius, radius) if a == axis else (0, 0) for a in (0, 1)], mode="edge")
            c = np.cumsum(p, axis=axis, dtype=np.float64)
            c = np.concatenate([np.zeros_like(np.take(c, [0], axis=axis)), c], axis=axis)
            out = ((np.take(c, np.arange(k, c.shape[axis]), axis=axis)
                    - np.take(c, np.arange(0, c.shape[axis] - k), axis=axis)) / k).astype(np.float32)
    return out


def dilate(mask, steps):
    m = mask.copy()
    for _ in range(steps):
        n = m.copy()
        n[1:] |= m[:-1]; n[:-1] |= m[1:]; n[:, 1:] |= m[:, :-1]; n[:, :-1] |= m[:, 1:]
        m = n
    return m


def _hash(ix, iz, seed):
    h = (ix.astype(np.uint64) * np.uint64(0x9E3779B1) + iz.astype(np.uint64) * np.uint64(0x85EBCA77)
         + np.uint64(seed * 0xC2B2AE3D)) & np.uint64(0xFFFFFFFF)
    h ^= h >> np.uint64(15)
    h = (h * np.uint64(0x2C1B3C6D)) & np.uint64(0xFFFFFFFF)
    h ^= h >> np.uint64(12)
    h = (h * np.uint64(0x297A2D39)) & np.uint64(0xFFFFFFFF)
    h ^= h >> np.uint64(15)
    return (h.astype(np.float64) / 0xFFFFFFFF) * 2.0 - 1.0


def value_noise(x0, z0, size, cell, seed):
    """Rumore a valori su reticolo in coordinate MONDO: identico fra region vicine."""
    xs = (np.arange(size) + x0) / cell
    zs = (np.arange(size) + z0) / cell
    ix, iz = np.floor(xs).astype(np.int64), np.floor(zs).astype(np.int64)
    fx, fz = xs - ix, zs - iz
    fx, fz = fx * fx * (3 - 2 * fx), fz * fz * (3 - 2 * fz)
    IX, IZ = np.meshgrid(ix, iz)
    v00, v10 = _hash(IX, IZ, seed), _hash(IX + 1, IZ, seed)
    v01, v11 = _hash(IX, IZ + 1, seed), _hash(IX + 1, IZ + 1, seed)
    FX, FZ = np.meshgrid(fx, fz)
    return ((v00 * (1 - FX) + v10 * FX) * (1 - FZ) + (v01 * (1 - FX) + v11 * FX) * FZ).astype(np.float32)


# ------------------- REGION -------------------
def build_region(loc, polys, lines, coast):
    size = REGION + 2 * MARGIN
    x0, z0 = loc[0] * REGION - MARGIN, loc[1] * REGION - MARGIN
    reg_box = (x0, z0, x0 + size, z0 + size)

    def hits(box):
        return box[0] <= reg_box[2] and box[2] >= reg_box[0] and box[1] <= reg_box[3] and box[3] >= reg_box[1]

    cls = np.full((size, size), 1, np.uint8)                  # terraferma, salvo prova contraria
    cls[coast_sea([c for c in coast], x0, z0, size)] = 0

    for p in polys:
        win = window_of(p.box, x0, z0, size) if hits(p.box) else None
        if not win:
            continue
        i0, j0, i1, j1 = win
        sub = cls[j0:j1, i0:i1]
        m = fill_mask(p.rings, x0 + i0, z0 + j0, i1 - i0, j1 - j0)
        if p.rule == "lagoon":
            sub[m] = 2
            if len(p.rings) > 1:
                outer = fill_mask(p.rings[:1], x0 + i0, z0 + j0, i1 - i0, j1 - j0)
                sub[outer & ~m] = 5                           # i buchi della laguna sono isole
        elif p.rule == "any":
            sub[m] = p.cls
        elif p.rule == "land":
            sub[m & np.isin(sub, LAND)] = p.cls
        elif p.rule == "water":
            sub[m & np.isin(sub, WATER)] = p.cls
        elif p.rule == "wetland":
            sub[m & np.isin(sub, WATER)] = 3
            sub[m & np.isin(sub, LAND)] = 9
        elif p.rule == "beach":
            sub[m & np.isin(sub, (0, 1, 5, 6, 7))] = p.cls

    host = cls.copy()                                          # classe prima delle incisioni
    # distanza normalizzata e profondita' d'asse, una coppia per tipo di linea
    t = {k: np.full((size, size), np.inf, np.float32) for k in (11, 12, 13)}
    d = {k: np.zeros((size, size), np.float32) for k in (11, 12, 13)}
    for ln in lines:
        if hits(ln.box):
            stroke(ln, x0, z0, size, t[ln.cls], d[ln.cls])
    rio = t[12] < t[11]
    tc = np.where(rio, t[12], t[11])
    dc = np.where(rio, d[12], d[11])
    canal = (tc < 1) & np.isin(host, CARVE_CANAL)
    cls[canal] = np.where(rio[canal], 12, 11)
    ghebo = (t[13] < 1) & np.isin(host, CARVE_GHEBO)
    cls[ghebo] = 13
    tg, dg = t[13], d[13]
    del t, d

    # ---- quote ----
    h = BASE[cls].copy()
    # Profilo a V: profondita' d'asse al centro, quota della classe ospite sul
    # ciglio. Un canale area (classe 11 senza asse vicino) resta alla sua base.
    for mask, tt, dd in ((canal, tc, dc), (ghebo, tg, dg)):
        u = np.clip(tt[mask], 0, 1)
        u = u * u * (3 - 2 * u)
        edge = BASE[host[mask]]
        h[mask] = np.minimum(dd[mask] + (edge - dd[mask]) * u, edge)
    noise = value_noise(x0, z0, size, 61.0, 1) * 0.7 + value_noise(x0, z0, size, 17.0, 2) * 0.3
    h += NOISE_AMP[cls] * noise
    blurred = box_blur(h, BLUR_RADIUS, BLUR_PASSES)
    # Rive di citta': quasi a muro (2-3 m di transizione), niente scivolo lungo.
    # Un gradino netto da +1 a -4 a 1 m per vertice esce come una fila di denti
    # triangolari sulle rive oblique: la sfumatura corta li toglie.
    near_island = dilate(cls == 5, 2)
    sharp = (cls == 5) | (near_island & np.isin(cls, SHARP_SHORE))
    h = np.where(sharp, box_blur(h, SHORE_RADIUS, SHORE_PASSES), blurred)
    land = np.isin(cls, LAND)
    water = np.isin(cls, WATER)
    h[land] = np.maximum(h[land], LAND_MIN)
    h[water] = np.minimum(h[water], WATER_MAX)

    c = slice(MARGIN, MARGIN + REGION)
    return cls[c, c], h[c, c].astype(np.float32)


# ------------------- USCITE -------------------
FAR_SCALE = 8            # atlante lontano: 1 pixel = 8 m (256 px per region)


def write_png(path, img):
    """PNG senza dipendenze: (h, w) grigio, (h, w, 3) RGB o (h, w, 4) RGBA, uint8."""
    img = np.ascontiguousarray(img, np.uint8)
    h, w = img.shape[:2]
    channels = 1 if img.ndim == 2 else img.shape[2]
    color_type = {1: 0, 3: 2, 4: 6}[channels]
    rows = img.reshape(h, -1)
    raw = np.concatenate([np.zeros((h, 1), np.uint8), rows], axis=1).tobytes()

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, color_type, 0, 0, 0))
                + chunk(b"IDAT", zlib.compress(raw, 6)) + chunk(b"IEND", b""))


def read_png_gray(path):
    """Legge i PNG in scala di grigi scritti da write_png (un solo IDAT, filtro 0 su ogni riga)."""
    with open(path, "rb") as f:
        data = f.read()
    pos, idat, w, h = 8, b"", 0, 0
    while pos < len(data):
        length = struct.unpack(">I", data[pos:pos + 4])[0]
        tag = data[pos + 4:pos + 8]
        body = data[pos + 8:pos + 8 + length]
        if tag == b"IHDR":
            w, h = struct.unpack(">II", body[:8])
        elif tag == b"IDAT":
            idat += body
        pos += 12 + length
    rows = np.frombuffer(zlib.decompress(idat), np.uint8).reshape(h, w + 1)
    return rows[:, 1:].copy()


def fill_missing_far(atlas, generated, ring, rx0, rz0, rx1, rz1):
    """Celle dell'atlante senza region generata (fuori dal poligono): terraferma o mare,
    cosi' oltre il bordo della mappa l'orizzonte resta coerente invece di diventare un
    mare a gradini. Regola: il lato di CHIUSURA del poligono (ultimo vertice -> primo,
    Cortellazzo -> Chioggia) corre in mare aperto; un punto fuori dalla mappa e' mare se
    il lato piu' vicino e' quello, terraferma altrimenti. (La linea di costa OSM non basta:
    e' scaricata solo dentro il poligono e molte righe non la incrociano.)"""
    n = REGION // FAR_SCALE
    p = np.asarray(ring, np.float64)
    a, b = p, np.roll(p, -1, axis=0)                 # lato i: a[i] -> b[i]; l'ultimo chiude
    d = b - a
    dd = np.maximum((d * d).sum(1), 1e-9)
    land_rgb = COLOR[1, :3]
    for z in range(rz0, rz1 + 1):
        for x in range(rx0, rx1 + 1):
            if (x, z) in generated:
                continue
            c = (np.arange(n) + 0.5) * FAR_SCALE
            px = (x * REGION + c)[None, :, None]
            pz = (z * REGION + c)[:, None, None]
            u = np.clip(((px - a[:, 0]) * d[:, 0] + (pz - a[:, 1]) * d[:, 1]) / dd, 0, 1)
            dist = np.hypot(px - (a[:, 0] + u * d[:, 0]), pz - (a[:, 1] + u * d[:, 1]))
            sea = dist.argmin(axis=2) == len(p) - 1
            oz, ox = (z - rz0) * n, (x - rx0) * n
            cell = atlas[oz:oz + n, ox:ox + n]
            cell[..., :3] = land_rgb
            cell[..., 3] = np.where(sea, 0, 255)


def far_block(cls):
    """Region -> 256 x 256 RGBA per l'atlante lontano: tinta media della sola terra,
    alfa 255 dove la cella da 8 m e' terra per almeno meta' (l'acqua la disegna il piano)."""
    n = REGION // FAR_SCALE
    land = np.isin(cls, LAND).reshape(n, FAR_SCALE, n, FAR_SCALE)
    rgb = COLOR[cls, :3].astype(np.float32).reshape(n, FAR_SCALE, n, FAR_SCALE, 3)
    land_count = land.sum(axis=(1, 3))
    col = (rgb * land[..., None]).sum(axis=(1, 3)) / np.maximum(land_count, 1)[..., None]
    out = np.zeros((n, n, 4), np.uint8)
    out[..., :3] = np.clip(col, 0, 255).astype(np.uint8)
    out[..., 3] = np.where(land_count >= FAR_SCALE * FAR_SCALE // 2, 255, 0)
    return out


# ---- lavoro parallelo: ogni processo carica i dati una volta sola ----
_W = {}


def _init_worker(inputs, tiles_dir, classes_dir):
    layers, _ = load_layers(inputs)
    polys, lines = build_features(layers)
    coast = [np.asarray(c["pts"], np.float64) for c in layers.get("coast", []) if len(c["pts"]) >= 2]
    _W.update(polys=polys, lines=lines, coast=coast, tiles=tiles_dir, classes=classes_dir)


def _work(loc):
    t0 = time.time()
    cls, h = build_region(loc, _W["polys"], _W["lines"], _W["coast"])
    tag = f"{loc[0]}_{loc[1]}"
    h.tofile(os.path.join(_W["tiles"], f"height_{tag}.f32"))
    COLOR[cls].tofile(os.path.join(_W["tiles"], f"color_{tag}.rgba"))
    write_png(os.path.join(_W["classes"], f"classes_{tag}.png"), cls)
    counts = np.bincount(cls.ravel(), minlength=256)
    stats = {name: int(counts[i]) for i, name, *_ in CLASSES if counts[i]}
    return loc, stats, far_block(cls), float(h.min()), float(h.max()), time.time() - t0


def _project_root(path):
    """Cartella che contiene project.godot, risalendo da path."""
    d = os.path.abspath(path)
    while not os.path.exists(os.path.join(d, "project.godot")):
        parent = os.path.dirname(d)
        if parent == d:
            raise SystemExit("project.godot non trovato sopra " + path)
        d = parent
    return d


def regions_in_polygon(ring, rx0, rz0, rx1, rz1, step=64):
    """Region (x, z) che il poligono tocca: campionato ogni `step` m."""
    small = [np.asarray(ring, np.float64) / step]
    out = []
    n = REGION // step
    for z in range(rz0, rz1 + 1):
        for x in range(rx0, rx1 + 1):
            if fill_mask(small, x * n, z * n, n + 1, n + 1).any():
                out.append((x, z))
    return out


def write_far(atlas, ring, generated, far_dir, build_dir, rx0, rz0, rx1, rz1):
    """Atlante del terreno lontano (8 m/px, alfa = terra), il suo .import, i metadati e
    l'anteprima a 1/4. Le region non generate (fuori dal poligono) si riempiono."""
    fill_missing_far(atlas, generated, ring, rx0, rz0, rx1, rz1)
    write_png(os.path.join(far_dir, "far_atlas.png"), atlas)
    # Import di Godot: compressa in VRAM (6144 x 6144 RGBA8 sarebbero 150 MB) e con le mipmap,
    # perche' lo si guarda solo da lontano. Il .import si scrive una volta: poi e' di Godot.
    import_path = os.path.join(far_dir, "far_atlas.png.import")
    if not os.path.exists(import_path):
        with open(import_path, "w", encoding="utf-8") as f:
            f.write('[remap]\n\nimporter="texture"\ntype="CompressedTexture2D"\n\n[deps]\n\n'
                    'source_file="res://' + os.path.relpath(os.path.join(far_dir, "far_atlas.png"),
                                                         _project_root(far_dir)).replace("\\", "/") + '"\n\n'
                    '[params]\n\ncompress/mode=2\ncompress/high_quality=false\nmipmaps/generate=true\n'
                    'mipmaps/limit=-1\ndetect_3d/compress_to=0\n')
    prev = atlas[::4, ::4].astype(np.float32)
    water = np.array([47, 95, 115], np.float32)
    a = prev[..., 3:4] / 255.0
    write_png(os.path.join(build_dir, "preview.png"), (prev[..., :3] * a + water * (1 - a)).astype(np.uint8))
    far_meta = {
        "region_size": REGION, "far_scale": FAR_SCALE,
        "regions_min": [rx0, rz0], "regions_max": [rx1, rz1],
        # rettangolo mondo coperto dall'atlante: [x0, z0, x1, z1]
        "world_rect": [rx0 * REGION, rz0 * REGION, (rx1 + 1) * REGION, (rz1 + 1) * REGION],
    }
    with open(os.path.join(far_dir, "far_atlas.json"), "w", encoding="utf-8") as f:
        json.dump(far_meta, f, indent=1)
    return far_meta


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--map", help="map.json: legge <mappa>/osm/layers, scrive tile, classi e atlante lontano")
    ap.add_argument("--input", nargs="+", help="JSON o cartelle di livelli di osm_extract.py (senza --map)")
    ap.add_argument("--regions", help="x0,z0,x1,z1 indici di region compresi (default: quelle toccate dal poligono)")
    ap.add_argument("--only", help="x,z;x,z;... solo queste region (collaudo)")
    ap.add_argument("--out", help="cartella dei tile (senza --map)")
    ap.add_argument("--jobs", type=int, default=max(1, (os.cpu_count() or 2) - 2))
    ap.add_argument("--far-only", action="store_true",
                    help="rifa solo l'atlante lontano dalle classi gia' generate (<mappa>/classes)")
    args = ap.parse_args()

    t_start = time.time()
    if args.map:
        map_dir = os.path.dirname(os.path.abspath(args.map))
        with open(args.map, "r", encoding="utf-8") as f:
            cfg = json.load(f)
        dirs = cfg["dirs"]
        inputs = [os.path.join(map_dir, dirs["osm"], "layers")]
        build_dir = os.path.join(map_dir, dirs["build"])
        tiles_dir = os.path.join(build_dir, "tiles")
        classes_dir = os.path.join(map_dir, dirs["classes"])
        far_dir = os.path.join(map_dir, "far")
    else:
        if not args.input or not args.out:
            ap.error("serve --map, oppure --input e --out")
        inputs, tiles_dir = args.input, args.out
        build_dir = tiles_dir
        classes_dir = far_dir = tiles_dir
    for d in (build_dir, tiles_dir, classes_dir, far_dir):
        os.makedirs(d, exist_ok=True)
    # l'editor non deve scandire ne' importare i tile grezzi e le classi (letti con FileAccess)
    for d in (build_dir, classes_dir):
        open(os.path.join(d, ".gdignore"), "w").close()

    metas = []
    for p in inputs:
        path = os.path.join(p, "meta.json") if os.path.isdir(p) else p
        with open(path, "r", encoding="utf-8") as f:
            doc = json.load(f)
        metas.append(doc if os.path.isdir(p) else doc.get("meta", {}))
    ring = next((m["bbox_ring"] for m in metas if "bbox_ring" in m), None)
    if ring is None:
        raise SystemExit("Nessun bbox_ring nei metadati dei livelli: rilancia osm_extract.py")
    allp = np.asarray(ring, np.float64)
    if args.regions:
        rx0, rz0, rx1, rz1 = (int(v) for v in args.regions.split(","))
    else:
        rx0, rz0 = (int(math.floor(v / REGION)) for v in allp.min(0))
        rx1, rz1 = (int(math.floor(v / REGION)) for v in allp.max(0))
    locs = regions_in_polygon(ring, rx0, rz0, rx1, rz1)
    if args.only:
        locs = [tuple(int(v) for v in p.split(",")) for p in args.only.split(";")]
    print(f"{len(locs)} region da generare (griglia {rx0}..{rx1} x {rz0}..{rz1}), {args.jobs} processi", flush=True)

    from concurrent.futures import ProcessPoolExecutor
    n = REGION // FAR_SCALE
    atlas = np.zeros(((rz1 - rz0 + 1) * n, (rx1 - rx0 + 1) * n, 4), np.uint8)
    if args.far_only:
        generated = set()
        for loc in locs:
            path = os.path.join(classes_dir, f"classes_{loc[0]}_{loc[1]}.png")
            if os.path.exists(path):
                oz, ox = (loc[1] - rz0) * n, (loc[0] - rx0) * n
                atlas[oz:oz + n, ox:ox + n] = far_block(read_png_gray(path))
                generated.add(loc)
        write_far(atlas, ring, generated, far_dir, build_dir, rx0, rz0, rx1, rz1)
        print(f"Atlante lontano rifatto da {len(generated)} region in {time.time() - t_start:.0f} s")
        return
    stats = {}
    done = 0
    with ProcessPoolExecutor(max_workers=args.jobs, initializer=_init_worker,
                             initargs=(inputs, tiles_dir, classes_dir)) as pool:
        for loc, st, far, hmin, hmax, dt in pool.map(_work, locs):
            done += 1
            stats[f"{loc[0]}_{loc[1]}"] = st
            if rx0 <= loc[0] <= rx1 and rz0 <= loc[1] <= rz1:
                oz, ox = (loc[1] - rz0) * n, (loc[0] - rx0) * n
                atlas[oz:oz + n, ox:ox + n] = far
            top = sorted(st.items(), key=lambda kv: -kv[1])[:3]
            print(f"  [{done}/{len(locs)}] region {loc}: {dt:4.1f} s  quote {hmin:6.2f}..{hmax:5.2f}  "
                  + ", ".join(f"{k} {100 * v / REGION ** 2:.0f}%" for k, v in top), flush=True)

    # Con --only l'atlante resterebbe vuoto fuori da quelle poche region: non lo si tocca.
    far_meta = None if args.only else write_far(atlas, ring, set(locs), far_dir, build_dir, rx0, rz0, rx1, rz1)
    meta = {
        "region_size": REGION, "vertex_spacing": 1.0, "sea_level": 0.0,
        "regions": [list(l) for l in locs], "inputs": [os.path.abspath(p) for p in inputs],
        "classes": [{"id": i, "name": nm, "height": hb, "color": list(rgb)} for i, nm, hb, _, rgb in CLASSES],
        "land_classes": LAND, "water_classes": WATER, "stats": stats, "far": far_meta,
        "files": {"height": "tiles/height_<x>_<z>.f32 float32", "color": "tiles/color_<x>_<z>.rgba RGBA8",
                  "classes": "classes/classes_<x>_<z>.png (grigio = id classe)"},
    }
    with open(os.path.join(tiles_dir, "meta.json"), "w", encoding="utf-8") as f:
        json.dump(meta, f, ensure_ascii=False, indent=1)
    print(f"Fatto: {len(locs)} region in {time.time() - t_start:.0f} s -> {os.path.abspath(tiles_dir)}")


if __name__ == "__main__":
    sys.exit(main())
