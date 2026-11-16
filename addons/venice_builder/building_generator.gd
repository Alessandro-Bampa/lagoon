@tool
extends RefCounted

## Da un'impronta OSM a un palazzo veneziano montato coi moduli del kit.
##
## Le piante reali sono oblique e mai multiple di 2 m, quindi il montaggio e' misto:
##   - FACCIATE: moduli del kit lungo ogni lato, scalati in X per riempirlo esatto
##     (i moduli con aperture solo fra 0,75 e 1,3; fuori da li' muro cieco);
##   - SOLAI E TETTO: generati sulla pianta vera, con le texture del kit;
##   - INTERNI: tramezzi e scala a U su una griglia allineata al lato piu' lungo.
## Tutto viene FUSO in poche mesh per edificio (una superficie per materiale) e in
## un solo corpo di collisione: niente nodi per modulo.
##
## Il risultato e' in coordinate locali: origine nel baricentro dell'impronta
## (vedi `footprint_center`), y = 0 alla quota del piano terra.

const Poly := preload("polygon_tools.gd")
const KitLibrary := preload("kit_library.gd")

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
const WELL := 4.0          # lato del vano di `stair_u` (2 x 2 celle)
const LANDING := 2.0       # spazio libero richiesto davanti all'ingresso del vano
const ROOM := 5.0          # passo nominale dei tramezzi

enum Slot { FREE, PARTY, BLANK }

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
## interior_range (m, 0 = sempre visibili). `neighbours`: anelli esterni (mondo)
## degli edifici vicini, per riconoscere i muri in comune. `ground`: quote dal
## terreno ({ground, low}). Ritorna null se l'impronta non e' utilizzabile.
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
	_stats = {"modules": 0}
	_overrides = settings.get("overrides", {})
	_blocks = []
	var seed_value: int = settings.get("seed", 0)
	_rng.seed = seed_value if seed_value != 0 else hash(item["id"])

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
		rings.append(ring)
	Poly.orient(rings)
	_rings = rings

	var floors: int = settings.get("floors", 0)
	if floors <= 0:
		floors = floors_of(item)
	_facade = settings.get("facade", "")
	if _facade.is_empty():
		_facade = KitLibrary.FACADES[_rng.randi() % KitLibrary.FACADES.size()]
	_axis = _dominant_axis(rings[0])
	var interiors: bool = settings.get("interiors", true)

	var well := {}
	if interiors and floors > 1:
		well = _find_well(rings)
	_stats["stairs"] = not well.is_empty()
	_stats["well"] = well

	_facades(rings, floors, neighbours, center, well)
	_skirt(rings, maxf(float(ground.get("ground", 0.0)) - float(ground.get("low", 0.0)), 0.0) + 1.0)
	_slabs(rings, floors, well)
	_roof(rings, floors * STOREY)
	if interiors:
		if not well.is_empty():
			_stairs(well, floors)
		_partitions(rings, floors, well)
	return _assemble(item, float(settings.get("interior_range", 0.0)))


func last_stats() -> Dictionary:
	return _stats


# ============================== FACCIATE ==============================

func _facades(rings: Array, floors: int, neighbours: Array, center: Vector2, well: Dictionary) -> void:
	var noble: String = ["ext_window_gothic", "ext_window_arch"][_rng.randi() % 2]
	var common: String = ["ext_window_rect", "ext_window_rect", "ext_window_arch"][_rng.randi() % 3]
	var dense := _rng.randf() < 0.5
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
			var slots := maxi(1, roundi(run / CELL))
			var edge := {"a": a2, "d": d, "n": n, "run": run, "slots": slots, "sx": run / (CELL * slots),
				"state": [], "free": 0, "ring": r,
				"full_a": a, "full_len": length, "turn_a": turns[i], "turn_b": turns[(i + 1) % count]}
			for k in slots:
				var mid: Vector2 = a2 + d * (run * (k + 0.5) / slots)
				var state := Slot.FREE
				if r == 0 and _inside_any(mid + n * 0.7 + center, neighbours):
					state = Slot.PARTY
				elif not well.is_empty() and _near_well(mid - n * 0.45, well):
					state = Slot.BLANK
				edge["state"].append(state)
				if state == Slot.FREE:
					edge["free"] += 1
			edges.append(edge)

	# Lato principale: quello con piu' moduli liberi e scala buona. Li' vanno
	# la porta e il poggiolo del piano nobile.
	var main := -1
	var main_score := -1.0
	for e in edges.size():
		var edge: Dictionary = edges[e]
		if edge["ring"] != 0:
			continue
		var score: float = edge["free"] + (0.5 if _scale_ok(edge["sx"]) else 0.0) + edge["run"] * 0.001
		if score > main_score:
			main_score = score
			main = e
	var door_slot := -1
	if main >= 0:
		door_slot = _centre_slot(edges[main])

	var top := floors * STOREY
	for e in edges.size():
		var edge: Dictionary = edges[e]
		var slots: int = edge["slots"]
		var sx: float = edge["sx"]
		var openings := _scale_ok(sx)
		for k in slots:
			var state: int = edge["state"][k]
			var mid: Vector2 = edge["a"] + edge["d"] * (edge["run"] * (k + 0.5) / slots)
			var pattern := dense or mini(k, slots - 1 - k) % 2 == 0
			for f in floors:
				var xform := _edge_xform(mid, edge["d"], edge["n"], sx, f * STOREY)
				var facade := "facade_%s%s" % [_facade, "_base" if f == 0 else ""]
				var is_door := e == main and k == door_slot and f == 0
				var name := "ext_wall"
				if is_door:
					name = "ext_door" if openings else "ext_wall"
					if not openings:
						_stats["no_door"] = true
				elif state == Slot.FREE and openings:
					if e == main and k == door_slot and f == 1:
						name = "ext_balcony"
					elif f == 0:
						if pattern and _rng.randf() < 0.7:
							name = "ext_window_grille"
					elif pattern:
						name = _window_variant(noble if f == 1 else common)
				if name == "ext_wall" and _ruin and state == Slot.FREE and sx > 0.6:
					var roll := _rng.randf()
					if roll < 0.03 and f == 0:
						name = "ext_wall_breach"
					elif roll < 0.07:
						name = "ext_wall_hole"
				# Le scelte a mano arrivano DOPO i tiri di dado: sostituire un blocco non
				# sposta la sequenza casuale, il resto dell'edificio resta com'era.
				var key := "w:%d:%d:%d" % [e, k, f]
				name = _overrides.get(key, name)
				if _overrides.has(key + "#c"):
					facade = "facade_%s%s" % [_overrides[key + "#c"], "_base" if f == 0 else ""]
				is_door = is_door and name == "ext_door"
				var half := CELL * sx * 0.5
				_block(key, "ext", name, _edge_xform(mid, edge["d"], edge["n"], 1.0, f * STOREY),
					AABB(Vector3(-half, 0.0, -WALL), Vector3(half * 2.0, STOREY, WALL + 0.06)))
				if name != "-":
					_module("shell", name, xform, facade)
				if state != Slot.PARTY:
					if f == 0 and not is_door:
						_module("shell", "ext_plinth", xform, "", false)
					elif f > 0:
						_module("shell", "ext_stringcourse", xform, "", false)
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


func _centre_slot(edge: Dictionary) -> int:
	var slots: int = edge["slots"]
	var best := slots / 2
	var best_dist := INF
	for k in slots:
		if edge["state"][k] == Slot.FREE and absf(k - (slots - 1) * 0.5) < best_dist:
			best_dist = absf(k - (slots - 1) * 0.5)
			best = k
	return best


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
		var acc := _acc("shell", "facade_%s%s" % [_facade, "_base" if f == 0 else ""])
		var y0 := f * STOREY
		var y1 := y0 + STOREY
		var u := length / CELL
		acc.quad(Vector3(from.x, y0, from.y), Vector3(to.x, y0, to.y), Vector3(to.x, y1, to.y), Vector3(from.x, y1, from.y),
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
	var acc := _acc("shell", "stone_blocks")
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

func _slabs(rings: Array, floors: int, well: Dictionary) -> void:
	var ground_mat: String = ["paving", "terrazzo"][_rng.randi() % 2]
	var upper_mat: String = ["planks", "terrazzo", "planks"][_rng.randi() % 3]
	var holed := rings
	if not well.is_empty():
		holed = rings.duplicate()
		holed.append(well["ring"])
		Poly.orient(holed)
	var solid_tris := Poly.triangulate(rings)
	var holed_tris := Poly.triangulate(holed) if holed != rings else solid_tris
	for f in floors:
		var has_hole := f > 0 and not well.is_empty()
		var floor_mat: String = _overrides.get("f:%d" % f, ground_mat if f == 0 else upper_mat)
		_block("f:%d" % f, "floor", floor_mat, Transform3D(Basis.IDENTITY, Vector3(0.0, f * STOREY, 0.0)), AABB())
		_flat(has_hole_tris(has_hole, holed_tris, solid_tris), f * STOREY, true, floor_mat, CELL, true)
		# Soffitto del piano f: bucato se il vano prosegue al piano di sopra.
		var open_above := f < floors - 1 and not well.is_empty()
		var ceiling_mat: String = _overrides.get("c:%d" % f, "ceiling")
		_block("c:%d" % f, "ceiling", ceiling_mat, Transform3D(Basis.IDENTITY, Vector3(0.0, f * STOREY + CLEAR, 0.0)), AABB())
		_flat(has_hole_tris(open_above, holed_tris, solid_tris), f * STOREY + CLEAR, false, ceiling_mat, 1.0 if ceiling_mat == "ceiling" else CELL, false)


func has_hole_tris(holed: bool, holed_tris: PackedVector2Array, solid_tris: PackedVector2Array) -> PackedVector2Array:
	return holed_tris if holed else solid_tris


func _flat(tris: PackedVector2Array, y: float, up: bool, mat: String, tile: float, collide: bool) -> void:
	var acc := _acc("shell", mat)
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

func _dominant_axis(ring: PackedVector2Array) -> Vector2:
	var best := Vector2.RIGHT
	var best_len := 0.0
	for i in ring.size():
		var d := ring[(i + 1) % ring.size()] - ring[i]
		if d.length() > best_len:
			best_len = d.length()
			best = d.normalized()
	return best


## Cerca dove far stare `stair_u`: un vano 4 x 4 m piu' 2 m liberi davanti
## all'ingresso, tutto dentro la pianta. Fra i posti validi sceglie il piu'
## vicino a un muro, cosi' la scala non finisce in mezzo a una stanza.
## Ritorna {} se non ci sta (edificio stretto): i piani alti restano senza scala.
func _find_well(rings: Array) -> Dictionary:
	var u := _axis
	var v := Vector2(-u.y, u.x)
	var lo := Vector2(INF, INF)
	var hi := Vector2(-INF, -INF)
	for p in rings[0]:
		var q := Vector2(p.dot(u), p.dot(v))
		lo = lo.min(q)
		hi = hi.max(q)
	var size := hi - lo
	if minf(size.x, size.y) < WELL + 2.0 * WALL or maxf(size.x, size.y) < WELL + LANDING + 2.0 * WALL:
		return {}
	var step := 1.0 if maxf(size.x, size.y) < 40.0 else 2.0
	var margin := WALL + 0.05
	var best := {}
	var best_score := INF
	for right: Vector2 in [u, v, -u, -v]:
		var forward := Vector2(right.y, -right.x)
		var gu := lo.x
		while gu <= hi.x:
			var gv := lo.y
			while gv <= hi.y:
				var origin := u * gu + v * gv
				if _well_fits(origin, right, forward, margin, rings):
					var centre := origin + forward * (WELL * 0.5)
					var score := Poly.boundary_distance(centre, rings) + _rng.randf() * 0.2
					if score < best_score:
						best_score = score
						best = {"origin": origin, "right": right, "forward": forward}
				gv += step
			gu += step
	if best.is_empty():
		return {}
	var o: Vector2 = best["origin"]
	var r: Vector2 = best["right"]
	var f: Vector2 = best["forward"]
	best["ring"] = PackedVector2Array([o - r * 2.0, o + r * 2.0, o + r * 2.0 + f * WELL, o - r * 2.0 + f * WELL])
	return best


func _well_fits(origin: Vector2, right: Vector2, forward: Vector2, margin: float, rings: Array) -> bool:
	var half := WELL * 0.5 + margin
	for ix in 4:
		var x := lerpf(-half, half, ix / 3.0)
		for iz in 5:
			var depth := lerpf(-LANDING, WELL + margin, iz / 4.0)
			if not Poly.inside(origin + right * x + forward * depth, rings):
				return false
	# Nessun vertice della pianta dentro il vano (una rientranza fra due campioni).
	for ring: PackedVector2Array in rings:
		for p in ring:
			var rel := p - origin
			if absf(rel.dot(right)) < half and rel.dot(forward) > -LANDING and rel.dot(forward) < WELL + margin:
				return false
	return true


func _near_well(p: Vector2, well: Dictionary) -> bool:
	var rel: Vector2 = p - well["origin"]
	var x: float = rel.dot(well["right"])
	var depth: float = rel.dot(well["forward"])
	return absf(x) < WELL * 0.5 + 0.6 and depth > -0.6 and depth < WELL + 0.6


func _stairs(well: Dictionary, floors: int) -> void:
	var o: Vector2 = well["origin"]
	var r: Vector2 = well["right"]
	var f: Vector2 = well["forward"]
	var x3 := Vector3(r.x, 0.0, r.y)
	var z3 := -Vector3(f.x, 0.0, f.y)
	var front := Basis(x3, Vector3.UP, z3)            # come la scala: +Z verso l'ingresso
	var back := front.rotated(Vector3.UP, PI)         # +Z verso il fondo del vano
	var side_plus := front.rotated(Vector3.UP, PI * 0.5)    # +Z verso +X della scala
	var side_minus := front.rotated(Vector3.UP, -PI * 0.5)
	for level in floors:
		var y := level * STOREY
		if level < floors - 1:
			_module("interior", "stair_u", Transform3D(front, _v3(o, y)), "")
		# Chiusura del vano sui tre lati diversi dall'ingresso.
		for s: float in [-1.0, 1.0]:
			_module("interior", "int_wall", Transform3D(front, _v3(o + f * WELL + r * s, y)), "")
			for depth: float in [1.0, 3.0]:
				_module("interior", "int_wall", Transform3D(side_plus, _v3(o + r * (2.0 * s) + f * depth, y)), "")
		if level == floors - 1:
			# Ultimo piano: sopra la prima rampa del piano sotto non c'e' altra
			# rampa a coprire il vuoto. Lo chiude un tramezzo sul bordo d'ingresso.
			_module("interior", "int_wall", Transform3D(front, _v3(o - r, y)), "")
		if level > 0:
			# Testa del solaio sui quattro lati del foro, rivolta verso il vuoto.
			for s: float in [-1.0, 1.0]:
				_module("interior", "floor_edge", Transform3D(front, _v3(o + f * WELL + r * s, y)), "", false)
				_module("interior", "floor_edge", Transform3D(back, _v3(o + r * s, y)), "", false)
			for depth: float in [1.0, 3.0]:
				_module("interior", "floor_edge", Transform3D(side_plus, _v3(o - r * 2.0 + f * depth, y)), "", false)
				_module("interior", "floor_edge", Transform3D(side_minus, _v3(o + r * 2.0 + f * depth, y)), "", false)


## Tramezzi perpendicolari al lato lungo, ogni ROOM metri circa, con una porta
## ciascuno: stanze in fila, comunicanti. Mai attraverso il vano scala ne' davanti
## al suo ingresso.
func _partitions(rings: Array, floors: int, well: Dictionary) -> void:
	var u := _axis
	var v := Vector2(-u.y, u.x)
	var lo := INF
	var hi := -INF
	for p in rings[0]:
		lo = minf(lo, p.dot(u))
		hi = maxf(hi, p.dot(u))
	var rooms := maxi(1, roundi((hi - lo) / ROOM))
	if rooms < 2:
		return
	# Ingombro del vano (ingresso compreso) nel riferimento u, v.
	var keep_lo := Vector2(INF, INF)
	var keep_hi := Vector2(-INF, -INF)
	if not well.is_empty():
		var o: Vector2 = well["origin"]
		var r: Vector2 = well["right"]
		var f: Vector2 = well["forward"]
		for corner: Vector2 in [o - r * 2.0 - f * LANDING, o + r * 2.0 - f * LANDING, o - r * 2.0 + f * WELL, o + r * 2.0 + f * WELL]:
			var q := Vector2(corner.dot(u), corner.dot(v))
			keep_lo = keep_lo.min(q - Vector2(0.3, 0.3))
			keep_hi = keep_hi.max(q + Vector2(0.3, 0.3))
	var x3 := Vector3(v.x, 0.0, v.y)
	var basis := Basis(x3, Vector3.UP, x3.cross(Vector3.UP))
	for k in range(1, rooms):
		var t := lerpf(lo, hi, float(k) / rooms)
		var pieces: Array = []
		for span: Array in Poly.line_spans(u * t, v, rings):
			var s0: float = span[0] + 0.15
			var s1: float = span[1] - 0.15
			if t > keep_lo.x and t < keep_hi.x and s1 > keep_lo.y and s0 < keep_hi.y:
				pieces.append([s0, minf(s1, keep_lo.y)])
				pieces.append([maxf(s0, keep_hi.y), s1])
			else:
				pieces.append([s0, s1])
		var piece_index := -1
		for piece: Array in pieces:
			piece_index += 1
			var length: float = piece[1] - piece[0]
			if length < 1.5:
				continue
			var slots := maxi(1, roundi(length / CELL))
			var sx := length / (CELL * slots)
			var can_door := _scale_ok(sx)
			for level in floors:
				var door := _rng.randi() % slots
				for s in slots:
					var name := "int_wall"
					if s == door:
						# niente porta della misura giusta: resta un varco
						name = "int_door" if can_door else "-"
					elif _ruin and _rng.randf() < 0.12:
						name = "int_wall_breach"
					var key := "p:%d:%d:%d:%d" % [k, piece_index, s, level]
					name = _overrides.get(key, name)
					var mid: Vector2 = u * t + v * (piece[0] + length * (s + 0.5) / slots)
					var half := CELL * sx * 0.5
					_block(key, "int", name, Transform3D(basis, _v3(mid, level * STOREY)),
						AABB(Vector3(-half, 0.0, -0.1), Vector3(half * 2.0, CLEAR, 0.2)))
					if name == "-":
						continue
					var scaled := Basis(basis.x * sx, basis.y, basis.z)
					_module("interior", name, Transform3D(scaled, _v3(mid, level * STOREY)), "")


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
func _module(group: String, name: String, xform: Transform3D, facade: String, collide: bool = true) -> void:
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
	if collide:
		_faces.append_array(xform * (data["faces"] as PackedVector3Array))
		for points: PackedVector3Array in data["convex"]:
			_convex.append(xform * points)


## Collisione di una faccia verticale fatta a mano (punte e riempimenti).
func _wall_faces(a: Vector2, b: Vector2, y0: float, y1: float) -> void:
	var p := [Vector3(a.x, y0, a.y), Vector3(b.x, y0, b.y), Vector3(b.x, y1, b.y), Vector3(a.x, y1, a.y)]
	for i in [0, 1, 2, 0, 2, 3]:
		_faces.append(p[i])


func _assemble(item: Dictionary, interior_range: float) -> Node3D:
	var root := Node3D.new()
	root.name = "Building"
	root.set_meta("osm_id", item["id"])
	root.set_meta("blocks", _blocks)
	root.set_meta("rings", _rings)
	root.set_meta("kit", _lib.kit)
	var triangles := 0
	for group: String in ["shell", "interior"]:
		var mesh := ArrayMesh.new()
		var keys := _accs.keys()
		keys.sort()
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
			mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)
			var index := mesh.get_surface_count() - 1
			mesh.surface_set_name(index, mat_name)
			mesh.surface_set_material(index, _lib.material(mat_name))
			triangles += acc.verts.size() / 3
		if mesh.get_surface_count() == 0:
			continue
		var node := MeshInstance3D.new()
		node.name = "Shell" if group == "shell" else "Interior"
		node.mesh = mesh
		if group == "interior" and interior_range > 0.0:
			# Tramezzi e scale non si vedono da lontano: fuori portata non si disegnano.
			node.visibility_range_end = interior_range
			node.visibility_range_end_margin = 5.0
		root.add_child(node)
		node.owner = root
	_stats["triangles"] = triangles
	_stats["surfaces"] = _accs.size()

	var body := StaticBody3D.new()
	body.name = "Collision"
	root.add_child(body)
	body.owner = root
	if not _faces.is_empty():
		var shape := ConcavePolygonShape3D.new()
		shape.set_faces(_faces)
		var node := CollisionShape3D.new()
		node.name = "Trimesh"
		node.shape = shape
		body.add_child(node)
		node.owner = root
	for i in _convex.size():
		var shape := ConvexPolygonShape3D.new()
		shape.points = _convex[i]
		var node := CollisionShape3D.new()
		node.name = "Ramp%d" % i
		node.shape = shape
		body.add_child(node)
		node.owner = root
	return root
