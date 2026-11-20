"""Vaporetto da 24 m (classe "Serie 90") ricavato da un modello scaricato.

SORGENTE (non e' nel repository, si riscarica e si passa il percorso):
  "2010 Vaporetto" di r_ka, https://sketchfab.com/3d-models/2010-vaporetto-71b8ec3054e14f62887d6da7ac1f5789
  licenza CC-BY-4.0 (attribuzione obbligatoria: assets/models/LICENSES.md quando entra il .glb).

Uso (Blender aperto con l'estensione MCP, skill blender-pipeline; lo script RESETTA la scena):

    python tools/blender/blender_client.py tools/blender/build_vaporetto.py "<...>/2010_vaporetto/scene.gltf"

Produce in assets/models/boats/ (aggiungere `noexport` agli argomenti per la sola revisione):
  Vaporetto.glb, Vaporetto_Worn.glb          resa + HydroHull, HullCollision, DeckCollision,
                                             SwimCollision, attacchi, cancelli Gate_* (+ _Open)
  Console_Vaporetto.glb, Console_Vaporetto_Worn.glb   consolle con Wheel / Hand_L / Hand_R
Le due varianti hanno gli stessi nomi di materiale e immagini diverse. In Blender resta la revisione:
copia rovinata a +7 m, piano dell'acqua alla DWL, cilindro da 1,80 m, anteprima dei cancelli (1-120).
Dopo l'export: check_boat_model.py (pipeline §6.2) e import in Godot con
`gltf/embedded_image_handling=3` nei .glb.import (texture incorporate non compresse).

COSA FA AL MODELLO SORGENTE (pipeline §3):
  - scala e assi: lunghezza fuori tutto 23,93 m e larghezza di scafo 4,22 m (scheda dati, Wikipedia
    "Vaporetto" Serie 90, conf. B); prua +Y, origine a mezza lunghezza sul piano di simmetria,
    linea d'acqua di progetto (DWL) a z = 0. La larghezza del sorgente (5,95 m sul parabordo) e'
    sproporzionata: X e Y hanno fattori diversi. Le ALTEZZE NON sono scalate: con la scala della
    lunghezza le porte scendevano a 1,85 m e la cabina a 2,1 m (abitabilita' §4.1: 2,05 m netti);
  - toglie scafo (forma a V profonda: ~50 t al galleggiamento contro le 25 t della scheda) e i decal
    con logo e targhe dell'esercente (niente marchi nel gioco, §3.1);
  - scafo nuovo per sezioni (stesse funzioni che serviranno alla HydroHull, §4.1-§4.2): pianta e
    cavallino ricavati dal parabordo del sorgente, murate verticali dalla DWL al ponte e V tonda sotto,
    immersione del corpo (~0,66 m) calcolata perche' la carena alla DWL valga le 25 t della scheda
    (lo skeg, che e' un Foil, escluso); dritto di poppa (skeg) fino a
    1,44 m (immersione di registro), elica a 4 pale, asse, timone appeso al tallone;
  - ponti (il sorgente non ne ha: dentro si vedeva il fondo dello scafo), boccaporto del motore sul
    ponte d'imbarco, panche nella cabina di poppa;
  - texture PSX (64 px/m, 5 bit con dithering, filtro nearest), materiali per nome (livrea in grigi).
"""

import math

import bpy
import bmesh
import numpy as np
from mathutils import Vector

SRC = ARGV[0].replace("\\", "/")
SRC_DIR = SRC.rsplit("/", 1)[0]

# =====================================================================================
# Scheda dati (pipeline §2) e trasformazione del sorgente
# =====================================================================================

LOA = 23.93            # m, fuori tutto (B, Wikipedia "Vaporetto", Serie 90)
BEAM = 4.22            # m, larghezza di scafo (B, idem; ACTV 91 su naviecapitani: 4,23)
DRAFT = 1.44           # m, immersione massima, al tallone del dritto (B, ACTV 94 su naviecapitani)
DISPLACEMENT = 25000.0  # kg (B, Wikipedia e ACTV 91)
RHO = 1025.0
DECK = 0.50            # m, ponte a mezza nave sopra la DWL (D: foto agli imbarcaderi)

SRC_X0, SRC_X1 = -14.952, 12.172          # poppa e prua del sorgente (parabordo compreso)
SRC_FENDER = 0.15                          # sporgenza del parabordo nel sorgente
SRC_HALF_MAX = 2.977                       # mezza larghezza massima sul parabordo
SRC_DECK_Z = 0.55                          # base delle pareti = ponte nel sorgente
K = LOA / (SRC_X1 - SRC_X0)                # lunghezza
KB = (BEAM / 2) / (SRC_HALF_MAX - SRC_FENDER)  # larghezza
X_MID = (SRC_X0 + SRC_X1) / 2
DZ = DECK - SRC_DECK_Z                     # altezze: solo traslate
FENDER_P = SRC_FENDER * KB                 # sporgenza del parabordo nel gioco
FENDER_H = 0.085

# Pianta (mezza larghezza esterna del parabordo) e cavallino (sommita' del parabordo) del sorgente,
# misurati sulle facce del parabordo (coordinate del sorgente: x prua, z alto).
PLAN = [(-14.952, 0.73), (-14.43, 1.19), (-13.86, 1.71), (-13.21, 2.07), (-12.55, 2.24),
        (-11.78, 2.32), (-10.01, 2.48), (-8.24, 2.61), (-6.47, 2.72), (-4.70, 2.81),
        (-2.93, 2.89), (-1.16, 2.95), (0.11, 2.977), (2.80, 2.83), (5.19, 2.65), (6.82, 2.48),
        (8.42, 2.32), (9.65, 2.04), (10.68, 1.47), (11.60, 0.66), (12.172, 0.0)]
SHEER = [(-14.952, 0.547), (5.19, 0.547), (6.82, 0.60), (8.42, 0.659), (9.67, 0.711),
         (10.72, 0.756), (11.69, 0.797), (12.172, 0.817)]

Y_S, Y_B = -LOA / 2, LOA / 2
Y_HS = Y_S + SRC_FENDER * KB               # specchio: la lunghezza fuori tutto comprende il parabordo


def to_game(p):
    """Sorgente (prua +X, Y-up gia' convertito dall'import) -> barca (prua +Y, DWL a z = 0)."""
    return Vector((-p.y * KB, (p.x - X_MID) * K, p.z + DZ))


def interp(table, x):
    if x <= table[0][0]:
        return table[0][1]
    for (xa, ya), (xb, yb) in zip(table, table[1:]):
        if x <= xb:
            return ya + (yb - ya) * (x - xa) / (xb - xa)
    return table[-1][1]


def src_x(y):
    return y / K + X_MID


def fender_half(y):
    return interp(PLAN, src_x(y)) * KB


def fender_p(y):
    return min(FENDER_P, fender_half(y))


def half_beam(y):
    """Mezza larghezza dello scafo al ponte (fuori fasciame, senza parabordo)."""
    return max(fender_half(y) - FENDER_P, 0.0)


def sheer(y):
    """Quota del ponte al bordo (= sommita' del parabordo)."""
    return interp(SHEER, src_x(y)) + DZ


# =====================================================================================
# Forma della carena
# =====================================================================================

Y_AFT = -4.0           # inizio dello slancio di poppa
Y_FWD = 3.0            # inizio del piede di prua
Z_COUNTER = 0.30       # quota della volta di poppa all'estremita' (sopra l'acqua)
# Pienezza delle sezioni sotto la murata (esponente della superellisse: 1 = V dritta, 2 = ellisse,
# >2 = ginocchio squadrato): V appena tonda. L'immersione la calcola solve_body_draft().
SHAPE = {"full_mid": 1.3}
FULL_END_RATIO = 0.75
SKEG_START = -2.0      # il dritto di poppa nasce qui dal corpo e scende fino a -DRAFT
SKEG_FULL = -5.5
N_ST = 40              # stazioni della carena di resa
N_HALF = 9             # punti per mezza sezione

# Propulsione: elica sotto la volta, dritto di poppa davanti, timone appeso al tallone.
Y_STERNPOST = -9.15
PROP_R = 0.47          # D 0,94 m (D: 147 kW a ~700 giri all'elica)
PROP_Y, PROP_Z = -9.50, -0.82
SHAFT_DEG = 2.0
RUDDER_Y = -10.00      # asse della pala
RUDDER_CHORD = 0.62
RUDDER_Z0, RUDDER_Z1 = -1.34, -0.18
SHOE_Z0, SHOE_Z1 = -DRAFT, -DRAFT + 0.08
SKEG_HALF = 0.08


def keel_z(y, tb):
    if y < Y_AFT:
        u = (Y_AFT - y) / (Y_AFT - Y_S)
        return -tb + (tb + Z_COUNTER) * u ** 1.5
    if y > Y_FWD:
        u = (y - Y_FWD) / (Y_B - Y_FWD)
        return -tb + (sheer(Y_B) - 0.02 + tb) * u ** 3.0
    return -tb


def half_section(y, tb, n=N_HALF):
    """Mezza sezione (x, z) dalla chiglia alla falchetta, lato dritta: murata VERTICALE dalla linea
    d'acqua (zt) al ponte, sotto una superellisse (V tonda). Con la V fino al ponte la murata
    sporgeva sopra l'acqua: chi nuota la trovava come un soffitto e non poteva issarsi al varco."""
    hb, zk, zs = half_beam(y), keel_z(y, tb), sheer(y)
    # Inizio della murata: la DWL a mezza nave; alle estremita' (chiglia che sale fin sopra l'acqua)
    # una frazione dell'altezza, mai sotto la chiglia: continuo lungo tutto lo scafo.
    zt = min(zk + 0.75 * (zs - zk), max(0.0, zk + 0.3 * (zs - zk)))
    t = (y - Y_S) / LOA
    fm = SHAPE["full_mid"]
    full = max(1.0, fm * FULL_END_RATIO + fm * (1.0 - FULL_END_RATIO) * (1.0 - abs(2 * t - 1) ** 2))
    pts = []
    for i in range(n - 1):
        th = i / (n - 2) * math.pi / 2
        pts.append((hb * math.sin(th) ** (2.0 / full), zt - (zt - zk) * math.cos(th) ** (2.0 / full)))
    pts.append((hb, zs))
    return pts


def ring(y, tb, n=N_HALF):
    half = half_section(y, tb, n)
    return [(-x, y, z) for x, z in reversed(half[1:])] + [(x, y, z) for x, z in half]


def station_ys(n):
    return [Y_HS + (Y_B - Y_HS) * (0.5 - 0.5 * math.cos(math.pi * i / (n - 1))) for i in range(n)]


def clipped_area(half):
    """Area della mezza sezione sotto z = 0 (poligono chiuso sull'asse)."""
    poly = list(half) + [(0.0, half[-1][1])]
    out = []
    for i in range(len(poly)):
        a, b = poly[i - 1], poly[i]
        ina, inb = a[1] <= 0, b[1] <= 0
        if inb:
            if not ina:
                f = a[1] / (a[1] - b[1])
                out.append((a[0] + (b[0] - a[0]) * f, 0.0))
            out.append(b)
        elif ina:
            f = a[1] / (a[1] - b[1])
            out.append((a[0] + (b[0] - a[0]) * f, 0.0))
    s = 0.0
    for i in range(len(out)):
        (x0, z0), (x1, z1) = out[i - 1], out[i]
        s += x0 * z1 - x1 * z0
    return abs(s) / 2


def skeg_bottom(y, tb):
    """Fondo del dritto di poppa: scende dal corpo fino all'immersione di registro."""
    if y >= SKEG_START:
        return None
    if y >= SKEG_FULL:
        return -tb + (SKEG_START - y) / (SKEG_START - SKEG_FULL) * (tb - DRAFT)
    return -DRAFT


def displaced_volume(tb, n=400):
    """Carena del CORPO alla DWL. Lo skeg resta fuori dalla HydroHull (e' un Foil): ~0,6 m³,
    errore d'immersione ~1 cm su 1,44 m (pipeline §4.2, sotto l'1%)."""
    vol = 0.0
    prev = None
    for i in range(n + 1):
        y = Y_HS + (Y_B - Y_HS) * i / n
        a = 2 * clipped_area(half_section(y, tb, 24))
        if prev is not None:
            vol += (a + prev) / 2 * (Y_B - Y_HS) / n
        prev = a
    return vol


def solve_body_draft():
    """Immersione del corpo a mezza nave che da' alla carena alla DWL il dislocamento della scheda
    (pienezza fissa: con le murate verticali dalla DWL anche la V piu' stretta, a 1 m, faceva 39 t)."""
    target = DISPLACEMENT / RHO
    lo, hi = 0.3, 1.4
    for _ in range(40):
        mid = (lo + hi) / 2
        if displaced_volume(mid) < target:
            lo = mid
        else:
            hi = mid
    return (lo + hi) / 2


# =====================================================================================
# Texture PSX (come build_venice_kit.py: 64 px/m, 5 bit per canale, dithering 4 x 4)
# =====================================================================================

BAYER = (np.array([[0, 8, 2, 10], [12, 4, 14, 6], [3, 11, 1, 9], [15, 7, 13, 5]]) + 0.5) / 16 - 0.5


def psx(img, levels=32):
    h, w = img.shape[:2]
    t = np.tile(BAYER, (h // 4 + 1, w // 4 + 1))[:h, :w, None]
    return np.clip(np.floor(img * (levels - 1) + 0.5 + t * 0.9) / (levels - 1), 0, 1)


def vnoise(w, h, cx, cy, seed):
    rng = np.random.default_rng(seed)
    g = rng.random((cy, cx))
    xs, ys = np.arange(w) * cx / w, np.arange(h) * cy / h
    x0, y0 = np.floor(xs).astype(int), np.floor(ys).astype(int)
    fx, fy = xs - x0, ys - y0
    fx, fy = fx * fx * (3 - 2 * fx), fy * fy * (3 - 2 * fy)
    x1, y1 = (x0 + 1) % cx, (y0 + 1) % cy
    a, b = g[np.ix_(y0, x0)], g[np.ix_(y0, x1)]
    c, d = g[np.ix_(y1, x0)], g[np.ix_(y1, x1)]
    top = a + (b - a) * fx[None, :]
    bot = c + (d - c) * fx[None, :]
    return top + (bot - top) * fy[:, None]


def fbm(w, h, cx, cy, seed, octaves=3):
    total, amp, norm = np.zeros((h, w)), 1.0, 0.0
    for o in range(octaves):
        total += amp * vnoise(w, h, min(w, cx * 2 ** o), min(h, cy * 2 ** o), seed + o * 17)
        norm += amp
        amp *= 0.5
    return total / norm


def grain(w, h, seed, amount):
    return 1.0 - amount / 2 + amount * np.random.default_rng(seed).random((h, w))


def grey(v, w, h):
    return np.repeat(np.asarray(v, dtype=np.float64)[..., None], 3, axis=2) * np.ones((h, w, 3))


def streaks(img, seed, count, rows, length=(8, 30), strength=0.25, tint=(1.0, 0.93, 0.85)):
    """Colature verso il basso (riga crescente = piu' in basso nell'immagine) da righe date."""
    rng = np.random.default_rng(seed)
    h, w = img.shape[:2]
    tint = np.array(tint)
    for _ in range(count):
        x = int(rng.integers(w))
        y0 = int(rng.choice(rows)) + int(rng.integers(0, 3))
        n = int(rng.integers(*length))
        for k in range(n):
            f = strength * (1.0 - k / n)
            img[(y0 + k) % h, x % w] *= (1.0 - f) * 1.0 + f * tint * 0.55
            if rng.random() < 0.15:
                x += int(rng.choice([-1, 1]))


def tex_hull():
    """Fasciame d'acciaio 2 x 2 m: lamiere 2 x 1 m sfalsate, saldature, ruggine (in grigio chiaro)."""
    w = h = 128
    v = 0.80 + 0.10 * (fbm(w, h, 4, 4, 11, 4) - 0.5)
    v *= grain(w, h, 12, 0.06)
    img = grey(v, w, h)
    for row in (0, 64):
        img[row] *= 0.62
        img[(row + 1) % h] *= 1.08
    img[0:64, 0] *= 0.65
    img[64:128, 64] *= 0.65
    streaks(img, 13, 70, [1, 65], (6, 40), 0.35)
    pits = np.random.default_rng(14).random((h, w)) > 0.985
    img[pits] *= 0.7
    return img


def tex_superstructure():
    """Pareti di lamiera verniciata 2 x 2 m: pannelli da 1 m, chiodatura, sporco che cola."""
    w = h = 128
    v = 0.90 + 0.06 * (fbm(w, h, 4, 4, 21, 4) - 0.5)
    v *= grain(w, h, 22, 0.04)
    img = grey(v, w, h)
    for col in (0, 64):
        img[:, col] *= 0.80
        img[:, (col + 1) % w] *= 1.04
    for x in range(2, w, 8):
        img[60, x] *= 0.7
    streaks(img, 23, 40, [0, 61], (10, 50), 0.18)
    return img


def tex_roof():
    w = h = 128
    v = 0.86 + 0.10 * (fbm(w, h, 3, 3, 31, 4) - 0.5)
    v *= grain(w, h, 32, 0.05)
    soot = np.clip((fbm(w, h, 6, 6, 33, 3) - 0.6) * 3, 0, 1)
    v *= 1 - 0.10 * soot
    img = grey(v, w, h)
    for k in (0, 64):
        img[k] *= 0.78
        img[:, k] *= 0.78
    return img


def tex_deck():
    """Ponte antisdrucciolo 1 x 1 m: graniglia, giunti, consumo."""
    w = h = 64
    v = 0.78 + 0.10 * (fbm(w, h, 4, 4, 41, 3) - 0.5)
    v *= grain(w, h, 42, 0.18)
    wear = np.clip((fbm(w, h, 3, 3, 43, 3) - 0.5) * 3, 0, 1)
    v *= 1 - 0.15 * wear
    img = grey(v, w, h)
    img[0] *= 0.65
    img[:, 0] *= 0.65
    return img


def tex_fender():
    """Parabordo 1 x 1 m: due scanalature, bulloni ogni 0,5 m, strisciate."""
    w = h = 64
    v = 0.82 + 0.08 * (fbm(w, h, 8, 2, 51, 3) - 0.5)
    v *= grain(w, h, 52, 0.08)
    img = grey(v, w, h)
    for row in (18, 19, 44, 45):
        img[row] *= 0.65
    for x in (0, 32):
        img[30:34, x:x + 3] *= 0.45
    rng = np.random.default_rng(53)
    for _ in range(25):
        x, y, n = int(rng.integers(w)), int(rng.integers(h)), int(rng.integers(4, 14))
        for k in range(n):
            img[y % h, (x + k) % w] *= 0.8
    return img


def tex_checker():
    """Lamiera mandorlata 1 x 1 m (boccaporti)."""
    w = h = 64
    v = 0.62 + 0.06 * (fbm(w, h, 4, 4, 61, 3) - 0.5)
    img = grey(v, w, h)
    yy, xx = np.mgrid[0:h, 0:w]
    for flip in (0, 1):
        cx = (xx + (yy // 8 % 2) * 4 * flip) % 8
        cy = yy % 8
        if flip == 0:
            m = (np.abs(cx - cy) <= 1) & (cx < 6) & (cy < 6) & (yy // 8 % 2 == 0)
        else:
            m = (np.abs(cx - (5 - cy)) <= 1) & (cx < 6) & (cy < 6) & (yy // 8 % 2 == 1)
        img[m] *= 1.35
    img[0] *= 0.6
    img[:, 0] *= 0.6
    return img


def tex_plain(seed, base, amount, size=32):
    v = base + amount * (fbm(size, size, 4, 4, seed, 3) - 0.5)
    v *= grain(size, size, seed + 1, amount)
    return grey(v, size, size)


def tex_glass():
    """Finestra (UV 0..1 del sorgente): telaio e traversa opachi, vetro trasparente a retino."""
    w = h = 64
    img = np.ones((h, w, 3)) * np.array([0.20, 0.25, 0.26])
    alpha = np.full((h, w), 0.45)
    yy, xx = np.mgrid[0:h, 0:w]
    refl = ((xx + yy) % 40 < 6) & (xx > 4) & (yy > 4)
    img[refl] *= 1.6
    frame = (xx < 4) | (xx >= w - 4) | (yy < 4) | (yy >= h - 4) | ((yy >= 36) & (yy < 38))
    img[frame] = np.array([0.80, 0.80, 0.78])
    img[frame] *= grain(w, h, 71, 0.1)[frame][:, None]
    alpha[frame] = 1.0
    t = np.tile(BAYER, (h // 4, w // 4))
    alpha = np.clip(np.floor(alpha * 7 + 0.5 + t * 0.9) / 7, 0, 1)
    return img, alpha


def load_source_image(name):
    img = bpy.data.images.load("{}/textures/{}".format(SRC_DIR, name), check_existing=True)
    w, h = img.size
    px = np.array(img.pixels[:], dtype=np.float64).reshape(h, w, 4)[::-1]
    return px


def downsample(px, factor):
    h, w, c = px.shape
    return px.reshape(h // factor, factor, w // factor, factor, c).mean(axis=(1, 3))


def tex_railing():
    px = downsample(load_source_image("Rilings_baseColor.png"), 2)
    rgb = px[..., :3]
    lum = rgb.mean(axis=2, keepdims=True)
    rgb = rgb * 0.5 + lum * 0.5
    return rgb, (px[..., 3] > 0.5).astype(np.float64)


def tex_fittings():
    px = downsample(load_source_image("Deck_stuff_baseColor.png"), 2)
    return px[..., :3]


def tex_cabin():
    """Vernice verde chiaro della timoneria (1 x 1 m): pannelli e leggero sporco."""
    w = h = 64
    v = 0.92 + 0.05 * (fbm(w, h, 4, 4, 101, 3) - 0.5)
    v *= grain(w, h, 102, 0.03)
    img = grey(v, w, h)
    img[:, 0] *= 0.86
    streaks(img, 103, 12, [0], (10, 40), 0.10)
    return img


def tex_consoletop():
    """Piano della consolle: vernice scura un po' consumata."""
    w = h = 64
    v = 0.85 + 0.12 * (fbm(w, h, 3, 3, 111, 4) - 0.5)
    v *= grain(w, h, 112, 0.06)
    return grey(v, w, h)


def tex_wood():
    """Mogano verniciato: venatura lungo U."""
    w = h = 64
    yy, xx = np.mgrid[0:h, 0:w]
    warp = vnoise(w, h, 4, 8, 121) * 6
    ring = 0.5 + 0.5 * np.sin((yy + warp) * 0.9)
    v = 0.78 + 0.16 * ring + 0.08 * (fbm(w, h, 16, 2, 122, 3) - 0.5)
    return grey(v, w, h)


# Atlante della strumentazione (128 x 128): righe/colonne dell'array (riga 0 = in alto).
PANEL_REGIONS = {
    "buttons": (0, 0, 64, 64), "screen": (0, 64, 64, 128), "radio": (64, 0, 96, 64),
    "gauges": (64, 64, 96, 128), "dark": (100, 4, 124, 28), "red": (100, 36, 124, 44),
    "black": (100, 52, 124, 60),
}


def panel_uv(region):
    r0, c0, r1, c1 = PANEL_REGIONS[region]
    u0, u1 = c0 / 128, c1 / 128
    v0, v1 = 1 - r1 / 128, 1 - r0 / 128
    return [(u0, v0), (u1, v0), (u1, v1), (u0, v1)]


def tex_panel():
    w = h = 128
    img = np.ones((h, w, 3)) * 0.16
    yy, xx = np.mgrid[0:h, 0:w]
    rng = np.random.default_rng(131)
    # Pulsantiera: file di tasti colorati e due manopole.
    img[0:64, 0:64] = (0.20, 0.22, 0.22)
    cols = [(0.75, 0.15, 0.10), (0.15, 0.60, 0.20), (0.85, 0.75, 0.20), (0.85, 0.85, 0.82), (0.10, 0.10, 0.10)]
    for r in range(6, 40, 8):
        for c in range(6, 58, 8):
            img[r:r + 5, c:c + 5] = cols[int(rng.integers(len(cols)))]
    for cx, cy in ((16, 50), (46, 50)):
        m = (xx - cx) ** 2 + (yy - cy) ** 2 < 36
        img[m] = (0.55, 0.55, 0.55)
        img[(np.abs(xx - cx) < 1) & (yy > cy - 6) & (yy < cy)] = (0.05, 0.05, 0.05)
    # Schermo: cornice nera, fosfori verdi, griglia e cerchi del radar.
    img[0:64, 64:128] = (0.05, 0.05, 0.05)
    scr = (yy >= 6) & (yy < 58) & (xx >= 70) & (xx < 122)
    img[scr] = (0.04, 0.16, 0.08)
    d = np.sqrt((xx - 96.0) ** 2 + (yy - 32.0) ** 2)
    rings = scr & ((np.abs(d - 10) < 0.6) | (np.abs(d - 20) < 0.6))
    img[rings] = (0.2, 0.7, 0.3)
    img[scr & ((xx == 96) | (yy == 32))] = (0.12, 0.45, 0.2)
    for _ in range(14):
        img[int(rng.integers(8, 56)), int(rng.integers(72, 120))] = (0.6, 1.0, 0.6)
    # Radio: display arancio e manopole.
    img[64:96, 0:64] = (0.08, 0.08, 0.09)
    img[70:80, 6:40] = (0.80, 0.45, 0.10)
    for c in range(8, 38, 6):
        img[72:78, c:c + 3] = (0.25, 0.12, 0.02)
    for cx in (48, 58):
        img[(xx - cx) ** 2 + (yy - 80) ** 2 < 16] = (0.45, 0.45, 0.45)
    img[86:92, 6:58:4] = (0.30, 0.30, 0.30)
    # Due strumenti tondi.
    img[64:96, 64:128] = (0.12, 0.14, 0.13)
    for cx in (80, 112):
        m = (xx - cx) ** 2 + (yy - 80) ** 2 < 144
        img[m] = (0.90, 0.90, 0.85)
        img[m & ((xx - cx) ** 2 + (yy - 80) ** 2 > 110)] = (0.1, 0.1, 0.1)
        img[(yy == 80) & (xx > cx - 8) & (xx <= cx)] = (0.7, 0.1, 0.1)
    img[100:124, 4:28] = 0.18
    img[100:124, 36:44] = (0.80, 0.12, 0.10)
    img[100:124, 52:60] = 0.05
    return img


# Usura della variante "rovinata": non distrutta, solo vecchia e sporca (ruggine che cola dai giunti,
# chiazze, vernice scrostata). (ruggine, sporco, righe da cui colano le gocce)
WEAR = {
    "hull": (0.8, 0.6, [1, 65]), "superstructure": (0.55, 0.6, [0, 61]), "roof": (0.35, 0.8, [0, 64]),
    "deck": (0.35, 0.9, None), "fender": (0.15, 0.8, None), "checker": (0.8, 0.5, None),
    "steel": (0.7, 0.4, None), "soot": (0.3, 0.6, None), "bronze": (0.0, 0.7, None),
    "cushion": (0.0, 0.8, None), "cabin": (0.25, 0.7, [0]), "consoletop": (0.0, 0.8, None),
    "wood": (0.0, 0.7, None), "panel": (0.0, 0.5, None), "railing": (0.7, 0.4, None),
    "fittings": (0.35, 0.6, None), "glass": (0.0, 0.0, None),
}
RUST = np.array([0.52, 0.27, 0.12])


def weather(img, seed, rust, dirt, rows):
    img = np.array(img, dtype=np.float64)
    h, w = img.shape[:2]
    lum = img.mean(axis=2, keepdims=True)
    img = (img * 0.8 + lum * 0.2) * 0.9
    cx, cy = max(2, w // 16), max(2, h // 16)
    blot = np.clip((fbm(w, h, cx, cy, seed, 4) - 0.45) * 3, 0, 1)
    img *= 1 - 0.30 * dirt * blot[..., None]
    img *= (1 - 0.18 * dirt * (np.random.default_rng(seed + 1).random((h, w)) > 0.8))[..., None]
    if rust > 0:
        # Poche chiazze grandi (alone bruno + cuore di ruggine), non puntini dappertutto.
        f = fbm(w, h, max(2, cx // 2), max(2, cy // 2), seed + 2, 5)
        edge = np.clip((f - (0.66 - 0.05 * rust)) * 5, 0, 1)
        core = np.clip((f - (0.69 - 0.05 * rust)) * 8, 0, 1)
        img *= (1 - 0.25 * edge * rust)[..., None]
        tone = (0.7 + 0.4 * vnoise(w, h, max(2, w // 4), max(2, h // 4), seed + 4))[..., None]
        k = (core * rust)[..., None]
        img = img * (1 - k) + RUST[None, None] * tone * k
        chips = np.random.default_rng(seed + 5).random((h, w)) > 1 - 0.002 * rust
        img[chips] = RUST * 0.6
        if rows:
            rng = np.random.default_rng(seed + 6)
            for _ in range(int(w * 0.5 * rust)):
                x = int(rng.integers(w))
                y0 = int(rng.choice(rows)) + int(rng.integers(0, 3))
                n = int(rng.integers(6, max(8, h // 2)))
                for k in range(n):
                    f = 0.6 * rust * (1 - k / n)
                    img[(y0 + k) % h, x % w] = img[(y0 + k) % h, x % w] * (1 - f) + RUST * f
                    if rng.random() < 0.12:
                        x += int(rng.choice([-1, 1]))
    return np.clip(img, 0, 1)


def weathered_glass(img, alpha):
    """Vetro sporco: piu' opaco, velato di grigio-bruno, aloni."""
    h, w = img.shape[:2]
    film = np.clip((fbm(w, h, 4, 4, 141, 3) - 0.3) * 1.5, 0, 1)
    glass = alpha < 0.99
    img = img.copy()
    img[glass] = img[glass] * 0.6 + np.array([0.42, 0.40, 0.34]) * 0.4
    a = alpha.copy()
    a[glass] = np.clip(0.55 + 0.3 * film[glass], 0, 1)
    t = np.tile(BAYER, (h // 4, w // 4))
    a = np.clip(np.floor(a * 7 + 0.5 + t * 0.9) / 7, 0, 1)
    frame = ~glass
    img[frame] = weather(img, 142, 0.5, 0.6, None)[frame]
    return img, a


TEXTURES = {
    "hull": tex_hull, "fender": tex_fender, "superstructure": tex_superstructure, "deck": tex_deck,
    "cushion": lambda: tex_plain(81, 0.85, 0.10, 64), "roof": tex_roof, "checker": tex_checker,
    "steel": lambda: tex_plain(85, 0.85, 0.10), "soot": lambda: tex_plain(91, 0.80, 0.25),
    "bronze": lambda: tex_plain(95, 0.85, 0.15), "glass": tex_glass, "railing": tex_railing,
    "fittings": tex_fittings, "cabin": tex_cabin, "consoletop": tex_consoletop, "wood": tex_wood,
    "panel": tex_panel,
}


def texture(name, worn):
    data = TEXTURES[name]()
    img, alpha = data if isinstance(data, tuple) else (data, None)
    if worn:
        if name == "glass":
            img, alpha = weathered_glass(img, alpha)
        else:
            rust, dirt, rows = WEAR[name]
            img = weather(img, 500 + sum(map(ord, name)), rust, dirt, rows)
    return img, alpha


def make_image(name, img, alpha=None):
    img = psx(np.clip(img, 0, 1))
    h, w = img.shape[:2]
    rgba = np.ones((h, w, 4))
    rgba[..., :3] = img
    if alpha is not None:
        rgba[..., 3] = alpha
    old = bpy.data.images.get(name)
    if old is not None:
        bpy.data.images.remove(old)
    image = bpy.data.images.new(name, w, h, alpha=alpha is not None)
    image.pixels.foreach_set(rgba[::-1].astype(np.float32).ravel())
    image.pack()
    return image


# =====================================================================================
# Materiali per nome (pipeline §4.1). Livrea: Hull, Stripe, Deck, Interior, Cushion (texture in
# grigi chiari, il colore qui sotto e' quello di default e in Godot lo sostituisce BoatLivery).
# =====================================================================================

# nome: (texture, colore, metal, alfa, metri per ripetizione della texture)
MATERIALS = {
    "Hull": ("hull", (0.10, 0.13, 0.20), 0.0, None, 2.0),
    "Stripe": ("fender", (0.62, 0.22, 0.17), 0.0, None, 1.0),
    "Interior": ("superstructure", (0.93, 0.93, 0.91), 0.0, None, 2.0),
    "Deck": ("deck", (0.50, 0.52, 0.50), 0.0, None, 1.0),
    "Cushion": ("cushion", (0.30, 0.38, 0.52), 0.0, None, 1.0),
    "Roof": ("roof", (0.92, 0.92, 0.90), 0.0, None, 2.0),
    "Metal": ("steel", (0.78, 0.79, 0.80), 0.5, None, 1.0),
    "Checker": ("checker", (0.80, 0.80, 0.82), 0.5, None, 1.0),
    "MotorTrim": ("soot", (0.16, 0.16, 0.17), 0.0, None, 1.0),
    "Bronze": ("bronze", (0.78, 0.56, 0.32), 0.7, None, 1.0),
    "Glass": ("glass", (1.0, 1.0, 1.0), 0.0, "BLEND", None),
    "Railing": ("railing", (0.92, 0.92, 0.90), 0.0, "MASK", None),
    "Fittings": ("fittings", (1.0, 1.0, 1.0), 0.0, None, None),
    # Timoneria (foto di riferimento): verde chiaro, piano verde scuro, bordi di mogano. Colori
    # LINEARI (come tutti quelli qui): sRGB della foto ~ (170, 205, 165), (40, 85, 75), (150, 80, 45).
    "Cabin": ("cabin", (0.40, 0.61, 0.38), 0.0, None, 1.0),
    "ConsoleTop": ("consoletop", (0.025, 0.09, 0.07), 0.0, None, 1.0),
    "Wood": ("wood", (0.32, 0.09, 0.035), 0.0, None, 1.0),
    "Panel": ("panel", (1.0, 1.0, 1.0), 0.0, None, None),
}
# Suffisso dei materiali della variante rovinata. Solo in Blender, per vederle affiancate: il .glb
# rovinato avra' gli STESSI nomi (la livrea li riconosce per nome) e solo le immagini diverse.
WORN = "_Worn"


def build_materials(worn=False):
    suffix = WORN if worn else ""
    images = {}
    for name, (tex, color, metal, alpha, _tile) in MATERIALS.items():
        if tex not in images:
            img, a = texture(tex, worn)
            images[tex] = make_image("vap_" + tex + suffix, img, a)
        mat = bpy.data.materials.new(name + suffix)
        try:
            mat.use_nodes = True
        except AttributeError:
            pass
        # Una faccia sola (glTF doubleSided = false): dove serve il retro ci sono due facce.
        mat.use_backface_culling = True
        nt = mat.node_tree
        bsdf = nt.nodes.get("Principled BSDF")
        node = nt.nodes.new("ShaderNodeTexImage")
        node.image = images[tex]
        node.interpolation = "Closest"
        node.location = (-700, 300)
        mix = nt.nodes.new("ShaderNodeMix")
        mix.data_type = "RGBA"
        mix.blend_type = "MULTIPLY"
        mix.inputs["Factor"].default_value = 1.0
        mix.inputs[7].default_value = (*color, 1.0)
        mix.location = (-350, 300)
        nt.links.new(node.outputs["Color"], mix.inputs[6])
        nt.links.new(mix.outputs[2], bsdf.inputs["Base Color"])
        bsdf.inputs["Roughness"].default_value = 0.6 if metal > 0 else 1.0
        bsdf.inputs["Metallic"].default_value = metal
        if "Specular IOR Level" in bsdf.inputs:
            bsdf.inputs["Specular IOR Level"].default_value = 0.0
        if alpha == "MASK":
            rnd = nt.nodes.new("ShaderNodeMath")
            rnd.operation = "ROUND"
            rnd.location = (-350, 0)
            nt.links.new(node.outputs["Alpha"], rnd.inputs[0])
            nt.links.new(rnd.outputs[0], bsdf.inputs["Alpha"])
            mat.surface_render_method = "DITHERED"
        elif alpha == "BLEND":
            nt.links.new(node.outputs["Alpha"], bsdf.inputs["Alpha"])
            mat.surface_render_method = "BLENDED"


def tile_of(mat_name):
    return MATERIALS[mat_name.removesuffix(WORN)][4]


# =====================================================================================
# Geometria
# =====================================================================================

class Builder:
    """Vertici e facce con materiale e verso: `out` e' una direzione approssimativa verso l'esterno
    (la faccia viene girata se la sua normale le va contro). Senza `out` il verso e' quello dato."""

    def __init__(self):
        self.verts, self.faces, self.mats, self.uvs = [], [], [], []

    def poly(self, pts, mat, out=None, uv=None):
        pts = [Vector(p) for p in pts]
        if out is not None:
            n = Vector((0, 0, 0))
            for i in range(len(pts)):
                n += pts[i - 1].cross(pts[i])
            if n.dot(Vector(out)) < 0:
                pts = pts[::-1]
                uv = uv[::-1] if uv else uv
        base = len(self.verts)
        self.verts.extend(tuple(p) for p in pts)
        self.faces.append(list(range(base, base + len(pts))))
        self.mats.append(mat)
        self.uvs.append(uv)

    def torus(self, radius, r, mat, seg=16, sides=6):
        """Anello nel piano XY attorno a Z (corona di una ruota), senza fessure fra i segmenti."""
        rings = []
        for i in range(seg):
            a = i / seg * math.tau
            c, s = math.cos(a), math.sin(a)
            rings.append([((radius + r * math.cos(t)) * c, (radius + r * math.cos(t)) * s, r * math.sin(t))
                          for t in (j / sides * math.tau for j in range(sides))])
        for i in range(seg):
            ra, rb = rings[i], rings[(i + 1) % seg]
            a = i / seg * math.tau
            for j in range(sides):
                k = (j + 1) % sides
                q = [ra[j], ra[k], rb[k], rb[j]]
                mid = sum((Vector(p) for p in q), Vector()) / 4
                core = Vector((math.cos(a), math.sin(a), 0)) * radius
                self.poly(q, mat, out=mid - core)

    def tube(self, path, r, mat, seg=6, closed=False):
        pts = list(path) + ([path[0]] if closed else [])
        for a, b in zip(pts, pts[1:]):
            self.cylinder(a, b, r, mat, seg, caps=False)

    def extrude(self, plan, z0, z1, side, top=None, bottom=None):
        """Poligono in pianta [(x, y)] estruso fra z0 e z1 (anche concavo)."""
        area = sum(plan[i - 1][0] * plan[i][1] - plan[i][0] * plan[i - 1][1] for i in range(len(plan)))
        if area < 0:
            plan = plan[::-1]
        for (xa, ya), (xb, yb) in zip(plan, plan[1:] + plan[:1]):
            self.poly([(xa, ya, z0), (xb, yb, z0), (xb, yb, z1), (xa, ya, z1)], side, out=(yb - ya, xa - xb, 0))
        if top:
            self.poly([(x, y, z1) for x, y in plan], top, out=(0, 0, 1))
        if bottom:
            self.poly([(x, y, z0) for x, y in plan], bottom, out=(0, 0, -1))

    def atlas_box(self, x0, x1, y0, y1, z0, z1, mat, regions):
        """Scatola con UV su regioni dell'atlante: regions = {faccia: regione}, '*' = le altre.
        Le facce sono scritte gia' rivolte all'esterno, coi vertici nell'ordine della regione."""
        faces = {
            "x": [(x0, y1, z0), (x0, y0, z0), (x0, y0, z1), (x0, y1, z1)],
            "X": [(x1, y0, z0), (x1, y1, z0), (x1, y1, z1), (x1, y0, z1)],
            "y": [(x0, y0, z0), (x1, y0, z0), (x1, y0, z1), (x0, y0, z1)],
            "Y": [(x1, y1, z0), (x0, y1, z0), (x0, y1, z1), (x1, y1, z1)],
            "z": [(x0, y1, z0), (x1, y1, z0), (x1, y0, z0), (x0, y0, z0)],
            "Z": [(x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1)],
        }
        for key, pts in faces.items():
            self.poly(pts, mat, uv=panel_uv(regions.get(key, regions.get("*", "dark"))))

    def both(self, pts, mat):
        self.poly(pts, mat)
        self.poly(pts[::-1], mat)

    def box(self, x0, x1, y0, y1, z0, z1, mat, skip=""):
        c = ((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2)
        faces = {
            "x": [(x0, y0, z0), (x0, y1, z0), (x0, y1, z1), (x0, y0, z1)],
            "X": [(x1, y0, z0), (x1, y1, z0), (x1, y1, z1), (x1, y0, z1)],
            "y": [(x0, y0, z0), (x1, y0, z0), (x1, y0, z1), (x0, y0, z1)],
            "Y": [(x0, y1, z0), (x1, y1, z0), (x1, y1, z1), (x0, y1, z1)],
            "z": [(x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0)],
            "Z": [(x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1)],
        }
        for key, pts in faces.items():
            if key in skip:
                continue
            mid = Vector(pts[0]) + Vector(pts[2])
            self.poly(pts, mat, out=mid / 2 - Vector(c))

    def cylinder(self, a, b, r, mat, seg=8, caps=True):
        a, b = Vector(a), Vector(b)
        axis = (b - a).normalized()
        u = axis.orthogonal().normalized()
        w = axis.cross(u)
        ra = [a + (u * math.cos(t) + w * math.sin(t)) * r for t in (i / seg * math.tau for i in range(seg))]
        rb = [p + (b - a) for p in ra]
        for i in range(seg):
            j = (i + 1) % seg
            mid = (ra[i] + rb[j]) / 2
            self.poly([ra[i], ra[j], rb[j], rb[i]], mat, out=(mid - a) - axis * (mid - a).dot(axis))
        if caps:
            self.poly(ra, mat, out=-axis)
            self.poly(rb, mat, out=axis)

    def prism(self, profile, x0, x1, mat):
        """Profilo (y, z) chiuso estruso fra x0 e x1."""
        cy = sum(p[0] for p in profile) / len(profile)
        cz = sum(p[1] for p in profile) / len(profile)
        self.poly([(x0, y, z) for y, z in profile], mat, out=(-1, 0, 0))
        self.poly([(x1, y, z) for y, z in profile], mat, out=(1, 0, 0))
        for (ya, za), (yb, zb) in zip(profile, profile[1:] + profile[:1]):
            self.poly([(x0, ya, za), (x0, yb, zb), (x1, yb, zb), (x1, ya, za)], mat,
                      out=(0, (ya + yb) / 2 - cy, (za + zb) / 2 - cz))


def to_object(name, b, parent=None, location=(0, 0, 0)):
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(b.verts, [], b.faces)
    used = [m for m in MATERIALS if m in b.mats]
    for m in used:
        mesh.materials.append(bpy.data.materials[m])
    uvl = mesh.uv_layers.new(name="UVMap")
    for poly, m, uv in zip(mesh.polygons, b.mats, b.uvs):
        poly.material_index = used.index(m)
        poly.use_smooth = False
        if uv:
            for li, t in zip(poly.loop_indices, uv):
                uvl.data[li].uv = t
    bm = bmesh.new()
    bm.from_mesh(mesh)
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=0.0005)
    bmesh.ops.dissolve_degenerate(bm, edges=bm.edges, dist=0.0005)
    bm.to_mesh(mesh)
    bm.free()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    obj.location = location
    if parent is not None:
        obj.parent = parent
    return obj


def empty(name, location, parent=None, rotation=(0, 0, 0)):
    obj = bpy.data.objects.new(name, None)
    obj.empty_display_size = 0.15
    obj.location = location
    obj.rotation_euler = rotation
    bpy.context.scene.collection.objects.link(obj)
    if parent is not None:
        obj.parent = parent
    return obj


def box_uv(obj, keep_existing=()):
    """UV a proiezione cubica nello spazio dell'oggetto, `tile_of(materiale)` metri per ripetizione.
    I materiali in `keep_existing` tengono le UV del sorgente (finestre, ringhiere, accessori)."""
    me = obj.data
    if not me.uv_layers:
        me.uv_layers.new(name="UVMap")
    while len(me.uv_layers) > 1:
        me.uv_layers.remove(me.uv_layers[-1])
    uv = me.uv_layers[0].data
    for poly in me.polygons:
        mname = me.materials[poly.material_index].name
        if mname.removesuffix(WORN) in keep_existing or tile_of(mname) is None:
            continue
        t = tile_of(mname)
        n = poly.normal
        ax = max(range(3), key=lambda i: abs(n[i]))
        for li in poly.loop_indices:
            co = obj.matrix_world @ me.vertices[me.loops[li].vertex_index].co
            if ax == 2:
                u, v = co.x, co.y
            elif ax == 0:
                u, v = co.y, co.z
            else:
                u, v = co.x, co.z
            uv[li].uv = (u / t, v / t)


# =====================================================================================
# Sorgente: import, trasformazione, pulizia
# =====================================================================================

SOURCE_ROLES = {          # mesh del sorgente -> materiale (None = da togliere)
    "Vaporetto_Side_0": "Interior",
    "Vaporetto_Roof_0": "Roof",
    "Vaporetto_Window_0": "Glass",
    "Vaporetto_Rilings_0": "Railing",
    "Vaporetto_Deck_stuff_0": "Fittings",
    "Vaporetto_Standard_10_0": None,     # logo, targhe, cartello delle fermate: marchi
    "Vaporetto_Underwater _0": None,     # scafo: rifatto
}


def view3d_override(**extra):
    ctx = {}
    for window in bpy.context.window_manager.windows:
        for area in window.screen.areas:
            if area.type != "VIEW_3D":
                continue
            region = next((r for r in area.regions if r.type == "WINDOW"), None)
            if region is None:
                continue
            ctx = {"window": window, "screen": window.screen, "area": area, "region": region}
            break
        if ctx:
            break
    ctx.update(extra)
    return bpy.context.temp_override(**ctx)


def import_source(root):
    before = set(bpy.data.objects)
    with view3d_override():
        bpy.ops.import_scene.gltf(filepath=SRC)
    imported = [o for o in bpy.data.objects if o not in before]
    kept = {}
    for o in imported:
        if o.type != "MESH":
            continue
        role = SOURCE_ROLES.get(o.name)
        if role is None:
            continue
        me = o.data.copy()
        me.transform(o.matrix_world)
        bm = bmesh.new()
        bm.from_mesh(me)
        for v in bm.verts:
            v.co = to_game(v.co)
        bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=0.001)
        bmesh.ops.dissolve_degenerate(bm, edges=bm.edges, dist=0.0005)
        if role in ("Interior", "Railing"):
            # Le pareti partono dal ponte: dove il ponte sale (prua) la loro base finiva sotto e
            # si compenetrava col fasciame.
            for v in bm.verts:
                v.co.z = max(v.co.z, sheer(v.co.y))
        if role in ("Glass", "Railing"):
            # Una faccia sola: da dentro sparivano (Godot scarta il retro).
            dup = bmesh.ops.duplicate(bm, geom=bm.faces[:])
            bmesh.ops.reverse_faces(bm, faces=[f for f in dup["geom"] if isinstance(f, bmesh.types.BMFace)])
        bm.to_mesh(me)
        bm.free()
        me.materials.clear()
        me.materials.append(bpy.data.materials[role])
        for p in me.polygons:
            p.material_index = 0
            p.use_smooth = False
        name = {"Interior": "Superstructure", "Roof": "Roof", "Glass": "Windows",
                "Railing": "Railings", "Fittings": "Fittings"}[role]
        me.name = name
        obj = bpy.data.objects.new(name, me)
        bpy.context.scene.collection.objects.link(obj)
        obj.parent = root
        kept[role] = obj
    for o in imported:
        bpy.data.objects.remove(o, do_unlink=True)
    return kept


# =====================================================================================
# Scafo nuovo
# =====================================================================================

def deck_outline():
    """Bordo del ponte, giro chiuso: dritta da poppa a prua, sinistra da prua a poppa."""
    ys = station_ys(N_ST)
    right = [(half_beam(y), y, sheer(y), fender_p(y)) for y in ys]
    left = [(-x, y, z, p) for x, y, z, p in reversed(right[:-1])]
    return right + left


def build_hull(root, tb):
    b = Builder()
    ys = station_ys(N_ST)
    rings = [ring(y, tb) for y in ys]
    for ya, ra, yb, rb in zip(ys, rings, ys[1:], rings[1:]):
        zc = (keel_z(ya, tb) + sheer(ya)) / 2
        for i in range(len(ra) - 1):
            q = [ra[i], ra[i + 1], rb[i + 1], rb[i]]
            mx = sum(p[0] for p in q) / 4
            mz = sum(p[2] for p in q) / 4
            b.poly(q, "Hull", out=(mx, 0, mz - zc))
    b.poly(rings[0], "Hull", out=(0, -1, 0))                 # specchio (piccolo, sopra l'acqua)

    # Ponte: piatto fra le murate, sale col cavallino a prua.
    for ya, yb in zip(ys, ys[1:]):
        ha, hb_ = half_beam(ya), half_beam(yb)
        if ha < 0.01 and hb_ < 0.01:
            continue
        b.poly([(-ha, ya, sheer(ya)), (ha, ya, sheer(ya)), (hb_, yb, sheer(yb)), (-hb_, yb, sheer(yb))],
               "Deck", out=(0, 0, 1))

    # Parabordo tutto attorno (anche sullo specchio).
    loop = deck_outline()
    n = len(loop)
    prof = lambda p: [(0.0, -FENDER_H), (p, -FENDER_H + 0.022), (p, -0.010), (p * 0.3, 0.0), (0.0, 0.0)]
    norms = []
    for k in range(n):
        a, c = loop[k - 1], loop[(k + 1) % n]
        d = Vector((c[0] - a[0], c[1] - a[1], 0))
        nn = Vector((d.y, -d.x, 0))
        norms.append(nn.normalized() if nn.length > 1e-6 else Vector((0, 1, 0)))
    secs = []
    for (x, y, z, p), nn in zip(loop, norms):
        secs.append([Vector((x, y, z)) + nn * dx + Vector((0, 0, dz)) for dx, dz in prof(p)])
    for k in range(n):
        sa, sc = secs[k], secs[(k + 1) % n]
        for j in range(len(sa) - 1):
            q = [sa[j], sa[j + 1], sc[j + 1], sc[j]]
            hint = (norms[k] + norms[(k + 1) % n]) + Vector((0, 0, ((sa[j].z + sa[j + 1].z) / 2 - (loop[k][2] - 0.04)) * 20))
            b.poly(q, "Stripe", out=hint)

    # Dritto di poppa (skeg): pinna sull'asse fino al tallone; poi il tallone sotto l'elica.
    prof = []
    yy = [SKEG_START + 0.3 - i * (SKEG_START + 0.3 - Y_STERNPOST) / 24 for i in range(25)]
    prof += [(y, keel_z(y, tb) + 0.06) for y in yy]
    prof += [(Y_STERNPOST, -DRAFT), (SKEG_FULL, -DRAFT), (SKEG_START, -tb)]
    b.prism(prof, -SKEG_HALF, SKEG_HALF, "Hull")
    b.box(-SKEG_HALF * 0.8, SKEG_HALF * 0.8, RUDDER_Y - 0.10, Y_STERNPOST + 0.01, SHOE_Z0, SHOE_Z1, "Hull")
    hull = to_object("Hull", b, root)

    # Asse dell'elica dal dritto al mozzo.
    s = Builder()
    tilt = math.radians(SHAFT_DEG)
    hub_front = Vector((0, PROP_Y + 0.12, PROP_Z + 0.12 * math.tan(tilt)))
    back = Vector((0, Y_STERNPOST + 0.3, PROP_Z + (Y_STERNPOST + 0.3 - PROP_Y) * math.tan(tilt)))
    s.cylinder(back, hub_front, 0.05, "Metal", seg=8)
    to_object("Shaft", s, root)
    return hull


def build_rudder(root):
    """Pala appesa al tallone: origine sull'asse (25% della corda), ruota attorno a +Z."""
    b = Builder()
    c = RUDDER_CHORD
    zc = (RUDDER_Z0 + RUDDER_Z1) / 2
    z0, z1 = RUDDER_Z0 - zc, RUDDER_Z1 - zc
    t = 0.07
    sect = [(0.0, 0.25 * c), (t / 2, 0.10 * c), (t / 2, -0.25 * c), (0.0, -0.75 * c),
            (-t / 2, -0.25 * c), (-t / 2, 0.10 * c)]
    lo = [(x, y, z0) for x, y in sect]
    hi = [(x, y, z1) for x, y in sect]
    for i in range(len(sect)):
        j = (i + 1) % len(sect)
        mx, my = (sect[i][0] + sect[j][0]) / 2, (sect[i][1] + sect[j][1]) / 2
        b.poly([lo[i], lo[j], hi[j], hi[i]], "Hull", out=(mx, my + 0.1 * c, 0))
    b.poly(lo, "Hull", out=(0, 0, -1))
    b.poly(hi, "Hull", out=(0, 0, 1))
    b.cylinder((0, 0, z1), (0, 0, z1 + 0.45), 0.05, "Metal", seg=8)
    return to_object("Rudder", b, root, location=(0.0, RUDDER_Y, zc))


def build_propeller(root):
    """Elica destrorsa a 4 pale: origine nel mozzo, asse +Y (Blender) = spinta."""
    b = Builder()
    r = PROP_R
    b.cylinder((0, -0.14, 0), (0, 0.12, 0), r * 0.17, "Bronze", seg=8)
    b.cylinder((0, -0.14, 0), (0, -0.22, 0), r * 0.09, "Bronze", seg=8)
    for k in range(4):
        a0 = k / 4 * math.tau
        pts_lead, pts_trail = [], []
        for rr, chord, pitch in ((0.17, 0.30, 50.0), (0.45, 0.42, 38.0), (0.75, 0.40, 28.0), (0.97, 0.18, 22.0)):
            rad = rr * r
            half_ang = chord * r / rad / 2
            for sign, lst in ((1, pts_lead), (-1, pts_trail)):
                ang = a0 + sign * half_ang
                dy = sign * chord * r / 2 * math.sin(math.radians(pitch))
                lst.append((math.cos(ang) * rad, dy, math.sin(ang) * rad))
        outline = pts_lead + pts_trail[::-1]
        b.both(outline, "Bronze")
    return to_object("Propeller", b, root, location=(0.0, PROP_Y, PROP_Z))


def wall_inner(y):
    """Faccia interna delle pareti della cabina di poppa (misurata sul sorgente: 2,08 -> 2,73)."""
    ya, yb = (-11.99 - X_MID) * K, (-1.12 - X_MID) * K
    u = (y - ya) / (yb - ya)
    return (2.08 + (2.73 - 2.08) * u) * KB


def build_fitout(root):
    """Panche lungo le pareti della cabina di poppa e boccaporto del motore sul ponte d'imbarco."""
    b = Builder()
    deck = DECK
    y0, y1 = (-11.99 - X_MID) * K + 0.35, (-1.12 - X_MID) * K - 0.55
    seat, depth = 0.45, 0.42
    for side in (1, -1):
        wa, wb = wall_inner(y0) - 0.02, wall_inner(y1) - 0.02
        ia, ib = wa - depth, wb - depth
        s = side
        top = [(s * ia, y0, deck + seat), (s * wa, y0, deck + seat), (s * wb, y1, deck + seat), (s * ib, y1, deck + seat)]
        b.poly(top, "Cushion", out=(0, 0, 1))
        b.poly([(s * ia, y0, deck), (s * ib, y1, deck), (s * ib, y1, deck + seat), (s * ia, y0, deck + seat)],
               "Cushion", out=(-s, 0, 0))
        for y, w, i, o in ((y0, wa, ia, -1), (y1, wb, ib, 1)):
            b.poly([(s * i, y, deck), (s * w, y, deck), (s * w, y, deck + seat), (s * i, y, deck + seat)],
                   "Cushion", out=(0, o, 0))
        # Schienale contro la parete.
        bt = 0.07
        b.poly([(s * (wa - bt), y0, deck + seat), (s * (wb - bt), y1, deck + seat),
                (s * (wb - bt), y1, deck + seat + 0.42), (s * (wa - bt), y0, deck + seat + 0.42)],
               "Cushion", out=(-s, 0, 0))
        b.poly([(s * (wa - bt), y0, deck + seat + 0.42), (s * (wb - bt), y1, deck + seat + 0.42),
                (s * wb, y1, deck + seat + 0.42), (s * wa, y0, deck + seat + 0.42)], "Cushion", out=(0, 0, 1))
    # Boccaporto della sala macchine (ponte d'imbarco, fra la cabina e la timoneria).
    b.box(-0.75, 0.75, 1.25, 3.05, deck, deck + 0.05, "Checker", skip="z")
    # Pedana del pilota (vedi PILOT_DECK) col gradino a poppa.
    for x0, x1, y0, y1, h in PILOT_SOLIDS:
        b.box(x0, x1, y0, y1, deck - 0.02, sheer(HELM_Y) + h - 0.01, "Cabin", skip="z")
        b.box(x0, x1, y0, y1, sheer(HELM_Y) + h - 0.01, sheer(HELM_Y) + h, "Checker", skip="z")
    # Sgabello del pilota sulla pedana: seduta a 0,46 m (la posa seduta del gioco), centro 4 cm
    # dietro i piedi (Seat_Helm).
    sx, sy, fl = HELM_X, HELM_Y - 0.04, helm_feet_z()
    b.cylinder((sx, sy, fl), (sx, sy, fl + 0.02), 0.20, "Metal", seg=10)
    b.cylinder((sx, sy, fl + 0.02), (sx, sy, fl + 0.40), 0.035, "Metal", seg=6)
    b.cylinder((sx, sy, fl + 0.40), (sx, sy, fl + 0.46), 0.19, "Cushion", seg=10)
    return to_object("Fitout", b, root)


# =====================================================================================
# Timoneria (foto di riferimento: consolle verde col bordo di mogano, ruota a raggi di legno)
# =====================================================================================

# Interno della timoneria misurato sul sorgente gia' trasformato (piano a 0,7 m dal ponte).
WH_X = 1.14            # faccia interna delle pareti laterali
WH_Y0, WH_Y1 = 3.72, 6.57   # parete di poppa (porta al centro), parete di prua (centro)
HELM_X = -0.25         # ruota un po' a sinistra, come nella foto
HELM_Y = 5.50          # Seat_Helm: piedi del pilota
# Pedana del pilota. Da seduti gli occhi stanno 1,20 m sopra i piedi (FirstPersonCamera.SeatedEyeHeight,
# fissato dalla posa del gioco) e il davanzale del parabrezza 1,33 m sopra il ponte: senza pedana si
# guardava il muro sotto i vetri. Con 0,50 m gli occhi sono a 1,70 m (controllo di visuale in
# check_boat_model.py). Si sale da un gradino di 0,25 m (gradino massimo 0,35).
PILOT_DECK = 0.50
WHEEL_R = 0.22         # corona (raggio); le caviglie arrivano a 0,31
# Mozzo sopra i piedi del pilota: le mani (corona a ±60° dalla cima) cadono a ~0,86 m, come nella posa
# al volante del gioco (0,83-0,91), e la caviglia piu' alta resta 0,14 m sotto gli occhi (1,20): a
# 0,85 con la corona da 0,25 copriva il centro della visuale.
WHEEL_HUB_Z = 0.75
DESK_Z = 0.95          # piano della consolle (le finestre partono da 1,33)
# Pianta della consolle (assi barca): segue le pareti di prua, bordo verso il pilota a U.
DESK = [(-1.11, 5.78), (-0.58, 6.51), (0.58, 6.51), (1.11, 5.78), (1.11, 5.35), (0.88, 5.62),
        (0.60, 6.00), (-0.60, 6.00), (-0.88, 5.62), (-1.11, 5.35)]
WORN_OFFSET = 7.0      # la copia rovinata, in revisione, sta a dritta di tanto
# Pedana e gradino: (x0, x1, y0, y1, altezza sul ponte). La pedana non tocca la consolle (bordo a
# 5,86 m in y a x -0,70).
PILOT_SOLIDS = [(HELM_X - 0.45, HELM_X + 0.45, HELM_Y - 0.55, HELM_Y + 0.28, PILOT_DECK),
                (HELM_X - 0.45, HELM_X + 0.45, HELM_Y - 0.85, HELM_Y - 0.55, PILOT_DECK / 2)]


def helm_feet_z():
    return sheer(HELM_Y) + PILOT_DECK


def mount_console():
    return Vector((HELM_X, HELM_Y + 0.25, helm_feet_z()))


def paint_wheelhouse(objs):
    """Facce interne della timoneria (pareti e soffitto) -> materiale Cabin (verde della foto)."""
    roof = objs["Roof"]
    bm = bmesh.new()
    bm.from_mesh(roof.data)
    # Il tetto e' fatto di poche facce grandi: si taglia sul perimetro della timoneria.
    for co, no in (((WH_X + 0.03, 0, 0), (1, 0, 0)), ((-WH_X - 0.03, 0, 0), (1, 0, 0)),
                   ((0, WH_Y0 - 0.02, 0), (0, 1, 0)), ((0, WH_Y1 + 0.05, 0), (0, 1, 0))):
        g = bm.verts[:] + bm.edges[:] + bm.faces[:]
        bmesh.ops.bisect_plane(bm, geom=g, dist=1e-5, plane_co=co, plane_no=no)
    bm.to_mesh(roof.data)
    bm.free()
    centre = Vector((0.0, (WH_Y0 + WH_Y1) / 2, DECK + 1.2))
    count = 0
    for obj in (objs["Superstructure"], roof):
        me = obj.data
        cabin = bpy.data.materials["Cabin"]
        if cabin.name not in [m.name for m in me.materials]:
            me.materials.append(cabin)
        idx = [m.name for m in me.materials].index(cabin.name)
        for p in me.polygons:
            c = p.center
            if abs(c.x) < WH_X + 0.03 and WH_Y0 - 0.02 < c.y < WH_Y1 + 0.05 and DECK < c.z < 3.0:
                if p.normal.dot(centre - c) > 0.0:
                    p.material_index = idx
                    count += 1
    return count


def build_console():
    """Consolle della timoneria (Console_Vaporetto.glb): origine = Mount_Console, 0,25 m davanti ai
    piedi del pilota. Ruota a raggi verticale: il vuoto `Wheel` ha l'asse Z di Blender (Y di Godot,
    attorno a cui BoatAssembly.ApplyHelmPose la gira) verso prua; mani `Hand_L`/`Hand_R` sulla corona.
    """
    m = mount_console()
    root = empty("Console_Vaporetto", m)
    loc = lambda x, y: (x - m.x, y - m.y)
    b = Builder()
    fz = sheer(HELM_Y + 0.25) - m.z          # quota del ponte nello spazio della consolle (-0,50)
    plan = [loc(x, y) for x, y in DESK]
    b.extrude(plan, fz - 0.05, fz + DESK_Z - 0.04, "Cabin", bottom=None)
    b.extrude(plan, fz + DESK_Z - 0.04, fz + DESK_Z, "Wood", top="ConsoleTop")
    # Listello di mogano sul bordo verso il pilota e montanti agli spigoli.
    aft = [loc(x, y) for x, y in DESK[4:]]
    for (xa, ya), (xb, yb) in zip(aft, aft[1:]):
        # Il bordo va da dritta a sinistra: la normale a sinistra del verso guarda il pilota.
        n = Vector((-(yb - ya), xb - xa)).normalized()
        quad = [(xa, ya), (xb, yb), (xb + n.x * 0.03, yb + n.y * 0.03), (xa + n.x * 0.03, ya + n.y * 0.03)]
        b.extrude(quad, fz + DESK_Z - 0.07, fz + DESK_Z + 0.015, "Wood", top="Wood", bottom="Wood")
    for x, y in aft:
        b.box(x - 0.03, x + 0.03, y - 0.03, y + 0.03, fz - 0.05, fz + DESK_Z - 0.07, "Wood")
    # Strumenti sul piano: pulsantiera e leva al centro, schermi e radio sui fianchi.
    top = fz + DESK_Z
    cx, cy = loc(0.15, 6.32)
    b.atlas_box(cx - 0.25, cx + 0.25, cy - 0.14, cy + 0.14, top, top + 0.09, "Panel", {"Z": "buttons"})
    cx, cy = loc(-0.30, 6.38)
    b.atlas_box(cx - 0.17, cx + 0.17, cy - 0.04, cy + 0.04, top, top + 0.20, "Panel", {"y": "gauges"})
    cx, cy = loc(-0.82, 5.95)
    b.atlas_box(cx - 0.13, cx + 0.13, cy - 0.06, cy + 0.06, top, top + 0.20, "Panel", {"y": "screen"})
    cx, cy = loc(0.85, 5.95)
    b.atlas_box(cx - 0.13, cx + 0.13, cy - 0.08, cy + 0.08, top, top + 0.08, "Panel", {"y": "radio", "Z": "radio"})
    # Leva dell'invertitore/gas (il pomello rosso della foto), a destra della ruota.
    lx, ly = loc(0.22, 6.12)
    b.atlas_box(lx - 0.05, lx + 0.05, ly - 0.08, ly + 0.08, top, top + 0.05, "Panel", {"*": "black"})
    b.cylinder((lx, ly, top + 0.05), (lx, ly - 0.07, top + 0.20), 0.012, "Metal", seg=6)
    b.atlas_box(lx - 0.025, lx + 0.025, ly - 0.095, ly - 0.045, top + 0.18, top + 0.23, "Panel", {"*": "red"})
    # Colonnina di mogano sul piano e asse fino al mozzo: con la pedana la ruota sta 0,40 m sopra il
    # piano, alle mani del pilota (mozzo 0,85 m sopra i suoi piedi, 0,32 m davanti).
    hx, hy = loc(HELM_X, HELM_Y + 0.32)
    py = loc(0, 6.0)[1]
    b.box(hx - 0.07, hx + 0.07, py, py + 0.14, top, WHEEL_HUB_Z + 0.08, "Wood")
    b.cylinder((hx, hy, WHEEL_HUB_Z), (hx, py + 0.01, WHEEL_HUB_Z), 0.03, "Bronze", seg=8)
    to_object("Console", b, root)

    # Ruota: nello spazio del vuoto Wheel la corona sta nel piano XY, l'asse e' +Z (verso prua dopo
    # la rotazione di -90 gradi attorno a X); Y locale punta in basso, la cima e' a 270 gradi.
    wheel = empty("Wheel", (hx, hy, WHEEL_HUB_Z), root, rotation=(math.radians(-90), 0, 0))
    w = Builder()
    r = WHEEL_R
    w.torus(r, 0.022, "Wood", seg=20, sides=6)
    for k in range(8):
        a = k / 8 * math.tau + math.tau / 16
        c, s = math.cos(a), math.sin(a)
        w.cylinder((c * 0.06, s * 0.06, 0), (c * (r + 0.01), s * (r + 0.01), 0), 0.014, "Wood", seg=5, caps=False)
        w.cylinder((c * (r + 0.02), s * (r + 0.02), 0), (c * (r + 0.09), s * (r + 0.09), 0), 0.019, "Wood", seg=6)
    w.cylinder((0, 0, -0.04), (0, 0, 0.06), 0.07, "Bronze", seg=10)
    w.cylinder((0, 0, -0.055), (0, 0, -0.04), 0.045, "Bronze", seg=10)
    to_object("WheelMesh", w, wheel)
    for nm, a in (("Hand_L", 210), ("Hand_R", 330)):
        empty(nm, (r * math.cos(math.radians(a)), r * math.sin(math.radians(a)), 0.0), wheel)
    return root


# =====================================================================================
# Cancelli scorrevoli del varco d'imbarco (i tre tubi del sorgente, due pannelli per lato)
# =====================================================================================

# Due pannelli per lato su due binari affiancati, entrambi verso POPPA lungo la parete del
# vestibolo (telescopici): verso prua chiuderebbero il passaggio di 0,7 m accanto alla timoneria.
# (nome, x del binario, y di poppa chiuso, lunghezza, corsa verso poppa)
GATES = [("Aft", 1.98, 1.32, 1.18, 1.00), ("Fwd", 1.91, 2.50, 1.19, 2.00)]
GATE_H = (0.02, 1.07)      # montanti (sopra il ponte)
GATE_BARS = (0.35, 0.70, 1.05)
GATE_FRAMES = (1, 40, 80, 120)    # anteprima: chiuso, aperto, aperto, chiuso


def gate_mesh(name, length, parent, location):
    """Pannello nello spazio del vuoto: da y = 0 (poppa) a y = length, x = 0, z dal ponte."""
    b = Builder()
    for y in (0.03, length - 0.03):
        b.cylinder((0, y, GATE_H[0]), (0, y, GATE_H[1]), 0.025, "Metal", seg=6)
    for z in GATE_BARS:
        b.cylinder((0, 0.03, z), (0, length - 0.03, z), 0.021, "Metal", seg=6, caps=False)
    return to_object(name, b, parent, location)


def build_gates(root, railings):
    """Toglie dal sorgente i quattro pannelli piatti del varco e mette i cancelli veri. Ogni cancello
    `Gate_<lato>_<pannello>` ha accanto il vuoto `<nome>_Open` = posa ad anta aperta; in gioco lo
    stato (aperto/chiuso) sara' dell'host e il pannello scorrera' fra le due pose. In Blender
    un'azione d'anteprima li apre e li chiude (timeline 1-120)."""
    bm = bmesh.new()
    bm.from_mesh(railings.data)
    doomed = [f for f in bm.faces if abs(f.calc_center_median().x) > 1.85 and 1.25 < f.calc_center_median().y < 3.75]
    bmesh.ops.delete(bm, geom=doomed, context="FACES")
    bm.to_mesh(railings.data)
    bm.free()
    gates = []
    for side, sx in (("Port", -1), ("Stbd", 1)):
        for panel, x, y0, length, travel in GATES:
            name = "Gate_{}_{}".format(side, panel)
            z = sheer(y0)
            g = gate_mesh(name, length, root, (sx * x, y0, z))
            empty(name + "_Open", (sx * x, y0 - travel, sheer(y0 - travel)), root)
            for f, open_ in zip(GATE_FRAMES, (False, True, True, False)):
                g.location.y = y0 - travel if open_ else y0
                g.keyframe_insert("location", index=1, frame=f)
            g.location.y = y0
            gates.append(g)
    scene = bpy.context.scene
    scene.frame_start, scene.frame_end = GATE_FRAMES[0], GATE_FRAMES[-1] + 20
    return gates


# =====================================================================================
# Variante rovinata (solo per la revisione: copia affiancata con i materiali _Worn)
# =====================================================================================

def worn_copy(src_root, offset):
    mapping = {}

    def clone(obj, parent):
        new = obj.copy()
        if obj.data is not None:
            new.data = obj.data.copy()
            for slot in new.material_slots:
                if slot.material and not slot.material.name.startswith("_"):
                    slot.material = bpy.data.materials.get(slot.material.name + WORN, slot.material)
        bpy.context.scene.collection.objects.link(new)
        new.parent = parent
        mapping[obj] = new
        for child in obj.children:
            clone(child, new)
        return new

    root = clone(src_root, None)
    root.location = src_root.location + Vector(offset)
    root.name = src_root.name + WORN
    return root


# =====================================================================================
# Revisione
# =====================================================================================

# =====================================================================================
# Geometrie di ruolo (pipeline §4.2-§4.5) e attacchi (§5)
# =====================================================================================

N_HYDRO = 34           # stazioni della HydroHull (navi: 30-36, piu' fitte a prua)
N_HYDRO_HALF = 10      # punti per mezza sezione (uno spostato sulla DWL)


def hydro_ring(y, tb, n=N_HYDRO_HALF):
    """Sezione completa con un vertice esattamente sulla DWL (stesso numero di punti ovunque)."""
    # Un punto IN PIU' (numero costante per stazione): sulla DWL dove la sezione la attraversa,
    # altrimenti a meta' della murata.
    half = half_section(y, tb, n)
    extra = None
    for i, (a, b) in enumerate(zip(half, half[1:])):
        if a[1] < 0.0 < b[1]:
            f = -a[1] / (b[1] - a[1])
            extra = (i + 1, (a[0] + (b[0] - a[0]) * f, 0.0))
            break
    if extra is None:
        a, b = half[-2], half[-1]
        extra = (len(half) - 1, ((a[0] + b[0]) / 2, (a[1] + b[1]) / 2))
    half.insert(extra[0], extra[1])
    return [(-x, y, z) for x, z in reversed(half[1:])] + [(x, y, z) for x, z in half]


def hydro_stations(n):
    return [Y_HS + (Y_B - Y_HS) * (1.0 - (1.0 - i / (n - 1)) ** 1.15) for i in range(n)]


def closed_hull(name, parent, tb, n_st, n_half, deck_cap=True):
    """Carena chiusa (o aperta in alto se deck_cap=False): fasciame, specchio, coperta alla falchetta."""
    b = Builder()
    ys = hydro_stations(n_st)
    rings = [hydro_ring(y, tb, n_half) for y in ys]
    for ya, ra, rb in zip(ys, rings, rings[1:]):
        zc = (keel_z(ya, tb) + sheer(ya)) / 2
        for i in range(len(ra) - 1):
            q = [ra[i], ra[i + 1], rb[i + 1], rb[i]]
            m = [sum(p[k] for p in q) / 4 for k in range(3)]
            # Alle estremita' (prua che sale, volta di poppa) la normale guarda soprattutto avanti o
            # indietro: l'asse di riferimento si ferma 3 m prima delle punte.
            ax_y = min(max(m[1], Y_HS + 3.0), Y_B - 3.0)
            b.poly(q, "Hull", out=(m[0], m[1] - ax_y, m[2] - zc))
    b.poly(rings[0], "Hull", out=(0, -1, 0))
    if deck_cap:
        for a, c in zip(rings, rings[1:]):
            b.poly([a[0], a[-1], c[-1], c[0]], "Hull", out=(0, 0, 1))
    obj = to_object(name, b, parent)
    triangulate(obj)            # e normali lisce: niente vertici doppi nell'export
    obj.hide_render = True
    return obj


def superstructure_shells(b):
    """Cabina di poppa e timoneria come gusci chiusi appoggiati alla coperta (1 cm sopra, non
    compenetrati): riserva di galleggiamento ai grandi sbandamenti e superficie al vento."""
    y0, y1 = (-12.06 - X_MID) * K, (-1.06 - X_MID) * K
    ys = [y0 + (y1 - y0) * i / 4 for i in range(5)]
    plan = [(half_beam(y), y) for y in ys] + [(-half_beam(y), y) for y in reversed(ys)]
    b.extrude(plan, max(sheer(y) for y in ys) + 0.01, 3.20, "Hull", top="Hull", bottom="Hull")
    wh = [(-1.17, 3.68), (1.17, 3.68), (1.17, 5.77), (0.60, 6.60), (-0.60, 6.60), (-1.17, 5.77)]
    b.extrude(wh, sheer(6.6) + 0.01, 3.20, "Hull", top="Hull", bottom="Hull")


def build_hydro(root, tb):
    b = Builder()
    obj = closed_hull("HydroHull", root, tb, N_HYDRO, N_HYDRO_HALF)
    superstructure_shells(b)
    shells = to_object("_shells", b)
    # Unisce i gusci alla HydroHull (stesso oggetto, gusci separati).
    me = obj.data
    bm = bmesh.new()
    bm.from_mesh(me)
    bm.from_mesh(shells.data)
    bm.to_mesh(me)
    bm.free()
    bpy.data.objects.remove(shells, do_unlink=True)
    triangulate(obj)
    return obj


def triangulate(obj):
    """Triangoli decisi qui (l'export triangolava a modo suo i poligoni ai poli: 9 triangoli
    degeneri, buchi per Godot), poi via quelli di area nulla."""
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bmesh.ops.triangulate(bm, faces=bm.faces[:], quad_method="BEAUTY", ngon_method="BEAUTY")
    bmesh.ops.dissolve_degenerate(bm, edges=bm.edges, dist=0.001)
    bm.to_mesh(obj.data)
    bm.free()
    for p in obj.data.polygons:
        p.use_smooth = True


def slab_from_mesh(b, obj, thickness=0.05, select=None, min_area=0.02):
    """Per ogni faccia (dopo aver fuso le complanari) una lastra spessa `thickness` verso l'interno
    della faccia: pareti, vetri, tetto e ringhiere del sorgente diventano solidi per la capsula."""
    me = obj.data.copy()
    bm = bmesh.new()
    bm.from_mesh(me)
    bm.transform(obj.matrix_world)
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=0.002)
    bmesh.ops.dissolve_limit(bm, angle_limit=math.radians(2.0), verts=bm.verts, edges=bm.edges)
    n = 0
    for f in bm.faces:
        if select is not None and not select(f):
            continue
        if f.calc_area() < min_area:
            continue
        # Il trimesh di Godot collide da un lato solo (backface_collision falso): ogni faccia della
        # lastra deve guardare fuori.
        nrm = f.normal.copy()
        top = [v.co.copy() for v in f.verts]
        bot = [p - nrm * thickness for p in top]
        centre = sum(top, Vector()) / len(top)
        b.poly(top, "Hull", out=nrm)
        b.poly(bot, "Hull", out=-nrm)
        for i in range(len(top)):
            j = (i + 1) % len(top)
            b.poly([top[j], top[i], bot[i], bot[j]], "Hull", out=(top[i] + top[j]) / 2 - centre)
        n += 1
    bm.free()
    bpy.data.meshes.remove(me)
    return n


def build_deck_collision(root, kept):
    """Trimesh calpestabile (layer 6): ponte a lastra, pareti/vetri/tetto/ringhiere come lastre,
    panche, consolle, boccaporto. I cancelli NO: si muovono, li gestisce il gioco."""
    b = Builder()
    ys = station_ys(N_ST)
    for ya, yb in zip(ys, ys[1:]):
        ha, hb_ = max(half_beam(ya), 0.02), max(half_beam(yb), 0.02)
        za, zb = sheer(ya), sheer(yb)
        top = [(-ha, ya, za), (ha, ya, za), (hb_, yb, zb), (-hb_, yb, zb)]
        bot = [(x, y, z - 0.10) for x, y, z in top]
        b.poly(top, "Hull", out=(0, 0, 1))
        b.poly(bot, "Hull", out=(0, 0, -1))
        b.poly([top[1], top[2], bot[2], bot[1]], "Hull", out=(1, 0, 0))
        b.poly([top[3], top[0], bot[0], bot[3]], "Hull", out=(-1, 0, 0))
    y0 = ys[0]
    h0 = half_beam(y0)
    b.poly([(-h0, y0, sheer(y0)), (h0, y0, sheer(y0)), (h0, y0, sheer(y0) - 0.1), (-h0, y0, sheer(y0) - 0.1)],
           "Hull", out=(0, -1, 0))
    counts = {}
    # Degli accessori conta solo il cassone della zattera: salvagente, faro, griglie no.
    for key, min_area in (("Superstructure", 0.02), ("Roof", 0.02), ("Windows", 0.02),
                          ("Railings", 0.02), ("Fittings", 0.3)):
        counts[key] = slab_from_mesh(b, kept[key], 0.05, min_area=min_area)
    # Panche (lato interno e schienale) come prismi pieni.
    deck = DECK
    y0, y1 = (-11.99 - X_MID) * K + 0.35, (-1.12 - X_MID) * K - 0.55
    for s in (1, -1):
        wa, wb = wall_inner(y0) - 0.02, wall_inner(y1) - 0.02
        b.extrude([(s * (wa - 0.42), y0), (s * wa, y0), (s * wb, y1), (s * (wb - 0.42), y1)],
                  deck - 0.02, deck + 0.45, "Hull", top="Hull", bottom="Hull")
        b.extrude([(s * (wa - 0.07), y0), (s * wa, y0), (s * wb, y1), (s * (wb - 0.07), y1)],
                  deck + 0.45, deck + 0.87, "Hull", top="Hull", bottom="Hull")
    b.extrude(DESK, sheer(HELM_Y + 0.25) - 0.05, sheer(HELM_Y + 0.25) + DESK_Z, "Hull", top="Hull", bottom="Hull")
    for x0, x1, y0, y1, h in PILOT_SOLIDS:
        b.box(x0, x1, y0, y1, sheer(HELM_Y) - 0.05, sheer(HELM_Y) + h, "Hull")
    b.box(-0.75, 0.75, 1.25, 3.05, deck - 0.02, deck + 0.05, "Hull")
    obj = to_object("DeckCollision", b, root)
    obj.hide_render = True
    return obj, counts


SKEG_CHORD = SKEG_START - Y_STERNPOST


def skeg_geometry(tb, n=200):
    """Area laterale, apertura media e centro dello skeg (per il FoilDefinition e Mount_Skeg)."""
    area, zsum = 0.0, 0.0
    dy = SKEG_CHORD / n
    for i in range(n):
        y = Y_STERNPOST + (i + 0.5) * dy
        top, bot = keel_z(y, tb), skeg_bottom(y, tb)
        if bot is None or top <= bot:
            continue
        h = top - bot
        area += h * dy
        zsum += (top + bot) / 2 * h * dy
    return area, area / SKEG_CHORD, zsum / max(area, 1e-6)


def build_mounts(root, tb):
    area, span, zc = skeg_geometry(tb)
    empty("Mount_Prop", (0.0, PROP_Y, PROP_Z), root, rotation=(math.radians(SHAFT_DEG), 0, 0))
    empty("Mount_Rudder", (0.0, RUDDER_Y, (RUDDER_Z0 + RUDDER_Z1) / 2), root)
    empty("Mount_Engine", (0.0, 2.15, -0.30), root)
    empty("Mount_Skeg", (0.0, (SKEG_START + Y_STERNPOST) / 2, zc), root)
    return {"skeg_area": round(area, 3), "skeg_span": round(span, 3), "skeg_chord": round(SKEG_CHORD, 3),
            "skeg_z": round(zc, 3), "rudder_area": round(RUDDER_CHORD * (RUDDER_Z1 - RUDDER_Z0), 3),
            "rudder_span": round(RUDDER_Z1 - RUDDER_Z0, 3)}


# =====================================================================================
# Export (pipeline §6.1): scafo e consolle, ciascuno pulito e rovinato (stessi nomi di materiale,
# immagini diverse).
# =====================================================================================

OUT_DIR = PROJECT_DIR + "/assets/models/boats"


def swap_images(worn):
    for name, spec in MATERIALS.items():
        mat = bpy.data.materials[name]
        for node in mat.node_tree.nodes:
            if node.type == "TEX_IMAGE":
                node.image = bpy.data.images["vap_" + spec[0] + (WORN if worn else "")]


def export_glb(root, filename, worn=False):
    objs = [root] + [o for o in root.children_recursive if not o.name.startswith("_")]
    saved = root.location.copy()
    root.location = (0, 0, 0)
    swap_images(worn)
    try:
        # Dopo bpy.data.objects.remove il view layer ha voci vuote finche' non si aggiorna.
        bpy.context.view_layer.update()
        for o in bpy.context.scene.objects:
            o.select_set(False)
        for o in objs:
            o.hide_set(False)
            o.select_set(True)
        bpy.context.view_layer.objects.active = root
        bpy.context.view_layer.update()
        path = "{}/{}.glb".format(OUT_DIR, filename)
        # Solo finestra/area: passando anche selected_objects l'operatore di Blender 5.2 fallisce
        # ('NoneType' ... select_set); la selezione e' gia' quella giusta.
        with view3d_override():
            bpy.ops.export_scene.gltf(
                filepath=path, export_format="GLB", use_selection=True, export_yup=True,
                export_animations=False, export_skins=False,
                export_materials="EXPORT", export_cameras=False, export_lights=False,
                export_extras=False)
    finally:
        swap_images(False)
        root.location = saved
        for o in objs:
            o.select_set(False)
    return path


def review_helpers():
    """Piano dell'acqua alla DWL e un 'uomo' da 1,80 m: solo per la revisione, non si esportano."""
    mat = bpy.data.materials.new("_ReviewWater")
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    bsdf.inputs["Base Color"].default_value = (0.10, 0.35, 0.40, 1.0)
    bsdf.inputs["Alpha"].default_value = 0.45
    mat.surface_render_method = "BLENDED"
    b = Builder()
    b.verts = [(-6, -16, 0), (6 + WORN_OFFSET, -16, 0), (6 + WORN_OFFSET, 16, 0), (-6, 16, 0)]
    b.faces = [[0, 1, 2, 3]]
    b.mats = ["Glass"]
    me = bpy.data.meshes.new("_ReviewWater")
    me.from_pydata(b.verts, [], b.faces)
    me.materials.append(mat)
    water = bpy.data.objects.new("_ReviewWater", me)
    bpy.context.scene.collection.objects.link(water)
    water.hide_select = True
    p = Builder()
    p.cylinder((0.0, 2.2, DECK), (0.0, 2.2, DECK + 1.8), 0.25, "MotorTrim", seg=10)
    to_object("_ReviewPerson_1m80", p)


def purge_source_data():
    """Materiali, immagini e mesh del sorgente rimasti senza utenti dopo l'import."""
    for coll in (bpy.data.meshes, bpy.data.materials, bpy.data.images):
        for item in list(coll):
            if item.users == 0 and item.name.removesuffix(WORN) not in MATERIALS and not item.name.startswith("vap_"):
                coll.remove(item)


def setup_view():
    for window in bpy.context.window_manager.windows:
        for area in window.screen.areas:
            if area.type == "VIEW_3D":
                space = area.spaces.active
                space.shading.type = "MATERIAL"
                space.clip_end = 1000
                region = next((r for r in area.regions if r.type == "WINDOW"), None)
                try:
                    with bpy.context.temp_override(window=window, screen=window.screen, area=area, region=region):
                        bpy.ops.view3d.view_all()
                except RuntimeError:
                    pass


def count_tris(obj):
    return sum(len(p.vertices) - 2 for p in obj.data.polygons)


def main():
    bpy.ops.wm.read_homefile(use_empty=True)
    bpy.context.scene.unit_settings.system = "METRIC"
    bpy.context.scene.unit_settings.scale_length = 1.0
    build_materials()
    build_materials(worn=True)
    root = empty("Vaporetto", (0, 0, 0))
    kept = import_source(root)
    tb = solve_body_draft()
    full = SHAPE["full_mid"]
    hull = build_hull(root, tb)
    rudder = build_rudder(root)
    prop = build_propeller(root)
    fit = build_fitout(root)
    named = {o.name: o for o in kept.values()}
    cabin_faces = paint_wheelhouse(named)
    gates = build_gates(root, named["Railings"])
    empty("Seat_Helm", (HELM_X, HELM_Y, helm_feet_z()), root)
    empty("Mount_Console", tuple(mount_console()), root)
    console = build_console()
    for o in [hull, fit, rudder, prop] + list(kept.values()) + [bpy.data.objects["Shaft"]] + gates:
        box_uv(o, keep_existing=("Glass", "Railing", "Fittings"))
    for o in console.children_recursive:
        if o.type == "MESH":
            box_uv(o, keep_existing=("Panel",))
    hydro = build_hydro(root, tb)
    closed_hull("HullCollision", root, tb, 10, 4)
    closed_hull("SwimCollision", root, tb, N_HYDRO, N_HYDRO_HALF, deck_cap=False)
    deckcol, slabs = build_deck_collision(root, named)
    foils = build_mounts(root, tb)
    purge_source_data()
    exported = []
    if "noexport" not in ARGV:
        exported = [export_glb(root, "Vaporetto"), export_glb(root, "Vaporetto_Worn", worn=True),
                    export_glb(console, "Console_Vaporetto"),
                    export_glb(console, "Console_Vaporetto_Worn", worn=True)]
    worn_copy(root, (WORN_OFFSET, 0, 0))
    worn_copy(console, (WORN_OFFSET, 0, 0))
    review_helpers()
    # In revisione le geometrie di ruolo restano nascoste nel viewport (si riaccendono a mano).
    for o in bpy.data.objects:
        if o.name.split(".")[0] in ("HydroHull", "HullCollision", "SwimCollision", "DeckCollision"):
            o.hide_set(True)
    setup_view()

    roles = ("HydroHull", "HullCollision", "SwimCollision", "DeckCollision")
    meshes = [o for o in root.children if o.type == "MESH" and o.name not in roles]
    xs = [(o.matrix_world @ v.co) for o in meshes for v in o.data.vertices]
    return {
        "body_draft_m": tb,
        "fullness_mid": round(full, 3),
        "displacement_kg": round(displaced_volume(tb) * RHO),
        "loa": round(max(v.y for v in xs) - min(v.y for v in xs), 3),
        "beam_overall": round(max(v.x for v in xs) - min(v.x for v in xs), 3),
        "beam_hull_mid": round(2 * half_beam(0.0), 3),
        "z_range": [round(min(v.z for v in xs), 3), round(max(v.z for v in xs), 3)],
        "tris": {o.name: count_tris(o) for o in meshes},
        "tris_total": sum(count_tris(o) for o in meshes),
        "console_tris": sum(count_tris(o) for o in console.children_recursive if o.type == "MESH"),
        "wheelhouse_faces_green": cabin_faces,
        "deck_slabs": slabs,
        "role_tris": {o.name: count_tris(o) for o in root.children if o.name in roles},
        "foils": foils,
        "exported": exported,
        "k": round(K, 4), "kb": round(KB, 4),
    }


result = main()
