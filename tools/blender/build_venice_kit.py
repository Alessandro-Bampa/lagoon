"""Genera il kit modulare dei palazzi veneziani (stile PSX / Half-Life 2).

Tutto e' procedurale: texture (numpy) e mesh (coordinate scritte a mano). Nessun pack esterno.
Produce in `assets/models/buildings/<kit>/`:

  textures/*.png   texture condivise da tutti i moduli (64 px/m, palette a 15 bit con dithering)
  <modulo>.gltf    un file per modulo (+ .bin), che referenzia le texture per percorso relativo

Uso (Blender aperto con l'estensione MCP, vedi la skill blender-pipeline):

    python tools/blender/blender_client.py tools/blender/build_venice_kit.py                      # i due kit
    python tools/blender/blender_client.py tools/blender/build_venice_kit.py venice_ruin preview  # un kit + render

Due kit dalla stessa sorgente: `venice` (pulito) e `venice_ruin` (stile S.T.A.L.K.E.R.: stesse
misure e stessi nomi, texture sporche, vetri trasparenti e in piu' i pezzi rotti).

GRIGLIA E ASSI (dettagli e tabella dei moduli nella skill `venice-building-kit`).
  - cella 2 x 2 m, piano alto 3 m (2,7 m netti + 0,3 m di solaio);
  - in Blender i muri corrono lungo X e la faccia ESTERNA guarda -Y, che dopo l'export e'
    +Z di Godot. Il muro si sviluppa verso l'interno (+Y Blender = -Z Godot);
  - moduli "di bordo" (muri, fasce): origine al centro del bordo, a quota pavimento;
  - moduli "di cella" (pavimenti, soffitti, tetto): origine al centro della cella.

COLLISIONI. Gli oggetti si chiamano `<modulo>-col`: l'import di Godot ne ricava un
MeshInstance3D + StaticBody3D (trimesh). I muri con un'apertura hanno invece una collisione
SEMPLIFICATA a parte (`<modulo>_col-colonly`, vedi `collider_wall`): muro pieno se
l'apertura e' chiusa (vetro, scuri, grata), muro col varco se ci si passa; niente cornici,
davanzali e scuri. Le scale hanno rampe
convesse (`-convcolonly`) al posto dei gradini. Nessun materiale e' double-sided: dove serve il
retro (vetri, grate, balaustre) ci sono due facce, perche' un trimesh non ferma chi arriva
da dietro.
"""

import math
import os
import tempfile

import bpy
import numpy as np
from mathutils import Vector

# kit -> e' la variante "rovina"? (stessi moduli e stessa griglia, texture sporche + pezzi rotti)
KITS = {"venice": False, "venice_ruin": True}
PREVIEW = "preview" in ARGV
SELECTED = [a for a in ARGV if a in KITS] or list(KITS)
BUILDINGS_DIR = PROJECT_DIR + "/assets/models/buildings"
# Impostati da run() per il kit in costruzione.
KIT = "venice"
RUIN = False
OUT_DIR = ""
TEX_DIR = ""

PPM = 64          # pixel per metro
W = 2.0           # larghezza di un modulo
H = 3.0           # altezza di un piano (pavimento -> pavimento)
T = 0.3           # spessore del muro esterno
TI = 0.16         # spessore del muro interno
HI = 2.7          # altezza netta interna
PITCH = 0.4       # pendenza del tetto (0,8 m ogni cella)


# =====================================================================================
# TEXTURE
# =====================================================================================

def c(r, g, b):
    return np.array([r, g, b], dtype=np.float32)


# TAVOLOZZA. I colori di base di tutto il kit stanno qui (e in FACADES, piu' sotto, per gli
# intonaci di facciata): cambiarne uno e rilanciare lo script ricolora ogni texture che lo usa,
# in entrambi i kit, e la rovina ci applica sopra lo stesso `grime()`.
PALETTE = {
    "shutter": c(0.15, 0.33, 0.24),       # scuri (verde veneziano)
    "plaster": c(0.86, 0.80, 0.67),       # pareti interne
    "dado": c(0.50, 0.36, 0.27),          # zoccolatura dipinta
    "stone": c(0.80, 0.78, 0.71),         # pietra d'Istria: cornici, fasce, gradini
    "roof": c(0.72, 0.39, 0.24),          # coppi
    "wood": c(0.30, 0.21, 0.14),          # travi e telai
    "door": c(0.34, 0.21, 0.13),          # portoni
    "brick_dark": c(0.50, 0.23, 0.17),
    "brick_light": c(0.72, 0.40, 0.27),
    "mold": c(0.10, 0.13, 0.08),          # muffa e umidita' (solo rovina)
}


def vnoise(w, h, cells_x, cells_y, seed):
    """Rumore di valore tileable (griglia `cells` che si richiude sui bordi)."""
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
    cc = g[np.ix_(y1, x0)]
    d = g[np.ix_(y1, x1)]
    top = a + (b - a) * fx[None, :]
    bot = cc + (d - cc) * fx[None, :]
    return top + (bot - top) * fy[:, None]


def fbm(w, h, cx, cy, seed, octaves=3):
    total = np.zeros((h, w))
    amp, norm = 1.0, 0.0
    for o in range(octaves):
        total += amp * vnoise(w, h, min(w, cx * 2 ** o), min(h, cy * 2 ** o), seed + o * 17)
        norm += amp
        amp *= 0.5
    return total / norm


def grid(w, h):
    yy, xx = np.mgrid[0:h, 0:w]
    return xx, yy


def flat(w, h, color):
    return np.ones((h, w, 3), dtype=np.float32) * color[None, None, :]


def shade(img, factor):
    return img * factor[..., None]


def grain(w, h, seed, amount):
    return 1.0 - amount / 2 + amount * np.random.default_rng(seed).random((h, w))


def cracks(img, seed, count, strength=0.3):
    rng = np.random.default_rng(seed)
    h, w = img.shape[:2]
    for _ in range(count):
        x, y = int(rng.integers(w)), int(rng.integers(h))
        for _ in range(int(rng.integers(14, 44))):
            img[y % h, x % w] *= 1.0 - strength
            y += 1
            x += int(rng.choice([-1, 0, 0, 1]))


def grime(img, seed, amount=1.0, drips=True):
    """Sporco da Zona: toglie colore, scurisce, aggiunge chiazze, colature e muffa."""
    h, w = img.shape[:2]
    grey = img.mean(axis=2, keepdims=True)
    img = img * (1 - 0.45 * amount) + grey * 0.45 * amount
    img = img * (1 - 0.28 * amount)
    cx, cy = max(2, w // 32), max(2, h // 32)
    # chiazze a grana fine: con poche ottave sembrano ombre morbide, non sporco
    blot = np.clip((fbm(w, h, cx * 2, cy * 2, seed, 5) - 0.48) * 4, 0, 1)
    img = shade(img, 1 - 0.40 * amount * blot)
    img = shade(img, 1 - 0.25 * amount * (np.random.default_rng(seed + 7).random((h, w)) > 0.82))
    if drips:
        streak = np.clip(vnoise(w, h, max(2, w // 2), 2, seed + 1) - 0.5, 0, 1) * 2
        img = shade(img, 1 - 0.45 * amount * streak)
    mold = (np.clip((fbm(w, h, cx * 3, cy * 3, seed + 2, 4) - 0.63) * 8, 0, 1) * 0.45 * amount)[..., None]
    return img * (1 - mold) + PALETTE["mold"][None, None] * mold


BAYER = (np.array([[0, 8, 2, 10], [12, 4, 14, 6], [3, 11, 1, 9], [15, 7, 13, 5]]) + 0.5) / 16 - 0.5


def psx(img, levels=32):
    """Riduce a 5 bit per canale con dithering ordinato: e' la grana delle texture PSX."""
    h, w = img.shape[:2]
    t = np.tile(BAYER, (h // 4 + 1, w // 4 + 1))[:h, :w, None]
    return np.clip(np.floor(img * (levels - 1) + 0.5 + t * 0.9) / (levels - 1), 0, 1)


def bricks(w, h, seed, bw=16, bh=6):
    rng = np.random.default_rng(seed)
    xx, yy = grid(w, h)
    row = yy // bh
    off = (row % 2) * (bw // 2)
    col = ((xx + off) // bw) % (w // bw)
    tone = rng.random((h // bh + 1, w // bw))[row, col]
    img = PALETTE["brick_dark"][None, None] + (PALETTE["brick_light"] - PALETTE["brick_dark"])[None, None] * tone[..., None]
    img[tone < 0.10] = c(0.36, 0.20, 0.17)
    img[tone > 0.93] = c(0.74, 0.52, 0.36)
    img = shade(img, grain(w, h, seed + 1, 0.16))
    mortar = ((yy % bh) == bh - 1) | (((xx + off) % bw) == 0)
    img[mortar] = c(0.60, 0.56, 0.48)
    return img


FACADES = {
    "ochre": c(0.80, 0.61, 0.36),
    "red": c(0.66, 0.31, 0.23),
    "pink": c(0.83, 0.60, 0.52),
    "yellow": c(0.87, 0.75, 0.49),
    "grey": c(0.72, 0.69, 0.62),
    "brick": None,
}


def tex_facade(kind, seed, base):
    """Intonaco 2 x 3 m. `base` = piano terra: umidita' di risalita e mattoni a vista."""
    w, h = 128, 192
    xx, yy = grid(w, h)
    brick = bricks(w, h, seed + 40)
    if FACADES[kind] is None:
        img = shade(brick, 0.82 + 0.30 * fbm(w, h, 4, 6, seed, 3))
    else:
        col = FACADES[kind]
        img = shade(flat(w, h, col), 0.80 + 0.34 * fbm(w, h, 4, 6, seed, 4))
        # rappezzi piu' chiari
        patch = np.clip((fbm(w, h, 3, 4, seed + 5, 2) - 0.60) * 7, 0, 1)[..., None]
        img = img * (1 - patch) + (col * 1.10 + 0.05)[None, None] * patch
        img = shade(img, grain(w, h, seed + 1, 0.12))
        # colature verticali
        streak = np.clip(vnoise(w, h, 32, 2, seed + 9) - 0.55, 0, 1)
        img = shade(img, 1 - 0.14 * streak)
        cracks(img, seed + 3, 14 if RUIN else 5)
        if RUIN:
            # intonaco caduto a chiazze, con l'ombra del bordo sopra il mattone
            fall = fbm(w, h, 3, 5, seed + 30, 4) > 0.63
            lip = fall & ~np.roll(fall, 1, axis=0)
            img = np.where(fall[..., None], brick * 0.9, img)
            img[lip] *= 0.45
    if base:
        hb = (h - 1 - yy) / PPM
        edge = 0.25 + 0.60 * vnoise(w, 1, 8, 1, seed + 20)[0][None, :] \
            + 0.14 * (vnoise(w, h, 16, 24, seed + 21) - 0.5)
        if FACADES[kind] is not None:
            # sali: fascia sbiadita sopra il bordo dell'intonaco
            salt = np.clip(1 - (hb - edge) / 0.5, 0, 1) * (hb >= edge)
            img = img * (1 - 0.35 * salt[..., None]) + (img * 0.5 + 0.42) * 0.35 * salt[..., None]
            lip = (hb >= edge) & (hb < edge + 1.6 / PPM)
            img[lip] *= 0.62
            img = np.where((hb < edge)[..., None], brick, img)
        damp = np.clip(1 - hb / 0.40, 0, 1)[..., None]
        img = img * (1 - 0.50 * damp) + c(0.15, 0.19, 0.13)[None, None] * 0.50 * damp
    return img


def tex_plaster():
    """Parete interna 2 x 3 m: marmorino chiaro, zoccolatura dipinta, battiscopa."""
    w, h = 128, 192
    xx, yy = grid(w, h)
    hb = (h - 1 - yy) / PPM
    img = shade(flat(w, h, PALETTE["plaster"]), 0.86 + 0.22 * fbm(w, h, 4, 6, 71, 4))
    cracks(img, 72, 3, 0.2)
    dado = shade(flat(w, h, PALETTE["dado"]), 0.80 + 0.36 * fbm(w, h, 6, 8, 73, 3))
    img = np.where((hb < 0.90)[..., None], dado, img)
    img[(hb >= 0.90) & (hb < 0.93)] = c(0.28, 0.20, 0.15)
    img[(hb >= 0.93) & (hb < 0.95)] = c(0.92, 0.87, 0.74)
    img[hb < 0.10] = c(0.24, 0.19, 0.15)
    if RUIN:
        fall = fbm(w, h, 3, 5, 76, 3) > 0.64
        lip = fall & ~np.roll(fall, 1, axis=0)
        img = np.where(fall[..., None], bricks(w, h, 77) * 0.85, img)
        img[lip] *= 0.5
        damp = np.clip(1 - hb / 0.7, 0, 1)[..., None] * 0.55
        img = img * (1 - damp) + c(0.12, 0.15, 0.10)[None, None] * damp
    return shade(img, grain(w, h, 74, 0.08))


def tex_stone(blocks):
    w = h = 64
    xx, yy = grid(w, h)
    img = shade(flat(w, h, PALETTE["stone"]), 0.84 + 0.26 * fbm(w, h, 4, 4, 81, 3))
    vein = np.clip(vnoise(w, h, 8, 32, 82) - 0.72, 0, 1)
    img = shade(img, 1 - 0.8 * vein)
    img = shade(img, grain(w, h, 83, 0.08))
    if blocks:
        joint = ((yy % 32) == 31) | (((xx + (yy // 32 % 2) * 32) % 64) == 0)
        light = ((yy % 32) == 0)
        img[light] *= 1.06
        img[joint] *= 0.60
    return img


def tex_dentil():
    w, h = 64, 16
    xx, yy = grid(w, h)
    img = shade(flat(w, h, PALETTE["stone"]), grain(w, h, 91, 0.10))
    gap = (yy >= 4) & (yy < 12) & ((xx % 8) >= 5)
    img[gap] = c(0.26, 0.24, 0.21)
    img[yy >= 12] *= 0.74
    img[yy == 3] *= 0.85
    return img


def tex_ceiling():
    w = h = 64
    return shade(flat(w, h, c(0.86, 0.84, 0.77)), (0.90 + 0.14 * fbm(w, h, 4, 4, 95, 3)) * grain(w, h, 96, 0.06))


def tex_shutter(h=64):
    w = 32
    xx, yy = grid(w, h)
    img = flat(w, h, PALETTE["shutter"])
    inner = (xx >= 3) & (xx < w - 3) & (yy >= 3) & (yy < h - 3) & ~((yy >= h // 2 - 2) & (yy < h // 2 + 2))
    img[inner & (yy % 4 == 3)] *= 0.55
    img[inner & (yy % 4 == 0)] *= 1.18
    img[~inner] *= 0.88
    img[(xx == 0) | (xx == w - 1) | (yy == 0) | (yy == h - 1)] *= 0.6
    img = shade(img, (0.82 + 0.3 * fbm(w, h, 4, 8, 101, 2)) * grain(w, h, 102, 0.12))
    peel = np.random.default_rng(103).random((h, w)) > (0.90 if RUIN else 0.975)
    img[peel] = c(0.50, 0.52, 0.44)
    if RUIN:
        # stecche mancanti
        for y0 in np.random.default_rng(104).integers(6, h - 8, 3):
            gap = inner & (yy >= y0 - y0 % 4) & (yy < y0 - y0 % 4 + 4) & (xx > 4) & (xx < 20 + y0 % 9)
            img[gap] = c(0.04, 0.04, 0.04)
    return img


def tex_shutters_closed():
    """Coppia di scuri chiusi, 64 x 128: riempie l'intera apertura."""
    leaf = tex_shutter(128)
    img = np.concatenate([leaf, leaf[:, ::-1]], axis=1)
    img[:, 31:33] *= 0.35
    for y0 in (20, 104):
        img[y0:y0 + 3, 2:10] = c(0.10, 0.10, 0.10)
        img[y0:y0 + 3, 54:62] = c(0.10, 0.10, 0.10)
    return img


def window_frame(xx, yy, w=64, h=128):
    return (xx < 4) | (xx >= w - 4) | (yy < 4) | (yy >= h - 4) | (np.abs(xx - 31.5) < 2) \
        | (np.abs(yy - 43.5) < 1.5) | (np.abs(yy - 85.5) < 1.5)


def tex_glass(dirt, broken, seed):
    """Vetro TRASPARENTE (RGBA): telaio opaco, lastre semitrasparenti piu' sporche ai bordi.

    L'alfa e' ridotta a 8 livelli con dithering: la trasparenza "a retino" delle console a
    32 bit, che regge anche col filtro nearest. `broken` apre fori frastagliati e crepe.
    """
    w, h = 64, 128
    xx, yy = grid(w, h)
    rng = np.random.default_rng(seed)
    frame = window_frame(xx, yy)
    tint = c(0.50, 0.62, 0.64) * (1 - dirt) + c(0.36, 0.37, 0.27) * dirt
    img = shade(flat(w, h, tint), 0.85 + 0.3 * fbm(w, h, 4, 8, seed, 3))
    dx = np.minimum.reduce([np.abs(xx - 3.5), np.abs(xx - 31.5) - 1.5, np.abs(xx - 60)])
    dy = np.minimum.reduce([np.abs(yy - 3.5), np.abs(yy - 43.5) - 1, np.abs(yy - 85.5) - 1, np.abs(yy - 124)])
    edge = np.clip(1 - np.minimum(dx, dy) / 9.0, 0, 1)
    alpha = 0.20 + 0.22 * dirt + edge * (0.22 + 0.38 * dirt)
    alpha += dirt * 0.45 * np.clip((fbm(w, h, 4, 8, seed + 1, 3) - 0.45) * 3, 0, 1)
    glint = ((xx + yy // 2) % 48) < 7
    img[glint] = img[glint] * 0.4 + c(0.86, 0.92, 0.94) * 0.6
    alpha = alpha + glint * 0.16 * (1 - dirt)
    if broken:
        for px in (17.5, 46.5):
            for py in (23.5, 64.5, 105.0):
                if rng.random() < 0.25:
                    continue
                cx, cy = px + rng.uniform(-5, 5), py + rng.uniform(-8, 8)
                radius = rng.uniform(6, 13)
                sectors = 0.45 + rng.random(9)
                ang = np.arctan2(yy - cy, xx - cx)
                dist = np.hypot(xx - cx, yy - cy)
                hole = dist < radius * sectors[((ang + np.pi) / (2 * np.pi) * 9).astype(int) % 9]
                alpha[hole] = 0.0
                for a in rng.uniform(0, 2 * np.pi, 5):
                    for t in np.arange(radius * 0.5, radius * 2.6, 0.5):
                        x, y = int(cx + math.cos(a) * t), int(cy + math.sin(a) * t)
                        if 0 <= x < w and 0 <= y < h and alpha[y, x] > 0:
                            img[y, x] = c(0.85, 0.90, 0.90)
                            alpha[y, x] = 0.85
    wood = shade(flat(w, h, c(0.33, 0.24, 0.16)), grain(w, h, 112, 0.25))
    if RUIN:
        wood = grime(wood, seed + 5, 0.8)
    img = np.where(frame[..., None], wood, img)
    t = np.tile(BAYER, (h // 4, w // 4))
    alpha = np.clip(np.floor(np.clip(alpha, 0, 0.92) * 7 + 0.5 + t * 0.9) / 7, 0, 1)
    alpha = np.where(frame, 1.0, alpha)
    return img, alpha.astype(np.float32)


def tex_brick_core():
    """Sezione di muratura: il materiale di ogni rottura (bordi di brecce, creste, solai)."""
    img = bricks(64, 66, 401)[:64] * 0.72
    chunk = fbm(64, 64, 8, 8, 402, 2) > 0.62
    img[chunk] = c(0.44, 0.41, 0.35)
    return shade(img, 0.75 + 0.4 * fbm(64, 64, 4, 4, 403, 3))


def tex_rubble():
    """Macerie: frammenti di mattone, intonaco e pietra."""
    w = h = 64
    xx, yy = grid(w, h)
    rng = np.random.default_rng(411)
    pts = rng.random((46, 2)) * w
    best = np.full((h, w), 1e9)
    second = np.full((h, w), 1e9)
    owner = np.zeros((h, w), dtype=int)
    for i, (px, py) in enumerate(pts):
        ddx = np.minimum(np.abs(xx - px), w - np.abs(xx - px))
        ddy = np.minimum(np.abs(yy - py), h - np.abs(yy - py))
        d = np.hypot(ddx, ddy)
        closer = d < best
        second = np.where(closer, best, np.minimum(second, d))
        owner = np.where(closer, i, owner)
        best = np.where(closer, d, best)
    palette = [c(0.50, 0.26, 0.19), c(0.40, 0.21, 0.17), c(0.58, 0.55, 0.47), c(0.36, 0.35, 0.33),
               c(0.62, 0.36, 0.25), c(0.46, 0.43, 0.36), c(0.24, 0.20, 0.17)]
    img = np.array(palette)[rng.integers(0, len(palette), len(pts))][owner]
    img = shade(img, (0.75 + 0.35 * rng.random(len(pts))[owner]) * grain(w, h, 412, 0.18))
    img[(second - best) < 1.1] *= 0.35
    return img * 0.72


def tex_roof_bare():
    """Orditura del tetto senza coppi (ritaglio alfa): travetti lungo la falda e listelli."""
    w = h = 128
    xx, yy = grid(w, h)
    rafter = (xx % 32) < 7
    batten = ((yy % 16) < 3) & ((xx // 32 + yy // 16) % 5 != 0)
    img = shade(flat(w, h, c(0.25, 0.20, 0.15)), (0.75 + 0.45 * vnoise(w, h, 64, 4, 421)) * grain(w, h, 422, 0.15))
    img[batten & ~rafter] *= 0.8
    img[(xx % 32) == 0] *= 0.5
    return img, (rafter | batten).astype(np.float32)


def tex_window():
    w, h = 64, 128
    xx, yy = grid(w, h)
    t = (yy / h)[..., None]
    img = c(0.11, 0.15, 0.21)[None, None] * (1 - t) + c(0.04, 0.06, 0.09)[None, None] * t
    img = img + (((xx + yy // 2) % 48) < 7)[..., None] * 0.07
    img = shade(img, grain(w, h, 111, 0.10))
    frame = (xx < 4) | (xx >= w - 4) | (yy < 4) | (yy >= h - 4) | (np.abs(xx - 31.5) < 2) \
        | (np.abs(yy - 43.5) < 1.5) | (np.abs(yy - 85.5) < 1.5)
    wood = shade(flat(w, h, c(0.33, 0.24, 0.16)), grain(w, h, 112, 0.25))
    img = np.where(frame[..., None], wood, img)
    edge = (xx == 4) | (yy == 4) | (xx == 34) | (yy == 46) | (yy == 88)
    img[edge & ~frame] *= 0.5
    return img


def tex_door_ext():
    w, h = 64, 128
    xx, yy = grid(w, h)
    tone = np.random.default_rng(121).random(w // 8)[xx // 8]
    img = shade(flat(w, h, PALETTE["door"]), 0.80 + 0.35 * tone)
    img = shade(img, 0.85 + 0.3 * vnoise(w, h, 32, 4, 122))
    img[(xx % 8) == 0] *= 0.55
    img[(xx == 31) | (xx == 32)] *= 0.35
    for y0 in (18, 104):
        band = (yy >= y0) & (yy < y0 + 5)
        img[band] = c(0.15, 0.15, 0.16)
        img[band & (yy == y0 + 2) & (xx % 8 == 4)] = c(0.42, 0.42, 0.42)
    img[(yy >= 58) & (yy < 66) & (xx >= 36) & (xx < 40)] = c(0.62, 0.50, 0.22)
    damp = np.clip((yy - 100) / 28, 0, 1)
    img = shade(img, 1 - 0.45 * damp)
    return shade(img, grain(w, h, 123, 0.10))


def tex_door_int():
    w, h = 64, 128
    xx, yy = grid(w, h)
    img = shade(flat(w, h, c(0.45, 0.30, 0.18)), 0.85 + 0.3 * vnoise(w, h, 32, 4, 131))
    for (y0, y1) in ((10, 58), (68, 118)):
        ring = (xx >= 8) & (xx < 56) & (yy >= y0) & (yy < y1)
        inner = (xx >= 10) & (xx < 54) & (yy >= y0 + 2) & (yy < y1 - 2)
        img[ring & ~inner] *= 0.55
        img[inner] *= 0.88
    img[(yy >= 60) & (yy < 65) & (xx >= 54) & (xx < 59)] = c(0.66, 0.55, 0.25)
    img[(xx == 0) | (xx == w - 1)] *= 0.6
    return shade(img, grain(w, h, 132, 0.10))


def tex_wood():
    w = h = 64
    img = shade(flat(w, h, PALETTE["wood"]), 0.78 + 0.44 * vnoise(w, h, 4, 32, 141))
    return shade(img, grain(w, h, 142, 0.10))


def tex_planks(color, seed):
    w = h = 128
    xx, yy = grid(w, h)
    rng = np.random.default_rng(seed)
    board = xx // 16
    tone = rng.random(w // 16)[board]
    img = shade(flat(w, h, color), 0.78 + 0.40 * tone)
    img = shade(img, 0.86 + 0.28 * vnoise(w, h, 64, 4, seed + 1))
    joints = rng.integers(0, h, w // 16)[board]
    img[(yy == joints) | (yy == (joints + 71) % h)] *= 0.55
    img[(xx % 16) == 0] *= 0.50
    return shade(img, grain(w, h, seed + 2, 0.08))


def tex_terrazzo():
    w = h = 128
    rng = np.random.default_rng(151)
    img = shade(flat(w, h, c(0.70, 0.61, 0.54)), 0.92 + 0.14 * fbm(w, h, 4, 4, 152, 2))
    chips = [c(0.90, 0.88, 0.82), c(0.52, 0.22, 0.18), c(0.16, 0.15, 0.15), c(0.76, 0.58, 0.30),
             c(0.55, 0.55, 0.52)]
    r = rng.random((h, w))
    pick = rng.integers(0, len(chips), (h, w))
    for i, chip in enumerate(chips):
        img[(r < 0.22) & (pick == i)] = chip
    return img


def tex_paving():
    w = h = 128
    xx, yy = grid(w, h)
    row = yy // 32
    off = (row % 2) * 32
    col = ((xx + off) // 64) % 2
    tone = np.random.default_rng(161).random((4, 2))[row, col]
    img = shade(flat(w, h, c(0.47, 0.47, 0.48)), 0.80 + 0.36 * tone)
    img = shade(img, (0.88 + 0.24 * fbm(w, h, 8, 8, 162, 3)) * grain(w, h, 163, 0.14))
    joint = ((yy % 32) == 0) | (((xx + off) % 64) == 0)
    img[joint] *= 0.45
    return img


def tex_roof():
    """Coppi: colonne da 25 cm lungo v (= lungo la falda), file da 50 cm."""
    w = h = 128
    xx, yy = grid(w, h)
    tone = np.random.default_rng(171).random((4, 8))[yy // 32, xx // 16]
    img = flat(w, h, PALETTE["roof"]) * (0.78 + 0.36 * tone)[..., None]
    img[tone < 0.10] = c(0.42, 0.31, 0.23)
    img[tone > 0.92] = c(0.80, 0.56, 0.36)
    p = (xx % 16 + 0.5) / 16
    img = shade(img, 0.50 + 0.62 * np.sin(np.pi * p))
    img[(yy % 32) == 31] *= 0.45
    img[(yy % 32) == 0] *= 1.15
    img[(xx % 16) == 0] *= 0.55
    img = shade(img, grain(w, h, 172, 0.14))
    if RUIN:
        img = grime(img, 173, 0.9)
        moss = (np.clip((fbm(w, h, 6, 6, 174, 3) - 0.50) * 5, 0, 1) * 0.75)[..., None]
        img = img * (1 - moss) + c(0.17, 0.22, 0.10)[None, None] * moss * (0.7 + 0.6 * np.sin(np.pi * p))[..., None]
    return img


def tex_grille():
    w = h = 64
    xx, yy = grid(w, h)
    bar = ((xx % 16) // 2 == 3) | ((yy % 16) // 2 == 3)
    img = shade(flat(w, h, c(0.13, 0.12, 0.12)), grain(w, h, 181, 0.3))
    rust = np.random.default_rng(182).random((h, w)) > 0.85
    img[rust] = c(0.30, 0.17, 0.10)
    return img, bar.astype(np.float32)


def tex_balustrade():
    """Balaustra 1 m x 0,9 m: quattro colonnine e due correnti, il resto e' trasparente."""
    w = h = 64
    xx, yy = grid(w, h)
    t = np.clip((yy - 7) / 50, 0, 1)
    half = 1.6 + 3.4 * np.sin(np.pi * t ** 0.7) ** 1.5
    dist = np.abs((xx % 16) - 7.5)
    rail = (yy < 7) | (yy >= 57)
    alpha = rail | (dist < half)
    img = shade(flat(w, h, c(0.82, 0.80, 0.73)), grain(w, h, 191, 0.10))
    img = shade(img, np.where(rail, 1.0, 1.05 - 0.45 * dist / 5.5))
    img[(yy == 6) | (yy == 57)] *= 0.6
    return img, alpha.astype(np.float32)


def tex_railing():
    """Ringhiera in ferro 1 m x 0,95 m: tondini verticali ogni 12,5 cm e due correnti."""
    w = h = 64
    xx, yy = grid(w, h)
    bar = (xx % 8) < 2
    rail = (yy < 3) | ((yy >= 54) & (yy < 57))
    img = shade(flat(w, h, c(0.13, 0.12, 0.12)), grain(w, h, 195, 0.3))
    rust = np.random.default_rng(196).random((h, w)) > 0.85
    img[rust] = c(0.30, 0.17, 0.10)
    return img, (bar | rail).astype(np.float32)


# Texture che lo sporco lo hanno gia' dentro, o che non devono averlo.
NO_GRIME = {"glass", "glass_broken", "roof", "brick_core", "rubble", "roof_bare"}
NO_DRIPS = {"terrazzo", "planks", "boards", "paving", "ceiling", "wood"}


def save_texture(name, img, alpha=None):
    if RUIN and name not in NO_GRIME:
        img = grime(np.clip(img, 0, 1), sum(map(ord, name)), 1.0, name not in NO_DRIPS)
    img = psx(np.clip(img, 0, 1))
    h, w = img.shape[:2]
    rgba = np.ones((h, w, 4), dtype=np.float32)
    rgba[..., :3] = img
    if alpha is not None:
        rgba[..., 3] = alpha
    path = "{}/{}.png".format(TEX_DIR, name)
    old = bpy.data.images.get(name)
    if old is not None:
        bpy.data.images.remove(old)
    image = bpy.data.images.new(name, w, h, alpha=alpha is not None)
    image.pixels.foreach_set(rgba[::-1].ravel())
    image.filepath_raw = path
    image.file_format = "PNG"
    image.save()
    bpy.data.images.remove(image)
    loaded = bpy.data.images.load(path)
    loaded.name = name
    write_texture_import(path)
    return loaded


def write_texture_import(path):
    """Import Godot senza compressione VRAM: il BC1 impasterebbe pixel cosi' grossi.

    Scritto solo se manca: una volta importato, il file appartiene a Godot (uid compreso).
    """
    target = path + ".import"
    if os.path.exists(target):
        return
    with open(target, "w", encoding="utf-8", newline="\n") as handle:
        handle.write('[remap]\n\nimporter="texture"\ntype="CompressedTexture2D"\n\n[params]\n\n'
                     "compress/mode=0\nmipmaps/generate=true\ndetect_3d/compress_to=0\n")


def build_textures():
    os.makedirs(TEX_DIR, exist_ok=True)
    images = {}
    for i, kind in enumerate(FACADES):
        images["facade_" + kind] = save_texture("facade_" + kind, tex_facade(kind, 200 + i * 50, False))
        images["facade_{}_base".format(kind)] = save_texture(
            "facade_{}_base".format(kind), tex_facade(kind, 200 + i * 50, True))
    simple = {
        "plaster": tex_plaster(),
        "stone": tex_stone(False),
        "stone_blocks": tex_stone(True),
        "dentil": tex_dentil(),
        "ceiling": tex_ceiling(),
        "shutter": tex_shutter(),
        "window": tex_window(),
        "door_ext": tex_door_ext(),
        "door_int": tex_door_int(),
        "wood": tex_wood(),
        "planks": tex_planks(c(0.52, 0.37, 0.22), 301),
        "boards": tex_planks(c(0.27, 0.19, 0.13), 311),
        "terrazzo": tex_terrazzo(),
        "paving": tex_paving(),
        "roof": tex_roof(),
        "shutters": tex_shutters_closed(),
    }
    cutouts = {"grille": tex_grille(), "balustrade": tex_balustrade(), "railing": tex_railing(),
               "glass": tex_glass(0.75 if RUIN else 0.12, False, 501)}
    if RUIN:
        simple["brick_core"] = tex_brick_core()
        simple["rubble"] = tex_rubble()
        cutouts["glass_broken"] = tex_glass(0.85, True, 511)
        cutouts["roof_bare"] = tex_roof_bare()
    for name, img in simple.items():
        images[name] = save_texture(name, img)
    for name, (img, alpha) in cutouts.items():
        images[name] = save_texture(name, img, alpha)
    return images


# =====================================================================================
# MATERIALI
# =====================================================================================

# nome del materiale (= nome della superficie in Godot) -> (texture, metri per ripetizione)
MATERIALS = {
    "facade": ("facade_ochre", (2.0, 3.0)),
    "plaster": ("plaster", (2.0, 3.0)),
    "stone": ("stone", (1.0, 1.0)),
    "stone_blocks": ("stone_blocks", (1.0, 1.0)),
    "dentil": ("dentil", (0.5, 1.0)),
    "ceiling": ("ceiling", (1.0, 1.0)),
    "shutter": ("shutter", (1.0, 1.0)),
    "window": ("window", (1.0, 1.0)),
    "door_ext": ("door_ext", (1.0, 1.0)),
    "door_int": ("door_int", (1.0, 1.0)),
    "wood": ("wood", (1.0, 1.0)),
    "planks": ("planks", (2.0, 2.0)),
    "boards": ("boards", (2.0, 2.0)),
    "terrazzo": ("terrazzo", (2.0, 2.0)),
    "paving": ("paving", (2.0, 2.0)),
    "roof": ("roof", (2.0, 2.0)),
    "grille": ("grille", (1.0, 1.0)),
    "balustrade": ("balustrade", (1.0, 0.9)),
    "railing": ("railing", (1.0, 0.95)),
    "shutters": ("shutters", (1.0, 1.0)),
    "glass": ("glass", (1.0, 1.0)),
    "glass_broken": ("glass_broken", (1.0, 1.0)),
    "brick_core": ("brick_core", (1.0, 1.0)),
    "rubble": ("rubble", (1.0, 1.0)),
    "roof_bare": ("roof_bare", (2.0, 2.0)),
}
CUTOUT = {"grille", "balustrade", "railing", "roof_bare"}    # alfa a soglia (MASK)
BLEND = {"glass", "glass_broken"}                 # alfa sfumata (BLEND)
MATS = {}


def build_materials(images):
    MATS.clear()
    for name, (texture, _) in MATERIALS.items():
        if texture not in images:
            continue
        mat = bpy.data.materials.new(name)
        mat.use_nodes = True
        mat.use_backface_culling = True
        nodes, links = mat.node_tree.nodes, mat.node_tree.links
        bsdf = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")
        tex = nodes.new("ShaderNodeTexImage")
        tex.image = images[texture]
        tex.interpolation = "Closest"
        links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
        bsdf.inputs["Roughness"].default_value = 1.0
        bsdf.inputs["Metallic"].default_value = 0.0
        bsdf.inputs["Specular IOR Level"].default_value = 0.0
        if name in CUTOUT:
            # Alfa -> Math(ROUND) -> BSDF e' il disegno che l'exporter glTF legge come MASK 0,5.
            rnd = nodes.new("ShaderNodeMath")
            rnd.operation = "ROUND"
            links.new(tex.outputs["Alpha"], rnd.inputs[0])
            links.new(rnd.outputs[0], bsdf.inputs["Alpha"])
        elif name in BLEND:
            # Alfa collegata direttamente = alphaMode BLEND nel glTF.
            links.new(tex.outputs["Alpha"], bsdf.inputs["Alpha"])
            try:
                mat.surface_render_method = "BLENDED"
            except AttributeError:
                mat.blend_method = "BLEND"
        MATS[name] = mat


# =====================================================================================
# COSTRUTTORE DI MESH
# =====================================================================================

def newell(pts):
    n = Vector((0, 0, 0))
    for i, p in enumerate(pts):
        q = pts[(i + 1) % len(pts)]
        n.x += (p.y - q.y) * (p.z + q.z)
        n.y += (p.z - q.z) * (p.x + q.x)
        n.z += (p.x - q.x) * (p.y + q.y)
    return n


def box_uv(p, n, mat):
    """Proiezione planare sull'asse dominante della normale, in metri / dimensione texture."""
    tw, th = MATERIALS[mat][1] if mat else (1.0, 1.0)
    ax, ay, az = abs(n.x), abs(n.y), abs(n.z)
    if ay >= ax and ay >= az:
        return (p.x / tw, p.z / th)
    if ax >= az:
        return (p.y / tw, p.z / th)
    return (p.x / tw, p.y / th)


class Builder:
    def __init__(self):
        self.faces = []

    def poly(self, pts, mat, hint, uv=None, uvfn=None):
        """Aggiunge una faccia orientata verso `hint`. UV: esplicite, da funzione o planari."""
        pts = [Vector(p) for p in pts]
        if uv is not None:
            uv = list(uv)
        keep = [i for i, p in enumerate(pts) if (p - pts[i - 1]).length > 1e-5]
        pts = [pts[i] for i in keep]
        if uv is not None:
            uv = [uv[i] for i in keep]
        if len(pts) < 3:
            return
        n = newell(pts)
        if n.length < 1e-8:
            return
        if n.dot(Vector(hint)) < 0:
            pts.reverse()
            n = -n
            if uv is not None:
                uv.reverse()
        if uv is None:
            uv = [uvfn(p) for p in pts] if uvfn else [box_uv(p, n, mat) for p in pts]
        self.faces.append((pts, uv, mat))

    def xz(self, x0, x1, z0, z1, y, mat, hint_y, fit=False):
        if x1 - x0 < 1e-5 or z1 - z0 < 1e-5:
            return
        pts = [(x0, y, z0), (x1, y, z0), (x1, y, z1), (x0, y, z1)]
        uv = [(0, 0), (1, 0), (1, 1), (0, 1)] if fit else None
        self.poly(pts, mat, (0, hint_y, 0), uv)

    def box(self, x0, x1, y0, y1, z0, z1, mat, sides="xXyYzZ", uvfn=None):
        """Parallelepipedo; `sides` elenca le facce da creare (minuscola = lato negativo)."""
        quads = {
            "x": ([(x0, y0, z0), (x0, y1, z0), (x0, y1, z1), (x0, y0, z1)], (-1, 0, 0)),
            "X": ([(x1, y0, z0), (x1, y1, z0), (x1, y1, z1), (x1, y0, z1)], (1, 0, 0)),
            "y": ([(x0, y0, z0), (x1, y0, z0), (x1, y0, z1), (x0, y0, z1)], (0, -1, 0)),
            "Y": ([(x0, y1, z0), (x1, y1, z0), (x1, y1, z1), (x0, y1, z1)], (0, 1, 0)),
            "z": ([(x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0)], (0, 0, -1)),
            "Z": ([(x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1)], (0, 0, 1)),
        }
        for key in sides:
            pts, hint = quads[key]
            self.poly(pts, mat, hint, uvfn=uvfn)


def to_object(name, builder, object_name=None):
    verts, faces, uvs, indices, slots = [], [], [], [], []
    for pts, uv, mat in builder.faces:
        start = len(verts)
        verts.extend(pts)
        faces.append(list(range(start, start + len(pts))))
        uvs.extend(uv)
        if mat not in slots:
            slots.append(mat)
        indices.append(slots.index(mat))
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(verts, [], faces)
    layer = mesh.uv_layers.new(name="UVMap")
    layer.data.foreach_set("uv", [v for uv in uvs for v in uv])
    for slot in slots:
        if slot:
            mesh.materials.append(MATS[slot])
    mesh.polygons.foreach_set("material_index", indices)
    mesh.update()
    obj = bpy.data.objects.new(object_name or name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    return obj


# =====================================================================================
# APERTURE
# =====================================================================================

def opening(x0, x1, z0, chain, **extra):
    """Apertura: `chain` e' il profilo superiore, da (x0, zs) a (x1, zs), con x crescente."""
    op = {"x0": x0, "x1": x1, "z0": z0, "zs": chain[0][1], "chain": chain,
          "top": max(p[1] for p in chain), "floor": [(x0, z0), (x1, z0)]}
    op.update(extra)
    return op


def chain_flat(x0, x1, zt):
    return [(x0, zt), (x1, zt)]


def chain_round(x0, x1, zs, seg=6):
    r, cx = (x1 - x0) / 2, (x0 + x1) / 2
    return [(cx - r * math.cos(math.pi * i / seg), zs + r * math.sin(math.pi * i / seg))
            for i in range(seg + 1)]


OGEE = [(1.0, 0.0), (0.97, 0.26), (0.76, 0.52), (0.42, 0.66), (0.16, 0.80), (0.0, 1.0)]


def chain_ogee(x0, x1, zs, rise):
    """Arco inflesso del gotico veneziano: convesso alle reni, concavo verso la cuspide."""
    r, cx = (x1 - x0) / 2, (x0 + x1) / 2
    left = [(cx - u * r, zs + v * rise) for u, v in OGEE]
    right = [(cx + u * r, zs + v * rise) for u, v in reversed(OGEE[:-1])]
    return left + right


def breach(cx, cz, rx, rz, seed, steps=5):
    """Breccia frastagliata: due profili irregolari (sopra e sotto) fra gli stessi estremi.

    Dove il profilo inferiore scende sotto il pavimento viene tagliato a quota 0: la breccia
    diventa un varco da cui si passa.
    """
    rng = np.random.default_rng(seed)
    x0, x1 = cx - rx, cx + rx
    top, bottom = [(x0, max(cz, 0.0))], [(x0, max(cz, 0.0))]
    for i in range(1, steps):
        t = i / steps
        x = x0 + 2 * rx * t + rng.uniform(-0.06, 0.06)
        bulge = math.sin(math.pi * t) ** 0.6
        top.append((x, cz + rz * bulge * rng.uniform(0.7, 1.15)))
        low = cz - rz * bulge * rng.uniform(0.7, 1.15)
        # sotto i 35 cm la soglia sparisce: o si scavalca un bordo vero o si passa a filo
        bottom.append((x + rng.uniform(-0.05, 0.05), low if low >= 0.35 else 0.0))
    top.append((x1, max(cz, 0.0)))
    bottom.append((x1, max(cz, 0.0)))
    op = opening(x0, x1, 0.0, top, raw=True)
    op["floor"] = bottom
    return op


def outline(op):
    """Contorno chiuso, antiorario visto da fuori (x a destra, z in alto)."""
    return list(op["floor"]) + list(reversed(op["chain"]))


def face_with_openings(b, xl, xr, zb, zt, ops, y, hint_y, mat):
    x = xl
    for op in sorted(ops, key=lambda o: o["x0"]):
        b.xz(x, op["x0"], zb, zt, y, mat, hint_y)
        for p, q in zip(op["floor"], op["floor"][1:]):
            b.poly([(p[0], y, zb), (q[0], y, zb), (q[0], y, q[1]), (p[0], y, p[1])], mat, (0, hint_y, 0))
        chain = op["chain"]
        for p, q in zip(chain, chain[1:]):
            b.poly([(p[0], y, p[1]), (q[0], y, q[1]), (q[0], y, zt), (p[0], y, zt)], mat, (0, hint_y, 0))
        x = op["x1"]
    b.xz(x, xr, zb, zt, y, mat, hint_y)


def reveal(b, loop, y0, y1, mat, skip_bottom):
    """Imbotte: lo spessore del muro dentro l'apertura."""
    for i, p in enumerate(loop):
        q = loop[(i + 1) % len(loop)]
        # Mai una faccia a quota pavimento: farebbe z-fighting col pavimento della cella.
        if (skip_bottom and i == 0) or (p[1] <= 1e-4 and q[1] <= 1e-4):
            continue
        dx, dz = q[0] - p[0], q[1] - p[1]
        b.poly([(p[0], y0, p[1]), (q[0], y0, q[1]), (q[0], y1, q[1]), (p[0], y1, p[1])],
               mat, (-dz, 0, dx))


def offset_loop(loop, width):
    out = []
    count = len(loop)
    for i, p in enumerate(loop):
        a, q = loop[i - 1], loop[(i + 1) % count]
        n1 = Vector((p[1] - a[1], -(p[0] - a[0]))).normalized()
        n2 = Vector((q[1] - p[1], -(q[0] - p[0]))).normalized()
        m = (n1 + n2).normalized()
        k = width / max(0.35, m.dot(n1))
        out.append((p[0] + m.x * k, p[1] + m.y * k))
    return out


def trim(b, loop, width, y_wall, y_front, mat, skip_bottom):
    """Cornice attorno all'apertura: fascia frontale + costa esterna."""
    outer = offset_loop(loop, width)
    if skip_bottom:
        outer = [(x, max(z, loop[0][1])) for x, z in outer]
    side = 1 if y_front > y_wall else -1
    for i, p in enumerate(loop):
        if skip_bottom and i == 0:
            continue
        j = (i + 1) % len(loop)
        q, po, qo = loop[j], outer[i], outer[j]
        b.poly([(p[0], y_front, p[1]), (q[0], y_front, q[1]), (qo[0], y_front, qo[1]),
                (po[0], y_front, po[1])], mat, (0, side, 0))
        dx, dz = qo[0] - po[0], qo[1] - po[1]
        b.poly([(po[0], y_front, po[1]), (qo[0], y_front, qo[1]), (qo[0], y_wall, qo[1]),
                (po[0], y_wall, po[1])], mat, (dz, 0, -dx))


def pane(b, op, y, mat, hint_y, inset=0.0):
    """Riempie l'apertura (vetro, anta, grata) con UV adattate al suo rettangolo."""
    x0, x1, z0, zs, top = op["x0"], op["x1"], op["z0"], op["zs"], op["top"]

    def uv(p):
        return ((p[0] - x0) / (x1 - x0), (p[1] - z0) / (top - z0))

    def add(points):
        b.poly([(p[0], y, p[1]) for p in points], mat, (0, hint_y, 0), [uv(p) for p in points])

    add([(x0, z0), (x1, z0), (x1, zs), (x0, zs)])
    for p, q in zip(op["chain"], op["chain"][1:]):
        add([(p[0], zs), (q[0], zs), q, p])


def double_pane(b, op, y, mat):
    pane(b, op, y, mat, -1)
    pane(b, op, y + 0.012, mat, 1)


def shutter(target, xa, xb, z0, z1, tilt=0.0, pivot=None):
    """Scuro aperto contro il muro: una scatola sottile davanti alla cornice.

    `tilt` (gradi) lo fa pendere attorno a `pivot` (x, z): il cardine rimasto.
    """
    b = Builder()
    y0, y1 = -0.085, -0.045
    b.poly([(xa, y0, z0), (xb, y0, z0), (xb, y0, z1), (xa, y0, z1)], "shutter", (0, -1, 0),
           [(0, 0), (1, 0), (1, 1), (0, 1)])
    edge = [(0.0, 0.0), (0.06, 0.0), (0.06, 1.0), (0.0, 1.0)]
    b.poly([(xa, y0, z0), (xa, y1, z0), (xa, y1, z1), (xa, y0, z1)], "shutter", (-1, 0, 0), edge)
    b.poly([(xb, y0, z0), (xb, y1, z0), (xb, y1, z1), (xb, y0, z1)], "shutter", (1, 0, 0), edge)
    b.poly([(xa, y0, z1), (xb, y0, z1), (xb, y1, z1), (xa, y1, z1)], "shutter", (0, 0, 1), edge)
    b.poly([(xa, y0, z0), (xb, y0, z0), (xb, y1, z0), (xa, y1, z0)], "shutter", (0, 0, -1), edge)
    if tilt:
        ca, sa = math.cos(math.radians(tilt)), math.sin(math.radians(tilt))
        for pts, _, _ in b.faces:
            for p in pts:
                dx, dz = p.x - pivot[0], p.z - pivot[1]
                p.x, p.z = pivot[0] + dx * ca - dz * sa, pivot[1] + dx * sa + dz * ca
    target.faces.extend(b.faces)


def boards(b, op, seed):
    """Assi inchiodate di traverso sull'apertura (due facce: si vedono anche da dentro)."""
    rng = np.random.default_rng(seed)
    xa, xb = op["x0"] - 0.16, op["x1"] + 0.16
    count = 4
    for i in range(count):
        zc = op["z0"] + (op["top"] - op["z0"]) * (i + 0.6) / (count + 0.3)
        a = math.radians(rng.uniform(-9, 9))
        cx = (xa + xb) / 2 + rng.uniform(-0.05, 0.05)
        half, hh = (xb - xa) / 2 * rng.uniform(0.85, 1.0), 0.085
        corners = []
        for sx, sz in ((-1, -1), (1, -1), (1, 1), (-1, 1)):
            lx, lz = sx * half, sz * hh
            corners.append((cx + lx * math.cos(a) - lz * math.sin(a), zc + lx * math.sin(a) + lz * math.cos(a)))
        uv = [(0, 0), (2 * half, 0), (2 * half, 0.17), (0, 0.17)]
        b.poly([(x, -0.075, z) for x, z in corners], "wood", (0, -1, 0), uv)
        b.poly([(x, -0.050, z) for x, z in corners], "wood", (0, 1, 0), uv)


def simple_chain(chain):
    """Profilo superiore di un'apertura ridotto a 4 punti al massimo (per le collisioni).

    I punti sono presi dal profilo vero: il varco che ne esce e' inscritto in quello visibile.
    """
    count = len(chain)
    if count <= 4:
        return list(chain)
    picks = sorted({0, round((count - 1) / 3), round(2 * (count - 1) / 3), count - 1})
    return [chain[i] for i in picks]


def collider_wall(ops, xl, xr, zt, y0, y1, blocked):
    """Collisione semplificata di un muro con aperture: le due facce e, solo per le aperture
    da cui si passa davvero, il varco con la sua imbotte.

    Dove `blocked(op)` e' vero (vetro integro, scuri chiusi, grata, assi) il muro e' pieno:
    per la fisica una finestra chiusa e' un muro. Una finestra passa cosi' da 150-220
    triangoli a 4, una porta da 30-100 a 18-30.
    """
    c = Builder()
    simple = [opening(op["x0"], op["x1"], op["z0"], simple_chain(op["chain"]))
              for op in ops if not blocked(op)]
    face_with_openings(c, xl, xr, 0, zt, simple, y0, -1, None)
    face_with_openings(c, xl, xr, 0, zt, simple, y1, 1, None)
    for op in simple:
        reveal(c, outline(op), y0, y1, None, op["z0"] <= 0)
    return c


def op_blocked(op):
    """L'apertura ferma chi passa (e i colpi)? Vetro integro, scuri chiusi, grata o assi."""
    glazed = op.get("glass", True) and not op.get("broken")
    return bool(glazed or op.get("shut") or op.get("grille") or op.get("boarded"))


GLASS = "window"  # materiale dei vetri: opaco nel kit pulito, "glass" (trasparente) nella rovina
TRIM_W = 0.10     # larghezza della cornice in pietra
TRIM_D = 0.04     # sporgenza della cornice


def ext_wall(ops=(), width=W):
    """Muro esterno: faccia d'intonaco fuori, d'interno dentro, e le aperture rifinite."""
    b = Builder()
    b.nocol = Builder()   # parti senza collisione (vetri rotti, scuri penzolanti)
    xl, xr = -width / 2, width / 2
    face_with_openings(b, xl, xr, 0, H, ops, 0.0, -1, "facade")
    face_with_openings(b, xl, xr, 0, H, ops, T, 1, "plaster")
    for op in ops:
        loop = outline(op)
        if op.get("raw"):
            reveal(b, loop, 0.0, T, "brick_core", False)
            continue
        door = op["z0"] <= 0
        reveal(b, loop, -TRIM_D, T, "stone", door)
        trim(b, loop, TRIM_W, 0.0, -TRIM_D, "stone", door)
        if not door:
            b.box(op["x0"] - 0.14, op["x1"] + 0.14, -0.11, -TRIM_D, op["z0"] - 0.08, op["z0"],
                  "stone", "xXyzZ")
        if op.get("broken"):
            double_pane(b.nocol, op, 0.10, "glass_broken")
        elif op.get("glass", True):
            double_pane(b, op, 0.10, GLASS)
        if op.get("grille"):
            double_pane(b, op, -0.02, "grille")
        if op.get("shut"):
            double_pane(b, op, -0.03, "shutters")
        if op.get("boarded"):
            boards(b, op, 77)
        half = (op["x1"] - op["x0"]) / 2
        if op.get("shutters"):
            shutter(b, op["x0"] - half, op["x0"], op["z0"], op["zs"])
            shutter(b, op["x1"], op["x1"] + half, op["z0"], op["zs"])
        if op.get("hanging"):
            # resta un solo scuro, appeso al cardine alto
            shutter(b.nocol, op["x1"], op["x1"] + half, op["z0"], op["zs"], -14.0,
                    (op["x1"], op["zs"]))
    # Le brecce (`raw`) hanno un profilo irregolare anche in basso: tengono la mesh intera.
    if ops and not any(op.get("raw") for op in ops):
        b.col = collider_wall(ops, xl, xr, H, 0.0, T, op_blocked)
    return b


# =====================================================================================
# MODULI
# =====================================================================================

def m_ext_wall():
    return ext_wall()


def m_ext_window_rect():
    return ext_wall([opening(-0.45, 0.45, 0.9, chain_flat(-0.45, 0.45, 2.35), shutters=True)])


def m_ext_window_arch():
    return ext_wall([opening(-0.45, 0.45, 0.9, chain_round(-0.45, 0.45, 1.95), shutters=True)])


def m_ext_window_gothic():
    return ext_wall([opening(-0.45, 0.45, 0.9, chain_ogee(-0.45, 0.45, 1.80, 0.62), shutters=True)])


def m_ext_window_high():
    """Finestrella alta (1,85-2,55 m) per i muri dei vani scala: sta sopra il pianerottolo."""
    return ext_wall([opening(-0.35, 0.35, 1.85, chain_flat(-0.35, 0.35, 2.55))])


def m_ext_window_grille():
    return ext_wall([opening(-0.38, 0.38, 1.25, chain_flat(-0.38, 0.38, 2.05), grille=True)])


def m_ext_door():
    return ext_wall([opening(-0.6, 0.6, 0.0, chain_flat(-0.6, 0.6, 2.3), glass=False)])


def m_ext_portal():
    return ext_wall([opening(-0.65, 0.65, 0.0, chain_round(-0.65, 0.65, 1.80, 8), glass=False)])


def m_ext_balcony():
    """Portafinestra gotica con poggiolo in pietra su due mensole."""
    b = ext_wall([opening(-0.45, 0.45, 0.0, chain_ogee(-0.45, 0.45, 1.80, 0.62))])
    b.box(-0.82, 0.82, -0.56, 0.0, -0.12, 0.0, "stone", "xXyzZ")
    for cx in (-0.55, 0.55):
        x0, x1 = cx - 0.07, cx + 0.07
        b.poly([(x0, 0, -0.12), (x0, -0.46, -0.12), (x0, 0, -0.50)], "stone", (-1, 0, 0))
        b.poly([(x1, 0, -0.12), (x1, -0.46, -0.12), (x1, 0, -0.50)], "stone", (1, 0, 0))
        b.poly([(x0, -0.46, -0.12), (x1, -0.46, -0.12), (x1, 0, -0.50), (x0, 0, -0.50)], "stone",
               (0, -1, -1))
    # balaustra: due facce per lato (vedi la nota sulle collisioni in testa al file)
    for y, hint in ((-0.52, -1), (-0.508, 1)):
        b.poly([(-0.75, y, 0), (0.75, y, 0), (0.75, y, 0.9), (-0.75, y, 0.9)], "balustrade",
               (0, hint, 0), [(0, 0), (1.5, 0), (1.5, 1), (0, 1)])
    for sx in (-1, 1):
        for x, hint in ((sx * 0.75, sx), (sx * 0.738, -sx)):
            b.poly([(x, -0.52, 0), (x, -0.02, 0), (x, -0.02, 0.9), (x, -0.52, 0.9)], "balustrade",
                   (hint, 0, 0), [(0, 0), (0.5, 0), (0.5, 1), (0, 1)])
    # collisione: la soletta del poggiolo e la balaustra (le mensole no)
    b.col.box(-0.82, 0.82, -0.56, 0.0, -0.12, 0.0, None, "xXyzZ")
    for y, hint in ((-0.52, -1), (-0.508, 1)):
        b.col.poly([(-0.75, y, 0), (0.75, y, 0), (0.75, y, 0.9), (-0.75, y, 0.9)], None, (0, hint, 0))
    for sx in (-1, 1):
        for x, hint in ((sx * 0.75, sx), (sx * 0.738, -sx)):
            b.col.poly([(x, -0.52, 0), (x, -0.02, 0), (x, -0.02, 0.9), (x, -0.52, 0.9)], None, (hint, 0, 0))
    return b


def m_ext_quadrifora():
    """Quadrifora del piano nobile: modulo DOPPIO (4 m), quattro luci gotiche in un campo di pietra."""
    b = Builder()
    lw, pier = 0.62, 0.16
    start = -(4 * lw + 3 * pier) / 2
    ops = []
    for i in range(4):
        x0 = start + i * (lw + pier)
        ops.append(opening(x0, x0 + lw, 0.9, chain_ogee(x0, x0 + lw, 1.85, 0.55)))
    fx, fz0, fz1, fy = 1.62, 0.78, 2.56, -0.03
    b.xz(-2.0, -fx, 0, H, 0.0, "facade", -1)
    b.xz(fx, 2.0, 0, H, 0.0, "facade", -1)
    b.xz(-fx, fx, 0, fz0, 0.0, "facade", -1)
    b.xz(-fx, fx, fz1, H, 0.0, "facade", -1)
    face_with_openings(b, -fx, fx, fz0, fz1, ops, fy, -1, "stone")
    b.box(-fx, fx, fy, 0.0, fz0, fz1, "stone", "xXzZ")
    b.box(-fx - 0.08, fx + 0.08, -0.12, 0.0, fz0 - 0.08, fz0, "stone", "xXyzZ")
    face_with_openings(b, -2.0, 2.0, 0, H, ops, T, 1, "plaster")
    for op in ops:
        reveal(b, outline(op), fy, T, "stone", False)
        double_pane(b, op, 0.10, GLASS)
    return b


def m_ext_plinth():
    """Zoccolo in pietra d'Istria del piano terra."""
    b = Builder()
    b.box(-1, 1, -0.05, 0.0, 0.0, 0.5, "stone_blocks", "yZ")
    return b


def m_ext_stringcourse():
    """Marcapiano: va posato alla quota del solaio."""
    b = Builder()
    b.box(-1, 1, -0.05, 0.0, -0.08, 0.08, "stone", "yzZ")
    return b


# Profilo del cornicione: (sporgenza, quota) rispetto al filo superiore del muro.
CORNICE = [((0.0, -0.38), (0.08, -0.38), "stone"),
           ((0.08, -0.38), (0.08, -0.30), "stone"),
           ((0.08, -0.30), (0.26, -0.22), "dentil"),
           ((0.26, -0.22), (0.26, -0.17), "stone"),
           ((0.26, -0.17), (0.36, -0.17), "wood"),
           ((0.36, -0.17), (0.36, -0.144), "roof"),
           ((0.36, -0.144), (0.0, 0.0), "roof")]


def m_ext_cornice():
    """Cornicione con gronda di coppi: va posato al filo superiore dell'ultimo piano."""
    b = Builder()
    for (o0, z0), (o1, z1), mat in CORNICE:
        pts = [(-1, -o0, z0), (1, -o0, z0), (1, -o1, z1), (-1, -o1, z1)]
        if mat == "roof" and o1 < o0:
            b.poly(pts, mat, (0, 0, 1), uvfn=lambda p: (p.x / 2, p.y / 2))
        elif mat == "dentil":
            b.poly(pts, mat, (0, -1, -1), [(-2, 1), (2, 1), (2, 0), (-2, 0)])
        elif abs(z1 - z0) < 1e-6:
            b.poly(pts, mat, (0, 0, -1))
        else:
            b.poly(pts, mat, (0, -1, 0))
    return b


def m_ext_cornice_corner():
    """Angolo CONVESSO del cornicione. Origine sullo spigolo; i muri partono verso +X e +Y."""
    b = Builder()
    for (o0, z0), (o1, z1), mat in CORNICE:
        a0, a1 = (0, -o0, z0), (0, -o1, z1)
        d0, d1 = (-o0, -o0, z0), (-o1, -o1, z1)
        e0, e1 = (-o0, 0, z0), (-o1, 0, z1)
        if mat == "roof" and o1 < o0:
            b.poly([d0, a0, a1, d1], mat, (0, 0, 1), uvfn=lambda p: (p.x / 2, p.y / 2))
            b.poly([e0, d0, d1, e1], mat, (0, 0, 1), uvfn=lambda p: (p.y / 2, p.x / 2))
            continue
        flat_face = abs(z1 - z0) < 1e-6
        hint_a = (0, 0, -1) if flat_face else (0, -1, -0.5 if mat == "dentil" else 0)
        hint_e = (0, 0, -1) if flat_face else (-1, 0, -0.5 if mat == "dentil" else 0)
        uv_a = uv_e = None
        if mat == "dentil":
            uv_a = [(-o0 / 0.5, 1), (0, 1), (0, 0), (-o1 / 0.5, 0)]
            uv_e = [(0, 1), (o0 / 0.5, 1), (o1 / 0.5, 0), (0, 0)]
        b.poly([d0, a0, a1, d1], mat, hint_a, uv_a)
        b.poly([e0, d0, d1, e1], mat, hint_e, uv_e)
    return b


def m_ext_corner_convex():
    """Cantonale bugnato per gli spigoli convessi. Origine sullo spigolo; muri verso +X e +Y."""
    b = Builder()
    b.box(-0.06, 0.36, -0.06, 0.36, 0, H, "stone_blocks", "xy")
    b.poly([(0.36, -0.06, 0), (0.36, 0, 0), (0.36, 0, H), (0.36, -0.06, H)], "stone_blocks", (1, 0, 0))
    b.poly([(-0.06, 0.36, 0), (0, 0.36, 0), (0, 0.36, H), (-0.06, 0.36, H)], "stone_blocks", (0, 1, 0))
    return b


def m_ext_corner_concave():
    """Riempie, dal lato delle stanze, la tacca lasciata da due muri a spigolo concavo.

    Origine sullo spigolo esterno; i muri partono verso +X (esterno a -Y) e verso -Y
    (esterno a -X... cioe' la stanza sta nel quadrante x<0, y>0).
    """
    b = Builder()
    b.poly([(0, T, 0), (-T, T, 0), (-T, T, H), (0, T, H)], "plaster", (0, 1, 0))
    b.poly([(-T, T, 0), (-T, 0, 0), (-T, 0, H), (-T, T, H)], "plaster", (-1, 0, 0))
    return b


# ---- tetto (moduli di cella: origine al centro, base a z = 0) -------------------------

def roof_u(p):
    return (p.x / 2, p.y / 2)


def roof_v(p):
    return (p.y / 2, p.x / 2)


R = 2 * PITCH   # 0,8 m: dislivello di una cella


def m_roof_slope():
    """Falda: gronda a -Y (= +Z di Godot, come la faccia esterna dei muri)."""
    b = Builder()
    b.poly([(-1, -1, 0), (1, -1, 0), (1, 1, R), (-1, 1, R)], "roof", (0, 0, 1), uvfn=roof_u)
    return b


def m_roof_corner_outer():
    """Displuvio: angolo basso a (-X, -Y)."""
    b = Builder()
    b.poly([(-1, -1, 0), (1, -1, 0), (1, 1, R)], "roof", (0, 0, 1), uvfn=roof_u)
    b.poly([(-1, -1, 0), (1, 1, R), (-1, 1, 0)], "roof", (0, 0, 1), uvfn=roof_v)
    return b


def m_roof_corner_inner():
    """Compluvio (spigolo concavo): angolo basso a (-X, -Y), alti i lati +X e +Y."""
    b = Builder()
    b.poly([(-1, -1, 0), (1, -1, R), (1, 1, R)], "roof", (0, 0, 1), uvfn=roof_v)
    b.poly([(-1, -1, 0), (1, 1, R), (-1, 1, R)], "roof", (0, 0, 1), uvfn=roof_u)
    return b


def m_roof_ridge():
    """Colmo lungo X, per le strisce larghe una cella."""
    b = Builder()
    b.poly([(-1, -1, 0), (1, -1, 0), (1, 0, R / 2), (-1, 0, R / 2)], "roof", (0, 0, 1), uvfn=roof_u)
    b.poly([(1, 1, 0), (-1, 1, 0), (-1, 0, R / 2), (1, 0, R / 2)], "roof", (0, 0, 1), uvfn=roof_u)
    return b


def m_roof_ridge_end():
    """Testata a padiglione del colmo: lato chiuso a -X, il colmo prosegue verso +X."""
    b = Builder()
    b.poly([(-1, -1, 0), (0, 0, R / 2), (-1, 1, 0)], "roof", (0, 0, 1), uvfn=roof_v)
    b.poly([(-1, -1, 0), (1, -1, 0), (1, 0, R / 2), (0, 0, R / 2)], "roof", (0, 0, 1), uvfn=roof_u)
    b.poly([(1, 1, 0), (-1, 1, 0), (0, 0, R / 2), (1, 0, R / 2)], "roof", (0, 0, 1), uvfn=roof_u)
    return b


def m_roof_peak():
    """Piramide: chiude una cella isolata."""
    b = Builder()
    apex = (0, 0, R / 2)
    b.poly([(-1, -1, 0), (1, -1, 0), apex], "roof", (0, 0, 1), uvfn=roof_u)
    b.poly([(1, 1, 0), (-1, 1, 0), apex], "roof", (0, 0, 1), uvfn=roof_u)
    b.poly([(1, -1, 0), (1, 1, 0), apex], "roof", (0, 0, 1), uvfn=roof_v)
    b.poly([(-1, 1, 0), (-1, -1, 0), apex], "roof", (0, 0, 1), uvfn=roof_v)
    return b


def m_chimney():
    """Camino veneziano a campana rovesciata. Affonda 0,6 m per posarsi su qualunque falda."""
    b = Builder()
    s, f = 0.25, 0.46
    b.box(-s, s, -s, s, -0.6, 1.2, "facade", "xXyY")
    for sx, sy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
        tx, ty = -sy, sx
        lo = [(sx * s + tx * s, sy * s + ty * s, 1.2), (sx * s - tx * s, sy * s - ty * s, 1.2)]
        hi = [(sx * f - tx * f, sy * f - ty * f, 1.75), (sx * f + tx * f, sy * f + ty * f, 1.75)]
        top = [(p[0], p[1], 1.88) for p in hi]
        b.poly(lo + hi, "facade", (sx, sy, -0.3))
        b.poly([hi[1], hi[0], top[0], top[1]], "stone", (sx, sy, 0))
        b.poly([top[1], top[0], (0, 0, 2.08)], "roof", (sx, sy, 1),
               uvfn=lambda p, tx=tx, ty=ty: ((p.x * tx + p.y * ty) / 2, p.z))
    return b


# ---- interni ---------------------------------------------------------------------------

def int_wall(ops=(), frame=None):
    b = Builder()
    y0, y1 = -TI / 2, TI / 2
    face_with_openings(b, -1, 1, 0, HI, ops, y0, -1, "plaster")
    face_with_openings(b, -1, 1, 0, HI, ops, y1, 1, "plaster")
    for op in ops:
        loop = outline(op)
        if op.get("raw"):
            reveal(b, loop, y0, y1, "brick_core", False)
            continue
        if frame:
            reveal(b, loop, y0 - 0.02, y1 + 0.02, frame, True)
            trim(b, loop, 0.08, y0, y0 - 0.02, frame, True)
            trim(b, loop, 0.08, y1, y1 + 0.02, frame, True)
        else:
            reveal(b, loop, y0, y1, "plaster", True)
    if ops and not any(op.get("raw") for op in ops):
        b.col = collider_wall(ops, -1, 1, HI, y0, y1, lambda op: False)
    return b


def m_int_wall():
    return int_wall()


def m_int_door():
    # 1,2 m: il generatore stringe i moduli fino al 75%, e a 0,9 m deve ancora passarci
    # una capsula larga 0,7 m senza doverla infilare al centimetro.
    return int_wall([opening(-0.6, 0.6, 0.0, chain_flat(-0.6, 0.6, 2.1))], "wood")


def m_int_arch():
    return int_wall([opening(-0.7, 0.7, 0.0, chain_round(-0.7, 0.7, 1.55, 8))])


def m_int_post():
    """Pilastrino: copre incroci, angoli e testate libere dei muri interni."""
    b = Builder()
    b.box(-0.11, 0.11, -0.11, 0.11, 0, HI, "plaster", "xXyY")
    return b


def floor(mat):
    b = Builder()
    b.poly([(-1, -1, 0), (1, -1, 0), (1, 1, 0), (-1, 1, 0)], mat, (0, 0, 1))
    return b


def m_floor_terrazzo():
    return floor("terrazzo")


def m_floor_planks():
    return floor("planks")


def m_floor_paving():
    return floor("paving")


def m_floor_edge():
    """Testa del solaio (30 cm): chiude il bordo attorno ai vani scala. Modulo di bordo."""
    b = Builder()
    b.xz(-1, 1, -0.3, 0.0, 0.0, "stone", -1)
    return b


def m_ceiling_plaster():
    b = Builder()
    b.poly([(-1, -1, HI), (1, -1, HI), (1, 1, HI), (-1, 1, HI)], "ceiling", (0, 0, -1))
    return b


def m_ceiling_beams():
    """Soffitto alla veneziana: tavolato scuro e travi a vista lungo Y."""
    b = Builder()
    b.poly([(-1, -1, HI), (1, -1, HI), (1, 1, HI), (-1, 1, HI)], "boards", (0, 0, -1))
    for cx in (-0.75, -0.25, 0.25, 0.75):
        b.box(cx - 0.06, cx + 0.06, -1, 1, HI - 0.16, HI, "wood", "xXz",
              uvfn=lambda p: (p.y, p.x + p.z))
    return b


STEP_RISE = H / 16     # 18,75 cm
STEP_RUN = 0.25
STAIR_SLAB = 0.2       # spessore della soletta sotto i gradini
# Rientro della scala a U dai tre lati del vano che danno su un muro. Un muro esterno ha la
# faccia esterna sul bordo della cella e il vetro 10-11 cm piu' dentro: a filo cella la scala
# sbucava in facciata e attraversava i vetri. A 13 cm resta tutta dietro il vetro, e da un
# tramezzo (faccia a 8 cm) dista 5 cm.
STAIR_INSET = 0.13
# La rampa dritta puo' avere il pavimento di fianco, quindi rientra solo di 2 cm: quanto basta
# a non essere complanare con la faccia esterna di un muro cieco.
STAIR_SLIVER = 0.02


def flight(b, x0, x1, y_start, direction, z_base, steps):
    """Rampa a soletta: gradini sopra, intradosso inclinato sotto, fianchi chiusi.

    Larga quanto la cella (nessuna fessura col pavimento accanto) e NON piena fino a terra:
    le rampe si impilano nello stesso vano senza togliere l'altezza a quella sotto.
    `direction` = +1 sale verso +Y, -1 verso -Y.
    """
    slope = STEP_RISE / STEP_RUN

    def soffit(d):
        return z_base + d * slope - STAIR_SLAB

    for k in range(steps):
        d0, d1 = k * STEP_RUN, (k + 1) * STEP_RUN
        y0, y1 = y_start + direction * d0, y_start + direction * d1
        z0, z1 = z_base + k * STEP_RISE, z_base + (k + 1) * STEP_RISE
        b.poly([(x0, y0, z0), (x1, y0, z0), (x1, y0, z1), (x0, y0, z1)], "stone", (0, -direction, 0))
        b.poly([(x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1)], "stone", (0, 0, 1))
        for x, hint in ((x0, -1), (x1, 1)):
            b.poly([(x, y0, soffit(d0)), (x, y1, soffit(d1)), (x, y1, z1), (x, y0, z1)], "stone",
                   (hint, 0, 0))
    length = steps * STEP_RUN
    y_end = y_start + direction * length
    b.poly([(x0, y_start, soffit(0)), (x1, y_start, soffit(0)), (x1, y_end, soffit(length)),
            (x0, y_end, soffit(length))], "ceiling", (0, 0, -1))
    b.poly([(x0, y_start, soffit(0)), (x1, y_start, soffit(0)), (x1, y_start, z_base),
            (x0, y_start, z_base)], "stone", (0, -direction, 0))


def prism(profile, x0, x1):
    """Solido di collisione: `profile` (y, z) convesso, estruso lungo X."""
    b = Builder()
    cy = sum(p[0] for p in profile) / len(profile)
    cz = sum(p[1] for p in profile) / len(profile)
    b.poly([(x0, y, z) for y, z in profile], None, (-1, 0, 0))
    b.poly([(x1, y, z) for y, z in profile], None, (1, 0, 0))
    for i, (py, pz) in enumerate(profile):
        qy, qz = profile[(i + 1) % len(profile)]
        b.poly([(x0, py, pz), (x1, py, pz), (x1, qy, qz), (x0, qy, qz)], None,
               (0, (py + qy) / 2 - cy, (pz + qz) / 2 - cz))
    return b


def ramp(x0, x1, y_start, direction, z_base, steps):
    """Rampa convessa lungo la linea dei nasi: si sale senza inciampare nei gradini."""
    length, height = steps * STEP_RUN, steps * STEP_RISE
    local = [(0, 0), (length - STEP_RUN, height), (length, height), (length, height - STAIR_SLAB),
             (0, -STAIR_SLAB)]
    return prism([(y_start + direction * d, z_base + z) for d, z in local], x0, x1)


RAIL_H = 0.95          # altezza della ringhiera sulla linea di base (pavimento o rampa)
RAIL_T = 0.06          # ingombro in pianta di corrimano e montanti


def rail_slab(target, a, c):
    """Lastra piena che fa da collisione a un tratto di ringhiera (6 facce, convessa)."""
    a, c = Vector(a), Vector(c)
    d = Vector((c.x - a.x, c.y - a.y, 0)).normalized()
    n = Vector((-d.y, d.x, 0)) * (RAIL_T / 2)
    up = Vector((0, 0, RAIL_H + 0.04))
    target.poly([a - n, c - n, c - n + up, a - n + up], None, -n)
    target.poly([a + n, c + n, c + n + up, a + n + up], None, n)
    target.poly([a - n, a + n, a + n + up, a - n + up], None, -d)
    target.poly([c - n, c + n, c + n + up, c - n + up], None, d)
    target.poly([a - n + up, c - n + up, c + n + up, a + n + up], None, (0, 0, 1))
    target.poly([a - n, c - n, c + n, a + n], None, (0, 0, -1))


def railing(b, a, c, post_a=True, post_c=True):
    """Ringhiera da `a` a `c` (punti della linea di base, lungo X o lungo Y, anche in salita):
    pannello di tondini a due facce, corrimano di legno, montanti agli estremi.

    Ritorna il solido di collisione: una lastra convessa, perche' un pannello traforato
    disegnato a texture non ferma nessuno e un trimesh sottile si attraversa di corsa.
    """
    a, c = Vector(a), Vector(c)
    flat_run = Vector((c.x - a.x, c.y - a.y, 0))
    length = flat_run.length
    d = flat_run / length
    n = Vector((-d.y, d.x, 0))
    half = n * (RAIL_T / 2)
    up = Vector((0, 0, 1))
    for sign in (-1, 1):
        o = n * (0.006 * sign)
        b.poly([a + o, c + o, c + o + up * RAIL_H, a + o + up * RAIL_H], "railing", n * sign,
               [(0, 0), (length, 0), (length, 1), (0, 1)])
    lo, hi = up * (RAIL_H - 0.02), up * (RAIL_H + 0.04)
    b.poly([a - half + hi, c - half + hi, c + half + hi, a + half + hi], "wood", (0, 0, 1))
    b.poly([a - half + lo, c - half + lo, c + half + lo, a + half + lo], "wood", (0, 0, -1))
    for sign in (-1, 1):
        s = half * sign
        b.poly([a + s + lo, c + s + lo, c + s + hi, a + s + hi], "wood", n * sign)
    b.poly([a - half + lo, a + half + lo, a + half + hi, a - half + hi], "wood", -d)
    b.poly([c - half + lo, c + half + lo, c + half + hi, c - half + hi], "wood", d)
    for base, inward, wanted in ((a, d, post_a), (c, -d, post_c)):
        if not wanted:
            continue
        p, q = base - half, base + half + inward * RAIL_T
        b.box(min(p.x, q.x), max(p.x, q.x), min(p.y, q.y), max(p.y, q.y), base.z,
              base.z + RAIL_H + 0.08, "wood", "xXyYZ")
    solid = Builder()
    rail_slab(solid, a, c)
    return solid


def m_stair_rail():
    """Parapetto attorno al foro di un vano scala: modulo di bordo da 2 m, come `floor_edge`.

    Sta sul lato del pavimento (-Z in Godot), a filo del foro; il vuoto e' a +Z.
    """
    b = Builder()
    a, c = (-1, RAIL_T / 2, 0), (1, RAIL_T / 2, 0)
    railing(b, a, c)
    b.col = Builder()
    rail_slab(b.col, a, c)
    return b


def stair_straight(part="all"):
    """Rampa dritta: 1 x 2 celle (4 m verso +Y), sale di un piano.

    Arriva sul bordo opposto a quello di partenza: la cella DOPO l'arrivo dev'essere pavimento
    del piano di sopra. Se li' c'e' un muro, usare `stair_u`.
    `part`: "all" (rampa e due ringhiere), "core" (sola rampa), "rail_l" / "rail_r" (una sola
    ringhiera, come modulo a se'): contro un muro la ringhiera non va messa.
    """
    b = Builder()
    half = 1 - STAIR_SLIVER
    solids = {}
    if part in ("all", "core"):
        flight(b, -half, half, 0.0, 1, 0.0, 16)
        solids["ramp"] = ramp(-half, half, 0.0, 1, 0.0, 16)
    for tag, x in (("rail_l", -half + RAIL_T / 2), ("rail_r", half - RAIL_T / 2)):
        if part in ("all", tag):
            solids[tag] = railing(b, (x, 0, 0), (x, 4, H))
    return b, solids


def m_stair_straight():
    return stair_straight()


def m_stair_straight_core():
    return stair_straight("core")


def m_stair_straight_rail_l():
    return stair_straight("rail_l")


def m_stair_straight_rail_r():
    return stair_straight("rail_r")


def stair_u(part="all"):
    """Scala a U: 2 x 2 celle, origine al centro del bordo d'ingresso.

    Prima rampa a sinistra (-X) verso +Y fino al pianerottolo a 1,5 m, seconda a destra che
    torna indietro: ARRIVA SULLO STESSO BORDO DA CUI E' PARTITA, quindi sul pavimento del
    piano di sopra per costruzione, e si impila identica a ogni piano.
    `part`: "all" (tutto), "core" (rampe, pianerottolo e le due ringhiere centrali),
    "rail_l" / "rail_r" (fianco esterno di una rampa + quel lato del pianerottolo),
    "rail_back" (fondo del pianerottolo). Le ringhiere esterne sono moduli a se' perche'
    contro un muro non vanno messe: il generatore monta "core" e solo i lati liberi.
    """
    b = Builder()
    side, far, top = 2 - STAIR_INSET, 4 - STAIR_INSET, H / 2
    solids = {}
    e = RAIL_T / 2
    xo, yb = side - e, far - e
    if part in ("all", "core"):
        flight(b, -side, 0, 0.0, 1, 0.0, 8)
        flight(b, 0, side, 2.0, -1, top, 8)
        b.poly([(-side, 2, top), (side, 2, top), (side, far, top), (-side, far, top)], "stone", (0, 0, 1))
        b.poly([(-side, 2, top - STAIR_SLAB), (side, 2, top - STAIR_SLAB), (side, far, top - STAIR_SLAB),
                (-side, far, top - STAIR_SLAB)], "ceiling", (0, 0, -1))
        b.box(-side, side, 2, far, top - STAIR_SLAB, top, "stone", "xXyY")
        solids["ramp_a"] = ramp(-side, 0, 0.0, 1, 0.0, 8)
        solids["ramp_b"] = ramp(0, side, 2.0, -1, top, 8)
        solids["landing"] = prism([(2, top - STAIR_SLAB), (far, top - STAIR_SLAB), (far, top), (2, top)],
                                  -side, side)
        # I due fianchi interni: di la' c'e' il vuoto sopra l'altra rampa. I montanti al centro
        # del bordo d'ingresso, impilando le scale, si affiancano a quelli del piano accanto.
        solids["rail_a_in"] = railing(b, (-e, 0, 0), (-e, 2, top))
        solids["rail_b_in"] = railing(b, (e, 2, top), (e, 0, H))
    if part in ("all", "rail_l"):
        solids["rail_a_out"] = railing(b, (-xo, 0, 0), (-xo, 2, top))
        solids["rail_land_l"] = railing(b, (-xo, 2, top), (-xo, far, top), post_a=False)
    if part in ("all", "rail_r"):
        solids["rail_b_out"] = railing(b, (xo, 2, top), (xo, 0, H))
        solids["rail_land_r"] = railing(b, (xo, 2, top), (xo, far, top), post_a=False)
    if part in ("all", "rail_back"):
        solids["rail_land_back"] = railing(b, (-side + RAIL_T, yb, top), (side - RAIL_T, yb, top), False, False)
    return b, solids


def m_stair_u():
    return stair_u()


def m_stair_u_core():
    return stair_u("core")


def m_stair_u_rail_l():
    return stair_u("rail_l")


def m_stair_u_rail_r():
    return stair_u("rail_r")


def m_stair_u_rail_back():
    return stair_u("rail_back")


# La scala stretta sta sempre fra tre muri (vedi sotto): basta staccarla dalla faccia di un
# tramezzo (8 cm dalla linea di griglia).
STAIR_NARROW_INSET = 0.10


def m_stair_u_narrow():
    """Scala a U stretta: 1 x 2 celle (2 m di larghezza, 4 di profondita'), per le case strette.

    Stessa logica di `stair_u` (origine al centro del bordo d'ingresso, rampa a sinistra fino al
    pianerottolo a 1,5 m, rampa a destra che torna sullo stesso bordo: si impila identica), ma
    con rampe da 0,9 m. NON ha ringhiere sui fianchi esterni ne' sul pianerottolo: toglierebbero
    il passo a una capsula larga 0,7 m. Va quindi SEMPRE chiusa fra muri su fianchi e fondo
    (esterni o tramezzi): e' il generatore a garantirlo.
    """
    b = Builder()
    side, far, top = 1 - STAIR_NARROW_INSET, 4 - STAIR_NARROW_INSET, H / 2
    flight(b, -side, 0, 0.0, 1, 0.0, 8)
    flight(b, 0, side, 2.0, -1, top, 8)
    b.poly([(-side, 2, top), (side, 2, top), (side, far, top), (-side, far, top)], "stone", (0, 0, 1))
    b.poly([(-side, 2, top - STAIR_SLAB), (side, 2, top - STAIR_SLAB), (side, far, top - STAIR_SLAB),
            (-side, far, top - STAIR_SLAB)], "ceiling", (0, 0, -1))
    b.box(-side, side, 2, far, top - STAIR_SLAB, top, "stone", "xXyY")
    solids = {"ramp_a": ramp(-side, 0, 0.0, 1, 0.0, 8),
              "ramp_b": ramp(0, side, 2.0, -1, top, 8),
              "landing": prism([(2, top - STAIR_SLAB), (far, top - STAIR_SLAB), (far, top), (2, top)],
                               -side, side)}
    e = RAIL_T / 2
    solids["rail_a_in"] = railing(b, (-e, 0, 0), (-e, 2, top))
    solids["rail_b_in"] = railing(b, (e, 2, top), (e, 0, H))
    return b, solids


def door_leaf(width, height, thick, mat):
    """Anta: origine sul cardine (spigolo in basso a sinistra), nessuna collisione."""
    b = Builder()
    fit = [(0, 0), (1, 0), (1, 1), (0, 1)]
    b.poly([(0, -thick / 2, 0), (width, -thick / 2, 0), (width, -thick / 2, height), (0, -thick / 2, height)],
           mat, (0, -1, 0), fit)
    b.poly([(0, thick / 2, 0), (width, thick / 2, 0), (width, thick / 2, height), (0, thick / 2, height)],
           mat, (0, 1, 0), fit)
    b.box(0, width, -thick / 2, thick / 2, 0, height, "wood", "xXZ")
    return b


def m_door_int():
    return door_leaf(1.18, 2.08, 0.04, "door_int")


def m_door_ext():
    return door_leaf(1.18, 2.28, 0.06, "door_ext")


def m_door_portal():
    """Portone ad arco per `ext_portal`: origine al centro della soglia, nessuna collisione."""
    b = Builder()
    op = opening(-0.64, 0.64, 0.0, chain_round(-0.64, 0.64, 1.80, 8))
    pane(b, op, 0.08, "door_ext", -1)
    pane(b, op, 0.14, "door_ext", 1)
    return b


# ---- scuri chiusi (entrambi i kit) ---------------------------------------------------

def m_ext_window_rect_shut():
    return ext_wall([opening(-0.45, 0.45, 0.9, chain_flat(-0.45, 0.45, 2.35), shut=True)])


def m_ext_window_arch_shut():
    return ext_wall([opening(-0.45, 0.45, 0.9, chain_round(-0.45, 0.45, 1.95), shut=True)])


def m_ext_window_gothic_shut():
    return ext_wall([opening(-0.45, 0.45, 0.9, chain_ogee(-0.45, 0.45, 1.80, 0.62), shut=True)])


# ---- pezzi rotti (solo kit rovina) -----------------------------------------------------

def m_ext_window_rect_broken():
    """Vetri sfondati e un solo scuro rimasto, appeso a un cardine."""
    return ext_wall([opening(-0.45, 0.45, 0.9, chain_flat(-0.45, 0.45, 2.35), broken=True, hanging=True)])


def m_ext_window_gothic_broken():
    return ext_wall([opening(-0.45, 0.45, 0.9, chain_ogee(-0.45, 0.45, 1.80, 0.62), broken=True)])


def m_ext_window_arch_boarded():
    """Finestra sbarrata con assi inchiodate; dietro, i vetri rotti."""
    return ext_wall([opening(-0.45, 0.45, 0.9, chain_round(-0.45, 0.45, 1.95), broken=True, boarded=True)])


def m_ext_wall_breach():
    """Breccia fino a terra: ci si passa."""
    return ext_wall([breach(0.05, 1.15, 0.72, 1.25, 601)])


def m_ext_wall_hole():
    """Squarcio alto, non praticabile."""
    return ext_wall([breach(-0.15, 1.9, 0.46, 0.44, 602)])


# Creste dei muri crollati. Gli estremi stanno SEMPRE a quota piena (H) o a quota LOW, cosi'
# i moduli si accostano senza gradini: [pieno][hl][low]...[low][lh][pieno].
LOW = 1.0
PROFILE_HL = [(-1.0, H), (-0.72, 2.78), (-0.55, 2.2), (-0.22, 2.05), (-0.05, 1.5), (0.38, 1.38),
              (0.55, 1.08), (1.0, LOW)]
PROFILE_LOW = [(-1.0, LOW), (-0.62, 1.16), (-0.3, 0.8), (0.1, 0.96), (0.48, 0.68), (0.76, 1.1), (1.0, LOW)]
PROFILE_LH = [(-1.0, LOW), (-0.6, 0.86), (-0.35, 1.3), (0.0, 1.62), (0.2, 2.25), (0.5, 2.4),
              (0.8, 2.86), (1.0, H)]


def crest_wall(profile, y0, y1, mat0, mat1, top):
    """Muro con la sommita' crollata lungo `profile`; la cresta mostra la muratura."""
    b = Builder()
    pts = [(x, LOW + (z - LOW) * (top - LOW) / (H - LOW) if z > LOW else z) for x, z in profile]
    for (px, pz), (qx, qz) in zip(pts, pts[1:]):
        b.poly([(px, y0, 0), (qx, y0, 0), (qx, y0, qz), (px, y0, pz)], mat0, (0, -1, 0))
        b.poly([(px, y1, 0), (qx, y1, 0), (qx, y1, qz), (px, y1, pz)], mat1, (0, 1, 0))
        b.poly([(px, y0, pz), (qx, y0, qz), (qx, y1, qz), (px, y1, pz)], "brick_core",
               (-(qz - pz), 0, qx - px))
    return b


def m_ext_wall_ruin_hl():
    """Cresta che scende: piena a -X, bassa (1 m) a +X."""
    return crest_wall(PROFILE_HL, 0.0, T, "facade", "plaster", H)


def m_ext_wall_ruin_low():
    return crest_wall(PROFILE_LOW, 0.0, T, "facade", "plaster", H)


def m_ext_wall_ruin_lh():
    """Cresta che sale: bassa a -X, piena a +X."""
    return crest_wall(PROFILE_LH, 0.0, T, "facade", "plaster", H)


def m_int_wall_breach():
    return int_wall([breach(-0.05, 1.05, 0.68, 1.15, 611)])


def m_int_wall_ruin_hl():
    return crest_wall(PROFILE_HL, -TI / 2, TI / 2, "plaster", "plaster", HI)


def m_int_wall_ruin_low():
    return crest_wall(PROFILE_LOW, -TI / 2, TI / 2, "plaster", "plaster", HI)


def m_int_wall_ruin_lh():
    return crest_wall(PROFILE_LH, -TI / 2, TI / 2, "plaster", "plaster", HI)


# Foro dei solai sfondati: lo stesso contorno per pavimento, soffitto e tetto, cosi' il
# pavimento di un piano e il soffitto di quello sotto combaciano.
HOLE_C = (0.08, -0.05)
HOLE_R = [0.62, 0.50, 0.70, 0.55, 0.74, 0.58, 0.66, 0.48]
HOLE = [(HOLE_C[0] + r * math.cos(math.radians(k * 45 + 8)), HOLE_C[1] + r * math.sin(math.radians(k * 45 + 8)))
        for k, r in enumerate(HOLE_R)]
OUTER = [(1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1)]


def ring(b, zfn, mat, hint, uvfn=None):
    """La cella meno il foro: otto quad fra il bordo e il contorno frastagliato."""
    for k in range(8):
        j = (k + 1) % 8
        pts = [OUTER[k], OUTER[j], HOLE[j], HOLE[k]]
        b.poly([(x, y, zfn(x, y)) for x, y in pts], mat, hint, uvfn=uvfn)


def floor_broken(mat):
    b = Builder()
    ring(b, lambda x, y: 0.0, mat, (0, 0, 1))
    for k in range(8):
        (px, py), (qx, qy) = HOLE[k], HOLE[(k + 1) % 8]
        b.poly([(px, py, 0), (qx, qy, 0), (qx, qy, -0.3), (px, py, -0.3)], "brick_core",
               (HOLE_C[0] - (px + qx) / 2, HOLE_C[1] - (py + qy) / 2, 0))
    beam = lambda p: (p.y, p.x + p.z)
    b.box(-0.31, -0.19, -1, 1, -0.27, -0.09, "wood", "xXzZ", uvfn=beam)
    b.box(0.30, 0.42, -1, 0.05, -0.27, -0.09, "wood", "xXzZY", uvfn=beam)
    return b


def m_floor_terrazzo_broken():
    return floor_broken("terrazzo")


def m_floor_planks_broken():
    return floor_broken("planks")


def m_ceiling_plaster_broken():
    b = Builder()
    ring(b, lambda x, y: HI, "ceiling", (0, 0, -1))
    return b


def m_ceiling_beams_broken():
    b = Builder()
    ring(b, lambda x, y: HI, "boards", (0, 0, -1))
    for cx in (-0.75, -0.25, 0.25, 0.75):
        b.box(cx - 0.06, cx + 0.06, -1, 1, HI - 0.16, HI, "wood", "xXz",
              uvfn=lambda p: (p.y, p.x + p.z))
    return b


def rafters(b, dz):
    """Orditura a vista: un quad a ritaglio alfa, con le due facce."""
    pts = [(-1, -1, dz), (1, -1, dz), (1, 1, R + dz), (-1, 1, R + dz)]
    b.poly(pts, "roof_bare", (0, 0, 1), uvfn=roof_u)
    b.poly([(x, y, z - 0.012) for x, y, z in pts], "roof_bare", (0, 0, -1), uvfn=roof_u)


def m_roof_slope_broken():
    """Falda sfondata: dal foro si vedono i travetti."""
    b = Builder()
    ring(b, lambda x, y: PITCH * (y + 1), "roof", (0, 0, 1), roof_u)
    rafters(b, -0.05)
    return b


def m_roof_slope_bare():
    """Falda senza coppi: solo l'orditura, senza collisione (ci si cade attraverso)."""
    b = Builder()
    rafters(b, 0.0)
    return b


def add_underside(builder):
    """Intradosso del tetto: nella rovina i soffitti si sfondano e il tetto si vede da sotto."""
    for pts, _, mat in list(builder.faces):
        if mat == "roof":
            builder.poly([(p.x, p.y, p.z - 0.02) for p in reversed(pts)], "boards", (0, 0, -1))


def rubble(scale):
    """Cumulo di macerie: otto punti a terra e due cime."""
    b = Builder()
    radii = [0.92, 0.70, 0.86, 0.62, 0.95, 0.74, 0.80, 0.66]
    base = [(r * math.cos(math.radians(k * 45 + 12)) * scale, r * math.sin(math.radians(k * 45 + 12)) * scale, 0.0)
            for k, r in enumerate(radii)]
    tops = [(0.30 * scale, -0.10 * scale, 0.42 * scale), (-0.18 * scale, 0.12 * scale, 0.60 * scale)]
    pick = [0 if (base[k][0] + base[(k + 1) % 8][0]) > 0.1 * scale else 1 for k in range(8)]
    for k in range(8):
        j = (k + 1) % 8
        b.poly([base[k], base[j], tops[pick[k]]], "rubble", (0, 0, 1))
        if pick[k] != pick[j]:
            b.poly([base[j], tops[pick[k]], tops[pick[j]]], "rubble", (0, 0, 1))
    return b


def m_rubble_pile():
    return rubble(1.0)


def m_rubble_small():
    return rubble(0.5)


# nome -> (funzione, collisione dalla mesh[, solo kit rovina])
MODULES = {
    "ext_wall": (m_ext_wall, True),
    "ext_window_rect": (m_ext_window_rect, True),
    "ext_window_arch": (m_ext_window_arch, True),
    "ext_window_gothic": (m_ext_window_gothic, True),
    "ext_window_grille": (m_ext_window_grille, True),
    "ext_window_high": (m_ext_window_high, True),
    "ext_door": (m_ext_door, True),
    "ext_portal": (m_ext_portal, True),
    "ext_balcony": (m_ext_balcony, True),
    "ext_quadrifora": (m_ext_quadrifora, True),
    "ext_plinth": (m_ext_plinth, False),
    "ext_stringcourse": (m_ext_stringcourse, False),
    "ext_cornice": (m_ext_cornice, False),
    "ext_cornice_corner": (m_ext_cornice_corner, False),
    "ext_corner_convex": (m_ext_corner_convex, True),
    "ext_corner_concave": (m_ext_corner_concave, True),
    "roof_slope": (m_roof_slope, True),
    "roof_corner_outer": (m_roof_corner_outer, True),
    "roof_corner_inner": (m_roof_corner_inner, True),
    "roof_ridge": (m_roof_ridge, True),
    "roof_ridge_end": (m_roof_ridge_end, True),
    "roof_peak": (m_roof_peak, True),
    "chimney": (m_chimney, True),
    "int_wall": (m_int_wall, True),
    "int_door": (m_int_door, True),
    "int_arch": (m_int_arch, True),
    "int_post": (m_int_post, True),
    "floor_terrazzo": (m_floor_terrazzo, True),
    "floor_planks": (m_floor_planks, True),
    "floor_paving": (m_floor_paving, True),
    "floor_edge": (m_floor_edge, False),
    "ceiling_plaster": (m_ceiling_plaster, True),
    "ceiling_beams": (m_ceiling_beams, True),
    "stair_straight": (m_stair_straight, False),
    "stair_u": (m_stair_u, False),
    "stair_u_core": (m_stair_u_core, False),
    "stair_u_rail_l": (m_stair_u_rail_l, False),
    "stair_u_rail_r": (m_stair_u_rail_r, False),
    "stair_u_rail_back": (m_stair_u_rail_back, False),
    "stair_straight_core": (m_stair_straight_core, False),
    "stair_straight_rail_l": (m_stair_straight_rail_l, False),
    "stair_straight_rail_r": (m_stair_straight_rail_r, False),
    "stair_u_narrow": (m_stair_u_narrow, False),
    "stair_rail": (m_stair_rail, True),
    "door_int": (m_door_int, False),
    "door_ext": (m_door_ext, False),
    "door_portal": (m_door_portal, False),
    "ext_window_rect_shut": (m_ext_window_rect_shut, True),
    "ext_window_arch_shut": (m_ext_window_arch_shut, True),
    "ext_window_gothic_shut": (m_ext_window_gothic_shut, True),
    "ext_window_rect_broken": (m_ext_window_rect_broken, True, True),
    "ext_window_gothic_broken": (m_ext_window_gothic_broken, True, True),
    "ext_window_arch_boarded": (m_ext_window_arch_boarded, True, True),
    "ext_wall_breach": (m_ext_wall_breach, True, True),
    "ext_wall_hole": (m_ext_wall_hole, True, True),
    "ext_wall_ruin_hl": (m_ext_wall_ruin_hl, True, True),
    "ext_wall_ruin_low": (m_ext_wall_ruin_low, True, True),
    "ext_wall_ruin_lh": (m_ext_wall_ruin_lh, True, True),
    "int_wall_breach": (m_int_wall_breach, True, True),
    "int_wall_ruin_hl": (m_int_wall_ruin_hl, True, True),
    "int_wall_ruin_low": (m_int_wall_ruin_low, True, True),
    "int_wall_ruin_lh": (m_int_wall_ruin_lh, True, True),
    "floor_terrazzo_broken": (m_floor_terrazzo_broken, True, True),
    "floor_planks_broken": (m_floor_planks_broken, True, True),
    "ceiling_plaster_broken": (m_ceiling_plaster_broken, True, True),
    "ceiling_beams_broken": (m_ceiling_beams_broken, True, True),
    "roof_slope_broken": (m_roof_slope_broken, True, True),
    "roof_slope_bare": (m_roof_slope_bare, False, True),
    "rubble_pile": (m_rubble_pile, True, True),
    "rubble_small": (m_rubble_small, True, True),
}


# =====================================================================================
# EXPORT
# =====================================================================================

def view3d_override(**extra):
    """temp_override con un'area VIEW_3D: senza, l'export fallisce il poll()."""
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


def export(name, objects):
    path = "{}/{}.gltf".format(OUT_DIR, name)
    # `use_selection` legge la selezione VERA del view layer: l'override da solo esporta il vuoto.
    bpy.context.view_layer.update()
    for other in bpy.context.view_layer.objects:
        other.select_set(other in objects)
    bpy.context.view_layer.objects.active = objects[0]
    with view3d_override(object=objects[0], active_object=objects[0],
                         selected_objects=objects, selected_editable_objects=objects):
        bpy.ops.export_scene.gltf(
            filepath=path,
            export_format="GLTF_SEPARATE",
            export_keep_originals=True,
            use_selection=True,
            export_yup=True,
            export_skins=False,
            export_animations=False,
            export_materials="EXPORT",
            export_cameras=False,
            export_lights=False,
        )
    with open(path, "r", encoding="utf-8") as handle:
        if '"meshes"' not in handle.read():
            raise RuntimeError("Export vuoto per {}: la selezione non e' arrivata all'exporter".format(name))


def build_modules():
    """Costruisce ed esporta i moduli del kit corrente. Ritorna nome -> oggetti visibili."""
    report = {}
    built = {}
    for name, spec in MODULES.items():
        fn, collide = spec[0], spec[1]
        if len(spec) > 2 and spec[2] and not RUIN:
            continue
        result = fn()
        builder, colliders = result if isinstance(result, tuple) else (result, {})
        if RUIN and name.startswith("roof_"):
            add_underside(builder)
        simple = getattr(builder, "col", None)
        has_simple = bool(collide and simple is not None and simple.faces)
        visible = [to_object(name, builder, name + ("-col" if collide and not has_simple else ""))]
        loose = getattr(builder, "nocol", None)
        if loose is not None and loose.faces:
            visible.append(to_object(name + "_loose", loose))
        objects = list(visible)
        if has_simple:
            # `-colonly`: Godot ne ricava solo lo StaticBody3D (trimesh), senza mesh visibile.
            objects.append(to_object(name + "_col", simple, name + "_col-colonly"))
        for tag, solid in colliders.items():
            objects.append(to_object("{}_{}".format(name, tag), solid, "{}_{}-convcolonly".format(name, tag)))
        export(name, objects)
        report[name] = sum(len(p.vertices) - 2 for o in visible for p in o.data.polygons)
        built[name] = visible
        for extra in objects[len(visible):]:
            extra.hide_render = True
            extra.hide_viewport = True
    return built, report


# =====================================================================================
# ANTEPRIMA: un palazzetto di prova montato con i moduli, per controllare a occhio
# =====================================================================================

def preview(built):
    scene = bpy.context.scene
    demo = bpy.data.collections.new("Demo")
    scene.collection.children.link(demo)
    catalog = bpy.data.collections.new("Catalog")
    scene.collection.children.link(catalog)

    def put(name, x, y, z, rot=0):
        for source in built[name]:
            obj = bpy.data.objects.new("demo_" + name, source.data)
            obj.location = (x, y, z)
            obj.rotation_euler = (0, 0, math.radians(rot))
            demo.objects.link(obj)

    nx, ny, floors = 4, 3, 3
    x_min, y_min = -nx, -ny
    x_max, y_max = nx, ny
    south = [["ext_window_grille", "ext_door", "ext_window_grille", "ext_portal"],
             ["ext_window_gothic", "ext_quadrifora", None, "ext_balcony"],
             ["ext_window_rect", "ext_window_arch", "ext_window_arch", "ext_window_rect"]]
    side = [["ext_wall", "ext_window_grille", "ext_wall"],
            ["ext_window_arch", "ext_wall", "ext_window_arch"],
            ["ext_window_rect", "ext_window_rect", "ext_window_rect"]]
    south[2][0] = "ext_window_rect_shut"
    if RUIN:
        south[2][1] = "ext_window_arch_boarded"
        south[2][3] = "ext_window_rect_broken"
        south[1][0] = "ext_window_gothic_broken"
        south[0][2] = "ext_wall_breach"
        side[0][1] = "ext_wall_hole"
        side[1][0] = "ext_window_arch_shut"
    else:
        south[2][1] = "ext_window_arch_shut"
        south[1][0] = "ext_window_gothic_shut"
    broken_cell = (1, 0)
    floors_mat = ["floor_paving", "floor_terrazzo", "floor_planks"]
    ceilings = ["ceiling_beams", "ceiling_beams", "ceiling_plaster"]
    stair_cells = {(2, 1), (3, 1), (2, 2), (3, 2)}
    for f in range(floors):
        z = f * H
        for i in range(nx):
            cx = x_min + 1 + 2 * i
            name = south[f][i]
            if name == "ext_quadrifora":
                put(name, cx + 1, y_min, z)
            elif name:
                put(name, cx, y_min, z)
            if i >= 2 and f < floors - 1:
                put("ext_window_high", cx, y_max, z, 180)
            else:
                put("ext_window_rect" if (i + f) % 2 else "ext_wall", cx, y_max, z, 180)
            if f == 0 and name not in ("ext_door", "ext_portal"):
                put("ext_plinth", cx, y_min, z)
            if f > 0:
                put("ext_stringcourse", cx, y_min, z)
                put("ext_stringcourse", cx, y_max, z, 180)
        for j in range(ny):
            cy = y_min + 1 + 2 * j
            put("ext_wall" if j >= 1 and f < floors - 1 else side[f][j], x_max, cy, z, 90)
            put(side[f][ny - 1 - j], x_min, cy, z, -90)
            if f == 0:
                put("ext_plinth", x_max, cy, z, 90)
                put("ext_plinth", x_min, cy, z, -90)
            else:
                put("ext_stringcourse", x_max, cy, z, 90)
                put("ext_stringcourse", x_min, cy, z, -90)
        for (x, y, rot) in ((x_min, y_min, 0), (x_max, y_min, 90), (x_max, y_max, 180), (x_min, y_max, -90)):
            put("ext_corner_convex", x, y, z, rot)
        for i in range(nx):
            for j in range(ny):
                cx, cy = x_min + 1 + 2 * i, y_min + 1 + 2 * j
                smashed = "_broken" if RUIN and (i, j) == broken_cell else ""
                if f == 0 or (i, j) not in stair_cells:
                    put(floors_mat[f] + (smashed if f > 0 else ""), cx, cy, z)
                if f == floors - 1 or (i, j) not in stair_cells:
                    put(ceilings[f] + smashed, cx, cy, z)
        if f < floors - 1:
            put("stair_u", x_min + 6, y_min + 2, z)
        if f > 0:
            # Testa del solaio su ogni lato del vano che confina con una cella col pavimento:
            # senza, fra soffitto (2,7 m) e pavimento di sopra (3 m) si vede dentro lo spessore.
            for cx in (x_min + 5, x_min + 7):
                put("floor_edge", cx, y_min + 2, z, 180)
            for cy in (y_min + 3, y_min + 5):
                put("floor_edge", 0, cy, z, 90)
        # Il tramezzo su x = 0 separa le stanze dal vano scala (j = 1, 2): la porta va SOLO sul
        # tratto che da' sul disimpegno (j = 0), mai contro il fianco di una rampa.
        for j in range(ny):
            put("int_door" if j == 0 else "int_wall", 0, y_min + 1 + 2 * j, z, 90)
        put("int_arch", x_min + 1, y_min + 2, z)
        put("int_wall_breach" if RUIN and f < 2 else "int_wall", x_min + 3, y_min + 2, z)
        put("int_post", 0, y_min + 2, z)
    top = floors * H
    for i in range(nx):
        cx = x_min + 1 + 2 * i
        put("ext_cornice", cx, y_min, top)
        put("ext_cornice", cx, y_max, top, 180)
    for j in range(ny):
        cy = y_min + 1 + 2 * j
        put("ext_cornice", x_max, cy, top, 90)
        put("ext_cornice", x_min, cy, top, -90)
    for (x, y, rot) in ((x_min, y_min, 0), (x_max, y_min, 90), (x_max, y_max, 180), (x_min, y_max, -90)):
        put("ext_cornice_corner", x, y, top, rot)
    for (i, j, rot) in ((0, 0, 0), (nx - 1, 0, 90), (nx - 1, ny - 1, 180), (0, ny - 1, -90)):
        put("roof_corner_outer", x_min + 1 + 2 * i, y_min + 1 + 2 * j, top, rot)
    for i in range(1, nx - 1):
        slope = ("roof_slope_broken" if i == 1 else "roof_slope_bare") if RUIN else "roof_slope"
        put(slope, x_min + 1 + 2 * i, y_min + 1, top)
        put("roof_slope", x_min + 1 + 2 * i, y_max - 1, top, 180)
    put("roof_slope", x_max - 1, 0, top, 90)
    put("roof_slope", x_min + 1, 0, top, -90)
    put("roof_ridge_end", -1, 0, top + R)
    put("roof_ridge_end", 1, 0, top + R, 180)
    put("chimney", x_min + 1.2, 0.2, top + 0.6)
    if RUIN:
        put("rubble_pile", x_min + 3, y_min + 1, 0)
        put("rubble_small", 1.2, y_min - 1.0, 0)
        # un tratto di muro crollato, a parte: [pieno][scende][basso][sale][pieno]
        run_x = x_max + 4
        for k, name in enumerate(["ext_wall", "ext_wall_ruin_hl", "ext_wall_ruin_low", "ext_wall_ruin_lh",
                                  "ext_window_rect_broken"]):
            put(name, run_x + 2 * k, y_min, 0)
        for k, name in enumerate(["int_wall", "int_wall_ruin_hl", "int_wall_ruin_low", "int_wall_ruin_lh",
                                  "int_wall_breach"]):
            put(name, run_x + 2 * k, y_min + 3, 0)
            put("floor_paving", run_x + 2 * k, y_min + 1, 0)
            put("floor_paving", run_x + 2 * k, y_min + 3, 0)
        put("rubble_pile", run_x + 4, y_min + 1.2, 0)

    # catalogo: tutti i moduli in fila, per gruppi
    rows = {"ext": 0, "roo": 1, "chi": 1, "rub": 1, "int": 2, "flo": 2, "cei": 2, "sta": 3, "doo": 3}
    cursor = {}
    for name, objects in built.items():
        row = rows[name[:3]]
        x = cursor.get(row, 0.0)
        cursor[row] = x + (5.0 if name == "ext_quadrifora" else 3.0)
        for obj in objects:
            obj.location = (60 + x, row * 8.0, 0)
            scene.collection.objects.unlink(obj)
            catalog.objects.link(obj)

    # luci e camera
    world = bpy.data.worlds.new("PreviewWorld")
    world.use_nodes = True
    bg = world.node_tree.nodes["Background"]
    bg.inputs[0].default_value = (0.30, 0.33, 0.33, 1) if RUIN else (0.55, 0.65, 0.78, 1)
    bg.inputs[1].default_value = 0.9
    scene.world = world
    sun = bpy.data.objects.new("Sun", bpy.data.lights.new("Sun", "SUN"))
    sun.data.energy = 1.8 if RUIN else 3.0
    sun.rotation_euler = (math.radians(55), 0, math.radians(-35))
    scene.collection.objects.link(sun)
    lamp = bpy.data.objects.new("Lamp", bpy.data.lights.new("Lamp", "POINT"))
    lamp.data.energy = 300
    lamp.location = (-1.5, -0.5, H + 2.2)
    scene.collection.objects.link(lamp)
    cam = bpy.data.objects.new("Cam", bpy.data.cameras.new("Cam"))
    scene.collection.objects.link(cam)
    scene.camera = cam
    for engine in ("BLENDER_EEVEE", "BLENDER_EEVEE_NEXT"):
        try:
            scene.render.engine = engine
            break
        except TypeError:
            continue
    scene.view_settings.view_transform = "Standard"
    scene.render.resolution_x, scene.render.resolution_y = 1280, 800
    scene.render.image_settings.file_format = "PNG"

    def shoot(tag, location, target, lens=28, ortho=None):
        cam.location = location
        direction = Vector(target) - Vector(location)
        cam.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()
        cam.data.type = "ORTHO" if ortho else "PERSP"
        if ortho:
            cam.data.ortho_scale = ortho
        cam.data.lens = lens
        cam.data.clip_start = 0.05
        path = "{}/{}_{}.png".format(tempfile.gettempdir().replace("\\", "/"), KIT, tag)
        scene.render.filepath = path
        bpy.ops.render.render(write_still=True)
        return path

    shots = [
        shoot("front", (-3.5, -17, 5.5), (0, -3, 5.0), 30),
        shoot("corner", (12, -14, 13), (0, 0, 5.5), 30),
        shoot("detail", (-1.0, -8.5, 4.6), (0.5, -3, 4.6), 35),
        shoot("ground", (2.5, -7.5, 1.7), (1.5, -3, 1.4), 28),
        shoot("interior1", (-3.4, -2.4, H + 1.6), (3.5, 1.5, H + 1.2), 18),
        shoot("interior0", (1.0, 2.4, 1.6), (3.5, -2.5, 1.0), 18),
        shoot("stairs_up", (0.7, -2.5, H + 1.65), (2.4, 1.2, H + 0.9), 16),
        shoot("stairs_down", (3.4, -2.6, H + 1.7), (1.6, 1.5, H - 1.2), 16),
        shoot("back", (9.5, 15, 7), (1.5, 3, 4.2), 30),
        shoot("well", (3.2, 2.3, H / 2 + 1.6), (0.2, -1.2, H + 0.1), 16),
    ]
    if RUIN:
        shots.append(shoot("ruins", (run_x + 1.5, y_min - 9.5, 3.4), (run_x + 4, y_min + 1, 1.3), 30))
        shots.append(shoot("hole", (0.4, -0.7, 0.5), (-1.0, -2.0, 5.5), 16))
        shots.append(shoot("glass", (-3.2, 0.4, H + 1.5), (-1.0, -3.0, H + 1.5), 20))
    return shots


# =====================================================================================

GLASS_TRES = """[gd_resource type="StandardMaterial3D" load_steps=2 format=3]

[ext_resource type="Texture2D" path="res://assets/models/buildings/{kit}/textures/{texture}.png" id="1"]

[resource]
resource_name = "{name}"
transparency = 1
albedo_texture = ExtResource("1")
metallic_specular = 0.0
roughness = 1.0
texture_filter = 2
"""


def write_glass_materials():
    """Materiali Godot del vetro trasparente, pronti da assegnare alle superfici `window`/`glass`.

    Scritti solo se mancano: dopo il primo import appartengono a Godot (uid) e a chi li ritocca.
    """
    folder = BUILDINGS_DIR + "/materials"
    os.makedirs(folder, exist_ok=True)
    for name, kit, texture in (("glass_psx", "venice", "glass"),
                               ("glass_psx_dirty", "venice_ruin", "glass"),
                               ("glass_psx_broken", "venice_ruin", "glass_broken")):
        target = "{}/{}.tres".format(folder, name)
        if not os.path.exists(target):
            with open(target, "w", encoding="utf-8", newline="\n") as handle:
                handle.write(GLASS_TRES.format(kit=kit, texture=texture, name=name))


def run(kit):
    global KIT, RUIN, OUT_DIR, TEX_DIR, GLASS
    KIT, RUIN = kit, KITS[kit]
    OUT_DIR = "{}/{}".format(BUILDINGS_DIR, kit)
    TEX_DIR = OUT_DIR + "/textures"
    GLASS = "glass" if RUIN else "window"
    bpy.ops.wm.read_homefile(use_empty=True)
    bpy.context.scene.unit_settings.scale_length = 1.0
    os.makedirs(OUT_DIR, exist_ok=True)
    images = build_textures()
    build_materials(images)
    built, report = build_modules()
    summary = {"textures": len(images), "modules": len(report), "total_tris": sum(report.values()),
               "heaviest": max(report, key=report.get), "heaviest_tris": max(report.values())}
    if PREVIEW:
        summary["preview"] = preview(built)
    return summary


result = {kit: run(kit) for kit in SELECTED}
write_glass_materials()
