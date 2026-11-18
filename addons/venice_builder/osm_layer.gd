@tool
class_name OsmLayer
extends Node3D

## Livello di dati OpenStreetMap dentro una mappa: importa gli elementi come
## segnaposto (OsmPlaceholder) e ne genera gli asset col kit dei palazzi.
##
## Flusso:
##   1. "Scarica da Overpass": lancia tools/osm_extract.py. Con `map_json` (mappa
##      vera, es. world/maps/laguna) riscarica tutta la mappa (`--map`, a tasselli con
##      cache) e riscrive i livelli in <mappa>/osm/layers; senza, scarica il riquadro
##      `bbox` in `osm_json`. In ogni caso le coordinate sono quelle del mondo della mappa.
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
##
## Struttura prodotta (pensata per reggere una citta' intera):
##   OsmLayer / Chunk_<x>_<z> (OsmChunk, griglia di `chunk_size` metri)
##       Far                 LOD lontano di tutto il settore, una mesh sola
##       B_way_123 (OsmPlaceholder) / Building (Shell, Interior, Occluder, Collision)
## Su disco, in `output_directory`: <segnaposto>.scn, far/<segnaposto>.res (il LOD
## lontano del singolo edificio, materia prima della fusione) e chunks/<chunk>.res.

const Generator := preload("building_generator.gd")
const Poly := preload("polygon_tools.gd")
const TerrainSampler := preload("terrain_sampler.gd")
const PlaceholderScript := preload("osm_placeholder.gd")
const Chunk := preload("osm_chunk.gd")
const FAR_MARGIN := 10.0   # m di isteresi nello scambio vicino/lontano
const EXTRACTOR := "res://addons/venice_builder/tools/osm_extract.py"
## Tipi di `building=*` che non sono palazzi: restano senza segnaposto.
const SKIPPED_KINDS: PackedStringArray = ["roof", "column", "bridge", "ruins", "construction"]

## map.json della mappa vera: georeferenziazione e poligono da scaricare.
@export_file("*.json") var map_json := "res://world/maps/laguna/map.json"
## JSON degli edifici prodotto da osm_extract.py (con --map: <mappa>/osm/layers/buildings.json).
@export_file("*.json") var osm_json := "res://world/maps/laguna/osm/layers/buildings.json"
## Solo senza `map_json`: riquadro da scaricare, sud,ovest,nord,est in gradi.
@export var bbox := ""
## Cartella delle region di Terrain3D da cui leggere la quota d'appoggio.
@export_dir var terrain_directory := "res://world/maps/laguna/terrain"
## Dove salvare gli edifici generati (una scena .scn per edificio).
@export_dir var output_directory := "res://world/generated/buildings"
## Oltre questa distanza solai, scale e tramezzi non si disegnano (0 = sempre).
@export var interior_range := 60.0
## Lato dei settori della griglia, in metri. Cambiarlo non sposta i segnaposto
## gia' smistati: va deciso prima di importare.
@export var chunk_size := 100.0
## Oltre questa distanza dal centro di un settore i suoi edifici vengono
## sostituiti dal LOD lontano fuso. Dev'essere maggiore di `chunk_size`.
@export var far_range := 120.0
## LOD automatici e mesh d'ombra sulle mesh degli edifici (ImporterMesh).
@export var mesh_lods := true
## Comando Python per lo scaricamento.
@export var python_command := "python"

@export_group("Nuovi segnaposto")
@export var default_kit: PlaceholderScript.Kit = PlaceholderScript.Kit.NORMALE
@export var default_facade: PlaceholderScript.Facade = PlaceholderScript.Facade.CASUALE

@export_group("Azioni")
@export_tool_button("1. Scarica da Overpass", "AssetLib") var _download_action := download_osm
@export_tool_button("2. Importa i segnaposto", "Add") var _import_action := import_placeholders
@export_tool_button("Genera tutti i mancanti", "Play") var _generate_all_action := generate_missing
@export_tool_button("Rigenera tutti quelli gia' generati", "Reload") var _regenerate_action := regenerate_generated
@export_tool_button("Rimuovi tutti gli edifici generati", "Remove") var _clear_all_action := clear_all

var _doc: Dictionary = {}
var _doc_key := ""
var _generator: Generator


## Scarica il riquadro da Overpass e riscrive `osm_json`. All'esterno parte solo
## il riquadro geografico.
func download_osm() -> void:
	var args := PackedStringArray([ProjectSettings.globalize_path(EXTRACTOR)])
	if not map_json.is_empty():
		args.append_array(["--map", ProjectSettings.globalize_path(map_json)])
	elif not bbox.is_empty():
		args.append_array(["--bbox", bbox, "--out", ProjectSettings.globalize_path(osm_json)])
	else:
		push_error("[venice_builder] Serve map_json oppure bbox per scaricare.")
		return
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
	organize_chunks()
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
		var chunk := _chunk_at(center.x, center.y)
		placeholder.position = Vector3(center.x, sampler.ground_stats(ring)["ground"], center.y) - chunk.position
		chunk.add_child(placeholder)
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
		elif child is Chunk:
			for sub in child.get_children():
				if sub is OsmPlaceholder:
					out.append(sub)
	return out


## Sposta nei settori della griglia i segnaposto ancora figli diretti del layer
## (scene nate prima dei chunk). Ritorna quanti ne ha spostati.
func organize_chunks() -> int:
	var scene_owner := _scene_owner()
	var moved := 0
	for child in get_children():
		var placeholder := child as OsmPlaceholder
		if placeholder == null:
			continue
		var world := placeholder.position
		var chunk := _chunk_at(world.x, world.z)
		var building := placeholder.get_node_or_null(OsmPlaceholder.BUILDING_NODE)
		# Il proprietario va tolto prima di cambiare ramo e rimesso dopo.
		placeholder.owner = null
		if building != null:
			building.owner = null
		remove_child(placeholder)
		chunk.add_child(placeholder)
		placeholder.position = world - chunk.position
		placeholder.owner = scene_owner
		if building != null:
			building.owner = scene_owner
		moved += 1
	if moved > 0:
		print("[venice_builder] %d segnaposto smistati nei settori da %d m." % [moved, roundi(chunk_size)])
		_mark_unsaved()
	return moved


func generate_missing() -> void:
	var todo: Array = []
	for p in placeholders():
		if not p.is_generated():
			todo.append(p)
	generate_placeholders(todo)


## Rigenera gli edifici gia' presenti: serve dopo un aggiornamento del generatore
## (o del kit) per riportare tutto alla stessa versione.
func regenerate_generated() -> void:
	var todo: Array = []
	for p in placeholders():
		if p.is_generated():
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
	var base := output_directory.trim_suffix("/")
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(base + "/far"))
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(base + "/chunks"))
	var scene_owner := _scene_owner()
	organize_chunks()
	var started := Time.get_ticks_msec()
	var done := 0
	var triangles := 0
	var far_triangles := 0
	var loot_points := 0
	var without_stairs := 0
	var dirty := {}   # settori il cui LOD lontano va rifuso
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
		settings["lods"] = mesh_lods
		var ground := sampler.ground_stats(ring)
		var building := _generator.generate(item, settings, neighbours, ground)
		if building == null:
			push_warning("[venice_builder] %s: impronta non utilizzabile." % placeholder.name)
			continue
		var stats := _generator.last_stats()
		triangles += stats.get("triangles", 0)
		far_triangles += stats.get("far_triangles", 0)
		loot_points += stats.get("loot_points", 0)
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
		var far_mesh := _generator.last_far_mesh()
		if far_mesh != null:
			ResourceSaver.save(far_mesh, _far_path(placeholder), ResourceSaver.FLAG_COMPRESS)
		else:
			_remove_file(_far_path(placeholder))
		if placeholder.get_parent() is Chunk:
			dirty[placeholder.get_parent()] = true
		_remove_building(placeholder)
		var scene: PackedScene = ResourceLoader.load(path, "", ResourceLoader.CACHE_MODE_REPLACE)
		# Come istanza "da editor": cosi' la scena della mappa salva solo il riferimento
		# all'edificio, non una copia dei suoi metadati (la tabella dei blocchi).
		var instance := scene.instantiate(PackedScene.GEN_EDIT_STATE_INSTANCE)
		instance.name = OsmPlaceholder.BUILDING_NODE
		placeholder.add_child(instance)
		instance.owner = scene_owner
		placeholder.position.y = ground["ground"]
		placeholder.update_marker()
		done += 1
		if done % 50 == 0:
			print("[venice_builder] %d / %d ..." % [done, list.size()])
	for chunk: Node3D in dirty:
		_rebuild_chunk(chunk)
	print("[venice_builder] %d edifici generati in %.1f s, %d triangoli (%d nel LOD lontano), %d settori, %d punti di loot; %d a piu' piani senza scala (troppo stretti)." % [
		done, (Time.get_ticks_msec() - started) / 1000.0, triangles, far_triangles, dirty.size(), loot_points, without_stairs])
	_mark_unsaved()
	_rescan()


## Toglie l'edificio generato (e la sua scena su disco) dai segnaposto dati.
func clear_placeholders(list: Array) -> void:
	var dirty := {}
	for placeholder: OsmPlaceholder in list:
		if not placeholder.is_generated():
			continue
		_remove_building(placeholder)
		_remove_file("%s/%s.scn" % [output_directory.trim_suffix("/"), placeholder.name])
		_remove_file(_far_path(placeholder))
		if placeholder.get_parent() is Chunk:
			dirty[placeholder.get_parent()] = true
		placeholder.update_marker()
	for chunk: Node3D in dirty:
		_rebuild_chunk(chunk)
	_mark_unsaved()
	_rescan()


func _far_path(placeholder: Node) -> String:
	return "%s/far/%s.res" % [output_directory.trim_suffix("/"), placeholder.name]


func _remove_file(path: String) -> void:
	if FileAccess.file_exists(path):
		DirAccess.remove_absolute(ProjectSettings.globalize_path(path))


## Settore della griglia che contiene il punto (x, z mondo); lo crea se manca.
func _chunk_at(x: float, z: float) -> Node3D:
	var cx := floori(x / chunk_size)
	var cz := floori(z / chunk_size)
	var chunk_name := "Chunk_%d_%d" % [cx, cz]
	var chunk := get_node_or_null(chunk_name) as Node3D
	if chunk == null:
		chunk = Chunk.new()
		chunk.name = chunk_name
		chunk.position = Vector3(cx * chunk_size, 0.0, cz * chunk_size)
		add_child(chunk)
		chunk.owner = _scene_owner()
	return chunk


## Rifonde il LOD lontano di un settore dai LOD dei suoi edifici (una superficie
## per materiale) e ne aggiorna i punti di loot. Costa poco: solo copie di array.
func _rebuild_chunk(chunk: Node3D) -> void:
	var tools := {}       # materiale -> SurfaceTool
	var materials := {}
	var loot := PackedVector3Array()
	for child in chunk.get_children():
		var placeholder := child as OsmPlaceholder
		if placeholder == null or not placeholder.is_generated():
			continue
		var offset := Transform3D(Basis.IDENTITY, placeholder.position)
		var building := placeholder.get_node(OsmPlaceholder.BUILDING_NODE)
		for point: Vector3 in building.get_meta("loot_points", PackedVector3Array()):
			loot.append(offset * point)
		var far_path := _far_path(placeholder)
		if not FileAccess.file_exists(far_path):
			continue
		var mesh: ArrayMesh = ResourceLoader.load(far_path, "", ResourceLoader.CACHE_MODE_REPLACE)
		if mesh == null:
			continue
		for s in mesh.get_surface_count():
			var mat_name := mesh.surface_get_name(s)
			if not tools.has(mat_name):
				var tool := SurfaceTool.new()
				tool.begin(Mesh.PRIMITIVE_TRIANGLES)
				tools[mat_name] = tool
				materials[mat_name] = mesh.surface_get_material(s)
			(tools[mat_name] as SurfaceTool).append_from(mesh, s, offset)
	chunk.set_meta("loot_points", loot)

	var path := "%s/chunks/%s.res" % [output_directory.trim_suffix("/"), chunk.name]
	var far := chunk.get_node_or_null(Chunk.FAR_NODE) as MeshInstance3D
	if tools.is_empty():
		if far != null:
			chunk.remove_child(far)
			far.free()
		_remove_file(path)
	else:
		var merged := ArrayMesh.new()
		var names := tools.keys()
		names.sort()
		for mat_name: String in names:
			(tools[mat_name] as SurfaceTool).commit(merged)
			var index := merged.get_surface_count() - 1
			merged.surface_set_name(index, mat_name)
			merged.surface_set_material(index, materials[mat_name])
		var err := ResourceSaver.save(merged, path, ResourceSaver.FLAG_COMPRESS)
		if err != OK:
			push_error("[venice_builder] Salvataggio di %s fallito (errore %d)." % [path, err])
			return
		if far == null:
			far = MeshInstance3D.new()
			far.name = Chunk.FAR_NODE
			chunk.add_child(far)
			chunk.move_child(far, 0)
			far.owner = _scene_owner()
		far.mesh = ResourceLoader.load(path, "", ResourceLoader.CACHE_MODE_REPLACE)
		far.visibility_range_begin = far_range
		far.visibility_range_begin_margin = FAR_MARGIN
		# Oltre `far_range` le ombre del sole non arrivano comunque.
		far.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	chunk.call("wire")


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
