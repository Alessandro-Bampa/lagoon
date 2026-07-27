"""Funzioni condivise per l'import delle clip Mixamo.

Usato sia da `import_mixamo_animation.py` (una clip -> un .glb) sia da
`build_animation_library.py` (tutte le clip -> una libreria unica). La logica
delicata — prefisso, scala del bacino, verifica "in place" — vive qui una volta
sola: sono le tre cose su cui e' facile sbagliare, vedi la skill blender-pipeline.

Gli script eseguiti dentro Blender lo importano cosi':

    import sys
    sys.path.append("c:/repositories/lagoon/tools/blender")
    import mixamo_common
"""

import re

import bpy

PREFIX = re.compile(r"^mixamorig\d*:")

PROJECT_DIR = "c:/repositories/lagoon"
BLEND_PATH = PROJECT_DIR + "/assets/models/source/Body_Base.blend"
ANIM_DIR = PROJECT_DIR + "/assets/models/animations"

ARMATURE_NAME = "Armature_Character"
OUR_HEIGHT = 1.78

# Traslazione ORIZZONTALE del bacino oltre la quale la clip non e' "in place".
IN_PLACE_MAX_M = 0.25


def view3d_override(**extra):
    """temp_override con un'area VIEW_3D: senza, gli operatori con poll() falliscono."""
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


def action_fcurves(action):
    """Blender 5.x: le fcurve stanno in layers/strips/channelbag, non in action.fcurves."""
    if hasattr(action, "fcurves"):
        return list(action.fcurves)
    curves = []
    for layer in action.layers:
        for strip in layer.strips:
            for bag in strip.channelbags:
                curves.extend(bag.fcurves)
    return curves


def import_clip(fbx_path, clip_name):
    """Importa un FBX Mixamo e restituisce (action, oggetti_importati, diagnostica).

    Toglie il prefisso, riscala la traslazione del bacino e misura se la clip e'
    davvero "in place". NON cancella gli oggetti importati: se ne occupa il chiamante,
    che potrebbe volerne leggere altro prima.
    """
    before = set(bpy.data.objects.keys())
    with view3d_override():
        bpy.ops.import_scene.fbx(filepath=fbx_path)
    imported = [bpy.data.objects[n] for n in bpy.data.objects.keys() if n not in before]
    theirs = next(o for o in imported if o.type == "ARMATURE")

    zs = [(theirs.matrix_world @ b.head_local).z for b in theirs.data.bones]
    zs += [(theirs.matrix_world @ b.tail_local).z for b in theirs.data.bones]
    their_height = max(zs) - min(zs)

    # Rinominare i bone aggiorna da solo i data path dell'azione: e' il modo piu' sicuro
    # di togliere il prefisso. Attenzione: NON e' sempre "mixamorig:", Mixamo ci mette un
    # numero ("mixamorig10:"), quindi serve la regex.
    renamed = 0
    for bone in theirs.data.bones:
        clean = PREFIX.sub("", bone.name)
        if clean != bone.name:
            bone.name = clean
            renamed += 1

    action = theirs.animation_data.action
    action.name = clip_name
    action.use_fake_user = True  # Sopravvive alla cancellazione dell'armature Mixamo.
    curves = action_fcurves(action)

    # La traslazione del bacino arriva nelle unita' di Mixamo: oggetto a scala 0.01 e
    # scheletro piu' alto del nostro. Applicata cosi' com'e' scaglierebbe il personaggio.
    factor = 0.01 * (OUR_HEIGHT / their_height) if their_height else 0.01
    spans = {}
    for curve in curves:
        if curve.data_path.endswith(".location") and '"Hips"' in curve.data_path:
            for kp in curve.keyframe_points:
                kp.co[1] *= factor
                kp.handle_left[1] *= factor
                kp.handle_right[1] *= factor
            curve.update()
            vals = [kp.co[1] for kp in curve.keyframe_points]
            spans["xyz"[curve.array_index]] = round(max(vals) - min(vals), 4)

    # ATTENZIONE ALL'ASSE: la traslazione di un pose bone e' nello spazio LOCALE dell'osso,
    # e Hips punta in ALTO. Il canale Y e' quindi la VERTICALE. L'orizzontale sono X e Z.
    # Confondersi qui fa scartare come "root motion" qualunque salto.
    horizontal = max(spans.get("x", 0.0), spans.get("z", 0.0))

    info = {
        "clip": clip_name,
        "their_height": round(their_height, 4),
        "renamed_bones": renamed,
        "fcurves": len(curves),
        "hips_span_m": spans,
        "horizontal_span_m": round(horizontal, 4),
        "vertical_span_m": round(spans.get("y", 0.0), 4),
        "in_place": horizontal <= IN_PLACE_MAX_M,
        "frame_range": [int(v) for v in action.frame_range],
    }
    return action, imported, info


def assign_action(arm_obj, action):
    """Assegna un'azione a un oggetto. In Blender 5.x serve anche lo slot."""
    animation_data = arm_obj.animation_data_create()
    animation_data.action = action
    for slot in action.slots:
        try:
            animation_data.action_slot = slot
            return getattr(animation_data.action_slot, "identifier", None)
        except (TypeError, RuntimeError):
            continue
    return None


def discard(objects):
    """Rimuove gli oggetti importati: il rig Mixamo non deve finire nell'export."""
    for obj in objects:
        if obj.name in bpy.data.objects:
            bpy.data.objects.remove(obj, do_unlink=True)


def flatten_root_motion(action):
    """Toglie la traslazione ORIZZONTALE del bacino, rendendo la clip "in place".

    Serve per le clip che su Mixamo non offrono la spunta *In Place*. Fa la stessa cosa
    che farebbe quell'export: il bacino resta fermo e i piedi spazzano sotto di lui.

    ATTENZIONE ALL'ASSE (stessa trappola di import_clip): la traslazione di un pose bone
    e' nello spazio LOCALE dell'osso, e `Hips` punta in ALTO. Orizzontale = X e Z; Y e' la
    VERTICALE e va lasciata stare, altrimenti si appiattisce l'arco dei salti e la
    flessione degli atterraggi.

    Ogni chiave viene portata al valore della PRIMA: cosi' sparisce lo spostamento ma
    resta l'eventuale scarto iniziale del bacino rispetto all'origine, che fa parte
    della posa.

    Limite: toglie la traslazione, non la ROTAZIONE del bacino. Su una clip che curva
    resta un po' di deriva angolare.
    """
    flattened = {}
    for curve in action_fcurves(action):
        if not (curve.data_path.endswith(".location") and '"Hips"' in curve.data_path):
            continue
        if curve.array_index not in (0, 2):  # 0 = X, 2 = Z; 1 = Y = verticale, si tiene
            continue

        points = curve.keyframe_points
        if not points:
            continue

        base = points[0].co[1]
        span = max(kp.co[1] for kp in points) - min(kp.co[1] for kp in points)
        for kp in points:
            delta = base - kp.co[1]
            kp.co[1] = base
            kp.handle_left[1] += delta
            kp.handle_right[1] += delta
        curve.update()
        flattened["xz"[0 if curve.array_index == 0 else 1]] = round(span, 4)

    return flattened
