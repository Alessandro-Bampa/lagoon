@tool
extends RefCounted

## Geometria 2D sulle piante degli edifici (piano x, z del mondo).
##
## Convenzione degli anelli: l'esterno ha area positiva, i buchi (cortili) negativa.
## Con questa orientazione la normale "verso fuori dal pieno" di un lato a -> b e'
## sempre (d.y, -d.x), sia sull'anello esterno sia sui buchi.

const EPS := 1e-7


## Area con segno. I punti si riportano al primo vertice: le coordinate mondo
## sono dell'ordine di 15 km e Vector2 e' a 32 bit, la formula di Gauss fatta
## sui valori assoluti perderebbe tutte le cifre utili.
static func area(ring: PackedVector2Array) -> float:
	if ring.is_empty():
		return 0.0
	var base := ring[0]
	var s := 0.0
	for i in ring.size():
		s += (ring[i] - base).cross(ring[(i + 1) % ring.size()] - base)
	return s * 0.5


static func centroid(ring: PackedVector2Array) -> Vector2:
	if ring.is_empty():
		return Vector2.ZERO
	var base := ring[0]
	var c := Vector2.ZERO
	var a := 0.0
	var mean := Vector2.ZERO
	for i in ring.size():
		var p := ring[i] - base
		var q := ring[(i + 1) % ring.size()] - base
		var w := p.cross(q)
		c += (p + q) * w
		a += w
		mean += p
	if absf(a) < EPS:
		return base + mean / ring.size()
	return base + c / (3.0 * a)


## Anello dal JSON ([[x, z], ...]); toglie il vertice di chiusura ripetuto.
static func ring_from_json(points: Array) -> PackedVector2Array:
	var ring := PackedVector2Array()
	for p in points:
		ring.append(Vector2(p[0], p[1]))
	if ring.size() > 1 and ring[0].is_equal_approx(ring[ring.size() - 1]):
		ring.remove_at(ring.size() - 1)
	return ring


## Orienta gli anelli: esterno (indice 0) positivo, buchi negativi.
static func orient(rings: Array) -> Array:
	for k in rings.size():
		var ring: PackedVector2Array = rings[k]
		if (area(ring) > 0.0) != (k == 0):
			ring.reverse()
			rings[k] = ring
	return rings


## Toglie i vertici che deviano meno di `tolerance` dalla retta fra i vicini e
## quelli doppi: le piante OSM seguono le curve con tanti lati cortissimi, che
## un kit a moduli da 2 m non sa rendere.
static func simplify(ring: PackedVector2Array, tolerance: float) -> PackedVector2Array:
	var pts := ring.duplicate()
	var changed := true
	while changed and pts.size() > 3:
		changed = false
		var best := -1
		var best_dist := tolerance
		for i in pts.size():
			var a := pts[(i + pts.size() - 1) % pts.size()]
			var b := pts[i]
			var c := pts[(i + 1) % pts.size()]
			var chord := c - a
			var d := a.distance_to(b) if chord.length() < 1e-6 else absf(chord.cross(b - a)) / chord.length()
			if d < best_dist:
				best_dist = d
				best = i
		if best >= 0:
			pts.remove_at(best)
			changed = true
	return pts


static func inside_ring(p: Vector2, ring: PackedVector2Array) -> bool:
	return Geometry2D.is_point_in_polygon(p, ring)


## Dentro il pieno: dentro l'esterno e fuori da ogni buco.
static func inside(p: Vector2, rings: Array) -> bool:
	if not Geometry2D.is_point_in_polygon(p, rings[0]):
		return false
	for k in range(1, rings.size()):
		if Geometry2D.is_point_in_polygon(p, rings[k]):
			return false
	return true


## Distanza di p dal bordo piu' vicino (qualunque anello).
static func boundary_distance(p: Vector2, rings: Array) -> float:
	var best := INF
	for ring: PackedVector2Array in rings:
		for i in ring.size():
			var q := Geometry2D.get_closest_point_to_segment(p, ring[i], ring[(i + 1) % ring.size()])
			best = minf(best, p.distance_to(q))
	return best


## Tratti interni al pieno della retta origin + t * dir: coppie [t0, t1] ordinate.
static func line_spans(origin: Vector2, dir: Vector2, rings: Array) -> Array:
	var hits: Array[float] = []
	var normal := Vector2(-dir.y, dir.x)
	for ring: PackedVector2Array in rings:
		for i in ring.size():
			var a := ring[i] - origin
			var b := ring[(i + 1) % ring.size()] - origin
			var da := a.dot(normal)
			var db := b.dot(normal)
			if (da <= 0.0) == (db <= 0.0):
				continue
			var k := da / (da - db)
			hits.append(a.lerp(b, k).dot(dir))
	hits.sort()
	var spans: Array = []
	for i in range(0, hits.size() - 1, 2):
		spans.append([hits[i], hits[i + 1]])
	return spans


## Assegna ogni buco all'esterno piu' piccolo che lo contiene.
## Ritorna una lista di gruppi [esterno, buco, buco, ...] orientati.
static func group(outers: Array, holes: Array) -> Array:
	var groups: Array = []
	for o: PackedVector2Array in outers:
		if o.size() >= 3:
			groups.append([o])
	for h: PackedVector2Array in holes:
		if h.size() < 3:
			continue
		var best := -1
		var best_area := INF
		for g in groups.size():
			var o: PackedVector2Array = groups[g][0]
			var a := absf(area(o))
			if a < best_area and Geometry2D.is_point_in_polygon(h[0], o):
				best_area = a
				best = g
		if best >= 0:
			groups[best].append(h)
	for g in groups:
		orient(g)
	return groups


## Restringe il pieno di `d` metri. Ritorna gruppi [esterno, buchi...]; vuoto se
## il pieno e' piu' stretto di 2 d.
static func inset(rings: Array, d: float) -> Array:
	var solids: Array = []
	for p in Geometry2D.offset_polygon(rings[0], -d, Geometry2D.JOIN_MITER):
		if not Geometry2D.is_polygon_clockwise(p):
			solids.append(p)
	# I cortili si allargano e si sottraggono.
	for k in range(1, rings.size()):
		var hole: PackedVector2Array = rings[k].duplicate()
		hole.reverse()
		for grown in Geometry2D.offset_polygon(hole, d, Geometry2D.JOIN_MITER):
			if Geometry2D.is_polygon_clockwise(grown):
				continue
			var next: Array = []
			for s in solids:
				next.append_array(Geometry2D.clip_polygons(s, grown))
			solids = next
	var outers: Array = []
	var holes: Array = []
	for s: PackedVector2Array in solids:
		if absf(area(s)) < 0.5:
			continue
		if Geometry2D.is_polygon_clockwise(s):
			holes.append(s)
		else:
			outers.append(s)
	return group(outers, holes)


# ------------------------------ triangolazione ------------------------------

## Triangoli (terne di punti) di un pieno con buchi.
static func triangulate(rings: Array) -> PackedVector2Array:
	var poly: PackedVector2Array = rings[0]
	if rings.size() == 1:
		var indices := Geometry2D.triangulate_polygon(poly)
		if not indices.is_empty():
			var out := PackedVector2Array()
			for i in indices:
				out.append(poly[i])
			return out
	else:
		# Geometry2D non triangola i poligoni con buchi: si ricuce ogni buco
		# all'esterno con un "ponte" e si ritaglia a orecchie l'anello unico.
		poly = _merge_holes(poly, rings.slice(1))
	return _ear_clip(poly)


## Fonde i buchi nell'anello esterno (metodo di Eberly).
static func _merge_holes(outer: PackedVector2Array, holes: Array) -> PackedVector2Array:
	var poly := outer.duplicate()
	var order := holes.duplicate()
	order.sort_custom(func(a: PackedVector2Array, b: PackedVector2Array) -> bool: return _max_x(a) > _max_x(b))
	for hole: PackedVector2Array in order:
		var mi := 0
		for i in hole.size():
			if hole[i].x > hole[mi].x:
				mi = i
		var m := hole[mi]
		var hit_x := INF
		var pi := -1
		for i in poly.size():
			var a := poly[i]
			var b := poly[(i + 1) % poly.size()]
			if (a.y <= m.y) == (b.y <= m.y):
				continue
			var x := a.x + (m.y - a.y) * (b.x - a.x) / (b.y - a.y)
			if x >= m.x - 1e-9 and x < hit_x:
				hit_x = x
				pi = i if a.x > b.x else (i + 1) % poly.size()
		if pi < 0:
			continue  # buco fuori dall'esterno: dato rotto, si ignora
		var hit := Vector2(hit_x, m.y)
		var corner := poly[pi]
		var best_tan := INF
		for i in poly.size():
			var r := poly[i]
			if r.x <= m.x or r == corner or not _in_triangle(r, m, hit, corner):
				continue
			var tangent := absf(r.y - m.y) / (r.x - m.x)
			if tangent < best_tan and _locally_inside(poly, i, m):
				best_tan = tangent
				pi = i
		var merged := PackedVector2Array()
		for i in range(0, pi + 1):
			merged.append(poly[i])
		for k in hole.size() + 1:
			merged.append(hole[(mi + k) % hole.size()])
		merged.append(poly[pi])
		for i in range(pi + 1, poly.size()):
			merged.append(poly[i])
		poly = merged
	return poly


static func _ear_clip(poly: PackedVector2Array) -> PackedVector2Array:
	var out := PackedVector2Array()
	var idx: Array[int] = []
	for i in poly.size():
		idx.append(i)
	var start := 0
	while idx.size() > 3:
		var n := idx.size()
		var clipped := false
		for step in n:
			var k := (start + step) % n
			var a := poly[idx[(k + n - 1) % n]]
			var b := poly[idx[k]]
			var c := poly[idx[(k + 1) % n]]
			if (b - a).cross(c - b) <= EPS:
				continue
			var blocked := false
			for j in n:
				var p := poly[idx[j]]
				if p == a or p == b or p == c:
					continue
				if (b - a).cross(p - a) > EPS and (c - b).cross(p - b) > EPS and (a - c).cross(p - c) > -EPS:
					blocked = true
					break
			if not blocked:
				out.append(a); out.append(b); out.append(c)
				idx.remove_at(k)
				start = maxi(k - 1, 0)
				clipped = true
				break
		if not clipped:
			var flattest := 0
			var flat_value := INF
			for k in n:
				var v := absf((poly[idx[k]] - poly[idx[(k + n - 1) % n]]).cross(poly[idx[(k + 1) % n]] - poly[idx[k]]))
				if v < flat_value:
					flat_value = v
					flattest = k
			idx.remove_at(flattest)
			start = 0
	if idx.size() == 3:
		out.append(poly[idx[0]]); out.append(poly[idx[1]]); out.append(poly[idx[2]])
	return out


static func _in_triangle(p: Vector2, a: Vector2, b: Vector2, c: Vector2) -> bool:
	var d1 := (b - a).cross(p - a)
	var d2 := (c - b).cross(p - b)
	var d3 := (a - c).cross(p - c)
	return not ((d1 < 0.0 or d2 < 0.0 or d3 < 0.0) and (d1 > 0.0 or d2 > 0.0 or d3 > 0.0))


static func _locally_inside(poly: PackedVector2Array, i: int, m: Vector2) -> bool:
	var n := poly.size()
	var a := poly[(i + n - 1) % n]
	var b := poly[i]
	var c := poly[(i + 1) % n]
	var left_in := (b - a).cross(m - a) > 0.0
	var left_out := (c - b).cross(m - b) > 0.0
	return (left_in and left_out) if (b - a).cross(c - b) >= 0.0 else (left_in or left_out)


static func _max_x(ring: PackedVector2Array) -> float:
	var best := -INF
	for p in ring:
		best = maxf(best, p.x)
	return best
