extends SceneTree
## Collaudo dell'acqua (skill water-ocean), senza editor e senza rete.
##
##   Godot --path . --resolution 1280x720 --script tools/playtest_water.gd -- <cartella_output> [view|physics|all]
##
##   view    = mappa della laguna: canale, velma con marea bassa/alta, mare in burrasca, sott'acqua,
##             vista dall'alto. Uno screenshot per passo.
##   perf    = costo GPU dell'acqua (ms) per qualita', in canale e in mare, contro l'acqua nascosta.
##   physics = livello di prova: quota e beccheggio della barca e delle casse col mare poco mosso e
##             in tempesta (numeri stampati), piu' uno screenshot.
##
## Con `--headless` gira lo stesso copione senza immagini (lo shader non viene compilato: per quello
## serve la finestra).

var out_dir := ""
var mode := "all"
var shots := 0


func _init() -> void:
	var args := OS.get_cmdline_user_args()
	out_dir = args[0] if args.size() > 0 else "user://"
	mode = args[1] if args.size() > 1 else "all"
	_run.call_deferred()


func wait(seconds: float) -> void:
	await create_timer(seconds).timeout


func snap(name: String) -> void:
	if DisplayServer.get_name() == "headless":
		print("[water] (headless) ", name)
		return
	await RenderingServer.frame_post_draw
	var image := root.get_viewport().get_texture().get_image()
	shots += 1
	var path := "%s/%02d_%s.png" % [out_dir, shots, name]
	image.save_png(path)
	print("[water] ", path)


func place(cam: Camera3D, pos: Vector3, yaw_deg: float, pitch_deg: float) -> void:
	cam.global_position = pos
	cam.global_rotation = Vector3(deg_to_rad(pitch_deg), deg_to_rad(yaw_deg), 0.0)


func _run() -> void:
	if mode == "view" or mode == "all":
		await run_view()
	if mode == "perf":
		await run_perf()
	if mode == "physics" or mode == "all":
		await run_physics()
	quit(0)


func run_view() -> void:
	var level: Node = load("res://world/maps/laguna/Laguna.tscn").instantiate()
	root.add_child(level)
	var cam: Camera3D = level.get_node("PreviewCamera")
	var water: Node = level.get_node("Water")
	cam.make_current()

	# Canale vicino a San Marco: laguna, poco mosso.
	water.call("SetSeaState", 1.0, 0.0)
	water.set("TideClockHours", 3.0)
	place(cam, Vector3(-6920, 2.5, -7224), 60.0, -12.0)
	await wait(4.0)
	print("[water] canale: esposizione %.2f, livello %.2f" % [water.call("ExposureAt", -6920.0, -7224.0), water.get("WaterLevel")])
	await snap("canale_poco_mosso")
	water.call("SetSeaState", 3.0, 0.0)
	await wait(1.5)
	await snap("canale_agitato")

	# Velma: con la bassa emerge, con l'alta torna sommersa.
	water.call("SetSeaState", 1.0, 0.0)
	water.set("TideClockHours", 6.2)
	place(cam, Vector3(-5752, 2.0, -7992), 30.0, -20.0)
	await wait(4.0)
	print("[water] velma, bassa: livello %.2f" % water.get("WaterLevel"))
	await snap("velma_bassa")
	water.set("TideClockHours", 0.0)
	await wait(1.0)
	print("[water] velma, alta: livello %.2f" % water.get("WaterLevel"))
	await snap("velma_alta")

	# Mare aperto in burrasca.
	water.call("SetSeaState", 4.0, 0.0)
	place(cam, Vector3(8000, 5.0, 8000), 120.0, -8.0)
	await wait(4.0)
	print("[water] mare: esposizione %.2f" % water.call("ExposureAt", 8000.0, 8000.0))
	await snap("mare_tempesta")
	water.call("SetSeaState", 2.0, 0.0)
	await wait(1.0)
	await snap("mare_mosso")

	# Dall'alto, per vedere griglia e LOD.
	water.call("SetSeaState", 3.0, 0.0)
	place(cam, Vector3(8000, 80.0, 8000), 120.0, -35.0)
	await wait(1.0)
	await snap("mare_dall_alto")

	# Sott'acqua.
	water.call("SetSeaState", 1.0, 0.0)
	place(cam, Vector3(8000, -2.0, 8000), 120.0, 10.0)
	await wait(1.5)
	await snap("sottacqua")

	level.queue_free()
	await wait(0.5)


func run_physics() -> void:
	var level: Node = load("res://world/scenes/Levels/TestLevel.tscn").instantiate()
	root.add_child(level)
	var water: Node = level.get_node("Lagoon/Water")
	var boat: RigidBody3D = level.get_node("Barchino")
	var crate: RigidBody3D = level.get_node("Lagoon/CrateA")
	var cam := Camera3D.new()
	level.add_child(cam)
	cam.make_current()
	place(cam, Vector3(38, 4.0, 14), 30.0, -12.0)

	# Terzo giro: mare aperto (esposizione 1) in tempesta, barca a tutta in cerchio.
	for state in [1.5, 4.0, 4.1]:
		if state > 4.0:
			state = 4.0
			water.set("DefaultExposure", 1.0)
			# Acqua ovunque: il bacino del livello di prova e' 60 x 60 m, la barca ne uscirebbe.
			water.set("ExtentXZ", Vector2(5000, 5000))
			boat.set("DebugAutoDrive", true)
			boat.set("DebugSteer", 0.0)
			# Prua contro le onde (vengono da scirocco, sud-est): mare di prora, il caso dei salti.
			boat.global_rotation = Vector3(0.0, deg_to_rad(-135.0), 0.0)
		water.call("SetSeaState", state, 0.0)
		await wait(3.0)
		var y_min := 1e9
		var y_max := -1e9
		var pitch_max := 0.0
		var crate_min := 1e9
		var crate_max := -1e9
		var airborne := 0
		var speed_max := 0.0
		for i in 60:
			await wait(0.1)
			# In volo: nessun triangolo della carena sott'acqua.
			if float(boat.call("DebugTelemetry").get("immersed_area", 1.0)) <= 0.0:
				airborne += 1
			speed_max = max(speed_max, boat.linear_velocity.length())
			y_min = min(y_min, boat.global_position.y)
			y_max = max(y_max, boat.global_position.y)
			pitch_max = max(pitch_max, abs(rad_to_deg(boat.global_rotation.x)))
			crate_min = min(crate_min, crate.global_position.y)
			crate_max = max(crate_max, crate.global_position.y)
		print("[water] stato %.1f: barca y %.2f..%.2f, beccheggio max %.1f°, in volo %d/60, velocita' max %.1f m/s, cassa y %.2f..%.2f, superficie sotto la barca %.2f" % [
			state, y_min, y_max, pitch_max, airborne, speed_max, crate_min, crate_max,
			water.call("HeightAt", boat.global_position)])
		await snap("barca_stato_%d_esp_%d" % [int(state * 10), int(float(water.get("DefaultExposure")) * 100)])

	level.queue_free()
	await wait(0.5)


func measure_gpu(frames: int) -> Vector2:
	var vp := root.get_viewport().get_viewport_rid()
	var cpu := 0.0
	var gpu := 0.0
	for i in 20:
		await process_frame
	for i in frames:
		await process_frame
		cpu += RenderingServer.viewport_get_measured_render_time_cpu(vp)
		gpu += RenderingServer.viewport_get_measured_render_time_gpu(vp)
	return Vector2(cpu, gpu) / frames


func run_perf() -> void:
	RenderingServer.viewport_set_measure_render_time(root.get_viewport().get_viewport_rid(), true)
	var level: Node = load("res://world/maps/laguna/Laguna.tscn").instantiate()
	root.add_child(level)
	var cam: Camera3D = level.get_node("PreviewCamera")
	var water: Node = level.get_node("Water")
	var surface: Node3D = level.get_node("Water/Surface")
	cam.make_current()
	water.call("SetSeaState", 3.0, 0.0)
	for spot in [[Vector3(-6920, 2.5, -7224), 60.0, -12.0, "canale"], [Vector3(8000, 5.0, 8000), 120.0, -8.0, "mare"]]:
		place(cam, spot[0], spot[1], spot[2])
		await wait(4.0)
		surface.visible = false
		var none := await measure_gpu(200)
		surface.visible = true
		print("[water] %s, senza acqua: CPU %.2f ms, GPU %.2f ms" % [spot[3], none.x, none.y])
		for q in [0, 1, 2]:
			surface.set("Quality", q)
			var t := await measure_gpu(200)
			print("[water] %s, qualita' %d: CPU %.2f ms, GPU %.2f ms (acqua %.2f ms)" % [spot[3], q, t.x, t.y, t.y - none.y])
	level.queue_free()
	await wait(0.5)
