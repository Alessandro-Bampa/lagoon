extends SceneTree
## Collaudo di rete dei cancelli scorrevoli (vaporetto, skill vehicles-boats): due processi ENet
## locali su MainWaveTest.
##
##   Godot --path . --headless --script tools/playtest_gates_net.gd -- host
##   Godot --path . --headless --script tools/playtest_gates_net.gd -- client     (subito dopo)
##
## Il CLIENT chiede il cancello di sinistra (bit 0) prima da lontano (l'host deve rifiutare), poi dal
## varco sul ponte (l'host deve accettare). Entrambi i processi stampano maschera replicata e posa di
## un pannello: sul client devono seguire l'host, e la richiesta lontana non deve cambiare nulla.

var role := "host"


func _init() -> void:
	var args := OS.get_cmdline_user_args()
	role = args[0] if args.size() > 0 else "host"
	_run.call_deferred()


func wait(seconds: float) -> void:
	await create_timer(seconds).timeout


func report(boat: Node, panel: Node3D, label: String) -> void:
	print("[gatenet] %s %-14s maschera=%d pannello_z=%.2f" % [role, label, int(boat.get("SyncGateOpenMask")),
		panel.position.z if panel != null else -99.0])


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

	var boat: RigidBody3D = main.get_node("WaveTestLevel/Vaporetto")
	var panel := boat.find_child("Gate_Port_Aft", true, false) as Node3D
	var players: Node = main.get_node("Players")
	report(boat, panel, "inizio")

	if role == "client":
		var me: Node3D = players.get_node(str(main.multiplayer.get_unique_id()))
		# 1) Da lontano (dove si nasce): l'host deve rifiutare.
		boat.call("SubmitToggleGate", 0)
		await wait(2.6)
		report(boat, panel, "da_lontano")
		# 2) Sul ponte, dentro il varco di sinistra: l'host valida sulla posizione replicata.
		var centre: Vector3 = boat.call("GateGlobalCenter", 0)
		var inward: Vector3 = (boat.global_position - centre)
		inward = boat.global_basis.x * signf(inward.dot(boat.global_basis.x))
		me.global_position = centre + inward * 0.8 + Vector3.UP * 0.1
		await wait(1.0)
		boat.call("SubmitToggleGate", 0)
		await wait(2.6)
		report(boat, panel, "dal_varco")
		boat.call("SubmitToggleGate", 0)
		await wait(2.6)
		report(boat, panel, "richiuso")
	else:
		for i in 30:
			await wait(0.5)
			report(boat, panel, "t=%.1f" % (0.5 * (i + 1)))

	print("[gatenet] %s fine" % role)
	quit(0)
