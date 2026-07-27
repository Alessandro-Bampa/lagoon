# Verifica che Body_Base.glb sia importato da Godot con scheletro, skin e scala
# corretti. E' uno dei rari script GDScript ammessi dal CLAUDE.md §2 (tooling).
#
# Uso:
#   Godot_console.exe --path . --headless --script tools/verify_godot_import.gd
extends SceneTree

const MODEL_PATH := "res://assets/models/Body_Base.glb"
const ANIM_DIR := "res://assets/models/animations"
const HEIGHT_MIN := 1.75
const HEIGHT_MAX := 1.80

const EXPECTED_BONES := [
	"Hips", "Spine", "Spine1", "Spine2", "Neck", "Head",
	"LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand",
	"RightShoulder", "RightArm", "RightForeArm", "RightHand",
	"LeftUpLeg", "LeftLeg", "LeftFoot", "LeftToeBase",
	"RightUpLeg", "RightLeg", "RightFoot", "RightToeBase",
]

var _failures := 0


func _check(label: String, ok: bool, detail: String = "") -> void:
	var mark := "OK  " if ok else "FAIL"
	var line := "  [%s] %s" % [mark, label]
	if not ok and detail != "":
		line += " -> " + detail
	print(line)
	if not ok:
		_failures += 1


func _find(node: Node, type_name: String) -> Node:
	if node.is_class(type_name):
		return node
	for child in node.get_children():
		var found := _find(child, type_name)
		if found != null:
			return found
	return null


func _initialize() -> void:
	print("== %s ==" % MODEL_PATH)

	var packed: PackedScene = load(MODEL_PATH)
	if packed == null:
		print("  [FAIL] impossibile caricare la scena importata")
		quit(1)
		return

	var root: Node = packed.instantiate()
	var skeleton := _find(root, "Skeleton3D") as Skeleton3D
	_check("Skeleton3D presente", skeleton != null)
	if skeleton == null:
		root.free()
		quit(1)
		return

	var names: PackedStringArray = []
	for i in skeleton.get_bone_count():
		names.append(skeleton.get_bone_name(i))
	print("  bone: %d -> %s" % [skeleton.get_bone_count(), ", ".join(names)])

	var missing: PackedStringArray = []
	for bone in EXPECTED_BONES:
		if skeleton.find_bone(bone) == -1:
			missing.append(bone)
	_check("bone Mixamo attesi presenti", missing.is_empty(), ", ".join(missing))

	var mesh_instance := _find(root, "MeshInstance3D") as MeshInstance3D
	_check("MeshInstance3D presente", mesh_instance != null)
	if mesh_instance != null:
		_check("skin assegnata", mesh_instance.skin != null)
		var aabb: AABB = mesh_instance.mesh.get_aabb()
		var height: float = aabb.size.y
		print("  aabb: pos=%s size=%s" % [aabb.position, aabb.size])
		_check("altezza in %.2f-%.2f m" % [HEIGHT_MIN, HEIGHT_MAX],
			height >= HEIGHT_MIN and height <= HEIGHT_MAX, "%.4f m" % height)
		_check("piedi a y=0", absf(aabb.position.y) < 0.001, "%.5f" % aabb.position.y)
		_check("scala del nodo unitaria", mesh_instance.scale.is_equal_approx(Vector3.ONE),
			str(mesh_instance.scale))
		var surface_count: int = mesh_instance.mesh.get_surface_count()
		_check("una sola superficie", surface_count == 1, str(surface_count))

	_check("scala della radice unitaria",
		not (root is Node3D) or (root as Node3D).scale.is_equal_approx(Vector3.ONE))

	root.free()

	_verify_animations(names)

	print("")
	print("%d controlli falliti" % _failures)
	quit(1 if _failures > 0 else 0)


# Le clip vivono in .glb separati (sola armature) e vanno agganciate a Body_Base:
# i nomi delle track devono combaciare con i bone dello scheletro.
func _verify_animations(skeleton_bones: PackedStringArray) -> void:
	var dir := DirAccess.open(ANIM_DIR)
	if dir == null:
		print("\n== %s: nessuna animazione ==" % ANIM_DIR)
		return

	for file in dir.get_files():
		if not file.ends_with(".glb"):
			continue
		var path := ANIM_DIR.path_join(file)
		print("\n== %s ==" % path)

		var packed: PackedScene = load(path)
		if packed == null:
			_check("caricamento", false, path)
			continue
		var root: Node = packed.instantiate()
		var player := _find(root, "AnimationPlayer") as AnimationPlayer
		_check("AnimationPlayer presente", player != null)
		if player == null:
			root.free()
			continue

		var clips: PackedStringArray = player.get_animation_list()
		print("  clip: %s" % ", ".join(clips))
		_check("almeno una clip", clips.size() > 0)

		for clip_name in clips:
			var anim: Animation = player.get_animation(clip_name)
			print("  '%s': %.3f s, %d track" % [clip_name, anim.length, anim.get_track_count()])
			_check("'%s' ha durata > 0" % clip_name, anim.length > 0.0)

			# Ogni track punta a un bone: se il nome non esiste nello scheletro
			# la track e' muta e l'animazione risulta parziale senza errori.
			var unknown: PackedStringArray = []
			for i in anim.get_track_count():
				var sub: String = anim.track_get_path(i).get_concatenated_subnames()
				if sub != "" and not skeleton_bones.has(sub) and not unknown.has(sub):
					unknown.append(sub)
			_check("'%s': tutte le track puntano a bone esistenti" % clip_name,
				unknown.is_empty(), "sconosciuti: " + ", ".join(unknown))

		root.free()
