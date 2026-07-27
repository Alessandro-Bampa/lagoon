# Genera il BlendTree del personaggio e lo salva come risorsa.
#
# Perche' generato e non scritto a mano nel .tscn: l'albero e' fatto di path di
# track (`Armature_Character/Skeleton3D:Spine`) e di indici di connessione, che
# scritti a mano si sbagliano in silenzio — un filtro con un path errato non da'
# errore, semplicemente non maschera nulla. Qui la struttura e' leggibile, i nomi
# dei bone vengono da una lista sola, ed e' rigenerabile quando l'albero cambia.
#
# Uso:
#   Godot_console.exe --path . --headless --script tools/build_animation_tree.gd
#
# E' uno dei rari GDScript ammessi da CLAUDE.md §2 (tooling da editor).
extends SceneTree

const OUT_PATH := "res://animation/resources/CharacterBlendTree.tres"
const SKELETON := "Armature_Character/Skeleton3D"

# Deve combaciare con PlayerController: sono le coordinate dei punti del BlendSpace.
# CharacterAnimator rilegge WALK_SPEED e CROUCH_SPEED dai bordi dei blend space a
# runtime e segnala in console se non corrispondono ai propri, cosi' una modifica
# fatta da una parte sola non passa inosservata.
const WALK_SPEED := 4.0
const RUN_SPEED := 7.0
const CROUCH_SPEED := 2.0

# Idle NEUTRA disarmata al centro degli spazi di locomozione. La posa "reggi arma"
# non c'entra: quella la sovrappone il layer arma sul solo upper body.
const IDLE_CLIP := "idle_neutral"

# Maschera upper-body: busto, collo, testa e braccia. E' l'insieme di bone che la
# posa dell'arma SOSTITUISCE, lasciando le gambe alla locomozione. Le clavicole
# ci stanno dentro: senza, la spalla resterebbe alla posa di corsa e il braccio
# si staccherebbe visibilmente dal busto.
const UPPER_BODY := [
	"Spine", "Spine1", "Spine2", "Neck", "Head", "HeadTop_End",
	"LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand",
	"RightShoulder", "RightArm", "RightForeArm", "RightHand",
]


# Costruisce uno spazio direzionale a 5 punti: idle al centro e le quattro clip
# cardinali a distanza `radius`.
#
# I cinque punti sono un ROMBO: le quattro punte formano un quadrato ruotato di 45
# gradi e il centro sta strettamente DENTRO il loro inviluppo convesso, quindi la
# triangolazione di Delaunay e' obbligata a produrre i quattro triangoli che coprono
# tutto il rombo. Nessuna terna e' degenere — la condizione che mancava prima, quando
# CrouchSpace aveva due soli punti (collineari per definizione, zero triangoli) e la
# locomozione aveva run_fwd in linea con idle e walk_fwd.
#
# TRAPPOLA: i triangoli ESPLICITI (auto_triangles = false) qui non si possono usare.
# ResourceSaver serializza `triangles` PRIMA di `blend_point_N/pos`, e al caricamento
# add_triangle rifiuta ogni indice perche' i punti non esistono ancora ("Index p_x = 0
# is out of bounds"). Il risultato e' uno spazio a zero triangoli, cioe' la T-pose. La
# rete di sicurezza e' il controllo di copertura in tools/verify_godot_import.gd.
func _directional_space(idle: String, prefix: String, radius: float) -> AnimationNodeBlendSpace2D:
	var space := AnimationNodeBlendSpace2D.new()
	space.min_space = Vector2(-radius, -radius)
	space.max_space = Vector2(radius, radius)
	space.snap = Vector2(0.1, 0.1)
	space.blend_mode = AnimationNodeBlendSpace2D.BLEND_MODE_INTERPOLATED
	space.auto_triangles = true
	space.add_blend_point(_anim(idle), Vector2(0, 0), -1, "idle")
	space.add_blend_point(_anim(prefix + "_fwd"), Vector2(0, radius), -1, prefix + "_fwd")
	space.add_blend_point(_anim(prefix + "_back"), Vector2(0, -radius), -1, prefix + "_back")
	space.add_blend_point(_anim(prefix + "_left"), Vector2(-radius, 0), -1, prefix + "_left")
	space.add_blend_point(_anim(prefix + "_right"), Vector2(radius, 0), -1, prefix + "_right")
	return space


func _anim(clip: String) -> AnimationNodeAnimation:
	var node := AnimationNodeAnimation.new()
	node.animation = clip
	return node


func _apply_upper_body_filter(node: AnimationNode) -> void:
	node.filter_enabled = true
	for bone in UPPER_BODY:
		node.set_filter_path("%s:%s" % [SKELETON, bone], true)


func _initialize() -> void:
	var tree := AnimationNodeBlendTree.new()

	# --- Layer 1: locomozione ------------------------------------------------
	# Assi in m/s nel riferimento dell'AVATAR: X = destra, Y = avanti. Sono le stesse
	# unita' di PlayerController.SyncLocalVelocity, quindi il parametro si scrive senza
	# conversioni.
	#
	# Camminata e corsa sono DUE spazi identici a raggio diverso, non un solo spazio con
	# la corsa come punto in piu': quel punto sarebbe collineare con idle e walk_fwd, ed
	# e' esattamente cio' che genera triangolazioni degeneri.
	tree.add_node("WalkSpace", _directional_space(IDLE_CLIP, "walk", WALK_SPEED), Vector2(-880, -80))
	tree.add_node("RunSpace", _directional_space(IDLE_CLIP, "run", RUN_SPEED), Vector2(-880, 160))

	# sync = true: l'ingresso con peso 0 continua ad avanzare. Senza, passando da
	# camminata a corsa quella ferma ripartirebbe da un tempo vecchio e i piedi
	# salterebbero. NOTA: sync fa avanzare, non mette in FASE — le clip hanno durate
	# diverse, quindi un breve disallineamento del passo nel crossfade resta.
	var move := AnimationNodeBlend2.new()
	move.sync = true
	tree.add_node("MoveBlend", move, Vector2(-620, 20))

	# --- Layer 2: crouch -----------------------------------------------------
	# Anche il crouch e' direzionale a 4 assi, con le clip prese TUTTE dallo stesso set
	# Mixamo: mischiare famiglie diverse cambia l'altezza dell'accovacciamento fra un
	# punto di blend e l'altro, e nelle direzioni intermedie il bacino scatta.
	tree.add_node("CrouchSpace",
		_directional_space("crouch_idle", "crouch", CROUCH_SPEED), Vector2(-880, 400))

	var crouch_blend := AnimationNodeBlend2.new()
	crouch_blend.sync = true
	tree.add_node("CrouchBlend", crouch_blend, Vector2(-620, 240))

	# --- Layer 2b: aria ------------------------------------------------------
	# `fall_loop` (Mixamo "Falling Idle") e' un LOOP vero, a differenza di `jump` che e'
	# un arco completo: e' la clip che mancava per avere uno stato di caduta. Sta PRIMA
	# del layer arma, cosi' cadendo si continua a impugnare l'arma.
	var air := AnimationNodeBlend2.new()
	air.sync = true
	tree.add_node("AirBlend", air, Vector2(-380, 120))
	tree.add_node("FallClip", _anim("fall_idle"), Vector2(-620, 620))

	# --- Layer 3: posa dell'arma (upper-body) --------------------------------
	# Transition e non BlendSpace: fra "reggi fucile" e "reggi pistola" non esiste
	# una via di mezzo sensata da interpolare, si passa dall'una all'altra.
	var pose := AnimationNodeTransition.new()
	pose.input_count = 2
	pose.set_input_name(0, "rifle")
	pose.set_input_name(1, "pistol")
	pose.xfade_time = 0.15
	tree.add_node("WeaponPose", pose, Vector2(-620, 500))
	tree.add_node("RifleIdle", _anim("rifle_idle"), Vector2(-880, 480))
	tree.add_node("PistolIdle", _anim("pistol_idle"), Vector2(-880, 580))

	# Blend2 FILTRATO, non Add2: senza clip-delta un additivo sommerebbe due pose
	# assolute e raddoppierebbe le trasformazioni. Qui la posa dell'arma
	# SOSTITUISCE l'upper body e lascia intatte le gambe.
	#
	# sync = true e' l'invariante "impugnare un'arma non ferma le gambe": da armato
	# il peso di questo nodo e' 1, quindi l'ingresso 0 (la locomozione) ha peso 0 pur
	# restando VISIBILE sulle gambe, che il filtro non copre.
	var weapon_blend := AnimationNodeBlend2.new()
	weapon_blend.sync = true
	_apply_upper_body_filter(weapon_blend)
	tree.add_node("WeaponBlend", weapon_blend, Vector2(-120, 120))

	# --- Layer 4: one-shot ---------------------------------------------------
	# Lo sparo e' filtrato sull'upper body: sparare mentre si corre non interrompe
	# la locomozione, che continua a girare sulle gambe. Stesso motivo di sopra per
	# sync = true.
	var fire := AnimationNodeOneShot.new()
	fire.fadein_time = 0.05
	fire.fadeout_time = 0.15
	fire.mix_mode = AnimationNodeOneShot.MIX_MODE_BLEND
	fire.sync = true
	_apply_upper_body_filter(fire)
	tree.add_node("Fire", fire, Vector2(140, 120))
	tree.add_node("FireClip", _anim("rifle_fire"), Vector2(-120, 360))

	# Il salto invece coinvolge tutto il corpo: nessun filtro.
	#
	# TimeScale davanti alla clip: `jump` e' un arco di salto completo di 1,03 s, ma
	# la durata reale del volo la decidono JumpVelocity e Gravity di PlayerController
	# (2 * v / g, circa 0,6 s con i valori attuali). Senza riscalare, il personaggio
	# atterra mentre la clip e' ancora a mezz'aria. La scala la calcola
	# CharacterAnimator, che conosce sia la durata della clip sia il tempo di volo.
	var jump := AnimationNodeOneShot.new()
	jump.fadein_time = 0.10
	jump.fadeout_time = 0.20
	jump.mix_mode = AnimationNodeOneShot.MIX_MODE_BLEND
	tree.add_node("Jump", jump, Vector2(400, 120))
	tree.add_node("JumpScale", AnimationNodeTimeScale.new(), Vector2(140, 380))
	tree.add_node("JumpClip", _anim("jump"), Vector2(-120, 380))

	# Atterraggio DURO: one-shot su tutto il corpo, innescato solo oltre
	# HardLandingSpeed. Sotto quella soglia non parte nessuna clip e resta la sola
	# ammortizzazione procedurale del bacino, che per un salto normale basta e avanza.
	var land := AnimationNodeOneShot.new()
	land.fadein_time = 0.05
	land.fadeout_time = 0.25
	land.mix_mode = AnimationNodeOneShot.MIX_MODE_BLEND
	tree.add_node("Land", land, Vector2(660, 120))
	tree.add_node("LandClip", _anim("land_hard"), Vector2(400, 380))

	# --- Connessioni ---------------------------------------------------------
	tree.connect_node("MoveBlend", 0, "WalkSpace")
	tree.connect_node("MoveBlend", 1, "RunSpace")
	tree.connect_node("CrouchBlend", 0, "MoveBlend")
	tree.connect_node("CrouchBlend", 1, "CrouchSpace")
	tree.connect_node("AirBlend", 0, "CrouchBlend")
	tree.connect_node("AirBlend", 1, "FallClip")
	tree.connect_node("WeaponPose", 0, "RifleIdle")
	tree.connect_node("WeaponPose", 1, "PistolIdle")
	tree.connect_node("WeaponBlend", 0, "AirBlend")
	tree.connect_node("WeaponBlend", 1, "WeaponPose")
	tree.connect_node("Fire", 0, "WeaponBlend")
	tree.connect_node("Fire", 1, "FireClip")
	tree.connect_node("JumpScale", 0, "JumpClip")
	tree.connect_node("Jump", 0, "Fire")
	tree.connect_node("Jump", 1, "JumpScale")
	tree.connect_node("Land", 0, "Jump")
	tree.connect_node("Land", 1, "LandClip")
	tree.connect_node("output", 0, "Land")

	var err := ResourceSaver.save(tree, OUT_PATH)
	if err != OK:
		printerr("Salvataggio fallito: %d" % err)
		quit(1)
		return

	print("BlendTree salvato in %s" % OUT_PATH)
	print("  nodi: %s" % ", ".join(tree.get_node_list()))
	quit(0)
