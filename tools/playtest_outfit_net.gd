extends SceneTree
## Collaudo di rete del vestiario: due processi ENet locali.
##
##   Godot --path . --headless --script tools/playtest_outfit_net.gd -- host
##   Godot --path . --headless --script tools/playtest_outfit_net.gd -- client     (subito dopo)
##
## Dopo l'ingresso del client l'host mette un elmetto e una fascia al giocatore del client (come
## farebbe un intento d'inventario validato). Ogni processo stampa, per ogni giocatore, gli
## ItemId replicati da CharacterAppearance e i pezzi agganciati allo scheletro da CharacterOutfit:
## devono coincidere sui due peer, prima e dopo il cambio.

var role := "host"


func _init() -> void:
	var args := OS.get_cmdline_user_args()
	role = args[0] if args.size() > 0 else "host"
	_run.call_deferred()


func wait(seconds: float) -> void:
	await create_timer(seconds).timeout


func report(main: Node, tag: String) -> void:
	for player in main.get_node("Players").get_children():
		var worn = player.get_node("Appearance").get("WornItemIds")
		var skeleton: Node = player.get_node("Visual/CharacterRig").get("Skeleton")
		var pieces := 0
		for child in skeleton.get_children():
			if child is MeshInstance3D and String(child.name).begins_with("Wear_") \
					and not String(child.name).contains("FirstPerson"):
				pieces += 1
		print("[net-outfit] %s %s giocatore=%s indossati=%s pezzi=%d" % [role, tag, player.name, worn, pieces])


func _run() -> void:
	var main: Node = load("res://world/scenes/Main.tscn").instantiate()
	root.add_child(main)
	current_scene = main
	await wait(0.5)
	var net: Node = root.get_node("NetworkManager")
	var players: Node = main.get_node("Players")
	if role == "host":
		net.call("HostGame", 1)
		# si aspetta il client (al massimo 40 s)
		for i in 400:
			if players.get_child_count() >= 2:
				break
			await wait(0.1)
		await wait(4.0)
		report(main, "prima")
		for player in players.get_children():
			if player.name != str(main.multiplayer.get_unique_id()):
				var inv: Node = player.get_node("Inventory")
				print("[net-outfit] host: elmetto al client=", inv.call("HostGiveItem", "helmet"),
					" fascia=", inv.call("HostGiveItem", "armband"))
		await wait(2.0)
		report(main, "dopo")
		await wait(8.0)      # il client deve fare in tempo a stampare
	else:
		await wait(1.0)
		net.call("JoinGame", 1, "127.0.0.1")
		for i in 400:
			if players.get_child_count() >= 2:
				break
			await wait(0.1)
		await wait(2.0)
		report(main, "prima")
		await wait(6.0)
		report(main, "dopo")
	quit(0)
