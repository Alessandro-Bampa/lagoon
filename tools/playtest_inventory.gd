extends SceneTree
## Collaudo in gioco dell'inventario, senza editor: avvia una partita locale (ENet), apre la
## schermata, esegue operazioni (raccolta, saccheggio, trascinamenti col mouse, scambi, stack,
## caricatori, ordinamento) e salva uno screenshot per passo. A ogni passo stampa lo stato
## dell'inventario (`PlayerInventory.DescribeState`), cosi' il copione gira anche `--headless`.
##
## Uso (dalla radice del progetto, dopo `dotnet build`):
##
##   Godot --path . --resolution 1920x1080 --script tools/playtest_inventory.gd -- <cartella_output>

var out_dir := ""
var shots := 0
var player: Node3D
var inventory: Node
var screen: Control


func _init() -> void:
	var args := OS.get_cmdline_user_args()
	out_dir = args[0] if args.size() > 0 else "user://"
	_run.call_deferred()


func wait(seconds: float) -> void:
	await create_timer(seconds).timeout


func snap(name: String) -> void:
	print("[inv] --- ", name, " ---\n", inventory.call("DescribeState"))
	if DisplayServer.get_name() == "headless":
		return
	await RenderingServer.frame_post_draw
	var image := root.get_viewport().get_texture().get_image()
	shots += 1
	var path := "%s/%02d_%s.png" % [out_dir, shots, name]
	image.save_png(path)
	print("[inv] ", path)


func press(action: String) -> void:
	Input.action_press(action)
	await wait(0.1)
	Input.action_release(action)
	await wait(0.2)


## Nodi con lo script dato (i tipi C# non globali non si trovano per nome di classe).
func nodes_with_script(from: Node, file: String, into: Array = []) -> Array:
	var script: Script = from.get_script()
	if script != null and script.resource_path.ends_with("/" + file):
		into.append(from)
	for child in from.get_children():
		nodes_with_script(child, file, into)
	return into


func mouse_move(pos: Vector2) -> void:
	var motion := InputEventMouseMotion.new()
	motion.position = pos
	motion.global_position = pos
	Input.parse_input_event(motion)
	await process_frame


func mouse_button(pos: Vector2, pressed: bool, ctrl := false, button := MOUSE_BUTTON_LEFT) -> void:
	var event := InputEventMouseButton.new()
	event.button_index = button
	event.pressed = pressed
	event.position = pos
	event.global_position = pos
	event.ctrl_pressed = ctrl
	Input.parse_input_event(event)
	await process_frame


## Trascina dal punto `from` al punto `to` (coordinate di schermo), a piccoli passi.
func drag(from: Vector2, to: Vector2, rotate := false, ctrl_release := false) -> void:
	await mouse_move(from)
	await mouse_button(from, true)
	for i in range(1, 9):
		await mouse_move(from.lerp(to, i / 8.0))
	if rotate:
		await press("rotate_item")
		await mouse_move(to)
	await wait(0.15)
	await mouse_button(to, false, ctrl_release)
	await wait(0.3)


func world_uid(item_id: String) -> int:
	for node in get_nodes_in_group("world_item"):
		if not node.is_queued_for_deletion() and str(node.get("Payload").get("item", "")) == item_id:
			return node.get("Uid")
	return 0


func go_to(item_id: String) -> int:
	var uid := world_uid(item_id)
	for node in get_nodes_in_group("world_item"):
		if node.get("Uid") == uid and not node.is_queued_for_deletion():
			player.global_position = node.global_position + Vector3(0, 1.0, -1.2)
	await wait(0.3)
	return uid


func slot_views() -> Array:
	return nodes_with_script(screen, "SlotView.cs")


func grid_views() -> Array:
	return nodes_with_script(screen, "GridView.cs")


func center(control: Control) -> Vector2:
	return control.get_global_rect().get_center()


func _run() -> void:
	var main: Node = load("res://world/scenes/Main.tscn").instantiate()
	root.add_child(main)
	current_scene = main
	await wait(0.5)

	main.get_node("UI/MainMenu").get_node("%HostLocalButton").pressed.emit()
	await wait(1.5)

	var players := main.get_node("Players")
	if players.get_child_count() == 0:
		print("[inv] ERRORE: nessun giocatore spawnato")
		quit(1)
		return
	player = players.get_child(0)
	inventory = player.get_node("Inventory")
	screen = nodes_with_script(player, "InventoryScreen.cs")[0]

	await snap("hud_closed")
	await press("toggle_inventory")
	await snap("kit_iniziale")

	# Raccolta con l'intento "prendi quell'oggetto": l'host decide dove va.
	for id in ["helmet", "headset", "body_armor", "knife", "flashlight", "cash", "medkit"]:
		var uid := await go_to(id)
		inventory.call("SubmitPickup", uid)
		await wait(0.25)
	await snap("dopo_raccolta") # headset rifiutate: l'elmetto blocca le cuffie

	# Rig corazzato con il corpetto indossato: lo slot e' bloccato, finisce nello zaino.
	inventory.call("SubmitPickup", await go_to("armored_rig"))
	await wait(0.3)
	await snap("rig_corazzato")

	# Saccheggio: la cassa si apre nella colonna destra.
	var crate := await go_to("supply_crate")
	screen.call("OpenWorldContainer", crate)
	await wait(0.4)
	await snap("cassa_aperta")

	# Trascinamento col mouse: il primo oggetto della cassa verso lo zaino, ruotandolo.
	var grids := grid_views()
	var loot: Control = grids[grids.size() - 1]
	var cell: float = loot.custom_minimum_size.x / 8.0
	var backpack: Control = null
	for g in grids:
		if is_equal_approx(g.custom_minimum_size.x, cell * 5.0) and is_equal_approx(g.custom_minimum_size.y, cell * 4.0):
			backpack = g
	var from := loot.global_position + Vector2(cell * 0.5, cell * 0.5)
	if backpack != null:
		# A meta' trascinamento: anteprima verde sul bersaglio.
		await mouse_move(from)
		await mouse_button(from, true)
		var over := backpack.global_position + Vector2(cell * 3.5, cell * 2.5)
		for i in range(1, 9):
			await mouse_move(from.lerp(over, i / 8.0))
		await wait(0.2)
		await snap("trascinamento_in_corso")
		await mouse_button(over, false)
		await wait(0.3)
		await snap("dopo_trascinamento")

	# Ctrl+click: spostamento rapido dalla cassa all'inventario (priorita' per categoria).
	for i in 4:
		await mouse_button(from, true, true)
		await mouse_button(from, false, true)
		await wait(0.25)
	await snap("dopo_ctrl_click")

	# Scambio di slot: il fucile a tracolla trascinato sullo slot "sulla schiena".
	var slots := slot_views()
	await drag(center(slots[6]), center(slots[7]))
	await snap("fucile_sulla_schiena")

	# Doppio click sul fucile: finestra di dettaglio con lo slot del caricatore.
	await mouse_button(center(slots[7]), true)
	await mouse_button(center(slots[7]), false)
	var dbl := InputEventMouseButton.new()
	dbl.button_index = MOUSE_BUTTON_LEFT
	dbl.pressed = true
	dbl.double_click = true
	dbl.position = center(slots[7])
	dbl.global_position = dbl.position
	Input.parse_input_event(dbl)
	await process_frame
	await mouse_button(center(slots[7]), false)
	await wait(0.4)
	await snap("finestra_arma")

	# Esc chiude l'ultima finestra, poi l'inventario.
	await press("toggle_menu")
	await snap("dopo_esc_1")
	await press("toggle_menu")
	await wait(0.2)
	print("[inv] inventario aperto dopo due Esc: ", screen.visible)

	# Ricarica: il caricatore di scorta entra nell'arma, il vecchio prende il suo posto.
	var weapon := player.get_node("Weapon")
	weapon.call("SubmitHold", 8)
	await wait(1.0)
	Input.action_press("fire")
	await wait(0.6)
	Input.action_release("fire")
	await wait(0.3)
	print("[inv] dopo la raffica: caricatore=", weapon.get("MagazineAmmo"), " riserva=", weapon.get("ReserveAmmo"))
	weapon.call("SubmitReload")
	await wait(4.0)
	print("[inv] dopo la ricarica: caricatore=", weapon.get("MagazineAmmo"), " riserva=", weapon.get("ReserveAmmo"))
	await snap("dopo_ricarica")

	print("[inv] fine")
	quit(0)
