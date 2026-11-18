"""Mappa dell'acqua della laguna: zone (per il colore) ed esposizione alle onde.

Legge le classi gia' generate da osm_terrain.py (<mappa>/classes/classes_<x>_<z>.png, 1 m/px) e,
per le region fuori dal poligono, l'alfa dell'atlante lontano (<mappa>/far/far_atlas.png). Scrive
in <mappa>/water/:

  water_zones.png     16 m/px, grigio = id della classe d'acqua prevalente (tabella CLASSES di
                      osm_terrain.py). Una cella con almeno 1/4 d'acqua prende la classe d'acqua piu'
                      frequente: cosi' un rio largo 8 m sopravvive al sottocampionamento.
                      Serve a WaterSurface per scegliere la tavolozza dove si trova la camera.
  water_exposure.png  32 m/px, 0..255 = esposizione alle onde 0..1, sfocata (~110 m). La leggono
                      ALLO STESSO MODO lo shader (onde disegnate) e WaterVolume (galleggiamento
                      sull'host): bilineare, centri dei texel a +0,5. 0 = acqua ferma (rii),
                      0,5 = laguna aperta (spettro "riparato"), 1 = mare aperto.
  water_map.json      rettangolo mondo coperto e scale.

Uso:
  python addons/venice_builder/tools/water_map.py --map world/maps/laguna/map.json      # ~1 min
Solo stdlib + numpy.
"""
import argparse
import json
import os
import struct
import sys
import time
import zlib

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from osm_terrain import REGION, WATER, box_blur, read_png_gray, write_png, _project_root  # noqa: E402

ZONE_SCALE = 16          # m/px della mappa delle zone
EXPOSURE_SCALE = 32      # m/px della mappa d'esposizione
BLUR_RADIUS = 3          # px (a 32 m): 3 passate di box blur ~ gaussiana di ~110 m
BLUR_PASSES = 3

# Esposizione alle onde per classe (id di osm_terrain.CLASSES). La terra vale 0: sfocando, le onde
# si spengono da sole avvicinandosi alla riva.
EXPOSURE = {
    0: 1.00,   # mare
    2: 0.50,   # laguna aperta: spettro riparato pieno
    3: 0.35,   # acqua bassa
    4: 0.25,   # valle da pesca (arginata)
    10: 0.35,  # velma
    11: 0.45,  # canale
    12: 0.08,  # rio: quasi fermo
    13: 0.20,  # ghebo
}
EXPOSURE_LUT = np.zeros(256, np.float32)
for _c, _e in EXPOSURE.items():
    EXPOSURE_LUT[_c] = _e
WATER_ARR = np.array(WATER)


def read_png_any(path):
    """PNG scritti da write_png (filtro 0 su ogni riga, un IDAT), grigio/RGB/RGBA -> (h, w[, c])."""
    with open(path, "rb") as f:
        data = f.read()
    pos, idat, w, h, ch = 8, b"", 0, 0, 1
    while pos < len(data):
        length = struct.unpack(">I", data[pos:pos + 4])[0]
        tag = data[pos + 4:pos + 8]
        body = data[pos + 8:pos + 8 + length]
        if tag == b"IHDR":
            w, h = struct.unpack(">II", body[:8])
            ch = {0: 1, 2: 3, 6: 4}[body[9]]
        elif tag == b"IDAT":
            idat += body
        pos += 12 + length
    rows = np.frombuffer(zlib.decompress(idat), np.uint8).reshape(h, w * ch + 1)
    if rows[:, 0].any():
        raise SystemExit(f"{path}: filtri PNG non supportati (serve un file scritto da write_png)")
    img = rows[:, 1:]
    return img.reshape(h, w) if ch == 1 else img.reshape(h, w, ch)


def zones_from_classes(cls):
    """Region 2048^2 -> 128^2: classe d'acqua prevalente dove l'acqua e' >= 1/4 della cella."""
    n = REGION // ZONE_SCALE
    cells = cls.reshape(n, ZONE_SCALE, n, ZONE_SCALE).transpose(0, 2, 1, 3).reshape(n * n, -1)
    idx = np.arange(n * n)[:, None] * 16 + np.minimum(cells, 15)
    counts = np.bincount(idx.ravel(), minlength=n * n * 16).reshape(n * n, 16)
    water_counts = counts[:, WATER_ARR]
    water_total = water_counts.sum(1)
    best_water = WATER_ARR[water_counts.argmax(1)]
    best_any = counts.argmax(1)
    out = np.where(water_total * 4 >= ZONE_SCALE * ZONE_SCALE, best_water, best_any)
    return out.reshape(n, n).astype(np.uint8)


def exposure_from_classes(cls):
    """Region 2048^2 -> 64^2: esposizione media della cella (non ancora sfocata)."""
    n = REGION // EXPOSURE_SCALE
    e = EXPOSURE_LUT[cls]
    return e.reshape(n, EXPOSURE_SCALE, n, EXPOSURE_SCALE).mean(axis=(1, 3))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--map", required=True, help="map.json della mappa")
    args = ap.parse_args()
    t0 = time.time()

    map_dir = os.path.dirname(os.path.abspath(args.map))
    with open(args.map, "r", encoding="utf-8") as f:
        cfg = json.load(f)
    classes_dir = os.path.join(map_dir, cfg["dirs"]["classes"])
    far_dir = os.path.join(map_dir, "far")
    out_dir = os.path.join(map_dir, "water")
    os.makedirs(out_dir, exist_ok=True)

    with open(os.path.join(far_dir, "far_atlas.json"), "r", encoding="utf-8") as f:
        far = json.load(f)
    rx0, rz0 = far["regions_min"]
    rx1, rz1 = far["regions_max"]
    far_scale = far["far_scale"]
    atlas_alpha = read_png_any(os.path.join(far_dir, "far_atlas.png"))[..., 3]

    nz, ne, nf = REGION // ZONE_SCALE, REGION // EXPOSURE_SCALE, REGION // far_scale
    zones = np.zeros(((rz1 - rz0 + 1) * nz, (rx1 - rx0 + 1) * nz), np.uint8)
    expo = np.zeros(((rz1 - rz0 + 1) * ne, (rx1 - rx0 + 1) * ne), np.float32)
    from_classes = 0
    for z in range(rz0, rz1 + 1):
        for x in range(rx0, rx1 + 1):
            oz, ox = z - rz0, x - rx0
            path = os.path.join(classes_dir, f"classes_{x}_{z}.png")
            if os.path.exists(path):
                cls = read_png_gray(path)
                zones[oz * nz:(oz + 1) * nz, ox * nz:(ox + 1) * nz] = zones_from_classes(cls)
                expo[oz * ne:(oz + 1) * ne, ox * ne:(ox + 1) * ne] = exposure_from_classes(cls)
                from_classes += 1
            else:
                # Fuori dal poligono: l'atlante lontano dice solo terra (alfa 255) o mare (0).
                land = atlas_alpha[oz * nf:(oz + 1) * nf, ox * nf:(ox + 1) * nf] > 127
                fz, fe = ZONE_SCALE // far_scale, EXPOSURE_SCALE // far_scale
                zones[oz * nz:(oz + 1) * nz, ox * nz:(ox + 1) * nz] = np.where(land[::fz, ::fz], 1, 0)
                sea = (~land).astype(np.float32).reshape(ne, fe, ne, fe).mean(axis=(1, 3))
                expo[oz * ne:(oz + 1) * ne, ox * ne:(ox + 1) * ne] = sea

    expo = np.clip(box_blur(expo, BLUR_RADIUS, BLUR_PASSES), 0.0, 1.0)
    write_png(os.path.join(out_dir, "water_zones.png"), zones)
    write_png(os.path.join(out_dir, "water_exposure.png"), np.round(expo * 255).astype(np.uint8))

    # Import come Image (non come texture): la leggono la CPU (galleggiamento, zone) e, per
    # l'esposizione, lo shader tramite una ImageTexture creata a runtime. Il .import si scrive una
    # volta sola: poi e' di Godot.
    root = _project_root(out_dir)
    for name in ("water_zones.png", "water_exposure.png"):
        imp = os.path.join(out_dir, name + ".import")
        if not os.path.exists(imp):
            rel = os.path.relpath(os.path.join(out_dir, name), root).replace("\\", "/")
            with open(imp, "w", encoding="utf-8") as f:
                f.write('[remap]\n\nimporter="image"\ntype="Image"\n\n[deps]\n\n'
                        f'source_file="res://{rel}"\n\n[params]\n\n')

    meta = {
        "world_rect": far["world_rect"],
        "zone_scale": ZONE_SCALE,
        "exposure_scale": EXPOSURE_SCALE,
        "zones_size": [int(zones.shape[1]), int(zones.shape[0])],
        "exposure_size": [int(expo.shape[1]), int(expo.shape[0])],
        "exposure_by_class": {str(k): v for k, v in EXPOSURE.items()},
    }
    with open(os.path.join(out_dir, "water_map.json"), "w", encoding="utf-8") as f:
        json.dump(meta, f, indent=1)
    print(f"Mappa dell'acqua: {from_classes} region dalle classi, zone {zones.shape[1]}x{zones.shape[0]}, "
          f"esposizione {expo.shape[1]}x{expo.shape[0]}, in {time.time() - t0:.0f} s")


if __name__ == "__main__":
    main()
