#!/usr/bin/env python3
"""Valida assets/models/Body_Base.glb leggendo direttamente il binario.

Verifica indipendente da Blender: se l'esportatore sbagliasse scala, skin o
attributi, questo script se ne accorge comunque. Esce con codice != 0 se un
controllo fallisce.

Uso:
    python tools/blender/verify_glb.py [percorso.glb]
"""

import json
import struct
import sys

DEFAULT_PATH = "assets/models/Body_Base.glb"

HEIGHT_RANGE = (1.75, 1.80)
TRI_BUDGET = (6000, 10000)

EXPECTED_BONES = [
    "Hips", "Spine", "Spine1", "Spine2", "Neck", "Head",
    "LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand",
    "RightShoulder", "RightArm", "RightForeArm", "RightHand",
    "LeftUpLeg", "LeftLeg", "LeftFoot", "LeftToeBase",
    "RightUpLeg", "RightLeg", "RightFoot", "RightToeBase",
]


def read_glb(path):
    """Estrae il chunk JSON di un file .glb."""
    with open(path, "rb") as handle:
        data = handle.read()

    magic, version, total = struct.unpack_from("<4sII", data, 0)
    if magic != b"glTF":
        raise ValueError("Non e' un file GLB: magic {!r}".format(magic))
    if version != 2:
        raise ValueError("Versione glTF attesa 2, trovata {}".format(version))
    if total != len(data):
        raise ValueError("Lunghezza dichiarata {} != dimensione reale {}".format(total, len(data)))

    offset = 12
    gltf = None
    while offset < len(data):
        length, kind = struct.unpack_from("<I4s", data, offset)
        offset += 8
        if kind == b"JSON":
            gltf = json.loads(data[offset:offset + length].decode("utf-8"))
        offset += length
    if gltf is None:
        raise ValueError("Chunk JSON assente")
    return gltf


def verify(path):
    gltf = read_glb(path)
    nodes = gltf.get("nodes", [])
    meshes = gltf.get("meshes", [])
    accessors = gltf.get("accessors", [])
    skins = gltf.get("skins", [])
    checks = []
    info = {}

    def check(name, ok, detail=""):
        checks.append((name, bool(ok), detail))

    # --- Scala: nessun nodo deve introdurre un fattore (il classico 0.01) ---
    scaled = []
    for node in nodes:
        s = node.get("scale")
        if s is not None and any(abs(c - 1.0) > 1e-6 for c in s):
            scaled.append((node.get("name"), s))
        m = node.get("matrix")
        if m is not None:
            identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]
            if any(abs(a - b) > 1e-6 for a, b in zip(m, identity)):
                scaled.append((node.get("name"), "matrix"))
    check("nessun nodo con scala != 1", not scaled, str(scaled))

    # --- Dimensioni reali dagli accessor POSITION ---
    prim = meshes[0]["primitives"][0]
    pos = accessors[prim["attributes"]["POSITION"]]
    lo, hi = pos["min"], pos["max"]
    # glTF e' Y-up: l'altezza del personaggio e' lungo Y.
    height = hi[1] - lo[1]
    info["bbox_min"] = [round(v, 5) for v in lo]
    info["bbox_max"] = [round(v, 5) for v in hi]
    info["height_m"] = round(height, 5)
    check("altezza in {}-{} m".format(*HEIGHT_RANGE), HEIGHT_RANGE[0] <= height <= HEIGHT_RANGE[1],
          "{:.4f} m".format(height))
    check("piedi a y=0", abs(lo[1]) < 1e-3, "min.y = {:.5f}".format(lo[1]))

    # --- Geometria ---
    check("primitive triangolari", prim.get("mode", 4) == 4, "mode={}".format(prim.get("mode", 4)))
    tris = accessors[prim["indices"]]["count"] // 3
    info["tris"] = tris
    check("tris in {}-{}".format(*TRI_BUDGET), TRI_BUDGET[0] <= tris <= TRI_BUDGET[1], str(tris))
    check("UV presenti (TEXCOORD_0)", "TEXCOORD_0" in prim["attributes"])
    check("normali presenti", "NORMAL" in prim["attributes"])

    # --- Skinning ---
    attrs = prim["attributes"]
    joint_sets = [k for k in attrs if k.startswith("JOINTS_")]
    weight_sets = [k for k in attrs if k.startswith("WEIGHTS_")]
    info["joint_sets"] = sorted(joint_sets)
    check("JOINTS_0/WEIGHTS_0 presenti", "JOINTS_0" in attrs and "WEIGHTS_0" in attrs)
    # Un solo set => al massimo 4 influenze per vertice, per definizione glTF.
    check("un solo set di influenze (max 4/vertice)",
          len(joint_sets) == 1 and len(weight_sets) == 1,
          "joints={} weights={}".format(joint_sets, weight_sets))

    check("skin presente", len(skins) == 1, "{} skin".format(len(skins)))
    joint_names = [nodes[i].get("name", "?") for i in skins[0]["joints"]] if skins else []
    info["joint_count"] = len(joint_names)
    missing = [b for b in EXPECTED_BONES if b not in joint_names]
    check("bone Mixamo attesi presenti", not missing, "mancanti: {}".format(missing))
    check("nessun prefisso mixamorig:", not any(n.startswith("mixamorig") for n in joint_names))

    # --- Gerarchia: ogni bone atteso deve avere il parent giusto ---
    parent_of = {}
    for i, node in enumerate(nodes):
        for child in node.get("children", []):
            parent_of[child] = node.get("name")
    index_of = {n.get("name"): i for i, n in enumerate(nodes)}
    expected_parents = {
        "Spine": "Hips", "Spine1": "Spine", "Spine2": "Spine1",
        "Neck": "Spine2", "Head": "Neck",
        "LeftShoulder": "Spine2", "LeftArm": "LeftShoulder",
        "LeftForeArm": "LeftArm", "LeftHand": "LeftForeArm",
        "RightShoulder": "Spine2", "RightArm": "RightShoulder",
        "RightForeArm": "RightArm", "RightHand": "RightForeArm",
        "LeftUpLeg": "Hips", "LeftLeg": "LeftUpLeg",
        "LeftFoot": "LeftLeg", "LeftToeBase": "LeftFoot",
        "RightUpLeg": "Hips", "RightLeg": "RightUpLeg",
        "RightFoot": "RightLeg", "RightToeBase": "RightFoot",
    }
    wrong = []
    for bone, want in expected_parents.items():
        got = parent_of.get(index_of.get(bone))
        if got != want:
            wrong.append("{}: atteso {}, trovato {}".format(bone, want, got))
    check("gerarchia Mixamo corretta", not wrong, "; ".join(wrong))

    check("nessuna animazione esportata", not gltf.get("animations"))
    check("nessuna camera/luce esportata",
          not gltf.get("cameras") and "KHR_lights_punctual" not in gltf.get("extensionsUsed", []))

    return checks, info


def main(argv):
    path = argv[1] if len(argv) > 1 else DEFAULT_PATH
    try:
        checks, info = verify(path)
    except (OSError, ValueError, KeyError, IndexError) as exc:
        print("FALLITO: impossibile validare {}: {}".format(path, exc))
        return 1

    print("== {} ==".format(path))
    for key, value in info.items():
        print("  {}: {}".format(key, value))
    print()
    failed = 0
    for name, ok, detail in checks:
        mark = "OK  " if ok else "FAIL"
        line = "  [{}] {}".format(mark, name)
        if detail and not ok:
            line += " -> {}".format(detail)
        print(line)
        failed += not ok

    print()
    print("{}/{} controlli superati".format(len(checks) - failed, len(checks)))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
