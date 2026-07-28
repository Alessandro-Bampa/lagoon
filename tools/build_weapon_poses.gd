# Genera le POSE D'IMPUGNATURA (assolute, sole braccia) come AnimationLibrary.
#
# Uso:
#   Godot --path . --headless --script tools/build_weapon_poses.gd
#
# ----------------------------------------------------------------------------------
# PERCHE' ASSOLUTE E NON DELTA ADDITIVI (misurato, non supposto)
# ----------------------------------------------------------------------------------
# La prima versione dell'impugnatura era un DELTA additivo costante, sommato sopra la
# locomozione. La semantica additiva di Godot e' `risultato = Base x (Rest^-1 x Chiave)`,
# quindi un delta costante applica al braccio una rotazione RELATIVA: riproduce la posa
# giusta solo quando la base coincide con la clip di riferimento (idle_neutral), e su
# qualunque altra base il braccio finisce altrove. Misurato sul rig, distanza fra le due
# mani (che reggendo un fucile DEVE valere 0,39 m, cioe' la lunghezza dell'astina):
#
#     idle_neutral + add/rifle_aim ....... 0,39 m   (corretto: base = riferimento)
#     walk_fwd     + add/rifle_aim ....... 0,58 m   (mano di supporto fuori dall'arma)
#     run_fwd      + add/pistol .......... 0,32 m
#     crouch_fwd   + add/rifle_aim ....... mani all'altezza del BACINO
#
# Non e' un delta "fatto male": e' cio' che un delta costante puo' fare. L'oscillazione
# delle braccia della camminata resta nella base e si somma alla presa, quindi le mani
# ballano attorno all'arma invece di stringerla.
#
# La presa e' un VINCOLO GEOMETRICO (due mani sulla stessa arma), non una sfumatura da
# sommare: si esprime con una posa ASSOLUTA delle braccia e una maschera. Le otto ossa
# qui sotto vengono sovrascritte dal `Blend2` filtrato `HoldMask` dell'albero; bacino,
# gambe e rachide restano alla locomozione, quindi il busto continua a respirare, a
# oscillare in corsa e ad accovacciarsi, e le braccia — figlie dello stesso Spine2 — lo
# seguono in blocco tenendo la presa. Cio' che RESTA additivo e' quello che additivo lo
# e' davvero: aim offset, rinculo, flinch (tools/build_additive_clips.gd).
#
# Le pose derivano dalle DUE clip Mixamo `rifle_idle` e `pistol_idle` — le stesse su cui
# sono misurati GripRotationDegrees, SupportGripOffset e il polo del gomito (skill
# character-animation §7), quindi quelle misure restano valide senza ritoccarle. Il porto
# rilassato si ottiene ruotando le braccia verso il basso di un angolo MISURATO sulla
# quota delle mani, non scelto a occhio.
#
# E' uno dei rari GDScript ammessi da CLAUDE.md §2 (tooling da editor).
extends SceneTree

const BODY_PATH := "res://assets/models/Body_Base.glb"
const LIBRARY_PATH := "res://assets/models/animations/CharacterAnimations.glb"
const OUT_PATH := "res://animation/resources/WeaponHoldPoses.tres"
const SKELETON := "Armature_Character/Skeleton3D"

# Le uniche ossa che la maschera d'impugnatura sovrascrive. Devono combaciare con
# HOLD_MASK di tools/build_animation_tree.gd: la clip porta queste track e il filtro
# dell'albero lascia passare queste ossa. Le clavicole ci stanno, altrimenti la spalla
# resta alla posa di corsa e il braccio si stacca dal busto.
const ARM_BONES := [
	"LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand",
	"RightShoulder", "RightArm", "RightForeArm", "RightHand",
]

# Porto rilassato ("low ready"): di quanto ruotano verso il basso braccio e avambraccio
# rispetto alla posa di mira. I valori sono TARATI sulla quota delle mani stampata in
# fondo: 35/15 (la vecchia posa authorata in Blender) portava le mani a y = +0,01 m dal
# bacino e z = +0,04 m, cioe' braccia lungo il corpo e mani dentro i fianchi — il difetto
# segnalato. L'arma deve restare DAVANTI, col muso in basso.
const RIFLE_LOWER_ARM_DEG := 16.0
const RIFLE_LOWER_FOREARM_DEG := 6.0

# La pistola parte da una posa gia' estesa in avanti: il porto la abbassa di piu',
# perche' con una sola mano il gomito scende lungo il fianco.
const PISTOL_LOWER_ARM_DEG := 34.0
const PISTOL_LOWER_FOREARM_DEG := 10.0

var _skel: Skeleton3D
var _library: AnimationLibrary
var _out := AnimationLibrary.new()


# ==========================================================================
#  Primitive sullo scheletro
# ==========================================================================

func _sample(clip: String, bone: String, time: float) -> Quaternion:
	var anim: Animation = _library.get_animation(clip)
	for i in anim.get_track_count():
		if anim.track_get_type(i) == Animation.TYPE_ROTATION_3D \
				and String(anim.track_get_path(i)).ends_with(":" + bone):
			return anim.rotation_track_interpolate(i, time)
	return _skel.get_bone_rest(_skel.find_bone(bone)).basis.get_rotation_quaternion()


# Mette lo scheletro nella posa di una clip assoluta.
func _apply_clip(clip: String, time: float) -> void:
	for b in _skel.get_bone_count():
		_skel.set_bone_pose_rotation(b, _sample(clip, _skel.get_bone_name(b), time))


# Ruota un osso attorno a un asse dichiarato nello spazio dello SCHELETRO (il
# personaggio guarda +Z, la sua sinistra e' +X, su e' +Y).
#
# La rotazione va portata nello spazio del PADRE, perche' e' li' che vive la posa
# locale dell'osso: nuova_locale = Padre^-1 x R x Padre x locale. I figli seguono da
# soli, essendo espressi rispetto a questo osso.
func _rotate_bone(bone: String, axis: Vector3, degrees: float) -> void:
	var idx := _skel.find_bone(bone)
	var parent := _skel.get_bone_parent(idx)
	var parent_basis := Basis.IDENTITY
	if parent >= 0:
		parent_basis = _skel.get_bone_global_pose(parent).basis.orthonormalized()
	var rot := Basis(axis.normalized(), deg_to_rad(degrees))
	var local := _skel.get_bone_pose_rotation(idx)
	_skel.set_bone_pose_rotation(idx,
		(parent_basis.inverse() * rot * parent_basis).get_rotation_quaternion() * local)


# Abbassa entrambe le braccia: rotazione attorno all'asse laterale (+X), che porta
# l'avanti (+Z) verso il basso (-Y). E' la stessa costruzione della vecchia posa
# authorata in Blender, con l'angolo tarato invece che scelto a occhio.
func _lower_arms(arm_deg: float, forearm_deg: float) -> void:
	for side in ["Left", "Right"]:
		_rotate_bone(side + "Arm", Vector3(1, 0, 0), arm_deg)
		_rotate_bone(side + "ForeArm", Vector3(1, 0, 0), forearm_deg)


# ==========================================================================
#  Costruzione
# ==========================================================================

# Congela la posa CORRENTE delle sole braccia in una clip costante.
#
# Due chiavi identiche agli estremi: la posa non cambia nel tempo e il loop non salta.
# Nessuna track fuori da ARM_BONES: cosi' anche se un giorno il filtro dell'albero
# venisse allargato per sbaglio, questa clip non avrebbe nulla da dare al resto del
# corpo (stessa filosofia della maschera "nella clip" delle clip delta).
func _freeze_arms(name: String) -> void:
	var anim := Animation.new()
	anim.length = 1.0
	anim.loop_mode = Animation.LOOP_LINEAR

	for bone in ARM_BONES:
		var t := anim.add_track(Animation.TYPE_ROTATION_3D)
		anim.track_set_path(t, "%s:%s" % [SKELETON, bone])
		var q := _skel.get_bone_pose_rotation(_skel.find_bone(bone))
		anim.rotation_track_insert_key(t, 0.0, q)
		anim.rotation_track_insert_key(t, 1.0, q)

	_out.add_animation(name, anim)
	print("  %-16s %s" % [name, _hand_probe()])


# Diagnostica: mani rispetto al bacino e distanza fra loro. La distanza e' la misura
# che conta — reggendo un fucile DEVE restare quella dell'astina, e ruotare entrambe le
# braccia attorno allo stesso asse la conserva per costruzione.
func _hand_probe() -> String:
	var hips := _skel.get_bone_global_pose(_skel.find_bone("Hips")).origin
	var rh := _skel.get_bone_global_pose(_skel.find_bone("RightHand")).origin - hips
	var lh := _skel.get_bone_global_pose(_skel.find_bone("LeftHand")).origin - hips
	return "manoDX(y%+.2f z%+.2f) manoSX(y%+.2f z%+.2f) distanza=%.3f m" \
		% [rh.y, rh.z, lh.y, lh.z, (lh - rh).length()]


func _initialize() -> void:
	var body: Node = (load(BODY_PATH) as PackedScene).instantiate()
	root.add_child(body)
	await process_frame
	_skel = body.get_node(SKELETON) as Skeleton3D

	_library = load(LIBRARY_PATH) as AnimationLibrary
	if _library == null:
		printerr("Libreria non caricabile: %s" % LIBRARY_PATH)
		quit(1)
		return

	print("Pose d'impugnatura da %s (%d clip sorgente)"
		% [LIBRARY_PATH, _library.get_animation_list().size()])

	# --- fucile ---------------------------------------------------------------
	# La posa di MIRA e' rifle_idle tale e quale: e' la ready-stance su cui sono
	# misurati presa, offset dell'astina e polo del gomito (skill §7). Toccarla qui
	# invaliderebbe quelle misure.
	_apply_clip("rifle_idle", 0.0)
	_freeze_arms("rifle_aim")
	_lower_arms(RIFLE_LOWER_ARM_DEG, RIFLE_LOWER_FOREARM_DEG)
	_freeze_arms("rifle_lowered")

	# --- pistola --------------------------------------------------------------
	_apply_clip("pistol_idle", 0.0)
	_freeze_arms("pistol_aim")
	_lower_arms(PISTOL_LOWER_ARM_DEG, PISTOL_LOWER_FOREARM_DEG)
	_freeze_arms("pistol")

	var err := ResourceSaver.save(_out, OUT_PATH)
	if err != OK:
		printerr("Salvataggio fallito: %d" % err)
		quit(1)
		return

	print("\nAnimationLibrary d'impugnatura salvata in %s (%d pose)"
		% [OUT_PATH, _out.get_animation_list().size()])
	quit(0)
