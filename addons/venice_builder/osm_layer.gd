@tool
class_name OsmLayer
extends Node3D

## Livello di dati OpenStreetMap dentro una mappa: importa gli elementi come
## segnaposto (OsmPlaceholder) e ne genera gli asset col kit dei palazzi.
##
## Flusso:
##   1. "Scarica da Overpass": lancia tools/osm_extract.py sul riquadro `bbox` e
##      riscrive `osm_json` (in coordinate mondo della mappa).
##   2. "Importa i segnaposto": un cilindretto per ogni edificio del JSON non
##      ancora importato (riconosciuto dall'id OSM).
##   3. Selezionare uno o piu' segnaposto, scegliere kit e colore nell'ispettore,
##      "Genera edifici" nella barra della vista 3D.
##
## Tooling di sola generazione (per questo e' GDScript e non C#): in gioco questi
## script non fanno nulla, gli edifici generati sono solo mesh e collisioni.
##
## Il nodo va lasciato a trasformata identita': le coordinate del JSON sono gia'
## quelle del mondo.

const Generator := preload("building_generator.gd")
const Poly := preload("polygon_tools.gd")
const TerrainSampler := preload("terrain_sampler.gd")
const PlaceholderScript := preload("osm_placeholder.gd")
const EXTRACTOR := "res://addons/venice_builder/tools/osm_extract.py"
## Tipi di `building=*` che non sono palazzi: restano senza segnaposto.
const SKIPPED_KINDS: PackedStringArray = ["roof", "column", "bridge", "ruins", "construction"]

## JSON prodotto da osm_extract.py.
@export_file("*.json") var osm_json := "res://demo/venice/venice_osm.json"
## Riquadro da scaricare: sud,ovest,nord,est in gradi (come in overpass-turbo).
@export var bbox := "45.4270,12.3330,45.4365,12.3460"
## Cartella delle region di Terrain3D da cui leggere la quota d'appoggio.
@export_dir var terrain_directory := "res://demo/data/terrain_polished"
## Dove salvare gli edifici generati (una scena .scn per edificio).
@export_dir var output_directory := "res://world/generated/buildings"
## Oltre questa distanza scale e tramezzi non si disegnano (0 = sempre).
@export var interior_range := 80.0
## Comando Python per lo scaricamento.
@export var python_command := "python"

@export_group("Nuovi segnaposto")
@export var default_kit: PlaceholderScript.Kit = PlaceholderScript.Kit.NORMALE
@export var default_facade: PlaceholderScript.Facade = PlaceholderScript.Facade.CASUALE

@export_group("Azioni")
@export_tool_button("1. Scarica da Overpass", "AssetLib") var _download_action := download_osm
@export_tool_button("2. Importa i segnaposto", "Add") var _import_action := import_placeholders
@export_tool_button("Genera tutti i mancanti", "Play") var _generate_all_action := generate_missing
@export_tool_button("Rimuovi tutti gli edifici generati", "Remove") var _clear_all_action := clear_all

var _doc: Dictionary = {}
var _doc_key := ""
var _generator: Generator


## Scarica il riquadro da Overpass e riscrive `osm_json`. All'esterno parte solo
## il riquadro geografico.
func download_osm() -> void:
	var args := PackedStringArray([ProjectSettings.globalize_path(EXTRACTOR), "--bbox", bbox,
		"--out", ProjectSettings.globalize_path(osm_json)])
	print("[venice_builder] %s %s" % [python_command, " ".join(args)])
	var output: Array = []
	var code := OS.execute(python_command, args, output, true)
	for line in output:
		print(line)
	if code != 0:
		push_error("[venice_builder] Scaricamento fallito (codice %d)." % code)
		return
	_doc_key = ""
	_rescan()


## Crea un segnaposto per ogni edificio del JSON non ancora importato.
## Ritorna quanti ne ha aggiunti.
func import_placeholders() -> int:
	var doc := _load_doc()
	if doc.is_empty():
		return 0
	var known := {}
	for p in placeholders():
		known[p.osm_id] = true
	var sampler := TerrainSampler.new(terrain_directory)
	var scene_owner := _scene_owner()
	var added := 0
	for item: Dictionary in doc.get("buildings", []):
		var id: String = item["id"]
		if known.has(id) or SKIPPED_KINDS.has(str(item.get("kind", ""))):
			continue
		var ring := Poly.ring_from_json(item["rings"][0])
		if ring.size() < 3 or absf(Poly.area(ring)) < 4.0:
			continue
		var center := Generator.footprint_center(item)
		var placeholder: OsmPlaceholder = PlaceholderScript.new()
		placeholder.name = "B_" + id.replace("/", "_").replace("#", "_")
		placeholder.osm_id = id
		placeholder.osm_layer = "buildings"
		placeholder.kit = default_kit
		placeholder.facade = default_facade
		placeholder.info = _describe(item)
		placeholder.position = Vector3(center.x, sampler.ground_stats(ring)["ground"], center.y)
		add_child(placeholder)
		placeholder.owner = scene_owner
		known[id] = true
		added += 1
	print("[venice_builder] %d segnaposto aggiunti, %d gia' presenti." % [added, known.size() - added])
	_mark_unsaved()
	return added


func placeholders() -> Array[OsmPlaceholder]:
	var out: Array[OsmPlaceholder] = []
	for child in get_children():
		if child is OsmPlaceholder:
			out.append(child)
	return out


func generate_missing() -> void:
	var todo: Array = []
	for p in placeholders():
		if not p.is_generated():
			todo.append(p)
	generate_placeholders(todo)


func clear_all() -> void:
	clear_placeholders(placeholders())


## Genera (o rigenera) gli edifici dei segnaposto dati.
func generate_placeholders(list: Array) -> void:
	var doc := _load_doc()
	if doc.is_empty() or list.is_empty():
		return
	var index := {}
	var footprints: Array = []  # [Rect2, anello esterno, id]
	for item: Dictionary in doc.get("buildings", []):
		index[item["id"]] = item
		if SKIPPED_KINDS.has(str(item.get("kind", ""))):
			continue
		var ring := Poly.ring_from_json(item["rings"][0])
		if ring.size() >= 3:
			footprints.append([_bounds(ring), ring, item["id"]])
	if _generator == null:
		_generator = Generator.new()
	var sampler := TerrainSampler.new(terrain_directory)
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(output_directory))
	var scene_owner := _scene_owner()
	var started := Time.get_ticks_msec()
	var done := 0
	var triangles := 0
	var without_stairs := 0
	for placeholder: OsmPlaceholder in list:
		var item: Dictionary = index.get(placeholder.osm_id, {})
		if item.is_empty():
			push_warning("[venice_builder] %s: id %s assente da %s." % [placeholder.name, placeholder.osm_id, osm_json])
			continue
		var ring := Poly.ring_from_json(item["rings"][0])
		var bounds := _bounds(ring).grow(1.5)
		var neighbours: Array = []
		for entry: Array in footprints:
			if entry[2] != item["id"] and bounds.intersects(entry[0]):
				neighbours.append(entry[1])
		var settings := placeholder.settings()
		settings["interior_range"] = interior_range
		var ground := sampler.ground_stats(ring)
		var building := _generator.generate(item, settings, neighbours, ground)
		if building == null:
			push_warning("[venice_builder] %s: impronta non utilizzabile." % placeholder.name)
			continue
		var stats := _generator.last_stats()
		triangles += stats.get("triangles", 0)
		if settings["interiors"] and Generator.floors_of(item) > 1 and not stats.get("stairs", false):
			without_stairs += 1
		var path := "%s/%s.scn" % [output_directory.trim_suffix("/"), placeholder.name]
		var packed := PackedScene.new()
		var err := packed.pack(building)
		if err == OK:
			err = ResourceSaver.save(packed, path, ResourceSaver.FLAG_COMPRESS)
		building.free()
		if err != OK:
			push_error("[venice_builder] Salvataggio di %s fallito (errore %d)." % [path, err])
			continue
		_remove_building(placeholder)
		var scene: PackedScene = ResourceLoader.load(path, "", ResourceLoader.CACHE_MODE_REPLACE)
		var instance := scene.instantiate()
		instance.name = OsmPlaceholder.BUILDING_NODE
		placeholder.add_child(instance)
		instance.owner = scene_owner
		placeholder.position.y = ground["ground"]
		placeholder.update_marker()
		done += 1
		if done % 50 == 0:
			print("[venice_builder] %d / %d ..." % [done, list.size()])
	print("[venice_builder] %d edifici generati in %.1f s, %d triangoli; %d a piu' piani senza scala (troppo stretti)." % [
		done, (Time.get_ticks_msec() - started) / 1000.0, triangles, without_stairs])
	_mark_unsaved()
	_rescan()


## Toglie l'edificio generato (e la sua scena su disco) dai segnaposto dati.
func clear_placeholders(list: Array) -> void:
	for placeholder: OsmPlaceholder in list:
		if not placeholder.is_generated():
			continue
		_remove_building(placeholder)
		var path := "%s/%s.scn" % [output_directory.trim_suffix("/"), placeholder.name]
		if FileAccess.file_exists(path):
			DirAccess.remove_absolute(ProjectSettings.globalize_path(path))
		placeholder.update_marker()
	_mark_unsaved()
	_rescan()


func _remove_building(placeholder: OsmPlaceholder) -> void:
	var old := placeholder.get_node_or_null(OsmPlaceholder.BUILDING_NODE)
	if old != null:
		placeholder.remove_child(old)
		old.free()


func _describe(item: Dictionary) -> String:
	var tags: Dictionary = item.get("tags", {})
	var lines := PackedStringArray()
	if tags.has("name"):
		lines.append(str(tags["name"]))
	lines.append("building=%s" % item.get("kind", "yes"))
	lines.append("piani: %d (altezza %.1f m, da %s)" % [Generator.floors_of(item), item.get("h", 0.0), item.get("h_src", "?")])
	return "\n".join(lines)


func _bounds(ring: PackedVector2Array) -> Rect2:
	var rect := Rect2(ring[0], Vector2.ZERO)
	for p in ring:
		rect = rect.expand(p)
	return rect


func _load_doc() -> Dictionary:
	var key := "%s@%d" % [osm_json, FileAccess.get_modified_time(osm_json)]
	if key == _doc_key:
		return _doc
	var text := FileAccess.get_file_as_string(osm_json)
	if text.is_empty():
		push_error("[venice_builder] Impossibile leggere %s: scarica prima i dati." % osm_json)
		return {}
	var parsed = JSON.parse_string(text)
	if not parsed is Dictionary:
		push_error("[venice_builder] %s non e' un JSON di osm_extract.py." % osm_json)
		return {}
	_doc = parsed
	_doc_key = key
	return _doc


## Proprietario dei nodi creati: la scena aperta nell'editor (cosi' vengono
## salvati con lei), altrimenti quello del layer.
func _scene_owner() -> Node:
	if Engine.is_editor_hint() and is_inside_tree() and get_tree().edited_scene_root != null:
		return get_tree().edited_scene_root
	return owner if owner != null else self


func _editor() -> Object:
	# Niente riferimento diretto a EditorInterface: lo script esiste anche in gioco.
	return Engine.get_singleton("EditorInterface") if Engine.is_editor_hint() and Engine.has_singleton("EditorInterface") else null


func _mark_unsaved() -> void:
	var editor := _editor()
	if editor != null:
		editor.call("mark_scene_as_unsaved")


func _rescan() -> void:
	var editor := _editor()
	if editor != null:
		editor.call("get_resource_filesystem").call("scan")
