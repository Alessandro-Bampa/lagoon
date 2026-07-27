# Verifica COMPORTAMENTALE del rig: fa girare davvero l'AnimationTree e guarda le
# ossa. E' complementare a verify_godot_import.gd, che controlla la struttura.
#
# Serve perche' i due bug bloccanti della Fase C erano entrambi MUTI: nessun errore,
# nessun warning, tutti i parametri al loro posto.
#   - crouch in T-pose: CrouchSpace era un BlendSpace2D con due soli punti, quindi
#     collineari, quindi zero triangoli, quindi nessuna uscita e rest pose;
#   - animazioni ferme: le clip Mixamo arrivavano con loop_mode = LOOP_NONE, quindi
#     dopo un ciclo restavano congelate sull'ultimo fotogramma.
# Qui si misura direttamente cio' che l'occhio vedeva: "sei in T-pose?" e "ti stai
# ancora muovendo?".
#
# Uso:
#   Godot_console.exe --path . --headless --script tools/verify_animation_runtime.gd
extends SceneTree

const RIG_PATH := "res://animation/scenes/CharacterRig.tscn"

# Velocita' di camminata: deve combaciare con WALK_SPEED di build_animation_tree.gd.
const WALK_SPEED := 4.0
const RUN_SPEED := 7.0
const CROUCH_SPEED := 2.0

# Scarto minimo dalla rest pose (T-pose) perche' una posa sia considerata "animata".
# Una posa vera si discosta di oltre un radiante su qualche osso; il rumore numerico
# sta sotto il millesimo.
const TPOSE_EPSILON := 0.05

# Movimento minimo per frame perche' la locomozione sia considerata "viva".
const MOTION_EPSILON := 0.002

const LEGS := ["LeftUpLeg", "LeftLeg", "RightUpLeg", "RightLeg"]

var _failures := 0
var _skel: Skeleton3D
var _tree: AnimationTree


func _check(label: String, ok: bool, detail: String = "") -> void:
	var mark := "OK  " if ok else "FAIL"
	var line := "  [%s] %s" % [mark, label]
	if detail != "":
		line += " -> " + detail
	print(line)
	if not ok:
		_failures += 1


func _rest_distance() -> float:
	var m := 0.0
	for i in _skel.get_bone_count():
		var pose := _skel.get_bone_pose_rotation(i)
		var rest := _skel.get_bone_rest(i).basis.get_rotation_quaternion()
		m = maxf(m, pose.angle_to(rest))
	return m


func _legs_snapshot() -> Array:
	var out := []
	for bone in LEGS:
		out.append(_skel.get_bone_pose_rotation(_skel.find_bone(bone)))
	return out


# Movimento delle gambe misurato a FINESTRE, e ritornato come il minimo fra le
# finestre.
#
# Non si guarda il singolo frame: in headless capitano tick con delta nullo, e anche
# in un ciclo di camminata sano c'e' un istante di inversione in cui la gamba e'
# ferma. Si guarda invece se ESISTE una finestra in cui non succede piu' niente, che
# e' il sintomo del congelamento: la clip finita che resta sull'ultimo fotogramma.
func _legs_motion(windows: int, window_frames: int) -> float:
	var quietest := INF
	for w in windows:
		var prev := _legs_snapshot()
		var moved := 0.0
		for i in window_frames:
			await process_frame
			var now := _legs_snapshot()
			for j in LEGS.size():
				moved = maxf(moved, (prev[j] as Quaternion).angle_to(now[j]))
			prev = now
		quietest = minf(quietest, moved)
	return quietest


# Confina la posizione di blend nel rombo, come fa CharacterAnimator.ClampToDiamond.
func _diamond(v: Vector2, radius: float) -> Vector2:
	var l1: float = absf(v.x) + absf(v.y)
	return v * (radius / l1) if l1 > radius else v


func _drive(local_velocity: Vector2, crouch: float, weapon: float) -> void:
	_tree.set("parameters/WalkSpace/blend_position", _diamond(local_velocity, WALK_SPEED))
	_tree.set("parameters/RunSpace/blend_position", _diamond(local_velocity, RUN_SPEED))
	_tree.set("parameters/CrouchSpace/blend_position", _diamond(local_velocity, CROUCH_SPEED))
	_tree.set("parameters/CrouchBlend/blend_amount", crouch)
	_tree.set("parameters/WeaponBlend/blend_amount", weapon)
	_tree.set("parameters/AirBlend/blend_amount", 0.0)
	var band: float = maxf(RUN_SPEED - WALK_SPEED, 0.001)
	_tree.set("parameters/MoveBlend/blend_amount",
		clampf((local_velocity.length() - WALK_SPEED) / band, 0.0, 1.0))


func _settle(frames: int) -> void:
	for i in frames:
		await process_frame


# Attesa a TEMPO, non a frame.
#
# In headless il frame dura circa 7 ms, non 16,7: contare i frame per aspettare la fine
# di una clip da' un'attesa lunga meno della meta' del previsto, e il controllo fallisce
# per colpa della sonda invece che del rig. Serve solo dove conta la durata REALE di una
# clip — la vita dei one-shot.
func _settle_seconds(seconds: float) -> void:
	var elapsed := 0.0
	while elapsed < seconds:
		elapsed += get_root().get_process_delta_time()
		await process_frame


func _initialize() -> void:
	print("== %s (comportamento) ==" % RIG_PATH)

	var rig: Node = (load(RIG_PATH) as PackedScene).instantiate()
	# Via lo script: CharacterAnimator._Process riscriverebbe i parametri a ogni frame
	# e la sonda misurerebbe i suoi valori invece di quelli impostati qui. Quello che
	# si vuole verificare e' l'ALBERO; la logica C# che lo pilota e' replicata sopra
	# in _drive e _diamond.
	rig.set_script(null)
	root.add_child(rig)
	await process_frame

	_tree = rig.get_node("AnimationTree") as AnimationTree
	_skel = _tree.get_node(_tree.root_node).get_node("Armature_Character/Skeleton3D") as Skeleton3D

	# --- Nessuna posa deve essere la T-pose ---------------------------------
	# Ogni combinazione qui sotto deve produrre una posa VERA. Una qualunque che
	# ricada sulla rest pose e' un blend space senza triangoli.
	var casi := [
		["fermo in piedi", Vector2.ZERO, 0.0, 0.0],
		["camminata avanti", Vector2(0, WALK_SPEED), 0.0, 0.0],
		["camminata indietro", Vector2(0, -WALK_SPEED), 0.0, 0.0],
		["strafe sinistra", Vector2(-WALK_SPEED, 0), 0.0, 0.0],
		["strafe destra", Vector2(WALK_SPEED, 0), 0.0, 0.0],
		["diagonale avanti-destra", Vector2(2.83, 2.83), 0.0, 0.0],
		["diagonale indietro-sx", Vector2(-2.83, -2.83), 0.0, 0.0],
		["corsa avanti", Vector2(0, RUN_SPEED), 0.0, 0.0],
		["corsa indietro", Vector2(0, -RUN_SPEED), 0.0, 0.0],
		["corsa sinistra", Vector2(-RUN_SPEED, 0), 0.0, 0.0],
		["corsa destra", Vector2(RUN_SPEED, 0), 0.0, 0.0],
		["corsa diagonale", Vector2(4.95, 4.95), 0.0, 0.0],
		["accovacciato fermo", Vector2.ZERO, 1.0, 0.0],
		["accovacciato avanti", Vector2(0, CROUCH_SPEED), 1.0, 0.0],
		["accovacciato indietro", Vector2(0, -CROUCH_SPEED), 1.0, 0.0],
		["accovacciato sinistra", Vector2(-CROUCH_SPEED, 0), 1.0, 0.0],
		["accovacciato destra", Vector2(CROUCH_SPEED, 0), 1.0, 0.0],
		["accovacciato diagonale", Vector2(1.41, 1.41), 1.0, 0.0],
		["in piedi armato", Vector2.ZERO, 0.0, 1.0],
		["camminata armato", Vector2(0, WALK_SPEED), 0.0, 1.0],
		["accovacciato armato", Vector2.ZERO, 1.0, 1.0],
	]
	_tree.set("parameters/WeaponPose/transition_request", "rifle")
	for caso in casi:
		_drive(caso[1], caso[2], caso[3])
		await _settle(12)
		var d := _rest_distance()
		_check("'%s' non e' in T-pose" % caso[0], d > TPOSE_EPSILON,
			"scarto dalla rest pose = %.4f rad" % d)

	# --- La locomozione non deve congelarsi ----------------------------------
	# Dieci secondi sono nove cicli di walk_fwd: senza loop_mode la clip finisce al
	# primo e le gambe restano immobili per i nove decimi restanti.
	_drive(Vector2(0, WALK_SPEED), 0.0, 0.0)
	await _settle(30)
	var m: float = await _legs_motion(20, 30)
	_check("camminata: le gambe si muovono per 10 s", m > MOTION_EPSILON,
		"finestra piu' quieta = %.5f rad" % m)

	# Da armato il layer arma ha peso 1 e la locomozione peso 0, ma le gambe restano
	# visibili perche' il filtro copre solo l'upper body: e' il caso in cui
	# AnimationNodeSync.sync = false congelava tutto.
	_drive(Vector2(0, WALK_SPEED), 0.0, 1.0)
	await _settle(30)
	m = await _legs_motion(10, 30)
	_check("camminata armato: le gambe si muovono", m > MOTION_EPSILON,
		"finestra piu' quieta = %.5f rad" % m)

	# Sparare mentre si cammina non deve fermare le gambe: e' l'invariante del
	# filtro upper-body.
	for raffica in 10:
		_tree.set("parameters/Fire/request", AnimationNodeOneShot.ONE_SHOT_REQUEST_FIRE)
		await _settle(8)
	m = await _legs_motion(10, 30)
	_check("raffica in camminata: le gambe si muovono", m > MOTION_EPSILON,
		"finestra piu' quieta = %.5f rad" % m)

	# Il one-shot del salto deve RIENTRARE: se la clip ciclasse resterebbe attivo per
	# sempre e la locomozione non tornerebbe mai.
	# jump dura 1,03 s (scala 1 senza CharacterAnimator) + 0,20 s di dissolvenza.
	_tree.set("parameters/Jump/request", AnimationNodeOneShot.ONE_SHOT_REQUEST_FIRE)
	await _settle_seconds(3.0)
	_check("il one-shot del salto rientra", not _tree.get("parameters/Jump/active"),
		"ancora attivo dopo 3 s")

	# --- stato di caduta e atterraggio duro ---------------------------------
	# fall_loop e' un LOOP: in aria a lungo la posa non deve congelarsi.
	_drive(Vector2(0, WALK_SPEED), 0.0, 0.0)
	_tree.set("parameters/AirBlend/blend_amount", 1.0)
	await _settle(30)
	var d := _rest_distance()
	_check("'in caduta' non e' in T-pose", d > TPOSE_EPSILON,
		"scarto dalla rest pose = %.4f rad" % d)
	m = await _legs_motion(6, 30)
	_check("la caduta cicla (non si congela)", m > MOTION_EPSILON,
		"finestra piu' quieta = %.5f rad" % m)
	_tree.set("parameters/AirBlend/blend_amount", 0.0)
	await _settle(30)

	# L'atterraggio duro e' un one-shot: deve partire e RIENTRARE.
	# land_hard dura 2,03 s + 0,25 s di dissolvenza.
	_tree.set("parameters/Land/request", AnimationNodeOneShot.ONE_SHOT_REQUEST_FIRE)
	await _settle(4)
	_check("l'atterraggio duro parte", _tree.get("parameters/Land/active"))
	await _settle_seconds(4.0)
	_check("l'atterraggio duro rientra", not _tree.get("parameters/Land/active"),
		"ancora attivo dopo 4 s")

	await _verify_grip(rig)

	print("")
	print("%d controlli falliti" % _failures)
	quit(1 if _failures > 0 else 0)


# Aggancio dell'arma alla mano e IK della mano di supporto (Fase D).
#
# Il difetto che chiude: WeaponMount era un Node3D con offset fisso sotto Visual, quindi
# l'arma fluttuava accanto al fianco invece di stare in mano. Qui si misura la sola cosa
# che conta davvero — quanto dista il punto di presa dall'osso della mano, e quanto dista
# la mano sinistra dal punto di supporto sull'arma.
func _verify_grip(rig: Node) -> void:
	print("")
	print("== presa dell'arma e IK (Fase D) ==")

	var grip_rig: Node3D = rig.get_node_or_null("GripRig")
	_check("GripRig presente nel rig", grip_rig != null)
	if grip_rig == null:
		return

	var attachment := _skel.get_node_or_null("RightHandAttachment") as BoneAttachment3D
	_check("BoneAttachment3D creato sulla mano destra", attachment != null)
	if attachment == null:
		return
	_check("agganciato all'osso RightHand", attachment.bone_name == "RightHand", attachment.bone_name)

	var grip_point := attachment.get_node_or_null("GripPoint") as Node3D
	_check("punto di presa presente", grip_point != null)
	if grip_point == null:
		return

	# Arma a due mani: presa dichiarata dal .tres, mano sinistra in IK sull'astina.
	var set_res: Resource = load("res://animation/resources/two_handed.tres")
	_check("WeaponAnimationSet a due mani caricato", set_res != null)
	if set_res == null:
		return

	grip_rig.call("ApplyWeapon", set_res)
	_drive(Vector2(0, WALK_SPEED), 0.0, 1.0)
	await _settle(90)

	# Il punto di presa deve stare ADDOSSO alla mano destra, non da qualche altra parte.
	var hand_world: Vector3 = _skel.global_transform * _skel.get_bone_global_pose(
		_skel.find_bone("RightHand")).origin
	var grip_distance: float = hand_world.distance_to(grip_point.global_position)
	var grip_expected: float = (set_res.get("GripOffset") as Vector3).length()
	_check("la presa segue la mano destra", absf(grip_distance - grip_expected) < 0.05,
		"distanza=%.3f m, offset dichiarato=%.3f m" % [grip_distance, grip_expected])

	var support := grip_point.get_node_or_null("SupportGripTarget") as Node3D
	_check("bersaglio della mano di supporto presente", support != null)
	if support == null:
		return

	# LACUNA DICHIARATA: l'IK della mano di supporto e' spento (EnableSupportHandIk = false)
	# perche' TwoBoneIkModifier viene eseguito ma la posa che calcola non arriva allo
	# scheletro. Qui si verifica solo che resti spento: acceso e non funzionante sarebbe
	# peggio che assente, perche' sembrerebbe fatto. Quando il modificatore funzionera',
	# questo controllo va sostituito con la misura dell'errore residuo (< 0.08 m).
	var left_world: Vector3 = _skel.global_transform * _skel.get_bone_global_pose(
		_skel.find_bone("LeftHand")).origin
	var reach_error: float = left_world.distance_to(support.global_position)
	_check("IK della mano di supporto dichiarato spento", not grip_rig.get("EnableSupportHandIk"),
		"acceso ma non converge: errore residuo = %.3f m" % reach_error)

	# Il rinculo deve muovere la presa e poi RIENTRARE: se non rientrasse, l'arma
	# arretrerebbe di un po' a ogni colpo e finirebbe dentro il torace.
	var before: Vector3 = grip_point.position
	grip_rig.call("PlayRecoil")
	await _settle(2)
	var kicked: Vector3 = grip_point.position
	_check("il rinculo sposta la presa", before.distance_to(kicked) > 0.001,
		"spostamento = %.4f m" % before.distance_to(kicked))
	await _settle(120)
	_check("il rinculo rientra", grip_point.position.distance_to(before) < 0.002,
		"scarto residuo = %.4f m" % grip_point.position.distance_to(before))
