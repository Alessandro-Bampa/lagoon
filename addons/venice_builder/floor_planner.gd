@tool
extends RefCounted

## Pianta degli interni di un edificio, un piano alla volta, su una griglia di celle.
##
## La griglia (celle da 2 m, come i moduli del kit) e' allineata al lato piu' lungo
## della pianta e parte dalla FACCIA INTERNA dei muri esterni. Ogni piano si decide
## in quest'ordine, e ogni passo dipende da cio' che c'e' gia':
##   1. la scala che sale da sotto (fissata una volta per edificio) e il suo sbarco;
##   2. il disimpegno ("hall"): androne o salone al piano terra, pianerottolo sopra;
##   3. le unita' (appartamenti, solo nei condomini) e le stanze, per tagli successivi
##      lungo le linee di griglia di cio' che resta;
##   4. i muri fra stanze diverse e le porte: un albero che parte dal disimpegno,
##      cosi' ogni stanza e' raggiungibile per costruzione.
## Qui non si genera geometria: esce un dizionario (`plan_floor`) che il generatore
## convalida contro le facciate e poi monta.
##
## Celle: Vector2i (i lungo u, j lungo v). Bordi: Vector3i(i, j, lato), lato 0 = fra
## (i-1, j) e (i, j), sulla linea u = i; lato 1 = fra (i, j-1) e (i, j), sulla linea v = j.

const Poly := preload("polygon_tools.gd")

const CELL := 2.0
const WALL := 0.3
const BURY := 0.15         # m di cui un tramezzo entra nel muro esterno
const DOOR_MIN := 0.75     # celle: tratto minimo su cui sta un modulo con la porta
const DIRS: Array[Vector2i] = [Vector2i(1, 0), Vector2i(0, 1), Vector2i(-1, 0), Vector2i(0, -1)]

enum Kind { CASA, PADRONALE, CONDOMINIO }

var rings: Array = []
var u := Vector2.RIGHT
var v := Vector2.DOWN
var cover: Dictionary = {}    # cella -> frazione dentro i muri (solo se > 0)
var full: Dictionary = {}     # celle tutte interne: le sole su cui sta una scala
var usable: Dictionary = {}   # celle interne per piu' di meta': quelle che fanno stanza
var parent: Dictionary = {}   # cella di frangia -> cella usabile di cui segue la stanza

var _ou := 0.0
var _ov := 0.0
var _spans: Dictionary = {}
var _clips: Dictionary = {}


## `axis`: direzione del lato piu' lungo; `anchor`: un suo punto. La linea v = 0
## della griglia cade sulla faccia interna di quel muro.
func setup(p_rings: Array, axis: Vector2, anchor: Vector2) -> void:
	rings = p_rings
	u = axis
	v = Vector2(-u.y, u.x)
	var lo := Vector2(INF, INF)
	var hi := Vector2(-INF, -INF)
	for p: Vector2 in rings[0]:
		var q := Vector2(p.dot(u), p.dot(v))
		lo = Vector2(minf(lo.x, q.x), minf(lo.y, q.y))
		hi = Vector2(maxf(hi.x, q.x), maxf(hi.y, q.y))
	var base_v := anchor.dot(v) + WALL
	_ov = base_v - CELL * ceilf((base_v - lo.y) / CELL - 0.001)
	# Lungo u si prova qualche sfasamento e si tiene quello con piu' celle intere.
	var tries: Array[float] = [0.0]
	if (hi.x - lo.x) * (hi.y - lo.y) < 900.0:
		tries = [0.0, 0.5, 1.0, 1.5]
	var best_off := 0.0
	var best_score := -1.0
	for off in tries:
		_classify(lo.x + WALL + off - (CELL if off > 0.0 else 0.0), hi)
		var score := full.size() + usable.size() * 0.25 - off * 0.01
		if score > best_score:
			best_score = score
			best_off = off
	if best_off != tries[tries.size() - 1]:
		_classify(lo.x + WALL + best_off - (CELL if best_off > 0.0 else 0.0), hi)


func _classify(origin_u: float, hi: Vector2) -> void:
	_ou = origin_u
	cover.clear(); full.clear(); usable.clear(); parent.clear()
	_spans.clear(); _clips.clear()
	var ni := ceili((hi.x - _ou) / CELL)
	var nj := ceili((hi.y - _ov) / CELL)
	var steps: Array[float] = [1.0 / 6.0, 0.5, 5.0 / 6.0]
	for i in ni:
		for j in nj:
			var hits := 0
			for a in steps:
				for b in steps:
					if interior(at(i + a, j + b)):
						hits += 1
			if hits == 0:
				continue
			var cell := Vector2i(i, j)
			cover[cell] = hits / 9.0
			if hits >= 5:
				usable[cell] = true
			if hits == 9 and interior(at(i + 0.01, j + 0.01)) and interior(at(i + 0.99, j + 0.01)) \
					and interior(at(i + 0.99, j + 0.99)) and interior(at(i + 0.01, j + 0.99)):
				full[cell] = true
	for cell: Vector2i in cover:
		if usable.has(cell):
			continue
		var best := -1.0
		for d in DIRS:
			var n: Vector2i = cell + d
			if usable.has(n) and cover[n] > best:
				best = cover[n]
				parent[cell] = n


## Punto locale dell'edificio dalle coordinate di griglia (in celle).
func at(gx: float, gy: float) -> Vector2:
	return u * (_ou + gx * CELL) + v * (_ov + gy * CELL)


func cell_of(p: Vector2) -> Vector2i:
	return Vector2i(floori((p.dot(u) - _ou) / CELL), floori((p.dot(v) - _ov) / CELL))


## Coordinata di griglia (in celle) di un punto lungo u (asse 0) o v (asse 1).
func grid_coord(p: Vector2, axis: int) -> float:
	return (p.dot(u) - _ou) / CELL if axis == 0 else (p.dot(v) - _ov) / CELL


func interior(p: Vector2, margin: float = WALL - 0.02) -> bool:
	return Poly.inside(p, rings) and Poly.boundary_distance(p, rings) >= margin


func kind_for(floors: int) -> int:
	var n := usable.size()
	if (floors >= 4 and n >= 14) or (floors >= 2 and n >= 40):
		return Kind.CONDOMINIO
	if floors >= 2 and floors <= 3 and n >= 12:
		return Kind.PADRONALE
	return Kind.CASA


static func edge_key(a: Vector2i, b: Vector2i) -> Vector3i:
	if a.x != b.x:
		return Vector3i(maxi(a.x, b.x), a.y, 0)
	return Vector3i(a.x, maxi(a.y, b.y), 1)


## Le due celle separate da un bordo.
static func edge_cells(key: Vector3i) -> Array[Vector2i]:
	if key.z == 0:
		return [Vector2i(key.x - 1, key.y), Vector2i(key.x, key.y)]
	return [Vector2i(key.x, key.y - 1), Vector2i(key.x, key.y)]


## Punto locale sul bordo `key` alla coordinata `s` (in celle) lungo la sua linea.
func edge_point(key: Vector3i, s: float) -> Vector2:
	return at(key.x, s) if key.z == 0 else at(s, key.y)


func room_of(plan: Dictionary, cell: Vector2i) -> int:
	var room: Dictionary = plan["room"]
	if room.has(cell):
		return room[cell]
	if parent.has(cell):
		# Un ritaglio dietro una scala chiusa non e' di nessuno: resta murato.
		if plan["enclosed"] and plan["stair"].has(parent[cell]):
			return -1
		return room.get(parent[cell], -1)
	return -1


# ------------------------------ geometria dei bordi ------------------------------

## Tratti interni alla pianta di una linea di griglia, in celle: [[da, a], ...].
func _line(side: int, index: int) -> Array:
	var key := Vector2i(side, index)
	if not _spans.has(key):
		var out: Array = []
		var o := at(index, 0.0) if side == 0 else at(0.0, index)
		for span: Array in Poly.line_spans(o, v if side == 0 else u, rings):
			out.append([span[0] / CELL, span[1] / CELL])
		_spans[key] = out
	return _spans[key]


## Parte di un bordo di cella su cui puo' stare un tramezzo: {s0, s1, ext0, ext1, span}
## (`ext` = da quel capo finisce contro un muro esterno), oppure {} se e' tutto fuori.
func clip(key: Vector3i) -> Dictionary:
	if _clips.has(key):
		return _clips[key]
	var side := key.z
	var from := float(key.y if side == 0 else key.x)
	var out := {}
	var best := 0.02
	for span: Array in _line(side, key.x if side == 0 else key.y):
		var lo := maxf(from, span[0] + BURY / CELL)
		var hi := minf(from + 1.0, span[1] - BURY / CELL)
		if hi - lo > best:
			best = hi - lo
			out = {"s0": lo, "s1": hi, "ext0": lo > from + 0.0001, "ext1": hi < from + 0.9999, "span": span}
	_clips[key] = out
	return out


# ------------------------------ scala ------------------------------

## Cerca dove mettere la scala. `types`: [{name, w (celle di larghezza), enclosed,
## straight, bias}]. `facade_cells`: cella -> finestre che perderebbe se la scala
## le stesse davanti. Ritorna {} se nessun tipo ci sta.
## La scala occupa `w` x 2 celle tutte intere, piu' lo sbarco davanti (e, per la
## rampa dritta, la cella d'arrivo al piano di sopra).
func find_stair(types: Array, facade_cells: Dictionary, door_cell: Variant, rng: RandomNumberGenerator) -> Dictionary:
	var best := {}
	var best_score := INF
	var check_cut := usable.size() <= 160
	for type: Dictionary in types:
		var w: int = type["w"]
		for k in 4:
			var right := DIRS[k]
			var forward := Vector2i(right.y, -right.x)
			for anchor: Vector2i in full:
				var cells: Array = []
				var landing: Array = []
				var ok := true
				for a in w:
					for d in 2:
						var c: Vector2i = anchor + forward * d + right * a
						if not full.has(c) or (door_cell != null and c == door_cell):
							ok = false
						cells.append(c)
					var l: Vector2i = anchor - forward + right * a
					if not full.has(l):
						ok = false
					landing.append(l)
				var arrival: Array = []
				if type["straight"]:
					var top: Vector2i = anchor + forward * 2
					if not full.has(top):
						ok = false
					arrival.append(top)
				if not ok:
					continue
				var score: float = type["bias"] + rng.randf() * 2.5
				var inside := {}
				for c: Vector2i in cells:
					inside[c] = true
					score += float(facade_cells.get(c, 0))
				# Una scala sta volentieri contro i muri.
				for c: Vector2i in cells:
					for d in DIRS:
						if not cover.has(c + d):
							score -= 0.6
						elif not usable.has(c + d):
							score += 2.0   # ritaglio di pianta dietro la scala: spazio perso
				if check_cut:
					score += 4.0 * _cut_off(inside, landing[0])
				if score < best_score:
					best_score = score
					best = {"type": type["name"], "w": w, "enclosed": type["enclosed"], "straight": type["straight"],
						"anchor": anchor, "right": right, "forward": forward,
						"cells": cells, "landing": landing, "arrival": arrival}
	if best.is_empty():
		return {}
	var an: Vector2i = best["anchor"]
	var rt: Vector2i = best["right"]
	var fw: Vector2i = best["forward"]
	var width: int = best["w"]
	var r := u * rt.x + v * rt.y
	var f := u * fw.x + v * fw.y
	var o := at(an.x + 0.5, an.y + 0.5) - f * (CELL * 0.5) + r * ((width - 1) * CELL * 0.5)
	best["o"] = o
	best["r"] = r
	best["f"] = f
	best["ring"] = PackedVector2Array([o - r * width, o + r * width, o + r * width + f * (2.0 * CELL), o - r * width + f * (2.0 * CELL)])
	var open := {}
	for a in width:
		open[edge_key(an + rt * a, an - fw + rt * a)] = true
	if best["straight"]:
		open[edge_key(an + fw, an + fw * 2)] = true
	best["open"] = open
	return best


## Celle usabili che la scala taglierebbe fuori dal suo sbarco.
func _cut_off(blocked: Dictionary, start: Vector2i) -> int:
	var seen := {start: true}
	var queue: Array[Vector2i] = [start]
	while not queue.is_empty():
		var c: Vector2i = queue.pop_back()
		for d in DIRS:
			var n: Vector2i = c + d
			if usable.has(n) and not blocked.has(n) and not seen.has(n):
				seen[n] = true
				queue.append(n)
	return usable.size() - blocked.size() - seen.size()


# ------------------------------ pianta di un piano ------------------------------

## Pianta del piano `level`. Ritorna:
##   room   cella -> indice in `rooms` (le celle di frangia si leggono con `room_of`)
##   rooms  [{kind: "hall" | "stair" | "room" | "gone", unit, cells}]; 0 e' il disimpegno
##   walls  bordo -> {m: modulo, a, b: stanze, s0, s1: estremi in celle, door_ok}
##   posts  punti (locali) in cui va un `int_post`
##   hits   [punto locale, bordo]: dove un tramezzo finisce contro un muro esterno
##   stair  celle occupate dalla scala a questo piano
##   enclosed  la scala e' una stanza a se', chiusa da muri
##   merged, isolated  stanze fuse con una vicina perche' senza porta / rimaste senza
func plan_floor(level: int, kind: int, stair: Dictionary, door_cell: Variant, ruin: bool, rng: RandomNumberGenerator) -> Dictionary:
	var plan := {"room": {}, "rooms": [], "walls": {}, "posts": [], "hits": [], "stair": {}, "open": {}, "enclosed": false,
		"isolated": 0, "merged": 0}
	var avail := {}
	for c: Vector2i in usable:
		avail[c] = true
	var seeds: Array = []
	var has_stair: bool = not stair.is_empty() and (level <= 1 or not stair["straight"])
	if has_stair:
		for c: Vector2i in stair["cells"]:
			avail.erase(c)
			plan["stair"][c] = true
		plan["open"] = stair["open"]
		plan["enclosed"] = stair["enclosed"]
		seeds = stair["arrival"] if (stair["straight"] and level == 1) else stair["landing"]

	var hall := {}
	for c: Vector2i in seeds:
		hall[c] = true
	if level == 0 and door_cell != null and avail.has(door_cell):
		if hall.is_empty():
			hall[door_cell] = true
		else:
			for c: Vector2i in _path(hall, {door_cell: true}, avail):
				hall[c] = true
	if hall.is_empty() and not avail.is_empty():
		hall[_central(avail)] = true

	var parts: Array = []   # [unita', celle]
	if kind == Kind.CONDOMINIO:
		parts = _plan_condo(hall, avail, rng)
	else:
		parts = _plan_house(hall, avail, level, kind, rng)

	var rooms: Array = plan["rooms"]
	var hall_cells: Array = hall.keys()
	if has_stair and not stair["enclosed"]:
		# Scala aperta: sta dentro il disimpegno, senza muri attorno.
		hall_cells.append_array(stair["cells"])
	rooms.append({"kind": "hall", "unit": 0, "cells": hall_cells})
	if has_stair and stair["enclosed"]:
		rooms.append({"kind": "stair", "unit": -1, "cells": stair["cells"].duplicate()})
	for part: Array in parts:
		rooms.append({"kind": "room", "unit": part[0], "cells": part[1]})
	for index in rooms.size():
		for c: Vector2i in rooms[index]["cells"]:
			plan["room"][c] = index

	# Muri e porte; una stanza rimasta senza porta si fonde con una vicina e si riprova.
	var left: Array = []
	for attempt in 4:
		_build_walls(plan)
		left = _doors(plan, kind, rng)
		if left.is_empty():
			break
		var merged := false
		for index: int in left:
			merged = _merge_room(plan, index, kind) or merged
			plan["merged"] += 1
		if not merged:
			break
	plan["isolated"] = left.size()
	if ruin:
		for key: Vector3i in plan["walls"]:
			var wall: Dictionary = plan["walls"][key]
			if wall["m"] == "int_wall" and wall["door_ok"] and rng.randf() < 0.1:
				wall["m"] = "int_wall_breach"
	_wall_geometry(plan)
	return plan


## Casa unica: un disimpegno ampio (salone, o la stanza dello sbarco) e attorno le
## altre stanze. `kind` cambia solo le proporzioni.
func _plan_house(hall: Dictionary, avail: Dictionary, level: int, kind: int, rng: RandomNumberGenerator) -> Array:
	var n := avail.size()
	if n <= 5:
		for c: Vector2i in avail:
			hall[c] = true
		return []
	var share := rng.randf_range(0.3, 0.45) if level == 0 else rng.randf_range(0.15, 0.3)
	if kind == Kind.CASA:
		share = rng.randf_range(0.35, 0.6)
	_inflate(hall, avail, ceili(n * share), rng)
	var room_max := rng.randi_range(5, 9) if kind == Kind.PADRONALE else rng.randi_range(3, 7)
	var parts: Array = []
	for comp: Array in _components(_minus(avail, hall)):
		if comp.size() == 1 and _touches(comp, hall):
			hall[comp[0]] = true
			continue
		for room: Array in _bsp(comp, room_max, 2, rng, {}):
			parts.append([0, room])
	return parts


## Condominio: il disimpegno e' solo pianerottolo e corridoi; il resto si divide in
## unita' che devono tutte affacciarsi sul disimpegno, e ogni unita' in stanze.
func _plan_condo(hall: Dictionary, avail: Dictionary, rng: RandomNumberGenerator) -> Array:
	var unit_max := rng.randi_range(9, 18)
	var units: Array = []
	for comp: Array in _components(_minus(avail, hall)):
		units.append_array(_bsp(comp, unit_max, 5, rng, hall))
	# Un'unita' che non tocca il disimpegno: gli si porta un corridoio.
	var given_up := {}
	for round in 8:
		var target := -1
		for k in units.size():
			if not given_up.has(units[k][0]) and not _touches(units[k], hall):
				target = k
				break
		if target < 0:
			break
		var goals := {}
		for c: Vector2i in units[target]:
			goals[c] = true
		var path := _path(hall, goals, avail)
		if path.size() < 2:
			given_up[units[target][0]] = true
			continue
		path.remove_at(path.size() - 1)
		for c: Vector2i in path:
			hall[c] = true
		var next: Array = []
		for unit: Array in units:
			var rest: Array = []
			for c: Vector2i in unit:
				if not hall.has(c):
					rest.append(c)
			for comp: Array in _components(rest):
				if comp.size() == 1 and _touches(comp, hall):
					hall[comp[0]] = true
				else:
					next.append(comp)
		units = next
	var parts: Array = []
	for k in units.size():
		var unit: Array = units[k]
		if unit.size() <= 3:
			parts.append([k + 1, unit])
			continue
		for room: Array in _bsp(unit, rng.randi_range(4, 7), 2, rng, {}):
			parts.append([k + 1, room])
	return parts


## Allarga il disimpegno a strisce attorno al suo ingombro, finche' non arriva a
## `target` celle: resta una forma compatta, fatta di rettangoli.
func _inflate(hall: Dictionary, avail: Dictionary, target: int, rng: RandomNumberGenerator) -> void:
	for guard in 64:
		if hall.size() >= target:
			return
		var lo := Vector2i(1 << 30, 1 << 30)
		var hi := Vector2i(-(1 << 30), -(1 << 30))
		for c: Vector2i in hall:
			lo = Vector2i(mini(lo.x, c.x), mini(lo.y, c.y))
			hi = Vector2i(maxi(hi.x, c.x), maxi(hi.y, c.y))
		var options: Array = []
		for d in DIRS:
			var strip: Array = []
			if d.x != 0:
				for j in range(lo.y, hi.y + 1):
					strip.append(Vector2i(hi.x + 1 if d.x > 0 else lo.x - 1, j))
			else:
				for i in range(lo.x, hi.x + 1):
					strip.append(Vector2i(i, hi.y + 1 if d.y > 0 else lo.y - 1))
			# Solo le celle libere e attaccate (anche a catena) al disimpegno.
			var taken := {}
			var grown := true
			while grown:
				grown = false
				for c: Vector2i in strip:
					if taken.has(c) or hall.has(c) or not avail.has(c):
						continue
					for e in DIRS:
						if hall.has(c + e) or taken.has(c + e):
							taken[c] = true
							grown = true
							break
			if taken.size() * 2 >= strip.size():
				options.append(taken.keys())
		if options.is_empty():
			return
		for c: Vector2i in options[rng.randi() % options.size()]:
			hall[c] = true


## Taglia un insieme di celle lungo le linee di griglia finche' ogni pezzo ha al piu'
## `max_size` celle. Fra i tagli possibili sceglie con un po' di caso, preferendo
## pezzi equilibrati, tagli sul lato corto e (se `touch` non e' vuoto) pezzi che
## toccano tutti quelle celle.
func _bsp(cells: Array, max_size: int, min_size: int, rng: RandomNumberGenerator, touch: Dictionary) -> Array:
	if cells.size() <= max_size:
		return [cells]
	var lo := Vector2i(1 << 30, 1 << 30)
	var hi := Vector2i(-(1 << 30), -(1 << 30))
	for c: Vector2i in cells:
		lo = Vector2i(mini(lo.x, c.x), mini(lo.y, c.y))
		hi = Vector2i(maxi(hi.x, c.x), maxi(hi.y, c.y))
	var span := hi - lo + Vector2i.ONE
	var best_a: Array = []
	var best_b: Array = []
	var best_score := INF
	for axis in 2:
		var extent := span.x if axis == 0 else span.y
		var other := span.y if axis == 0 else span.x
		for cut in range(1, extent):
			var limit := (lo.x if axis == 0 else lo.y) + cut
			var a: Array = []
			var b: Array = []
			for c: Vector2i in cells:
				if (c.x if axis == 0 else c.y) < limit:
					a.append(c)
				else:
					b.append(c)
			if a.size() < min_size or b.size() < min_size:
				continue
			var score := absf(a.size() - b.size()) / cells.size() + rng.randf() * 0.6
			if extent < other:
				score += 0.4
			if not touch.is_empty():
				if not _touches(a, touch):
					score += 0.8
				if not _touches(b, touch):
					score += 0.8
			if score < best_score:
				best_score = score
				best_a = a
				best_b = b
	if best_a.is_empty():
		return [cells]
	var big: Array = []
	var small: Array = []
	for part: Array in _components(best_a) + _components(best_b):
		if part.size() >= min_size:
			big.append(part)
		else:
			small.append(part)
	# I ritagli troppo piccoli si riattaccano a un pezzo vicino.
	for part: Array in small:
		var near := {}
		for c: Vector2i in part:
			near[c] = true
		var joined := false
		for target: Array in big:
			if _touches(target, near):
				target.append_array(part)
				joined = true
				break
		if not joined:
			big.append(part)
	if big.size() < 2:
		return [cells]
	var out: Array = []
	for part: Array in big:
		out.append_array(_bsp(part, max_size, min_size, rng, touch))
	return out


func _minus(avail: Dictionary, taken: Dictionary) -> Array:
	var out: Array = []
	for c: Vector2i in avail:
		if not taken.has(c):
			out.append(c)
	return out


func _components(cells: Array) -> Array:
	var todo := {}
	for c: Vector2i in cells:
		todo[c] = true
	var out: Array = []
	for start: Vector2i in cells:
		if not todo.has(start):
			continue
		todo.erase(start)
		var comp: Array = [start]
		var head := 0
		while head < comp.size():
			var c: Vector2i = comp[head]
			head += 1
			for d in DIRS:
				if todo.has(c + d):
					todo.erase(c + d)
					comp.append(c + d)
		out.append(comp)
	return out


func _touches(cells: Array, other: Dictionary) -> bool:
	for c: Vector2i in cells:
		for d in DIRS:
			if other.has(c + d):
				return true
	return false


## Cammino piu' corto dalle celle `from` a una di `goals` attraverso `avail`:
## le celle percorse, traguardo compreso (vuoto se non ci si arriva).
func _path(from: Dictionary, goals: Dictionary, avail: Dictionary) -> Array:
	var prev := {}
	var queue: Array[Vector2i] = []
	for c: Vector2i in from:
		prev[c] = c
		queue.append(c)
	var head := 0
	while head < queue.size():
		var c := queue[head]
		head += 1
		# Prima si prova a proseguire dritti: i corridoi escono con meno gomiti.
		var straight: Vector2i = c - prev[c]
		var order: Array[Vector2i] = DIRS.duplicate()
		if straight != Vector2i.ZERO:
			order.erase(straight)
			order.push_front(straight)
		for d in order:
			var n: Vector2i = c + d
			if prev.has(n) or not avail.has(n):
				continue
			prev[n] = c
			if goals.has(n):
				var path: Array = []
				var walk := n
				while not from.has(walk):
					path.push_front(walk)
					walk = prev[walk]
				return path
			queue.append(n)
	return []


func _central(avail: Dictionary) -> Vector2i:
	var mean := Vector2.ZERO
	for c: Vector2i in avail:
		mean += Vector2(c)
	mean /= avail.size()
	var best := Vector2i.ZERO
	var best_dist := INF
	for c: Vector2i in avail:
		var dist := Vector2(c).distance_squared_to(mean) + (0.0 if full.has(c) else 4.0)
		if dist < best_dist:
			best_dist = dist
			best = c
	return best


# ------------------------------ muri e porte ------------------------------

func _build_walls(plan: Dictionary) -> void:
	var walls := {}
	var stair: Dictionary = plan["stair"]
	var open: Dictionary = plan["open"]
	for c: Vector2i in cover:
		var ra := room_of(plan, c)
		if ra < 0:
			continue
		for d: Vector2i in [Vector2i(1, 0), Vector2i(0, 1)]:
			var n: Vector2i = c + d
			var rb := room_of(plan, n)
			if rb < 0 or rb == ra:
				continue
			var key := edge_key(c, n)
			if open.has(key):
				continue
			var cl := clip(key)
			if cl.is_empty():
				continue
			var door_ok: bool = cl["s1"] - cl["s0"] >= DOOR_MIN and usable.has(c) and usable.has(n) \
					and not stair.has(c) and not stair.has(n)
			walls[key] = {"m": "int_wall", "a": ra, "b": rb, "door_ok": door_ok}
	if plan["enclosed"]:
		# Scala chiusa: muro anche verso i ritagli di pianta senza stanza (la scala
		# stretta non ha ringhiere, deve avere un muro su fianchi e fondo).
		for c: Vector2i in stair:
			for d in DIRS:
				var n: Vector2i = c + d
				var key := edge_key(c, n)
				if cover.has(n) and room_of(plan, n) < 0 and not walls.has(key) and not clip(key).is_empty():
					walls[key] = {"m": "int_wall", "a": plan["room"][c], "b": -1, "door_ok": false}
	plan["walls"] = walls


## Mette le porte: prima l'ingresso di ogni unita' dal disimpegno (condominio), poi
## un albero che collega le stanze fra loro, poi qualche porta in piu'.
## Ritorna le stanze rimaste irraggiungibili.
func _doors(plan: Dictionary, kind: int, rng: RandomNumberGenerator) -> Array:
	var rooms: Array = plan["rooms"]
	var walls: Dictionary = plan["walls"]
	var connected := {0: true}
	var paired := {}
	var condo := kind == Kind.CONDOMINIO
	if condo:
		var by_unit := {}
		for key: Vector3i in walls:
			var wall: Dictionary = walls[key]
			if not wall["door_ok"] or (wall["a"] == 0) == (wall["b"] == 0):
				continue
			var other: int = wall["b"] if wall["a"] == 0 else wall["a"]
			if rooms[other]["kind"] != "room":
				continue
			var unit: int = rooms[other]["unit"]
			if not by_unit.has(unit):
				by_unit[unit] = []
			by_unit[unit].append(key)
		for unit: int in by_unit:
			var list: Array = by_unit[unit]
			var wall: Dictionary = walls[list[rng.randi() % list.size()]]
			wall["m"] = "int_door"
			connected[wall["a"]] = true
			connected[wall["b"]] = true
			paired[Vector2i(mini(wall["a"], wall["b"]), maxi(wall["a"], wall["b"]))] = true
	while true:
		var front: Array = []
		for key: Vector3i in walls:
			var wall: Dictionary = walls[key]
			if not wall["door_ok"] or wall["m"] != "int_wall":
				continue
			if connected.has(wall["a"]) == connected.has(wall["b"]):
				continue
			if rooms[wall["a"]]["kind"] == "stair" or rooms[wall["b"]]["kind"] == "stair":
				continue
			if condo and rooms[wall["a"]]["unit"] != rooms[wall["b"]]["unit"]:
				continue
			front.append(key)
		if front.is_empty():
			break
		var wall: Dictionary = walls[front[rng.randi() % front.size()]]
		var touches_hall: bool = wall["a"] == 0 or wall["b"] == 0
		wall["m"] = "int_arch" if (kind == Kind.PADRONALE and touches_hall and rng.randf() < 0.3) else "int_door"
		connected[wall["a"]] = true
		connected[wall["b"]] = true
		paired[Vector2i(mini(wall["a"], wall["b"]), maxi(wall["a"], wall["b"]))] = true
	# Qualche passaggio in piu' fra stanze gia' collegate della stessa unita'.
	for key: Vector3i in walls:
		var wall: Dictionary = walls[key]
		if not wall["door_ok"] or wall["m"] != "int_wall":
			continue
		var pair := Vector2i(mini(wall["a"], wall["b"]), maxi(wall["a"], wall["b"]))
		if paired.has(pair) or not connected.has(wall["a"]) or not connected.has(wall["b"]):
			continue
		if rooms[wall["a"]]["kind"] == "stair" or rooms[wall["b"]]["kind"] == "stair":
			continue
		if rooms[wall["a"]]["unit"] != rooms[wall["b"]]["unit"]:
			continue
		if rng.randf() < 0.1:
			wall["m"] = "int_door"
			paired[pair] = true
	var left: Array = []
	for index in rooms.size():
		if rooms[index]["kind"] == "room" and not connected.has(index):
			left.append(index)
	return left


## Fonde una stanza senza porta con una vicina (meglio se della stessa unita').
func _merge_room(plan: Dictionary, index: int, kind: int) -> bool:
	var rooms: Array = plan["rooms"]
	if rooms[index]["kind"] != "room":
		return false
	var target := -1
	var target_score := -1
	for key: Vector3i in plan["walls"]:
		var wall: Dictionary = plan["walls"][key]
		if wall["a"] != index and wall["b"] != index:
			continue
		var other: int = wall["b"] if wall["a"] == index else wall["a"]
		if rooms[other]["kind"] == "stair" or rooms[other]["kind"] == "gone":
			continue
		var score := 1
		if rooms[other]["unit"] == rooms[index]["unit"]:
			score += 2
		if kind != Kind.CONDOMINIO and other == 0:
			score += 1
		if score > target_score:
			target_score = score
			target = other
	if target < 0:
		return false
	for c: Vector2i in rooms[index]["cells"]:
		plan["room"][c] = target
	rooms[target]["cells"].append_array(rooms[index]["cells"])
	rooms[index] = {"kind": "gone", "unit": -2, "cells": []}
	return true


## Estremi veri di ogni tramezzo, pilastrini e punti in cui tocca i muri esterni.
func _wall_geometry(plan: Dictionary) -> void:
	var walls: Dictionary = plan["walls"]
	var nodes := {}   # nodo di griglia -> [muri lungo v, muri lungo u]
	for key: Vector3i in walls:
		var wall: Dictionary = walls[key]
		var cl := clip(key)
		var side := key.z
		var from := key.y if side == 0 else key.x
		var s: Array[float] = [cl["s0"], cl["s1"]]
		var ext: Array[bool] = [cl["ext0"], cl["ext1"]]
		var span: Array = cl["span"]
		for end in 2:
			if ext[end]:
				continue
			# Oltre il nodo non c'e' una coppia di stanze (ritaglio di pianta senza celle):
			# il muro prosegue fino al muro esterno, senno' resterebbe una fessura.
			var step := 1 if end == 1 else -1
			var next := Vector3i(key.x, key.y + step, 0) if side == 0 else Vector3i(key.x + step, key.y, 1)
			var beyond := edge_cells(next)
			if room_of(plan, beyond[0]) >= 0 and room_of(plan, beyond[1]) >= 0:
				continue
			var limit: float = span[end] + (BURY / CELL if end == 0 else -BURY / CELL)
			if absf(limit - s[end]) < 1.0 and (limit - s[end]) * step > 0.001:
				s[end] = limit
				ext[end] = true
		wall["s0"] = s[0]
		wall["s1"] = s[1]
		for end in 2:
			if ext[end]:
				plan["hits"].append([edge_point(key, span[end]), key])
				continue
			var node := Vector2i(key.x, from + end) if side == 0 else Vector2i(from + end, key.y)
			if not nodes.has(node):
				nodes[node] = [0, 0]
			nodes[node][side] += 1
	for node: Vector2i in nodes:
		var count: Array = nodes[node]
		if (count[0] == 2 and count[1] == 0) or (count[1] == 2 and count[0] == 0):
			continue
		var p := at(node.x, node.y)
		if interior(p, WALL + 0.12):
			plan["posts"].append(p)


## Pianta in caratteri, per il collaudo: `#` scala, `.` disimpegno, lettere = stanze
## (maiuscole/minuscole alternate per unita').
func ascii(plan: Dictionary) -> String:
	var hi := Vector2i.ZERO
	for c: Vector2i in cover:
		hi = Vector2i(maxi(hi.x, c.x), maxi(hi.y, c.y))
	var doors := 0
	for key: Vector3i in plan["walls"]:
		if plan["walls"][key]["m"] != "int_wall":
			doors += 1
	var out := ""
	for j in range(hi.y, -1, -1):
		var line := ""
		for i in hi.x + 1:
			var c := Vector2i(i, j)
			var room := room_of(plan, c)
			if plan["stair"].has(c):
				line += "#"
			elif room < 0:
				line += " "
			elif room == 0:
				line += "." if usable.has(c) else ","
			else:
				var letter := char(65 + room % 26)
				line += letter if plan["rooms"][room]["unit"] % 2 == 0 else letter.to_lower()
		out += line + "\n"
	return out + "stanze %d, porte %d, fuse %d, isolate %d\n" % [plan["rooms"].size(), doors, plan["merged"], plan["isolated"]]
