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

# Ossa delle braccia, per la sonda "le braccia si muovono": sono le stesse otto della
# maschera d'impugnatura, cioe' quelle che le clip di crouch di Mixamo lasciavano ferme.
const ARMS := [
	"LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand",
	"RightShoulder", "RightArm", "RightForeArm", "RightHand",
]

# Margine minimo fra l'asse dell'arma e le gambe da accovacciati, in metri.
#
# In piedi, col fucile al porto, la stessa misura vale 0,358 m; senza sollevamento delle
# braccia scende a 0,204 fermi e 0,035 in movimento, cioe' canna e avambracci dentro le
# cosce. Col sollevamento risale a 0,302 e 0,199: il caso in movimento resta il piu'
# stretto perche' crouch_fwd piega il busto in avanti di 59 gradi, e le braccia — figlie di
# Spine2 — quella piega se la portano dietro comunque. Raddrizzare il busto la toglierebbe,
# ma e' stato provato a schermo e il personaggio non sembrava piu' accovacciato.
const CROUCH_WEAPON_CLEARANCE := 0.18

# Distanza fra le due mani reggendo il fucile, cioe' la lunghezza dell'astina. DEVE
# combaciare con SupportGripOffset.z di animation/resources/two_handed.tres: e' la stessa
# grandezza vista da due parti, la posa d'impugnatura e il bersaglio dell'IK. La misura
# la stampa tools/build_weapon_poses.gd quando rigenera le pose.
const HANDGUARD := 0.254

# Scarto massimo tollerato, in gradi, fra la canna e la direzione in cui si sta mirando.
# E' il controllo che mancava: con le pose derivate da `rifle_idle` valeva 85 gradi (arma
# di traverso sul petto) e nessun altro controllo se ne accorgeva, perche' tutti
# misuravano DOVE finisce l'arma e nessuno DOVE punta.
const AIM_TOLERANCE_DEG := 15.0

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
# `hold` e' il peso della MASCHERA d'impugnatura (HoldMask, Blend2 filtrato sulle braccia):
# la posa la sceglie il Transition WeaponPose. La locomozione e' unica e agnostica
# dall'arma: non esistono piu' spazi armati ne' StanceBlend. `aim` e `aim_amount` pilotano
# l'aim offset, che invece additivo lo e' davvero.
func _drive(local_velocity: Vector2, crouch: float, hold: float,
		aim := Vector2.ZERO, aim_amount := 0.0) -> void:
	var walk := _diamond(local_velocity, WALK_SPEED)
	var run := _diamond(local_velocity, RUN_SPEED)
	_tree.set("parameters/WalkSpace/blend_position", walk)
	_tree.set("parameters/RunSpace/blend_position", run)
	_tree.set("parameters/CrouchSpace/blend_position", _diamond(local_velocity, CROUCH_SPEED))
	_tree.set("parameters/CrouchBlend/blend_amount", crouch)
	_tree.set("parameters/HoldMask/blend_amount", hold)
	_tree.set("parameters/AimSpace/blend_position", aim)
	_tree.set("parameters/AimAdd/add_amount", aim_amount)
	_tree.set("parameters/AirBlend/blend_amount", 0.0)
	var band: float = maxf(RUN_SPEED - WALK_SPEED, 0.001)
	var run_weight: float = clampf((local_velocity.length() - WALK_SPEED) / band, 0.0, 1.0)
	_tree.set("parameters/MoveBlend/blend_amount", run_weight)


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
	# I casi sono [etichetta, velocita' locale, crouch, hold additivo, aim offset, peso aim].
	# La locomozione e' UNICA e agnostica dall'arma: le combinazioni "armate" si ottengono
	# alzando il peso del delta di impugnatura sopra la stessa locomozione, non cambiando
	# set di clip. Il caso ARMATO va comunque provato su ogni asse: un additivo sbagliato
	# (delta calcolato contro il riferimento sbagliato) rompe la posa senza dare errori.
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
		["accovacciato armato", Vector2.ZERO, 1.0, 1.0],
		# Impugnatura ADDITIVA sopra ogni asse della locomozione unica: sono le
		# combinazioni che prima richiedevano un set di clip armato dedicato.
		["armato fermo", Vector2.ZERO, 0.0, 1.0],
		["armato avanti", Vector2(0, WALK_SPEED), 0.0, 1.0],
		["armato indietro", Vector2(0, -WALK_SPEED), 0.0, 1.0],
		["armato strafe sinistra", Vector2(-WALK_SPEED, 0), 0.0, 1.0],
		["armato strafe destra", Vector2(WALK_SPEED, 0), 0.0, 1.0],
		["armato diagonale", Vector2(2.83, 2.83), 0.0, 1.0],
		["armato corsa avanti", Vector2(0, RUN_SPEED), 0.0, 1.0],
		["armato corsa indietro", Vector2(0, -RUN_SPEED), 0.0, 1.0],
		["armato corsa sinistra", Vector2(-RUN_SPEED, 0), 0.0, 1.0],
		["armato corsa destra", Vector2(RUN_SPEED, 0), 0.0, 1.0],
		["armato corsa diagonale", Vector2(4.95, 4.95), 0.0, 1.0],
		# Aim offset: i quattro estremi della sfera di mira piu' il centro. Sono i punti
		# del BlendSpace2D additivo, dove una triangolazione degenere darebbe T-pose.
		["mira al centro", Vector2.ZERO, 0.0, 1.0, Vector2.ZERO, 1.0],
		["mira in alto", Vector2.ZERO, 0.0, 1.0, Vector2(0, 1), 1.0],
		["mira in basso", Vector2.ZERO, 0.0, 1.0, Vector2(0, -1), 1.0],
		["mira a sinistra", Vector2.ZERO, 0.0, 1.0, Vector2(-1, 0), 1.0],
		["mira a destra", Vector2.ZERO, 0.0, 1.0, Vector2(1, 0), 1.0],
		["mira diagonale in camminata", Vector2(0, WALK_SPEED), 0.0, 1.0, Vector2(0.5, 0.5), 1.0],
	]
	# La posa d'arma del layer additivo: "rifle_aim" e' l'ingresso di mira del
	# Transition a 4 pose (rifle_lowered / rifle_aim / pistol / pistol_aim).
	_tree.set("parameters/WeaponPose/transition_request", "rifle_aim")
	for caso in casi:
		_drive(caso[1], caso[2], caso[3],
			caso[4] if caso.size() > 4 else Vector2.ZERO,
			caso[5] if caso.size() > 5 else 0.0)
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

	# Con l'impugnatura additiva a peso 1 le gambe devono continuare a camminare: e' il
	# caso in cui AnimationNodeSync.sync = false congelava tutto.
	_drive(Vector2(0, WALK_SPEED), 0.0, 1.0)
	await _settle(30)
	m = await _legs_motion(10, 30)
	_check("camminata armata: le gambe si muovono", m > MOTION_EPSILON,
		"finestra piu' quieta = %.5f rad" % m)

	# E con l'aim offset acceso sopra: due Add2 in cascata sono il punto in cui un peso
	# sbagliato spegnerebbe la locomozione senza dare errori.
	_drive(Vector2(0, WALK_SPEED), 0.0, 1.0, Vector2(0.6, 0.4), 1.0)
	await _settle(30)
	m = await _legs_motion(10, 30)
	_check("camminata armata in mira: le gambe si muovono", m > MOTION_EPSILON,
		"finestra piu' quieta = %.5f rad" % m)

	# --- L'invariante dei layer additivi -------------------------------------
	# E' la ragione per cui il set di locomozione armato non serve piu': il delta di
	# impugnatura deve cambiare il BUSTO e lasciare intatte le GAMBE. Le clip delta
	# portano solo le track dell'upper body, quindi le gambe non ricevono nulla per
	# costruzione — un delta authorato con le gambe dentro si vedrebbe qui, e sarebbe
	# esattamente il difetto che rimetterebbe in gioco l'esplosione delle clip.
	await _verify_additive_isolation()

	# Sparare mentre si cammina non deve fermare le gambe: il delta di sparo tocca il
	# solo upper body. Si RIMETTE la camminata: la verifica qui sopra termina da fermo,
	# e misurare "le gambe si muovono" su un personaggio immobile misura il respiro
	# della clip di idle, non la locomozione.
	_drive(Vector2(0, WALK_SPEED), 0.0, 1.0)
	await _settle(30)
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

	await _verify_hit_reaction()
	await _verify_vault_clip()
	await _verify_strafe_direction()
	await _verify_hold_mask()
	await _verify_procedural_clips(rig)
	await _verify_grip(rig)
	await _verify_aim(rig)
	await _verify_feet(rig)
	await _verify_muzzle(rig)
	await _verify_crouch(rig)
	await _verify_npc()
	await _verify_slope()
	await _verify_parkour_geometry()

	print("")
	print("%d controlli falliti" % _failures)
	quit(1 if _failures > 0 else 0)


# Reazione ai colpi: quattro direzioni, additive, che rientrano.
#
# Sono one-shot in MIX_MODE_ADD su clip delta, quindi devono sommarsi a QUALUNQUE cosa
# stia girando sotto — qui si prova durante una camminata, che e' il caso in cui un
# flinch implementato come override fermerebbe le gambe. E devono essere distinguibili
# fra loro: quattro ingressi che producono la stessa posa vorrebbero dire che il
# Transition HitPose non e' cablato, e il colpo da destra si vedrebbe come da sinistra.
func _verify_hit_reaction() -> void:
	print("")
	print("== reazione ai colpi ==")

	var torso := ["Spine", "Spine1", "Spine2", "Head"]
	_drive(Vector2(0, WALK_SPEED), 0.0, 1.0)
	await _settle(30)

	var peaks := {}
	for direction in ["front", "back", "left", "right"]:
		_tree.set("parameters/HitPose/transition_request", direction)
		_tree.set("parameters/Hit/request", AnimationNodeOneShot.ONE_SHOT_REQUEST_FIRE)
		await _settle(4)
		_check("il flinch '%s' parte" % direction, _tree.get("parameters/Hit/active"))

		# Il picco della clip sta a 0,08 s: si campiona li', non a caso.
		await _settle_seconds(0.08)
		peaks[direction] = _pose_snapshot(torso)

		# 0,40 s di clip + 0,10 di dissolvenza: dopo un secondo deve essere finita, o il
		# personaggio resterebbe piegato per sempre.
		await _settle_seconds(1.0)
		_check("il flinch '%s' rientra" % direction, not _tree.get("parameters/Hit/active"),
			"ancora attivo dopo 1 s")

	# Le quattro direzioni devono essere distinte a coppie opposte: fronte/retro si
	# piegano su assi opposti, e cosi' sinistra/destra.
	var front_back: float = _max_pose_delta(peaks["front"], peaks["back"])
	var left_right: float = _max_pose_delta(peaks["left"], peaks["right"])
	_check("il colpo frontale differisce da quello alle spalle", front_back > 0.10,
		"scarto = %.4f rad" % front_back)
	_check("il colpo da sinistra differisce da quello da destra", left_right > 0.10,
		"scarto = %.4f rad" % left_right)

	# E le gambe devono aver continuato a camminare per tutto il tempo.
	var m: float = await _legs_motion(6, 30)
	_check("sotto i colpi le gambe continuano a camminare", m > MOTION_EPSILON,
		"finestra piu' quieta = %.5f rad" % m)


# Parkour: le due clip esistono, sono full body, partono e rientrano.
#
# La traiettoria della radice NON si verifica qui — la fa il motion warping di
# CharacterMotor, che e' codice C# senza scheletro di mezzo. Qui si verifica la META'
# animata: che il one-shot sia cablato, che copra il corpo intero (a differenza dei
# layer additivi) e che non resti appeso, perche' una manovra che non rientra
# congelerebbe il personaggio in quella posa per il resto della partita.
#
# Le due clip condividono un solo one-shot e si scelgono col Transition VaultPose:
# per questo si verificano entrambe con lo stesso corpo di controlli, cambiando solo
# l'ingresso richiesto.
func _verify_vault_clip() -> void:
	print("")
	print("== parkour ==")

	# Durate dichiarate da CharacterMotor: VaultDuration e MantleDuration. Se divergono
	# dalle clip, il warping distribuisce la traiettoria su un tempo sbagliato e le pose
	# arrivano prima o dopo i punti di contatto.
	await _verify_parkour_clip("vault_low", 0.9, "scavalcamento")
	await _verify_parkour_clip("mantle_high", 1.4, "arrampicata")


func _verify_parkour_clip(clip: String, expected: float, label: String) -> void:
	_check("la clip %s e' in libreria" % clip, _tree.has_animation(clip))
	if not _tree.has_animation(clip):
		return

	var length: float = _tree.get_animation(clip).length
	_check("%s dura quanto dichiarato dal motore (%.1f s)" % [clip, expected],
		absf(length - expected) < 0.15, "%.3f s" % length)

	_drive(Vector2.ZERO, 0.0, 0.0)
	_tree.set("parameters/VaultPose/transition_request", clip)
	await _settle(30)
	var before := _pose_snapshot(["Hips", "LeftUpLeg", "RightUpLeg", "Spine2", "RightArm"])

	_tree.set("parameters/Vault/request", AnimationNodeOneShot.ONE_SHOT_REQUEST_FIRE)
	await _settle(4)
	_check("%s: parte" % label, _tree.get("parameters/Vault/active"))

	# A meta' clip il corpo INTERO deve essersi mosso: e' la differenza con i layer
	# additivi, che lasciano le gambe alla locomozione.
	await _settle_seconds(length * 0.4)
	var mid := _pose_snapshot(["Hips", "LeftUpLeg", "RightUpLeg", "Spine2", "RightArm"])
	var legs_moved: float = _max_pose_delta(
		{"LeftUpLeg": before["LeftUpLeg"], "RightUpLeg": before["RightUpLeg"]},
		{"LeftUpLeg": mid["LeftUpLeg"], "RightUpLeg": mid["RightUpLeg"]})
	var arms_moved: float = _max_pose_delta(
		{"RightArm": before["RightArm"]}, {"RightArm": mid["RightArm"]})
	_check("%s: muove le gambe (e' full body)" % label, legs_moved > 0.15,
		"scarto sulle gambe = %.4f rad" % legs_moved)
	_check("%s: protende le braccia" % label, arms_moved > 0.15,
		"scarto sul braccio destro = %.4f rad" % arms_moved)

	await _settle_seconds(length + 0.5)
	_check("%s: rientra" % label, not _tree.get("parameters/Vault/active"),
		"ancora attivo dopo la durata della clip piu' la dissolvenza")


# Isolamento dei layer additivi: il delta di impugnatura tocca il busto, non le gambe.
#
# E' l'invariante su cui poggia tutta l'architettura a layer. Se cadesse, servirebbe di
# nuovo un set di locomozione per arma (quattro clip per camminata e quattro per corsa,
# per OGNI famiglia d'arma) — cioe' l'esplosione combinatoria che i layer esistono per
# evitare. La maschera non e' un filtro dell'albero ma una proprieta' delle CLIP: quelle
# delta portano solo le track dell'upper body, quindi un bone senza track non riceve
# nulla. Un delta authorato per sbaglio con le gambe dentro passerebbe ogni altro
# controllo (non e' T-pose, le gambe si muovono) e si vedrebbe solo qui.
#
# Si misura a IDLE e non in camminata: da fermi la clip di base e' quasi statica, quindi
# la differenza fra le due misure e' attribuibile al layer e non alla fase del passo. Il
# rumore residuo della clip di idle viene misurato e usato come riferimento, invece di
# fidarsi di una soglia scelta a occhio.
func _verify_additive_isolation() -> void:
	print("")
	print("== isolamento dei layer additivi ==")

	var legs := ["LeftUpLeg", "LeftLeg", "RightUpLeg", "RightLeg", "LeftFoot", "RightFoot"]
	var torso := ["Spine1", "Spine2", "RightArm", "RightForeArm", "LeftArm", "LeftForeArm"]

	# L'effetto di un layer si misura sul suo SCATTO, non a regime.
	#
	# Confrontare due pose lontane nel tempo non funziona: la clip di base continua ad
	# avanzare, e il respiro dell'idle sul busto vale qualche grado — lo stesso ordine
	# del segnale. Peggio, sulle gambe la deriva DIPENDE DALLA FASE del ciclo, quindi un
	# "rumore" misurato su una finestra non vale per la finestra dopo (misurato: respiro
	# 0,034 rad in una finestra, deriva 0,064 in un'altra, senza che nulla cambiasse).
	#
	# Qui i pesi si scrivono diretti sull'albero, senza lo smorzamento di
	# CharacterAnimator: l'effetto del layer e' quindi IMMEDIATO, mentre la base in due
	# frame non fa in tempo a muoversi. Si confronta lo scatto col drift di due frame
	# misurato subito prima, nella stessa fase di clip.
	var settle := 2

	_drive(Vector2.ZERO, 0.0, 0.0)
	await _settle(40)
	var legs_before := _pose_snapshot(legs)
	var torso_before := _pose_snapshot(torso)
	await _settle(settle)
	var legs_drift: float = _max_pose_delta(legs_before, _pose_snapshot(legs))
	var torso_drift: float = _max_pose_delta(torso_before, _pose_snapshot(torso))

	# Scatto della sola maschera d'impugnatura.
	legs_before = _pose_snapshot(legs)
	torso_before = _pose_snapshot(torso)
	_drive(Vector2.ZERO, 0.0, 1.0)
	await _settle(settle)
	var legs_delta: float = _max_pose_delta(legs_before, _pose_snapshot(legs))
	var torso_delta: float = _max_pose_delta(torso_before, _pose_snapshot(torso))

	_check("la maschera d'impugnatura cambia il busto", torso_delta > torso_drift + 0.15,
		"busto %.4f rad, deriva della base %.4f" % [torso_delta, torso_drift])
	# Sulle gambe non deve succedere NULLA oltre la deriva. Qui la maschera e' doppia: il
	# filtro di HoldMask e l'assenza di track fuori dalle braccia nelle pose hold/*. Basta
	# che una delle due regga, ma se cadessero entrambe si vedrebbe solo qui.
	_check("la maschera d'impugnatura NON tocca le gambe", legs_delta < legs_drift + 0.01,
		"gambe %.4f rad, deriva della base %.4f" % [legs_delta, legs_drift])

	# Stessa cosa per l'aim offset, che e' il secondo Add2 della catena.
	legs_before = _pose_snapshot(legs)
	torso_before = _pose_snapshot(torso)
	_drive(Vector2.ZERO, 0.0, 1.0, Vector2(0, 1), 1.0)
	await _settle(settle)
	legs_delta = _max_pose_delta(legs_before, _pose_snapshot(legs))
	torso_delta = _max_pose_delta(torso_before, _pose_snapshot(torso))

	# Margine piu' stretto dell'impugnatura: l'aim offset a fondo corsa distribuisce
	# AIM_PITCH_DEG su cinque ossa, quindi il singolo osso si muove meno di quanto
	# faccia un braccio che cambia presa.
	_check("l'aim offset cambia il busto", torso_delta > torso_drift + 0.06,
		"busto %.4f rad, deriva della base %.4f" % [torso_delta, torso_drift])
	_check("l'aim offset NON tocca le gambe", legs_delta < legs_drift + 0.01,
		"gambe %.4f rad, deriva della base %.4f" % [legs_delta, legs_drift])

	# E la mira in ALTO deve dare una posa diversa da quella in BASSO: se i due estremi
	# coincidessero, il BlendSpace2D additivo starebbe restituendo sempre il centro.
	_drive(Vector2.ZERO, 0.0, 1.0, Vector2(0, 1), 1.0)
	await _settle(settle)
	var up := _pose_snapshot(torso)
	_drive(Vector2.ZERO, 0.0, 1.0, Vector2(0, -1), 1.0)
	await _settle(settle)
	var span: float = _max_pose_delta(up, _pose_snapshot(torso))
	_check("gli estremi dell'aim offset sono distinti", span > 0.25,
		"scarto fra mira alta e bassa = %.4f rad" % span)

	_drive(Vector2.ZERO, 0.0, 0.0)
	await _settle(20)


func _max_pose_delta(a: Dictionary, b: Dictionary) -> float:
	var worst := 0.0
	for bone_name in a:
		worst = maxf(worst, (a[bone_name] as Quaternion).angle_to(b[bone_name]))
	return worst


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

	# Due suite sulla STESSA locomozione: disarmata e con l'impugnatura additiva sopra.
	# La seconda non e' ridondante — verifica che il layer additivo non ribalti le gambe,
	# che e' il modo in cui uno strafe specchiato potrebbe rientrare dalla finestra.
	# Si campionano le sole ossa delle GAMBE piu' il bacino: sono quelle che il delta
	# upper-body non tocca, quindi il confronto con la clip di locomozione resta valido
	# anche a impugnatura accesa.
	var suites := [
		["disarmato", 0.0],
		["armato", 1.0],
	]
	for suite in suites:
		_drive(Vector2(WALK_SPEED, 0), 0.0, suite[1])
		await _settle(60)
		var pose := _pose_snapshot(["Hips", "LeftUpLeg", "RightUpLeg"])
		var to_right: float = _min_clip_distance("walk_right", pose)
		var to_left: float = _min_clip_distance("walk_left", pose)
		_check("%s: blend a +X riproduce la clip DESTRA" % suite[0], to_right < to_left,
			"distanza da walk_right=%.3f, da walk_left=%.3f rad" % [to_right, to_left])


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


# Clip DELTA additive: la maschera sta nella CLIP, non nel filtro dell'albero.
#
# E' il controllo strutturale che rende vero per costruzione l'isolamento misurato in
# _verify_additive_isolation: una clip delta che non ha track sulle gambe non puo'
# toccarle, qualunque cosa faccia l'albero. Il rischio concreto e' un builder Blender
# che keyframa l'intera armatura per distrazione (bake_clip lo farebbe volentieri):
# passerebbe ogni controllo di posa e ricomparirebbe come locomozione sovrascritta
# dall'arma, cioe' il difetto da cui e' nata l'architettura a layer.
#
# Si verifica anche che il centro dell'aim offset e le ossa che un delta non deve toccare
# portino la chiave NEUTRA — che NON e' l'identita' (vedi _delta_from_rest).
func _verify_delta_clips() -> void:
	var lower_body := ["Hips", "LeftUpLeg", "LeftLeg", "LeftFoot", "LeftToeBase",
		"RightUpLeg", "RightLeg", "RightFoot", "RightToeBase"]

	var deltas := [
		"add/aim_center", "add/aim_up", "add/aim_down", "add/aim_left", "add/aim_right",
		"add/rifle_fire", "add/pistol_fire",
		"add/hit_front", "add/hit_back", "add/hit_left", "add/hit_right",
	]

	var leaking: PackedStringArray = []
	for clip in deltas:
		if not _tree.has_animation(clip):
			_check("'%s' presente in libreria" % clip, false)
			continue
		var anim: Animation = _tree.get_animation(clip)
		for bone_name in lower_body:
			if _find_rotation_track(anim, bone_name) >= 0:
				leaking.append("%s tocca %s" % [clip, bone_name])
				break

	_check("le clip delta non hanno track sulla parte bassa del corpo", leaking.is_empty(),
		"; ".join(leaking))

	# Il centro dell'aim offset e' per definizione il delta NULLO, su OGNI osso che tocca.
	#
	# E' il controllo che avrebbe intercettato il bug delle chiavi identita'. Dalla
	# semantica additiva `risultato = Base x (Rest^-1 x Chiave)` discende che il contributo
	# nullo si ha per Chiave = REST, non per Chiave = identita'. Guardare solo Spine2 non
	# bastava: li' il rest vale 4 gradi e passava sotto la soglia, mentre sulle CLAVICOLE
	# vale 115 gradi — accendere la mira scambiava le braccia di lato.
	if _tree.has_animation("add/aim_center"):
		var center: Animation = _tree.get_animation("add/aim_center")
		var worst := 0.0
		var worst_bone := ""
		for i in center.get_track_count():
			var bone_name := String(center.track_get_path(i)).split(":")[-1]
			var off := _delta_from_rest(center.rotation_track_interpolate(i, 0.0), bone_name)
			if off > worst:
				worst = off
				worst_bone = bone_name
		_check("il centro dell'aim offset e' il delta nullo su ogni osso", worst < 0.02,
			"peggiore: %s a %.4f rad dal rest" % [worst_bone, worst])

	# Gli estremi devono muovere il RACHIDE e lasciare fermo il resto: l'aim offset ruota
	# il busto, le braccia lo seguono perche' gli sono figlie. Un estremo che tocca le
	# clavicole storce le spalle a ogni movimento della mira.
	for clip in ["add/aim_up", "add/aim_down", "add/aim_left", "add/aim_right"]:
		if not _tree.has_animation(clip):
			continue
		var anim: Animation = _tree.get_animation(clip)
		var track := _find_rotation_track(anim, "Spine2")
		if track < 0:
			_check("'%s' anima il rachide" % clip, false, "nessuna track su Spine2")
			continue
		var off := _delta_from_rest(anim.rotation_track_interpolate(track, 0.0), "Spine2")
		_check("'%s' e' un delta non nullo" % clip, off > 0.03,
			"scarto dal rest = %.4f rad" % off)

		var arm := _find_rotation_track(anim, "RightShoulder")
		if arm >= 0:
			var shoulder := _delta_from_rest(anim.rotation_track_interpolate(arm, 0.0),
				"RightShoulder")
			_check("'%s' non tocca le clavicole" % clip, shoulder < 0.02,
				"scarto dal rest = %.4f rad" % shoulder)


# Quanto un delta additivo sposta un osso: e' `Rest^-1 x Chiave`, non `Chiave` e basta.
func _delta_from_rest(key: Quaternion, bone_name: String) -> float:
	var idx := _skel.find_bone(bone_name)
	if idx < 0:
		return 0.0
	var rest: Quaternion = _skel.get_bone_rest(idx).basis.get_rotation_quaternion()
	return (rest.inverse() * key).angle_to(Quaternion.IDENTITY)


# Pose d'impugnatura (build_weapon_poses.gd): assolute, sole braccia, presa conservata.
#
# La misura che conta e' la DISTANZA fra le mani: reggendo un fucile deve restare quella
# dell'astina in ogni posa e sopra ogni locomozione, ed e' precisamente cio' che il delta
# additivo non riusciva a garantire (0,39 m sopra l'idle, 0,58 m sopra walk_fwd). Qui si
# misura sull'albero VERO, con la locomozione sotto.
func _verify_hold_mask() -> void:
	print("")
	print("== maschera d'impugnatura ==")

	var forbidden := ["Hips", "Spine", "Spine1", "Spine2", "Neck", "Head",
		"LeftUpLeg", "LeftLeg", "LeftFoot", "RightUpLeg", "RightLeg", "RightFoot"]

	var leaking: PackedStringArray = []
	for clip in ["hold/rifle_lowered", "hold/rifle_aim", "hold/pistol", "hold/pistol_aim"]:
		if not _tree.has_animation(clip):
			_check("'%s' presente in libreria" % clip, false)
			continue
		var anim: Animation = _tree.get_animation(clip)
		for bone_name in forbidden:
			if _find_rotation_track(anim, bone_name) >= 0:
				leaking.append("%s tocca %s" % [clip, bone_name])
				break
	_check("le pose d'impugnatura toccano solo le braccia", leaking.is_empty(),
		"; ".join(leaking))

	var left := _skel.find_bone("LeftHand")
	var right := _skel.find_bone("RightHand")

	# La presa del fucile su OGNI locomozione: e' il difetto che ha motivato il passaggio
	# dal delta additivo alla maschera, e senza questa misura tornerebbe muto.
	_tree.set("parameters/WeaponPose/transition_request", "rifle_aim")
	var casi := [
		["fermi", Vector2.ZERO, 0.0],
		["in camminata", Vector2(0, WALK_SPEED), 0.0],
		["in strafe", Vector2(WALK_SPEED, 0), 0.0],
		["in corsa", Vector2(0, RUN_SPEED), 0.0],
		["accovacciati", Vector2(0, CROUCH_SPEED), 1.0],
	]
	for caso in casi:
		_drive(caso[1], caso[2], 1.0)
		await _settle(20)
		var gap: float = (_skel.get_bone_global_pose(left).origin
			- _skel.get_bone_global_pose(right).origin).length()
		_check("la presa del fucile regge %s" % caso[0], absf(gap - HANDGUARD) < 0.03,
			"distanza fra le mani = %.3f m (astina a %.3f)" % [gap, HANDGUARD])

	# E con l'aim offset a fondo corsa: la mira ruota il busto, non deve aprire la presa.
	_drive(Vector2.ZERO, 0.0, 1.0, Vector2(1, 1), 1.0)
	await _settle(20)
	var gap_aim: float = (_skel.get_bone_global_pose(left).origin
		- _skel.get_bone_global_pose(right).origin).length()
	_check("la presa regge in mira a fondo corsa", absf(gap_aim - HANDGUARD) < 0.03,
		"distanza fra le mani = %.3f m" % gap_aim)

	# Porto rilassato: le mani stanno piu' in basso della mira, ma DAVANTI al corpo e non
	# lungo i fianchi (era il difetto della vecchia posa a 35 gradi: mani a z = +0,04 m
	# dal bacino, cioe' dentro le cosce).
	var hips := _skel.find_bone("Hips")
	_tree.set("parameters/WeaponPose/transition_request", "rifle_lowered")
	_drive(Vector2.ZERO, 0.0, 1.0)
	await _settle(30)
	var origin := _skel.get_bone_global_pose(hips).origin
	var lowered := _skel.get_bone_global_pose(right).origin - origin
	_tree.set("parameters/WeaponPose/transition_request", "rifle_aim")
	_drive(Vector2.ZERO, 0.0, 1.0)
	await _settle(30)
	var aimed := _skel.get_bone_global_pose(right).origin - origin

	_check("il porto abbassa la mano rispetto alla mira", aimed.y > lowered.y + 0.04,
		"mano destra: mira %.3f m, porto %.3f m" % [aimed.y, lowered.y])
	_check("il porto tiene l'arma davanti al corpo", lowered.z > 0.12,
		"mano destra avanti di %.3f m dal bacino" % lowered.z)

	_drive(Vector2.ZERO, 0.0, 0.0)
	await _settle(20)


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

	# rifle_aim_idle / rifle_lowered_idle / pistol_aim_idle NON sono piu' in elenco: le
	# pose d'impugnatura vengono ora da hold/*, derivate in Godot dalle sorgenti Mixamo
	# (build_weapon_poses.gd). Restano nel .glb come le otto clip di locomozione armata,
	# non referenziate e senza costo. A verificarle c'e' _verify_hold_mask.
	for clip in ["pistol_fire", "land_soft", "vault_low"]:
		var ok: bool = player.has_animation(clip)
		_check("'%s' presente in libreria" % clip, ok)
		if not ok:
			continue
		player.play(clip)
		await _settle(8)
		var d := _rest_distance()
		_check("'%s' non e' in T-pose" % clip, d > TPOSE_EPSILON,
			"scarto dalla rest pose = %.4f rad" % d)

	_verify_delta_clips()

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

	var aim_rig: Node3D = rig.get_node_or_null("AimRig") as Node3D
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

	# --- LA CANNA DEVE PUNTARE DOVE SI MIRA ---------------------------------
	#
	# E' il controllo che mancava, ed e' diverso da tutti quelli sopra: quelli misurano
	# DOVE finisce l'arma (segue la mano, sta fra le mani, si sposta con la mira), questo
	# misura DOVE PUNTA. Con le pose d'impugnatura derivate da `rifle_idle` — un porto con
	# l'arma di traverso sul petto — la canna stava a 85 gradi dalla mira e ognuno degli
	# altri controlli passava lo stesso.
	#
	# Va provato su piu' locomozioni perche' la posa d'impugnatura sostituisce le sole
	# braccia: il busto sotto e' quello della clip in corso, e sono le sue rotazioni a
	# poter portare la canna altrove.
	_tree.set("parameters/WeaponPose/transition_request", "rifle_aim")
	var mire := [
		["dritto davanti", Vector3(0, 0, 1), Vector2.ZERO],
		["in alto", Vector3(0, 0.55, 1).normalized(), Vector2.ZERO],
		["in basso", Vector3(0, -0.4, 1).normalized(), Vector2.ZERO],
		["camminando", Vector3(0, 0, 1), Vector2(0, WALK_SPEED)],
		["in strafe", Vector3(0, 0, 1), Vector2(-WALK_SPEED, 0)],
		["in corsa", Vector3(0, 0, 1), Vector2(0, RUN_SPEED)],
	]
	aim_rig.set("Weight", 1.0)
	for caso in mire:
		_drive(caso[2], 0.0, 1.0, Vector2.ZERO, 1.0)
		aim_rig.set("AimDirection", caso[1])
		await _settle(60)
		var muzzle_error: float = rad_to_deg(
			grip.global_transform.basis.z.normalized().angle_to((caso[1] as Vector3).normalized()))
		_check("col fucile la canna punta sulla mira, %s" % caso[0],
			muzzle_error < AIM_TOLERANCE_DEG, "scarto = %.2f gradi" % muzzle_error)

	# Spenta la mira, il busto deve tornare alla posa animata: un layer procedurale che non
	# si spegne e' peggio di uno che non c'e'.
	_drive(Vector2(-WALK_SPEED, 0), 0.0, 1.0)
	aim_rig.set("AimDirection", Vector3(0, 0, 1))
	aim_rig.set("Weight", 0.0)
	await _settle(60)
	_check("a peso zero la correzione rientra", _aim_error(Vector3(0, 0, 1)) > 0.5,
		"il busto e' rimasto agganciato alla mira anche da disarmato")


# Da che parte sporge il gomito sinistro rispetto all'asse spalla -> mano.
#
# E' la componente di (gomito - spalla) PERPENDICOLARE a quell'asse: la sola parte che
# dice da che lato si piega il braccio. La lunghezza non interessa, solo la direzione,
# quindi si normalizza. Tutte le pose vengono da dopo i modificatori (§1.1).
func _elbow_side() -> Vector3:
	var root := _bone_after_modifiers("LeftArm")
	var tip := _bone_after_modifiers("LeftHand")
	var elbow := _bone_after_modifiers("LeftForeArm")
	var axis: Vector3 = (tip - root).normalized()
	var arm: Vector3 = elbow - root
	return (arm - axis * arm.dot(axis)).normalized()


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

		# L'IK di supporto deve girare per ULTIMO fra i modificatori: chiude un vincolo
		# sull'arma, e qualunque cosa muova il busto dopo di lui glielo porta via. Con
		# SupportHandIk davanti a SpineAim la mano restava 36 cm fuori dall'astina, e
		# soltanto in mira (fuori mira SpineAim ha influenza nulla).
		var modificatori: PackedStringArray = []
		for c in _skel.get_children():
			if c is SkeletonModifier3D:
				modificatori.append(c.name)
		_check("l'IK di supporto e' l'ultimo modificatore",
			not modificatori.is_empty() and modificatori[-1] == "SupportHandIk",
			"ordine di esecuzione: %s" % ", ".join(modificatori))

	_watch_bone("LeftHand")
	_watch_bone("RightHand")
	_watch_bone("LeftArm")
	_watch_bone("LeftForeArm")
	await _settle(60)
	var reach_error: float = _bone_after_modifiers("LeftHand").distance_to(support.global_position)
	_check("la mano di supporto raggiunge l'astina", reach_error < 0.08,
		"errore residuo = %.3f m" % reach_error)

	# Da che PARTE si piega il gomito.
	#
	# Raggiungere l'astina non basta: il gomito puo' arrivarci piegato al contrario, e la
	# distanza resta identica — e' il difetto che si e' visto solo a schermo. Lo decide il
	# pole_node, che vive nel frame dell'ARMA: cambiando GripRotationDegrees si sposta con
	# lei, quindi ogni presa nuova va accompagnata da un SupportElbowHint nuovo
	# (tools/build_weapon_poses.gd li stampa insieme).
	#
	# Il riferimento e' la posa ANIMATA, cioe' l'IK spento: la posa d'impugnatura ha gia'
	# il gomito dove va, ed e' quello il lato giusto per definizione.
	var elbow_ik := _elbow_side()
	grip_rig.set("EnableSupportHandIk", false)
	await _settle(60)
	var elbow_pose := _elbow_side()
	grip_rig.set("EnableSupportHandIk", true)
	await _settle(60)

	var flip: float = rad_to_deg(elbow_ik.angle_to(elbow_pose))
	_check("il gomito di supporto si piega dal lato della posa", flip < 90.0,
		"scarto fra IK e posa animata = %.0f gradi (oltre 90 = gomito rovesciato)" % flip)

	# --- La mano di supporto NON deve staccarsi, in nessuno stato ------------
	#
	# L'IK di supporto e' acceso sempre, non solo in mira: la mano sinistra e' SULL'ARMA,
	# e un vincolo che vale solo mirando lascia la mano a fluttuare accanto all'astina in
	# tutto il resto del gioco. Perche' regga anche nel porto rilassato serve che il polo
	# del gomito ruoti con l'arma — cosa che fa, essendo figlio di GripPoint — e che la
	# posa di porto sia derivata da quella di mira ruotando le braccia in blocco, cosi'
	# l'arma resta fra le mani anche li' (§1.6quater).
	# La mira PROCEDURALE va accesa: e' il caso in cui il difetto si vede, e misurarlo con
	# SpineAimModifier spento vuol dire non misurarlo affatto. Il modificatore ruota il
	# rachide ogni frame, quindi l'arma — appesa alla mano destra — si sposta di continuo,
	# ed e' esattamente in quelle condizioni che la mano sinistra puo' restare indietro.
	var aim_rig: Node3D = rig.get_node_or_null("AimRig") as Node3D
	var stati := [
		["in mira, fermi", "rifle_aim", Vector2.ZERO, 1.0],
		["in mira, in camminata", "rifle_aim", Vector2(0, WALK_SPEED), 1.0],
		["in mira, in corsa", "rifle_aim", Vector2(0, RUN_SPEED), 1.0],
		["in mira, in strafe", "rifle_aim", Vector2(-WALK_SPEED, 0), 1.0],
		["nel porto, fermi", "rifle_lowered", Vector2.ZERO, 0.0],
		["nel porto, in camminata", "rifle_lowered", Vector2(0, WALK_SPEED), 0.0],
		["nel porto, in corsa", "rifle_lowered", Vector2(0, RUN_SPEED), 0.0],
	]
	for stato in stati:
		_tree.set("parameters/WeaponPose/transition_request", stato[1])
		_drive(stato[2], 0.0, 1.0, Vector2.ZERO, stato[3])
		if aim_rig != null:
			aim_rig.set("Weight", stato[3])
			aim_rig.set("AimDirection", Vector3(0.4, 0.25, 1).normalized())
		await _settle(40)
		var slip: float = _bone_after_modifiers("LeftHand").distance_to(support.global_position)
		var side: float = rad_to_deg(_elbow_side().angle_to(elbow_pose))
		_check("la mano di supporto resta sull'astina %s" % stato[0], slip < 0.02,
			"scostamento = %.3f m, gomito a %.0f gradi dal lato della posa" % [slip, side])
	_tree.set("parameters/WeaponPose/transition_request", "rifle_aim")

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
# Accovacciati: braccia vive da disarmati, arma fuori dalle gambe da armati.
#
# Sono i DUE difetti che si vedevano solo accovacciati, e nessun controllo esistente poteva
# prenderli: tutti misuravano le gambe ("ti stai ancora muovendo?" e' vero anche con le
# braccia morte) oppure la presa in mira, dove la correzione procedurale del busto e' gia'
# accesa e nasconde il problema.
#
#   1. le cinque clip crouch_* di Mixamo hanno le braccia FERME — una sola chiave di
#      rotazione per osso — quindi da disarmati le mani restavano immobili lungo il corpo
#      mentre le gambe camminavano. Le rigenera tools/build_crouch_clips.gd;
#   2. il busto di quelle clip e' piegato in avanti di 35-59 gradi, e la posa
#      d'impugnatura e' una posa delle sole BRACCIA: figlie di Spine2, si portano dietro la
#      piega e finiscono l'arma fra le gambe. Misurato prima della correzione: 0,064 m fra
#      l'asse dell'arma e le gambe camminando accovacciati, contro 0,358 in piedi.
func _verify_crouch(rig: Node) -> void:
	print("")
	print("== accovacciati ==")

	# --- 1. da disarmati le braccia si muovono ------------------------------
	for caso in [["fermi", Vector2.ZERO], ["in movimento", Vector2(0, CROUCH_SPEED)]]:
		_drive(caso[1], 1.0, 0.0)
		await _settle(30)
		var m := await _arms_motion(6, 20)
		_check("accovacciati %s le braccia si muovono" % caso[0], m > MOTION_EPSILON,
			"finestra piu' quieta = %.5f rad" % m)

	# --- 2. da armati l'arma resta fuori dalle gambe ------------------------
	var grip_rig: Node3D = rig.get_node_or_null("GripRig")
	var grip: Node3D = _skel.get_node_or_null("RightHandAttachment/GripPoint")
	_check("GripRig e punto di presa presenti", grip_rig != null and grip != null)
	if grip_rig == null or grip == null:
		return

	for bone in ["LeftUpLeg", "LeftLeg", "LeftFoot", "RightUpLeg", "RightLeg", "RightFoot"]:
		_watch_bone(bone)

	grip_rig.call("ApplyWeapon", load("res://animation/resources/two_handed.tres"))
	_tree.set("parameters/WeaponPose/transition_request", "rifle_lowered")

	# Si replica cio' che scrive CharacterAnimator da accovacciati FUORI mira: CrouchLift,
	# cioe' braccia e canna alzate. Qui lo script del rig e' tolto, quindi va scritto a mano.
	# Il BUSTO non si tocca: raddrizzarlo toglieva l'arma dalle gambe ma a schermo il
	# personaggio non sembrava piu' accovacciato.
	var grezzo := 0.0
	for caso in [["fermi", Vector2.ZERO], ["in movimento", Vector2(0, CROUCH_SPEED)]]:
		for correzione in [false, true]:
			grip_rig.set("CrouchLift", 1.0 if correzione else 0.0)
			_drive(caso[1], 1.0, 1.0)
			await _settle(60)

			if not correzione:
				grezzo = _weapon_leg_clearance(grip)
				continue

			var clearance := _weapon_leg_clearance(grip)
			_check("accovacciati %s l'arma resta fuori dalle gambe" % caso[0],
				clearance >= CROUCH_WEAPON_CLEARANCE,
				"distanza arma-gambe = %.3f m (senza correzione %.3f)" % [clearance, grezzo])
			# La correzione deve anche FARE qualcosa: un giorno che il porto rilassato
			# cambiasse e il difetto sparisse da solo, questo controllo si accorgerebbe
			# che sta misurando un rimedio ormai inutile.
			_check("accovacciati %s il sollevamento ha effetto" % caso[0],
				clearance > grezzo + 0.02,
				"da %.3f a %.3f m" % [grezzo, clearance])

	grip_rig.set("CrouchLift", 0.0)
	grip_rig.call("ApplyWeapon", null)
	_drive(Vector2.ZERO, 0.0, 0.0)
	await _settle(30)


# Movimento delle BRACCIA: il minimo, fra piu' finestre, di quanto si allontanano dalla
# posa con cui la finestra e' cominciata.
#
# Si misura lo SPOSTAMENTO nella finestra e non lo scarto fra due frame consecutivi come
# per le gambe, ed e' la differenza fra misurare un ciclo di passo e misurare un respiro:
# il braccio di un idle percorre pochi gradi in due secondi, quindi da un frame all'altro
# si muove meno del rumore anche quando a schermo si vede benissimo.
func _arms_motion(windows: int, window_frames: int) -> float:
	var quietest := INF
	for w in windows:
		var start := _arms_snapshot()
		var moved := 0.0
		for i in window_frames:
			await process_frame
			var now := _arms_snapshot()
			for j in ARMS.size():
				moved = maxf(moved, (start[j] as Quaternion).angle_to(now[j]))
		quietest = minf(quietest, moved)
	return quietest


func _arms_snapshot() -> Array:
	var out := []
	for bone in ARMS:
		out.append(_skel.get_bone_pose_rotation(_skel.find_bone(bone)))
	return out


# Distanza minima fra l'ARMA e le gambe, in metri.
#
# L'arma si rappresenta con il segmento calcio -> volata attorno al punto di presa, che e'
# come e' costruito il placeholder di WeaponVisual, e le gambe con le due catene
# coscia -> ginocchio -> piede. Le pose delle gambe vengono da DOPO i modificatori (§1.1),
# quelle dell'arma dal BoneAttachment3D, che i modificatori li segue gia'.
func _weapon_leg_clearance(grip: Node3D) -> float:
	var barrel: Vector3 = grip.global_transform.basis.z.normalized()
	var stock: Vector3 = grip.global_position - barrel * 0.2
	var muzzle: Vector3 = grip.global_position + barrel * 0.75

	var closest := INF
	for catena in [["LeftUpLeg", "LeftLeg", "LeftFoot"], ["RightUpLeg", "RightLeg", "RightFoot"]]:
		for i in 2:
			closest = minf(closest, _segment_distance(stock, muzzle,
				_bone_after_modifiers(catena[i]), _bone_after_modifiers(catena[i + 1])))
	return closest


# Distanza fra due segmenti, campionata. Basta e avanza: qui interessa sapere se due
# volumi si compenetrano, non il punto esatto in cui lo fanno.
func _segment_distance(a: Vector3, b: Vector3, c: Vector3, d: Vector3) -> float:
	var closest := INF
	for i in 21:
		var p: Vector3 = a.lerp(b, i / 20.0)
		for j in 21:
			closest = minf(closest, p.distance_to(c.lerp(d, j / 20.0)))
	return closest


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
	_drive(Vector2.ZERO, 0.0, 1.0)
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


# Camminata su una RAMPA: si resta a terra?
#
# Il difetto che questo controllo blocca non e' fisico ma visivo, ed era muto: salendo una
# rampa il personaggio riproduceva la posa di CADUTA in continuazione. La causa era che
# CharacterMotor proiettava a mano la velocita' sul piano del pavimento e ne scriveva la
# componente VERTICALE in Velocity.Y; con Velocity.Y positivo Godot salta lo snap al
# pavimento (lo applica solo quando la velocita' non punta in alto), quindi il corpo si
# staccava di qualche millimetro a ogni tick e IsOnFloor() lampeggiava.
#
# Si misura sul motore VERO (un NpcController, che eredita da CharacterMotor senza
# riscrivere una riga di movimento) e sulla pendenza vera del livello di prova: 20 gradi.
# La sonda guarda SyncGrounded, cioe' esattamente il dato che alimenta il layer di caduta.
func _verify_slope() -> void:
	print("")
	print("== camminata in pendenza ==")

	# Mondo di prova isolato, lontano dagli altri oggetti della suite. Le quote sono quelle
	# della rampa di TestLevel: 20 gradi fra il pavimento (y = 0) e il molo (y = 1.25).
	var world := Node3D.new()
	root.add_child(world)
	_static_box(world, Vector3(40, 1, 10), Vector3(0, -0.5, -100), 0.0)
	_static_box(world, Vector3(4.4, 0.4, 4), Vector3(16.385, 0.437, -100), 20.0)
	_static_box(world, Vector3(8, 1, 6), Vector3(22.5, 0.75, -100), 0.0)

	var scene := load("res://ai/scenes/NpcCharacter.tscn") as PackedScene
	if scene == null:
		world.queue_free()
		return

	var npc: Node3D = scene.instantiate()
	# I waypoint sono LOCALI allo spawn, e lo spawn lo legge NpcController._Ready: la
	# posizione va scritta PRIMA di entrare nell'albero, o l'origine dei waypoint resta
	# quella della scena (l'origine del mondo) e l'NPC cammina da tutt'altra parte.
	# La quota del secondo punto e' quella del molo: l'arrivo si misura in 3D.
	npc.position = Vector3(13, 1.05, -100)
	npc.set("Waypoints", PackedVector3Array([Vector3(9, 1.25, 0), Vector3(0, 0, 0)]))
	world.add_child(npc)

	# Niente navmesh in questo mondo di prova: l'NPC usa il ripiego "punta dritto al
	# waypoint" di NpcController.
	# Un po' di frame per assestarsi sul pavimento prima di cominciare a contare.
	for i in 40:
		await physics_frame

	var airborne := 0
	var samples := 420
	var peak := npc.global_position.y
	for i in samples:
		await physics_frame
		peak = maxf(peak, npc.global_position.y)
		if not npc.get("SyncGrounded"):
			airborne += 1

	_check("la rampa viene salita davvero", peak > 1.6,
		"quota massima raggiunta = %.2f m (molo a 1,25 piu' mezza capsula)" % peak)
	_check("in pendenza non si stacca mai da terra", airborne == 0,
		"%d frame su %d con SyncGrounded falso" % [airborne, samples])

	world.queue_free()
	await _settle(4)


# Geometria del parkour: la META' che le clip non coprono.
#
# Qui non si guarda nessuno scheletro: si guarda dove FINISCE il personaggio. E' l'unico
# modo di verificare la sonda (ObstacleProbe), che e' tutta misura e nessuna posa —
# altezza, spessore, spazio d'atterraggio — e i cui difetti si vedono solo come un corpo
# che finisce dentro un muro o che non parte affatto.
#
# Quattro casi, che sono le quattro decisioni della sonda: muretto -> si scavalca e si
# finisce OLTRE; muro medio -> ci si arrampica e si finisce SOPRA; muro troppo alto ->
# non si fa niente; muretto con un muro subito dietro -> non si fa niente, perche' non
# c'e' dove atterrare (senza questo controllo ci si incastrerebbe nella geometria).
func _verify_parkour_geometry() -> void:
	print("")
	print("== geometria del parkour ==")

	# Un solo pavimento sotto TUTTI i casi: quattro pavimenti separati lascerebbero il
	# vuoto fra un ostacolo e l'altro, e un personaggio che cade non misura piu' niente.
	var world := Node3D.new()
	root.add_child(world)
	_static_box(world, Vector3(60, 1, 100), Vector3(0, -0.5, -175), 0.0)

	# Le partenze sono a 0,8 m dalla FACCIA del muro: dentro VaultReach (1,0 m).

	# Muretto 0,9 m: sta nella banda di scavalcamento.
	_static_box(world, Vector3(6, 0.9, 0.4), Vector3(0, 0.45, -200), 0.0)
	var vaulted := await _parkour_attempt(world, Vector3(0, 1.05, -201.0), Vector3(0, 0, 1))
	_check("il muretto si scavalca", vaulted.z > -199.5,
		"z finale = %.2f (partenza -201,0, muro fra -200,2 e -199,8)" % vaulted.z)
	_check("dopo lo scavalcamento si e' a terra", absf(vaulted.y - 1.0) < 0.35,
		"y finale = %.2f" % vaulted.y)

	# Muro 2 m: fuori dalla banda di scavalcamento, dentro quella di arrampicata.
	_static_box(world, Vector3(6, 2.0, 1.2), Vector3(0, 1.0, -180), 0.0)
	var mantled := await _parkour_attempt(world, Vector3(0, 1.05, -181.4), Vector3(0, 0, 1))
	_check("il muro medio si arrampica", mantled.y > 2.2,
		"y finale = %.2f (sommita' a 2,00 piu' mezza capsula)" % mantled.y)
	_check("dopo l'arrampicata si e' SOPRA il muro, non oltre", mantled.z < -179.4,
		"z finale = %.2f (muro fra -180,6 e -179,4)" % mantled.z)

	# Muro 4 m: oltre MantleMaxHeight, non si aggancia nulla.
	_static_box(world, Vector3(6, 4.0, 1.0), Vector3(0, 2.0, -160), 0.0)
	var blocked := await _parkour_attempt(world, Vector3(0, 1.05, -161.3), Vector3(0, 0, 1))
	_check("il muro troppo alto resta invalicabile", blocked.y < 1.5 and blocked.z < -161.0,
		"posizione finale = (y %.2f, z %.2f)" % [blocked.y, blocked.z])

	# Muretto scavalcabile con un muro subito dietro: non c'e' dove atterrare.
	_static_box(world, Vector3(6, 0.9, 0.4), Vector3(0, 0.45, -140), 0.0)
	_static_box(world, Vector3(6, 3.0, 0.4), Vector3(0, 1.5, -139.4), 0.0)
	var trapped := await _parkour_attempt(world, Vector3(0, 1.05, -141.0), Vector3(0, 0, 1))
	_check("senza spazio per atterrare non si scavalca", trapped.z < -140.2,
		"z finale = %.2f (muretto a -140, muro cieco a -139,4)" % trapped.z)

	world.queue_free()
	await _settle(4)


# Mette un NPC davanti all'ostacolo, gli chiede la manovra e restituisce dove finisce.
#
# Si usa l'NPC e non il giocatore perche' e' lo stesso CharacterMotor senza input, camera
# ne' rete di mezzo: se il parkour funziona di qui, funziona per costruzione anche per il
# giocatore — che e' l'invariante di ai-npc §1.
func _parkour_attempt(world: Node3D, at: Vector3, direction: Vector3) -> Vector3:
	var scene := load("res://ai/scenes/NpcCharacter.tscn") as PackedScene
	if scene == null:
		return Vector3.ZERO

	var npc: Node3D = scene.instantiate()
	npc.position = at
	# Nessun waypoint: l'NPC resta fermo e non trascina la manovra con la propria
	# navigazione. Qui si misura la traiettoria scriptata, non il cammino.
	npc.set("Waypoints", PackedVector3Array())
	world.add_child(npc)

	for i in 30:
		await physics_frame

	npc.call("TryStartParkour", direction)

	# Durata della clip piu' un margine per l'atterraggio e l'assestamento.
	for i in 140:
		await physics_frame

	var final_position: Vector3 = npc.global_position
	npc.queue_free()
	await _settle(2)
	return final_position


func _static_box(parent: Node3D, size: Vector3, at: Vector3, roll_degrees: float) -> void:
	var body := StaticBody3D.new()
	body.collision_layer = 1
	body.collision_mask = 1
	var shape := CollisionShape3D.new()
	var box := BoxShape3D.new()
	box.size = size
	shape.shape = box
	body.add_child(shape)
	parent.add_child(body)
	body.global_transform = Transform3D(
		Basis(Vector3(0, 0, 1), deg_to_rad(roll_degrees)), at)
