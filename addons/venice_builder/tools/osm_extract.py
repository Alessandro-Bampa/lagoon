"""
OpenStreetMap -> dati per generare la mappa di Venezia (test_map_ve.tscn).

Scarica da Overpass (lo stesso backend di https://overpass-turbo.eu/) TUTTO
quello che OSM sa di un riquadro lat/lon, lo proietta nello stesso sistema del
DEM (UTM 32N) e lo riscrive in coordinate mondo Godot, diviso per livelli:

    land        isole: il bordo preciso terra/acqua (anelli interni della laguna)
    lagoon      la laguna come area: esterno + buchi (isole) vicini al riquadro
    coast       linea di costa verso il mare (natural=coastline): terra a SINISTRA
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

  Solo copertura del suolo e acque (niente edifici, strade, negozi), scaricata
  a tasselli: e' la sorgente del terreno (osm_terrain.py) su riquadri grandi,
  dove la query completa sarebbe enorme:
      python osm_extract.py --landcover --bbox 45.418,12.300,45.512,12.470 --out lagoon_osm.json

  Poi:
      osm_terrain.py         quote e classi del terreno dai livelli land/lagoon/coast/water/areas
      terrain_import.gd      region di Terrain3D da quei tile
      build_map_scene.gd     genera venice_map.scn (POC in demo/venice)

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
    # l'istanza usata dall'interfaccia di overpass-turbo.eu: il 17/11/2026 rispondeva in un
    # secondo mentre overpass-api.de, private.coffee e mail.ru davano solo 502/504
    "https://api.overpass-turbo.eu/interpreter",
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


def transverse_mercator(lon_deg, lat_deg, lon0_rad, k0):
    """(est dal meridiano centrale, nord dall'equatore) in metri, ellissoide WGS84."""
    lat = math.radians(lat_deg)
    dlon = math.radians(lon_deg) - lon0_rad
    c = 2.0 * math.sqrt(_N) / (1.0 + _N)
    t = math.sinh(math.atanh(math.sin(lat)) - c * math.atanh(c * math.sin(lat)))
    xi = math.atan2(t, math.cos(dlon))
    eta = math.atanh(math.sin(dlon) / math.sqrt(1.0 + t * t))
    e, n = eta, xi
    for j, a in enumerate(_ALPHA, start=1):
        e += a * math.cos(2 * j * xi) * math.sinh(2 * j * eta)
        n += a * math.sin(2 * j * xi) * math.cosh(2 * j * eta)
    return k0 * _AA * e, k0 * _AA * n


def lonlat_to_utm32(lon_deg, lat_deg):
    e, n = transverse_mercator(lon_deg, lat_deg, _LON0, _K0)
    return 500000.0 + e, n


# Georeferenziazione attiva. Di default quella del vecchio POC (UTM 32 + scostamento
# del DEM); una mappa vera la sostituisce con configure_georef() leggendo il suo
# map.json: trasversa di Mercatore LOCALE centrata sulla mappa, origine Godot nel
# centro, X verso est e Z verso sud, scala 1 (niente convergenza al centro).
_GEOREF = {"mode": "legacy"}


def configure_georef(georef):
    """georef = {"lon0": gradi, "lat0": gradi} dal map.json della mappa."""
    lon0 = math.radians(georef["lon0"])
    e0, n0 = transverse_mercator(georef["lon0"], georef["lat0"], lon0, 1.0)
    _GEOREF.clear()
    _GEOREF.update(mode="local_tm", lon0=lon0, e0=e0, n0=n0)


def to_xz(lon_deg, lat_deg):
    """(lon, lat) WGS84 -> [X, Z] mondo Godot, al centimetro. Y la mette il terreno."""
    if _GEOREF["mode"] == "local_tm":
        e, n = transverse_mercator(lon_deg, lat_deg, _GEOREF["lon0"], 1.0)
        return [round(e - _GEOREF["e0"], 2), round(_GEOREF["n0"] - n, 2)]
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


def build_landcover_query(bbox, relations=True, ways=True):
    """Solo cio' che serve al terreno: coperture, acque, costa, bricole.

    Le relazioni (la laguna e' UN multipoligono con migliaia di buchi) si
    chiedono una volta sola per tutto il riquadro; way e nodi a tasselli.
    """
    s, w, n, e = bbox
    box = f"{s},{w},{n},{e}"
    parts = []
    if ways:
        parts.append(f'  way({box})[~"^(natural|landuse|wetland|water|waterway|leisure|tidal)$"~"."];')
        parts.append(f'  node({box})[~"^(man_made|seamark:type)$"~"^(dolphin|pile)$"];')
    if relations:
        parts.append(f'  relation({box})[type=multipolygon][~"^(natural|landuse|wetland|water|leisure)$"~"."];')
    return "[out:json][timeout:180];\n(\n" + "\n".join(parts) + "\n);\nout geom;\n"


def fetch_landcover(bbox, tile_deg, cache_dir):
    """Scarica a tasselli e unisce per (tipo, id): un elemento a cavallo di due
    tasselli arriva due volte, identico.

    Ogni risposta finisce in cache_dir: Overpass e' spesso sovraccarico e un
    tassello che fallisce non deve far ripartire da zero. Rilanciare lo stesso
    comando riprende da dove si era fermato; per riscaricare, cancellare la cartella."""
    s, w, n, e = bbox
    os.makedirs(cache_dir, exist_ok=True)
    merged = {}

    def cached(name, query):
        path = os.path.join(cache_dir, name + ".json")
        if os.path.exists(path):
            with open(path, "r", encoding="utf-8") as f:
                return json.load(f)
        doc = fetch_overpass(query, attempts=8)
        with open(path, "w", encoding="utf-8") as f:
            json.dump(doc, f)
        return doc

    def add(doc):
        for el in doc.get("elements", []):
            merged[(el["type"], el["id"])] = el

    print("  relazioni (tutto il riquadro)...")
    add(cached("relations", build_landcover_query(bbox, relations=True, ways=False)))
    step = tile_deg if tile_deg > 0 else max(n - s, e - w)
    lats = [s + i * step for i in range(math.ceil((n - s) / step - 1e-9))] or [s]
    lons = [w + i * step for i in range(math.ceil((e - w) / step - 1e-9))] or [w]
    for i, la in enumerate(lats):
        for j, lo in enumerate(lons):
            tile = [round(la, 5), round(lo, 5), round(min(la + step, n), 5), round(min(lo + step, e), 5)]
            print(f"  tassello {i * len(lons) + j + 1}/{len(lats) * len(lons)} {tile}")
            add(cached("ways_" + "_".join(f"{v:.5f}" for v in tile), build_landcover_query(tile, relations=False, ways=True)))
    return {"version": 0.6, "generator": "osm_extract.py --landcover", "elements": list(merged.values())}


def fetch_overpass(query, attempts=4):
    data = urllib.parse.urlencode({"data": query}).encode("utf-8")
    last_error = None
    for attempt in range(attempts):
        url = OVERPASS_URLS[attempt % len(OVERPASS_URLS)]
        try:
            req = urllib.request.Request(url, data=data, headers={"User-Agent": "osm-map-poc/0.1"})
            with urllib.request.urlopen(req, timeout=240) as resp:
                doc = json.loads(resp.read().decode("utf-8"))
            # Un timeout lato server arriva come 200 con dati TRONCATI e un "remark":
            # va trattato come un errore, non come una risposta.
            if "error" in doc.get("remark", "").lower():
                raise ValueError(doc["remark"])
            return doc
        except (urllib.error.URLError, TimeoutError, ValueError) as exc:
            last_error = exc
            # attesa crescente: i server pubblici rispondono 502/504 quando sono pieni
            wait = min(8 * 2 ** (attempt // len(OVERPASS_URLS)), 120)
            print(f"  {url}: {exc} - riprovo fra {wait} s", flush=True)
            time.sleep(wait)
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
def extract(doc, bbox, data_ring_lonlat=None):
    elements = doc["elements"]
    out = {name: [] for name in (
        "land", "lagoon", "coast", "water", "waterways", "buildings", "parts", "paths", "areas",
        "bridges", "piers", "barriers", "tree_rows", "points", "other")}

    s, w, n, e = bbox
    if data_ring_lonlat:
        # mappa vera: i dati coprono il poligono, non il riquadro che lo contiene
        bbox_ring = [to_xz(lon, lat) for lon, lat in data_ring_lonlat]
    else:
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
            out["water"].append({"id": fid, "kind": tags.get("water", "water"), "name": tags.get("name"), "rings": [ring], "tags": ctags})
            if tags.get("water") == "lagoon":
                out["lagoon"].append({"id": fid, "rings": [ring]})
            used = True
        if tags.get("natural") == "coastline":
            out["coast"].append({"id": fid, "pts": pts})
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
            members = [m for m in el["members"] if m.get("type") == "way" and "geometry" in m]
            inners = [lonlat_ring(m["geometry"]) for m in members if m.get("role") == "inner"]
            holes = []
            for i, ring in enumerate(r for r in (project_ring(r) for r in join_ways(inners)) if r):
                if near_bbox(ring):
                    out["land"].append({"id": f"{fid}#{i}", "rings": [ring]})
                    holes.append(ring)
            # La stessa laguna come area (esterno + i soli buchi vicini al
            # riquadro): fuori dall'esterno e' terraferma o mare, non laguna.
            # Niente multipolygon_polygons: con migliaia di buchi contro un
            # esterno di decine di migliaia di vertici il test punto-in-anello
            # costerebbe minuti; con un solo esterno non serve proprio.
            outers = [r for r in (project_ring(r) for r in join_ways(
                [lonlat_ring(m["geometry"]) for m in members if m.get("role") != "inner"])) if r and near_bbox(r)]
            for i, outer in enumerate(outers):
                own = holes if len(outers) == 1 else [h for h in holes if point_in_ring(h[0], outer)]
                out["lagoon"].append({"id": f"{fid}#{i}", "rings": [outer] + own})
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
        "bbox_lonlat_swne": bbox,
        # il riquadro (o il poligono della mappa) in coordinate Godot: fuori i dati sono incompleti
        "bbox_ring": bbox_ring,
        "default_height": default_h,
    }
    if _GEOREF["mode"] == "local_tm":
        out["meta"]["georef"] = {"projection": "local_tm", "lon0": math.degrees(_GEOREF["lon0"])}
    else:
        out["meta"].update(crs="EPSG:32632", dem_origin=[DEM_ORIGIN_E, DEM_ORIGIN_N], world_offset=WORLD_OFFSET)
    return out


# ------------------- MAPPA VERA (map.json) -------------------
def poly_filter(ring_lonlat):
    return 'poly:"' + " ".join(f"{lat} {lon}" for lon, lat in ring_lonlat) + '"'


def fetch_map(ring_lonlat, bbox, tile_deg, cache_dir):
    """TUTTO cio' che OSM sa dentro il poligono: nodi con tag, ogni way, le relazioni
    di geometria (come build_query), a tasselli e con la cache compressa in cache_dir.
    Rilanciare riprende dai tasselli gia' scaricati; cancellare la cartella per rifare."""
    import gzip
    os.makedirs(cache_dir, exist_ok=True)
    poly = poly_filter(ring_lonlat)
    merged = {}

    def cached(name, query):
        path = os.path.join(cache_dir, name + ".json.gz")
        if os.path.exists(path):
            with gzip.open(path, "rt", encoding="utf-8") as f:
                return json.load(f)
        doc = fetch_overpass(query, attempts=8)
        with gzip.open(path + ".part", "wt", encoding="utf-8") as f:
            json.dump(doc, f)
        os.replace(path + ".part", path)
        time.sleep(1.0)                          # gentilezza verso il server pubblico
        return doc

    def add(doc):
        for el in doc.get("elements", []):
            merged[(el["type"], el["id"])] = el

    print("  relazioni (tutto il poligono)...", flush=True)
    add(cached("relations", (
        "[out:json][timeout:600][maxsize:2000000000];\n"
        f'relation({poly})[type!~"^(boundary|route|route_master|network|superroute|restriction|public_transport)$"];\n'
        "out geom;\n")))
    s, w, n, e = bbox
    lats = [s + i * tile_deg for i in range(math.ceil((n - s) / tile_deg - 1e-9))]
    lons = [w + i * tile_deg for i in range(math.ceil((e - w) / tile_deg - 1e-9))]
    total = len(lats) * len(lons)
    for i, la in enumerate(lats):
        for j, lo in enumerate(lons):
            tile = [round(la, 5), round(lo, 5), round(min(la + tile_deg, n), 5), round(min(lo + tile_deg, e), 5)]
            box = ",".join(f"{v:.5f}" for v in tile)
            print(f"  tassello {i * len(lons) + j + 1}/{total} {tile}", flush=True)
            add(cached("tile_" + box.replace(",", "_"), (
                "[out:json][timeout:600][maxsize:2000000000];\n(\n"
                f"  node({poly})({box})(if:count_tags() > 0);\n"
                f"  way({poly})({box});\n"
                ");\nout geom;\n")))
    print(f"  {len(merged)} elementi unici", flush=True)
    return {"version": 0.6, "generator": "osm_extract.py --map", "elements": list(merged.values())}


def write_layers(out, layers_dir):
    """Un file per livello ({"<livello>": [...], "meta": {...}}): chi serve un solo
    livello (il terreno, gli edifici di un settore) non deve leggere tutto."""
    os.makedirs(layers_dir, exist_ok=True)
    for name, items in out.items():
        if name == "meta":
            continue
        with open(os.path.join(layers_dir, name + ".json"), "w", encoding="utf-8") as f:
            json.dump({name: items, "meta": out["meta"]}, f, ensure_ascii=False, separators=(",", ":"))
    with open(os.path.join(layers_dir, "meta.json"), "w", encoding="utf-8") as f:
        json.dump(out["meta"], f, ensure_ascii=False, indent=1)


# ------------------- MAIN -------------------
def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--map", help="map.json di una mappa vera: poligono, georeferenziazione e cartelle (ignora --bbox/--out)")
    ap.add_argument("--bbox", help="sud,ovest,nord,est in gradi")
    ap.add_argument("--input", help="risposta grezza gia' scaricata (venice_osm.raw.json o export di overpass-turbo)")
    ap.add_argument("--out", default=os.path.join(HERE, "venice_osm.json"))
    ap.add_argument("--print-query", action="store_true", help="stampa la query da incollare in overpass-turbo ed esce")
    ap.add_argument("--landcover", action="store_true",
                    help="solo coperture del suolo, acque, costa e bricole (sorgente di osm_terrain.py), a tasselli")
    ap.add_argument("--tile", type=float, default=None, help="lato dei tasselli in gradi (default 0.03 con --landcover, 0.05 con --map; 0 = uno solo)")
    args = ap.parse_args()

    if args.map:
        return main_map(args)
    if not args.bbox:
        ap.error("serve --bbox (oppure --map)")
    bbox = [float(v) for v in args.bbox.split(",")]
    if args.print_query:
        print(build_landcover_query(bbox) if args.landcover else build_query(bbox))
        return

    if args.input:
        with open(args.input, "r", encoding="utf-8") as f:
            doc = json.load(f)
    else:
        print(f"Scarico da Overpass il riquadro {bbox} ...")
        doc = fetch_landcover(bbox, 0.03 if args.tile is None else args.tile, os.path.splitext(args.out)[0] + ".cache") if args.landcover else fetch_overpass(build_query(bbox))
        raw_path = os.path.splitext(args.out)[0] + ".raw.json"
        with open(raw_path, "w", encoding="utf-8") as f:
            json.dump(doc, f)
        print(f"  risposta grezza salvata in {raw_path} (riusabile con --input)")
    if "elements" not in doc:
        raise SystemExit("Formato non riconosciuto: serve il JSON grezzo OSM ('elements'), non il GeoJSON.")

    out = extract(doc, bbox)
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False, separators=(",", ":"))
    report(out)
    print(f"Scritto {args.out} ({os.path.getsize(args.out) / 1e6:.1f} MB)")


def main_map(args):
    """Mappa vera: <mappa>/osm/raw (tasselli grezzi compressi, lo snapshot) e
    <mappa>/osm/layers (un JSON per livello in coordinate Godot della mappa)."""
    map_dir = os.path.dirname(os.path.abspath(args.map))
    with open(args.map, "r", encoding="utf-8") as f:
        cfg = json.load(f)
    configure_georef(cfg["georef"])
    ring = cfg["polygon_lonlat"]
    lons, lats = [p[0] for p in ring], [p[1] for p in ring]
    bbox = [min(lats), min(lons), max(lats), max(lons)]
    osm_dir = os.path.join(map_dir, cfg["dirs"]["osm"])
    os.makedirs(osm_dir, exist_ok=True)
    # .gdignore: l'editor non scandisce ne' importa centinaia di MB di JSON (FileAccess li legge lo stesso)
    open(os.path.join(osm_dir, ".gdignore"), "w").close()
    tile = args.tile or 0.05
    print(f"Mappa {cfg['name']}: poligono di {len(ring)} vertici, riquadro {bbox}, tasselli da {tile} gradi", flush=True)
    doc = fetch_map(ring, bbox, tile, os.path.join(osm_dir, "raw"))
    out = extract(doc, bbox, data_ring_lonlat=ring)
    out["meta"]["map"] = cfg["name"]
    write_layers(out, os.path.join(osm_dir, "layers"))
    report(out)
    print(f"Livelli scritti in {os.path.join(osm_dir, 'layers')}")


def report(out):
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
        print(f"  {name:10s} {len(items):6d}{detail}")
    print(f"Altezza di default (mediana degli edifici che la dichiarano): {out['meta']['default_height']} m")


if __name__ == "__main__":
    sys.exit(main())
