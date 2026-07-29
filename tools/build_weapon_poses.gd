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
# ----------------------------------------------------------------------------------
# LA POSA DI MIRA DEL FUCILE NON PUO' VENIRE DA `rifle_idle` (misurato)
# ----------------------------------------------------------------------------------
# `rifle_idle` non e' una posa di mira: e' un PORTO, con l'arma di traverso sul petto.
# Conta perche' l'arma non ha una direzione propria — pende dalla mano destra con
# GripRotationDegrees, e la mano di supporto le si aggancia a SupportGripOffset lungo il
# suo +Z: la canna punta quindi lungo la congiungente MANO DESTRA -> MANO SINISTRA.
# Misurato, angolo fra quella congiungente e l'avanti del busto (l'asse che
# SpineAimModifier porta sulla mira):
#
#     rifle_idle ..................... 85 gradi   (arma di traverso: NON punta)
#     rifle_fire (braccia, t = 0) .... 43 gradi   (posa spallata, ma busto ruotato)
#     posa di mira generata qui ....... 0 gradi
#
# Con 85 gradi di scarto nessun layer puo' rimediare: l'aim offset e SpineAimModifier
# ruotano il BUSTO verso il bersaglio, e le braccia lo seguono in blocco portandosi
# dietro lo scarto. Il risultato a video e' esattamente "impugno il fucile, miro, e le
# braccia non vanno in puntamento" — mentre la pistola sembra funzionare perche' non ha
# mano di supporto e la sua canna dipende dalla sola GripRotationDegrees.
#
# L'unica posa SPALLATA nella libreria e' `rifle_fire`, che pero' e' authorata su un
# busto girato di una quarantina di gradi (misurato: Spine2 avanti = (-0.61,-0.36,+0.71)
# contro (-0.09,+0.08,+0.99) di idle_neutral). Siccome le braccia sono figlie di Spine2,
# quella rotazione se la portano dietro. Qui si prendono le sue braccia e si RIALLINEANO
# ruotandole in blocco finche' la congiungente fra le mani coincide con l'avanti del
# busto: cosi' la posa e' spallata e l'arma punta dove punta il torso, che e' cio' che i
# layer di mira sanno orientare.
#
# Ruotare ENTRAMBE le braccia della stessa rotazione conserva la presa (le mani si
# spostano insieme), quindi il vincolo dell'astina resta valido: cambia solo la sua
# lunghezza misurata, che il tool ristampa per SupportGripOffset.
#
# Le misure di GripRotationDegrees e SupportGripOffset NON sono piu' quelle storiche,
# perche' la posa di riferimento e' cambiata: il tool le RICALCOLA e le stampa, e i
# valori vanno ricopiati in animation/resources/two_handed.tres e one_handed.tres
# (skill character-animation §7).
#
# Il porto rilassato si ottiene ruotando le braccia verso il basso di un angolo MISURATO
# sulla quota delle mani, non scelto a occhio.
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

# Osso di cui si misura l'"avanti": e' il vertice della catena di SpineAimModifier, cioe'
# l'osso che i layer di mira portano sul bersaglio. Allineare li' la canna e' cio' che
# rende la mira efficace su qualunque locomozione sotto.
const AIM_TIP := "Spine2"

# Porto rilassato ("low ready"): di quanto ruotano verso il basso braccio e avambraccio
# rispetto alla posa di mira. I valori sono TARATI sulla quota delle mani stampata in
# fondo: 35/15 (la vecchia posa authorata in Blender) portava le mani a y = +0,01 m dal
# bacino e z = +0,04 m, cioe' braccia lungo il corpo e mani dentro i fianchi — il difetto
# segnalato. L'arma deve restare DAVANTI, col muso in basso.
#
# Il fucile parte ora da una posa SPALLATA (mani all'altezza del petto), e l'abbassamento
# gira sulle clavicole: 30 gradi portano le mani a meta' petto tenendole davanti, con la
# canna inclinata di 31 gradi verso terra.
const RIFLE_LOWER_SHOULDER_DEG := 30.0
const RIFLE_LOWER_FOREARM_DEG := 0.0

# La pistola parte da una posa gia' estesa in avanti: il porto la abbassa di piu',
# perche' con una sola mano il gomito scende lungo il fianco.
const PISTOL_LOWER_SHOULDER_DEG := 34.0
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


# Prende dalla clip le SOLE braccia, lasciando il resto del corpo com'e'. E' la stessa
# operazione che a runtime fa il Blend2 filtrato HoldMask, quindi costruire la posa cosi'
# significa costruirla nelle condizioni in cui verra' consumata.
func _mask_arms(clip: String, time: float) -> void:
	for bone in ARM_BONES:
		_skel.set_bone_pose_rotation(_skel.find_bone(bone), _sample(clip, bone, time))


func _bone_position(bone: String) -> Vector3:
	return _skel.get_bone_global_pose(_skel.find_bone(bone)).origin


func _tip_basis() -> Basis:
	return _skel.get_bone_global_pose(_skel.find_bone(AIM_TIP)).basis.orthonormalized()


# Direzione "avanti" del busto nella posa CORRENTE, in coordinate dello scheletro.
#
# E' l'asse che SpineAimModifier porta sulla direzione di mira: lo misura sul rest del
# proprio osso di vertice (SkeletonForward = +Z) e poi lo trasporta con la posa. Qui si
# ripete la stessa costruzione, cosi' l'allineamento e' fatto contro esattamente l'asse
# che a runtime punta al bersaglio.
func _torso_forward() -> Vector3:
	var idx := _skel.find_bone(AIM_TIP)
	var forward_in_tip: Vector3 = \
		_skel.get_bone_global_rest(idx).basis.orthonormalized().inverse() * Vector3(0, 0, 1)
	return (_tip_basis() * forward_in_tip).normalized()


# Asse dell'arma nella posa corrente: la congiungente mano destra -> mano sinistra.
#
# Non e' una convenzione arbitraria — e' come WeaponGripRig costruisce l'arma: la presa
# pende dalla mano destra e la mano di supporto va a SupportGripOffset lungo il +Z
# dell'arma, quindi la canna passa per le due mani.
func _weapon_axis() -> Vector3:
	return (_bone_position("LeftHand") - _bone_position("RightHand")).normalized()


# Ruota un osso attorno a un asse dichiarato nello spazio dello SCHELETRO (il
# personaggio guarda +Z, la sua sinistra e' +X, su e' +Y).
#
# La rotazione va portata nello spazio del PADRE, perche' e' li' che vive la posa
# locale dell'osso: nuova_locale = Padre^-1 x R x Padre x locale. I figli seguono da
# soli, essendo espressi rispetto a questo osso.
func _rotate_bone(bone: String, axis: Vector3, degrees: float) -> void:
	_rotate_bone_by(bone, Basis(axis.normalized(), deg_to_rad(degrees)))


# Come sopra, con la rotazione data direttamente come Basis nello spazio dello scheletro.
func _rotate_bone_by(bone: String, rot: Basis) -> void:
	var idx := _skel.find_bone(bone)
	var parent := _skel.get_bone_parent(idx)
	var parent_basis := Basis.IDENTITY
	if parent >= 0:
		parent_basis = _skel.get_bone_global_pose(parent).basis.orthonormalized()
	var local := _skel.get_bone_pose_rotation(idx)
	_skel.set_bone_pose_rotation(idx,
		(parent_basis.inverse() * rot * parent_basis).get_rotation_quaternion() * local)


# Ruota le DUE braccia in blocco finche' l'asse dell'arma coincide con l'avanti del busto.
#
# Si ruotano le clavicole, non i bracci: e' il movimento con cui una spalla si porta
# dietro tutto il braccio, e mantiene la posa interna del gomito (quindi la presa).
#
# ITERATIVO per la stessa ragione per cui lo e' SpineAimModifier: le due spalle hanno
# origini diverse, quindi ruotare ciascuna attorno al PROPRIO giunto non e' esattamente
# una rotazione rigida della congiungente fra le mani. Un colpo solo lascia qualche grado
# di residuo; poche iterazioni lo portano sotto il decimo di grado.
func _align_weapon_to_torso(steps: int = 12) -> float:
	var residual := rad_to_deg(_weapon_axis().angle_to(_torso_forward()))
	for i in steps:
		var correction := Basis(Quaternion(_weapon_axis(), _torso_forward()))
		for bone in ["LeftShoulder", "RightShoulder"]:
			_rotate_bone_by(bone, correction)
		residual = rad_to_deg(_weapon_axis().angle_to(_torso_forward()))
		if residual < 0.05:
			break
	return residual


# Misura GripRotationDegrees e SupportGripOffset dalla posa corrente (skill §7: si
# MISURANO, non si indovinano) e le stampa nella forma da ricopiare nel .tres.
#
# `axis` e' la direzione in cui deve puntare la canna, in coordinate dello scheletro.
# La presa e' un Basis.from_euler(x, y, 0) applicato alla base della mano destra, e
# l'ordine YXZ di Godot da' R * (0,0,1) = (sin y cos x, -sin x, cos y cos x): la terza
# componente non entra (una rotazione attorno a +Z lascia fermo +Z), quindi le due
# incognite si ricavano in chiuso. La X resta dentro [-90, +90], cioe' l'intervallo in
# cui la componente di mezzo dell'ordine YXZ e' ben definita (skill §1.8).
func _measure_grip(label: String, axis: Vector3, two_handed: bool) -> void:
	var hand := _skel.get_bone_global_pose(_skel.find_bone("RightHand")).basis.orthonormalized()
	var d: Vector3 = (hand.inverse() * axis).normalized()
	var x := -asin(clampf(d.y, -1.0, 1.0))
	var y := atan2(d.x, d.z)
	var reach: float = (_bone_position("LeftHand") - _bone_position("RightHand")).length() \
		if two_handed else 0.0

	# Controprova: la presa cosi' costruita deve riprodurre l'asse voluto.
	var grip_basis := hand * Basis.from_euler(Vector3(x, y, 0.0))
	var rebuilt: Vector3 = grip_basis * Vector3(0, 0, 1)
	print("  %s -> GripRotationDegrees = Vector3(%.1f, %.1f, 0)   SupportGripOffset = Vector3(0, 0, %.3f)"
		% [label, rad_to_deg(x), rad_to_deg(y), reach])
	print("       canna ricostruita (%+.3f,%+.3f,%+.3f), scarto dall'asse voluto %.2f gradi"
		% [rebuilt.x, rebuilt.y, rebuilt.z, rad_to_deg(rebuilt.angle_to(axis))])

	if not two_handed:
		return

	# Polo del gomito sinistro, in coordinate della PRESA (e' li' che WeaponGripRig lo
	# mette, come figlio di GripPoint). Va misurato ogni volta che cambia la presa: e'
	# espresso nel frame dell'arma, quindi ruotando GripRotationDegrees si sposta con
	# lei — e un polo finito dall'altra parte fa piegare il gomito AL CONTRARIO, senza
	# che l'IK smetta di raggiungere l'astina (quindi senza che alcun controllo di
	# distanza se ne accorga).
	#
	# Il bersaglio e' il gomito della posa animata, spinto un po' PIU' IN LA' lungo la
	# propria direzione: TwoBoneIK3D usa il polo come direzione verso cui aprire il
	# ginocchio/gomito, e tenerlo esattamente sull'osso lo rende instabile quando la
	# mano si muove.
	var elbow: Vector3 = grip_basis.inverse() \
		* (_bone_position("LeftForeArm") - _bone_position("RightHand"))
	print("       SupportElbowHint = Vector3(%.3f, %.3f, %.3f)  (gomito misurato nella posa)"
		% [elbow.x, elbow.y, elbow.z])


# Abbassa entrambe le braccia: rotazione attorno all'asse laterale (+X), che porta
# l'avanti (+Z) verso il basso (-Y).
#
# La rotazione va sulle CLAVICOLE, non sui bracci, e la differenza e' misurata: le due
# clavicole nascono quasi nello stesso punto (la base del collo), quindi ruotarle della
# stessa quantita' e' quasi una rotazione rigida del blocco braccia e la distanza fra le
# mani — cioe' la lunghezza dell'astina, il vincolo della presa — resta invariata al
# millimetro. Ruotando i bracci, che nascono a mezzo metro l'uno dall'altro, la presa si
# allargava di 4,5 cm e per giunta la canna quasi non si inclinava:
#
#     braccio 42 + avambraccio 10 ... d = 0,299 m (da 0,254), canna giu' di  9 gradi
#     clavicola 30 ................. d = 0,254 m (invariata), canna giu' di 31 gradi
#
# Ed e' l'inclinazione della canna il senso del porto rilassato: l'arma punta a terra.
func _lower_arms(shoulder_deg: float, forearm_deg: float) -> void:
	for side in ["Left", "Right"]:
		_rotate_bone(side + "Shoulder", Vector3(1, 0, 0), shoulder_deg)
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
	# Inclinazione della canna rispetto all'orizzonte: positiva = muso in basso. In mira
	# deve essere nulla, nel porto rilassato l'arma deve puntare a terra.
	var pitch := rad_to_deg(asin(clampf(-_weapon_axis().y, -1.0, 1.0)))
	return "manoDX(y%+.2f z%+.2f) manoSX(y%+.2f z%+.2f) distanza=%.3f m canna_giu=%.0f gradi" \
		% [rh.y, rh.z, lh.y, lh.z, (lh - rh).length(), pitch]


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

	# Il corpo resta su una locomozione NEUTRA per tutta la costruzione, e non sulla clip
	# da cui arrivano le braccia: le pose congelate sono locali (quindi indipendenti dal
	# busto), ma le rotazioni di allineamento e di abbassamento sono dichiarate in
	# coordinate dello scheletro, e su un busto girato produrrebbero un altro risultato.
	# idle_neutral e' la base della locomozione, cioe' esattamente il caso d'uso.

	# --- fucile ---------------------------------------------------------------
	# Le braccia SPALLATE arrivano da rifle_fire (l'unica posa di mira vera nella
	# libreria: rifle_idle e' un porto di traverso, vedi l'intestazione) e vengono
	# riallineate in blocco finche' la canna punta dove punta il busto.
	print("\nfucile:")
	_apply_clip("idle_neutral", 0.0)
	_mask_arms("rifle_fire", 0.0)
	print("  scarto della canna dall'avanti del busto, prima: %.0f gradi"
		% rad_to_deg(_weapon_axis().angle_to(_torso_forward())))
	var residual := _align_weapon_to_torso()
	print("  scarto dopo l'allineamento: %.2f gradi" % residual)
	_freeze_arms("rifle_aim")
	_measure_grip("two_handed.tres", _torso_forward(), true)

	_lower_arms(RIFLE_LOWER_SHOULDER_DEG, RIFLE_LOWER_FOREARM_DEG)
	_freeze_arms("rifle_lowered")

	# --- pistola --------------------------------------------------------------
	# Qui NON si allinea nulla ruotando le braccia: con una sola mano sull'arma la
	# congiungente fra le mani non e' l'asse dell'arma (SupportGripOffset e' nullo), e
	# la direzione della canna dipende dalla sola GripRotationDegrees. La si misura
	# contro lo stesso avanti del busto, cosi' anche la pistola punta dove si mira.
	print("\npistola:")
	_apply_clip("idle_neutral", 0.0)
	_mask_arms("pistol_idle", 0.0)
	_freeze_arms("pistol_aim")
	_measure_grip("one_handed.tres", _torso_forward(), false)

	_lower_arms(PISTOL_LOWER_SHOULDER_DEG, PISTOL_LOWER_FOREARM_DEG)
	_freeze_arms("pistol")

	var err := ResourceSaver.save(_out, OUT_PATH)
	if err != OK:
		printerr("Salvataggio fallito: %d" % err)
		quit(1)
		return

	print("\nAnimationLibrary d'impugnatura salvata in %s (%d pose)"
		% [OUT_PATH, _out.get_animation_list().size()])
	quit(0)
