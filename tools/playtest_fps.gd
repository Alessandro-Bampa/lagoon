extends SceneTree
## Collaudo in gioco di animazioni e tiro, senza editor: avvia una partita locale (ENet), impugna
## le armi, mira, spara, ricarica, e salva uno screenshot per ogni passo.
##
## Uso (dalla radice del progetto, dopo `dotnet build`):
##
##   Godot --path . --resolution 1280x720 --script tools/playtest_fps.gd -- <cartella_output> [fp|tp|all]
##
##   fp  = prima persona: viewmodel, mira, raffica, corsa, ricarica, gambe, pistola
##   tp  = terza persona: manichini pilotati a mano (pose, corsa, strafe, morte), poi armati in
##         movimento visti di fianco, primi piani della mira, scavalcamento e arrampicata
##
## In finestra la partita cattura il mouse per una trentina di secondi. Con `--headless` gira lo
## stesso copione senza immagini: serve a controllare che nulla dia errore e che i numeri stampati
## tornino (colpi scalati dal caricatore, ricarica completata).
##
## Cosa guardare negli screenshot: in `fp_*_ads` il puntino verde al centro dello schermo deve
## cadere sulla punta del mirino (e' li' che l'host fa passare il colpo); in `tp_*` le mani devono
## stare sull'arma e la canna puntare davanti al manichino.

var out_dir := ""
var mode := "all"
var shots := 0
var aim_on := false


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


## La mira puo' essere "a pressione" o "a interruttore" (impostazioni): nel secondo caso il
## tasto va premuto e rilasciato, e premerlo due volte la spegne.
func set_aim(on: bool) -> void:
	var toggle: bool = root.get_node("SettingsService").get("AimToggle")
	if toggle:
		if on != aim_on:
			Input.action_press("aim")
			await wait(0.08)
			Input.action_release("aim")
	elif on:
		Input.action_press("aim")
	else:
		Input.action_release("aim")
	aim_on = on


func look(dx: float, dy: float) -> void:
	var motion := InputEventMouseMotion.new()
	motion.screen_relative = Vector2(dx, dy)
	motion.relative = Vector2(dx, dy)
	Input.parse_input_event(motion)


func add_center_dot() -> void:
	var layer := CanvasLayer.new()
	root.add_child(layer)
	var dot := ColorRect.new()
	dot.color = Color(0, 1, 0)
	dot.size = Vector2(2, 2)
	dot.position = Vector2(root.size) * 0.5 - Vector2(1, 1)
	layer.add_child(dot)


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

	if mode == "fp" or mode == "all":
		await first_person(player)
	if mode == "tp" or mode == "all":
		await third_person(main)

	print("[playtest] fine")
	quit(0)


func first_person(player: Node3D) -> void:
	var weapon := player.get_node("Weapon")
	var camera: Camera3D = player.get_node("PlayerCamera")

	add_center_dot()
	await snap("fp_unarmed")
	look(0, 620)
	await wait(0.4)
	await snap("fp_unarmed_look_down")
	look(0, -620)
	await wait(0.3)

	# Slot 7 = arma primaria (EquipSlotType.OnSling): il kit iniziale ci mette il fucile.
	weapon.call("RequestHold", 7)
	await wait(1.3)
	await snap("fp_rifle_hip")
	print("[playtest] impugnato=", weapon.get("HeldItemId"), " caricatore=", weapon.get("MagazineAmmo"))

	await set_aim(true)
	await wait(0.6)
	await snap("fp_rifle_ads")

	for i in 6:
		var aim: Vector3 = camera.global_position - camera.global_transform.basis.z * 30.0
		weapon.call("RequestFire", aim)
		await wait(0.1)
		if i == 2:
			await snap("fp_rifle_ads_firing")
	await wait(0.5)
	await set_aim(false)
	await wait(0.4)
	print("[playtest] dopo la raffica caricatore=", weapon.get("MagazineAmmo"))

	Input.action_press("move_up")
	await wait(0.7)
	await snap("fp_rifle_walk")
	Input.action_press("sprint")
	await wait(0.7)
	await snap("fp_rifle_run")
	Input.action_release("sprint")
	Input.action_release("move_up")
	await wait(0.4)

	weapon.call("RequestReload")
	await wait(1.4)
	await snap("fp_rifle_reload")
	await wait(2.4)
	print("[playtest] dopo la ricarica caricatore=", weapon.get("MagazineAmmo"),
		" in ricarica=", weapon.get("Reloading"))

	look(0, 620)
	await wait(0.4)
	await snap("fp_rifle_look_down")
	look(0, -620)

	Input.action_press("crouch")
	await wait(0.6)
	await snap("fp_rifle_crouch")
	Input.action_release("crouch")
	await wait(0.4)

	# Slot 9 = arma da fianco (EquipSlotType.Holster): la pistola.
	weapon.call("RequestHold", 9)
	await wait(1.2)
	await snap("fp_pistol_hip")
	await set_aim(true)
	await wait(0.5)
	await snap("fp_pistol_ads")
	var aim_point: Vector3 = camera.global_position - camera.global_transform.basis.z * 30.0
	weapon.call("RequestFire", aim_point)
	await wait(0.06)
	await snap("fp_pistol_ads_fire")
	await set_aim(false)
	await wait(0.3)


## Un manichino pilotato a mano: CharacterAnimator e' un ricevitore puro, quindi basta scrivere
## nelle sue proprieta' come farebbe un bridge.
func make_rig(main: Node, pos: Vector3, weapon_path: String, props: Dictionary) -> Node3D:
	var rig: Node3D = load("res://animation/scenes/CharacterRig.tscn").instantiate()
	main.add_child(rig)
	rig.global_position = pos
	if weapon_path != "":
		rig.set("Weapon", load(weapon_path))
	for key in props:
		rig.set(key, props[key])
	return rig


func third_person(main: Node) -> void:
	var rifle := "res://animation/resources/rifle_view.tres"
	var pistol := "res://animation/resources/pistol_view.tres"
	var base := Vector3(-6.0, 0.0, -8.0)
	# I manichini guardano +Z, verso la camera di prova.
	var rigs := [
		make_rig(main, base + Vector3(0, 0, 0), "", {}),
		make_rig(main, base + Vector3(1.5, 0, 0), rifle, {}),
		make_rig(main, base + Vector3(3.0, 0, 0), rifle, {"Aiming": true}),
		make_rig(main, base + Vector3(4.5, 0, 0), rifle, {"Aiming": true, "LookPitch": 0.5}),
		make_rig(main, base + Vector3(6.0, 0, 0), pistol, {"Aiming": true}),
		make_rig(main, base + Vector3(7.5, 0, 0), rifle, {"LocalVelocity": Vector2(0, 7)}),
		make_rig(main, base + Vector3(9.0, 0, 0), rifle, {"LocalVelocity": Vector2(4, 0)}),
		make_rig(main, base + Vector3(10.5, 0, 0), rifle, {"LocalVelocity": Vector2(0, -4)}),
		make_rig(main, base + Vector3(12.0, 0, 0), rifle, {"Crouching": true, "Aiming": true}),
		make_rig(main, base + Vector3(13.5, 0, 0), pistol, {"Crouching": true, "LocalVelocity": Vector2(0, 2)}),
		make_rig(main, base + Vector3(15.0, 0, 0), "", {"LocalVelocity": Vector2(0, 4)}),
		make_rig(main, base + Vector3(16.5, 0, 0), "", {"Grounded": false}),
		make_rig(main, base + Vector3(18.0, 0, 0), rifle, {"Dead": true}),
	]

	var cam := Camera3D.new()
	main.add_child(cam)
	cam.fov = 50.0
	cam.global_position = base + Vector3(9.0, 1.3, 12.0)
	cam.look_at(base + Vector3(9.0, 1.0, 0.0))
	cam.current = true
	await wait(2.6)
	await snap("tp_front")

	# Primi piani: fucile in mira (davanti e dietro) e pistola in mira.
	cam.fov = 35.0
	cam.global_position = base + Vector3(4.6, 1.5, 2.6)
	cam.look_at(base + Vector3(3.0, 1.35, 0.0))
	await wait(0.2)
	await snap("tp_rifle_close")
	cam.global_position = base + Vector3(2.2, 1.5, -1.6)
	cam.look_at(base + Vector3(3.0, 1.35, 0.0))
	await wait(0.2)
	await snap("tp_rifle_back")
	cam.global_position = base + Vector3(7.6, 1.5, 2.4)
	cam.look_at(base + Vector3(6.0, 1.35, 0.0))
	await wait(0.2)
	await snap("tp_pistol_close")
	cam.fov = 45.0
	cam.global_position = base + Vector3(14.0, 1.4, 5.5)
	cam.look_at(base + Vector3(14.5, 0.8, 0.0))
	await wait(0.2)
	await snap("tp_crouch_air_dead")
	for rig in rigs:
		rig.queue_free()

	await armed_in_motion(main, cam, rifle, pistol)
	await parkour(main, cam, rifle)
	cam.queue_free()
	await wait(0.2)


## Il caso che si rompe piu' facilmente: armati E in movimento, visti di fianco. Il busto delle
## clip di corsa e di accovacciata si sporge in avanti: braccia e arma devono seguirlo, non
## restare sull'asse del corpo.
func armed_in_motion(main: Node, cam: Camera3D, rifle: String, pistol: String) -> void:
	var origin := Vector3(-30.0, 0.0, -8.0)
	var cases := [
		["rifle_jog", rifle, {"LocalVelocity": Vector2(0, 4)}],
		["rifle_aim_walk", rifle, {"LocalVelocity": Vector2(0, 2.4), "Aiming": true}],
		["rifle_crouch_walk", rifle, {"LocalVelocity": Vector2(0, 2), "Crouching": true}],
		["rifle_sprint", rifle, {"LocalVelocity": Vector2(0, 7)}],
		["pistol_jog", pistol, {"LocalVelocity": Vector2(0, 4)}],
		["rifle_aim_idle", rifle, {"Aiming": true}],
		["pistol_aim_idle", pistol, {"Aiming": true}],
	]
	cam.fov = 40.0
	for entry in cases:
		var rig := make_rig(main, origin, entry[1], entry[2])
		# Di fianco, leggermente davanti: si leggono insieme l'inclinazione del busto e l'arma.
		cam.global_position = origin + Vector3(-2.6, 1.25, 1.1)
		cam.look_at(origin + Vector3(0.0, 1.1, 0.25))
		await wait(1.2)
		await snap("tp_side_" + entry[0])
		await wait(0.23)
		await snap("tp_side_" + entry[0] + "_b")
		if String(entry[0]).ends_with("aim_idle"):
			cam.global_position = origin + Vector3(0.9, 1.6, 1.3)
			cam.look_at(origin + Vector3(0.0, 1.5, 0.1))
			await wait(0.1)
			await snap("tp_face_" + entry[0])
		rig.queue_free()
		await wait(0.1)


## Scavalcamento e arrampicata senza il motore: il manichino percorre a mano la stessa
## traiettoria di CharacterMotor (VaultPoint / MantlePoint) davanti a un ostacolo finto.
func parkour(main: Node, cam: Camera3D, rifle: String) -> void:
	for mantle in [false, true]:
		var origin := Vector3(-40.0, 0.0, -8.0) if not mantle else Vector3(-50.0, 0.0, -8.0)
		var height := 2.2 if mantle else 1.0
		var duration := 1.4 if mantle else 0.9
		var depth := 1.2 if mantle else 0.3

		var wall := MeshInstance3D.new()
		var box := BoxMesh.new()
		box.size = Vector3(2.4, height, depth)
		wall.mesh = box
		main.add_child(wall)
		wall.global_position = origin + Vector3(0, height * 0.5, 1.0 + depth * 0.5)

		var start := origin
		var ledge := origin + Vector3(0, height, 1.0)
		var end := origin + Vector3(0, 0, 1.0 + depth + 0.65) if not mantle else ledge + Vector3(0, 0, 0.45)

		var rig := make_rig(main, start, rifle, {})
		cam.fov = 45.0
		cam.global_position = origin + Vector3(-4.2, 1.6 if not mantle else 2.2, 0.6)
		cam.look_at(origin + Vector3(0.0, 1.0 if not mantle else 1.7, 1.0))
		await wait(0.8)

		rig.set("Vaulting", true)
		rig.call("TriggerVault", ledge, Vector3(0, 0, -1), mantle, duration)
		var marks := [0.12, 0.3, 0.5, 0.7, 0.9] if not mantle else [0.1, 0.35, 0.6, 0.75, 0.92]
		var next := 0
		var time := 0.0
		while time < duration:
			await process_frame
			time += root.get_process_delta_time()
			var t := clampf(time / duration, 0.0, 1.0)
			rig.global_position = mantle_point(start, ledge, end, t) if mantle else vault_point(start, ledge, end, t)
			if next < marks.size() and t >= marks[next]:
				await snap("tp_%s_%02d" % ["mantle" if mantle else "vault", int(marks[next] * 100)])
				next += 1
		rig.set("Vaulting", false)
		await wait(0.5)
		await snap("tp_%s_end" % ("mantle" if mantle else "vault"))
		rig.queue_free()
		wall.queue_free()


## Stessa curva di CharacterMotor.VaultPoint, ma per l'origine ai PIEDI del rig.
func vault_point(start: Vector3, ledge: Vector3, end: Vector3, t: float) -> Vector3:
	var flat := start.lerp(end, smoothstep(0.0, 1.0, t))
	var apex := ledge.y + 0.08
	var y := lerpf(start.y, apex, smoothstep(0.0, 1.0, t / 0.5)) if t < 0.5 \
		else lerpf(apex, end.y, smoothstep(0.0, 1.0, (t - 0.5) / 0.5))
	return Vector3(flat.x, y, flat.z)


## Stessa curva di CharacterMotor.MantlePoint, per l'origine ai piedi.
func mantle_point(start: Vector3, ledge: Vector3, end: Vector3, t: float) -> Vector3:
	var pull_up := 0.62
	var hang := Vector3(start.x, ledge.y, start.z)
	if t < pull_up:
		var k := smoothstep(0.0, 1.0, t / pull_up)
		var rise := start.lerp(hang, k)
		var approach := start.lerp(Vector3(ledge.x, start.y, ledge.z), k * 0.5)
		return Vector3(approach.x, rise.y, approach.z)
	var s := smoothstep(0.0, 1.0, (t - pull_up) / (1.0 - pull_up))
	var from := Vector3(lerpf(start.x, ledge.x, 0.5), hang.y, lerpf(start.z, ledge.z, 0.5))
	return from.lerp(end, s)
