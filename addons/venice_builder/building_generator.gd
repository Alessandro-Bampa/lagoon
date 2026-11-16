@tool
extends RefCounted

## Da un'impronta OSM a un palazzo veneziano montato coi moduli del kit.
##
## Le piante reali sono oblique e mai multiple di 2 m, quindi il montaggio e' misto:
##   - FACCIATE: moduli del kit lungo ogni lato, scalati in X per riempirlo esatto
##     (i moduli con aperture solo fra 0,75 e 1,3; fuori da li' muro cieco);
##   - SOLAI E TETTO: generati sulla pianta vera, con le texture del kit;
##   - INTERNI: scala, stanze, tramezzi e porte su una griglia di celle allineata al
##     lato piu' lungo, decisi un piano alla volta da `floor_planner.gd` e convalidati
##     contro la facciata prima di montare qualunque cosa (vedi `generate`).
## Tutto viene FUSO in poche mesh per edificio (una superficie per materiale) e in
## un solo corpo di collisione: niente nodi per modulo.
##
## Per ogni edificio escono tre livelli di dettaglio e i dati per chi viene dopo:
##   - `Shell`    guscio completo (muri, tetto, fasce): da vicino;
##   - `ShadowCaster` il guscio in una sola superficie, sole posizioni: getta le ombre;
##   - `Interior` solai, soffitti, tramezzi, scale: solo entro `interior_range`;
##   - mesh FAR   (vedi `last_far_mesh`) facciate piatte + tetto, una frazione dei
##                triangoli: OsmLayer la fonde per chunk e la mostra da lontano;
##   - `Occluder` muri ciechi e tetto per l'occlusion culling;
##   - metadato `loot_points`: punti sul pavimento delle stanze raggiungibili.
##
## Il risultato e' in coordinate locali: origine nel baricentro dell'impronta
## (vedi `footprint_center`), y = 0 alla quota del piano terra.

const Poly := preload("polygon_tools.gd")
const KitLibrary := preload("kit_library.gd")
const Planner := preload("floor_planner.gd")

const CELL := 2.0          # larghezza nominale di un modulo
const STOREY := 3.0        # da pavimento a pavimento
const CLEAR := 2.7         # altezza netta: i tramezzi e il soffitto stanno qui
const WALL := 0.3          # spessore del muro esterno, verso l'interno
const SIMPLIFY := 0.2      # m: tolleranza con cui si raddrizzano le piante
const MIN_SCALE := 0.75    # scala in X oltre la quale un modulo con aperture si deforma
const MAX_SCALE := 1.3
const EAVE := 0.36         # sporgenza del cornicione (profilo CORNICE del kit)
const MAX_FLOORS := 8
const METERS_PER_LEVEL := 3.2   # come osm_extract.py: altezza OSM -> numero di piani
const ROOF_RUN := 2.5      # m di falda prima del colmo piatto
const ROOF_PITCH := 0.5
const FAR_BACK := 0.12     # m dietro la facciata: fondo scuro delle aperture nel LOD lontano
const LOOT_WALL := 0.7     # m dal muro a cui cade un punto di loot
## Superfici del guscio che non gettano ombra: vetri (il sole deve entrare dalle
## finestre) e ritagli a soglia, che in una mesh senza texture sarebbero lastre piene.
const NO_SHADOW: PackedStringArray = ["glass", "glass_broken", "grille", "balustrade", "railing"]

enum Slot { FREE, PARTY }

var _lib: KitLibrary
var _libs: Dictionary = {}   # kit -> KitLibrary
var _rng := RandomNumberGenerator.new()
var _ruin := false
var _accs: Dictionary = {}   # "gruppo|materiale" -> Acc
var _faces := PackedVector3Array()
var _convex: Array = []
var _facade := ""
var _axis := Vector2.RIGHT
var _stats: Dictionary = {}
var _overrides: Dictionary = {}   # chiave del blocco -> modulo / materiale scelto a mano
var _blocks: Array = []           # tabella dei blocchi sostituibili (vedi `_block`)
var _rings: Array = []
var _occ_verts := PackedVector3Array()   # occluder: muri ciechi + tetto
var _occ_indices := PackedInt32Array()
var _far_mesh: ArrayMesh
var _loot := PackedVector3Array()
var _planner: Planner            # griglia e piante degli interni
var _stair: Dictionary = {}      # scala scelta dal pianificatore ({} = nessuna)
var _plans: Array = []           # pianta di ogni piano (vuoto senza interni)
var _floors := 1
var _seed := 0
var _anchor := Vector2.ZERO      # un punto del lato piu' lungo


class Acc:
	var verts := PackedVector3Array()
	var normals := PackedVector3Array()
	var uvs := PackedVector2Array()

	func tri(a: Vector3, b: Vector3, c: Vector3, normal: Vector3, ua: Vector2, ub: Vector2, uc: Vector2) -> void:
		# In Godot la faccia frontale e' quella vista in senso orario.
		if (b - a).cross(c - a).dot(normal) > 0.0:
			var t := b; b = c; c = t
			var ut := ub; ub = uc; uc = ut
		verts.append(a); verts.append(b); verts.append(c)
		uvs.append(ua); uvs.append(ub); uvs.append(uc)
		for i in 3:
			normals.append(normal)

	func quad(a: Vector3, b: Vector3, c: Vector3, d: Vector3, normal: Vector3, ua: Vector2, ub: Vector2, uc: Vector2, ud: Vector2) -> void:
		tri(a, b, c, normal, ua, ub, uc)
		tri(a, c, d, normal, ua, uc, ud)


## Punto (x, z mondo) in cui va posato l'edificio generato: il baricentro
## dell'anello esterno grezzo. Segnaposto e generatore usano entrambi questo.
static func footprint_center(item: Dictionary) -> Vector2:
	return Poly.centroid(Poly.ring_from_json(item["rings"][0]))


## Numero di piani: tag building:levels, altrimenti dall'altezza.
static func floors_of(item: Dictionary) -> int:
	var tags: Dictionary = item.get("tags", {})
	var levels := str(tags.get("building:levels", "")).to_float()
	if levels < 1.0:
		levels = float(item.get("h", 12.8)) / METERS_PER_LEVEL
	return clampi(roundi(levels), 1, MAX_FLOORS)


## Scelte possibili per un tipo di blocco ("ext", "int", "floor", "ceiling"),
## filtrate su cio' che il kit contiene davvero. "-" = nessun modulo (varco).
static func options_for(kind: String, lib: KitLibrary) -> PackedStringArray:
	var all: PackedStringArray = []
	match kind:
		"ext":
			all = ["ext_wall", "ext_window_rect", "ext_window_rect_shut", "ext_window_rect_broken",
				"ext_window_arch", "ext_window_arch_shut", "ext_window_arch_boarded",
				"ext_window_gothic", "ext_window_gothic_shut", "ext_window_gothic_broken",
				"ext_window_grille", "ext_window_high", "ext_balcony", "ext_door", "ext_portal",
				"ext_wall_breach", "ext_wall_hole", "ext_wall_ruin_hl", "ext_wall_ruin_low", "ext_wall_ruin_lh", "-"]
		"int":
			all = ["int_wall", "int_door", "int_arch", "int_wall_breach",
				"int_wall_ruin_hl", "int_wall_ruin_low", "int_wall_ruin_lh", "-"]
		"floor":
			return PackedStringArray(["terrazzo", "planks", "paving"])
		"ceiling":
			return PackedStringArray(["ceiling", "planks", "wood"])
	var out := PackedStringArray()
	for name in all:
		if name == "-" or lib.has(name):
			out.append(name)
	return out


## Libreria del kit (per i menu dell'editor).
func library(kit: String) -> KitLibrary:
	if not _libs.has(kit):
		_libs[kit] = KitLibrary.new(kit)
	return _libs[kit]


## Genera l'edificio. `settings`: kit ("venice" | "venice_ruin"), facade (colore,
## "" = a caso), seed (0 = dall'id OSM), floors (0 = da OSM), interiors (bool),
## interior_range (m, 0 = sempre visibili), debug (bool: piante in caratteri nelle
## statistiche). `neighbours`: anelli esterni (mondo) degli edifici vicini, per
## riconoscere i muri in comune. `ground`: quote dal terreno ({ground, low}).
## Ritorna null se l'impronta non e' utilizzabile.
##
## L'edificio si decide PRIMA di montarlo, un piano alla volta dal basso:
##   1. dati generali (lati, moduli di facciata, muri in comune), tipo di edificio,
##      ingresso e scala;
##   2. per ogni piano la pianta degli interni, che parte da dove arriva la scala;
##   3. la convalida del piano contro la sua facciata (`_plan_facade`): finestre
##      contro cui finirebbe un tramezzo, muri accanto alle rampe, stanze cieche.
## Solo dopo si emette la geometria.
func generate(item: Dictionary, settings: Dictionary, neighbours: Array, ground: Dictionary) -> Node3D:
	var kit: String = settings.get("kit", "venice")
	if not _libs.has(kit):
		_libs[kit] = KitLibrary.new(kit)
	_lib = _libs[kit]
	if not _lib.has("ext_wall"):
		push_error("[venice_builder] Kit '%s' non trovato in %s." % [kit, KitLibrary.KITS_DIR])
		return null
	_ruin = kit.ends_with("_ruin")
	_accs.clear()
	_faces = PackedVector3Array()
	_convex = []
	_stats = {"modules": 0, "blinded": 0, "isolated": 0}
	_overrides = settings.get("overrides", {})
	_blocks = []
	_occ_verts = PackedVector3Array()
	_occ_indices = PackedInt32Array()
	_far_mesh = null
	_loot = PackedVector3Array()
	_stair = {}
	_plans = []
	var seed_value: int = settings.get("seed", 0)
	_seed = seed_value if seed_value != 0 else hash(item["id"])
	_rng.seed = _seed

	var center := footprint_center(item)
	var rings: Array = []
	for k in item["rings"].size():
		var ring := Poly.ring_from_json(item["rings"][k])
		for i in ring.size():
			ring[i] -= center
		ring = Poly.simplify(ring, SIMPLIFY)
		if ring.size() < 3 or absf(Poly.area(ring)) < 4.0:
			if k == 0:
				return null
			continue  # cortile troppo piccolo per avere muri
		if k == 0:
			ring = _clip_to_neighbours(ring, neighbours, center)
			if ring.size() < 3 or absf(Poly.area(ring)) < 4.0:
				return null
		rings.append(ring)
	Poly.orient(rings)
	_rings = rings

	var floors: int = settings.get("floors", 0)
	if floors <= 0:
		floors = floors_of(item)
	_floors = floors
	_facade = settings.get("facade", "")
	if _facade.is_empty():
		_facade = KitLibrary.FACADES[_rng.randi() % KitLibrary.FACADES.size()]
	_axis = _dominant_axis(rings[0])
	var interiors: bool = settings.get("interiors", true)
	_planner = Planner.new()
	_planner.setup(rings, _axis, _anchor)

	# 1. Dati generali.
	var edges := _build_edges(rings, floors, neighbours, center)
	var style := {"noble": ["ext_window_gothic", "ext_window_arch"][_rng.randi() % 2],
		"common": ["ext_window_rect", "ext_window_rect", "ext_window_arch"][_rng.randi() % 3],
		"rhythm": _rng.randi() % 4, "grille": _rng.randf_range(0.45, 0.9)}
	var entrance := _choose_entrance(edges)
	var kind: int = _planner.kind_for(floors)
	if interiors and floors > 1:
		_stair = _planner.find_stair(_stair_types(kind, floors), _facade_cells(edges), entrance.get("cell"), _rng)
	_stats["stairs"] = not _stair.is_empty()
	_stats["stair"] = _stair.get("type", "")
	_stats["kind"] = ["casa", "padronale", "condominio"][kind]

	# 2-3. Un piano alla volta: pianta, poi facciata convalidata contro la pianta.
	var rows: Array = []
	var debug: bool = settings.get("debug", false)
	if debug:
		_stats["plans"] = []
	for f in floors:
		if interiors:
			_plans.append(_plan_floor(f, kind, edges, entrance))
			_stats["isolated"] += _plans[f]["isolated"]
			if debug:
				_stats["plans"].append(_planner.ascii(_plans[f]))
		rows.append(_plan_facade(f, edges, entrance, style))

	_emit_facades(edges, rows)
	_skirt(rings, maxf(float(ground.get("ground", 0.0)) - float(ground.get("low", 0.0)), 0.0) + 1.0)
	_slabs(rings)
	_roof(rings, floors * STOREY)
	if interiors:
		_emit_stairs()
		for f in floors:
			_emit_partitions(f)
	_occluder_roof(rings, floors * STOREY)
	_loot_points()
	return _assemble(item, float(settings.get("interior_range", 0.0)), bool(settings.get("lods", true)))


## Le piante OSM di due edifici accostati a volte si sovrappongono di qualche
## centimetro (o decimetro): i muri si compenetrano e le facce quasi complanari
## sfarfallano. Fra due piante che si sovrappongono cede la piu' piccola: le viene
## tolta la parte comune (l'altra resta com'e', quindi non si apre nessuna fessura).
func _clip_to_neighbours(ring: PackedVector2Array, neighbours: Array, center: Vector2) -> PackedVector2Array:
	var own_area := absf(Poly.area(ring))
	var clipped := 0
	for other: PackedVector2Array in neighbours:
		var local := PackedVector2Array()
		for p in other:
			local.append(p - center)
		var other_area := absf(Poly.area(local))
		if other_area < own_area or (other_area == own_area and local[0] < ring[0]):
			continue
		if Geometry2D.intersect_polygons(ring, local).is_empty():
			continue
		var best := PackedVector2Array()
		var best_area := 0.0
		for piece: PackedVector2Array in Geometry2D.clip_polygons(ring, local):
			if Geometry2D.is_polygon_clockwise(piece):
				continue   # buco: il vicino sta tutto dentro, non e' una sovrapposizione di bordo
			var piece_area := absf(Poly.area(piece))
			if piece_area > best_area:
				best_area = piece_area
				best = piece
		# Se sparirebbe piu' di meta' edificio i dati sono rotti: meglio lasciarlo com'e'.
		if best_area > own_area * 0.5 and best_area < own_area - 0.01:
			ring = Poly.simplify(best, SIMPLIFY)
			own_area = absf(Poly.area(ring))
			clipped += 1
	if clipped > 0:
		_stats["clipped"] = clipped
	return ring


func last_stats() -> Dictionary:
	return _stats


## Mesh del LOD lontano dell'ultimo edificio generato (coordinate locali
## dell'edificio, una superficie per materiale), oppure null.
func last_far_mesh() -> ArrayMesh:
	return _far_mesh


# ============================== FACCIATE ==============================

## Lati della pianta coi loro moduli ("slot"). Emette anche le chiusure degli
## spigoli (punte acute, tacche rientranti), che non dipendono dai piani.
## Ogni lato: {a, d, n, run, slots, cuts, state, free, ring, full_a, full_len, turn_a, turn_b};
## il modulo k va da cuts[k] a cuts[k + 1] metri lungo il lato.
func _build_edges(rings: Array, floors: int, neighbours: Array, center: Vector2) -> Array:
	var edges: Array = []
	for r in rings.size():
		var ring: PackedVector2Array = rings[r]
		var count := ring.size()
		# Svolta in ogni vertice: > 0 spigolo convesso, < 0 rientrante.
		var turns: Array[float] = []
		for i in count:
			var d0 := (ring[i] - ring[(i + count - 1) % count]).normalized()
			var d1 := (ring[(i + 1) % count] - ring[i]).normalized()
			turns.append(atan2(d0.cross(d1), d0.dot(d1)))
		for i in count:
			var a := ring[i]
			var b := ring[(i + 1) % count]
			var length := a.distance_to(b)
			if length < 0.05:
				continue
			var d := (b - a) / length
			var n := Vector2(d.y, -d.x)
			# Spigoli acuti: la faccia interna di un muro sbucherebbe dalla faccia
			# esterna dell'altro. I due muri si accorciano e la punta si chiude a parte.
			var trim_a := _acute_trim(turns[i], length)
			var trim_b := _acute_trim(turns[(i + 1) % count], length)
			if trim_a > 0.0:
				_corner_tip(a, d, n, trim_a, floors)
			if trim_b > 0.0:
				_corner_tip(b - d * trim_b, d, n, trim_b, floors)
			if turns[i] < -0.09:
				var prev_d := (a - ring[(i + count - 1) % count]).normalized()
				_reflex_filler(a, Vector2(prev_d.y, -prev_d.x), n, floors)
			var a2 := a + d * trim_a
			var run := length - trim_a - trim_b
			var cuts := _slot_cuts(a2, d, run)
			var slots := cuts.size() - 1
			var edge := {"a": a2, "d": d, "n": n, "run": run, "slots": slots, "cuts": cuts,
				"state": [], "free": 0, "ring": r,
				"full_a": a, "full_len": length, "turn_a": turns[i], "turn_b": turns[(i + 1) % count]}
			for k in slots:
				var state := Slot.FREE
				if r == 0 and _inside_any(_slot_mid(edge, k) + n * 0.7 + center, neighbours):
					state = Slot.PARTY
				edge["state"].append(state)
				if state == Slot.FREE:
					edge["free"] += 1
			edges.append(edge)
	return edges


## Dove un lato si divide in moduli. Se il lato e' (quasi) parallelo alla griglia
## degli interni i tagli cadono SULLE LINEE DI GRIGLIA: un tramezzo arriva sempre
## fra due moduli, mai in mezzo a una finestra, e le finestre stanno al centro delle
## celle. I ritagli alle estremita' si fondono col modulo vicino se sono corti,
## altrimenti restano moduli stretti (ciechi sotto il 75%). Sui lati obliqui: moduli
## uguali, e ci pensa la convalida del piano.
func _slot_cuts(a: Vector2, d: Vector2, run: float) -> Array[float]:
	var cuts: Array[float] = [0.0]
	for axis in 2:
		if absf(d.dot(_planner.u if axis == 0 else _planner.v)) < 0.985:
			continue
		var g0 := _planner.grid_coord(a, axis)
		var g1 := _planner.grid_coord(a + d * run, axis)
		var marks: Array[float] = []
		for line in range(ceili(minf(g0, g1) + 0.001), floori(maxf(g0, g1) - 0.001) + 1):
			marks.append((line - g0) / (g1 - g0) * run)
		marks.sort()
		if not marks.is_empty() and marks[0] < 0.5:
			marks.remove_at(0)
		if not marks.is_empty() and run - marks[marks.size() - 1] < 0.5:
			marks.remove_at(marks.size() - 1)
		cuts.append_array(marks)
		break
	cuts.append(run)
	var widest := 0.0
	for k in cuts.size() - 1:
		widest = maxf(widest, cuts[k + 1] - cuts[k])
	if widest > CELL * MAX_SCALE:
		var slots := maxi(1, roundi(run / CELL))
		cuts = []
		for k in slots + 1:
			cuts.append(run * k / slots)
	return cuts


func _slot_mid(edge: Dictionary, k: int) -> Vector2:
	return edge["a"] + edge["d"] * ((edge["cuts"][k] + edge["cuts"][k + 1]) * 0.5)


func _slot_sx(edge: Dictionary, k: int) -> float:
	return (edge["cuts"][k + 1] - edge["cuts"][k]) / CELL


## Cella degli interni dietro un modulo di facciata (null se non ce n'e' una).
func _slot_cell(edge: Dictionary, k: int) -> Variant:
	var cell := _planner.cell_of(_slot_mid(edge, k) - edge["n"] * (WALL + 0.6))
	if _planner.usable.has(cell):
		return cell
	return _planner.parent.get(cell)


## Per ogni cella, quante finestre possibili ha davanti (la scala le toglierebbe).
func _facade_cells(edges: Array) -> Dictionary:
	var out := {}
	for edge: Dictionary in edges:
		for k in edge["slots"]:
			if edge["state"][k] != Slot.FREE or not _scale_ok(_slot_sx(edge, k)):
				continue
			var cell = _slot_cell(edge, k)
			if cell != null:
				out[cell] = out.get(cell, 0) + 1
	return out


## Porta d'ingresso: sul lato esterno con piu' moduli liberi, verso il centro ma non
## sempre al centro. Ritorna {edge, slot, cell} oppure {} (nessun modulo adatto).
func _choose_entrance(edges: Array) -> Dictionary:
	var order: Array = []
	for e in edges.size():
		var edge: Dictionary = edges[e]
		if edge["ring"] != 0:
			continue
		var good := 0
		for k in edge["slots"]:
			if edge["state"][k] == Slot.FREE and _scale_ok(_slot_sx(edge, k)):
				good += 1
		if good > 0:
			order.append([good + edge["run"] * 0.001, e])
	order.sort_custom(func(x: Array, y: Array) -> bool: return x[0] > y[0])
	# Prima si cerca un modulo con una cella degli interni dietro; se la pianta e'
	# troppo piccola per averne, va bene qualunque modulo libero.
	for need_cell: bool in [true, false]:
		for entry: Array in order:
			var e: int = entry[1]
			var edge: Dictionary = edges[e]
			var slots: int = edge["slots"]
			var best := -1
			var best_score := INF
			for k in slots:
				if edge["state"][k] != Slot.FREE or not _scale_ok(_slot_sx(edge, k)):
					continue
				if need_cell and _slot_cell(edge, k) == null:
					continue
				var score := absf(k - (slots - 1) * 0.5) + _rng.randf() * maxf(1.0, slots * 0.35)
				if score < best_score:
					best_score = score
					best = k
			if best >= 0:
				return {"edge": e, "slot": best, "cell": _slot_cell(edge, best)}
	_stats["no_door"] = true
	return {}


## Tipi di scala adatti al tipo di edificio, col loro peso (piu' basso = preferito).
## Condominio: tromba delle scale chiusa da muri. Casa padronale: scala aperta nel
## salone, a U oppure dritta lungo un muro (solo con due piani: la rampa dritta non si
## impila). Casa piccola: scala stretta fra tre muri.
func _stair_types(kind: int, floors: int) -> Array:
	var types: Array = []
	var roomy := _planner.usable.size() >= 24
	if kind == Planner.Kind.CONDOMINIO:
		types.append({"name": "stair_u", "w": 2, "enclosed": true, "straight": false, "bias": 0.0})
		types.append({"name": "stair_u_narrow", "w": 1, "enclosed": true, "straight": false, "bias": 4.0 if roomy else 0.5})
	elif kind == Planner.Kind.PADRONALE:
		types.append({"name": "stair_u", "w": 2, "enclosed": false, "straight": false, "bias": 0.0})
		if floors == 2:
			types.append({"name": "stair_straight", "w": 1, "enclosed": false, "straight": true,
				"bias": -3.0 if _rng.randf() < 0.5 else 3.0})
		types.append({"name": "stair_u_narrow", "w": 1, "enclosed": true, "straight": false, "bias": 4.0})
	else:
		types.append({"name": "stair_u_narrow", "w": 1, "enclosed": true, "straight": false, "bias": 0.0})
		types.append({"name": "stair_u", "w": 2, "enclosed": false, "straight": false, "bias": 2.5})
	var out: Array = []
	for type: Dictionary in types:
		if _lib.has(type["name"]):
			out.append(type)
	return out


## C'e' una scala che parte dal piano `level`?
func _stair_from(level: int) -> bool:
	if _stair.is_empty():
		return false
	return level == 0 if _stair["straight"] else level < _floors - 1


## Pianta del piano: qualche tentativo con dadi diversi, si tiene quello che va
## meno contro la facciata (tramezzi dentro una finestra o dentro la porta) e che
## non lascia stanze senza porta. In un condominio un piano puo' ripetere quello
## di sotto ("piano tipo").
func _plan_floor(level: int, kind: int, edges: Array, entrance: Dictionary) -> Dictionary:
	var rng := RandomNumberGenerator.new()
	if kind == Planner.Kind.CONDOMINIO and level >= 2:
		rng.seed = hash([_seed, level, -1])
		if rng.randf() < 0.35:
			return _plans[level - 1]
	var best := {}
	var best_score := INF
	for attempt in (2 if _planner.usable.size() > 300 else 4):
		rng.seed = hash([_seed, level, attempt])
		var plan := _planner.plan_floor(level, kind, _stair, entrance.get("cell"), _ruin, rng)
		var score: float = plan["isolated"] * 20.0
		for hit: Array in _wall_hits(plan, edges):
			if level == 0 and hit[0] == entrance.get("edge", -1) and hit[1] == entrance.get("slot", -1):
				score += 100.0
			else:
				score += 1.0
		if score < best_score:
			best_score = score
			best = plan
		if score <= 0.0:
			break
	# Un tramezzo dentro la porta d'ingresso non si puo' risolvere murando: via il tramezzo.
	if level == 0 and best_score >= 100.0:
		for hit: Array in _wall_hits(best, edges):
			if hit[0] == entrance.get("edge", -1) and hit[1] == entrance.get("slot", -1):
				best["walls"].erase(hit[2])
	return best


## Tramezzi che finiscono contro un modulo di facciata dentro la zona dell'apertura
## (cioe' non abbastanza vicino al bordo del modulo): [lato, modulo, bordo del tramezzo].
## Contano solo i moduli che potrebbero avere un'apertura.
func _wall_hits(plan: Dictionary, edges: Array) -> Array:
	var out: Array = []
	for hit: Array in plan["hits"]:
		var p: Vector2 = hit[0]
		if not plan["walls"].has(hit[1]):
			continue
		for e in edges.size():
			var edge: Dictionary = edges[e]
			var rel: Vector2 = p - edge["a"]
			var t: float = rel.dot(edge["d"])
			if absf(rel.dot(edge["n"])) > 0.12 or t < -0.05 or t > edge["run"] + 0.05:
				continue
			var cuts: Array[float] = edge["cuts"]
			for k in edge["slots"]:
				if t > cuts[k + 1]:
					continue
				var half := (cuts[k + 1] - cuts[k]) * 0.5
				if absf(t - cuts[k] - half) < half * 0.62 + 0.1 and edge["state"][k] == Slot.FREE and _scale_ok(half * 2.0 / CELL):
					out.append([e, k, hit[1]])
				break
			break
	return out


## Che cosa c'e' dentro, dietro un modulo di facciata, al piano `level`:
## {room: stanza (-1 = nessuna), stair: 0 niente, 1 fianco di una rampa, 2 fondo del vano}.
func _slot_inside(edge: Dictionary, k: int, plan: Dictionary, level: int) -> Dictionary:
	var out := {"room": -1, "stair": 0}
	var inward: Vector2 = -edge["n"] * (WALL + 0.45)
	var cuts: Array[float] = edge["cuts"]
	out["room"] = _planner.room_of(plan, _planner.cell_of(_slot_mid(edge, k) + inward))
	if not _stair_from(level):
		return out
	for share: float in [0.25, 0.5, 0.75]:
		var p: Vector2 = edge["a"] + edge["d"] * lerpf(cuts[k], cuts[k + 1], share) + inward
		if not plan["stair"].has(_planner.cell_of(p)):
			continue
		var back: bool = not _stair["straight"] and (edge["n"] as Vector2).dot(_stair["f"]) > 0.7
		out["stair"] = maxi(out["stair"], 2 if back else 1)
	return out


func _has_window(style: Dictionary, k: int, slots: int) -> bool:
	match style["rhythm"]:
		1:
			return mini(k, slots - 1 - k) % 2 == 0
		2:
			return mini(k, slots - 1 - k) % 3 != 2
	return true


## Moduli di facciata del piano `level`, convalidati contro la sua pianta:
##   - un tramezzo che arriva in mezzo a un modulo lo rende cieco;
##   - accanto alle rampe il muro e' cieco; sul fondo di una scala a U al piu' una
##     finestrella alta, che sta sopra il pianerottolo;
##   - ogni stanza con un muro libero verso l'esterno ha almeno una finestra.
## Ritorna [lato][modulo] = nome del modulo.
func _plan_facade(level: int, edges: Array, entrance: Dictionary, style: Dictionary) -> Array:
	var plan: Dictionary = _plans[level] if level < _plans.size() else {}
	var blocked := {}
	if not plan.is_empty():
		for hit: Array in _wall_hits(plan, edges):
			blocked[Vector2i(hit[0], hit[1])] = true
	var rows: Array = []
	var lit := {}     # stanza -> ha gia' un'apertura
	var spare := {}   # stanza -> moduli rimasti ciechi che potrebbero avere una finestra
	for e in edges.size():
		var edge: Dictionary = edges[e]
		var slots: int = edge["slots"]
		var row: Array[String] = []
		for k in slots:
			var state: int = edge["state"][k]
			var sx := _slot_sx(edge, k)
			var openings := _scale_ok(sx)
			var at_entrance: bool = e == entrance.get("edge", -1) and k == entrance.get("slot", -1)
			var inside := {"room": -1, "stair": 0}
			if not plan.is_empty():
				inside = _slot_inside(edge, k, plan, level)
			var room: int = inside["room"]
			var name := "ext_wall"
			if at_entrance and level == 0:
				name = "ext_door"
			elif state == Slot.FREE and openings:
				if blocked.has(Vector2i(e, k)):
					_stats["blinded"] += 1
				elif inside["stair"] == 2:
					if _rng.randf() < 0.6 and _lib.has("ext_window_high"):
						name = "ext_window_high"
				elif inside["stair"] == 0:
					if at_entrance and level == 1:
						name = "ext_balcony"
					elif level == 0:
						if _has_window(style, k, slots) and _rng.randf() < style["grille"]:
							name = "ext_window_grille"
					elif _has_window(style, k, slots):
						name = _window_variant(style["noble"] if level == 1 else style["common"])
					if name == "ext_wall" and room >= 0:
						if not spare.has(room):
							spare[room] = []
						spare[room].append(Vector2i(e, k))
			if name != "ext_wall" and room >= 0:
				lit[room] = true
			if name == "ext_wall" and _ruin and state == Slot.FREE and sx > 0.6 and inside["stair"] == 0:
				var roll := _rng.randf()
				if roll < 0.03 and level == 0:
					name = "ext_wall_breach"
				elif roll < 0.07:
					name = "ext_wall_hole"
			row.append(name)
		rows.append(row)
	for room: int in spare:
		if lit.has(room) or plan["rooms"][room]["kind"] == "stair":
			continue
		var list: Array = spare[room]
		var pick: Vector2i = list[list.size() / 2]
		if rows[pick.x][pick.y] == "ext_wall":
			rows[pick.x][pick.y] = "ext_window_grille" if level == 0 else _window_variant(style["common"])
	return rows


func _emit_facades(edges: Array, rows: Array) -> void:
	var top := _floors * STOREY
	for e in edges.size():
		var edge: Dictionary = edges[e]
		var slots: int = edge["slots"]
		var blind: Array = []   # [piano][modulo] = muro pieno, senza aperture
		var solid: Array = []   # [piano][modulo] = per la fisica e' un muro pieno
		for f in _floors:
			var row: Array[bool] = []
			row.resize(slots)
			blind.append(row)
			solid.append(row.duplicate())
		for k in slots:
			var state: int = edge["state"][k]
			var mid := _slot_mid(edge, k)
			var sx := _slot_sx(edge, k)
			for f in _floors:
				var xform := _edge_xform(mid, edge["d"], edge["n"], sx, f * STOREY)
				var facade := "facade_%s%s" % [_facade, "_base" if f == 0 else ""]
				var name: String = rows[f][e][k]
				# Le scelte a mano arrivano DOPO i tiri di dado e la convalida: sostituire
				# un blocco non cambia il resto dell'edificio.
				var key := "w:%d:%d:%d" % [e, k, f]
				name = _overrides.get(key, name)
				if _overrides.has(key + "#c"):
					facade = "facade_%s%s" % [_overrides[key + "#c"], "_base" if f == 0 else ""]
				var is_door := name == "ext_door" or name == "ext_portal"
				var half := CELL * sx * 0.5
				_block(key, "ext", name, _edge_xform(mid, edge["d"], edge["n"], 1.0, f * STOREY),
					AABB(Vector3(-half, 0.0, -WALL), Vector3(half * 2.0, STOREY, WALL + 0.06)))
				if name != "-":
					var module_data = _lib.module(name)
					solid[f][k] = module_data != null and module_data["solid"]
					_module("shell", name, xform, facade, not solid[f][k], true)
				else:
					_far_backing(xform)
				blind[f][k] = name == "ext_wall"
				if state != Slot.PARTY:
					if f == 0 and not is_door:
						_module("shell", "ext_plinth", xform, "", false)
					elif f > 0:
						_module("shell", "ext_stringcourse", xform, "", false)
		_occluder_walls(edge, blind)
		_collision_walls(edge, solid)
		# Cornicione: una corsa per lato, allungata sugli spigoli convessi cosi' i
		# due profili si incrociano e chiudono l'angolo.
		if edge["free"] * 2 >= slots or edge["ring"] != 0:
			var ext_a := _eave_extension(edge["turn_a"])
			var ext_b := _eave_extension(edge["turn_b"])
			var start: Vector2 = edge["full_a"] - edge["d"] * ext_a
			var length: float = edge["full_len"] + ext_a + ext_b
			var pieces := maxi(1, roundi(length / CELL))
			for k in pieces:
				var mid: Vector2 = start + edge["d"] * (length * (k + 0.5) / pieces)
				_module("shell", "ext_cornice", _edge_xform(mid, edge["d"], edge["n"], length / (CELL * pieces), top), "", false)


func _scale_ok(sx: float) -> bool:
	return sx >= MIN_SCALE and sx <= MAX_SCALE


func _window_variant(base: String) -> String:
	var roll := _rng.randf()
	var name := base
	if _ruin:
		if roll < 0.25:
			name = base + ("_boarded" if base.ends_with("arch") else "_broken")
		elif roll < 0.55:
			name = base + "_shut"
	elif roll < 0.3:
		name = base + "_shut"
	return name if _lib.has(name) else base


func _acute_trim(turn: float, length: float) -> float:
	if turn <= PI * 0.5 + 0.01:
		return 0.0
	# angolo interno = PI - turn (< 90 gradi)
	return minf(WALL / tan(PI - turn), length / 3.0)


func _eave_extension(turn: float) -> float:
	return clampf(EAVE * tan(turn * 0.5), 0.0, 0.6) if turn > 0.0 else 0.0


## Trasformata di un modulo di bordo: X lungo il lato (scalata), +Z verso fuori.
func _edge_xform(mid: Vector2, d: Vector2, n: Vector2, sx: float, y: float) -> Transform3D:
	var z := Vector3(n.x, 0.0, n.y)
	var x := Vector3.UP.cross(z)
	return Transform3D(Basis(x * sx, Vector3.UP, z), Vector3(mid.x, y, mid.y))


## Punta di uno spigolo acuto: due facce esterne lunghe quanto l'accorciamento.
func _corner_tip(from: Vector2, d: Vector2, n: Vector2, length: float, floors: int) -> void:
	var to := from + d * length
	var normal := Vector3(n.x, 0.0, n.y)
	for f in floors:
		var y0 := f * STOREY
		var y1 := y0 + STOREY
		var u := length / CELL
		for group: String in ["shell", "far"]:
			_acc(group, "facade_%s%s" % [_facade, "_base" if f == 0 else ""]).quad(
				Vector3(from.x, y0, from.y), Vector3(to.x, y0, to.y), Vector3(to.x, y1, to.y), Vector3(from.x, y1, from.y),
				normal, Vector2(0, 1), Vector2(u, 1), Vector2(u, 0), Vector2(0, 0))
	_wall_faces(from, to, 0.0, floors * STOREY)


## Spigolo rientrante: fra le testate dei due muri resta una tacca aperta verso
## la stanza (i muri non hanno testate). La chiude una faccia d'intonaco.
func _reflex_filler(v: Vector2, n_prev: Vector2, n: Vector2, floors: int) -> void:
	var p1 := v - n_prev * WALL
	var p2 := v - n * WALL
	if p1.distance_to(p2) < 0.01:
		return
	var inward := -(n_prev + n).normalized()
	var normal := Vector3(inward.x, 0.0, inward.y)
	var u := p1.distance_to(p2) / CELL
	var acc := _acc("shell", "plaster")
	for f in floors:
		var y0 := f * STOREY
		var y1 := y0 + STOREY
		acc.quad(Vector3(p1.x, y0, p1.y), Vector3(p2.x, y0, p2.y), Vector3(p2.x, y1, p2.y), Vector3(p1.x, y1, p1.y),
			normal, Vector2(0, 1), Vector2(u, 1), Vector2(u, 0), Vector2(0, 0))
	_wall_faces(p1, p2, 0.0, floors * STOREY)


## Fondazione: il terreno non e' piano, il piano terra si'. Sotto ogni lato una
## fascia di pietra scende fin sotto il punto piu' basso dell'impronta.
func _skirt(rings: Array, depth: float) -> void:
	for group: String in ["shell", "far"]:
		var acc := _acc(group, "stone_blocks")
		for ring: PackedVector2Array in rings:
			for i in ring.size():
				var a := ring[i]
				var b := ring[(i + 1) % ring.size()]
				var d := (b - a).normalized()
				var normal := Vector3(d.y, 0.0, -d.x)
				var u := a.distance_to(b)
				acc.quad(Vector3(a.x, -depth, a.y), Vector3(b.x, -depth, b.y), Vector3(b.x, 0.0, b.y), Vector3(a.x, 0.0, a.y),
					normal, Vector2(0, depth), Vector2(u, depth), Vector2(u, 0), Vector2(0, 0))


func _inside_any(p: Vector2, polygons: Array) -> bool:
	for ring: PackedVector2Array in polygons:
		if Geometry2D.is_point_in_polygon(p, ring):
			return true
	return false


# ============================== SOLAI E TETTO ==============================

## Solai e soffitti stanno con gli interni: da fuori non si vedono, e oltre
## `interior_range` non si disegnano.
func _flat(tris: PackedVector2Array, y: float, up: bool, mat: String, tile: float, collide: bool) -> void:
	var acc := _acc("interior", mat)
	var normal := Vector3.UP if up else Vector3.DOWN
	var cross_axis := Vector2(-_axis.y, _axis.x)
	for i in range(0, tris.size() - 2, 3):
		var p: Array[Vector3] = []
		var uv: Array[Vector2] = []
		for k in 3:
			var q := tris[i + k]
			p.append(Vector3(q.x, y, q.y))
			uv.append(Vector2(q.dot(_axis), q.dot(cross_axis)) / tile)
		acc.tri(p[0], p[1], p[2], normal, uv[0], uv[1], uv[2])
		if collide:
			_faces.append(p[0]); _faces.append(p[1]); _faces.append(p[2])


## Tetto a padiglione tronco: una falda larga ROOF_RUN lungo tutto il perimetro
## (cortili compresi) e un colmo piatto. Vera per ogni pianta, anche obliqua o con
## buchi, e dalla calle si legge come un tetto a falde.
func _roof(rings: Array, y0: float) -> void:
	var run := ROOF_RUN
	var groups: Array = []
	for attempt in 3:
		groups = Poly.inset(rings, run)
		if not groups.is_empty():
			break
		run *= 0.5
	var acc := _acc("shell", "roof")
	if groups.is_empty():
		_roof_tris(acc, Poly.triangulate(rings), {}, y0 + 0.02, 0.0)
		_roof_to_far(acc)
		return
	var rise := run * ROOF_PITCH
	var high := {}
	var outers: Array = [rings[0].duplicate()]
	var holes: Array = []
	for k in range(1, rings.size()):
		holes.append(rings[k].duplicate())
	for g: Array in groups:
		for k in g.size():
			var ring: PackedVector2Array = g[k].duplicate()
			for p in ring:
				high[p] = true
			if k == 0:
				holes.append(ring)
			else:
				outers.append(ring)
	for component: Array in Poly.group(outers, holes):
		_roof_tris(acc, Poly.triangulate(component), high, y0, rise)
	for g: Array in groups:
		_roof_tris(acc, Poly.triangulate(g), high, y0, rise)
	_roof_to_far(acc)


## Il tetto e' gia' poca geometria: nel LOD lontano va tale e quale.
func _roof_to_far(roof: Acc) -> void:
	var far := _acc("far", "roof")
	far.verts.append_array(roof.verts)
	far.normals.append_array(roof.normals)
	far.uvs.append_array(roof.uvs)


func _roof_tris(acc: Acc, tris: PackedVector2Array, high: Dictionary, y0: float, rise: float) -> void:
	for i in range(0, tris.size() - 2, 3):
		var p: Array[Vector3] = []
		for k in 3:
			var q := tris[i + k]
			p.append(Vector3(q.x, y0 + (rise if high.has(q) else 0.0), q.y))
		var normal := (p[1] - p[0]).cross(p[2] - p[0])
		if normal.length() < 1e-6:
			continue
		normal = normal.normalized()
		if normal.y < 0.0:
			normal = -normal
		# I coppi corrono lungo la pendenza: V segue la falda, U la gronda.
		var u_axis := Vector3(_axis.x, 0.0, _axis.y)
		var v_axis := Vector3(-_axis.y, 0.0, _axis.x)
		if normal.y < 0.999:
			u_axis = Vector3.UP.cross(normal).normalized()
			v_axis = normal.cross(u_axis)
		acc.tri(p[0], p[1], p[2], normal,
			Vector2(p[0].dot(u_axis), p[0].dot(v_axis)) / CELL,
			Vector2(p[1].dot(u_axis), p[1].dot(v_axis)) / CELL,
			Vector2(p[2].dot(u_axis), p[2].dot(v_axis)) / CELL)
		_faces.append(p[0]); _faces.append(p[1]); _faces.append(p[2])


# ============================== INTERNI ==============================

func _slabs(rings: Array) -> void:
	var ground_mat: String = ["paving", "terrazzo"][_rng.randi() % 2]
	var upper_mat: String = ["planks", "terrazzo", "planks"][_rng.randi() % 3]
	var solid_tris := Poly.triangulate(rings)
	var holed_tris := solid_tris
	if not _stair.is_empty():
		var holed := rings.duplicate()
		holed.append(_stair["ring"])
		Poly.orient(holed)
		holed_tris = Poly.triangulate(holed)
	for f in _floors:
		# Foro nel pavimento se una scala arriva da sotto, nel soffitto se ne parte una.
		var has_hole := f > 0 and _stair_from(f - 1)
		var floor_mat: String = _overrides.get("f:%d" % f, ground_mat if f == 0 else upper_mat)
		_block("f:%d" % f, "floor", floor_mat, Transform3D(Basis.IDENTITY, Vector3(0.0, f * STOREY, 0.0)), AABB())
		_flat(holed_tris if has_hole else solid_tris, f * STOREY, true, floor_mat, CELL, true)
		var ceiling_mat: String = _overrides.get("c:%d" % f, "ceiling")
		_block("c:%d" % f, "ceiling", ceiling_mat, Transform3D(Basis.IDENTITY, Vector3(0.0, f * STOREY + CLEAR, 0.0)), AABB())
		_flat(holed_tris if _stair_from(f) else solid_tris, f * STOREY + CLEAR, false, ceiling_mat, 1.0 if ceiling_mat == "ceiling" else CELL, false)


## Lato piu' lungo dell'anello esterno: da' l'asse della griglia degli interni.
## `_anchor` e' un suo punto (la griglia parte dalla faccia interna di quel muro).
func _dominant_axis(ring: PackedVector2Array) -> Vector2:
	var best := Vector2.RIGHT
	var best_len := 0.0
	_anchor = ring[0]
	for i in ring.size():
		var d := ring[(i + 1) % ring.size()] - ring[i]
		if d.length() > best_len:
			best_len = d.length()
			best = d.normalized()
			_anchor = ring[i]
	return best


## Monta la scala a ogni piano e, dove buca un solaio, la testa del solaio e i
## parapetti attorno al foro. La posizione e' quella decisa dal pianificatore
## (`_stair`: origine al centro del bordo d'ingresso, `r` a destra, `f` verso il fondo).
func _emit_stairs() -> void:
	if _stair.is_empty():
		return
	var o: Vector2 = _stair["o"]
	var r: Vector2 = _stair["r"]
	var f: Vector2 = _stair["f"]
	var w: int = _stair["w"]
	var straight: bool = _stair["straight"]
	var x3 := Vector3(r.x, 0.0, r.y)
	var z3 := -Vector3(f.x, 0.0, f.y)
	var front := Basis(x3, Vector3.UP, z3)            # come la scala: +Z verso l'ingresso
	var back := front.rotated(Vector3.UP, PI)         # +Z verso il fondo del vano
	var side_plus := front.rotated(Vector3.UP, PI * 0.5)    # +Z verso +X della scala
	var side_minus := front.rotated(Vector3.UP, -PI * 0.5)
	var half := w * CELL * 0.5
	var depth := 2.0 * CELL
	for level in _floors:
		var y := level * STOREY
		if _stair_from(level):
			_emit_stair_module(level, Transform3D(front, _v3(o, y)), half, depth)
		if level == 0 or not _stair_from(level - 1):
			continue
		var last := not _stair_from(level)
		var plan: Dictionary = _plans[level]
		# Bordi del foro: [base (+Z verso il vuoto), centro, parapetto]. Il parapetto va
		# dove si cadrebbe nel vano; non sul tratto d'arrivo ne' dove parte la rampa che
		# sale (il suo fianco chiude gia'): all'ultimo piano quella rampa non c'e'.
		var pieces: Array = []
		for a in w:
			var x := -half + CELL * 0.5 + a * CELL
			pieces.append([front, o + f * depth + r * x, not straight])
			if straight:
				pieces.append([back, o + r * x, true])
			else:
				pieces.append([back, o + r * x, last and w > 1 and x < 0.0])
		for d: float in [CELL * 0.5, CELL * 1.5]:
			pieces.append([side_plus, o - r * half + f * d, straight or last or d > CELL])
			pieces.append([side_minus, o + r * half + f * d, true])
		for piece: Array in pieces:
			var basis: Basis = piece[0]
			var at: Vector2 = piece[1]
			_module("interior", "floor_edge", Transform3D(basis, _v3(at, y)), "", false)
			if not piece[2]:
				continue
			# Dal lato del pavimento c'e' gia' un muro (esterno o tramezzo): niente parapetto.
			if not _edge_walled(plan, at, -Vector2(basis.z.x, basis.z.z)):
				_module("interior", "stair_rail", Transform3D(basis, _v3(at, y)), "")
		if last and w == 1 and not straight:
			# Scala stretta, ultimo piano: mezzo parapetto dove sotto parte la prima rampa.
			_module("interior", "stair_rail", Transform3D(Basis(back.x * 0.5, back.y, back.z), _v3(o - r * (CELL * 0.25), y)), "")


## La scala del piano `level`: il corpo (rampe, pianerottolo, ringhiere centrali) e le
## ringhiere esterne SOLO sui lati che non hanno un muro: un corrimano appoggiato a un
## muro non ha senso. Un lato conta come libero se almeno una delle sue celle lo e'.
func _emit_stair_module(level: int, xform: Transform3D, half: float, depth: float) -> void:
	var type: String = _stair["type"]
	if not _lib.has(type + "_core"):
		_module("interior", type, xform, "")   # tipo senza ringhiere esterne (scala stretta)
		return
	_module("interior", type + "_core", xform, "")
	var plan: Dictionary = _plans[level]
	var o: Vector2 = _stair["o"]
	var r: Vector2 = _stair["r"]
	var f: Vector2 = _stair["f"]
	# [modulo, primo punto del lato, direzione lungo il lato, verso fuori, celle]
	var sides: Array = [
		["_rail_l", o - r * half, f, -r, 2],
		["_rail_r", o + r * half, f, r, 2],
		["_rail_back", o + f * depth - r * half, r, f, _stair["w"]]]
	for side: Array in sides:
		if not _lib.has(type + side[0]):
			continue
		for k: int in side[4]:
			var at: Vector2 = side[1] + side[2] * (CELL * (k + 0.5))
			if not _edge_walled(plan, at, side[3]):
				_module("interior", type + side[0], xform, "")
				break


## Sul bordo di cella centrato in `at` c'e' un muro (esterno o tramezzo) dal lato `out`?
func _edge_walled(plan: Dictionary, at: Vector2, out: Vector2) -> bool:
	var cell_out := _planner.cell_of(at + out)
	if not Poly.inside(at + out * 0.6, _rings) or _planner.room_of(plan, cell_out) < 0:
		return true
	return plan["walls"].has(Planner.edge_key(_planner.cell_of(at - out), cell_out))


## Tramezzi del piano: un modulo per bordo di cella (porta, arco, muro) e, dove il
## bordo e' tagliato dal muro esterno, un tratto d'intonaco su misura. Poi i pilastrini
## su incroci, angoli e testate libere (i tramezzi del kit non hanno testate).
func _emit_partitions(level: int) -> void:
	var plan: Dictionary = _plans[level]
	var y := level * STOREY
	var walls: Dictionary = plan["walls"]
	for key: Vector3i in walls:
		var wall: Dictionary = walls[key]
		var p0 := _planner.edge_point(key, wall["s0"])
		var p1 := _planner.edge_point(key, wall["s1"])
		var length := p0.distance_to(p1)
		if length < 0.05:
			continue
		var dir := (p1 - p0) / length
		var x3 := Vector3(dir.x, 0.0, dir.y)
		var basis := Basis(x3, Vector3.UP, x3.cross(Vector3.UP))
		var mid := (p0 + p1) * 0.5
		var block := "p:%d:%d:%d:%d" % [key.x, key.y, key.z, level]
		var name: String = _overrides.get(block, wall["m"])
		_block(block, "int", name, Transform3D(basis, _v3(mid, y)),
			AABB(Vector3(-length * 0.5, 0.0, -0.1), Vector3(length, CLEAR, 0.2)))
		if name == "-":
			continue
		if length >= CELL * MIN_SCALE:
			_module("interior", name, Transform3D(Basis(basis.x * (length / CELL), basis.y, basis.z), _v3(mid, y)), "")
		else:
			_plain_partition(p0, p1, y)
		if _stair_from(level):
			# Muro del vano scala: i tramezzi arrivano al soffitto (2,7 m) ma nel vano il
			# soffitto non c'e'. Dal lato della scala il muro prosegue fino al piano di sopra,
			# senno' salendo si vedrebbe dentro il suo spessore e nel solaio.
			for cell: Vector2i in Planner.edge_cells(key):
				if plan["stair"].has(cell):
					_wall_riser(p0, p1, _planner.at(cell.x + 0.5, cell.y + 0.5), y)
	var u3 := Vector3(_axis.x, 0.0, _axis.y)
	var post_basis := Basis(u3, Vector3.UP, u3.cross(Vector3.UP))
	var cap := _acc("interior", "plaster")
	var hx := Vector3(_axis.x, 0.0, _axis.y) * 0.11
	var hz := Vector3(-_axis.y, 0.0, _axis.x) * 0.11
	for p: Vector2 in plan["posts"]:
		_module("interior", "int_post", Transform3D(post_basis, _v3(p, y)), "")
		# Testa del pilastrino: nei vani scala si vede da sopra.
		var c := _v3(p, y + CLEAR)
		cap.quad(c - hx - hz, c + hx - hz, c + hx + hz, c - hx + hz, Vector3.UP,
			Vector2(0, 0), Vector2(0.11, 0), Vector2(0.11, 0.07), Vector2(0, 0.07))


## Fascia d'intonaco che porta un tramezzo dal soffitto (2,7 m) al piano di sopra, sulla
## faccia che guarda `toward` (il vano scala), con la sua testa.
func _wall_riser(p0: Vector2, p1: Vector2, toward: Vector2, y: float) -> void:
	var d := (p1 - p0).normalized()
	var n := Vector2(-d.y, d.x)
	if n.dot(toward - p0) < 0.0:
		n = -n
	var a := p0 + n * 0.08
	var b := p1 + n * 0.08
	var normal := Vector3(n.x, 0.0, n.y)
	var acc := _acc("interior", "plaster")
	var u := p0.distance_to(p1) / CELL
	var v := 1.0 - CLEAR / STOREY
	acc.quad(_v3(a, y + CLEAR), _v3(b, y + CLEAR), _v3(b, y + STOREY), _v3(a, y + STOREY), normal,
		Vector2(0.0, v), Vector2(u, v), Vector2(u, 0.0), Vector2(0.0, 0.0))
	acc.quad(_v3(p0, y + STOREY), _v3(p1, y + STOREY), _v3(b, y + STOREY), _v3(a, y + STOREY), Vector3.UP,
		Vector2(0.0, 0.0), Vector2(u, 0.0), Vector2(u, 0.03), Vector2(0.0, 0.03))


## Tratto di tramezzo cieco di lunghezza qualunque (due facce d'intonaco).
func _plain_partition(p0: Vector2, p1: Vector2, y: float) -> void:
	var d := (p1 - p0).normalized()
	var acc := _acc("interior", "plaster")
	var length := p0.distance_to(p1)
	# La texture dell'intonaco copre 3 m da terra (V = 1 al pavimento): come nei moduli
	# del kit, cosi' lo zoccolo dipinto resta alla stessa quota lungo tutta la parete.
	var top_v := 1.0 - CLEAR / STOREY
	for sign: float in [-1.0, 1.0]:
		var n := Vector2(-d.y, d.x) * sign
		var a := p0 + n * 0.08
		var b := p1 + n * 0.08
		var normal := Vector3(n.x, 0.0, n.y)
		acc.quad(_v3(a, y), _v3(b, y), _v3(b, y + CLEAR), _v3(a, y + CLEAR), normal,
			Vector2(0.0, 1.0), Vector2(length / CELL, 1.0), Vector2(length / CELL, top_v), Vector2(0.0, top_v))
		_face_quad(_v3(a, y), _v3(b, y), _v3(b, y + CLEAR), _v3(a, y + CLEAR), normal)


## Occluder di un lato: un quad per ogni fila di moduli ciechi consecutivi dello
## stesso piano, a meta' spessore del muro. Solo cio' che e' davvero opaco: dietro
## a una finestra si deve continuare a vedere (interni, loot, altri edifici).
func _occluder_walls(edge: Dictionary, blind: Array) -> void:
	var a: Vector2 = edge["a"] - edge["n"] * (WALL * 0.5)
	var d: Vector2 = edge["d"]
	var cuts: Array[float] = edge["cuts"]
	for f in blind.size():
		for run: Vector2i in _runs(blind[f]):
			var p0 := a + d * cuts[run.x]
			var p1 := a + d * cuts[run.y]
			_occluder_quad(Vector3(p0.x, f * STOREY, p0.y), Vector3(p1.x, f * STOREY, p1.y),
				Vector3(p1.x, (f + 1) * STOREY, p1.y), Vector3(p0.x, (f + 1) * STOREY, p0.y))


## Collisione dei moduli "pieni" di un lato (muri ciechi e finestre chiuse): per
## ogni fila consecutiva dello stesso piano due sole facce, quella esterna e quella
## interna, al posto di quattro triangoli per modulo.
func _collision_walls(edge: Dictionary, solid: Array) -> void:
	var a: Vector2 = edge["a"]
	var d: Vector2 = edge["d"]
	var n: Vector2 = edge["n"]
	var cuts: Array[float] = edge["cuts"]
	var out := Vector3(n.x, 0.0, n.y)
	for f in solid.size():
		var y0 := f * STOREY
		var y1 := y0 + STOREY
		for run: Vector2i in _runs(solid[f]):
			var p0 := a + d * cuts[run.x]
			var p1 := a + d * cuts[run.y]
			_face_quad(_v3(p0, y0), _v3(p1, y0), _v3(p1, y1), _v3(p0, y1), out)
			var q0 := p0 - n * WALL
			var q1 := p1 - n * WALL
			_face_quad(_v3(q0, y0), _v3(q1, y0), _v3(q1, y1), _v3(q0, y1), -out)


## Punti in cui il gioco potra' far comparire il loot: sul pavimento, a ridosso di
## un muro, uno o due per stanza, solo ai piani raggiungibili (senza scala: solo il
## piano terra). Sono dati, non nodi: li legge l'host senza bisogno che le mesh
## siano caricate. Non usa il generatore casuale.
func _loot_points() -> void:
	var levels := _floors if not _stair.is_empty() else 1
	if _plans.is_empty():
		# Solo guscio: qualche punto al piano terra, sulle celle intere.
		var index := 0
		for cell: Vector2i in _planner.full:
			if index % 6 == 0:
				var p := _planner.at(cell.x + 0.5, cell.y + 0.5)
				_loot.append(Vector3(p.x, 0.02, p.y))
			index += 1
		return
	var skip := {}
	for cell: Vector2i in _stair.get("landing", []) + _stair.get("arrival", []):
		skip[cell] = true
	for level in levels:
		var plan: Dictionary = _plans[level]
		for room: Dictionary in plan["rooms"]:
			if room["kind"] != "room" and room["kind"] != "hall":
				continue
			var cells: Array = []
			for cell: Vector2i in room["cells"]:
				if _planner.full.has(cell) and not plan["stair"].has(cell) and not skip.has(cell):
					cells.append(cell)
			if cells.is_empty():
				continue
			var picks: Array = [cells[0]]
			if cells.size() >= 4 and room["kind"] == "room":
				picks.append(cells[cells.size() - 1])
			for cell: Vector2i in picks:
				var p := _planner.at(cell.x + 0.5, cell.y + 0.5)
				# Verso il primo muro che la cella ha accanto: a 0,7 m da quello.
				for d in Planner.DIRS:
					if _planner.room_of(plan, cell + d) != _planner.room_of(plan, cell):
						p += (_planner.u * d.x + _planner.v * d.y) * 0.3
						break
				_loot.append(Vector3(p.x, level * STOREY + 0.02, p.y))


# ============================== MONTAGGIO ==============================

func _v3(p: Vector2, y: float) -> Vector3:
	return Vector3(p.x, y, p.y)


## Registra un blocco sostituibile. La tabella finisce nei metadati dell'edificio
## ("blocks"): l'editor la usa per capire quale blocco e' stato cliccato e che cosa
## c'e' ora. `xform` e' senza scala; `box` e' l'ingombro nel riferimento del blocco
## (vuoto per pavimenti e soffitti, che si riconoscono dalla quota).
func _block(key: String, kind: String, current: String, xform: Transform3D, box: AABB) -> void:
	_blocks.append({"k": key, "t": kind, "m": current, "x": xform, "b": box})


func _acc(group: String, mat: String) -> Acc:
	var key := group + "|" + mat
	if not _accs.has(key):
		_accs[key] = Acc.new()
	return _accs[key]


## Fonde un modulo del kit nell'edificio. `facade` sostituisce la superficie
## "facade" (colore dell'edificio); `collide` = false per fasce e cornici.
func _module(group: String, name: String, xform: Transform3D, facade: String, collide: bool = true, far: bool = false) -> void:
	var data = _lib.module(name)
	if data == null:
		data = _lib.module("ext_wall" if name.begins_with("ext_w") else "int_wall" if name.begins_with("int_") else "")
		if data == null:
			return
	_stats["modules"] += 1
	var rotation := Transform3D(xform.basis.orthonormalized(), Vector3.ZERO)
	for surface: Dictionary in data["surfaces"]:
		var mat: String = surface["mat"]
		if mat == "facade" and not facade.is_empty():
			mat = facade
		var acc := _acc(group, mat)
		acc.verts.append_array(xform * (surface["verts"] as PackedVector3Array))
		acc.normals.append_array(rotation * (surface["normals"] as PackedVector3Array))
		acc.uvs.append_array(surface["uvs"])
	if far:
		var far_data: Dictionary = data["far"]
		for surface: Dictionary in far_data["surfaces"]:
			var mat: String = surface["mat"]
			if mat == "facade" and not facade.is_empty():
				mat = facade
			var acc := _acc("far", mat)
			acc.verts.append_array(xform * (surface["verts"] as PackedVector3Array))
			acc.normals.append_array(rotation * (surface["normals"] as PackedVector3Array))
			acc.uvs.append_array(surface["uvs"])
		if far_data["holed"]:
			_far_backing(xform)
	if collide:
		_faces.append_array(xform * (data["faces"] as PackedVector3Array))
		for points: PackedVector3Array in data["convex"]:
			_convex.append(xform * points)


## LOD lontano: fondo scuro dietro un modulo con aperture (o un varco), cosi' da
## lontano non si vede attraverso l'edificio.
func _far_backing(xform: Transform3D) -> void:
	var half := CELL * 0.5
	var normal := (xform.basis.orthonormalized() * Vector3.BACK).normalized()
	_acc("far", "far_dark").quad(
		xform * Vector3(-half, 0.0, -FAR_BACK), xform * Vector3(half, 0.0, -FAR_BACK),
		xform * Vector3(half, STOREY, -FAR_BACK), xform * Vector3(-half, STOREY, -FAR_BACK),
		normal, Vector2(0, 1), Vector2(1, 1), Vector2(1, 0), Vector2(0, 0))


## File di valori veri consecutivi: [inizio, fine escluso].
func _runs(row: Array) -> Array[Vector2i]:
	var out: Array[Vector2i] = []
	var start := -1
	for k in row.size() + 1:
		var on: bool = k < row.size() and row[k]
		if on and start < 0:
			start = k
		elif not on and start >= 0:
			out.append(Vector2i(start, k))
			start = -1
	return out


## Due triangoli di collisione rivolti verso `normal` (un trimesh ferma solo chi
## arriva dalla faccia frontale, che in Godot e' quella in senso orario).
func _face_quad(a: Vector3, b: Vector3, c: Vector3, d: Vector3, normal: Vector3) -> void:
	for tri: Array in [[a, b, c], [a, c, d]]:
		if ((tri[1] - tri[0]) as Vector3).cross(tri[2] - tri[0]).dot(normal) > 0.0:
			_faces.append(tri[0]); _faces.append(tri[2]); _faces.append(tri[1])
		else:
			_faces.append(tri[0]); _faces.append(tri[1]); _faces.append(tri[2])


func _occluder_quad(a: Vector3, b: Vector3, c: Vector3, d: Vector3) -> void:
	var base := _occ_verts.size()
	_occ_verts.append(a); _occ_verts.append(b); _occ_verts.append(c); _occ_verts.append(d)
	for i in [0, 1, 2, 0, 2, 3]:
		_occ_indices.append(base + i)


## Il tetto come occluder: la pianta piena alla quota di gronda (sta dentro il
## volume del tetto vero, quindi non nasconde mai nulla di visibile).
func _occluder_roof(rings: Array, y: float) -> void:
	var tris := Poly.triangulate(rings)
	for i in range(0, tris.size() - 2, 3):
		var base := _occ_verts.size()
		for k in 3:
			_occ_verts.append(Vector3(tris[i + k].x, y, tris[i + k].y))
			_occ_indices.append(base + k)


## Collisione di una faccia verticale fatta a mano (punte e riempimenti).
func _wall_faces(a: Vector2, b: Vector2, y0: float, y1: float) -> void:
	var p := [Vector3(a.x, y0, a.y), Vector3(b.x, y0, b.y), Vector3(b.x, y1, b.y), Vector3(a.x, y1, a.y)]
	for i in [0, 1, 2, 0, 2, 3]:
		_faces.append(p[i])


func _assemble(item: Dictionary, interior_range: float, lods: bool) -> Node3D:
	var root := Node3D.new()
	root.name = "Building"
	root.set_meta("osm_id", item["id"])
	root.set_meta("blocks", _blocks)
	root.set_meta("rings", _rings)
	root.set_meta("kit", _lib.kit)
	root.set_meta("loot_points", _loot)
	_stats["triangles"] = 0
	for group: String in ["shell", "interior"]:
		var mesh := _build_mesh(group, lods)
		if mesh == null:
			continue
		var node := MeshInstance3D.new()
		node.name = "Shell" if group == "shell" else "Interior"
		node.mesh = mesh
		if group == "interior" and interior_range > 0.0:
			# Solai, tramezzi e scale non si vedono da lontano: fuori portata non si disegnano.
			node.visibility_range_end = interior_range
			node.visibility_range_end_margin = 5.0
		root.add_child(node)
		node.owner = root
		if group == "shell":
			var caster := _build_shadow_mesh()
			if caster != null:
				# Le ombre del guscio le getta una mesh a parte con UNA sola superficie:
				# ogni cascata del sole costa una draw call per edificio invece di una
				# per materiale.
				node.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
				var shadow := MeshInstance3D.new()
				shadow.name = "ShadowCaster"
				shadow.mesh = caster
				shadow.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_SHADOWS_ONLY
				root.add_child(shadow)
				shadow.owner = root
	var near_triangles: int = _stats["triangles"]
	_far_mesh = _build_mesh("far", false)
	_stats["far_triangles"] = _stats["triangles"] - near_triangles
	_stats["triangles"] = near_triangles
	_stats["surfaces"] = _accs.size()
	_stats["loot_points"] = _loot.size()

	if not _occ_indices.is_empty():
		var occluder := ArrayOccluder3D.new()
		occluder.set_arrays(_occ_verts, _occ_indices)
		var node := OccluderInstance3D.new()
		node.name = "Occluder"
		node.occluder = occluder
		root.add_child(node)
		node.owner = root

	var body := StaticBody3D.new()
	body.name = "Collision"
	# Nascosto: la fisica non guarda la visibilita', ma l'editor (e --debug-collisions)
	# disegnerebbero il gizmo di ogni trimesh, ~14.000 triangoli semitrasparenti a
	# edificio. Sono quelli, non le mesh, a mettere in ginocchio la GPU nella vista 3D.
	body.visible = false
	root.add_child(body)
	body.owner = root
	if not _faces.is_empty():
		var shape := ConcavePolygonShape3D.new()
		shape.set_faces(_faces)
		var node := CollisionShape3D.new()
		node.name = "Trimesh"
		node.shape = shape
		# Senza riempimento: resta il solo filo di ferro quando si chiede di vederle.
		node.debug_fill = false
		body.add_child(node)
		node.owner = root
	for i in _convex.size():
		var shape := ConvexPolygonShape3D.new()
		shape.points = _convex[i]
		var node := CollisionShape3D.new()
		node.name = "Ramp%d" % i
		node.shape = shape
		node.debug_fill = false
		body.add_child(node)
		node.owner = root
	return root


## Mesh di un gruppo ("shell", "interior", "far"): una superficie per materiale,
## INDICIZZATA (i moduli arrivano come triangoli sciolti: saldare i vertici uguali
## riduce la memoria video). Con `lods` passa da ImporterMesh, che aggiunge i LOD
## automatici e la mesh d'ombra (sole posizioni) usata da ombre e depth prepass.
func _build_mesh(group: String, lods: bool) -> ArrayMesh:
	var keys := _accs.keys()
	keys.sort()
	var importer := ImporterMesh.new()
	var plain := ArrayMesh.new()
	var count := 0
	for key: String in keys:
		if not key.begins_with(group + "|"):
			continue
		var acc: Acc = _accs[key]
		if acc.verts.is_empty():
			continue
		var mat_name := key.get_slice("|", 1)
		var arrays := []
		arrays.resize(Mesh.ARRAY_MAX)
		arrays[Mesh.ARRAY_VERTEX] = acc.verts
		arrays[Mesh.ARRAY_NORMAL] = acc.normals
		arrays[Mesh.ARRAY_TEX_UV] = acc.uvs
		var tool := SurfaceTool.new()
		tool.create_from_arrays(arrays)
		tool.index()
		arrays = tool.commit_to_arrays()
		var material := _lib.material(mat_name)
		if lods:
			importer.add_surface(Mesh.PRIMITIVE_TRIANGLES, arrays, [], {}, material, mat_name)
		else:
			plain.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)
			plain.surface_set_name(count, mat_name)
			plain.surface_set_material(count, material)
		count += 1
		_stats["triangles"] += acc.verts.size() / 3
	if count == 0:
		return null
	if not lods:
		return plain
	importer.generate_lods(60.0, 0.0, [])
	return importer.get_mesh()


## Tutto il guscio opaco in una sola superficie di sole posizioni, coi vertici saldati.
func _build_shadow_mesh() -> ArrayMesh:
	var verts := PackedVector3Array()
	for key: String in _accs:
		if key.begins_with("shell|") and not NO_SHADOW.has(key.get_slice("|", 1)):
			verts.append_array((_accs[key] as Acc).verts)
	if verts.is_empty():
		return null
	var arrays := []
	arrays.resize(Mesh.ARRAY_MAX)
	arrays[Mesh.ARRAY_VERTEX] = verts
	var tool := SurfaceTool.new()
	tool.create_from_arrays(arrays)
	tool.index()
	var mesh := ArrayMesh.new()
	mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, tool.commit_to_arrays())
	return mesh
