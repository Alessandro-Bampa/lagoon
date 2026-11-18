extends SceneTree
## Collaudo del nuoto (skill water-ocean §8, vehicles-boats), senza editor.
##
##   Godot --path . --resolution 1280x720 --script tools/playtest_swim.gd -- <cartella_output> [test|laguna|all]
##   Godot --path . --resolution 960x540 --script tools/playtest_swim.gd -- <cartella_output> net-host
##   Godot --path . --resolution 960x540 --script tools/playtest_swim.gd -- <cartella_output> net-client
##
##   test    = livello di prova (bacino a x 20..80): tuffo dal molo, galleggiamento, nuoto, immersione,
##             mare poco mosso e in tempesta (laguna e mare aperto), arma bloccata, risalita su riva
##             bassa, su scaletta del molo e a bordo della barca dalla sua scaletta.
##   laguna  = mappa vera: si cerca acqua profonda vicino al punto di nascita, ci si galleggia col mare
##             poco mosso e in tempesta, poi si nuota verso riva finche' non si torna a camminare.
##   net-*   = due processi ENet locali: host e client nuotano entrambi, ognuno controlla che l'avatar
##             dell'altro risulti a nuoto (SyncSwimming replicato) e che il suo corpo usi il ramo di nuoto.
##             Avviare prima net-host, poi net-client entro ~10 s.
##
## Ogni riga "[swim] OK"/"[swim] FALLITO" e' un controllo; con --headless gira senza immagini.

var out_dir := ""
var mode := "all"
var shots := 0
var failures := 0


func _init() -> void:
	var args := OS.get_cmdline_user_args()
	out_dir = args[0] if args.size() > 0 else "user://"
	mode = args[1] if args.size() > 1 else "all"
	_run.call_deferred()


func wait(seconds: float) -> void:
	await create_timer(seconds).timeout


func snap(name: String) -> void:
	if DisplayServer.get_name() == "headless":
		print("[swim] (headless) ", name)
		return
	await RenderingServer.frame_post_draw
	var image := root.get_viewport().get_texture().get_image()
	shots += 1
	var path := "%s/%02d_%s.png" % [out_dir, shots, name]
	image.save_png(path)
	print("[swim] ", path)


func check(ok: bool, what: String) -> void:
	if ok:
		print("[swim] OK ", what)
	else:
		failures += 1
		print("[swim] FALLITO ", what)


func _run() -> void:
	match mode:
		"test":
			await run_test_level()
		"laguna":
			await run_laguna()
		"net-host":
			await run_net(true)
		"net-client":
			await run_net(false)
		_:
			await run_test_level()
			await run_laguna()
	print("[swim] fine, controlli falliti: ", failures)
	quit(1 if failures > 0 else 0)


# ======================================================================================
#  Utilita'
# ======================================================================================

func start(scene: String, host: bool) -> Node:
	var main: Node = load(scene).instantiate()
	root.add_child(main)
	current_scene = main
	await wait(0.5)
	main.get_node("UI/MainMenu").get_node("%HostLocalButton" if host else "%JoinLocalButton").pressed.emit()
	await wait(2.0 if host else 4.0)
	return main


func own_player(main: Node) -> CharacterBody3D:
	return main.get_node("Players").get_node_or_null(str(main.multiplayer.get_unique_id()))


func water_of(main: Node) -> Node:
	return main.get_tree().get_first_node_in_group("water")


func teleport(player: CharacterBody3D, pos: Vector3) -> void:
	player.global_position = pos
	player.velocity = Vector3.ZERO


## Gira lo sguardo (e quindi WASD) verso la direzione orizzontale data.
func face(player: Node, dir: Vector3) -> void:
	var cam: Node = player.get_node("PlayerCamera")
	var target := atan2(dir.x, dir.z)
	cam.call("AddYaw", angle_difference(float(cam.get("LookYaw")), target))


func eye_minus_surface(player: Node, water: Node) -> float:
	var cam: Node3D = player.get_node("PlayerCamera")
	return cam.global_position.y - float(water.call("HeightAt", cam.global_position))


## Statistiche di galleggiamento per `seconds`: quota degli occhi sopra l'onda, sempre a nuoto?
func float_stats(player: Node, water: Node, seconds: float) -> Dictionary:
	var lo := 1e9
	var hi := -1e9
	var sum := 0.0
	var n := 0
	var always := true
	var start_pos: Vector3 = player.global_position
	var max_jump := 0.0
	var last: Vector3 = start_pos
	var t := 0.0
	while t < seconds:
		await wait(0.1)
		t += 0.1
		var d := eye_minus_surface(player, water)
		lo = min(lo, d)
		hi = max(hi, d)
		sum += d
		n += 1
		always = always and bool(player.get("SyncSwimming"))
		var p: Vector3 = player.global_position
		max_jump = max(max_jump, p.distance_to(last))
		last = p
	return {"min": lo, "max": hi, "mean": sum / max(n, 1), "always": always, "max_step": max_jump,
		"drift": player.global_position.distance_to(start_pos)}


func print_stats(label: String, s: Dictionary, water: Node) -> void:
	print("[swim] %s: occhi sopra l'onda min %.2f max %.2f media %.2f m, sempre a nuoto %s, passo max %.2f m/0,1 s, deriva %.1f m, mare %.1f" % [
		label, s["min"], s["max"], s["mean"], s["always"], s["max_step"], s["drift"], float(water.get("SeaState"))])


## Spazio contro un bordo: si nuota verso `dir` tenendo avanti e si preme salto.
func climb(player: Node, dir: Vector3) -> void:
	face(player, dir)
	Input.action_press("move_up")
	await wait(0.6)
	Input.action_press("jump")
	await wait(0.1)
	Input.action_release("jump")
	await wait(0.3)
	Input.action_release("move_up")
	await wait(1.8)


# ======================================================================================
#  Livello di prova
# ======================================================================================

func run_test_level() -> void:
	var main := await start("res://world/scenes/Main.tscn", true)
	var player := own_player(main)
	var water := water_of(main)
	var weapon: Node = player.get_node("Weapon")
	check(player != null and water != null, "giocatore e acqua presenti")
	water.call("SetSeaState", 1.0, 0.0)
	await wait(0.5)

	# Tuffo dal molo nel bacino: si entra in acqua, si scende un po' e si torna a galla.
	teleport(player, Vector3(26.0, 2.3, 8.0))
	face(player, Vector3(1, 0, 0))
	Input.action_press("move_up")
	await wait(0.8)
	Input.action_release("move_up")
	var deepest := 0.0
	for i in 15:
		await wait(0.1)
		deepest = min(deepest, eye_minus_surface(player, water))
	print("[swim] tuffo: occhi fino a %.2f m sotto l'onda" % -deepest)
	await snap("test_tuffo")
	await wait(2.5)
	check(bool(player.get("SyncSwimming")), "dopo il tuffo si nuota")
	check(player.global_position.x > 24.0, "nessun rientro al molo (x=%.1f)" % player.global_position.x)

	# Galleggiamento col mare poco mosso.
	teleport(player, Vector3(45.0, 0.5, 10.0))
	await wait(2.0)
	var s := await float_stats(player, water, 4.0)
	print_stats("poco mosso", s, water)
	check(s["always"] and absf(s["mean"] - 0.2) < 0.15 and s["min"] > -0.25, "galleggia con gli occhi ~0,2 m sopra l'onda")
	await snap("test_galleggia")

	# Nuoto in avanti, poi veloce.
	face(player, Vector3(0, 0, -1))
	var z0 := player.global_position.z
	Input.action_press("move_up")
	await wait(3.0)
	var v := (z0 - player.global_position.z) / 3.0
	Input.action_press("sprint")
	await wait(2.0)
	var vel: Vector3 = player.velocity
	Input.action_release("sprint")
	Input.action_release("move_up")
	print("[swim] nuoto: %.2f m/s medi (con l'avvio), veloce %.2f m/s" % [v, Vector2(vel.x, vel.z).length()])
	check(v > 0.8 and v < float(player.get("SwimSpeed")) + 0.6, "nuoto piu' lento della camminata")
	await snap("test_nuoto")

	# Arma: col nuoto non si spara.
	weapon.call("RequestHold", 7)
	await wait(1.4)
	var ammo := int(weapon.get("MagazineAmmo"))
	var cam: Camera3D = player.get_node("PlayerCamera")
	weapon.call("RequestFire", cam.global_position - cam.global_transform.basis.z * 20.0)
	await wait(0.3)
	check(int(weapon.get("MagazineAmmo")) == ammo, "a nuoto il tiro e' rifiutato (caricatore %d -> %d)" % [ammo, int(weapon.get("MagazineAmmo"))])

	# Immersione e risalita.
	Input.action_press("crouch")
	await wait(1.5)
	var dive := eye_minus_surface(player, water)
	await snap("test_immersione")
	Input.action_release("crouch")
	print("[swim] immersione: occhi %.2f m sotto l'onda" % -dive)
	check(dive < -1.0, "accovacciarsi immerge")
	Input.action_press("jump")
	await wait(1.5)
	Input.action_release("jump")
	await wait(1.5)
	check(absf(eye_minus_surface(player, water) - 0.2) < 0.3, "si torna a galla")

	# Tempesta: nel bacino (esposizione laguna) e in mare aperto.
	water.call("SetSeaState", 4.0, 0.0)
	await wait(2.0)
	s = await float_stats(player, water, 8.0)
	print_stats("tempesta, laguna", s, water)
	check(s["always"] and s["min"] > -0.6 and s["max_step"] < 1.0, "tempesta in laguna: si resta a galla, senza scatti")
	await snap("test_tempesta_laguna")
	water.set("DefaultExposure", 1.0)
	await wait(2.0)
	s = await float_stats(player, water, 10.0)
	print_stats("tempesta, mare aperto", s, water)
	check(s["always"] and s["min"] > -1.0 and s["max_step"] < 1.0, "tempesta in mare aperto: si resta a galla, senza scatti")
	await snap("test_tempesta_mare")
	water.set("DefaultExposure", 0.45)
	water.call("SetSeaState", 1.0, 0.0)
	await wait(2.0)

	# Risalita su riva bassa: il bordo del pavimento (quota 0 = livello medio) a x = 20.
	teleport(player, Vector3(21.6, -0.4, 14.0))
	await wait(1.5)
	await climb(player, Vector3(-1, 0, 0))
	print("[swim] riva bassa: pos %s, a nuoto %s" % [player.global_position, player.get("SyncSwimming")])
	check(not bool(player.get("SyncSwimming")) and player.global_position.x < 20.2 and player.global_position.y > 0.8,
		"risalita sulla riva bassa")
	await snap("test_riva")

	# Muro alto senza scaletta: il molo (1,25 m) lontano dalla scaletta non si sale.
	teleport(player, Vector3(27.6, -0.4, 2.3))
	await wait(1.5)
	await climb(player, Vector3(-1, 0, 0))
	check(bool(player.get("SyncSwimming")), "senza scaletta il molo (1,25 m) non si sale")

	# Scaletta del molo (z = -2,3).
	teleport(player, Vector3(27.6, -0.4, -2.3))
	await wait(1.5)
	await climb(player, Vector3(-1, 0, 0))
	print("[swim] scaletta del molo: pos %s, a nuoto %s" % [player.global_position, player.get("SyncSwimming")])
	check(not bool(player.get("SyncSwimming")) and player.global_position.y > 2.0, "risalita dalla scaletta del molo")
	await snap("test_scaletta_molo")

	# A bordo del barchino dalla sua scaletta (sullo specchio di poppa, a dritta) col mare mosso.
	var boat: RigidBody3D = main.get_node("TestLevel/Barchino")
	water.call("SetSeaState", 2.0, 0.0)
	# Non ci sono cime d'ormeggio: le fasi in tempesta l'hanno spinta altrove (anche sul molo).
	boat.global_transform = Transform3D(Basis.IDENTITY, Vector3(31.0, 0.1, 3.0))
	boat.linear_velocity = Vector3.ZERO
	boat.angular_velocity = Vector3.ZERO
	await wait(1.5)
	var ladder: Node3D = null
	for n in boat.find_children("*", "Node3D", true, false):
		if n.is_in_group("swim_ladder"):
			ladder = n
			break
	var forward: Vector3 = -boat.global_transform.basis.z
	var ladder_pos: Vector3 = ladder.global_position if ladder else boat.global_transform * Vector3(0.55, 0.0, 2.3)
	teleport(player, Vector3(ladder_pos.x, boat.global_position.y - 0.4, ladder_pos.z) - forward * 0.7)
	await wait(1.5)
	# DEBUG temporaneo
	var space := (player as Node3D).get_world_3d().direct_space_state
	print("[swim] DBG ladder ", ladder, " pos ", ladder_pos, " player ", player.global_position, " boat ", boat.global_position, " fwd ", forward)
	for h in [-0.3, -0.1, 0.1, 0.3, 0.5]:
		var from: Vector3 = Vector3(player.global_position.x, h, player.global_position.z)
		var q := PhysicsRayQueryParameters3D.create(from, from + forward * 2.0, 39)
		var hit := space.intersect_ray(q)
		print("[swim] DBG ray y=%.1f -> %s" % [h, (str(hit.get("position")) + " " + str(hit.get("collider"))) if hit else "niente"])
	await climb(player, forward)
	await wait(0.5)
	print("[swim] barca: ancora %d, a nuoto %s, quota %.2f (ponte %.2f)" % [int(player.get("SyncAnchorId")),
		player.get("SyncSwimming"), player.global_position.y - 1.0, (boat.global_transform * Vector3(0, -0.05, 0)).y])
	check(int(player.get("SyncAnchorId")) == int(boat.get("VehicleId")) and not bool(player.get("SyncSwimming")),
		"a bordo della barca dalla scaletta")
	await snap("test_barca")

	# Corpo in terza persona: manichini pilotati a mano come in playtest_fps.gd (tp), a galla nel
	# bacino. L'origine del rig sta ai piedi nominali: superficie - (EyeHeight - SwimEyeAboveSurface).
	water.call("SetSeaState", 0.5, 0.0)
	await wait(0.5)
	var lift := float(player.get("EyeHeight")) - float(player.get("SwimEyeAboveSurface"))
	var cases := [["fermo", Vector2.ZERO], ["bracciate", Vector2(0, 1.6)], ["di_lato", Vector2(1.6, 0)]]
	var rigs: Array[Node3D] = []
	var base := Vector3(50.0, 0.0, -12.0)
	for i in cases.size():
		var rig: Node3D = load("res://animation/scenes/CharacterRig.tscn").instantiate()
		main.add_child(rig)
		var p := base + Vector3(i * 2.5, 0, 0)
		p.y = float(water.call("HeightAt", p)) - lift
		rig.global_position = p
		rig.set("SwimBodyLift", lift)
		rig.set("Swimming", true)
		rig.set("LocalVelocity", cases[i][1])
		rigs.append(rig)
	var tp := Camera3D.new()
	main.add_child(tp)
	tp.fov = 45.0
	tp.global_position = base + Vector3(2.5, 1.6, 6.5)
	tp.look_at(base + Vector3(2.5, 0.0, 0.0))
	tp.current = true
	await wait(1.8)
	await snap("test_tp_nuoto")
	tp.global_position = base + Vector3(-3.5, 1.2, 0.5)
	tp.look_at(base + Vector3(2.5, 0.0, 0.0))
	await wait(0.4)
	await snap("test_tp_nuoto_fianco")
	for rig in rigs:
		rig.queue_free()
	tp.queue_free()

	water.call("SetSeaState", 1.0, 0.0)
	main.queue_free()
	await wait(0.5)


# ======================================================================================
#  Mappa vera
# ======================================================================================

## Cerca vicino a `origin` un punto con almeno `min_depth` d'acqua sotto il livello medio.
func find_water(terrain: Node, water: Node, origin: Vector3, min_depth: float, radius: float,
		min_exposure: float = 0.0) -> Vector3:
	var data: Object = terrain.get("data")
	var level := float(water.get("WaterLevel"))
	var r := 10.0
	while r <= radius:
		for k in 36:
			var a := TAU * k / 36.0
			var p := origin + Vector3(cos(a), 0, sin(a)) * r
			var h := float(data.call("get_height", p))
			if not is_nan(h) and level - h > min_depth \
					and float(water.call("ExposureAt", p.x, p.z)) >= min_exposure:
				return Vector3(p.x, h, p.z)
		r += 10.0
	return Vector3.INF


func run_laguna() -> void:
	var main := await start("res://world/scenes/MainLaguna.tscn", true)
	var player := own_player(main)
	var water := water_of(main)
	var terrain: Node = main.get_node("Laguna/Terrain3D")
	await wait(3.0)
	water.call("SetSeaState", 1.0, 0.0)

	var spot := find_water(terrain, water, player.global_position, 3.0, 400.0)
	check(spot != Vector3.INF, "acqua profonda vicino al punto di nascita")
	if spot == Vector3.INF:
		main.queue_free()
		return
	print("[swim] laguna: acqua a %s, fondale %.2f, livello %.2f" % [spot, spot.y, float(water.get("WaterLevel"))])
	teleport(player, Vector3(spot.x, float(water.get("WaterLevel")) + 0.3, spot.z))
	await wait(4.0)
	var s := await float_stats(player, water, 4.0)
	print_stats("laguna poco mosso", s, water)
	check(s["always"] and absf(s["mean"] - 0.2) < 0.15, "laguna: galleggia col mare poco mosso")
	await snap("laguna_poco_mosso")

	water.call("SetSeaState", 4.0, 0.0)
	await wait(2.0)
	s = await float_stats(player, water, 6.0)
	print_stats("laguna tempesta, rio (esposizione %.2f)" % float(water.call("ExposureAt", spot.x, spot.z)), s, water)
	check(s["always"] and s["min"] > -0.8 and s["max_step"] < 1.0, "laguna: a galla in tempesta nel rio")

	# Laguna aperta (esposizione >= 0,45): qui la tempesta fa onde vere.
	var open := find_water(terrain, water, player.global_position, 2.0, 2500.0, 0.45)
	check(open != Vector3.INF, "laguna aperta entro 2,5 km")
	if open != Vector3.INF:
		teleport(player, Vector3(open.x, float(water.get("WaterLevel")) + 0.3, open.z))
		await wait(6.0)
		s = await float_stats(player, water, 10.0)
		print_stats("laguna tempesta, aperta %s (esposizione %.2f)" % [open, float(water.call("ExposureAt", open.x, open.z))], s, water)
		check(s["always"] and s["min"] > -0.8 and s["max_step"] < 1.0, "laguna: a galla in tempesta in laguna aperta")
		await snap("laguna_tempesta")
		teleport(player, Vector3(spot.x, float(water.get("WaterLevel")) + 0.3, spot.z))
		await wait(4.0)
	water.call("SetSeaState", 1.0, 0.0)
	await wait(1.0)

	# Verso riva: si nuota verso il punto di terra piu' vicino finche' non si torna a camminare.
	var data: Object = terrain.get("data")
	var level := float(water.get("WaterLevel"))
	var shore := Vector3.INF
	var best := 1e9
	for k in 72:
		var a := TAU * k / 72.0
		for r in range(5, 120, 5):
			var p := player.global_position + Vector3(cos(a), 0, sin(a)) * r
			var h := float(data.call("get_height", p))
			if not is_nan(h) and h > level + 0.05:
				if r < best:
					best = r
					shore = p
				break
	check(shore != Vector3.INF, "riva entro 120 m")
	if shore != Vector3.INF:
		print("[swim] laguna: riva a %.0f m" % best)
		face(player, shore - player.global_position)
		Input.action_press("move_up")
		var t := 0.0
		while t < 90.0 and bool(player.get("SyncSwimming")):
			await wait(0.5)
			t += 0.5
			if is_equal_approx(fmod(t, 5.0), 0.0):
				var col: KinematicCollision3D = player.get_last_slide_collision()
				print("[swim] laguna t=%.0f pos %s vel %s muro %s collisione %s normale %s" % [t, player.global_position,
					player.velocity, player.is_on_wall(), col.get_collider().get_path() if col else "-",
					col.get_normal() if col else Vector3.ZERO])
			face(player, shore - player.global_position)
			# Un bordo verticale (fondamenta) si sale col salto; una riva in pendenza si risale camminando.
			if player.global_position.distance_to(Vector3(shore.x, player.global_position.y, shore.z)) < 2.5:
				Input.action_press("jump")
				await wait(0.1)
				Input.action_release("jump")
		Input.action_release("move_up")
		await wait(1.0)
		print("[swim] laguna: dopo %.1f s a nuoto %s, pos %s" % [t, player.get("SyncSwimming"), player.global_position])
		check(not bool(player.get("SyncSwimming")), "laguna: si esce dall'acqua sulla riva")
		await snap("laguna_riva")
	main.queue_free()
	await wait(0.5)


# ======================================================================================
#  Due istanze ENet
# ======================================================================================

func run_net(host: bool) -> void:
	var main := await start("res://world/scenes/Main.tscn", host)
	var players := main.get_node("Players")
	var t := 0.0
	while players.get_child_count() < 2 and t < 30.0:
		await wait(0.5)
		t += 0.5
	check(players.get_child_count() >= 2, "due giocatori in partita")
	var me := own_player(main)
	var water := water_of(main)
	var other: Node = null
	for p in players.get_children():
		if p != me:
			other = p
	if other == null:
		main.queue_free()
		return

	# Ciascuno si tuffa nel bacino, a qualche metro dall'altro e di fronte a lui.
	if host:
		water.call("SetSeaState", 2.0, 0.0)
	# Lontano dalle casse galleggianti (x 40/46/60), che coprirebbero la vista.
	var mine := Vector3(52.0, 0.5, -2.0) if host else Vector3(52.0, 0.5, 4.0)
	teleport(me, mine)
	face(me, Vector3(0, 0, 1) if host else Vector3(0, 0, -1))
	await wait(4.0)
	# L'altro processo puo' essere ancora in partenza: si aspetta che si tuffi (fino a 20 s).
	t = 0.0
	while not bool(other.get("SyncSwimming")) and t < 20.0:
		await wait(0.5)
		t += 0.5
	await wait(1.0)
	check(bool(me.get("SyncSwimming")), "il proprio avatar nuota")
	check(bool(other.get("SyncSwimming")), "l'avatar dell'altro risulta a nuoto (replicato)")
	var rig: Node = other.get_node("Visual/CharacterRig")
	check(bool(rig.get("Swimming")), "il corpo dell'altro usa il ramo di nuoto")
	check(int(other.get("Mode")) == 2, "Mode dell'altro = Swimming")
	print("[swim] net %s: altro a %s, occhi sopra l'onda (proprio) %.2f" % ["host" if host else "client",
		other.global_position, eye_minus_surface(me, water)])
	await snap("net_%s_vede_altro" % ("host" if host else "client"))

	# Il client nuota verso l'host: l'host deve vederlo arrivare con le bracciate.
	if not host:
		Input.action_press("move_up")
	await wait(2.0)
	await snap("net_%s_bracciate" % ("host" if host else "client"))
	Input.action_release("move_up")
	await wait(4.0)
	main.queue_free()
	await wait(0.5)
