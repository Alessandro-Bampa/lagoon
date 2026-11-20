extends SceneTree
## Collaudo delle barche su misura (skill vehicles-boats §9) nel livello di prova delle onde.
##
##   Godot --path . --resolution 1280x720 --script tools/playtest_boats.gd -- <cartella_output> [Barca,Barca]
##
## Partita locale ENet su MainWaveTest: il giocatore prende il timone della barca a barra e poi di
## quella a volante, accelera, vira, e ripete col mare in tempesta. Stampa velocita', angolo del
## motore, velocita' di virata, sussulto e beccheggio; salva screenshot da fuori (corpo intero, come
## lo vedono gli altri) e in prima persona. Con --headless solo numeri.

var out_dir := ""
## Barche da provare (secondo argomento, separate da virgola); vuoto = tutte.
var only: PackedStringArray = []
var shots := 0
var main: Node
var player: Node3D
var chase: Camera3D


func _init() -> void:
	var args := OS.get_cmdline_user_args()
	out_dir = args[0] if args.size() > 0 else "user://"
	if args.size() > 1:
		only = args[1].split(",")
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


## A nuoto dietro la scaletta della barca, poi avanti + salto: si deve finire a bordo (ancora = barca).
func board_from_water(boat: RigidBody3D) -> void:
	var ladder: Node3D = null
	for n in boat.find_children("*", "Node3D", true, false):
		if n.is_in_group("swim_ladder"):
			ladder = n
			break
	if ladder == null:
		print("[boats] %s: nessuna scaletta, salita dall'acqua non provata" % boat.name)
		return
	# Il −Z della scaletta punta verso l'interno della barca (cosi' la montano tutti gli scafi).
	var to_boat: Vector3 = ladder.global_basis * Vector3.FORWARD
	to_boat.y = 0.0
	to_boat = to_boat.normalized()
	player.global_position = Vector3(ladder.global_position.x, boat.global_position.y - 0.6, ladder.global_position.z) - to_boat * 0.7
	player.velocity = Vector3.ZERO
	await wait(1.5)
	var cam: Node = player.get_node("PlayerCamera")
	cam.call("AddYaw", angle_difference(float(cam.get("LookYaw")), atan2(to_boat.x, to_boat.z)))
	# Come un giocatore: avanti contro lo scafo e salto, riprovando se l'onda lo ha spostato.
	Input.action_press("move_up")
	for attempt in 4:
		# Davanti alla scaletta (le onde spostano chi nuota), rivolti verso di lei: e' li' che il bordo
		# concede la salita alta.
		# Il −Z della scaletta punta verso l'interno della barca (cosi' la montano tutti gli scafi).
		to_boat = ladder.global_basis * Vector3.FORWARD
		to_boat.y = 0.0
		to_boat = to_boat.normalized()
		player.global_position = Vector3(ladder.global_position.x, player.global_position.y, ladder.global_position.z) - to_boat * 0.6
		player.velocity = Vector3.ZERO
		cam.call("AddYaw", angle_difference(float(cam.get("LookYaw")), atan2(to_boat.x, to_boat.z)))
		await wait(0.6)
		Input.action_press("jump")
		await wait(0.1)
		Input.action_release("jump")
		await wait(1.2)
		if not bool(player.get("SyncSwimming")):
			break
	Input.action_release("move_up")
	await wait(1.5)
	var ok := int(player.get("SyncAnchorId")) == int(boat.get("VehicleId")) and not bool(player.get("SyncSwimming"))
	print("[boats] %s salita dall'acqua dalla scaletta: %s (ancora %d, a nuoto %s, piedi %.2f m sopra l'acqua)" % [
		boat.name, "OK" if ok else "FALLITA", int(player.get("SyncAnchorId")), player.get("SyncSwimming"),
		player.global_position.y - 1.0])


## Varco con cancello scorrevole (vaporetto): da in acqua, davanti al varco, a cancello CHIUSO non si
## deve salire; aperto (richiesta fatta dall'acqua) si sale col bordo libero basso. Poi lo si richiude.
func board_through_gate(boat: RigidBody3D) -> void:
	var count := int(boat.get("GateCount"))
	if count == 0:
		return
	var gate := count - 1
	var centre: Vector3 = boat.call("GateGlobalCenter", gate)
	var outward: Vector3 = centre - boat.global_position
	var right: Vector3 = boat.global_basis.x
	outward = right * signf(outward.dot(right))
	var cam: Node = player.get_node("PlayerCamera")

	var climb := func(label: String) -> bool:
		player.global_position = Vector3(centre.x, boat.global_position.y - 0.6, centre.z) + outward * 0.9
		player.velocity = Vector3.ZERO
		await wait(1.5)
		Input.action_press("move_up")
		for attempt in 3:
			player.global_position = Vector3(centre.x, player.global_position.y, centre.z) + outward * 0.6
			player.velocity = Vector3.ZERO
			cam.call("AddYaw", angle_difference(float(cam.get("LookYaw")), atan2(-outward.x, -outward.z)))
			await wait(0.6)
			Input.action_press("jump")
			await wait(0.1)
			Input.action_release("jump")
			await wait(1.2)
			if not bool(player.get("SyncSwimming")):
				break
		Input.action_release("move_up")
		await wait(1.0)
		var aboard := int(player.get("SyncAnchorId")) == int(boat.get("VehicleId")) and not bool(player.get("SyncSwimming"))
		print("[boats] %s varco %d %s: %s (maschera %d)" % [boat.name, gate, label, "a bordo" if aboard else "in acqua", int(boat.get("SyncGateOpenMask"))])
		return aboard

	var closed_ok: bool = not await climb.call("chiuso")
	# Dall'acqua: richiesta al cancello (l'host valida la distanza sulla posizione replicata).
	player.global_position = Vector3(centre.x, boat.global_position.y - 0.6, centre.z) + outward * 0.9
	await wait(0.5)
	var rp: Vector3 = player.get("ResolvedSyncPosition")
	print("[boats] richiesta dall'acqua: distanza dal varco %.2f m (portata %.1f)" % [
		float(boat.call("GateDistance", gate, rp)), float(boat.get("GateRange"))])
	boat.call("SubmitToggleGate", gate)
	await wait(2.6)
	var opened := (int(boat.get("SyncGateOpenMask")) & (1 << gate)) != 0
	show_full_body(true)
	chase.make_current()
	chase.global_position = centre + outward * 6.0 + Vector3.UP * 1.5
	chase.look_at(centre, Vector3.UP)
	await snap(String(boat.name) + "_varco_aperto")
	player.get_node("PlayerCamera").call("make_current")
	show_full_body(false)
	var open_ok: bool = await climb.call("aperto")
	boat.call("SubmitToggleGate", gate)
	await wait(2.6)
	var reclosed := (int(boat.get("SyncGateOpenMask")) & (1 << gate)) == 0
	print("[boats] %s cancelli: chiuso blocca %s, apertura %s, salita dal varco %s, richiusura %s" % [
		boat.name, "OK" if closed_ok else "FALLITO", "OK" if opened else "FALLITA",
		"OK" if open_ok else "FALLITA", "OK" if reclosed else "FALLITA"])


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
	for n in ["BarchinoTiller", "BarchinoConsole", "Gozzo", "Peschereccio", "Vaporetto"]:
		moorings[n] = (level.get_node(n) as Node3D).global_transform
	for boat_name in ["BarchinoTiller", "BarchinoConsole", "Gozzo", "Peschereccio", "Vaporetto"]:
		if not only.is_empty() and not boat_name in only:
			continue
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
		await board_from_water(boat)
		await board_through_gate(boat)
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
		# Visuale in guida (pipeline §4.1): sguardo dritto a prua, si deve vedere fuori dai vetri.
		await drive(boat, 0.0, 0.0, 0.3)
		await snap(boat_name + "_prima_persona_avanti")

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
