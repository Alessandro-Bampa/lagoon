# Verifica che Body_Base.glb sia importato da Godot con scheletro, skin e scala
# corretti. E' uno dei rari script GDScript ammessi dal CLAUDE.md §2 (tooling).
#
# Uso:
#   Godot_console.exe --path . --headless --script tools/verify_godot_import.gd
extends SceneTree

const MODEL_PATH := "res://assets/models/Body_Base.glb"
const LIBRARY_PATH := "res://assets/models/animations/CharacterAnimations.glb"
const RIG_PATH := "res://animation/scenes/CharacterRig.tscn"
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
	await _verify_rig()

	print("")
	print("%d controlli falliti" % _failures)
	quit(1 if _failures > 0 else 0)


# Il rig va provato ISTANZIATO e dentro l'albero: un AnimationTree con root_node
# sbagliato o un parametro dal nome errato non producono alcun errore, semplicemente
# non animano. L'unico modo di accorgersene e' chiedere all'albero se il parametro
# esiste davvero.
func _verify_rig() -> void:
	print("\n== %s ==" % RIG_PATH)

	var packed: PackedScene = load(RIG_PATH)
	_check("CharacterRig caricata", packed != null, RIG_PATH)
	if packed == null:
		return

	var rig: Node = packed.instantiate()
	root.add_child(rig)
	await process_frame

	var tree := rig.get_node_or_null("AnimationTree") as AnimationTree
	_check("AnimationTree presente", tree != null)
	if tree == null:
		rig.queue_free()
		return

	_check("tree_root assegnato", tree.tree_root != null)
	_check("libreria di animazioni collegata", tree.has_animation("walk_fwd"),
		"walk_fwd non risolve")

	# root_node deve puntare al nodo istanziato dal .glb, altrimenti i path delle
	# track ("Armature_Character/Skeleton3D:Hips") non risolvono.
	var anim_root: Node = tree.get_node_or_null(tree.root_node)
	_check("root_node risolve", anim_root != null, str(tree.root_node))
	if anim_root != null:
		_check("root_node contiene lo scheletro",
			anim_root.get_node_or_null("Armature_Character/Skeleton3D") != null,
			anim_root.name)

	for param in [
		"parameters/WalkSpace/blend_position",
		"parameters/RunSpace/blend_position",
		"parameters/MoveBlend/blend_amount",
		"parameters/CrouchSpace/blend_position",
		"parameters/AirBlend/blend_amount",
		"parameters/Land/request",
		"parameters/CrouchBlend/blend_amount",
		"parameters/HoldMask/blend_amount",
		"parameters/AimAdd/add_amount",
		"parameters/AimSpace/blend_position",
		"parameters/WeaponPose/transition_request",
		"parameters/FirePose/transition_request",
		"parameters/HitPose/transition_request",
		"parameters/LandPose/transition_request",
		"parameters/Fire/request",
		"parameters/Hit/request",
		"parameters/Jump/request",
		"parameters/Vault/request",
		"parameters/VaultPose/transition_request",
		"parameters/JumpScale/scale",
	]:
		var found := false
		for prop in tree.get_property_list():
			if prop["name"] == param:
				found = true
				break
		_check("parametro %s" % param, found)

	# Prova di scrittura: se il BlendSpace non accettasse la posizione, qui si vedrebbe.
	tree.set("parameters/WalkSpace/blend_position", Vector2(0.0, 4.0))
	_check("blend_position scrivibile",
		tree.get("parameters/WalkSpace/blend_position") == Vector2(0.0, 4.0))

	# Qualche frame prima di guardare i triangoli: con auto_triangles la
	# triangolazione la calcola il nodo al primo _process, non il caricamento.
	for i in 4:
		await process_frame

	_verify_blend_space_coverage(tree.tree_root)
	_verify_no_frozen_layer(tree.tree_root)

	rig.queue_free()


# Copertura dei BlendSpace2D. E' il controllo che MANCAVA e che avrebbe intercettato
# il bug del crouch in T-pose.
#
# Un AnimationNodeBlendSpace2D interpolato funziona per TRIANGOLAZIONE: la posa e'
# la combinazione baricentrica dei tre vertici del triangolo che contiene il punto
# richiesto. Dove non esiste un triangolo il nodo non produce NIENTE e lo scheletro
# ricade sulla rest pose — che per questo rig e' la T-pose. Non e' un errore, non e'
# un warning: e' silenzioso. La verifica dei soli parametri lo lascia passare, perche'
# `blend_position` esiste ed e' scrivibile anche in uno spazio senza un solo triangolo.
#
# Qui si campiona il ROMBO |x| + |y| <= max_space.y, cioe' esattamente la regione in
# cui CharacterAnimator.ClampToDiamond confina la posizione di blend, e si pretende che
# ogni campione cada dentro un triangolo.
func _verify_blend_space_coverage(root: AnimationRootNode) -> void:
	if not (root is AnimationNodeBlendTree):
		return

	var tree := root as AnimationNodeBlendTree
	for node_name in tree.get_node_list():
		var node := tree.get_node(node_name)
		if not (node is AnimationNodeBlendSpace2D):
			continue

		var bs := node as AnimationNodeBlendSpace2D
		if bs.blend_mode != AnimationNodeBlendSpace2D.BLEND_MODE_INTERPOLATED:
			continue

		_check("%s ha triangoli" % node_name, bs.get_triangle_count() > 0,
			"%d punti, 0 triangoli -> rest pose (T-pose)" % bs.get_blend_point_count())
		if bs.get_triangle_count() == 0:
			continue

		# Ogni punto di blend deve essere vertice di almeno un triangolo, altrimenti
		# la sua clip non viene mai raggiunta.
		var used := {}
		for t in bs.get_triangle_count():
			for v in 3:
				used[bs.get_triangle_point(t, v)] = true
		var orphans: PackedStringArray = []
		for i in bs.get_blend_point_count():
			if not used.has(i):
				orphans.append(str(bs.get_blend_point_position(i)))
		_check("%s: ogni punto e' in un triangolo" % node_name, orphans.is_empty(),
			", ".join(orphans))

		var radius: float = bs.max_space.y
		var uncovered: PackedStringArray = []
		var steps := 12
		for ix in range(-steps, steps + 1):
			for iy in range(-steps, steps + 1):
				var p := Vector2(radius * ix / steps, radius * iy / steps)
				if absf(p.x) + absf(p.y) > radius + 0.0001:
					continue  # fuori dal rombo: l'animatore non ci arriva mai
				if not _inside_any_triangle(bs, p):
					uncovered.append("(%.2f, %.2f)" % [p.x, p.y])
		_check("%s copre tutto il rombo di raggio %.1f" % [node_name, radius],
			uncovered.is_empty(),
			"%d punti scoperti, es. %s" % [uncovered.size(), ", ".join(uncovered.slice(0, 4))])


func _inside_any_triangle(bs: AnimationNodeBlendSpace2D, p: Vector2) -> bool:
	for t in bs.get_triangle_count():
		var a := bs.get_blend_point_position(bs.get_triangle_point(t, 0))
		var b := bs.get_blend_point_position(bs.get_triangle_point(t, 1))
		var c := bs.get_blend_point_position(bs.get_triangle_point(t, 2))
		var den := (b.y - c.y) * (a.x - c.x) + (c.x - b.x) * (a.y - c.y)
		if absf(den) < 0.000001:
			continue  # triangolo degenere: non copre nulla
		var u := ((b.y - c.y) * (p.x - c.x) + (c.x - b.x) * (p.y - c.y)) / den
		var v := ((c.y - a.y) * (p.x - c.x) + (a.x - c.x) * (p.y - c.y)) / den
		var w := 1.0 - u - v
		if u >= -0.0001 and v >= -0.0001 and w >= -0.0001:
			return true
	return false


# L'invariante "sparare o impugnare un'arma non ferma le gambe".
#
# AnimationNodeSync.sync == false ferma i frame dell'ingresso con peso 0. Su un nodo
# FILTRATO e' un bug: l'ingresso a peso 0 resta VISIBILE sulle parti che il filtro non
# copre, quindi la locomozione smetterebbe di avanzare pur essendo a schermo.
#
# Il nodo filtrato dell'albero e' HoldMask, la maschera d'impugnatura: e' esattamente il
# caso esposto alla trappola, quindi il controllo ha di nuovo qualcosa da controllare.
#
# Si verifica anche CHE COSA filtra: la maschera deve coprire le otto ossa delle braccia
# e nient'altro. Una maschera allargata al rachide o al bacino spegnerebbe la locomozione
# del busto o delle gambe senza dare il minimo errore — il personaggio camminerebbe con
# le gambe ferme, o accovacciato resterebbe dritto.
func _verify_no_frozen_layer(root: AnimationRootNode) -> void:
	if not (root is AnimationNodeBlendTree):
		return

	var arms := ["LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand",
		"RightShoulder", "RightArm", "RightForeArm", "RightHand"]

	var tree := root as AnimationNodeBlendTree
	var filtered: PackedStringArray = []
	for node_name in tree.get_node_list():
		var node := tree.get_node(node_name)
		if not (node is AnimationNodeBlend2 or node is AnimationNodeOneShot):
			continue
		if not node.filter_enabled:
			continue
		filtered.append(node_name)
		_check("%s filtrato ha sync attivo" % node_name, node.sync,
			"con sync=false la locomozione si ferma pur restando visibile sulle gambe")

		# AnimationNode non espone get_filters(): l'elenco si legge dalla proprieta'
		# `filters`, che e' quella serializzata nel .tres.
		var masked: PackedStringArray = []
		for path in (node.get("filters") as Array):
			masked.append(String(path).split(":")[-1])
		masked.sort()
		var expected := arms.duplicate()
		expected.sort()
		_check("%s maschera le sole braccia" % node_name,
			Array(masked) == expected, "maschera = %s" % ", ".join(masked))

	_check("la maschera d'impugnatura esiste", filtered.has("HoldMask"),
		"nodi filtrati: %s" % ", ".join(filtered))


# Tutte le clip stanno in UNA AnimationLibrary, cosi' nell'AnimationTree si scrive
# `walk_fwd` e non `walk_fwd/walk_fwd`. I nomi delle track devono combaciare con i
# bone dello scheletro: una track che punta a un bone inesistente e' muta e produce
# un'animazione parziale senza alcun errore a runtime.
func _verify_animations(skeleton_bones: PackedStringArray) -> void:
	print("\n== %s ==" % LIBRARY_PATH)

	var library: AnimationLibrary = load(LIBRARY_PATH)
	_check("AnimationLibrary caricata", library != null, LIBRARY_PATH)
	if library == null:
		return

	var clips: Array[StringName] = library.get_animation_list()
	print("  %d clip: %s" % [clips.size(), ", ".join(clips)])
	_check("almeno una clip", clips.size() > 0)

	var unknown_all: PackedStringArray = []
	for clip_name in clips:
		var anim: Animation = library.get_animation(clip_name)
		_check("'%s' ha durata > 0" % clip_name, anim.length > 0.0, "%.3f s" % anim.length)

		var unknown: PackedStringArray = []
		for i in anim.get_track_count():
			var sub: String = anim.track_get_path(i).get_concatenated_subnames()
			if sub != "" and not skeleton_bones.has(sub) and not unknown.has(sub):
				unknown.append(sub)
		if not unknown.is_empty():
			unknown_all.append("%s: %s" % [clip_name, ", ".join(unknown)])
		print("    %-14s %6.3f s  %2d track" % [clip_name, anim.length, anim.get_track_count()])

	_check("tutte le track puntano a bone esistenti", unknown_all.is_empty(),
		"; ".join(unknown_all))

	_verify_loop_modes(library, clips)


# Modalita' di loop. E' l'altro controllo che MANCAVA, e che avrebbe intercettato il
# bug delle "animazioni che si bloccano".
#
# L'importatore glTF di Godot mette loop_mode = LOOP_NONE su tutto quello che non ha un
# suffisso `-loop` nel nome, e le clip Mixamo non ce l'hanno. Una clip ciclica importata
# senza loop parte, arriva in fondo e CONGELA sull'ultimo fotogramma: il personaggio
# cammina per un secondo e poi scivola nel mondo con le gambe immobili. Nessun errore,
# nessun warning — il blend space funziona benissimo, e' la clip che e' finita.
#
# Il loop non si imposta in Blender ne' nel .glb: si imposta in
# assets/models/animations/CharacterAnimations.glb.import, sotto _subresources/animations.
func _verify_loop_modes(library: AnimationLibrary, clips: Array[StringName]) -> void:
	# Clip cicliche: girano finche' dura lo stato che rappresentano.
	var must_loop := [
		"walk_fwd", "walk_back", "walk_left", "walk_right",
		"run_fwd", "run_back", "run_left", "run_right",
		"rifle_walk_fwd", "rifle_walk_back", "rifle_walk_left", "rifle_walk_right",
		"rifle_run_fwd", "rifle_run_back", "rifle_run_left", "rifle_run_right",
		"crouch_idle", "crouch_fwd", "crouch_back", "crouch_left", "crouch_right",
		"idle_neutral", "rifle_idle", "pistol_idle", "fall_idle",
		# Pose procedurali (build_procedural_clips.py): idle respiranti, ciclano.
		"rifle_aim_idle", "rifle_lowered_idle", "pistol_aim_idle",
	]
	# Clip a colpo singolo: devono finire, altrimenti il one-shot non rientra mai.
	# Le due di parkour sono fra queste: uno scavalcamento che ciclasse terrebbe il
	# personaggio in quella posa per il resto della partita.
	var must_not_loop := ["jump", "rifle_fire", "land_hard", "pistol_fire", "land_soft",
		"vault_low", "mantle_high"]

	# NOTA: le clip DELTA additive non compaiono qui perche' non stanno in questo .glb.
	# Vivono in animation/resources/AdditiveClips.tres, generate da
	# tools/build_additive_clips.gd, che imposta il loop_mode direttamente sulla
	# risorsa Animation — quindi non passano nemmeno dal .import.

	var wrong: PackedStringArray = []
	for clip_name in clips:
		var mode: int = library.get_animation(clip_name).loop_mode
		var name_str := str(clip_name)
		if must_loop.has(name_str) and mode == Animation.LOOP_NONE:
			wrong.append("%s dovrebbe ciclare" % name_str)
		elif must_not_loop.has(name_str) and mode != Animation.LOOP_NONE:
			wrong.append("%s NON dovrebbe ciclare" % name_str)

	_check("loop_mode corretto su tutte le clip", wrong.is_empty(), "; ".join(wrong))

	var uncategorised: PackedStringArray = []
	for clip_name in clips:
		var name_str := str(clip_name)
		if not must_loop.has(name_str) and not must_not_loop.has(name_str):
			uncategorised.append(name_str)
	_check("nessuna clip senza categoria di loop", uncategorised.is_empty(),
		"aggiungile a _verify_loop_modes: " + ", ".join(uncategorised))
