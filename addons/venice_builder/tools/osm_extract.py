"""
OpenStreetMap -> dati per generare la mappa di Venezia (test_map_ve.tscn).

Scarica da Overpass (lo stesso backend di https://overpass-turbo.eu/) TUTTO
quello che OSM sa di un riquadro lat/lon, lo proietta nello stesso sistema del
DEM (UTM 32N) e lo riscrive in coordinate mondo Godot, diviso per livelli:

    land        isole: il bordo preciso terra/acqua (anelli interni della laguna)
    water       specchi d'acqua mappati come area (canali, darsene, bacini)
    waterways   assi dei canali (nome, larghezza, navigabilita')
    buildings   impronte con cortili interni, altezza, materiali, tetto
    parts       building:part: i volumi che compongono gli edifici monumentali
    paths       calli, fondamenta, scale, ponti, sottoporteghi (con id dei nodi)
    areas       campi e piazze, parchi, giardini, prati
    bridges     sagoma dei ponti (man_made=bridge)
    piers       pontili e imbarcaderi
    barriers    muri, recinzioni, siepi
    tree_rows   filari
    points      alberi, panchine, lampioni, pozzi, bricole, monumenti, ingressi,
                civici, negozi, fermate del vaporetto...
    other       qualunque altra way con tag che non rientra nei livelli sopra

Ogni elemento conserva i propri tag OSM (tolte solo le traduzioni del nome):
un generatore procedurale futuro trova li' materiali, colori, forma del tetto,
numero di piani, superficie delle strade, ecc. La risposta grezza di Overpass
viene comunque salvata accanto (venice_osm.raw.json) e non perde nulla.

Nessuna dipendenza oltre la libreria standard.

USO
  Scarica e converte un riquadro (sud,ovest,nord,est in gradi):
      python osm_extract.py --bbox 45.4270,12.3330,45.4365,12.3460

  Riconverte senza riscaricare (o converte un export di overpass-turbo fatto
  con la query stampata da --print-query, "Esporta > dati grezzi OSM"):
      python osm_extract.py --input venice_osm.raw.json --bbox ...

  Poi, da Godot (vedi i due script):
      polish_terrain.gd      rifinisce la riva del terreno con il livello land
      build_map_scene.gd     genera venice_map.scn

DA DOVE VENGONO LE COSTANTI DI GEOREFERENZIAZIONE
  - Il DEM sorgente (w50075_s10.tif, TINITALY) e' in EPSG:32632 con origine
    (angolo in alto a sinistra) E=749950, N=5050050 e pixel di 10 m.
  - tile_heightmap_v4.py lo ricampiona x10: 1 pixel = 1 m, quindi
        px = E - 749950        py = 5050050 - N
  - batch_import_tiles_v2.gd importa ogni tile a (px, py) + ORIGIN_OFFSET con
    ORIGIN_OFFSET = -25050. MA Terrain3D assegna ogni tile alla region che
    contiene il suo angolo, cioe' ALLINEA alla griglia delle region (2048 m):
    floor(-25050 / 2048) = -13, quindi lo scostamento reale e' -13 * 2048 =
    -26624, non -25050. Verificato: la region (-13,-13) e' identica pixel per
    pixel al tile height_x0_y0.f32.
  - Ne segue:
        X_godot = E - 749950 - 26624
        Z_godot = 5050050 - N - 26624       (Z cresce verso sud)
"""

import argparse
import json
import math
import os
import statistics
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

# ------------------- GEOREFERENZIAZIONE DELLA MAPPA -------------------
DEM_ORIGIN_E = 749950.0      # angolo in alto a sinistra del GeoTIFF (EPSG:32632)
DEM_ORIGIN_N = 5050050.0
METERS_PER_PIXEL = 1.0       # dopo UPSCALE=10 di tile_heightmap_v4.py
REGION_SIZE = 2048           # region_size di Terrain3D = TILE_SIZE
IMPORT_OFFSET = -25050.0     # ORIGIN_OFFSET nominale di batch_import_tiles_v2.gd
# Terrain3D allinea alla griglia delle region: e' questo lo scostamento effettivo.
WORLD_OFFSET = math.floor(IMPORT_OFFSET / REGION_SIZE) * REGION_SIZE   # -26624

# ------------------- ALTEZZE -------------------
FALLBACK_HEIGHT = 12.0       # m, solo se nel riquadro nessun edificio ha un'altezza
METERS_PER_LEVEL = 3.2       # m per piano, quando c'e' solo building:levels
MIN_AREA = 1.0               # m^2: sotto questa soglia un poligono e' rumore
# Un edificio e' "descritto dalle sue parti" se i building:part ne coprono almeno
# questa frazione: in quel caso si disegnano le parti e non la sagoma intera.
PARTS_COVER_RATIO = 0.7
# Margine attorno al riquadro entro cui si tengono le isole (servono intere
# anche quando sporgono dal riquadro).
LAND_MARGIN = 300.0

OVERPASS_URLS = (
    "https://overpass-api.de/api/interpreter",
    "https://overpass.private.coffee/api/interpreter",
)
HERE = os.path.dirname(os.path.abspath(__file__))
KEPT_NAME_KEYS = ("name:it", "name:vec", "name:en")


# ------------------- PROIEZIONE WGS84 -> UTM 32N -------------------
# Trasversa di Mercatore, serie di Krueger (scarto < 1 mm rispetto a pyproj).
# Venezia (12.3 E) sta fuori dal fuso 32 "naturale" (6-12 E), ma TINITALY usa
# il 32 per tutta l'Italia: il DEM e' li', quindi proiettiamo li'.
_A = 6378137.0
_F = 1.0 / 298.257223563
_K0 = 0.9996
_LON0 = math.radians(9.0)
_N = _F / (2.0 - _F)
_AA = _A / (1.0 + _N) * (1.0 + _N**2 / 4.0 + _N**4 / 64.0)
_ALPHA = (
    _N / 2.0 - 2.0 * _N**2 / 3.0 + 5.0 * _N**3 / 16.0,
    13.0 * _N**2 / 48.0 - 3.0 * _N**3 / 5.0,
    61.0 * _N**3 / 240.0,
)


def lonlat_to_utm32(lon_deg, lat_deg):
    lat = math.radians(lat_deg)
    dlon = math.radians(lon_deg) - _LON0
    c = 2.0 * math.sqrt(_N) / (1.0 + _N)
    t = math.sinh(math.atanh(math.sin(lat)) - c * math.atanh(c * math.sin(lat)))
    xi = math.atan2(t, math.cos(dlon))
    eta = math.atanh(math.sin(dlon) / math.sqrt(1.0 + t * t))
    e, n = eta, xi
    for j, a in enumerate(_ALPHA, start=1):
        e += a * math.cos(2 * j * xi) * math.sinh(2 * j * eta)
        n += a * math.sin(2 * j * xi) * math.cosh(2 * j * eta)
    return 500000.0 + _K0 * _AA * e, _K0 * _AA * n


def to_xz(lon_deg, lat_deg):
    """(lon, lat) WGS84 -> [X, Z] mondo Godot, al centimetro. Y la mette il terreno."""
    e, n = lonlat_to_utm32(lon_deg, lat_deg)
    return [
        round((e - DEM_ORIGIN_E) / METERS_PER_PIXEL + WORLD_OFFSET, 2),
        round((DEM_ORIGIN_N - n) / METERS_PER_PIXEL + WORLD_OFFSET, 2),
    ]


# ------------------- OVERPASS -------------------
def build_query(bbox):
    s, w, n, e = bbox
    box = f"{s},{w},{n},{e}"
    # Nodi con almeno un tag (alberi, ingressi, negozi...), tutte le way e le
    # relazioni che descrivono geometria. Confini amministrativi e linee di
    # trasporto si escludono: sono enormi e qui non servono.
    return (
        "[out:json][timeout:180];\n"
        "(\n"
        f"  node({box})(if:count_tags() > 0);\n"
        f"  way({box});\n"
        f'  relation({box})[type!~"^(boundary|route|route_master|network|superroute|restriction|public_transport)$"];\n'
        ");\n"
        "out geom;\n"
    )


def fetch_overpass(query):
    data = urllib.parse.urlencode({"data": query}).encode("utf-8")
    last_error = None
    for attempt in range(4):
        url = OVERPASS_URLS[attempt % len(OVERPASS_URLS)]
        try:
            req = urllib.request.Request(url, data=data, headers={"User-Agent": "osm-map-poc/0.1"})
            with urllib.request.urlopen(req, timeout=240) as resp:
                return json.loads(resp.read().decode("utf-8"))
        except (urllib.error.URLError, TimeoutError, json.JSONDecodeError) as exc:
            last_error = exc
            print(f"  {url}: {exc} - riprovo")
            time.sleep(8)
    raise SystemExit(f"Overpass non risponde: {last_error}")


# ------------------- GEOMETRIA -------------------
def signed_area(ring):
    s = 0.0
    for i in range(len(ring)):
        x0, z0 = ring[i]
        x1, z1 = ring[(i + 1) % len(ring)]
        s += x0 * z1 - x1 * z0
    return s / 2.0


def centroid(ring):
    return [sum(p[0] for p in ring) / len(ring), sum(p[1] for p in ring) / len(ring)]


def bounds(ring):
    xs = [p[0] for p in ring]
    zs = [p[1] for p in ring]
    return min(xs), min(zs), max(xs), max(zs)


def point_in_ring(p, ring):
    x, z = p
    inside = False
    j = len(ring) - 1
    for i in range(len(ring)):
        xi, zi = ring[i]
        xj, zj = ring[j]
        if (zi > z) != (zj > z) and x < (xj - xi) * (z - zi) / (zj - zi) + xi:
            inside = not inside
        j = i
    return inside


def close_ring(points):
    """Lista di [x, z] senza il vertice di chiusura ripetuto; None se degenere."""
    ring = [list(p) for p in points]
    if len(ring) > 1 and ring[0] == ring[-1]:
        ring.pop()
    dedup = [p for i, p in enumerate(ring) if p != ring[i - 1]]
    if len(dedup) < 3 or abs(signed_area(dedup)) < MIN_AREA:
        return None
    return dedup


def join_ways(segments):
    """Ricuce in anelli chiusi i tratti di un multipoligono (liste di (lon, lat))."""
    segments = [list(s) for s in segments if len(s) >= 2]
    rings = []
    while segments:
        ring = segments.pop(0)
        while ring[0] != ring[-1]:
            for i, seg in enumerate(segments):
                if seg[0] == ring[-1]:
                    ring += seg[1:]
                elif seg[-1] == ring[-1]:
                    ring += seg[-2::-1]
                else:
                    continue
                segments.pop(i)
                break
            else:
                break                           # anello aperto: dato incompleto, si scarta
        if ring[0] == ring[-1] and len(ring) >= 4:
            rings.append(ring)
    return rings


def lonlat_ring(geometry):
    return [(p["lon"], p["lat"]) for p in geometry]


def project_ring(ring_ll):
    return close_ring([to_xz(lon, lat) for lon, lat in ring_ll])


def multipolygon_polygons(relation):
    """Relazione multipoligono -> [[esterno, buco, ...], ...] in coordinate Godot."""
    outers, inners = [], []
    for m in relation.get("members", []):
        if m.get("type") != "way" or "geometry" not in m:
            continue
        (inners if m.get("role") == "inner" else outers).append(lonlat_ring(m["geometry"]))
    polygons = [[r] for r in (project_ring(r) for r in join_ways(outers)) if r]
    for hole in (project_ring(r) for r in join_ways(inners)):
        if not hole:
            continue
        for poly in polygons:
            if point_in_ring(hole[0], poly[0]):
                poly.append(hole)
                break
    return polygons


# ------------------- TAG -------------------
def clean_tags(tags):
    return {k: v for k, v in tags.items() if not k.startswith("name:") or k in KEPT_NAME_KEYS}


def parse_meters(value):
    try:
        return float(str(value).lower().replace("m", "").replace(",", ".").split(";")[0].strip())
    except ValueError:
        return None


def tag_height(tags):
    """(altezza, origine) con origine in height | levels | None."""
    h = parse_meters(tags["height"]) if "height" in tags else None
    if h and h > 0:
        return h, "height"
    lv = parse_meters(tags["building:levels"]) if "building:levels" in tags else None
    if lv and lv > 0:
        return lv * METERS_PER_LEVEL, "levels"
    return None, None


def tag_min_height(tags):
    h = parse_meters(tags["min_height"]) if "min_height" in tags else None
    if h is not None:
        return max(h, 0.0)
    lv = parse_meters(tags["building:min_level"]) if "building:min_level" in tags else None
    return max(lv, 0.0) * METERS_PER_LEVEL if lv is not None else 0.0


# Chi ha piu' tag vince il primo che corrisponde: l'ordine e' la priorita'.
POINT_KINDS = (
    ("natural", "tree", "tree"),
    ("man_made", "dolphin", "pile"),
    ("seamark:type", "pile", "pile"),
    ("man_made", "water_well", "well"),
    ("highway", "street_lamp", "lamp"),
    ("amenity", "bench", "bench"),
    ("amenity", "waste_basket", "waste_basket"),
    ("amenity", "drinking_water", "fountain"),
    ("amenity", "fountain", "fountain"),
    ("man_made", "water_tap", "fountain"),
    ("emergency", "fire_hydrant", "hydrant"),
    ("man_made", "flagpole", "flagpole"),
    ("amenity", "ferry_terminal", "ferry_stop"),
    ("historic", None, "monument"),
    ("tourism", "artwork", "monument"),
    ("barrier", None, "gate"),
    ("entrance", None, "entrance"),
)
POI_KEYS = ("shop", "amenity", "tourism", "office", "craft", "leisure", "emergency", "man_made")
AREA_KEYS = ("leisure", "landuse", "natural", "place")
PATH_KINDS = ("pedestrian", "footway", "steps", "path", "cycleway", "service", "residential", "living_street", "track", "corridor")


def point_kind(tags):
    for key, value, kind in POINT_KINDS:
        if key in tags and (value is None or tags[key] == value):
            return kind
    for key in POI_KEYS:
        if key in tags:
            return "poi"
    if "addr:housenumber" in tags:
        return "address"
    return "other"


# ------------------- ESTRAZIONE -------------------
def extract(doc, bbox):
    elements = doc["elements"]
    out = {name: [] for name in (
        "land", "water", "waterways", "buildings", "parts", "paths", "areas",
        "bridges", "piers", "barriers", "tree_rows", "points", "other")}

    s, w, n, e = bbox
    bbox_ring = [to_xz(w, n), to_xz(e, n), to_xz(e, s), to_xz(w, s)]
    bx0, bz0, bx1, bz1 = bounds(bbox_ring)

    def near_bbox(ring):
        x0, z0, x1, z1 = bounds(ring)
        return x1 >= bx0 - LAND_MARGIN and x0 <= bx1 + LAND_MARGIN and z1 >= bz0 - LAND_MARGIN and z0 <= bz1 + LAND_MARGIN

    def add_building(fid, tags, rings):
        h, src = tag_height(tags)
        layer = "parts" if "building:part" in tags else "buildings"
        out[layer].append({
            "id": fid, "kind": tags.get("building:part" if layer == "parts" else "building", "yes"),
            "rings": rings, "h": h, "h_src": src, "min_h": tag_min_height(tags), "tags": clean_tags(tags),
        })

    def add_area(fid, tags, rings):
        if tags.get("highway") == "pedestrian":
            kind = "pedestrian"
        else:
            kind = next((tags[k] for k in AREA_KEYS if k in tags), "area")
        out["areas"].append({"id": fid, "kind": kind, "rings": rings, "tags": clean_tags(tags)})

    # Nodo OSM -> (way dell'edificio, indice): serve a orientare gli ingressi lungo il muro.
    wall_of_node = {}

    # ---- way ----
    for el in elements:
        if el["type"] != "way" or "geometry" not in el:
            continue
        tags = el.get("tags", {})
        if not tags:
            continue                            # membro di una relazione: lo legge la relazione
        fid = f"way/{el['id']}"
        geom = el["geometry"]
        pts = [to_xz(p["lon"], p["lat"]) for p in geom]
        closed = len(geom) >= 4 and geom[0] == geom[-1]
        ring = close_ring(pts) if closed else None
        ctags = clean_tags(tags)
        width = parse_meters(tags["width"]) if "width" in tags else None
        used = False

        if tags.get("man_made") == "pier":
            item = {"id": fid, "w": width, "tags": ctags}
            item["rings" if ring else "pts"] = [ring] if ring else pts
            out["piers"].append(item)
            continue                            # un pontile non e' ne' edificio ne' strada

        if ("building" in tags or "building:part" in tags) and ring:
            add_building(fid, tags, [ring])
            if "building" in tags:
                for i, node_id in enumerate(el.get("nodes", [])[:-1]):
                    wall_of_node[node_id] = (pts, i)
            used = True
        if tags.get("man_made") == "bridge" and ring:
            out["bridges"].append({"id": fid, "rings": [ring], "tags": ctags})
            used = True
        if tags.get("natural") == "water" and ring:
            out["water"].append({"id": fid, "kind": tags.get("water", "water"), "name": tags.get("name"), "rings": [ring]})
            used = True
        if "waterway" in tags:
            out["waterways"].append({"id": fid, "kind": tags["waterway"], "name": tags.get("name"), "w": width, "pts": pts, "tags": ctags})
            used = True
        if tags.get("highway") in PATH_KINDS:
            if ring and tags.get("area") == "yes":
                add_area(fid, tags, [ring])
            else:
                out["paths"].append({
                    "id": fid, "kind": tags["highway"], "pts": pts, "n": el.get("nodes", []),
                    "bridge": tags.get("bridge", "no") != "no", "tunnel": tags.get("tunnel"),
                    "w": width, "tags": ctags,
                })
            used = True
        if "barrier" in tags:
            out["barriers"].append({"id": fid, "kind": tags["barrier"], "pts": pts, "h": parse_meters(tags["height"]) if "height" in tags else None, "tags": ctags})
            used = True
        if tags.get("natural") == "tree_row":
            out["tree_rows"].append({"id": fid, "pts": pts})
            used = True
        if tags.get("man_made") == "water_well" and ring:
            out["points"].append({"id": fid, "kind": "well", "p": [round(v, 2) for v in centroid(ring)], "tags": ctags})
            used = True
        if not used and ring and "highway" not in tags and tags.get("natural") not in ("water", "tree_row") \
                and any(k in tags for k in AREA_KEYS):
            add_area(fid, tags, [ring])
            used = True
        if not used:
            out["other"].append({"id": fid, "pts": pts, "closed": closed, "tags": ctags})

    # ---- relazioni ----
    for el in elements:
        if el["type"] != "relation":
            continue
        tags = el.get("tags", {})
        if tags.get("type") != "multipolygon":
            continue                            # type=building, site, waterway: restano nel grezzo
        fid = f"relation/{el['id']}"
        if tags.get("natural") == "water" and tags.get("water") == "lagoon":
            # La laguna e' UN poligono enorme; le isole sono i suoi buchi. Sono
            # loro il bordo preciso terra/acqua che al DEM a 10 m manca.
            inners = [lonlat_ring(m["geometry"]) for m in el["members"]
                      if m.get("type") == "way" and m.get("role") == "inner" and "geometry" in m]
            for i, ring in enumerate(r for r in (project_ring(r) for r in join_ways(inners)) if r):
                if near_bbox(ring):
                    out["land"].append({"id": f"{fid}#{i}", "rings": [ring]})
            continue
        for i, rings in enumerate(multipolygon_polygons(el)):
            pid = f"{fid}#{i}"
            if "building" in tags or "building:part" in tags:
                add_building(pid, tags, rings)
            elif tags.get("natural") == "water":
                out["water"].append({"id": pid, "kind": tags.get("water", "water"), "name": tags.get("name"), "rings": rings})
            elif tags.get("highway") == "pedestrian" or any(k in tags for k in AREA_KEYS):
                add_area(pid, tags, rings)

    # ---- nodi ----
    for el in elements:
        if el["type"] != "node":
            continue
        tags = el.get("tags", {})
        if not tags:
            continue
        item = {"id": f"node/{el['id']}", "kind": point_kind(tags), "p": to_xz(el["lon"], el["lat"]), "tags": clean_tags(tags)}
        if el["id"] in wall_of_node:
            pts, i = wall_of_node[el["id"]]
            a, b = pts[i - 1] if i > 0 else pts[-2], pts[i + 1]
            # yaw del MURO: Basis(UP, yaw) di Godot porta l'asse X locale lungo il muro.
            item["yaw"] = round(math.atan2(-(b[1] - a[1]), b[0] - a[0]), 4)
        out["points"].append(item)

    # ---- altezze mancanti e rapporto edificio / parti ----
    known = [b["h"] for b in out["buildings"] if b["h"]]
    default_h = round(statistics.median(known), 1) if known else FALLBACK_HEIGHT
    for b in out["buildings"]:
        if not b["h"]:
            b["h"], b["h_src"] = default_h, "default"
        b["_bounds"] = bounds(b["rings"][0])
        b["_area"] = abs(signed_area(b["rings"][0])) - sum(abs(signed_area(r)) for r in b["rings"][1:])
        b["_covered"] = 0.0
    for part in out["parts"]:
        c = centroid(part["rings"][0])
        parent = next((b for b in out["buildings"]
                       if b["_bounds"][0] <= c[0] <= b["_bounds"][2] and b["_bounds"][1] <= c[1] <= b["_bounds"][3]
                       and point_in_ring(c, b["rings"][0])), None)
        part["parent"] = parent["id"] if parent else None
        if parent and part["min_h"] == 0.0:
            parent["_covered"] += abs(signed_area(part["rings"][0]))
        if not part["h"]:
            # una parte senza altezza propria eredita quella dell'edificio
            part["h"], part["h_src"] = (parent["h"], "parent") if parent else (default_h, "default")
    for b in out["buildings"]:
        b["parts_cover"] = b["_covered"] >= PARTS_COVER_RATIO * b["_area"]
        for key in ("_bounds", "_area", "_covered"):
            del b[key]

    out["meta"] = {
        "crs": "EPSG:32632",
        "dem_origin": [DEM_ORIGIN_E, DEM_ORIGIN_N],
        "world_offset": WORLD_OFFSET,
        "bbox_lonlat_swne": bbox,
        # il riquadro in coordinate Godot (NO, NE, SE, SO): fuori da qui i dati sono incompleti
        "bbox_ring": bbox_ring,
        "default_height": default_h,
    }
    return out


# ------------------- MAIN -------------------
def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--bbox", required=True, help="sud,ovest,nord,est in gradi")
    ap.add_argument("--input", help="risposta grezza gia' scaricata (venice_osm.raw.json o export di overpass-turbo)")
    ap.add_argument("--out", default=os.path.join(HERE, "venice_osm.json"))
    ap.add_argument("--print-query", action="store_true", help="stampa la query da incollare in overpass-turbo ed esce")
    args = ap.parse_args()

    bbox = [float(v) for v in args.bbox.split(",")]
    if args.print_query:
        print(build_query(bbox))
        return

    if args.input:
        with open(args.input, "r", encoding="utf-8") as f:
            doc = json.load(f)
    else:
        print(f"Scarico da Overpass il riquadro {bbox} ...")
        doc = fetch_overpass(build_query(bbox))
        raw_path = os.path.splitext(args.out)[0] + ".raw.json"
        with open(raw_path, "w", encoding="utf-8") as f:
            json.dump(doc, f)
        print(f"  risposta grezza salvata in {raw_path} (riusabile con --input)")
    if "elements" not in doc:
        raise SystemExit("Formato non riconosciuto: serve il JSON grezzo OSM ('elements'), non il GeoJSON.")

    out = extract(doc, bbox)
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False, separators=(",", ":"))

    print("Livelli estratti:")
    for name, items in out.items():
        if name == "meta":
            continue
        detail = ""
        if name == "points":
            kinds = {}
            for p in items:
                kinds[p["kind"]] = kinds.get(p["kind"], 0) + 1
            detail = "  (" + ", ".join(f"{k} {v}" for k, v in sorted(kinds.items(), key=lambda kv: -kv[1])) + ")"
        elif name == "buildings":
            src = {}
            for b in items:
                src[b["h_src"]] = src.get(b["h_src"], 0) + 1
            detail = f"  (altezza: {src}; {sum(1 for b in items if b['parts_cover'])} descritti dalle parti; " \
                     f"{sum(1 for b in items if len(b['rings']) > 1)} con cortili)"
        elif name == "paths":
            detail = f"  ({sum(1 for p in items if p['bridge'])} tratti di ponte, {sum(1 for p in items if p['tunnel'])} in galleria/sottoportego)"
        print(f"  {name:10s} {len(items):5d}{detail}")
    print(f"Altezza di default (mediana degli edifici che la dichiarano): {out['meta']['default_height']} m")
    print(f"Scritto {args.out} ({os.path.getsize(args.out) / 1e6:.1f} MB)")


if __name__ == "__main__":
    sys.exit(main())
