extends SceneTree
## Fotografa i modelli 3D degli item e ne ricava le icone dell'inventario.
##
## Per ogni definizione in res://resources/items/ cerca il modello
## res://assets/models/items/<ItemId>.glb (o quello indicato in MODEL_OVERRIDES) e scrive
## res://assets/textures/items/<ItemId>.png, con le proporzioni dell'ingombro in celle.
## ItemDefinition.ResolveIcon() preferisce il .png al vecchio .svg.
##
## Uso (NON headless: serve il renderer):
##   Godot_v4.7.1-stable_mono_win64_console.exe --path . --script tools/bake_item_icons.gd
## poi `--headless --import` per importare i .png nuovi. Opzionale: `-- <id> <id>` limita gli item.

const ITEMS_DIR := "res://resources/items"
const MODELS_DIR := "res://assets/models/items"
const OUT_DIR := "res://assets/textures/items"
const CELL_PX := 128

## Item il cui modello non segue la convenzione di percorso.
const MODEL_OVERRIDES := {
	"rifle": "res://assets/models/weapons/W_Rifle.glb",
	"pistol": "res://assets/models/weapons/W_Pistol.glb",
}

## Categorie fotografate di profilo, senza inclinazione (armi): vedi ItemCategory.
const PROFILE_CATEGORIES := [3, 5, 6]


func _initialize() -> void:
	_run.call_deferred()


func _run() -> void:
	var only := OS.get_cmdline_user_args()
	var viewport := SubViewport.new()
	viewport.own_world_3d = true
	viewport.transparent_bg = true
	viewport.msaa_3d = Viewport.MSAA_4X
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(viewport)

	var camera := Camera3D.new()
	camera.projection = Camera3D.PROJECTION_ORTHOGONAL
	camera.near = 0.01
	camera.far = 100.0
	var env := Environment.new()
	env.background_mode = Environment.BG_CLEAR_COLOR
	env.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	env.ambient_light_color = Color.WHITE
	env.ambient_light_energy = 0.75
	camera.environment = env
	viewport.add_child(camera)

	var sun := DirectionalLight3D.new()
	sun.light_energy = 1.1
	viewport.add_child(sun)

	var baked := 0
	for file in DirAccess.get_files_at(ITEMS_DIR):
		if not file.ends_with(".tres"):
			continue
		var def: Resource = load(ITEMS_DIR + "/" + file)
		if def == null:
			continue
		var id: String = def.get("ItemId")
		if not only.is_empty() and not only.has(id):
			continue

		var model_path: String = MODEL_OVERRIDES.get(id, "%s/%s.glb" % [MODELS_DIR, id])
		if not ResourceLoader.exists(model_path):
			continue

		var model: Node3D = (load(model_path) as PackedScene).instantiate()
		viewport.add_child(model)
		var w: int = def.get("Width")
		var h: int = def.get("Height")
		viewport.size = Vector2i(w, h) * CELL_PX
		_frame(camera, sun, model, float(w) / float(h), PROFILE_CATEGORIES.has(int(def.get("Category"))))

		await process_frame
		await process_frame
		await RenderingServer.frame_post_draw

		var image := viewport.get_texture().get_image()
		var out := ProjectSettings.globalize_path("%s/%s.png" % [OUT_DIR, id])
		image.save_png(out)
		print("[bake] ", id, " -> ", out, "  (", w, "x", h, ")")
		baked += 1

		viewport.remove_child(model)
		model.free()

	print("[bake] icone prodotte: ", baked)
	quit()


## Inquadra il modello: si guarda lungo l'asse piu' corto (la faccia piu' ampia verso la camera) e
## l'asse piu' lungo va lungo il lato lungo dell'icona.
func _frame(camera: Camera3D, sun: DirectionalLight3D, model: Node3D, aspect: float, profile: bool) -> void:
	var box := _bounds(model)
	var size := box.size
	var axes := [Vector3.RIGHT, Vector3.UP, Vector3.BACK]
	var order := [0, 1, 2]
	order.sort_custom(func(a: int, b: int) -> bool: return size[a] < size[b])

	var view_axis: Vector3 = axes[order[0]]
	var long_axis: Vector3 = axes[order[2]]
	var mid_axis: Vector3 = axes[order[1]]

	# Verticale dell'immagine: l'asse lungo per le icone alte, quello medio per le larghe. Se uno
	# dei due e' la verticale del mondo si preferisce quella, cosi' gli oggetti restano dritti.
	var up: Vector3 = long_axis if aspect < 1.0 else mid_axis
	if is_equal_approx(aspect, 1.0) and long_axis == Vector3.UP:
		up = long_axis
	if order[0] == 1:
		view_axis = Vector3.UP # dall'alto, non da sotto

	var center := box.get_center()
	var basis := Basis.looking_at(-view_axis, up)
	if not profile:
		# Tre quarti leggero: da' volume agli oggetti senza cambiarne la sagoma.
		basis = basis * Basis(Vector3.UP, deg_to_rad(24.0)) * Basis(Vector3.RIGHT, deg_to_rad(-20.0))

	camera.global_transform = Transform3D(basis, center + basis.z * 10.0)
	sun.global_transform = Transform3D(basis * Basis(Vector3.RIGHT, deg_to_rad(-35.0)) * Basis(Vector3.UP, deg_to_rad(30.0)), center)

	# Estensione sullo schermo degli otto vertici del volume, per riempire l'immagine con margine.
	var half_w := 0.0
	var half_h := 0.0
	for i in 8:
		var corner := box.get_endpoint(i) - center
		half_w = maxf(half_w, absf(corner.dot(basis.x)))
		half_h = maxf(half_h, absf(corner.dot(basis.y)))
	camera.keep_aspect = Camera3D.KEEP_HEIGHT
	camera.size = maxf(half_h * 2.0, half_w * 2.0 / aspect) * 1.1


func _bounds(model: Node3D) -> AABB:
	var box := AABB()
	var first := true
	for node in model.find_children("*", "MeshInstance3D", true, false) + [model]:
		if node is MeshInstance3D:
			var local: AABB = node.global_transform * node.get_aabb()
			box = local if first else box.merge(local)
			first = false
	return box
