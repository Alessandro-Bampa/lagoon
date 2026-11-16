@tool
class_name OsmPlaceholder
extends Node3D

## Segnaposto di un elemento OpenStreetMap importato da un OsmLayer.
##
## Nell'editor e' un cilindretto colorato (cliccabile) nel baricentro: arancione
## finche' non c'e' nulla, verde (e alzato sopra il tetto) quando l'edificio e'
## stato generato. In gioco non disegna e non fa nulla: l'edificio generato e'
## un figlio "Building" fatto solo di mesh e collisioni.
##
## Le proprieta' del gruppo "Generazione" si possono cambiare su piu' segnaposto
## insieme (selezione multipla); poi "Genera edifici" nella barra della vista 3D.

enum Kit { NORMALE, ROVINA }
enum Facade { CASUALE, OCRA, ROSSO, ROSA, GIALLO, GRIGIO, MATTONI }

const Poly := preload("polygon_tools.gd")
const KIT_NAMES: PackedStringArray = ["venice", "venice_ruin"]
const FACADE_NAMES: PackedStringArray = ["", "ochre", "red", "pink", "yellow", "grey", "brick"]
const BUILDING_NODE := "Building"
const COLOR_PENDING := Color(1.0, 0.55, 0.1)
const COLOR_DONE := Color(0.2, 0.85, 0.35)

## Identificativo OSM ("way/123", "relation/456"): un elemento gia' importato
## non viene importato due volte.
@export var osm_id := ""
## Livello del JSON da cui viene ("buildings"; in futuro strade, alberi, canali...).
@export var osm_layer := "buildings"
## Promemoria dei dati OSM (nome, tipo, piani, altezza). Solo lettura.
@export_multiline var info := ""

@export_group("Generazione")
## Kit con cui montare l'edificio: pulito o in rovina.
@export var kit: Kit = Kit.NORMALE
## Colore dell'intonaco di facciata. "Casuale" lo ricava dal seme.
@export var facade: Facade = Facade.CASUALE
## Seme delle scelte casuali (finestre, pavimenti, rotture). 0 = dall'id OSM:
## lo stesso edificio rinasce sempre uguale.
@export var seed_value := 0
## Numero di piani. 0 = quello dichiarato da OSM.
@export_range(0, 8) var floors := 0
## Scala, tramezzi e solai dei piani alti. Spento: solo il guscio.
@export var interiors := true
## Blocchi sostituiti a mano: chiave del blocco -> modulo o materiale. Si riempie
## da "Modifica blocchi" nella vista 3D; sopravvive alle rigenerazioni finche'
## la pianta e il numero di piani non cambiano.
@export var overrides: Dictionary = {}
@export_tool_button("Genera", "Play") var _generate_action := generate
@export_tool_button("Rimuovi l'edificio generato", "Remove") var _clear_action := clear
@export_tool_button("Azzera le modifiche ai blocchi", "Reload") var _reset_action := reset_overrides

func _ready() -> void:
	if Engine.is_editor_hint():
		update_gizmos()


func is_generated() -> bool:
	return has_node(BUILDING_NODE)


func settings() -> Dictionary:
	return {"kit": KIT_NAMES[kit], "facade": FACADE_NAMES[facade], "seed": seed_value,
		"floors": floors, "interiors": interiors, "overrides": overrides}


func reset_overrides() -> void:
	overrides = {}
	if is_generated():
		generate()


func generate() -> void:
	var layer := _layer()
	if layer != null:
		layer.call("generate_placeholders", [self])


func clear() -> void:
	var layer := _layer()
	if layer != null:
		layer.call("clear_placeholders", [self])


## Ridisegna il cilindro (colore e quota). Lo disegna placeholder_gizmo.gd.
func update_marker() -> void:
	if Engine.is_editor_hint():
		update_gizmos()


## Quota della base del cilindro: a terra, oppure sopra il tetto se l'edificio
## c'e' gia' (cosi' resta visibile e cliccabile).
func marker_height() -> float:
	if not is_generated():
		return 0.0
	var aabb := _building_aabb()
	return aabb.position.y + aabb.size.y + 1.0


## Blocco dell'edificio colpito per primo dal raggio (coordinate mondo); {} se nessuno.
func pick_block(origin: Vector3, direction: Vector3) -> Dictionary:
	var building := get_node_or_null(BUILDING_NODE) as Node3D
	if building == null or not building.has_meta("blocks"):
		return {}
	var to_local := building.global_transform.affine_inverse()
	var o := to_local * origin
	var d := (to_local.basis * direction).normalized()
	var rings: Array = building.get_meta("rings", [])
	var best := {}
	var best_dist := INF
	var top := 0.0
	for block: Dictionary in building.get_meta("blocks"):
		var xform: Transform3D = block["x"]
		var kind: String = block["t"]
		if kind == "ext" or kind == "int":
			var inverse := xform.affine_inverse()
			var local_origin := inverse * o
			var hit = (block["b"] as AABB).intersects_ray(local_origin, inverse.basis * d)
			if hit != null and local_origin.distance_to(hit) < best_dist:
				best_dist = local_origin.distance_to(hit)
				best = block
		else:
			var y := xform.origin.y
			top = maxf(top, y)
			# Il pavimento si clicca da sopra, il soffitto da sotto.
			if absf(d.y) < 1e-4 or (kind == "floor") != (d.y < 0.0):
				continue
			var t := (y - o.y) / d.y
			if t <= 0.0 or t >= best_dist or rings.is_empty():
				continue
			var p := o + d * t
			if Poly.inside(Vector2(p.x, p.z), rings):
				best_dist = t
				best = block
	# Dall'alto il tetto copre tutto: un clic sulle tegole non sceglie nulla.
	if d.y < -1e-4 and not rings.is_empty():
		var t_roof := (top + 0.3 - o.y) / d.y
		var p_roof := o + d * t_roof
		if t_roof > 0.0 and t_roof < best_dist and Poly.inside(Vector2(p_roof.x, p_roof.z), rings):
			return {}
	return best


func _building_aabb() -> AABB:
	var shell := get_node_or_null(BUILDING_NODE + "/Shell") as MeshInstance3D
	return shell.get_aabb() if shell != null else AABB()


func _layer() -> Node:
	var node := get_parent()
	while node != null and not node.has_method("generate_placeholders"):
		node = node.get_parent()
	if node == null:
		push_warning("[venice_builder] %s non sta sotto un OsmLayer." % name)
	return node
