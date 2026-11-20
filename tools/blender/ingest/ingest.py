"""Pipeline d'ingresso degli asset: UN comando, smistato per categoria (skill `asset-ingest`).

    "<blender 5.2>" --background --factory-startup --python tools/blender/ingest/ingest.py -- <manifesto.json> [preview] [noexport]

Il manifesto (tools/blender/ingest/manifests/<id>.json) dice cos'e' il modello sorgente, dove sta,
come e' orientato, quanto e' grande davvero, quali parti tenere e quali lasciare alla sola texture,
e la sezione della sua categoria:

  outfit  -> ingest/outfit.py   (vestiti ed equipaggiamento: adattamento al corpo, pesi, prima persona)
  boat    -> ingest/boat.py     (DWL dal dislocamento, HydroHull/collisioni, vuoti, materiali per nome,
                                 .glb + BoatConfig, controlli; skill `vehicles-boats` §9-13)
  weapon  -> da fare (bocca di fuoco, parti mobili, viewmodel)

Passi base comuni (ingest/base.py): carica, normalizza (assi e misura reale), semplifica, cuoce il
colore originale, lo porta nello stile del gioco. Con `preview` produce in %TEMP%/ingest/:
  <id>_compare.png   sorgente (sopra) e risultato (sotto) dalle stesse viste
e la sotto-pipeline aggiunge le sue anteprime.
"""

import json
import os
import sys

import bpy

try:
    PROJECT_DIR
except NameError:
    PROJECT_DIR = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(
        os.path.abspath(__file__))))).replace("\\", "/")
try:
    ARGV
except NameError:
    ARGV = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
for p in ("/tools/blender", "/tools/blender/wearables", "/tools/blender/ingest"):
    if PROJECT_DIR + p not in sys.path:
        sys.path.append(PROJECT_DIR + p)

import base  # noqa: E402
import charlib as C  # noqa: E402

PREVIEW = "preview" in ARGV
EXPORT = "noexport" not in ARGV
OUT_DIR = os.path.join(os.environ.get("TEMP", "/tmp"), "ingest")


COLOR_HINTS = ("basecolor", "base_color", "albedo", "diffuse", "color")


def link_textures(objs, texture_dir):
    """Sorgenti (FBX/OBJ) con le texture in una cartella a parte e i materiali senza immagine: a ogni
    materiale col Base Color scollegato si collega il file di `texture_dir` che contiene il nome del
    materiale e una parola da colore base (BaseColor, Albedo, Diffuse). La cottura le vuole tutte."""
    files = [f for f in os.listdir(texture_dir) if f.lower().endswith((".png", ".jpg", ".jpeg", ".tga", ".tif", ".tiff"))]
    linked = {}
    for mat in {m for o in objs for m in o.data.materials if m is not None}:
        if not mat.use_nodes:
            continue
        nt = mat.node_tree
        bsdf = next((n for n in nt.nodes if n.type == "BSDF_PRINCIPLED"), None)
        if bsdf is None or bsdf.inputs["Base Color"].is_linked:
            continue
        key = mat.name.lower().split(".")[0].replace(" ", "_")
        norm = {f: f.lower().replace(" ", "_").replace("-", "_") for f in files}
        match = next((f for hint in COLOR_HINTS for f in files
                      if key in norm[f] and hint in norm[f].replace("_", "")), None)
        if match is None:
            print("[ingest] ATTENZIONE: nessuna texture di colore per il materiale", mat.name)
            continue
        tex = nt.nodes.new("ShaderNodeTexImage")
        tex.image = bpy.data.images.load(os.path.join(texture_dir, match), check_existing=True)
        nt.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
        linked[mat.name] = match
    return linked


def run_base(manifest):
    """Passi comuni. Ritorna (oggetti sorgente, oggetto leggero stilizzato, report)."""
    report = {"id": manifest["id"], "category": manifest["category"]}
    high = base.load_source(manifest["source"])
    if manifest.get("texture_dir"):
        report["textures"] = link_textures(high, manifest["texture_dir"])
    report["source_tris"] = sum(base.tris(o) for o in high)
    report["scale"] = base.normalize(high, manifest["category"], manifest.get("source_front", "-Y"),
                                     manifest.get("source_up", "+Z"), manifest.get("size"),
                                     manifest.get("scale"))
    lo, hi = base.bounds(high)
    report["size_m"] = [round(float(x), 3) for x in (hi - lo)]
    low = base.simplify(high, manifest["parts"], manifest["budget"]["tris"], manifest.get("symmetry", False))
    report["tris"] = base.tris(low)
    tex = manifest.get("texture", {})
    color, occlusion = base.bake_color(high, low, tex.get("bake_size", 1024), tex.get("extrusion", 0.012),
                                       tex.get("ray_distance", 0.05), tex.get("ao", True))
    rgb = base.stylize(color, occlusion, tex.get("size", 256), tex.get("saturation", 0.82),
                       tex.get("value", 0.92), tex.get("contrast", 1.05), tex.get("ao_strength", 0.35))
    base.finish_material(low, manifest["id"], rgb)
    if PREVIEW:
        os.makedirs(OUT_DIR, exist_ok=True)
        C.make_image("tex", rgb, os.path.join(OUT_DIR, f"{manifest['id']}_texture.png"))
        base.compare_render(high, low, os.path.join(OUT_DIR, f"{manifest['id']}_compare.png"))
        report["compare"] = os.path.join(OUT_DIR, f"{manifest['id']}_compare.png")
    return high, low, report


def main():
    path = next(a for a in ARGV if a.endswith(".json"))
    if not os.path.isabs(path):
        path = os.path.join(PROJECT_DIR, path)
    manifest = json.load(open(path, encoding="utf-8"))
    C.reset_scene()
    high, low, report = run_base(manifest)
    category = manifest["category"]
    if category == "boat":
        # la barca legge ancora il pesante: involucro dello scafo, materiali sorgente, vetri
        import boat
        report.update(boat.run(high, low, manifest, PROJECT_DIR, export=EXPORT, preview_on=PREVIEW))
        for o in high:
            bpy.data.objects.remove(o)
        if not EXPORT:
            os.makedirs(OUT_DIR, exist_ok=True)
            out = os.path.join(OUT_DIR, f"{manifest['id']}_boat.blend")
            bpy.ops.wm.save_as_mainfile(filepath=out, compress=True)
            report["blend"] = out
        return report

    for o in high:
        bpy.data.objects.remove(o)          # il pesante non serve piu'
    if category == "outfit":
        import outfit
        report.update(outfit.run(low, manifest, PROJECT_DIR, export=EXPORT, preview_on=PREVIEW))
    else:
        # sotto-pipeline non ancora automatica: si consegna il modello base pronto
        os.makedirs(OUT_DIR, exist_ok=True)
        out = os.path.join(OUT_DIR, f"{manifest['id']}_base.blend")
        bpy.ops.wm.save_as_mainfile(filepath=out, compress=True)
        report["base_blend"] = out
        report["next"] = "weapon: sotto-pipeline da definire"
    return report


result = main()
print("RESULT", json.dumps(result))
