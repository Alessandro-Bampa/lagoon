@tool
extends EditorPlugin

## Parte "editor" dell'addon:
##   - il gizmo che disegna i segnaposto OSM e li rende cliccabili nella vista 3D;
##   - la barra "Genera edifici / Rimuovi" per i segnaposto selezionati;
##   - "Modifica blocchi": con un solo edificio selezionato, un clic su un muro, un
##     tramezzo, un pavimento o un soffitto apre il menu per sostituirlo.
## La generazione vera sta nei nodi OsmLayer e OsmPlaceholder.

const Generator := preload("building_generator.gd")
const PlaceholderGizmo := preload("placeholder_gizmo.gd")

const KIND_LABELS := {"ext": "Muro esterno", "int": "Tramezzo", "floor": "Pavimento", "ceiling": "Soffitto"}
const FACADE_LABELS := {"ochre": "Ocra", "red": "Rosso", "pink": "Rosa", "yellow": "Giallo", "grey": "Grigio", "brick": "Mattoni"}
const ID_RESET := 900
const ID_COLOR_BASE := 1000

var _bar: HBoxContainer
var _label: Label
var _edit_toggle: Button
var _gizmo: PlaceholderGizmo
var _menu: PopupMenu
var _color_menu: PopupMenu
var _target: OsmPlaceholder
var _picked: Dictionary = {}
var _options: PackedStringArray = []
var _highlight: MeshInstance3D


func _enter_tree() -> void:
	_gizmo = PlaceholderGizmo.new()
	add_node_3d_gizmo_plugin(_gizmo)

	_bar = HBoxContainer.new()
	_bar.add_child(VSeparator.new())
	_label = Label.new()
	_bar.add_child(_label)
	var generate := Button.new()
	generate.text = "Genera edifici"
	generate.tooltip_text = "Genera (o rigenera) gli edifici dei segnaposto OSM selezionati, con kit e colore scelti nell'ispettore."
	generate.pressed.connect(_run.bind("generate_placeholders"))
	_bar.add_child(generate)
	var clear := Button.new()
	clear.text = "Rimuovi"
	clear.tooltip_text = "Toglie gli edifici generati dai segnaposto selezionati."
	clear.pressed.connect(_run.bind("clear_placeholders"))
	_bar.add_child(clear)
	_edit_toggle = Button.new()
	_edit_toggle.text = "Modifica blocchi"
	_edit_toggle.toggle_mode = true
	_edit_toggle.tooltip_text = "Acceso: un clic su un muro, un tramezzo, un pavimento o un soffitto dell'edificio selezionato apre il menu per sostituirlo."
	_bar.add_child(_edit_toggle)
	add_control_to_container(CONTAINER_SPATIAL_EDITOR_MENU, _bar)

	_menu = PopupMenu.new()
	_menu.id_pressed.connect(_on_menu_id)
	_menu.popup_hide.connect(_clear_highlight)
	_color_menu = PopupMenu.new()
	_color_menu.id_pressed.connect(_on_menu_id)
	EditorInterface.get_base_control().add_child(_menu)

	EditorInterface.get_selection().selection_changed.connect(_refresh)
	_refresh()


func _exit_tree() -> void:
	EditorInterface.get_selection().selection_changed.disconnect(_refresh)
	_clear_highlight()
	remove_node_3d_gizmo_plugin(_gizmo)
	remove_control_from_container(CONTAINER_SPATIAL_EDITOR_MENU, _bar)
	_bar.queue_free()
	_menu.queue_free()
	_color_menu.queue_free()


# ------------------------------ selezione ------------------------------

func _placeholder_of(node: Node) -> OsmPlaceholder:
	var current := node
	while current != null and not current is OsmPlaceholder:
		current = current.get_parent()
	return current as OsmPlaceholder


## Segnaposto selezionati; vale anche selezionare l'edificio generato sotto di loro.
func _selected() -> Array:
	var found := {}
	for node in EditorInterface.get_selection().get_selected_nodes():
		var placeholder := _placeholder_of(node)
		if placeholder != null:
			found[placeholder] = true
	return found.keys()


func _refresh() -> void:
	var list := _selected()
	_bar.visible = not list.is_empty()
	_label.text = "OSM: %d segnaposto" % list.size()
	_edit_toggle.disabled = list.size() != 1 or not (list[0] as OsmPlaceholder).is_generated()
	if _edit_toggle.disabled:
		_edit_toggle.button_pressed = false


func _layer_of(placeholder: Node) -> Node:
	var layer := placeholder.get_parent()
	while layer != null and not layer is OsmLayer:
		layer = layer.get_parent()
	return layer


func _run(method: String) -> void:
	# Un layer per volta: i segnaposto si raggruppano per il loro OsmLayer.
	var by_layer := {}
	for placeholder: Node in _selected():
		var layer := _layer_of(placeholder)
		if layer != null:
			if not by_layer.has(layer):
				by_layer[layer] = []
			by_layer[layer].append(placeholder)
	for layer: Node in by_layer:
		layer.call(method, by_layer[layer])
	_refresh()


# ------------------------------ modifica dei blocchi ------------------------------

func _handles(object: Object) -> bool:
	return object is Node and _placeholder_of(object as Node) != null


func _forward_3d_gui_input(camera: Camera3D, event: InputEvent) -> int:
	if not _edit_toggle.button_pressed or _edit_toggle.disabled:
		return AFTER_GUI_INPUT_PASS
	var click := event as InputEventMouseButton
	if click == null or not click.pressed or click.button_index != MOUSE_BUTTON_LEFT:
		return AFTER_GUI_INPUT_PASS
	if click.shift_pressed or click.ctrl_pressed or click.alt_pressed:
		return AFTER_GUI_INPUT_PASS
	var list := _selected()
	if list.size() != 1:
		return AFTER_GUI_INPUT_PASS
	var placeholder: OsmPlaceholder = list[0]
	var block := placeholder.pick_block(camera.project_ray_origin(click.position), camera.project_ray_normal(click.position))
	if block.is_empty():
		return AFTER_GUI_INPUT_PASS
	_target = placeholder
	_picked = block
	_show_highlight()
	_show_menu()
	return AFTER_GUI_INPUT_STOP


func _show_menu() -> void:
	var kind: String = _picked["t"]
	var key: String = _picked["k"]
	var current: String = _picked["m"]
	var library := Generator.new().library(OsmPlaceholder.KIT_NAMES[_target.kit])
	_options = Generator.options_for(kind, library)
	_menu.clear()
	_menu.add_item("%s  [%s]" % [KIND_LABELS.get(kind, kind), key], 899)
	_menu.set_item_disabled(0, true)
	_menu.add_separator()
	for i in _options.size():
		_menu.add_radio_check_item("(nessuno: varco)" if _options[i] == "-" else _options[i], i)
		_menu.set_item_checked(_menu.get_item_index(i), _options[i] == current)
	if kind == "ext":
		_color_menu.clear()
		_color_menu.add_item("Come l'edificio", ID_COLOR_BASE)
		for i in Generator.KitLibrary.FACADES.size():
			var name: String = Generator.KitLibrary.FACADES[i]
			_color_menu.add_radio_check_item(FACADE_LABELS.get(name, name), ID_COLOR_BASE + 1 + i)
			_color_menu.set_item_checked(i + 1, _target.overrides.get(key + "#c", "") == name)
		_menu.add_separator()
		if _color_menu.get_parent() != null:
			_color_menu.get_parent().remove_child(_color_menu)
		_menu.add_submenu_node_item("Colore di questo blocco", _color_menu)
	_menu.add_separator()
	_menu.add_item("Ripristina questo blocco", ID_RESET)
	_menu.set_item_disabled(_menu.get_item_index(ID_RESET), not (_target.overrides.has(key) or _target.overrides.has(key + "#c")))
	_menu.popup(Rect2i(DisplayServer.mouse_get_position(), Vector2i.ZERO))


func _on_menu_id(id: int) -> void:
	if _target == null or not is_instance_valid(_target) or _picked.is_empty():
		return
	var key: String = _picked["k"]
	var changed := _target.overrides.duplicate()
	if id == ID_RESET:
		changed.erase(key)
		changed.erase(key + "#c")
	elif id == ID_COLOR_BASE:
		changed.erase(key + "#c")
	elif id > ID_COLOR_BASE:
		changed[key + "#c"] = Generator.KitLibrary.FACADES[id - ID_COLOR_BASE - 1]
	elif id >= 0 and id < _options.size():
		changed[key] = _options[id]
	else:
		return
	_target.overrides = changed
	var layer := _layer_of(_target)
	if layer != null:
		layer.call("generate_placeholders", [_target])
	_picked = {}


func _show_highlight() -> void:
	_clear_highlight()
	var building := _target.get_node_or_null(OsmPlaceholder.BUILDING_NODE) as Node3D
	if building == null:
		return
	var box: AABB = _picked["b"]
	if box.size == Vector3.ZERO:
		return  # pavimenti e soffitti: tutta la pianta, nessun riquadro
	var mesh := BoxMesh.new()
	mesh.size = box.size + Vector3(0.06, 0.06, 0.06)
	var material := StandardMaterial3D.new()
	material.albedo_color = Color(1.0, 0.8, 0.1, 0.35)
	material.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
	material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	material.no_depth_test = true
	mesh.material = material
	_highlight = MeshInstance3D.new()
	_highlight.mesh = mesh
	_highlight.transform = building.transform * (_picked["x"] as Transform3D) * Transform3D(Basis.IDENTITY, box.get_center())
	# Figlio interno: non ha proprietario, non finisce nella scena salvata.
	_target.add_child(_highlight, false, Node.INTERNAL_MODE_BACK)


func _clear_highlight() -> void:
	if _highlight != null and is_instance_valid(_highlight):
		_highlight.queue_free()
	_highlight = null
