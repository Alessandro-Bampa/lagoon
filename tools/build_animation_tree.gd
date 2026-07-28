# Genera il BlendTree del personaggio e lo salva come risorsa.
#
# ARCHITETTURA A LAYER ADDITIVI (rifatta rispetto alla versione a stance):
#   1. Locomozione FULL BODY, agnostica dall'arma: walk/run/crouch/aria. Nessun set
#      armato: l'arma non cambia come si cammina.
#   2. Impugnatura: delta ADDITIVO upper-body (add/rifle_* e add/pistol*), sommato
#      sopra qualunque locomozione. Le clip delta portano SOLO le track dell'upper
#      body, quindi le gambe non ricevono nulla per costruzione: non servono filtri.
#   3. Aim offset: BlendSpace2D di 5 pose additive (centro/su/giu'/sx/dx) pilotato
#      da yaw/pitch della mira, sommato sopra impugnatura e locomozione.
#   4. One-shot additivi: sparo e hit reaction (delta, MIX_MODE_ADD).
#   5. One-shot assoluti full body: salto, atterraggio, scavalcamento.
#   I layer procedurali (SpineAim, FootIk, GripRig) NON stanno qui: sono
#   SkeletonModifier3D che girano dopo l'albero.
#
# Le clip delta vivono in una libreria SEPARATA, prefisso `add/`
# (animation/resources/AdditiveClips.tres, generata da tools/build_additive_clips.gd).
# Non passano da Blender: il perche' — due difetti misurati della via glTF — sta
# nell'intestazione di quel tool. Rigenerare l'albero dopo aver rigenerato le clip.
#
# Perche' generato e non scritto a mano nel .tscn: l'albero e' fatto di path di
# track e di indici di connessione, che scritti a mano si sbagliano in silenzio.
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
# runtime e segnala in console se non corrispondono ai propri.
const WALK_SPEED := 4.0
const RUN_SPEED := 7.0
const CROUCH_SPEED := 2.0

# Idle NEUTRA disarmata al centro degli spazi di locomozione. La posa "reggi arma"
# non c'entra: quella la somma il layer additivo di impugnatura.
const IDLE_CLIP := "idle_neutral"


# Costruisce uno spazio direzionale a 5 punti: idle al centro e le quattro clip
# cardinali a distanza `radius`.
#
# I cinque punti sono un ROMBO: le quattro punte formano un quadrato ruotato di 45
# gradi e il centro sta strettamente DENTRO il loro inviluppo convesso, quindi la
# triangolazione di Delaunay e' obbligata a produrre i quattro triangoli che coprono
# tutto il rombo. Nessuna terna e' degenere.
#
# TRAPPOLA: i triangoli ESPLICITI (auto_triangles = false) qui non si possono usare.
# ResourceSaver serializza `triangles` PRIMA di `blend_point_N/pos`, e al caricamento
# add_triangle rifiuta ogni indice perche' i punti non esistono ancora. Il risultato
# e' uno spazio a zero triangoli, cioe' la T-pose. La rete di sicurezza e' il
# controllo di copertura in tools/verify_godot_import.gd.
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


# Aim offset: 5 pose DELTA (centro = identita') su assi normalizzati [-1, 1].
# X = yaw (positivo = destra), Y = pitch (positivo = su). CharacterAnimator normalizza
# gli angoli di mira sull'escursione con cui le pose sono state generate (AIM_YAW_DEG e
# AIM_PITCH_DEG di tools/build_additive_clips.gd) prima di scrivere blend_position.
func _aim_space() -> AnimationNodeBlendSpace2D:
	var space := AnimationNodeBlendSpace2D.new()
	space.min_space = Vector2(-1, -1)
	space.max_space = Vector2(1, 1)
	space.snap = Vector2(0.05, 0.05)
	space.blend_mode = AnimationNodeBlendSpace2D.BLEND_MODE_INTERPOLATED
	space.auto_triangles = true
	space.add_blend_point(_anim("add/aim_center"), Vector2(0, 0), -1, "center")
	space.add_blend_point(_anim("add/aim_up"), Vector2(0, 1), -1, "up")
	space.add_blend_point(_anim("add/aim_down"), Vector2(0, -1), -1, "down")
	space.add_blend_point(_anim("add/aim_left"), Vector2(-1, 0), -1, "left")
	space.add_blend_point(_anim("add/aim_right"), Vector2(1, 0), -1, "right")
	return space


func _anim(clip: String) -> AnimationNodeAnimation:
	var node := AnimationNodeAnimation.new()
	node.animation = clip
	return node


func _initialize() -> void:
	var tree := AnimationNodeBlendTree.new()

	# --- Layer 1: locomozione (unica, agnostica dall'arma) -------------------
	# Assi in m/s nel riferimento dell'AVATAR: X = destra, Y = avanti. Sono le stesse
	# unita' di PlayerController.SyncLocalVelocity.
	#
	# Camminata e corsa sono DUE spazi identici a raggio diverso, non un solo spazio
	# con la corsa come punto in piu': quel punto sarebbe collineare con idle e
	# walk_fwd, ed e' esattamente cio' che genera triangolazioni degeneri.
	tree.add_node("WalkSpace", _directional_space(IDLE_CLIP, "walk", WALK_SPEED), Vector2(-1140, -80))
	tree.add_node("RunSpace", _directional_space(IDLE_CLIP, "run", RUN_SPEED), Vector2(-1140, 160))

	# sync = true: l'ingresso con peso 0 continua ad avanzare. Senza, passando da
	# camminata a corsa quella ferma ripartirebbe da un tempo vecchio e i piedi
	# salterebbero. NOTA: sync fa avanzare, non mette in FASE — le clip hanno durate
	# diverse, quindi un breve disallineamento del passo nel crossfade resta.
	var move := AnimationNodeBlend2.new()
	move.sync = true
	tree.add_node("MoveBlend", move, Vector2(-880, 20))

	# --- Layer 1b: crouch ----------------------------------------------------
	# Anche il crouch e' direzionale a 4 assi, con le clip prese TUTTE dallo stesso
	# set: mischiare famiglie diverse cambia l'altezza dell'accovacciamento fra un
	# punto di blend e l'altro, e nelle direzioni intermedie il bacino scatta.
	tree.add_node("CrouchSpace",
		_directional_space("crouch_idle", "crouch", CROUCH_SPEED), Vector2(-880, 400))

	var crouch_blend := AnimationNodeBlend2.new()
	crouch_blend.sync = true
	tree.add_node("CrouchBlend", crouch_blend, Vector2(-620, 240))

	# --- Layer 1c: aria ------------------------------------------------------
	# `fall_idle` e' un loop vero. Sta PRIMA dei layer additivi, cosi' cadendo si
	# continua a impugnare l'arma.
	var air := AnimationNodeBlend2.new()
	air.sync = true
	tree.add_node("AirBlend", air, Vector2(-380, 120))
	tree.add_node("FallClip", _anim("fall_idle"), Vector2(-620, 620))

	# --- Layer 2: impugnatura (ADDITIVO upper-body) --------------------------
	# Transition e non BlendSpace: fra le pose d'arma non esiste una via di mezzo
	# sensata da interpolare, si passa dall'una all'altra (xfade 0.15, che sui DELTA
	# interpola verso/da identita' senza artefatti).
	#
	# QUATTRO delta, due per famiglia: porto rilassato e mira. La mira e' uno STATO
	# (RMB), non una conseguenza dell'essere armati. I nomi degli ingressi restano
	# quelli storici: CharacterAnimator non cambia contratto.
	var pose := AnimationNodeTransition.new()
	pose.input_count = 4
	pose.set_input_name(0, "rifle_lowered")
	pose.set_input_name(1, "rifle_aim")
	pose.set_input_name(2, "pistol")
	pose.set_input_name(3, "pistol_aim")
	pose.xfade_time = 0.15
	tree.add_node("WeaponPose", pose, Vector2(-620, 500))
	tree.add_node("RifleLowered", _anim("add/rifle_lowered"), Vector2(-880, 460))
	tree.add_node("RifleAim", _anim("add/rifle_aim"), Vector2(-880, 530))
	tree.add_node("PistolIdle", _anim("add/pistol"), Vector2(-880, 600))
	tree.add_node("PistolAim", _anim("add/pistol_aim"), Vector2(-880, 670))

	# Add2, NON Blend2 filtrato: le clip qui sopra sono DELTA (q_rif^-1 x q_bersaglio,
	# authorate cosi' in Blender) e portano solo le track dell'upper body. La maschera
	# sta nella clip: un bone senza track non riceve nulla, quindi le gambe restano
	# alla locomozione per costruzione, senza filtri da mantenere.
	var hold := AnimationNodeAdd2.new()
	hold.sync = true
	tree.add_node("HoldAdd", hold, Vector2(-120, 120))

	# --- Layer 3: aim offset (ADDITIVO, pitch/yaw) ---------------------------
	# Sfera di mira continua in stile aim-offset: il grosso della posa lo mette qui
	# l'albero, l'errore residuo (dipendente dalla clip in corso) lo chiude
	# SpineAimModifier rimisurando la posa vera di ogni frame.
	tree.add_node("AimSpace", _aim_space(), Vector2(-380, 380))
	var aim_add := AnimationNodeAdd2.new()
	aim_add.sync = true
	tree.add_node("AimAdd", aim_add, Vector2(140, 120))

	# --- Layer 4: one-shot ADDITIVI (sparo, hit reaction) --------------------
	# MIX_MODE_ADD su clip delta: il rinculo/flinch si somma a qualunque cosa stiano
	# facendo locomozione, impugnatura e mira, e il fade scala il delta verso zero.
	var fire := AnimationNodeOneShot.new()
	fire.fadein_time = 0.02
	fire.fadeout_time = 0.10
	fire.mix_mode = AnimationNodeOneShot.MIX_MODE_ADD
	fire.sync = true
	tree.add_node("Fire", fire, Vector2(400, 120))

	# La clip di sparo la sceglie l'ARMA (WeaponAnimationSet.FirePose), non l'albero.
	# I nomi degli ingressi restano i nomi storici, cosi' i .tres non cambiano.
	# xfade 0: la richiesta arriva al cambio d'arma, mai a meta' colpo.
	var fire_pose := AnimationNodeTransition.new()
	fire_pose.input_count = 2
	fire_pose.set_input_name(0, "rifle_fire")
	fire_pose.set_input_name(1, "pistol_fire")
	fire_pose.xfade_time = 0.0
	tree.add_node("FirePose", fire_pose, Vector2(140, 360))
	tree.add_node("RifleFireClip", _anim("add/rifle_fire"), Vector2(-120, 340))
	tree.add_node("PistolFireClip", _anim("add/pistol_fire"), Vector2(-120, 410))

	# Hit reaction: stesso schema dello sparo, quattro direzioni. La direzione la
	# decide l'host (contratto CLAUDE.md §3: nel payload viaggia la DIREZIONE del
	# colpo, mai il danno) e CharacterAnimator la mappa sull'ingresso.
	var hit := AnimationNodeOneShot.new()
	hit.fadein_time = 0.02
	hit.fadeout_time = 0.10
	hit.mix_mode = AnimationNodeOneShot.MIX_MODE_ADD
	hit.sync = true
	tree.add_node("Hit", hit, Vector2(660, 120))

	var hit_pose := AnimationNodeTransition.new()
	hit_pose.input_count = 4
	hit_pose.set_input_name(0, "front")
	hit_pose.set_input_name(1, "back")
	hit_pose.set_input_name(2, "left")
	hit_pose.set_input_name(3, "right")
	hit_pose.xfade_time = 0.0
	tree.add_node("HitPose", hit_pose, Vector2(400, 380))
	tree.add_node("HitFront", _anim("add/hit_front"), Vector2(140, 430))
	tree.add_node("HitBack", _anim("add/hit_back"), Vector2(140, 500))
	tree.add_node("HitLeft", _anim("add/hit_left"), Vector2(140, 570))
	tree.add_node("HitRight", _anim("add/hit_right"), Vector2(140, 640))

	# --- Layer 5: one-shot ASSOLUTI full body --------------------------------
	# Il salto coinvolge tutto il corpo: nessun filtro, MIX blend.
	#
	# TimeScale davanti alla clip: `jump` e' un arco di salto completo di 1,03 s, ma
	# la durata reale del volo la decidono JumpVelocity e Gravity (2 * v / g). Senza
	# riscalare, il personaggio atterra mentre la clip e' ancora a mezz'aria.
	var jump := AnimationNodeOneShot.new()
	jump.fadein_time = 0.10
	jump.fadeout_time = 0.20
	jump.mix_mode = AnimationNodeOneShot.MIX_MODE_BLEND
	tree.add_node("Jump", jump, Vector2(920, 120))
	tree.add_node("JumpScale", AnimationNodeTimeScale.new(), Vector2(660, 380))
	tree.add_node("JumpClip", _anim("jump"), Vector2(400, 440))

	# Atterraggio: one-shot a TRE regimi decisi da CharacterAnimator. Oltre
	# HardLandingSpeed parte land_hard; fra Soft e Hard parte land_soft; sotto resta
	# la sola ammortizzazione del bacino. Il selettore e' un Transition con xfade 0:
	# la richiesta arriva PRIMA del one-shot, mai a clip in corso.
	var land := AnimationNodeOneShot.new()
	land.fadein_time = 0.05
	land.fadeout_time = 0.25
	land.mix_mode = AnimationNodeOneShot.MIX_MODE_BLEND
	tree.add_node("Land", land, Vector2(1180, 120))

	var land_pose := AnimationNodeTransition.new()
	land_pose.input_count = 2
	land_pose.set_input_name(0, "land_hard")
	land_pose.set_input_name(1, "land_soft")
	land_pose.xfade_time = 0.0
	tree.add_node("LandPose", land_pose, Vector2(920, 380))
	tree.add_node("LandHardClip", _anim("land_hard"), Vector2(660, 440))
	tree.add_node("LandSoftClip", _anim("land_soft"), Vector2(660, 510))

	# Scavalcamento: full body, root IN PLACE — la traiettoria della radice la
	# deforma il motion warping via codice, qui c'e' solo la sequenza di pose.
	var vault := AnimationNodeOneShot.new()
	vault.fadein_time = 0.08
	vault.fadeout_time = 0.20
	vault.mix_mode = AnimationNodeOneShot.MIX_MODE_BLEND
	tree.add_node("Vault", vault, Vector2(1440, 120))
	tree.add_node("VaultClip", _anim("vault_low"), Vector2(1180, 380))

	# --- Connessioni ---------------------------------------------------------
	tree.connect_node("MoveBlend", 0, "WalkSpace")
	tree.connect_node("MoveBlend", 1, "RunSpace")
	tree.connect_node("CrouchBlend", 0, "MoveBlend")
	tree.connect_node("CrouchBlend", 1, "CrouchSpace")
	tree.connect_node("AirBlend", 0, "CrouchBlend")
	tree.connect_node("AirBlend", 1, "FallClip")
	tree.connect_node("WeaponPose", 0, "RifleLowered")
	tree.connect_node("WeaponPose", 1, "RifleAim")
	tree.connect_node("WeaponPose", 2, "PistolIdle")
	tree.connect_node("WeaponPose", 3, "PistolAim")
	tree.connect_node("HoldAdd", 0, "AirBlend")
	tree.connect_node("HoldAdd", 1, "WeaponPose")
	tree.connect_node("AimAdd", 0, "HoldAdd")
	tree.connect_node("AimAdd", 1, "AimSpace")
	tree.connect_node("FirePose", 0, "RifleFireClip")
	tree.connect_node("FirePose", 1, "PistolFireClip")
	tree.connect_node("Fire", 0, "AimAdd")
	tree.connect_node("Fire", 1, "FirePose")
	tree.connect_node("HitPose", 0, "HitFront")
	tree.connect_node("HitPose", 1, "HitBack")
	tree.connect_node("HitPose", 2, "HitLeft")
	tree.connect_node("HitPose", 3, "HitRight")
	tree.connect_node("Hit", 0, "Fire")
	tree.connect_node("Hit", 1, "HitPose")
	tree.connect_node("JumpScale", 0, "JumpClip")
	tree.connect_node("Jump", 0, "Hit")
	tree.connect_node("Jump", 1, "JumpScale")
	tree.connect_node("LandPose", 0, "LandHardClip")
	tree.connect_node("LandPose", 1, "LandSoftClip")
	tree.connect_node("Land", 0, "Jump")
	tree.connect_node("Land", 1, "LandPose")
	tree.connect_node("Vault", 0, "Land")
	tree.connect_node("Vault", 1, "VaultClip")
	tree.connect_node("output", 0, "Vault")

	var err := ResourceSaver.save(tree, OUT_PATH)
	if err != OK:
		printerr("Salvataggio fallito: %d" % err)
		quit(1)
		return

	print("BlendTree salvato in %s" % OUT_PATH)
	print("  nodi: %s" % ", ".join(tree.get_node_list()))
	quit(0)
