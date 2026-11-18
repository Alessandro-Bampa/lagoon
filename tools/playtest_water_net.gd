extends SceneTree
## Collaudo di rete dell'acqua (skill water-ocean §2): due processi ENet locali sul livello di prova.
##
##   Godot --path . --headless --script tools/playtest_water_net.gd -- host
##   Godot --path . --headless --script tools/playtest_water_net.gd -- client     (subito dopo)
##
## L'host, a partita avviata, porta il mare in tempesta, accende la marea e alza il sovralzo. Ogni
## processo stampa ogni 0,5 s una riga "[waternet] <ruolo> <ora di sistema> ..." con orologio delle
## onde, stato del mare, livello e quota della barca: confrontando le righe alla stessa ora di sistema
## si misura di quanto il client e' fuori passo.

var role := "host"


func _init() -> void:
	var args := OS.get_cmdline_user_args()
	role = args[0] if args.size() > 0 else "host"
	_run.call_deferred()


func wait(seconds: float) -> void:
	await create_timer(seconds).timeout


func _run() -> void:
	var main: Node = load("res://world/scenes/Main.tscn").instantiate()
	root.add_child(main)
	current_scene = main
	await wait(0.5)

	var net: Node = root.get_node("NetworkManager")
	if role == "host":
		net.call("HostGame", 1)        # TransportMode.LocalEnet
	else:
		await wait(1.0)
		net.call("JoinGame", 1, "127.0.0.1")
	await wait(2.0)

	var water: Node = get_first_node_in_group("water")
	var boat: Node3D = get_first_node_in_group("vehicle")
	if water == null or boat == null:
		print("[waternet] ERRORE: acqua o barca non trovate")
		quit(1)
		return
	print("[waternet] %s: peer %d, autorita' acqua %s" % [role, main.multiplayer.get_unique_id(), water.is_multiplayer_authority()])

	for i in 36:
		if role == "host" and i == 6:
			water.set("TideEnabled", true)
			water.set("TideTimeScale", 3600.0)     # un'ora di marea al secondo: si vede subito
			water.call("SetSeaState", 4.0, 2.0)
			water.call("SetSurge", 0.5, 3.0)
			print("[waternet] host: tempesta, marea e sovralzo avviati")
		print("[waternet] %s %.3f t=%.3f mare=%.3f livello=%.3f barca_y=%.3f onda(50,0)=%.3f" % [
			role, Time.get_unix_time_from_system(), water.get("PhysicsTime"), water.get("SeaState"),
			water.get("WaterLevel"), boat.global_position.y,
			water.call("HeightAt", Vector3(50, 0, 0))])
		await wait(0.5)

	print("[waternet] %s fine" % role)
	quit(0)
