extends SceneTree
## Collaudo delle barche su misura (skill vehicles-boats §9) nel livello di prova delle onde.
##
##   Godot --path . --resolution 1280x720 --script tools/playtest_boats.gd -- <cartella_output>
##
## Partita locale ENet su MainWaveTest: il giocatore prende il timone della barca a barra e poi di
## quella a volante, accelera, vira, e ripete col mare in tempesta. Stampa velocita', angolo del
## motore, velocita' di virata, sussulto e beccheggio; salva screenshot da fuori (corpo intero, come
## lo vedono gli altri) e in prima persona. Con --headless solo numeri.

var out_dir := ""
var shots := 0
var main: Node
var player: Node3D
var chase: Camera3D


func _init() -> void:
	var args := OS.get_cmdline_user_args()
	out_dir = args[0] if args.size() > 0 else "user://"
	_run.call_deferred()


func wait(seconds: float) -> void:
	await create_timer(seconds).timeout


func snap(name: String) -> void:
	if DisplayServer.get_name() == "headless":
		print("[boats] (headless) ", name)
		return
	await RenderingServer.frame_post_draw
	var image := root.get_viewport().get_texture().get_image()
	shots += 1
	var path := "%s/%02d_%s.png" % [out_dir, shots, name]
	image.save_png(path)
	print("[boats] ", path)


## Il corpo del giocatore locale e' quello "in prima persona" (busto collassato): per guardarlo da
## fuori si rimette visibile l'originale, come lo vedono gli altri peer.
func show_full_body(visible_full: bool) -> void:
	for node in player.find_children("*", "MeshInstance3D", true, false):
		var mesh := node as MeshInstance3D
		if String(mesh.name).contains("FirstPerson"):
			mesh.visible = not visible_full and String(mesh.name).ends_with("Seated")
		elif mesh.skeleton != NodePath(""):
			mesh.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_ON if visible_full \
				else GeometryInstance3D.SHADOW_CASTING_SETTING_SHADOWS_ONLY


func drive(boat: RigidBody3D, throttle: float, steer: float, seconds: float) -> Dictionary:
	var y_min := 1e9
	var y_max := -1e9
	var pitch_max := 0.0
	var roll_max := 0.0
	var upright_min := 1.0
	var speed_max := 0.0
	var yaw0 := boat.global_rotation.y
	var t := 0.0
	while t < seconds:
		boat.call("SubmitControls", throttle, steer, 0.0, 0.0)
		await wait(0.05)
		t += 0.05
		y_min = min(y_min, boat.global_position.y)
		y_max = max(y_max, boat.global_position.y)
		pitch_max = max(pitch_max, abs(rad_to_deg(boat.global_rotation.x)))
		roll_max = max(roll_max, abs(rad_to_deg(boat.global_rotation.z)))
		upright_min = min(upright_min, boat.global_transform.basis.y.dot(Vector3.UP))
		speed_max = max(speed_max, boat.linear_velocity.length())
	return {
		"speed_kmh": speed_max * 3.6,
		"heave": y_max - y_min,
		"pitch": pitch_max,
		"roll": roll_max,
		"capsized": upright_min < 0.0,
		"yaw_deg": rad_to_deg(angle_difference(yaw0, boat.global_rotation.y)),
		"motor_deg": rad_to_deg(boat.get("SyncMotorAngle")),
	}


## Lo sguardo si muove solo col mouse (LookPitch non e' scrivibile da fuori): si inietta un movimento.
func look_pitch(pixels: float) -> void:
	var ev := InputEventMouseMotion.new()
	ev.relative = Vector2(0.0, pixels)
	ev.screen_relative = Vector2(0.0, pixels)
	Input.parse_input_event(ev)


func place_chase(boat: Node3D, back: float, side: float, up: float) -> void:
	var b := boat.global_transform.basis
	var forward := -b.z
	forward.y = 0.0
	forward = forward.normalized()
	var right := forward.cross(Vector3.UP)
	chase.global_position = boat.global_position - forward * back + right * side + Vector3.UP * up
	chase.look_at(boat.global_position + Vector3.UP * 0.8, Vector3.UP)


func take_helm(boat: RigidBody3D) -> void:
	player.global_position = boat.call("get", "HelmGlobalPosition") if false else boat.get_node("HelmSeat").global_position + Vector3.UP * 0.1
	await wait(0.3)
	boat.call("SubmitTakeHelm")
	await wait(0.6)
	print("[boats] %s: pilota %d, modalita' %d" % [boat.name, boat.get("PilotPeerId"), player.get("Mode")])


func leave_helm(boat: RigidBody3D) -> void:
	boat.call("SubmitControls", 0.0, 0.0, 0.0, 0.0)
	boat.call("SubmitLeaveHelm")
	await wait(0.4)


func _run() -> void:
	main = load("res://world/scenes/MainWaveTest.tscn").instantiate()
	root.add_child(main)
	current_scene = main
	await wait(0.5)
	main.get_node("UI/MainMenu").get_node("%HostLocalButton").pressed.emit()
	await wait(2.0)
	var players := main.get_node("Players")
	if players.get_child_count() == 0:
		print("[boats] ERRORE: nessun giocatore")
		quit(1)
		return
	player = players.get_child(0)
	# I comandi li manda questo collaudo: l'input del giocatore (tasti a zero, 20 Hz) si alternerebbe
	# coi nostri e l'invertitore vedrebbe la leva saltare fra folle e marcia senza mai innestare.
	player.get_node("VehicleInput").set_physics_process(false)
	var water: Node = get_first_node_in_group("water")
	var level := main.get_node("WaveTestLevel")
	var fp_camera: Camera3D = player.get_node("PlayerCamera")
	chase = Camera3D.new()
	chase.fov = 60.0
	level.add_child(chase)

	var moorings := {}
	for n in ["BarchinoTiller", "BarchinoConsole", "Gozzo", "Peschereccio"]:
		moorings[n] = (level.get_node(n) as Node3D).global_transform
	for boat_name in ["BarchinoTiller", "BarchinoConsole", "Gozzo", "Peschereccio"]:
		var boat: RigidBody3D = level.get_node(boat_name)
		water.call("SetSeaState", 1.0, 0.0)
		# Tutte le barche all'ormeggio: in tempesta una barca lasciata accanto al pontile fisso ci
		# finisce sotto incastrata (non ci sono cime d'ormeggio).
		for n in moorings:
			var other: RigidBody3D = level.get_node(n)
			other.global_transform = moorings[n]
			other.linear_velocity = Vector3.ZERO
			other.angular_velocity = Vector3.ZERO
		await wait(1.5)
		print("[boats] %s a riposo: y %.2f, massa %.0f kg" % [boat_name, boat.global_position.y, boat.mass])
		await take_helm(boat)

		# Pilota seduto: vista di tre quarti dal fianco, corpo intero; poi la prima persona.
		show_full_body(true)
		chase.make_current()
		await drive(boat, 0.0, 0.0, 0.6)
		place_chase(boat, 2.5, 3.2, 1.6)
		await snap(boat_name + "_seduto")
		boat.call("SubmitControls", 0.0, 1.0, 0.0, 0.0)
		await drive(boat, 0.0, 1.0, 1.0)
		place_chase(boat, 2.5, 3.2, 1.6)
		await snap(boat_name + "_sterzo_dritta")
		show_full_body(false)
		fp_camera.make_current()
		# Sguardo verso prua e in basso, sul governo: le mani del corpo devono essere li'.
		var want: float = boat.get("HeadingYaw") + (0.6 if boat_name == "BarchinoTiller" else 0.0)
		fp_camera.call("AddYaw", angle_difference(fp_camera.get("LookYaw"), want))
		look_pitch(400.0)
		await drive(boat, 0.0, 1.0, 0.3)
		await snap(boat_name + "_prima_persona")
		look_pitch(-400.0)

		# Mare poco mosso: accelerazione e virata.
		var r := await drive(boat, 1.0, 0.0, 8.0)
		print("[boats] %s dritto 8 s: %.1f km/h max, sussulto %.2f m, beccheggio %.1f°, rollio %.1f°" % [boat_name, r.speed_kmh, r.heave, r.pitch, r.roll])
		show_full_body(true)
		chase.make_current()
		place_chase(boat, 9.0, 4.0, 3.0)
		await snap(boat_name + "_in_corsa")
		r = await drive(boat, 1.0, 1.0, 4.0)
		print("[boats] %s virata a dritta 4 s: %.0f° di prua, motore %.0f°, rollio max %.1f°" % [boat_name, r.yaw_deg, r.motor_deg, r.roll])

		# Tempesta in mare aperto.
		water.call("SetSeaState", 4.0, 0.0)
		await wait(1.0)
		r = await drive(boat, 1.0, 0.0, 8.0)
		print("[boats] %s tempesta 8 s: %.1f km/h max, sussulto %.2f m, beccheggio %.1f°, rollio %.1f°, capovolta: %s" % [boat_name, r.speed_kmh, r.heave, r.pitch, r.roll, r.capsized])
		r = await drive(boat, 0.6, 1.0, 6.0)
		print("[boats] %s tempesta, virata stretta 6 s: rollio max %.1f°, capovolta: %s" % [boat_name, r.roll, r.capsized])
		place_chase(boat, 10.0, 6.0, 3.0)
		await snap(boat_name + "_tempesta")
		show_full_body(false)
		fp_camera.make_current()
		await leave_helm(boat)
		# Riporta la barca dov'era (lontano dall'altra: sopra l'altra la ribalterebbe).
		boat.global_transform = moorings[boat_name]
		boat.linear_velocity = Vector3.ZERO
		boat.angular_velocity = Vector3.ZERO

	print("[boats] fine")
	quit(0)
