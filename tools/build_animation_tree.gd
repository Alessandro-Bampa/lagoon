# Genera il BlendTree del personaggio e lo salva come risorsa.
#
# ARCHITETTURA A LAYER (rifatta rispetto alla versione a stance):
#   1. Locomozione FULL BODY, agnostica dall'arma: walk/run/crouch/aria. Nessun set
#      armato: l'arma non cambia come si cammina.
#   2. Impugnatura: posa ASSOLUTA delle sole braccia (hold/rifle_* e hold/pistol*),
#      sostituita alla locomozione da un Blend2 FILTRATO sulle otto ossa delle
#      braccia. Non e' additiva, e il perche' e' misurato: vedi HOLD_MASK piu' sotto
#      e l'intestazione di tools/build_weapon_poses.gd.
#   3. Aim offset: BlendSpace2D di 5 pose additive (centro/su/giu'/sx/dx) pilotato
#      da yaw/pitch della mira, sommato sopra impugnatura e locomozione.
#   4. One-shot additivi: sparo e hit reaction (delta, MIX_MODE_ADD).
#   5. One-shot assoluti full body: salto, atterraggio, scavalcamento.
#   I layer procedurali (SpineAim, FootIk, GripRig) NON stanno qui: sono
#   SkeletonModifier3D che girano dopo l'albero.
#
# Le clip generate vivono in TRE librerie separate, tutte montate su AnimationTree:
#   `add/`    delta additivi      (animation/resources/AdditiveClips.tres)
#   `hold/`   pose d'impugnatura  (animation/resources/WeaponHoldPoses.tres)
#   `crouch/` locomozione accovacciata con le braccia animate (animation/resources/CrouchClips.tres)
# Nessuna delle tre passa da Blender: il perche' sta nell'intestazione dei rispettivi
# tool. Rigenerare l'albero dopo aver rigenerato le clip.
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
# non c'entra: quella la sovrappone il layer d'impugnatura.
const IDLE_CLIP := "idle_neutral"

# Le ossa che il layer d'impugnatura SOSTITUISCE. Deve combaciare con ARM_BONES di
# tools/build_weapon_poses.gd.
#
# Perche' una maschera e non un Add2. La semantica additiva di Godot e'
# `risultato = Base x (Rest^-1 x Chiave)`: un delta costante applica al braccio una
# rotazione RELATIVA, quindi riproduce la presa solo quando la base coincide con la
# clip di riferimento. Misurato sul rig — distanza fra le due mani, che reggendo un
# fucile DEVE valere 0,39 m: 0,39 su idle_neutral (base = riferimento), 0,58 su
# walk_fwd, mani all'altezza del bacino su crouch_fwd. L'oscillazione delle braccia
# della camminata restava nella base e si sommava alla presa.
#
# La presa e' un VINCOLO (due mani sulla stessa arma), non uno scarto: si esprime con
# una posa assoluta e una maschera. Rachide, bacino e gambe NON sono nella maschera,
# quindi il busto continua a respirare, a oscillare in corsa e ad accovacciarsi, e le
# braccia — figlie dello stesso Spine2 — lo seguono in blocco tenendo la presa.
const HOLD_MASK := [
	"LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand",
	"RightShoulder", "RightArm", "RightForeArm", "RightHand",
]


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
#
# `separator` esiste perche' il crouch non pesca piu' dal .glb ma dalla libreria
# `crouch` generata da tools/build_crouch_clips.gd, dove le clip si chiamano
# `crouch/fwd` e non `crouch_fwd`.
func _directional_space(idle: String, prefix: String, radius: float,
		separator := "_") -> AnimationNodeBlendSpace2D:
	var space := AnimationNodeBlendSpace2D.new()
	space.min_space = Vector2(-radius, -radius)
	space.max_space = Vector2(radius, radius)
	space.snap = Vector2(0.1, 0.1)
	space.blend_mode = AnimationNodeBlendSpace2D.BLEND_MODE_INTERPOLATED
	space.auto_triangles = true
	space.add_blend_point(_anim(idle), Vector2(0, 0), -1, "idle")
	for entry in [["fwd", Vector2(0, radius)], ["back", Vector2(0, -radius)],
			["left", Vector2(-radius, 0)], ["right", Vector2(radius, 0)]]:
		var clip: String = prefix + separator + str(entry[0])
		space.add_blend_point(_anim(clip), entry[1], -1, clip)
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
	#
	# Le clip NON sono piu' quelle del .glb: le cinque `crouch_*` di Mixamo hanno le
	# braccia ferme (una sola chiave di rotazione per osso), quindi da disarmati le mani
	# restavano immobili lungo il corpo mentre le gambe camminavano. La libreria `crouch`
	# le rigenera con l'oscillazione delle braccia trapiantata dalle clip in piedi
	# (tools/build_crouch_clips.gd): stesso corpo, stessa fase, braccia vive.
	tree.add_node("CrouchSpace",
		_directional_space("crouch/idle", "crouch", CROUCH_SPEED, "/"), Vector2(-880, 400))

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

	# --- Layer 2: impugnatura (posa ASSOLUTA delle braccia, mascherata) ------
	# Transition e non BlendSpace: fra le pose d'arma non esiste una via di mezzo
	# sensata da interpolare, si passa dall'una all'altra (xfade 0.15, che fra due pose
	# di braccia e' un crossfade normale).
	#
	# QUATTRO pose, due per famiglia: porto rilassato e mira. La mira e' uno STATO
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
	tree.add_node("RifleLowered", _anim("hold/rifle_lowered"), Vector2(-880, 460))
	tree.add_node("RifleAim", _anim("hold/rifle_aim"), Vector2(-880, 530))
	tree.add_node("PistolIdle", _anim("hold/pistol"), Vector2(-880, 600))
	tree.add_node("PistolAim", _anim("hold/pistol_aim"), Vector2(-880, 670))

	# Blend2 FILTRATO sulle otto ossa delle braccia (vedi HOLD_MASK): l'ingresso 1
	# SOSTITUISCE la locomozione su quelle ossa e non tocca nient'altro. Doppia
	# sicurezza, come per le clip delta: le pose d'impugnatura non hanno comunque
	# track fuori dalla maschera, quindi anche un filtro allargato per sbaglio non
	# potrebbe intaccare gambe e rachide.
	#
	# sync = true e' OBBLIGATORIO su un nodo filtrato: con sync = false l'ingresso a
	# peso 0 resta visibile sulle parti che il filtro non copre e la locomozione si
	# congela (skill character-animation §1.7).
	var hold := AnimationNodeBlend2.new()
	hold.sync = true
	hold.filter_enabled = true
	for bone in HOLD_MASK:
		hold.set_filter_path("%s:%s" % [SKELETON, bone], true)
	tree.add_node("HoldMask", hold, Vector2(-120, 120))

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

	# Parkour: full body, root IN PLACE — la traiettoria della radice la deforma il
	# motion warping via codice, qui c'e' solo la sequenza di pose.
	#
	# Un solo one-shot per due manovre, con un Transition sotto a scegliere la clip:
	# stesso schema di Land + LandPose. Due one-shot separati costerebbero un secondo
	# nodo in coda all'albero per un evento che non puo' MAI sovrapporsi a se stesso —
	# non ci si arrampica mentre si scavalca.
	var vault := AnimationNodeOneShot.new()
	vault.fadein_time = 0.08
	vault.fadeout_time = 0.20
	vault.mix_mode = AnimationNodeOneShot.MIX_MODE_BLEND
	tree.add_node("Vault", vault, Vector2(1440, 120))

	var vault_pose := AnimationNodeTransition.new()
	vault_pose.input_count = 2
	vault_pose.set_input_name(0, "vault_low")
	vault_pose.set_input_name(1, "mantle_high")
	vault_pose.xfade_time = 0.0
	tree.add_node("VaultPose", vault_pose, Vector2(1180, 380))
	tree.add_node("VaultClip", _anim("vault_low"), Vector2(920, 520))
	tree.add_node("MantleClip", _anim("mantle_high"), Vector2(920, 590))

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
	tree.connect_node("HoldMask", 0, "AirBlend")
	tree.connect_node("HoldMask", 1, "WeaponPose")
	tree.connect_node("AimAdd", 0, "HoldMask")
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
	tree.connect_node("VaultPose", 0, "VaultClip")
	tree.connect_node("VaultPose", 1, "MantleClip")
	tree.connect_node("Vault", 0, "Land")
	tree.connect_node("Vault", 1, "VaultPose")
	tree.connect_node("output", 0, "Vault")

	var err := ResourceSaver.save(tree, OUT_PATH)
	if err != OK:
		printerr("Salvataggio fallito: %d" % err)
		quit(1)
		return

	print("BlendTree salvato in %s" % OUT_PATH)
	print("  nodi: %s" % ", ".join(tree.get_node_list()))
	quit(0)
