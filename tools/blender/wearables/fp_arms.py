"""Varianti in prima persona: braccia del corpo (e dei capi che le vestono) portate sullo scheletro
`FP_Arms` del Retro Weapon Pack, quello dei viewmodel `VM_*.glb`.

Il viewmodel tiene scheletro, clip e arma del pack (aggancio dell'arma, tacche e `AimPitchDegrees`
dipendono da li'), ma la mesh delle braccia la danno corpo e vestiti: cosi' in prima persona si
vede la propria pelle, la manica della giacca, i guanti.

Come: per ogni osso UAL del braccio (clavicola -> dita) c'e' l'osso FP corrispondente. Ogni vertice
viene trasformato con la media pesata (i suoi pesi UAL) delle trasformazioni osso -> osso:
rotazione che porta la terna UAL (direzione dell'osso + asse delle nocche indice->mignolo) su quella
FP, scala lungo l'osso pari al rapporto delle lunghezze (le braccia del pack sono ~18 % piu' corte,
sono fatte per stare a pochi centimetri dalla camera) e di sqrt(rapporto) di traverso. Poi i gruppi
di pesi prendono i nomi FP; il peso dell'avambraccio si divide fra i tre ossi di torsione.

Il riposo FP si legge da `VM_Rifle.glb` gia' nel repo: il pack non serve. Tutte le armi del pack
condividono lo stesso `FP_Arms.fbx`, quindi la stessa variante vale per ogni viewmodel.
"""

import math
import os

import bmesh
import bpy
import numpy as np
from mathutils import Matrix, Vector

import charlib as C

VM_SOURCE = "assets/models/weapons/VM_Rifle.glb"
FP_DIR = "assets/models/wearables/fp"

# UAL -> FP (lato sostituito a runtime). Le dita UAL _01 partono dalla nocca: corrispondono alle
# _01 FP; il metacarpo FP (`*Knuckle`) resta nel palmo, cioe' nell'osso della mano. Il pollice UAL
# comincia dal metacarpo: thumb_01 -> thumbKnuckle, e cosi' via.
BONE_MAP = {
    "clavicle": "shoulder", "upperarm": "upperArm", "lowerarm": "lowerArm", "hand": "hand",
    "index_01": "index_01", "index_02": "index_02", "index_03": "index_03",
    "middle_01": "middle_01", "middle_02": "middle_02", "middle_03": "middle_03",
    "ring_01": "ring_01", "ring_02": "ring_02", "ring_03": "ring_03",
    "pinky_01": "pinky_01", "pinky_02": "pinky_02", "pinky_03": "pinky_03",
    "thumb_01": "thumbKnuckle", "thumb_02": "thumb_01", "thumb_03": "thumb_02",
}
# segmento di ogni osso: giunto -> giunto figlio (o foglia)
UAL_CHILD = {"clavicle": "upperarm", "upperarm": "lowerarm", "lowerarm": "hand", "hand": "middle_01",
             "index_01": "index_02", "index_02": "index_03", "index_03": "index_04_leaf",
             "middle_01": "middle_02", "middle_02": "middle_03", "middle_03": "middle_04_leaf",
             "ring_01": "ring_02", "ring_02": "ring_03", "ring_03": "ring_04_leaf",
             "pinky_01": "pinky_02", "pinky_02": "pinky_03", "pinky_03": "pinky_04_leaf",
             "thumb_01": "thumb_02", "thumb_02": "thumb_03", "thumb_03": "thumb_04_leaf"}
FP_CHILD = {"shoulder": "upperArm", "upperArm": "lowerArm", "lowerArm": "hand", "hand": "middle_01",
            "index_01": "index_02", "index_02": "index_03", "index_03": None,
            "middle_01": "middle_02", "middle_02": "middle_03", "middle_03": None,
            "ring_01": "ring_02", "ring_02": "ring_03", "ring_03": None,
            "pinky_01": "pinky_02", "pinky_02": "pinky_03", "pinky_03": None,
            "thumbKnuckle": "thumb_01", "thumb_01": "thumb_02", "thumb_02": None}
TWIST = ("twist_1_arm", "twist_2_arm", "twist_3_arm")
TORSO = {"spine_01", "spine_02", "spine_03", "neck_01", "Head", "pelvis", "root"}


def _split(name):
    """'upperarm_l' -> ('upperarm', 'l'); osso senza lato -> (nome, None)."""
    if name.endswith(("_l", "_r")):
        return name[:-2], name[-1]
    return name, None


def load_fp_armature(project_dir):
    """Armatura `Arms` del viewmodel a riposo; tutto il resto importato viene eliminato."""
    before_actions = set(bpy.data.actions)
    objs = C.import_glb(C.project_path(project_dir, VM_SOURCE))
    arms = next(o for o in objs if o.type == "ARMATURE" and o.name.startswith("Arms"))
    for o in objs:
        if o is not arms:
            bpy.data.objects.remove(o)
    if arms.animation_data:
        arms.animation_data.action = None
    for pb in arms.pose.bones:
        pb.matrix_basis.identity()
    for a in set(bpy.data.actions) - before_actions:
        bpy.data.actions.remove(a)
    bpy.context.view_layer.update()
    return arms


def _frames(heads, tails_of, lateral):
    """Per ogni osso: (testa, direzione, lunghezza, terna)."""
    out = {}
    for bone, (h, t) in heads.items():
        d = t - h
        out[bone] = (h, d.normalized(), d.length, C.frame_from(d, lateral[bone]))
    return out


def bone_transforms(ual_arm, fp_arm):
    """Matrici 4x4 osso UAL -> spazio FP per i soli ossi del braccio, piu' le info per la torsione."""
    uj = {b.name: ual_arm.matrix_world @ b.head_local for b in ual_arm.data.bones}
    fj = {b.name: fp_arm.matrix_world @ b.head_local for b in fp_arm.data.bones}
    ft = {b.name: fp_arm.matrix_world @ b.tail_local for b in fp_arm.data.bones}
    mats = {}
    for side in ("l", "r"):
        u_lat = uj[f"index_01_{side}"] - uj[f"pinky_01_{side}"]
        f_lat = fj[f"index_01_{side}"] - fj[f"pinky_01_{side}"]
        for ub, fb in BONE_MAP.items():
            uh = uj[f"{ub}_{side}"]
            ut = uj[f"{UAL_CHILD[ub]}_{side}"]
            fh = fj[f"{fb}_{side}"]
            fc = FP_CHILD[fb]
            fte = fj[f"{fc}_{side}"] if fc else ft[f"{fb}_{side}"]
            du, df = ut - uh, fte - fh
            rot = C.frame_from(df, f_lat) @ C.frame_from(du, u_lat).inverted()
            s = df.length / max(du.length, 1e-5)
            p = math.sqrt(s)
            axis = du.normalized()
            # scala: s lungo l'asse dell'osso, p di traverso (nello spazio UAL)
            scale = Matrix.Identity(3) * p + (s - p) * Matrix(
                [[axis[i] * axis[j] for j in range(3)] for i in range(3)])
            m = Matrix.Translation(fh) @ (rot @ scale).to_4x4() @ Matrix.Translation(-uh)
            mats[f"{ub}_{side}"] = m
        # avambraccio UAL e i tre ossi di torsione FP: si distribuisce lungo l'asse
        mats[f"_forearm_{side}"] = (uj[f"lowerarm_{side}"], uj[f"hand_{side}"])
    return mats


def conform(src, ual_arm, fp_arm, name):
    """Copia di `src` (skinnata su UAL, a riposo) con le sole parti del braccio, portata sullo
    scheletro FP. None se non resta nulla."""
    mats = bone_transforms(ual_arm, fp_arm)
    weights = C.vertex_weights(src)
    arm_bones = set(BONE_MAP)
    keep = []
    for ws in weights:
        dom = C.dominant_bone(ws) or ""
        base, side = _split(dom)
        keep.append(side is not None and base in arm_bones)
    if not any(keep):
        return None

    me = src.data.copy()
    obj = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(obj)
    obj.matrix_world = src.matrix_world.copy()
    obj.vertex_groups.clear()

    new_w = []
    mw = src.matrix_world
    for i, v in enumerate(me.vertices):
        if not keep[i]:
            new_w.append({})
            continue
        p = mw @ v.co
        side = "l" if p.x > 0 else "r"           # nel corpo UAL la sinistra e' +X
        acc = Vector()
        total = 0.0
        out = {}
        for bone, w in weights[i]:
            base, bside = _split(bone)
            if bside is None or base not in arm_bones:
                # busto, collo: segue la clavicola del suo lato e si pesa sulla spina FP
                key = f"clavicle_{side}"
                out["spine"] = out.get("spine", 0.0) + w
            else:
                key = bone
                if base == "lowerarm":
                    a, b = mats[f"_forearm_{bside}"]
                    t = max(0.0, min(1.0, (p - a).dot(b - a) / (b - a).length_squared))
                    for k, tw in enumerate(TWIST):
                        c = (k + 0.5) / 3
                        f = max(0.0, 1 - abs(t - c) * 3)
                        if f > 0:
                            out[f"{tw}_{bside}"] = out.get(f"{tw}_{bside}", 0.0) + w * f
                else:
                    fp_name = f"{BONE_MAP[base]}_{bside}"
                    out[fp_name] = out.get(fp_name, 0.0) + w
            acc += (mats[key] @ p) * w
            total += w
        v.co = mw.inverted() @ (acc / total if total else p)
        new_w.append(out)

    groups = {}
    for i, ws in enumerate(new_w):
        s = sum(ws.values())
        for bone, w in ws.items():
            if bone not in groups:
                groups[bone] = obj.vertex_groups.new(name=bone)
            groups[bone].add([i], w / s, "REPLACE")

    # via i vertici che non sono braccio
    bm = bmesh.new()
    bm.from_mesh(me)
    bm.verts.ensure_lookup_table()
    bmesh.ops.delete(bm, geom=[bm.verts[i] for i, k in enumerate(keep) if not k], context="VERTS")
    bm.to_mesh(me)
    bm.free()
    C.limit_and_normalize(obj, 4)
    obj.parent = fp_arm
    obj.matrix_world = Matrix.Identity(4)
    mod = obj.modifiers.new("Armature", "ARMATURE")
    mod.object = fp_arm
    return obj


def export_variant(project_dir, src, item_id, out_path=None):
    """Esporta la variante FP di `src` (armatura UAL = suo genitore). Ritorna il percorso o None."""
    ual_arm = src.parent
    fp_arm = load_fp_armature(project_dir)
    obj = conform(src, ual_arm, fp_arm, item_id + "_FP")
    result = None
    if obj is not None:
        rel = out_path or f"{FP_DIR}/{item_id}_FP.glb"
        C.export_glb(C.project_path(project_dir, rel), [fp_arm, obj])
        result = rel
        bpy.data.objects.remove(obj)
    bpy.data.objects.remove(fp_arm)
    return result
