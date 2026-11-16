"""Converte il Retro Weapon Pack (FBX) negli asset arma del progetto.

Per ogni arma produce due file in `assets/models/weapons/`:

  VM_<arma>.glb  viewmodel in prima persona: braccia + arma su due scheletri, con UNA
                 libreria di clip che li anima entrambi (idle, walk, run, mira, sparo,
                 ricarica, estrazione...). L'arma e' figlia dell'osso `hand_item_r`.
  W_<arma>.glb   arma statica per la terza persona, con i marcatori che servono al rig
                 del corpo (Muzzle, Grip, Support, EyeHip, EyeAim).

Uso (Blender aperto con l'estensione MCP, vedi la skill blender-pipeline):

    python tools/blender/blender_client.py tools/blender/build_weapon_viewmodels.py \
        "<...>/RetroWeaponPack_V1/Assets/RetroWeaponsPack" rifle

Il secondo argomento e' la chiave in WEAPONS; senza, si costruiscono tutte (attenzione al
timeout del client: alcuni FBX del pack pesano 60 MB).

CONVENZIONE DEGLI ASSI. Nel pack la camera guarda lungo +X di Blender. Qui tutto viene
ruotato perche' guardi +Y, che dopo l'export glTF e' -Z di Godot: il viewmodel si appende
alla Camera3D con trasformata identita', e la canna di ogni arma punta a -Z (il "davanti"
di `look_at`).

PERCHE' SI RICAMPIONA INVECE DI RIUSARE LE AZIONI. Gli FBX di animazione non hanno mesh,
quindi la loro "rest pose" e' la posa del momento dell'export: le azioni importate sono
delta rispetto a un riposo diverso da file a file e non si possono riassegnare
all'armatura con la mesh. Si campiona allora la posa MONDO di ogni osso a ogni frame e la
si riscrive come azione sull'armatura finale (in metri, scala 1).
"""

import math
import os

import bpy
from mathutils import Matrix, Vector

PACK = ARGV[0].replace("\\", "/").rstrip("/")
ONLY = ARGV[1] if len(ARGV) > 1 else None
OUT_DIR = PROJECT_DIR + "/assets/models/weapons"

# Camera del pack: guarda +X. La si porta a guardare +Y (= -Z di Godot).
CORRECTION = Matrix.Rotation(math.radians(90.0), 4, "Z")

FPS = 24

# Nome della clip nel progetto -> file FBX delle braccia ({w} = nome dell'arma nel pack).
ARM_CLIPS = {
    "idle": "Cycles/FP_Arms_{w}_Breathing",
    "idle_aim": "Cycles/FP_Arms_{w}_Breathing_Aiming",
    "idle_crouch": "Cycles/FP_Arms_{w}_Breathing_Crouching",
    "walk": "Cycles/FP_Arms_{w}_Walk",
    "walk_aim": "Cycles/FP_Arms_{w}_Walk_Aiming",
    "walk_crouch": "Cycles/FP_Arms_{w}_Walk_Crouching",
    "run": "Cycles/FP_Arms_{w}_Run",
    "fire": "OneTimeAnimations/FP_Arms_{w}_Fire",
    "fire_aim": "OneTimeAnimations/FP_Arms_{w}_Fire_Aiming",
    "reload": "OneTimeAnimations/FP_Arms_{w}_Reload",
    "interact": "OneTimeAnimations/FP_Arms_{w}_Interact",
    "pose_base": "Poses/FP_Arms_{w}_BasePose",
    "pose_aim": "Poses/FP_Arms_{w}_AimPose",
    "aim_start": "TransitionAnimations/FP_Arms_{w}_AimStart",
    "aim_end": "TransitionAnimations/FP_Arms_{w}_AimEnd",
    "draw": "TransitionAnimations/FP_Arms_{w}_Draw",
    "hide": "TransitionAnimations/FP_Arms_{w}_Hide",
}

WEAPONS = {
    "rifle": {
        "pack_name": "Rifle_01",
        "gun_fbx": "Guns/Rifle_01/Fbx_Files/Rifle_01.fbx",
        "gun_anim_dir": "Guns/Rifle_01/Fbx_Files/Animations",
        "albedo": "Guns/Rifle_01/Textures/Rifle_01_Albedo.png",
        # clip del progetto -> animazione delle parti mobili dell'arma
        "gun_clips": {"fire": "Rifle_01_Fire", "fire_aim": "Rifle_01_Fire", "reload": "Rifle_01_Reload"},
    },
    "pistol": {
        "pack_name": "Pistol_01",
        "gun_fbx": "Guns/Pistol_01/FbxFiles/Pistol_01.fbx",
        "gun_anim_dir": "Guns/Pistol_01/FbxFiles/Animations",
        "albedo": "Guns/Pistol_01/Textures/Pistol_01_Albedo.png",
        "gun_clips": {"fire": "Pistol_01_Fire", "fire_aim": "Pistol_01_Fire",
                      "reload": "Pistol_01_Reload", "empty": "Pistol_01_EmptyMagazine"},
    },
}

ARMS_FBX = "FP_Arms/FBX_Files/FP_Arms.fbx"
ARMS_ANIM_DIR = "FP_Arms/FBX_Files/Animations/{w}_Animations"
ARMS_ALBEDO = "FP_Arms/Texture/FPS_Arms_Albedo.png"
PROJECTILES_ALBEDO = "Guns/AdditionalMeshes/Projectiles/Texture/Projectiles_Albedo.png"


# ----------------------------------------------------------------------------------------
#  Utilita'
# ----------------------------------------------------------------------------------------

def view3d_override(**extra):
    """temp_override con un'area VIEW_3D: senza, import ed export falliscono il poll()."""
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


def reset_scene():
    """Scena vuota in METRI. Il pack lavora in centimetri (scale_length 0.01): ereditarne
    la scena falserebbe la scala di ogni import."""
    bpy.ops.wm.read_homefile(use_empty=True)
    scene = bpy.context.scene
    scene.unit_settings.scale_length = 1.0
    scene.render.fps = FPS
    scene.frame_start = 0
    scene.frame_end = 250


def import_fbx(path):
    if not os.path.exists(path):
        raise FileNotFoundError(path)
    before = set(bpy.data.objects.keys())
    with view3d_override():
        bpy.ops.import_scene.fbx(filepath=path, use_anim=True)
    return [bpy.data.objects[n] for n in bpy.data.objects.keys() if n not in before]


def discard(objects):
    for obj in objects:
        if obj.name in bpy.data.objects:
            bpy.data.objects.remove(obj, do_unlink=True)


def unscaled(matrix):
    loc, rot, _ = matrix.decompose()
    return Matrix.LocRotScale(loc, rot, None)


def normalize(armature, meshes):
    """Porta armatura e mesh nello spazio finale: metri, assi corretti, oggetti a identita'."""
    for mesh in meshes:
        mesh.data.transform(CORRECTION @ mesh.matrix_world)
        mesh.data.update()
    armature.data.transform(CORRECTION @ armature.matrix_world)
    armature.matrix_world = Matrix.Identity(4)
    bpy.context.view_layer.update()
    for mesh in meshes:
        mesh.parent = armature
        mesh.matrix_parent_inverse = Matrix.Identity(4)
        mesh.matrix_basis = Matrix.Identity(4)
        for mod in mesh.modifiers:
            if mod.type == "ARMATURE":
                mod.object = armature
    for pb in armature.pose.bones:
        pb.rotation_mode = "QUATERNION"
    bpy.context.view_layer.update()


def sample_clip(path):
    """Importa un FBX di animazione e restituisce, per ogni frame, la posa MONDO (gia'
    corretta e senza scala) di ogni osso. L'armatura importata viene poi scartata."""
    imported = import_fbx(path)
    source = next(o for o in imported if o.type == "ARMATURE")
    action = source.animation_data.action if source.animation_data else None
    if action is not None:
        first, last = (int(round(v)) for v in action.frame_range)
    else:
        first = last = 1

    scene = bpy.context.scene
    frames = []
    for frame in range(first, last + 1):
        scene.frame_set(frame)
        bpy.context.view_layer.update()
        world = CORRECTION @ source.matrix_world
        frames.append({pb.name: unscaled(world @ pb.matrix) for pb in source.pose.bones})

    if action is not None:
        bpy.data.actions.remove(action)
    discard(imported)
    return frames


def rest_frames(armature, count):
    """Posa di riposo ripetuta: per le clip in cui un'armatura non ha animazione propria."""
    pose = {b.name: b.matrix_local.copy() for b in armature.data.bones}
    return [pose for _ in range(max(count, 2))]


def write_action(target, name, frames):
    """Scrive `frames` (pose in spazio armatura) come azione su `target` e la mette su una
    traccia NLA col nome della clip. L'export glTF in modalita' NLA_TRACKS fonde le tracce
    con lo stesso nome di oggetti diversi in UNA animazione: e' cosi' che braccia e arma
    finiscono nella stessa clip."""
    bones = [b for b in target.data.bones if b.name in frames[0]]
    rest_local = {}
    for bone in bones:
        rest_local[bone.name] = (bone.parent.matrix_local.inverted() @ bone.matrix_local
                                 if bone.parent else bone.matrix_local.copy())

    action = bpy.data.actions.new("{}_{}".format(target.name, name))
    action.use_fake_user = True
    animation_data = target.animation_data_create()
    animation_data.action = action

    last_quat = {}
    channels = {}
    for index, pose in enumerate(frames):
        for bone in bones:
            matrix = pose[bone.name]
            if bone.parent is not None:
                parent = pose.get(bone.parent.name, bone.parent.matrix_local)
                matrix = parent.inverted() @ matrix
            basis = rest_local[bone.name].inverted() @ matrix
            loc, rot, _ = basis.decompose()

            # Continuita' del quaternione: q e -q sono la stessa rotazione, ma interpolare
            # fra i due fa fare all'osso il giro lungo.
            previous = last_quat.get(bone.name)
            if previous is not None and previous.dot(rot) < 0.0:
                rot.negate()
            last_quat[bone.name] = rot.copy()

            base = 'pose.bones["{}"]'.format(bone.name)
            for axis in range(3):
                channels.setdefault((base + ".location", axis, bone.name), []).append((index, loc[axis]))
            for axis in range(4):
                channels.setdefault((base + ".rotation_quaternion", axis, bone.name), []).append((index, rot[axis]))

    for (path, axis, group), keys in channels.items():
        curve = action.fcurve_ensure_for_datablock(target, path, index=axis, group_name=group)
        for frame, value in keys:
            point = curve.keyframe_points.insert(frame, value, options={"FAST"})
            point.interpolation = "LINEAR"
        curve.update()

    animation_data.action = None
    track = animation_data.nla_tracks.new()
    track.name = name
    strip = track.strips.new(name, 0, action)
    if hasattr(strip, "action_slot") and action.slots:
        try:
            strip.action_slot = action.slots[0]
        except (TypeError, RuntimeError):
            pass
    for pb in target.pose.bones:
        pb.matrix_basis = Matrix.Identity(4)
    return len(frames)


def make_material(name, image_path):
    """Materiale "retro" come da Readme del pack: albedo senza filtro, roughness 1,
    specular 0."""
    material = bpy.data.materials.new(name)
    material.use_nodes = True
    nodes = material.node_tree.nodes
    bsdf = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Roughness"].default_value = 1.0
    bsdf.inputs["Metallic"].default_value = 0.0
    if "Specular IOR Level" in bsdf.inputs:
        bsdf.inputs["Specular IOR Level"].default_value = 0.0
    texture = nodes.new("ShaderNodeTexImage")
    texture.image = bpy.data.images.load(image_path, check_existing=True)
    texture.interpolation = "Closest"
    material.node_tree.links.new(texture.outputs["Color"], bsdf.inputs["Base Color"])
    return material


def assign_materials(meshes, main_material, projectiles_material):
    for mesh in meshes:
        for slot in range(len(mesh.data.materials)):
            old = mesh.data.materials[slot]
            is_projectile = old is not None and "Projectiles" in old.name
            mesh.data.materials[slot] = projectiles_material if is_projectile else main_material
        if not mesh.data.materials:
            mesh.data.materials.append(main_material)


def add_empty(name, matrix, parent=None):
    empty = bpy.data.objects.new(name, None)
    empty.empty_display_size = 0.02
    bpy.context.scene.collection.objects.link(empty)
    if parent is not None:
        empty.parent = parent
        empty.matrix_parent_inverse = Matrix.Identity(4)
    empty.matrix_basis = matrix
    return empty


def muzzle_point(meshes):
    """Bocca dell'arma: baricentro dei vertici piu' avanzati (entro 1 cm dal massimo)."""
    points = [v.co.copy() for mesh in meshes for v in mesh.data.vertices]
    front = max(p.y for p in points)
    tip = [p for p in points if p.y > front - 0.01]
    center = sum(tip, Vector()) / len(tip)
    return Vector((center.x, front, center.z))


def export_glb(path, objects, animations):
    for obj in bpy.context.view_layer.objects:
        obj.select_set(False)
    for obj in objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with view3d_override(object=objects[0], active_object=objects[0],
                         selected_objects=objects, selected_editable_objects=objects):
        bpy.ops.export_scene.gltf(
            filepath=path,
            export_format="GLB",
            use_selection=True,
            export_yup=True,
            export_skins=animations,
            export_animations=animations,
            export_animation_mode="NLA_TRACKS",
            export_force_sampling=True,
            export_frame_range=False,
            export_anim_slide_to_zero=True,
            export_optimize_animation_size=True,
            export_materials="EXPORT",
            export_cameras=False,
            export_lights=False,
        )
    return os.path.getsize(path)


# ----------------------------------------------------------------------------------------
#  Un'arma
# ----------------------------------------------------------------------------------------

def build(key):
    spec = WEAPONS[key]
    w = spec["pack_name"]
    log = {"weapon": key, "clips": {}}

    reset_scene()

    # --- braccia ---
    imported = import_fbx(PACK + "/" + ARMS_FBX)
    arms = next(o for o in imported if o.type == "ARMATURE")
    arm_meshes = [o for o in imported if o.type == "MESH"]
    discard([o for o in imported if o is not arms and o not in arm_meshes])
    arms.name = "Arms"
    arms.data.name = "Arms"
    normalize(arms, arm_meshes)

    # --- arma ---
    imported = import_fbx(PACK + "/" + spec["gun_fbx"])
    gun = next(o for o in imported if o.type == "ARMATURE")
    gun_meshes = [o for o in imported if o.type == "MESH" and len(o.data.vertices) > 0]
    discard([o for o in imported if o is not gun and o not in gun_meshes])
    gun.name = "Gun"
    gun.data.name = "Gun"
    normalize(gun, gun_meshes)

    # L'arma segue l'osso hand_item_r: nel pack il suo osso radice `Main` coincide con
    # quell'osso in ogni posa. Va agganciata ADESSO, con tutto a riposo: farlo dopo aver
    # scritto le tracce NLA calcolerebbe l'offset contro una posa animata.
    main_rest = gun.data.bones["Main"].matrix_local.copy()
    gun.parent = arms
    gun.parent_type = "BONE"
    gun.parent_bone = "hand_item_r"
    bpy.context.view_layer.update()
    gun.matrix_world = arms.data.bones["hand_item_r"].matrix_local @ main_rest.inverted()
    bpy.context.view_layer.update()

    arms_material = make_material("FP_Arms", PACK + "/" + ARMS_ALBEDO)
    gun_material = make_material("Gun_" + key, PACK + "/" + spec["albedo"])
    projectiles_material = make_material("Projectiles", PACK + "/" + PROJECTILES_ALBEDO)
    assign_materials(arm_meshes, arms_material, projectiles_material)
    assign_materials(gun_meshes, gun_material, projectiles_material)

    camera_rest = arms.data.bones["camera"].matrix_local
    front = gun.data.bones.get("FrontSight")
    log["check"] = {
        "gun_local_to_hand_item": [[round(v, 3) for v in row] for row in
                                   (arms.data.bones["hand_item_r"].matrix_local.inverted() @ gun.matrix_world)],
        "camera_bone_x": [round(v, 3) for v in camera_rest.col[0].xyz],
        "camera_bone_y": [round(v, 3) for v in camera_rest.col[1].xyz],
        "gun_main_rest": [[round(v, 3) for v in row] for row in gun.data.bones["Main"].matrix_local],
        "front_sight_head": [round(v, 4) for v in front.head_local] if front else None,
        "arm_verts": sum(len(m.data.vertices) for m in arm_meshes),
        "gun_verts": sum(len(m.data.vertices) for m in gun_meshes),
    }

    # --- clip delle braccia ---
    anim_dir = PACK + "/" + ARMS_ANIM_DIR.format(w=w)
    arm_samples = {}
    for clip, pattern in ARM_CLIPS.items():
        path = "{}/{}.fbx".format(anim_dir, pattern.format(w=w))
        if not os.path.exists(path):
            # Il pack non e' uniforme nei nomi (Interact / Interaction).
            alt = path.replace("_Interact.fbx", "_Interaction.fbx")
            if not os.path.exists(alt):
                log.setdefault("missing", []).append(clip)
                continue
            path = alt
        arm_samples[clip] = sample_clip(path)

    # --- clip delle parti mobili dell'arma ---
    gun_samples = {}
    gun_files = {}
    for clip, stem in spec["gun_clips"].items():
        if stem not in gun_files:
            gun_files[stem] = sample_clip("{}/{}/{}.fbx".format(PACK, spec["gun_anim_dir"], stem))
        gun_samples[clip] = gun_files[stem]

    for clip in sorted(set(arm_samples) | set(gun_samples)):
        arm_frames = arm_samples.get(clip)
        gun_frames = gun_samples.get(clip)
        count = max(len(arm_frames or []), len(gun_frames or []))
        entry = {"frames": count, "seconds": round((count - 1) / FPS, 3)}
        if arm_frames is not None:
            entry["arms"] = write_action(arms, clip, arm_frames)
        # L'arma ha una traccia in OGNI clip: dove non ha animazione propria si scrive il
        # riposo, cosi' nessun blend lascia l'otturatore a meta' corsa.
        entry["gun"] = write_action(gun, clip, gun_frames or rest_frames(gun, count))
        log["clips"][clip] = entry

    # Clip RESET, scritta per ULTIMA: la traccia NLA piu' in alto e' quella che decide la
    # posa valutata, e l'export glTF cattura bind e riposo dallo stato corrente. Senza, il
    # riposo dell'arma finirebbe nel file catturato a meta' di un'altra clip. In Godot e'
    # anche la clip che AnimationMixer usa come valore di default delle tracce.
    write_action(arms, "RESET", rest_frames(arms, 2))
    write_action(gun, "RESET", rest_frames(gun, 2))
    bpy.context.scene.frame_set(0)
    bpy.context.view_layer.update()

    # --- marcatori, misurati sulle pose del pack ---
    markers = {}

    def gun_world(pose):
        """Trasformata oggetto dell'arma quando Main coincide con hand_item_r."""
        return pose["hand_item_r"] @ main_rest.inverted()

    base = arm_samples["pose_base"][0]
    aim = arm_samples["pose_aim"][0]
    hip_frame = gun_world(base)
    aim_frame = gun_world(aim)
    markers["Muzzle"] = Matrix.Translation(muzzle_point(gun_meshes))
    markers["Grip"] = Matrix.Translation(hip_frame.inverted() @ base["hand_r"].translation)
    markers["Support"] = Matrix.Translation(hip_frame.inverted() @ base["hand_l"].translation)
    # Dove sta la CAMERA (origine, assi identita') vista dall'arma nelle due pose: in terza
    # persona basta far coincidere questi marcatori con gli occhi del corpo.
    markers["EyeHip"] = hip_frame.inverted()
    markers["EyeAim"] = aim_frame.inverted()
    log["markers"] = {k: [round(v, 4) for v in m.translation] for k, m in markers.items()}

    vm_muzzle = add_empty("Muzzle", markers["Muzzle"], parent=gun)
    bpy.context.view_layer.update()

    vm_objects = [arms, gun, vm_muzzle] + arm_meshes + gun_meshes
    vm_path = "{}/VM_{}.glb".format(OUT_DIR, key.capitalize())
    log["viewmodel"] = {"glb": vm_path, "bytes": export_glb(vm_path, vm_objects, True)}

    # --- arma statica per la terza persona ---
    bpy.data.objects.remove(vm_muzzle, do_unlink=True)
    root = add_empty("W_" + key, Matrix.Identity(4))
    static = []
    for mesh in gun_meshes:
        copy = bpy.data.objects.new(mesh.name.replace("_Mesh", ""), mesh.data.copy())
        bpy.context.scene.collection.objects.link(copy)
        copy.parent = root
        copy.matrix_parent_inverse = Matrix.Identity(4)
        copy.matrix_basis = Matrix.Identity(4)
        static.append(copy)
    marker_objects = [add_empty(name, matrix, parent=root) for name, matrix in markers.items()]
    bpy.context.view_layer.update()

    w_path = "{}/W_{}.glb".format(OUT_DIR, key.capitalize())
    log["world"] = {"glb": w_path, "bytes": export_glb(w_path, [root] + static + marker_objects, False)}
    return log


keys = [ONLY] if ONLY else sorted(WEAPONS)
result = {"built": [build(k) for k in keys]}
