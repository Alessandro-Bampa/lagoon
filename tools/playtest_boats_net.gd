extends SceneTree
## Collaudo di rete delle barche su misura: due processi ENet locali su MainWaveTest.
##
##   Godot --path . --headless --script tools/playtest_boats_net.gd -- host
##   Godot --path . --headless --script tools/playtest_boats_net.gd -- client     (subito dopo)
##
## L'host prende il timone della barca a volante e la guida virando; ogni processo stampa ogni 0,5 s
## posizione della barca, gas e angolo del motore replicati, e come vede il pilota (modalita', seduto,
## mani sul volante). Sul client devono coincidere con l'host.

var role := "host"


func _init() -> void:
	var args := OS.get_cmdline_user_args()
	role = args[0] if args.size() > 0 else "host"
	_run.call_deferred()


func wait(seconds: float) -> void:
	await create_timer(seconds).timeout


func _run() -> void:
	var main: Node = load("res://world/scenes/MainWaveTest.tscn").instantiate()
	root.add_child(main)
	current_scene = main
	await wait(0.5)
	var net: Node = root.get_node("NetworkManager")
	if role == "host":
		net.call("HostGame", 1)
	else:
		await wait(1.0)
		net.call("JoinGame", 1, "127.0.0.1")
	await wait(3.0)

	var boat: RigidBody3D = main.get_node("WaveTestLevel/BarchinoConsole")
	var players: Node = main.get_node("Players")
	if role == "host":
		var me: Node3D = players.get_node(str(main.multiplayer.get_unique_id()))
		me.global_position = boat.get_node("HelmSeat").global_position + Vector3.UP * 0.1
		# I comandi li manda il collaudo: l'input del giocatore (tasti a zero) si alternerebbe ai nostri.
		me.get_node("VehicleInput").set_physics_process(false)
		await wait(0.4)
		boat.call("SubmitTakeHelm")
		await wait(0.5)

	for i in 24:
		if role == "host":
			boat.call("SubmitControls", 0.7, 1.0 if i < 12 else -1.0, 0.0, 0.0)
		var pilot: Node = players.get_node_or_null("1")
		var rig: Node = pilot.get_node("Visual/CharacterRig") if pilot != null else null
		print("[boatnet] %s %.3f pos=(%.1f, %.1f) gas=%.2f motore=%.1f° trim=%.1f° giri=%.0f elica=%.0f pilota=%d modo=%s seduto=%s posa=%.0f mano_sx=%s" % [
			role, Time.get_unix_time_from_system(), boat.global_position.x, boat.global_position.z,
			boat.get("SyncThrottle"), rad_to_deg(boat.get("SyncMotorAngle")), rad_to_deg(boat.get("SyncTrim")),
			boat.get("SyncEngineRpm"), boat.get("SyncPropRpm"), boat.get("PilotPeerId"),
			pilot.get("Mode") if pilot != null else -1,
			rig.get("Seated") if rig != null else false,
			rig.get("SeatPose") if rig != null else -1.0,
			rig.get("SeatLeftHand") != null if rig != null else false])
		# Intento a 20 Hz, come VehicleInput (l'host azzera i comandi dopo 0,5 s di silenzio).
		for k in 10:
			await wait(0.05)
			if role == "host":
				boat.call("SubmitControls", 0.7, 1.0 if i < 12 else -1.0, 0.0, 0.0)

	print("[boatnet] %s fine" % role)
	quit(0)
