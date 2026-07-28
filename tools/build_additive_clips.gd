# Genera le clip DELTA ADDITIVE e le salva come AnimationLibrary.
#
# Uso:
#   Godot --path . --headless --script tools/build_additive_clips.gd
#
# ----------------------------------------------------------------------------------
# PERCHE' QUI E NON IN BLENDER (misurato, non supposto)
# ----------------------------------------------------------------------------------
# Il primo tentativo authorava i delta in Blender e li faceva viaggiare nel .glb della
# libreria. Non funziona, per DUE motivi indipendenti, entrambi muti:
#
#   1. L'esportatore glTF con export_bake_animation=True campiona TUTTE le ossa
#      dell'armatura a ogni frame, non solo quelle con una fcurve. Le ossa che il
#      delta non tocca — bacino e gambe — venivano esportate con la posa RESIDUA del
#      pose bone, diversa da una clip all'altra. Misurato: add_aim_up e add_aim_center,
#      che devono differire solo sul rachide, differivano di 0,23 e 0,33 rad sui due
#      femori. Un delta di mira che muove le gambe e' esattamente il difetto che
#      l'architettura a layer esiste per eliminare.
#
#   2. Il rest pose di CharacterAnimations.glb NON coincide con quello di
#      Body_Base.glb: la libreria viene esportata con un'azione assegnata, quindi il
#      TRS dei nodi-osso non e' la posa di riposo. Il delta additivo pero' Godot lo
#      calcola contro il rest dello SCHELETRO, che viene da Body_Base. Misurato: una
#      posa authorata come identita' esatta arrivava a 0,07 rad dall'identita'.
#
# Il delta e' pura aritmetica sul rest di destinazione: si calcola dove verra'
# consumato. Le pose ASSOLUTE, che richiedono giudizio artistico, restano authorate in
# Blender (build_procedural_clips.py) e da li' vengono lette.
#
# ----------------------------------------------------------------------------------
# LA SEMANTICA ADDITIVA DI GODOT 4.7 (misurata con una sonda headless)
# ----------------------------------------------------------------------------------
#   risultato = Base x (Rest^-1 x Chiave)
# cioe': riferimento = REST POSE, composizione POST-moltiplicata in spazio locale.
# Ne discende la formula di authoring usata qui sotto: per ottenere `Target` quando la
# base vale `Riferimento`, la chiave da scrivere e'
#   Chiave = Rest x Riferimento^-1 x Target
#
# E' uno dei rari GDScript ammessi da CLAUDE.md §2 (tooling da editor).
extends SceneTree

# Si parte dagli ASSET, non da CharacterRig.tscn: la scena del rig referenzia il file
# che questo tool produce, quindi caricarla creerebbe una dipendenza circolare che
# impedirebbe il primo bootstrap (e romperebbe ogni rigenerazione dopo una modifica
# incompatibile alla libreria).
const BODY_PATH := "res://assets/models/Body_Base.glb"
const LIBRARY_PATH := "res://assets/models/animations/CharacterAnimations.glb"
const OUT_PATH := "res://animation/resources/AdditiveClips.tres"
const SKELETON := "Armature_Character/Skeleton3D"

# Le ossa che i delta possono toccare. E' LA maschera del sistema: un bone che non
# compare qui non riceve alcuna track, quindi non puo' essere toccato da nessun layer
# additivo, qualunque cosa faccia l'albero. Le clavicole ci stanno: senza, la spalla
# resterebbe alla posa di corsa e il braccio si staccherebbe dal busto.
const UPPER_BODY := [
	"Spine", "Spine1", "Spine2", "Neck", "Head",
	"LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand",
	"RightShoulder", "RightArm", "RightForeArm", "RightHand",
]

# Rachide e testa, con il peso di ciascuno nella distribuzione di una rotazione.
# Stessa filosofia di SpineAimModifier, che poi chiude a runtime l'errore residuo
# sulla mira vera: qui si mette il grosso della posa, non la precisione.
const AIM_CHAIN := {
	"Spine": 0.22, "Spine1": 0.26, "Spine2": 0.30, "Neck": 0.13, "Head": 0.09,
}

# Escursione dell'aim offset in gradi. DEVONO combaciare con AimYawRangeDeg e
# AimPitchRangeDeg di CharacterAnimator.cs, che normalizza gli angoli di mira su
# questi valori prima di scrivere blend_position.
const AIM_YAW_DEG := 60.0
const AIM_PITCH_DEG := 45.0

# Reazione ai colpi: ampiezza del flinch e distribuzione sul busto.
const HIT_CHAIN := {"Spine": 0.28, "Spine1": 0.32, "Spine2": 0.40, "Head": 0.40}
const HIT_DEG := 16.0
const HIT_PEAK := 0.08
const HIT_END := 0.40

var _skel: Skeleton3D
var _library: AnimationLibrary
var _out := AnimationLibrary.new()


# ==========================================================================
#  Primitive
# ==========================================================================

func _bone_rest(bone: String) -> Quaternion:
	return _skel.get_bone_rest(_skel.find_bone(bone)).basis.get_rotation_quaternion()


# Rotazione GLOBALE della posa di riposo di un osso: serve a portare un asse dichiarato
# in coordinate mondo dentro lo spazio locale dell'osso, che e' lo spazio in cui vive
# il delta additivo.
func _bone_global_rest(bone: String) -> Basis:
	return _skel.get_bone_global_rest(_skel.find_bone(bone)).basis


# Posa di un osso in una clip assoluta, a un dato istante. Se la clip non ha la track
# (capita: le clip Mixamo non animano tutto), vale la posa di riposo.
func _sample(clip: String, bone: String, time: float) -> Quaternion:
	var anim: Animation = _library.get_animation(clip)
	for i in anim.get_track_count():
		if anim.track_get_type(i) == Animation.TYPE_ROTATION_3D \
				and String(anim.track_get_path(i)).ends_with(":" + bone):
			return anim.rotation_track_interpolate(i, time)
	return _bone_rest(bone)


# Chiave additiva che porta la base `reference` sul bersaglio `target`.
# Chiave = Rest x Riferimento^-1 x Target (vedi l'intestazione).
func _key_for(bone: String, reference: Quaternion, target: Quaternion) -> Quaternion:
	return _bone_rest(bone) * reference.inverse() * target


# Rotazione attorno a un asse dichiarato in coordinate MONDO, espressa nello spazio
# locale dell'osso. Si porta l'asse nello spazio dell'osso con la sua rest GLOBALE:
# dichiarare l'asse gia' in locale significherebbe indovinare l'orientamento di ogni
# osso, che dipende dai roll del rig (skill blender-pipeline).
func _local_swing(bone: String, world_axis: Vector3, degrees: float) -> Quaternion:
	var axis: Vector3 = (_bone_global_rest(bone).inverse() * world_axis).normalized()
	return Quaternion(axis, deg_to_rad(degrees))


# Crea una clip vuota con le track di rotazione dell'upper body gia' predisposte.
# Ritorna {bone: indice_track}.
func _new_clip(length: float, loop: bool) -> Array:
	var anim := Animation.new()
	anim.length = length
	anim.loop_mode = Animation.LOOP_LINEAR if loop else Animation.LOOP_NONE
	var tracks := {}
	for bone in UPPER_BODY:
		var t := anim.add_track(Animation.TYPE_ROTATION_3D)
		anim.track_set_path(t, "%s:%s" % [SKELETON, bone])
		tracks[bone] = t
	return [anim, tracks]


# ==========================================================================
#  Costruttori
# ==========================================================================

# Posa additiva COSTANTE: il delta che trasforma `reference_clip` in `target_clip`.
#
# E' cosi' che l'impugnatura smette di richiedere un set di locomozione per arma: la
# posa "reggi fucile" e' authorata su un bacino neutro (idle_neutral), il delta la
# esprime come "cosa cambia rispetto allo stare fermi", e quel cambiamento si somma
# identico sopra qualunque clip di locomozione.
func _build_hold(name: String, reference_clip: String, target_clip: String) -> void:
	var pair := _new_clip(1.0, true)
	var anim: Animation = pair[0]
	var tracks: Dictionary = pair[1]

	for bone in UPPER_BODY:
		var key := _key_for(bone, _sample(reference_clip, bone, 0.0),
			_sample(target_clip, bone, 0.0))
		# Due chiavi identiche: la posa e' costante e il loop non salta.
		anim.rotation_track_insert_key(tracks[bone], 0.0, key)
		anim.rotation_track_insert_key(tracks[bone], 1.0, key)

	_out.add_animation(name, anim)
	print("  %-16s posa costante, %d track" % [name, anim.get_track_count()])


# Posa additiva di aim offset: una rotazione distribuita sul rachide.
#
# Il centro e' l'identita' ESATTA per costruzione (chain vuota), che e' la proprieta'
# che authorando in Blender non si riusciva a ottenere.
func _build_aim(name: String, world_axis: Vector3, degrees: float) -> void:
	var pair := _new_clip(1.0, true)
	var anim: Animation = pair[0]
	var tracks: Dictionary = pair[1]

	for bone in UPPER_BODY:
		var key := Quaternion.IDENTITY
		if AIM_CHAIN.has(bone) and absf(degrees) > 0.001:
			key = _local_swing(bone, world_axis, degrees * AIM_CHAIN[bone])
		anim.rotation_track_insert_key(tracks[bone], 0.0, key)
		anim.rotation_track_insert_key(tracks[bone], 1.0, key)

	_out.add_animation(name, anim)
	print("  %-16s aim offset %.0f gradi" % [name, degrees])


# Reazione al colpo: identita' -> flinch -> identita'.
#
# Additiva e non filtrata: si somma a locomozione, impugnatura e mira gia' in corso,
# quindi funziona identica in piedi, accovacciati, in corsa o mentre si mira.
func _build_hit(name: String, world_axis: Vector3, degrees: float) -> void:
	var pair := _new_clip(HIT_END, false)
	var anim: Animation = pair[0]
	var tracks: Dictionary = pair[1]

	for bone in UPPER_BODY:
		var peak := Quaternion.IDENTITY
		if HIT_CHAIN.has(bone):
			peak = _local_swing(bone, world_axis, degrees * HIT_CHAIN[bone])
		anim.rotation_track_insert_key(tracks[bone], 0.0, Quaternion.IDENTITY)
		anim.rotation_track_insert_key(tracks[bone], HIT_PEAK, peak)
		anim.rotation_track_insert_key(tracks[bone], HIT_END, Quaternion.IDENTITY)

	_out.add_animation(name, anim)
	print("  %-16s flinch %.0f gradi" % [name, degrees])


# Rinculo: il delta della clip di sparo rispetto al proprio primo fotogramma.
#
# Cosi' il colpo porta SOLO lo scarto del rinculo, e si somma sopra la posa di mira
# corrente invece di sostituirla: sparare mirando in alto non riabbassa l'arma.
func _build_fire(name: String, clip: String) -> void:
	var source: Animation = _library.get_animation(clip)
	var pair := _new_clip(source.length, false)
	var anim: Animation = pair[0]
	var tracks: Dictionary = pair[1]

	# 24 campioni: la clip di sparo dura qualche decimo di secondo, e il rinculo e' una
	# curva breve che va ricampionata abbastanza fitta da non tagliarne il picco.
	var samples := 24
	for bone in UPPER_BODY:
		var first := _sample(clip, bone, 0.0)
		for s in samples + 1:
			var time: float = source.length * float(s) / float(samples)
			anim.rotation_track_insert_key(tracks[bone], time,
				_key_for(bone, first, _sample(clip, bone, time)))

	_out.add_animation(name, anim)
	print("  %-16s rinculo da '%s' (%.3f s)" % [name, clip, source.length])


func _initialize() -> void:
	# Il rest usato qui DEVE essere quello dello scheletro che consumera' le clip —
	# cioe' quello di Body_Base.glb, NON quello del .glb della libreria, che e' diverso
	# (l'export della libreria avviene con un'azione assegnata). E' l'intero motivo per
	# cui questo tool esiste.
	var body: Node = (load(BODY_PATH) as PackedScene).instantiate()
	root.add_child(body)
	await process_frame
	_skel = body.get_node(SKELETON) as Skeleton3D

	_library = load(LIBRARY_PATH) as AnimationLibrary
	if _library == null:
		printerr("Libreria non caricabile: %s" % LIBRARY_PATH)
		quit(1)
		return

	print("Rest pose da %s (%d ossa), clip sorgente da %s (%d clip)"
		% [BODY_PATH, _skel.get_bone_count(), LIBRARY_PATH,
			_library.get_animation_list().size()])

	# --- impugnatura: delta da idle_neutral verso le pose assolute -----------
	print("\nimpugnatura (delta da idle_neutral):")
	_build_hold("rifle_lowered", "idle_neutral", "rifle_lowered_idle")
	_build_hold("rifle_aim", "idle_neutral", "rifle_aim_idle")
	_build_hold("pistol", "idle_neutral", "pistol_idle")
	_build_hold("pistol_aim", "idle_neutral", "pistol_aim_idle")

	# --- aim offset ----------------------------------------------------------
	# Il rig guarda +Z e la sua sinistra e' +X (contratto degli assi, skill
	# character-animation §5). Pitch attorno a X: NEGATIVO alza il busto. Yaw attorno
	# a Y: positivo ruota verso la sinistra del personaggio, quindi "left" e' +.
	print("\naim offset:")
	_build_aim("aim_center", Vector3.RIGHT, 0.0)
	_build_aim("aim_up", Vector3.RIGHT, -AIM_PITCH_DEG)
	_build_aim("aim_down", Vector3.RIGHT, AIM_PITCH_DEG)
	_build_aim("aim_left", Vector3.UP, AIM_YAW_DEG)
	_build_aim("aim_right", Vector3.UP, -AIM_YAW_DEG)

	# --- reazione ai colpi ---------------------------------------------------
	# La direzione e' quella di VOLO del proiettile: un colpo che viaggia verso -Z
	# arriva da DAVANTI e spinge il busto all'indietro (pitch su, cioe' X negativo).
	print("\nreazione ai colpi:")
	_build_hit("hit_front", Vector3.RIGHT, -HIT_DEG)
	_build_hit("hit_back", Vector3.RIGHT, HIT_DEG)
	_build_hit("hit_left", Vector3.BACK, -HIT_DEG)
	_build_hit("hit_right", Vector3.BACK, HIT_DEG)

	# --- rinculo -------------------------------------------------------------
	print("\nrinculo:")
	_build_fire("rifle_fire", "rifle_fire")
	_build_fire("pistol_fire", "pistol_fire")

	var err := ResourceSaver.save(_out, OUT_PATH)
	if err != OK:
		printerr("Salvataggio fallito: %d" % err)
		quit(1)
		return

	print("\nAnimationLibrary additiva salvata in %s (%d clip)"
		% [OUT_PATH, _out.get_animation_list().size()])
	quit(0)
