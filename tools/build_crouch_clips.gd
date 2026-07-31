# Genera le clip di LOCOMOZIONE ACCOVACCIATA con le braccia animate.
#
# Uso:
#   Godot --path . --headless --script tools/build_crouch_clips.gd
#
# ----------------------------------------------------------------------------------
# PERCHE' ESISTE (misurato, non supposto)
# ----------------------------------------------------------------------------------
# Le cinque clip Mixamo di crouch hanno le braccia FERME: una sola chiave di rotazione
# per osso, ampiezza 0,00 gradi su LeftArm/RightArm/LeftForeArm/RightForeArm, mentre il
# bacino e le gambe hanno 20-30 chiavi ciascuno (misurato con una sonda headless sulla
# libreria importata). Da disarmati il risultato a schermo e' esattamente il difetto
# segnalato: accovacciati le mani restano immobili lungo il corpo mentre le gambe
# camminano. Da armati non si vede, perche' la maschera d'impugnatura sovrascrive le
# braccia comunque.
#
# Non e' un loop_mode sbagliato (§1.5 della skill: quello lo verifica gia'
# verify_godot_import) e non e' un blend space scoperto: la clip gira, semplicemente le
# braccia non ci sono. Nessun controllo esistente poteva accorgersene, perche' tutti
# misuravano le GAMBE — "ti stai ancora muovendo?" e' vero anche con le braccia morte.
#
# ----------------------------------------------------------------------------------
# COSA FA, E PERCHE' UN DELTA ATTORNO ALLA MEDIA
# ----------------------------------------------------------------------------------
# Le braccia si prendono dalla clip IN PIEDI corrispondente (walk_fwd -> crouch_fwd, e
# cosi' via), ma NON in valore assoluto: quello che si trapianta e' lo SCARTO rispetto
# alla media della clip sorgente, applicato sopra la posa d'attesa della clip di crouch:
#
#     q(t) = q_crouch_braccio x (media_sorgente^-1 x q_sorgente(u))
#
# cioe' la stessa composizione post-moltiplicata in spazio locale della semantica
# additiva di Godot (build_additive_clips.gd). E' l'unica forma che funziona qui:
#
#   - in ASSOLUTO le braccia in piedi finirebbero addosso alle gambe. Il busto delle
#     clip di crouch e' piegato in avanti di 35 gradi (crouch_idle) e 59 (crouch_fwd) —
#     misurato sull'asse avanti di Spine2 — e le braccia, figlie di Spine2, si portano
#     dietro quella rotazione: un braccio "lungo il corpo" li' e' un braccio dentro le
#     cosce;
#   - come DELTA attorno alla media la posa media resta ESATTAMENTE quella authorata
#     nella clip di crouch, e ci si somma solo l'oscillazione. Il difetto sparisce senza
#     spostare di un millimetro la posa di riposo, che e' l'unica cosa che l'artista ha
#     davvero deciso.
#
# La FASE si conserva perche' le clip di crouch e quelle in piedi sono in fase fra loro:
# misurata la Z della caviglia sinistra rispetto al bacino, walk_fwd e crouch_fwd hanno
# il massimo nello stesso ottavo di ciclo (indice 2) e il minimo nel medesimo (indice 7),
# e cosi' walk_back/crouch_back. Il tempo si normalizza sulla DURATA della clip di
# destinazione, quindi il trapianto resta agganciato al passo delle gambe di crouch.
#
# `crouch_idle` e' l'eccezione dichiarata: `idle_neutral` dura 9,96 s contro 2,13 s, e
# normalizzarla vorrebbe dire respirare cinque volte piu' in fretta. La sorgente si
# percorre quindi avanti e indietro (ping-pong) su una finestra lunga quanto la clip di
# destinazione: il respiro resta alla sua velocita' e il ciclo si chiude per costruzione
# (l'istante finale coincide con quello iniziale), che e' cio' che un loop pretende.
#
# E' uno dei rari GDScript ammessi da CLAUDE.md §2 (tooling da editor).
extends SceneTree

# Si parte dagli ASSET e non da CharacterRig.tscn, per la stessa ragione degli altri due
# generatori: la scena del rig referenzia il file prodotto qui, e caricarla creerebbe una
# dipendenza circolare che impedisce il primo bootstrap.
const BODY_PATH := "res://assets/models/Body_Base.glb"
const LIBRARY_PATH := "res://assets/models/animations/CharacterAnimations.glb"
const OUT_PATH := "res://animation/resources/CrouchClips.tres"
const SKELETON := "Armature_Character/Skeleton3D"

# Le ossa a cui si rifa' il movimento. Sono le stesse otto della maschera d'impugnatura
# (HOLD_MASK di build_animation_tree.gd): da armati vengono sovrascritte, da disarmati
# sono esattamente quelle che restavano immobili.
const ARM_BONES := [
	"LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand",
	"RightShoulder", "RightArm", "RightForeArm", "RightHand",
]

# Chiavi al secondo delle track rigenerate. Le clip Mixamo stanno sui 30 fps: campionare
# piu' fitto non aggiunge informazione, campionare piu' rado taglia gli estremi
# dell'oscillazione.
const SAMPLE_RATE := 30.0

# Margine minimo fra una mano e i giunti delle gambe, in metri.
#
# Serve perche' il busto delle clip di crouch e' piegato in avanti e le ginocchia stanno
# alte davanti al petto: le braccia, figlie di Spine2, si portano dietro quella rotazione
# e la stessa oscillazione che in piedi passa larga li' arriva addosso alle cosce. A piena
# ampiezza, misurato, crouch/right scendeva a 0,100 m e crouch/fwd a 0,118 — mano dentro
# la coscia, cioe' lo stesso difetto dell'impugnatura, solo da disarmati.
#
# Il riferimento alto sono le clip authorate, che stanno fra 0,184 (walk_back, la piu'
# stretta) e 0,23: irraggiungibile qui, perche' da accovacciati la posa FERMA e' gia' a
# 0,20 e non lascia margine a nessuna oscillazione. 0,15 e' la soglia sotto cui i due
# volumi si toccano davvero — mano ~0,05 m di raggio piu' ~0,07 dell'arto — e sopra la
# quale l'oscillazione resta visibile: misurato, lascia 17 gradi di braccio in avanti e
# 41 all'indietro.
const MIN_HAND_LEG := 0.15

# Ampiezze provate, dalla piu' larga in giu': si tiene la PRIMA che rispetta il margine.
# Cosi' ogni clip prende tutta l'oscillazione che la sua postura le concede, invece di
# subire un unico fattore tarato sul caso peggiore. Vengono moltiplicate per il `gain`
# della clip (vedi CLIPS).
const SWING_SCALES := [1.0, 0.85, 0.7, 0.55, 0.4, 0.25]

# Le cinque clip da rigenerare. `arms` e' la clip IN PIEDI da cui viene l'oscillazione,
# `pingpong` la percorre avanti e indietro invece che una volta sola (vedi intestazione).
#
# `gain` e' il tetto dell'ampiezza. Vale 1 ovunque — l'oscillazione di una camminata e'
# gia' quella giusta, e allargarla darebbe un passo caricaturale — tranne sull'idle, dove
# il respiro di idle_neutral vale 7 gradi di braccio distribuiti su dieci secondi: portato
# su una clip di due, resta sotto la soglia a cui si distingue dall'immobilita' (misurato,
# 0,0017 rad per frame contro i 0,002 che la suite pretende). Li' le gambe sono ferme e il
# margine c'e' tutto, quindi si allarga finche' si vede.
const CLIPS := [
	{"name": "idle", "crouch": "crouch_idle", "arms": "idle_neutral", "pingpong": true, "gain": 2.0},
	{"name": "fwd", "crouch": "crouch_fwd", "arms": "walk_fwd", "pingpong": false, "gain": 1.0},
	{"name": "back", "crouch": "crouch_back", "arms": "walk_back", "pingpong": false, "gain": 1.0},
	{"name": "left", "crouch": "crouch_left", "arms": "walk_left", "pingpong": false, "gain": 1.0},
	{"name": "right", "crouch": "crouch_right", "arms": "walk_right", "pingpong": false, "gain": 1.0},
]

var _skel: Skeleton3D
var _library: AnimationLibrary
var _out := AnimationLibrary.new()


# ==========================================================================
#  Primitive sulle clip
# ==========================================================================

func _track_of(anim: Animation, bone: String) -> int:
	for i in anim.get_track_count():
		if anim.track_get_type(i) == Animation.TYPE_ROTATION_3D \
				and String(anim.track_get_path(i)).ends_with(":" + bone):
			return i
	return -1


func _sample(anim: Animation, bone: String, time: float) -> Quaternion:
	var track := _track_of(anim, bone)
	if track < 0:
		return _skel.get_bone_rest(_skel.find_bone(bone)).basis.get_rotation_quaternion()
	return anim.rotation_track_interpolate(track, time)


# Rotazione MEDIA di un osso sulla finestra [0, span] della clip sorgente.
#
# I quaternioni si mediano componente per componente allineando prima i segni (q e -q
# sono la stessa rotazione, ma sommati si annullano) e rinormalizzando. Su un'escursione
# di pochi gradi e' indistinguibile dalla media geometrica vera, e serve solo come
# ANCORA: e' il punto in cui il delta trapiantato vale identita', cioe' l'istante in cui
# il braccio resta esattamente nella posa della clip di crouch.
func _mean_rotation(anim: Animation, bone: String, span: float) -> Quaternion:
	var samples := maxi(2, int(span * SAMPLE_RATE))
	var accum := Quaternion(0, 0, 0, 0)
	var reference := _sample(anim, bone, 0.0)
	for i in samples:
		var q := _sample(anim, bone, span * i / float(samples))
		if q.dot(reference) < 0.0:
			q = -q
		accum.x += q.x
		accum.y += q.y
		accum.z += q.z
		accum.w += q.w
	return accum.normalized()


# Tempo a cui leggere la clip sorgente per il tempo `t` della clip di destinazione.
func _source_time(t: float, dst_length: float, span: float, pingpong: bool) -> float:
	var phase: float = t / dst_length
	if not pingpong:
		return span * phase
	# Triangolo 0 -> 1 -> 0: il valore finale coincide col primo, quindi il loop chiude.
	return span * (1.0 - absf(2.0 * phase - 1.0))


# ==========================================================================
#  Diagnostica
# ==========================================================================

# Mette lo scheletro nella posa di una clip, istante per istante, e misura quanto le MANI
# si avvicinano ai giunti delle gambe e quanto oscilla il braccio.
#
# E' la misura che dice se il trapianto e' andato a buon fine: un'oscillazione in piedi
# sommata a un busto piegato in avanti puo' portare le mani dentro le cosce, e sarebbe lo
# stesso difetto dell'impugnatura da accovacciati, solo da disarmati.
func _probe(anim: Animation) -> Array:
	var closest := INF
	var swing := 0.0
	var first := _sample(anim, "LeftArm", 0.0)
	var samples := 24
	for i in samples:
		var t: float = anim.length * i / float(samples)
		for b in _skel.get_bone_count():
			_skel.set_bone_pose_rotation(b, _sample(anim, _skel.get_bone_name(b), t))
		swing = maxf(swing, rad_to_deg(first.angle_to(_sample(anim, "LeftArm", t))))
		for side in ["Left", "Right"]:
			var hand: Vector3 = _skel.get_bone_global_pose(_skel.find_bone(side + "Hand")).origin
			for leg in ["LeftUpLeg", "LeftLeg", "RightUpLeg", "RightLeg"]:
				closest = minf(closest,
					hand.distance_to(_skel.get_bone_global_pose(_skel.find_bone(leg)).origin))
	return [swing, closest]


# ==========================================================================
#  Costruzione
# ==========================================================================

func _build(entry: Dictionary, scale: float) -> Animation:
	var source: Animation = _library.get_animation(entry["arms"])
	var anim: Animation = _library.get_animation(entry["crouch"]).duplicate(true)
	anim.loop_mode = Animation.LOOP_LINEAR

	var pingpong: bool = entry["pingpong"]
	var span: float = minf(anim.length, source.length) if pingpong else source.length
	var keys := maxi(2, int(anim.length * SAMPLE_RATE))

	for bone in ARM_BONES:
		# Posa d'attesa: la clip di crouch tiene le braccia ferme, quindi un istante
		# qualunque va bene — si legge a 0 per non dipendere dal campionamento.
		var hold := _sample(anim, bone, 0.0)
		var mean := _mean_rotation(source, bone, span)

		var track := _track_of(anim, bone)
		if track < 0:
			track = anim.add_track(Animation.TYPE_ROTATION_3D)
			anim.track_set_path(track, "%s:%s" % [SKELETON, bone])
		else:
			for k in range(anim.track_get_key_count(track) - 1, -1, -1):
				anim.track_remove_key(track, k)

		for i in keys:
			var t: float = anim.length * i / float(keys)
			var u := _source_time(t, anim.length, span, pingpong)
			var delta := mean.inverse() * _sample(source, bone, u)
			if not is_equal_approx(scale, 1.0):
				# slerp con t > 1 ESTRAPOLA: e' cosi' che si allarga l'oscillazione
				# oltre quella della sorgente, non solo la si riduce.
				delta = Quaternion.IDENTITY.slerp(delta, scale)
			anim.rotation_track_insert_key(track, t, hold * delta)

	return anim


# Costruisce la clip alla massima ampiezza che rispetta il margine mani-gambe.
func _build_fitted(entry: Dictionary) -> void:
	var chosen: Animation
	var chosen_scale := 0.0
	var measured := [0.0, 0.0]

	for step in SWING_SCALES:
		var scale: float = step * float(entry["gain"])
		var anim := _build(entry, scale)
		var probe := _probe(anim)
		chosen = anim
		chosen_scale = scale
		measured = probe
		if probe[1] >= MIN_HAND_LEG:
			break

	_out.add_animation(entry["name"], chosen)
	var warning := "" if measured[1] >= MIN_HAND_LEG else "   <-- MARGINE NON RAGGIUNTO"
	print("  crouch/%-6s da %-12s + braccia di %-12s ampiezza %.2f -> oscillazione %5.1f gradi, mani-gambe %.3f m%s"
		% [entry["name"], entry["crouch"], entry["arms"], chosen_scale,
			measured[0], measured[1], warning])


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

	print("Clip di crouch da %s" % LIBRARY_PATH)
	for entry in CLIPS:
		_build_fitted(entry)

	var err := ResourceSaver.save(_out, OUT_PATH)
	if err != OK:
		printerr("Salvataggio fallito: %d" % err)
		quit(1)
		return

	print("\nAnimationLibrary di crouch salvata in %s (%d clip)"
		% [OUT_PATH, _out.get_animation_list().size()])
	quit(0)
