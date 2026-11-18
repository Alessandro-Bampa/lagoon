@tool
extends RefCounted

## Quote del terreno lette direttamente dalle region di Terrain3D su disco
## (stessa lettura del POC demo/venice/build_map_scene.gd): funziona anche
## headless e senza il nodo Terrain3D in scena.

const REGION_SIZE := 2048
## Terreno generato da OSM (tools/osm_terrain.py): il mare e' a quota 0, ogni
## classe di terra sta sopra +0,15 e ogni classe d'acqua sotto -0,1 (velme e
## acqua bassa comprese). Sopra questa soglia e' terra.
const WATER_THRESHOLD := 0.0
## Quota usata dove il terreno e' acqua o la region non e' importata.
const FALLBACK_GROUND := 1.0

var _directory := ""
var _maps: Dictionary = {}  # Vector2i -> Image (null se la region manca)


func _init(directory: String) -> void:
	_directory = directory.trim_suffix("/")


## Quote sotto un'impronta: mediana, minimo e massimo dei vertici a terra.
func ground_stats(ring: PackedVector2Array) -> Dictionary:
	var land: Array[float] = []
	for p in ring:
		var h := height(p.x, p.y)
		if not is_nan(h) and h > WATER_THRESHOLD:
			land.append(h)
	if land.is_empty():
		return {"ground": FALLBACK_GROUND, "low": FALLBACK_GROUND, "on_land": false}
	land.sort()
	# mediana: robusta a un vertice finito su un argine
	return {"ground": land[land.size() / 2], "low": land[0], "on_land": true}


## Quota nel vertice di terreno piu' vicino; NAN se la region non e' importata.
func height(x: float, z: float) -> float:
	if _directory.is_empty():
		return NAN
	var xi := roundi(x)
	var zi := roundi(z)
	var loc := Vector2i(floori(float(xi) / REGION_SIZE), floori(float(zi) / REGION_SIZE))
	var image := _map(loc)
	if image == null:
		return NAN
	return image.get_pixel(xi - loc.x * REGION_SIZE, zi - loc.y * REGION_SIZE).r


func _map(loc: Vector2i) -> Image:
	if not _maps.has(loc):
		# Convenzione di nomi di Terrain3D: "_" per indici >= 0, "-" per i negativi.
		var path := "%s/terrain3d%s%02d%s%02d.res" % [
			_directory,
			"_" if loc.x >= 0 else "-", absi(loc.x),
			"_" if loc.y >= 0 else "-", absi(loc.y),
		]
		var image: Image = null
		if ResourceLoader.exists(path):
			image = load(path).get("height_map")
		_maps[loc] = image
	return _maps[loc]
