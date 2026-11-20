extends SceneTree
## Collaudo del vestiario (skill character-wearables): ospita una partita ENet locale, mette in fila
## dei personaggi con combinazioni di capi in pose diverse e fotografa davanti e dietro; poi veste il
## giocatore e fotografa la prima persona (maniche e guanti sul viewmodel, pantaloni e stivali
## guardando in basso).
##
##   Godot --path . --resolution 1280x720 --script tools/playtest_outfit.gd -- <cartella> [tp|fp|all]
##
## Cosa guardare: nessuna pelle che attraversa un capo; nessun buco dove una regione e' nascosta ma
## il capo non la copre; nelle pose (corsa, accovacciati, mira, seduti) i capi seguono il corpo.

var out_dir := ""
var mode := "all"
var shots := 0

const OUTFITS := [
	[],
	["cargo_pants", "work_jacket", "rubber_boots"],
	["cargo_pants", "work_jacket", "rubber_boots", "work_gloves", "body_armor", "helmet", "goggles"],
	["cargo_pants", "work_jacket", "rubber_boots", "vest", "backpack", "headset", "armband"],
	["cargo_pants", "work_jacket", "rubber_boots", "armored_rig", "balaclava", "work_gloves"],
	["cargo_pants", "rubber_boots", "balaclava", "helmet", "backpack"],
]

const POSES := [
	{},
	{"LocalVelocity": Vector2(0, 4)},
	{"Crouching": true},
	{"Aiming": true},
]


func _init() -> void:
	var args := OS.get_cmdline_user_args()
	out_dir = args[0] if args.size() > 0 else "user://"
	mode = args[1] if args.size() > 1 else "all"
	_run.call_deferred()


func wait(seconds: float) -> void:
	await create_timer(seconds).timeout


func snap(name: String) -> void:
	if DisplayServer.get_name() == "headless":
		print("[playtest] (headless) ", name)
		return
	await RenderingServer.frame_post_draw
	var image := root.get_viewport().get_texture().get_image()
	shots += 1
	var path := "%s/%02d_%s.png" % [out_dir, shots, name]
	image.save_png(path)
	print("[playtest] ", path)


func look(dx: float, dy: float) -> void:
	var motion := InputEventMouseMotion.new()
	motion.screen_relative = Vector2(dx, dy)
	motion.relative = Vector2(dx, dy)
	Input.parse_input_event(motion)


func _run() -> void:
	var main: Node = load("res://world/scenes/Main.tscn").instantiate()
	root.add_child(main)
	current_scene = main
	await wait(0.5)
	main.get_node("UI/MainMenu").get_node("%HostLocalButton").pressed.emit()
	await wait(1.5)
	var players := main.get_node("Players")
	if players.get_child_count() == 0:
		print("[playtest] ERRORE: nessun giocatore spawnato")
		quit(1)
		return
	var player: Node3D = players.get_child(0)

	if mode == "tp" or mode == "all":
		await third_person(main)
	if mode == "fp" or mode == "all":
		await first_person(player)
	print("[playtest] fine")
	quit(0)


func make_rig(main: Node, pos: Vector3, worn: Array, props: Dictionary, weapon_path: String) -> Node3D:
	var rig: Node3D = load("res://animation/scenes/CharacterRig.tscn").instantiate()
	main.add_child(rig)
	rig.global_position = pos
	if weapon_path != "":
		rig.set("Weapon", load(weapon_path))
	for key in props:
		rig.set(key, props[key])
	rig.get_node("Outfit").set("Worn", PackedStringArray(worn))
	return rig


func third_person(main: Node) -> void:
	var rifle := "res://animation/resources/rifle_view.tres"
	var base := Vector3(-6.0, 0.0, -8.0)
	var cam := Camera3D.new()
	main.add_child(cam)
	cam.current = true
	for p in POSES.size():
		var rigs := []
		for i in OUTFITS.size():
			var weapon := rifle if POSES[p].has("Aiming") else ""
			rigs.append(make_rig(main, base + Vector3(i * 1.4, 0, 0), OUTFITS[i], POSES[p], weapon))
		await wait(2.0)
		var mid := base + Vector3((OUTFITS.size() - 1) * 0.7, 0, 0)
		cam.fov = 40.0
		cam.global_position = mid + Vector3(0, 1.2, 9.5)
		cam.look_at(mid + Vector3(0, 0.95, 0))
		await snap("tp_pose%d_front" % p)
		cam.global_position = mid + Vector3(0, 1.4, -9.5)
		cam.look_at(mid + Vector3(0, 0.95, 0))
		await snap("tp_pose%d_back" % p)
		if p == 0:
			# primi piani di testa e busto, davanti e di tre quarti
			cam.fov = 30.0
			for i in [2, 3, 4]:
				var c: Vector3 = base + Vector3(i * 1.4, 0, 0)
				cam.global_position = c + Vector3(0.9, 1.7, 2.2)
				cam.look_at(c + Vector3(0, 1.35, 0))
				await snap("tp_close_%d" % i)
				cam.global_position = c + Vector3(-1.0, 1.8, -2.1)
				cam.look_at(c + Vector3(0, 1.3, 0))
				await snap("tp_close_back_%d" % i)
		for rig in rigs:
			rig.queue_free()
		await wait(0.2)
	cam.queue_free()
	await wait(0.2)


func first_person(player: Node3D) -> void:
	var inventory := player.get_node("Inventory")
	var weapon := player.get_node("Weapon")
	# Il kit iniziale veste gia' giacca, pantaloni e stivali: si aggiungono i guanti e la fascia.
	inventory.call("HostGiveItem", "work_gloves")
	inventory.call("HostGiveItem", "armband")
	await wait(0.5)
	print("[playtest] indossati=", player.get_node("Appearance").get("WornItemIds"))
	await snap("fp_dressed_unarmed")
	look(0, 620)
	await wait(0.4)
	await snap("fp_dressed_look_down")
	look(0, -620)
	await wait(0.3)
	weapon.call("RequestHold", 7)
	await wait(1.3)
	await snap("fp_dressed_rifle_hip")
	weapon.call("RequestReload")
	await wait(1.4)
	await snap("fp_dressed_rifle_reload")
	await wait(2.4)
	weapon.call("RequestHold", 9)
	await wait(1.2)
	await snap("fp_dressed_pistol_hip")
