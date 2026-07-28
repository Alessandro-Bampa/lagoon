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

# Ossa da campionare DENTRO la passata dei modificatori (vedi _bone_after_modifiers).
var _watched: Dictionary = {}
var _watched_pose: Dictionary = {}


# Posa di un osso DOPO i modificatori di scheletro, in coordinate mondo.
#
# Serve un giro cosi' contorto perche' Godot 4.7 RIPRISTINA le pose sorgente al termine
# della passata dei modificatori: un SkeletonModifier3D scrive, il risultato va allo
# skinning, e subito dopo lo scheletro rimette le pose animate. Chiamare
# get_bone_global_pose() dal normale codice di gioco restituisce quindi la posa PRIMA
# dell'IK, sempre, anche quando l'IK funziona benissimo.
#
# E' un fallimento MUTO della stessa famiglia dei tre della skill: nessun errore, un
# valore plausibile, e la conclusione sbagliata che l'IK "non applica". La finestra in
# cui il risultato e' leggibile e' il segnale skeleton_updated, che scatta dentro la
# passata. Chi ha bisogno del risultato in gioco deve agganciarsi li'.
func _watch_bone(bone_name: String) -> void:
	if _watched.is_empty():
		_skel.skeleton_updated.connect(_sample_watched)
	_watched[bone_name] = _skel.find_bone(bone_name)


func _sample_watched() -> void:
	for bone_name in _watched:
		_watched_pose[bone_name] = _skel.global_transform * _skel.get_bone_global_pose(
			_watched[bone_name])


func _bone_after_modifiers(bone_name: String) -> Vector3:
	var t: Transform3D = _watched_pose.get(bone_name, Transform3D.IDENTITY)
	return t.origin


func _bone_basis_after_modifiers(bone_name: String) -> Basis:
	var t: Transform3D = _watched_pose.get(bone_name, Transform3D.IDENTITY)
	return t.basis


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


# Replica cio' che fa CharacterAnimator, perche' qui il suo script e' stato tolto.
#
# `weapon` e' l'override upper-body (pistola, o fucile da accovacciato); `stance` accende
# il set di locomozione ARMATO su tutto il corpo. Sono due meccanismi distinti: vedi
# CharacterAnimator.UpdateWeapon.
func _drive(local_velocity: Vector2, crouch: float, weapon: float, stance: float = 0.0) -> void:
	var walk := _diamond(local_velocity, WALK_SPEED)
	var run := _diamond(local_velocity, RUN_SPEED)
	_tree.set("parameters/WalkSpace/blend_position", walk)
	_tree.set("parameters/RunSpace/blend_position", run)
	_tree.set("parameters/RifleWalkSpace/blend_position", walk)
	_tree.set("parameters/RifleRunSpace/blend_position", run)
	_tree.set("parameters/CrouchSpace/blend_position", _diamond(local_velocity, CROUCH_SPEED))
	_tree.set("parameters/CrouchBlend/blend_amount", crouch)
	_tree.set("parameters/WeaponBlend/blend_amount", weapon)
	_tree.set("parameters/StanceBlend/blend_amount", stance)
	_tree.set("parameters/AirBlend/blend_amount", 0.0)
	var band: float = maxf(RUN_SPEED - WALK_SPEED, 0.001)
	var run_weight: float = clampf((local_velocity.length() - WALK_SPEED) / band, 0.0, 1.0)
	_tree.set("parameters/MoveBlend/blend_amount", run_weight)
	_tree.set("parameters/ArmedMoveBlend/blend_amount", run_weight)


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
		["pistola in piedi", Vector2.ZERO, 1.0, 0.0],
		["pistola in camminata", Vector2(0, WALK_SPEED), 1.0, 0.0],
		["accovacciato armato", Vector2.ZERO, 1.0, 0.0, 1.0],
		# Stance ARMATA a due mani: e' il set di locomozione nuovo, quattro assi per
		# camminata e corsa. Sono i punti di blend che prima non esistevano affatto.
		["fucile fermo", Vector2.ZERO, 0.0, 0.0, 1.0],
		["fucile avanti", Vector2(0, WALK_SPEED), 0.0, 0.0, 1.0],
		["fucile indietro", Vector2(0, -WALK_SPEED), 0.0, 0.0, 1.0],
		["fucile strafe sinistra", Vector2(-WALK_SPEED, 0), 0.0, 0.0, 1.0],
		["fucile strafe destra", Vector2(WALK_SPEED, 0), 0.0, 0.0, 1.0],
		["fucile diagonale", Vector2(2.83, 2.83), 0.0, 0.0, 1.0],
		["fucile corsa avanti", Vector2(0, RUN_SPEED), 0.0, 0.0, 1.0],
		["fucile corsa indietro", Vector2(0, -RUN_SPEED), 0.0, 0.0, 1.0],
		["fucile corsa sinistra", Vector2(-RUN_SPEED, 0), 0.0, 0.0, 1.0],
		["fucile corsa destra", Vector2(RUN_SPEED, 0), 0.0, 0.0, 1.0],
		["fucile corsa diagonale", Vector2(4.95, 4.95), 0.0, 0.0, 1.0],
	]
	# La posa d'arma del layer upper-body: "rifle_aim" e' l'ingresso di mira del
	# Transition a 4 pose (rifle_lowered / rifle_aim / pistol / pistol_aim).
	_tree.set("parameters/WeaponPose/transition_request", "rifle_aim")
	for caso in casi:
		_drive(caso[1], caso[2], caso[3], caso[4] if caso.size() > 4 else 0.0)
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

	# Con la PISTOLA il layer upper-body ha peso 1 e la locomozione peso 0, ma le gambe
	# restano visibili perche' il filtro copre solo il busto: e' il caso in cui
	# AnimationNodeSync.sync = false congelava tutto.
	_drive(Vector2(0, WALK_SPEED), 0.0, 1.0)
	await _settle(30)
	m = await _legs_motion(10, 30)
	_check("camminata con pistola: le gambe si muovono", m > MOTION_EPSILON,
		"finestra piu' quieta = %.5f rad" % m)

	# Stessa verifica sul set armato: qui le gambe vengono da clip diverse, quindi un
	# errore nei nomi delle clip nuove si vedrebbe come locomozione ferma.
	_drive(Vector2(0, WALK_SPEED), 0.0, 0.0, 1.0)
	await _settle(30)
	m = await _legs_motion(10, 30)
	_check("camminata col fucile: le gambe si muovono", m > MOTION_EPSILON,
		"finestra piu' quieta = %.5f rad" % m)

	# E la stance armata deve dare una posa DIVERSA da quella disarmata: se i due set
	# producessero la stessa cosa, vorrebbe dire che StanceBlend non e' collegato.
	_drive(Vector2(0, WALK_SPEED), 0.0, 0.0, 0.0)
	await _settle(40)
	var unarmed_pose := _legs_snapshot()
	_drive(Vector2(0, WALK_SPEED), 0.0, 0.0, 1.0)
	await _settle(40)
	var armed_pose := _legs_snapshot()
	var spread := 0.0
	for i in unarmed_pose.size():
		spread = maxf(spread, unarmed_pose[i].angle_to(armed_pose[i]))
	_check("la stance armata cambia davvero la locomozione", spread > 0.02,
		"scarto massimo sulle gambe = %.4f rad" % spread)

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
	_tree.set("parameters/LandPose/transition_request", "land_hard")
	_tree.set("parameters/Land/request", AnimationNodeOneShot.ONE_SHOT_REQUEST_FIRE)
	await _settle(4)
	_check("l'atterraggio duro parte", _tree.get("parameters/Land/active"))
	await _settle_seconds(4.0)
	_check("l'atterraggio duro rientra", not _tree.get("parameters/Land/active"),
		"ancora attivo dopo 4 s")

	# L'atterraggio MORBIDO usa la clip procedurale land_soft via il Transition LandPose:
	# 0,45 s + 0,25 s di dissolvenza, deve partire e rientrare come il duro.
	_tree.set("parameters/LandPose/transition_request", "land_soft")
	_tree.set("parameters/Land/request", AnimationNodeOneShot.ONE_SHOT_REQUEST_FIRE)
	await _settle(4)
	_check("l'atterraggio morbido parte", _tree.get("parameters/Land/active"))
	await _settle_seconds(2.0)
	_check("l'atterraggio morbido rientra", not _tree.get("parameters/Land/active"),
		"ancora attivo dopo 2 s")

	await _verify_strafe_direction()
	await _verify_procedural_clips(rig)
	await _verify_grip(rig)
	await _verify_aim(rig)
	await _verify_feet(rig)
	await _verify_muzzle(rig)
	await _verify_npc()

	print("")
	print("%d controlli falliti" % _failures)
	quit(1 if _failures > 0 else 0)


# Direzione dello strafe (anti-specchiamento).
#
# Il contratto della blend position e' "X = destra": a +X deve suonare la clip *_right.
# Nessun altro controllo lo copre — "non e' in T-pose" passa anche con le clip scambiate,
# e la copertura dei triangoli pure. E' il bug che ha specchiato lo strafe in tutte e
# cinque le locomozioni senza un solo errore: la X locale veniva pubblicata col segno
# della SINISTRA (il Visual guarda +Z e la sua sinistra e' +X), e nessuna sonda misurava
# la direzione. La meta' C# del contratto e' verificata in _verify_npc su
# CharacterMotor.WorldToLocalVelocity; qui si verifica la meta' dell'ALBERO.
func _verify_strafe_direction() -> void:
	print("")
	print("== direzione dello strafe ==")

	var suites := [
		["camminata", "walk_right", "walk_left", 0.0],
		["fucile", "rifle_walk_right", "rifle_walk_left", 1.0],
	]
	for suite in suites:
		_drive(Vector2(WALK_SPEED, 0), 0.0, 0.0, suite[3])
		await _settle(60)
		var pose := _pose_snapshot(["Hips", "LeftUpLeg", "RightUpLeg"])
		var to_right: float = _min_clip_distance(suite[1], pose)
		var to_left: float = _min_clip_distance(suite[2], pose)
		_check("%s: blend a +X riproduce la clip DESTRA" % suite[0], to_right < to_left,
			"distanza da %s=%.3f, da %s=%.3f rad" % [suite[1], to_right, suite[2], to_left])


func _pose_snapshot(bones: Array) -> Dictionary:
	var out := {}
	for bone_name in bones:
		out[bone_name] = _skel.get_bone_pose_rotation(_skel.find_bone(bone_name))
	return out


# Distanza minima fra una posa e una clip campionata lungo TUTTA la sua durata: la fase
# di riproduzione del blend space non e' nota, quindi si confronta col fotogramma piu'
# vicino. Sulla clip giusta il minimo e' ~0; su quella specchiata resta grande, perche'
# le clip di strafe ruotano il bacino da lati opposti.
func _min_clip_distance(clip: String, pose: Dictionary) -> float:
	var anim: Animation = _tree.get_animation(clip)
	if anim == null:
		return INF
	var best := INF
	var samples := 24
	for s in samples:
		var t: float = anim.length * float(s) / float(samples)
		var total := 0.0
		for bone_name in pose:
			var track := _find_rotation_track(anim, bone_name)
			if track < 0:
				continue
			var q: Quaternion = anim.rotation_track_interpolate(track, t)
			total += (pose[bone_name] as Quaternion).angle_to(q)
		best = minf(best, total)
	return best


func _find_rotation_track(anim: Animation, bone_name: String) -> int:
	for i in anim.get_track_count():
		if anim.track_get_type(i) == Animation.TYPE_ROTATION_3D \
				and String(anim.track_get_path(i)).ends_with(":" + bone_name):
			return i
	return -1


# Clip procedurali (build_procedural_clips.py): esistono, non sono la T-pose, e la
# posa di mira e' DIVERSA dal porto rilassato.
#
# Si suonano con un AnimationPlayer di servizio (albero spento): l'albero non le
# referenzia ancora tutte e qui si vuole verificare la CLIP, non il blending.
func _verify_procedural_clips(rig: Node) -> void:
	print("")
	print("== clip procedurali ==")

	var player := AnimationPlayer.new()
	player.root_node = NodePath("../Body")
	rig.add_child(player)
	player.add_animation_library("", _tree.get_animation_library(""))

	_tree.active = false

	for clip in ["rifle_aim_idle", "rifle_lowered_idle", "pistol_aim_idle",
			"pistol_fire", "land_soft"]:
		var ok: bool = player.has_animation(clip)
		_check("'%s' presente in libreria" % clip, ok)
		if not ok:
			continue
		player.play(clip)
		await _settle(8)
		var d := _rest_distance()
		_check("'%s' non e' in T-pose" % clip, d > TPOSE_EPSILON,
			"scarto dalla rest pose = %.4f rad" % d)

	# La mira alza le mani, il porto le abbassa: se le due pose coincidessero, il
	# Transition della Fase 4 non mostrerebbe alcuna differenza fra RMB premuto e no.
	var hand := _skel.find_bone("RightHand")
	if player.has_animation("rifle_aim_idle") and player.has_animation("rifle_lowered_idle"):
		player.play("rifle_aim_idle")
		await _settle(8)
		var aim_y: float = _skel.get_bone_global_pose(hand).origin.y
		player.play("rifle_lowered_idle")
		await _settle(8)
		var lowered_y: float = _skel.get_bone_global_pose(hand).origin.y
		_check("la mira alza la mano rispetto al porto", aim_y > lowered_y + 0.10,
			"mano destra: aim %.3f m, porto %.3f m" % [aim_y, lowered_y])

	player.stop()
	rig.remove_child(player)
	player.queue_free()
	_tree.active = true
	await _settle(4)


# Mira procedurale (SpineAimModifier).
#
# E' il controllo che chiude il difetto da cui e' nato tutto il lavoro: da armato l'arma non
# puntava dove puntava il cursore, perche' le clip di strafe ruotano il bacino e l'arma pende
# dalla catena che parte da li'. Qui si guida di proposito uno STRAFE — cioe' il caso peggiore,
# quello che ruota di piu' il bacino — e si pretende che il busto finisca comunque sulla mira.
#
# Tutte le misure passano da _bone_after_modifiers: lette fuori dalla passata dei modificatori
# darebbero la posa animata, e il controllo direbbe sempre "non funziona".
func _verify_aim(rig: Node) -> void:
	print("")
	print("== mira procedurale ==")

	var aim_rig: Node3D = rig.get_node_or_null("AimRig")
	_check("AimRig presente nel rig", aim_rig != null)
	if aim_rig == null:
		return

	var modifier := _skel.get_node_or_null("SpineAim")
	_check("SpineAimModifier costruito sotto lo scheletro", modifier != null)
	if modifier == null:
		return

	_watch_bone("Spine2")

	# Caso peggiore: strafe a sinistra, che e' la clip che ruota di piu' il bacino.
	_drive(Vector2(-WALK_SPEED, 0), 0.0, 1.0)

	# Mira orizzontale in avanti (+Z, la direzione in cui guarda il rig).
	aim_rig.set("Weight", 1.0)
	aim_rig.set("AimDirection", Vector3(0, 0, 1))
	await _settle(60)
	var forward_error: float = _aim_error(Vector3(0, 0, 1))
	_check("da armato il busto punta sulla mira", forward_error < 15.0,
		"scarto = %.1f gradi" % forward_error)

	# Mira in salita: e' l'inclinazione che NESSUNA clip contiene.
	var up_aim := Vector3(0, 0.55, 1).normalized()
	aim_rig.set("AimDirection", up_aim)
	await _settle(60)
	var up_error: float = _aim_error(up_aim)
	_check("la mira verticale viene seguita", up_error < 15.0,
		"scarto = %.1f gradi" % up_error)

	# E deve trascinarsi dietro l'ARMA: se il punto di presa non si muove, la correzione
	# resta sulla mesh e il modello dell'arma continua a puntare altrove.
	var grip: Node3D = _skel.get_node_or_null("RightHandAttachment/GripPoint")
	_check("punto di presa raggiungibile", grip != null)
	if grip == null:
		return

	var high: Vector3 = grip.global_position
	aim_rig.set("AimDirection", Vector3(0, -0.55, 1).normalized())
	await _settle(60)
	var low: Vector3 = grip.global_position
	_check("l'arma segue la correzione di mira", high.distance_to(low) > 0.05,
		"spostamento della presa fra mira alta e bassa = %.3f m" % high.distance_to(low))

	# Spenta la mira, il busto deve tornare alla posa animata: un layer procedurale che non
	# si spegne e' peggio di uno che non c'e'.
	aim_rig.set("Weight", 0.0)
	await _settle(60)
	_check("a peso zero la correzione rientra", _aim_error(Vector3(0, 0, 1)) > 0.5,
		"il busto e' rimasto agganciato alla mira anche da disarmato")


# Scarto in GRADI fra dove punta il busto e dove si sta mirando.
func _aim_error(aim: Vector3) -> float:
	# "Avanti" nello spazio dell'osso, misurato sulla posa di riposo: cosi' la sonda non
	# assume nulla su come sono orientati gli assi dei bone di questo rig.
	var tip := _skel.find_bone("Spine2")
	var forward_in_bone: Vector3 = _skel.get_bone_global_rest(tip).basis.inverse() * Vector3.BACK
	var forward: Vector3 = (_bone_basis_after_modifiers("Spine2") * forward_in_bone).normalized()
	return rad_to_deg(forward.angle_to(aim.normalized()))


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

	# IK della mano di supporto: la mano sinistra deve arrivare SULL'ASTINA.
	#
	# La posa va letta dopo i modificatori (_bone_after_modifiers): letta fuori dalla
	# passata si otterrebbe la posa animata e l'IK sembrerebbe non funzionare — ed e'
	# esattamente l'errore di misura che per una fase intera ha fatto credere rotto un IK
	# che invece girava.
	_check("IK della mano di supporto acceso", grip_rig.get("EnableSupportHandIk"))
	var ik := _skel.get_node_or_null("SupportHandIk") as TwoBoneIK3D
	_check("TwoBoneIK3D nativo costruito", ik != null)
	if ik != null:
		_check("il polo del gomito e' dichiarato", not ik.get_pole_node(0).is_empty(),
			"senza pole_node TwoBoneIK3D non risolve affatto la catena")

	_watch_bone("LeftHand")
	_watch_bone("RightHand")
	await _settle(60)
	var reach_error: float = _bone_after_modifiers("LeftHand").distance_to(support.global_position)
	_check("la mano di supporto raggiunge l'astina", reach_error < 0.08,
		"errore residuo = %.3f m" % reach_error)

	# L'arma deve GIACERE fra le due mani, non spuntare dalla mano in una direzione qualsiasi.
	# E' il controllo che il placeholder e' orientato sull'animazione e non su un sistema di
	# riferimento inventato: il vecchio box era tarato su quando l'avatar era una capsula.
	var between: Vector3 = _bone_after_modifiers("LeftHand") - _bone_after_modifiers("RightHand")
	# +Z e non -Z: in questo progetto l'arma punta verso +Z locale, come il "naso" dell'avatar
	# (il rig arriva da glTF, dove il personaggio guarda +Z, non la -Z convenzionale di Godot).
	var axis: Vector3 = grip_point.global_transform.basis.z
	var misalignment: float = rad_to_deg(axis.angle_to(between))
	_check("l'asse dell'arma passa fra le mani", misalignment < 20.0,
		"scarto = %.1f gradi" % misalignment)

	# Il rinculo deve muovere la presa e poi RIENTRARE: se non rientrasse, l'arma
	# arretrerebbe di un po' a ogni colpo e finirebbe dentro il torace.
	var before: Vector3 = grip_point.position
	var muzzle_before: float = grip_point.global_transform.basis.z.normalized().y
	grip_rig.call("PlayRecoil")
	await _settle(2)
	var kicked: Vector3 = grip_point.position
	_check("il rinculo sposta la presa", before.distance_to(kicked) > 0.001,
		"spostamento = %.4f m" % before.distance_to(kicked))
	# Direzione, non solo entita': un rinculo che abbassa il muso e' un rinculo sbagliato, e
	# misurando il solo spostamento non si distingue dall'altro.
	_check("il rinculo alza il muso",
		grip_point.global_transform.basis.z.normalized().y > muzzle_before,
		"componente verticale da %.4f a %.4f"
			% [muzzle_before, grip_point.global_transform.basis.z.normalized().y])
	await _settle(120)
	_check("il rinculo rientra", grip_point.position.distance_to(before) < 0.002,
		"scarto residuo = %.4f m" % grip_point.position.distance_to(before))


# Piedi a terra (FootIkRig).
#
# Si costruisce uno SCALINO: meta' pavimento a quota 0, meta' a quota 0.15. Un piede finisce
# sul gradino e l'altro no — che e' il caso in cui le clip, che mettono i piedi sempre alla
# stessa quota, sbagliano in modo visibile: uno affonda o uno galleggia.
#
# Anche qui le pose si leggono dopo i modificatori: fuori dalla passata si otterrebbe la posa
# animata, cioe' proprio quella che l'IK deve correggere.
func _verify_feet(rig: Node) -> void:
	print("")
	print("== piedi a terra ==")

	var foot_rig: Node3D = rig.get_node_or_null("FootIkRig")
	_check("FootIkRig presente nel rig", foot_rig != null)
	if foot_rig == null:
		return

	var ik := _skel.get_node_or_null("FootIk") as TwoBoneIK3D
	_check("TwoBoneIK3D dei piedi costruito", ik != null)
	if ik == null:
		return
	_check("due catene configurate", ik.get_setting_count() == 2, str(ik.get_setting_count()))
	_check("entrambe le catene hanno un polo",
		not ik.get_pole_node(0).is_empty() and not ik.get_pole_node(1).is_empty(),
		"senza pole_node TwoBoneIK3D non risolve")

	# La SINISTRA del personaggio e' +X (vedi tools/blender/build_character.py). Il lato
	# destro sprofonda di 12 cm: e' il caso che mette alla prova ENTRAMBI i meccanismi,
	# perche' un piede deve scendere piu' in basso di dove la gamba arriva da sola, e per
	# arrivarci deve essere il bacino ad abbassarsi. Un gradino in RIALZO non lo farebbe:
	# li' basta piegare di piu' la gamba, e il bacino resta giustamente dov'e'.
	var ground := StaticBody3D.new()
	ground.collision_layer = 1     # CollisionLayers.World
	ground.collision_mask = 0
	_add_slab(ground, Vector3(-1.0, -0.62, 0), Vector3(2, 1, 4))
	_add_slab(ground, Vector3(1.0, -0.5, 0), Vector3(2, 1, 4))
	root.add_child(ground)

	_watch_bone("LeftFoot")
	_watch_bone("RightFoot")

	_drive(Vector2.ZERO, 0.0, 0.0)
	foot_rig.set("Grounded", true)
	foot_rig.set("LocalVelocity", Vector2.ZERO)

	# L'abbassamento del bacino lo APPLICA CharacterAnimator, che qui e' senza script: va
	# replicato a mano, come si replicano i parametri dell'albero in _drive. Senza, i
	# bersagli restano dove il terreno li vuole ma il corpo no, e la gamba non ci arriva.
	var rest_y: float = (rig as Node3D).position.y
	for i in 150:
		await process_frame
		(rig as Node3D).position.y = rest_y - float(foot_rig.get("PelvisDrop"))

	var left_y: float = _bone_after_modifiers("LeftFoot").y
	var right_y: float = _bone_after_modifiers("RightFoot").y

	# Il dislivello fra i due piedi deve riprodurre quello del terreno, non limitarsi ad
	# avvicinarcisi: una tolleranza larga lascerebbe passare un IK che converge a meta'.
	var step: float = left_y - right_y
	_check("i piedi riproducono il dislivello del terreno", absf(step - 0.12) < 0.02,
		"sinistro %.3f m, destro %.3f m, dislivello %.3f m (atteso 0.120)" % [left_y, right_y, step])

	var drop: float = foot_rig.get("PelvisDrop")
	_check("il bacino scende verso il piede piu' basso", drop > 0.01,
		"abbassamento = %.3f m" % drop)

	# In aria l'IK deve spegnersi: un piede tirato verso un suolo che non si sta toccando e'
	# peggio dell'assenza di IK.
	foot_rig.set("Grounded", false)
	await _settle(90)
	_check("in aria l'IK dei piedi si spegne", ik.influence < 0.05,
		"influence = %.3f" % ik.influence)

	ground.queue_free()


func _add_slab(body: StaticBody3D, pos: Vector3, size: Vector3) -> void:
	var shape := CollisionShape3D.new()
	var box := BoxShape3D.new()
	box.size = size
	shape.shape = box
	shape.position = pos
	body.add_child(shape)


# Reazione ai muri (WeaponSpaceProbe -> "port arms").
#
# Si mette un muro davanti alla canna e si pretende che l'arma si alzi e si ritragga, e che
# torni giu' quando il muro sparisce. E' la seconda meta' del "reagire all'ambiente": la
# prima sono i piedi sul terreno.
func _verify_muzzle(rig: Node) -> void:
	print("")
	print("== arma contro i muri ==")

	var grip_rig: Node3D = rig.get_node_or_null("GripRig")
	var grip: Node3D = _skel.get_node_or_null("RightHandAttachment/GripPoint")
	_check("GripRig e punto di presa presenti", grip_rig != null and grip != null)
	if grip_rig == null or grip == null:
		return

	grip_rig.call("ApplyWeapon", load("res://animation/resources/two_handed.tres"))
	_drive(Vector2.ZERO, 0.0, 0.0, 1.0)
	await _settle(60)

	# Si misura la DIREZIONE della canna, non l'angolo di Eulero della presa: con la presa
	# gia' a -76 gradi, aggiungerne altri 25 supera i 90 e la decomposizione YXZ riavvolge
	# il valore su un triplo equivalente ma diverso. L'angolo letto resterebbe quasi fermo
	# anche con l'arma alzata per davvero.
	var free_pitch: float = grip.global_transform.basis.z.normalized().y
	var free_reach: float = grip.position.z
	_check("senza ostacoli la canna resta bassa", float(grip_rig.get("MuzzleBlocked")) < 0.05,
		"ostruzione = %.3f" % float(grip_rig.get("MuzzleBlocked")))

	# Ostacolo sulla traiettoria della canna.
	#
	# Va messo abbastanza LONTANO da non inghiottire l'origine del raggio: intersect_ray non
	# segnala nulla quando parte da dentro una forma (hit_from_inside e' false), e un muro
	# grande piazzato a 25 cm dalla presa contiene gia' il punto di partenza. Sintomo: nessun
	# colpo, come se la sonda fosse spenta.
	var wall := StaticBody3D.new()
	wall.collision_layer = 1     # CollisionLayers.World
	wall.collision_mask = 0
	var shape := CollisionShape3D.new()
	var box := BoxShape3D.new()
	box.size = Vector3(0.4, 0.4, 0.4)
	shape.shape = box
	wall.add_child(shape)
	root.add_child(wall)
	wall.global_position = grip.global_position + grip.global_transform.basis.z.normalized() * 0.55

	await _settle(90)
	var blocked: float = grip_rig.get("MuzzleBlocked")
	_check("contro un muro la sonda rileva l'ostruzione", blocked > 0.3,
		"ostruzione = %.3f" % blocked)
	var blocked_pitch: float = grip.global_transform.basis.z.normalized().y
	_check("la canna si alza", blocked_pitch > free_pitch + 0.15,
		"componente verticale della canna da %.3f a %.3f" % [free_pitch, blocked_pitch])
	_check("l'arma si ritrae", grip.position.z < free_reach - 0.02,
		"z da %.3f a %.3f m" % [free_reach, grip.position.z])

	wall.queue_free()
	await _settle(120)
	_check("tolto il muro l'arma torna giu'", float(grip_rig.get("MuzzleBlocked")) < 0.05,
		"ostruzione residua = %.3f" % float(grip_rig.get("MuzzleBlocked")))


# NPC: il rig e' davvero condiviso?
#
# Non e' un controllo sull'IA, che non esiste. E' il collaudo dell'invariante di
# animation/: lo stesso rig, lo stesso albero e gli stessi layer procedurali devono
# funzionare con un pilota diverso dal giocatore. Se qualcosa in animation/ avesse
# preso una dipendenza da player/, e' qui che si romperebbe.
func _verify_npc() -> void:
	print("")
	print("== NPC sullo stesso rig ==")

	var scene := load("res://ai/scenes/NpcCharacter.tscn") as PackedScene
	_check("NpcCharacter.tscn caricabile", scene != null)
	if scene == null:
		return

	var npc: Node3D = scene.instantiate()
	root.add_child(npc)
	await _settle(90)

	var rig: Node3D = npc.get_node_or_null("Visual/CharacterRig")
	_check("l'NPC istanzia il CharacterRig condiviso", rig != null)
	if rig == null:
		npc.queue_free()
		return

	var skel: Skeleton3D = rig.get_node("Body/Armature_Character/Skeleton3D")
	var spread := 0.0
	for i in skel.get_bone_count():
		spread = maxf(spread, skel.get_bone_pose_rotation(i).angle_to(
			skel.get_bone_rest(i).basis.get_rotation_quaternion()))
	_check("l'NPC non e' in T-pose", spread > TPOSE_EPSILON,
		"scarto dalla rest pose = %.4f rad" % spread)

	_check("l'NPC ha il proprio bridge di animazione",
		npc.get_node_or_null("AnimationBridge") != null)
	_check("i layer procedurali sono costruiti anche per l'NPC",
		skel.get_node_or_null("FootIk") != null and skel.get_node_or_null("SupportHandIk") != null)

	# Contratto del motore (meta' C# del check anti-specchiamento, vedi
	# _verify_strafe_direction): la velocita' locale pubblicata ha X = DESTRA.
	# Con yaw 0 l'avatar guarda +Z e la sua destra e' mondo -X (la sinistra del rig e' +X).
	var local: Vector2 = npc.call("WorldToLocalVelocity", Vector3(-1, 0, 0), 0.0)
	_check("WorldToLocalVelocity: mondo -X a yaw 0 e' DESTRA (+X locale)",
		local.distance_to(Vector2(1, 0)) < 0.001, str(local))
	local = npc.call("WorldToLocalVelocity", Vector3(0, 0, 1), PI / 2)
	_check("WorldToLocalVelocity: mondo +Z a yaw 90 e' DESTRA (+X locale)",
		local.distance_to(Vector2(1, 0)) < 0.001, str(local))
	local = npc.call("WorldToLocalVelocity", Vector3(0, 0, 1), 0.0)
	_check("WorldToLocalVelocity: mondo +Z a yaw 0 e' AVANTI (+Y locale)",
		local.distance_to(Vector2(0, 1)) < 0.001, str(local))

	# Turn-in-place: zona morta con isteresi (PlanAimFacing, soglie 55/8 gradi). La sequenza
	# e' STATEFUL apposta: attraversa ingresso, inseguimento, stop e reset in movimento.
	var yaw: float = npc.call("PlanAimFacing", 0.0, deg_to_rad(40.0), false)
	_check("PlanAimFacing: dentro la zona morta il corpo non ruota",
		absf(yaw) < 0.001, "target = %.3f rad" % yaw)
	yaw = npc.call("PlanAimFacing", 0.0, deg_to_rad(80.0), false)
	_check("PlanAimFacing: oltre la soglia parte il turn-in-place",
		absf(yaw - deg_to_rad(80.0)) < 0.001, "target = %.3f rad" % yaw)
	yaw = npc.call("PlanAimFacing", deg_to_rad(60.0), deg_to_rad(80.0), false)
	_check("PlanAimFacing: isteresi, il recupero continua sotto la soglia d'ingresso",
		absf(yaw - deg_to_rad(80.0)) < 0.001, "target = %.3f rad" % yaw)
	yaw = npc.call("PlanAimFacing", deg_to_rad(76.0), deg_to_rad(80.0), false)
	_check("PlanAimFacing: sotto la soglia di stop il corpo si ferma",
		absf(yaw - deg_to_rad(76.0)) < 0.001, "target = %.3f rad" % yaw)
	yaw = npc.call("PlanAimFacing", 0.0, deg_to_rad(10.0), true)
	_check("PlanAimFacing: in movimento il corpo insegue sempre la mira",
		absf(yaw - deg_to_rad(10.0)) < 0.001, "target = %.3f rad" % yaw)

	npc.queue_free()
