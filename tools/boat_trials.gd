extends SceneTree
## Prove in mare delle barche (skill vehicles-boats §13): collaudo headless in TEMPO SIMULATO.
##
##   Godot --path . --headless --fixed-fps 60 --script tools/boat_trials.gd -- <barca|all> [prove]
##
## barca: un nome di PRESETS (barchino_tiller, barchino_console, gozzo, peschereccio), "all", oppure il
## percorso di una scena di barca (res://vehicles/scenes/boats/X.tscn) o di una BoatConfig
## (res://resources/boats/x.tres): una barca nuova si prova senza toccare questo file (bersagli
## assenti = solo misure, PASS se i numeri sono finiti).
## prove (separate da virgola, default tutte): self, statics, drop, roll, heel, bollard, speed, trim,
## turn, stop, reverse, kick, thruster, waves.
## Con --fixed-fps ogni frame avanza di 1/60 s a prescindere dal tempo reale: le prove girano alla
## velocita' della CPU e sono riproducibili. Ogni riga [trial] riporta misure e bersagli (TARGETS).

const PRESETS := {
	"barchino_tiller": "res://vehicles/scenes/boats/BarchinoTiller.tscn",
	"barchino_console": "res://vehicles/scenes/boats/BarchinoConsole.tscn",
	"gozzo": "res://vehicles/scenes/boats/Gozzo.tscn",
	"peschereccio": "res://vehicles/scenes/boats/Peschereccio.tscn",
}

## Valori di prova per una barca senza voce in TARGETS (massa del peso a bordo e sua distanza
## dall'asse, quota del ponte nello spazio della barca, tempi di assestamento e di corsa).
const DEFAULTS := {"deck_y": 0.2, "weight": 90.0, "arm": 0.7, "settle": 20.0, "run": 60.0}

## Bersagli per classe (specifiche §14; il barchino e' uno skiff da 4,5 m).
const TARGETS := {
	"barchino_tiller": {"roll_period": [1.0, 1.6], "speed_kn": [18.0, 23.0], "bollard_kgf": [130.0, 260.0], "plane_s": [2.0, 6.0], "plane_trim": [3.0, 6.0], "deck_y": -0.05, "weight": 90.0, "arm": 0.7, "settle": 10.0, "run": 40.0},
	"barchino_console": {"roll_period": [1.0, 1.6], "speed_kn": [22.0, 27.0], "bollard_kgf": [230.0, 360.0], "plane_s": [2.0, 5.0], "plane_trim": [3.0, 6.0], "deck_y": -0.05, "weight": 90.0, "arm": 0.7, "settle": 10.0, "run": 40.0},
	"gozzo": {"roll_period": [2.3, 3.0], "speed_kn": [6.5, 7.8], "bollard_kgf": [350.0, 400.0], "deck_y": 0.22, "weight": 90.0, "arm": 0.9, "settle": 15.0, "run": 60.0},
	"peschereccio": {"roll_period": [6.0, 9.0], "speed_kn": [10.0, 11.0], "bollard_kgf": [6700.0, 8000.0], "deck_y": 1.4, "weight": 3000.0, "arm": 2.0, "settle": 40.0, "run": 150.0},
}

var level: Node3D
var water: Node
var boat: RigidBody3D
var preset := ""
var failures := 0
var settled := false
var rest_y := 0.0
var rest_pitch := 0.0


func _init() -> void:
	_run.call_deferred()


func _run() -> void:
	var args := OS.get_cmdline_user_args()
	var which: String = args[0] if args.size() > 0 else "all"
	var tests: PackedStringArray = (args[1] if args.size() > 1 else "self,statics,drop,roll,heel,bollard,speed,trim,turn,stop,reverse,kick,thruster,waves").split(",")
	if "self" in tests:
		for line in load("res://vehicles/scripts/Hydro/HydroSelfTest.cs").new().Run():
			print("[self] ", line)
			if line.begins_with("FALLITO"):
				failures += 1
	var names: Array = PRESETS.keys() if which == "all" else [which]
	# Un percorso diretto: il "preset" e' il percorso stesso (bersagli assenti, DEFAULTS).
	for p in names:
		preset = p
		for t in tests:
			if t == "self":
				continue
			await call("trial_" + t)
	print("[trial] fine, %d fallimenti" % failures)
	quit(1 if failures > 0 else 0)


# =====================================================================================
#  Infrastruttura
# =====================================================================================

func steps(n: int) -> void:
	for i in n:
		await physics_frame


func seconds(s: float) -> int:
	return int(round(s * 60.0))


func setup(sea := 0.0) -> void:
	level = Node3D.new()
	root.add_child(level)
	water = load("res://water/scenes/Water.tscn").instantiate()
	water.set("ExtentXZ", Vector2(8000, 8000))
	# Acqua FERMA per le prove statiche (anche il mare calmo aperto ha onda lunga).
	water.set("DefaultExposure", 0.0 if sea <= 0.0 else 1.0)
	water.set("TideEnabled", false)
	water.set("InitialSeaState", sea)
	water.set("WindSpeedOverride", 0.0 if sea <= 0.0 else -1.0)
	level.add_child(water)
	var seabed := StaticBody3D.new()
	var shape := CollisionShape3D.new()
	var box := BoxShape3D.new()
	box.size = Vector3(8000, 1, 8000)
	shape.shape = box
	seabed.add_child(shape)
	seabed.position = Vector3(0, -60, 0)
	level.add_child(seabed)
	var path: String = PRESETS.get(preset, preset)
	if path.ends_with(".tres"):
		boat = load("res://vehicles/scenes/Boat.tscn").instantiate()
		boat.get_node("Assembly").set("Config", load(path))
	else:
		boat = load(path).instantiate()
	boat.set("VehicleId", 99)
	level.add_child(boat)
	await steps(2)
	settled = false
	place(0.0, 0.0)
	await steps(seconds(target("settle")))
	# Galleggiamento assestato in acqua ferma: da qui ripartono le prove (place).
	rest_y = boat.global_position.y
	rest_pitch = pitch()
	settled = true


func teardown() -> void:
	level.queue_free()
	await steps(3)


func tel() -> Dictionary:
	return boat.call("DebugTelemetry")


func target(key: String):
	return TARGETS.get(preset, {}).get(key, DEFAULTS.get(key))


## Tipo di propulsione della barca in prova ("outboard", "inboard", "") e se ha l'elica di prua.
func drive_kind() -> String:
	return tel().get("drive", "")


## Rimette la barca dritta al galleggiamento statico, ferma, con sbandamento e prua dati (gradi).
func place(heel_deg: float, heading_deg: float, lift := 0.0) -> void:
	var heave: float = rest_y if settled else float(tel()["static_heave"])
	var trim := deg_to_rad(rest_pitch) if settled else 0.0
	var basis := Basis(Vector3.UP, -deg_to_rad(heading_deg)) * Basis.from_euler(Vector3(trim, 0, -deg_to_rad(heel_deg)))
	boat.global_transform = Transform3D(basis, Vector3(boat.global_position.x, heave + lift, boat.global_position.z))
	boat.linear_velocity = Vector3.ZERO
	boat.angular_velocity = Vector3.ZERO


func heel() -> float:
	return rad_to_deg(asin(clamp(-boat.global_basis.x.y, -1.0, 1.0)))


func pitch() -> float:
	return rad_to_deg(asin(clamp(-boat.global_basis.z.y, -1.0, 1.0)))


## Prua in gradi bussola (0 = nord = −Z, orario).
func heading() -> float:
	var f := -boat.global_basis.z
	return fposmod(rad_to_deg(atan2(f.x, -f.z)), 360.0)


func knots() -> float:
	return tel().get("speed_kn", 0.0)


func drive(throttle: float, steer := 0.0, thruster := 0.0) -> void:
	boat.set("DebugAutoDrive", true)
	boat.set("DebugThrottle", throttle)
	boat.set("DebugSteer", steer)
	boat.set("DebugThruster", thruster)


func stop_driving() -> void:
	boat.set("DebugAutoDrive", false)


func report(name: String, ok: bool, text: String) -> void:
	print("[trial] %-16s %-12s %s %s" % [preset, name, "PASS" if ok else "FAIL", text])
	if not ok:
		failures += 1


func in_range(v: float, r) -> bool:
	return r == null or (v >= r[0] and v <= r[1])


func check_numeric(name: String) -> bool:
	var t := tel()
	var bad: int = t.get("invalid_steps", 0)
	var clamped: int = t.get("clamped_steps", 0)
	if bad > 0 or clamped > 0:
		report(name + "/num", false, "passi non finiti %d, limitati %d" % [bad, clamped])
		return false
	return true


# =====================================================================================
#  Prove
# =====================================================================================

func trial_statics() -> void:
	await setup()
	var t := tel()
	var m: float = t["mass"]
	var inertia: Vector3 = t["inertia"]
	var length: float = t["hull_length"]
	var beam: float = t["hull_beam"]
	var gz2: float = boat.call("DebugRightingArm", 2.0)
	var gm: float = gz2 / sin(deg_to_rad(2.0))
	var k_roll: float = sqrt(inertia.z / m) / beam
	var k_pitch: float = sqrt(inertia.x / m) / length
	var draft: float = -(float(t["static_heave"]) + float(t["hull_bottom"]))
	var gz := PackedStringArray()
	for a in [10, 20, 30, 40, 50, 60, 70, 80]:
		gz.append("%d°:%.2f" % [a, boat.call("DebugRightingArm", float(a))])
	print("[trial] %-16s statics     %.3f ms/passo, LCB statico %+.2f, massa %.0f kg, G %s, triangoli %d, volume scafo %.2f m³, pescaggio %.2f m, GM %.2f m, k_rollio %.2f·B, k_beccheggio %.2f·L" % [
		preset, t["step_ms"], t["lcb_static"], m, t["com"], t["hull_triangles"], t["hull_volume"], draft, gm, k_roll, k_pitch])
	print("[trial] %-16s GZ (m)      %s" % [preset, " ".join(gz)])
	for line in String(boat.get_node("Assembly").call("DebugHydrostatics")).split("\n"):
		print("[trial] %-16s anteprima   %s" % [preset, line])
	t = tel()
	var vol_err: float = abs(float(t["volume"]) - m / 1025.0) / (m / 1025.0)
	report("galleggiamento", vol_err < 0.01 and abs(heel()) < 0.3 and abs(rest_pitch) < 2.0,
		"volume immerso %.3f m³ (m/ρ %.3f), quota %.3f (statica a assetto 0: %.3f), assetto %+.2f°, sbandamento %+.2f°" % [
		t["volume"], m / 1025.0, rest_y, t["static_heave"], rest_pitch, heel()])
	await teardown()


func trial_drop() -> void:
	await setup()
	var rest := rest_y
	place(0.0, 0.0, 1.5)
	var settle_window: float = target("settle") * 2.0
	var n := seconds(settle_window)
	var ys := PackedFloat32Array()
	for i in n:
		await physics_frame
		ys.append(boat.global_position.y)
	# Primo impatto = primo minimo; poi massimo rimbalzo.
	var first_min := 0
	for i in range(1, n - 1):
		if ys[i] < ys[i - 1] and ys[i] <= ys[i + 1]:
			first_min = i
			break
	var rebound := -1e9
	for i in range(first_min, n):
		rebound = max(rebound, ys[i])
	var final := ys[n - 1]
	var settle := 0.0
	for i in n:
		if abs(ys[i] - final) > 0.01:
			settle = float(i) / 60.0
	var hull_jump: float = rebound - rest
	var ok: bool = check_numeric("drop") and settle < settle_window * 0.9 and abs(final - rest) < 0.03
	report("caduta", ok, "rimbalzo %.2f m sopra il galleggiamento, assestamento ±1 cm in %.1f s, quota finale %.3f (a riposo %.3f)" % [hull_jump, settle, final, rest])
	await teardown()


func trial_roll() -> void:
	await setup()
	place(10.0, 0.0)
	var n := seconds(max(12.0, float(target("settle")) * 0.8))
	var h := PackedFloat32Array()
	for i in n:
		await physics_frame
		h.append(heel())
	var crossings := []
	for i in range(1, n):
		if (h[i - 1] > 0.0) != (h[i] > 0.0):
			crossings.append(float(i) / 60.0)
	var peaks := []
	for i in range(1, n - 1):
		if abs(h[i]) >= abs(h[i - 1]) and abs(h[i]) > abs(h[i + 1]) and abs(h[i]) > 0.2:
			peaks.append(abs(h[i]))
	var period := 0.0
	if crossings.size() >= 3:
		period = 2.0 * (crossings[crossings.size() - 1] - crossings[0]) / float(crossings.size() - 1)
	var zeta := 0.0
	if peaks.size() >= 2:
		var delta: float = log(peaks[0] / peaks[1])
		zeta = delta / sqrt(4.0 * PI * PI + delta * delta)
	var ok: bool = check_numeric("roll") and in_range(period, target("roll_period"))
	report("rollio", ok, "periodo %.2f s (bersaglio %s), ζ %.3f, picchi %s" % [period, target("roll_period"), zeta, peaks.slice(0, 4)])
	await teardown()


func trial_heel() -> void:
	await setup()
	var w: float = target("weight")
	var d: float = target("arm")
	boat.call("DebugSetMass", -1, w, Vector3(d, target("deck_y"), 0.0))
	await steps(seconds(target("settle")))
	var sum := 0.0
	for i in 120:
		await physics_frame
		sum += heel()
	var measured: float = sum / 120.0
	var t := tel()
	var m: float = t["mass"]
	# Equilibrio dall'idrostatica (stessa del runtime): GZ(φ) = 0 col baricentro spostato di lato.
	var lo := 0.0
	var hi := 45.0
	for i in 30:
		var mid := 0.5 * (lo + hi)
		if float(boat.call("DebugRightingArm", mid)) < 0.0:
			lo = mid
		else:
			hi = mid
	var analytic: float = 0.5 * (lo + hi)
	var gm := 0.0
	var ok: bool = check_numeric("heel") and abs(measured - analytic) <= max(0.15 * abs(analytic), 0.3)
	report("sbandamento", ok, "%.0f kg a %.1f m: %.2f° (idrostatica %.2f°, massa %.0f kg)" % [w, d, measured, analytic, m])
	await teardown()


func trial_bollard() -> void:
	await setup()
	boat.axis_lock_linear_x = true
	boat.axis_lock_linear_z = true
	boat.axis_lock_angular_y = true
	drive(1.0)
	await steps(seconds(20))
	var sum := 0.0
	var rpm := 0.0
	for i in 300:
		await physics_frame
		var t := tel()
		sum += t.get("thrust_net", 0.0)
		rpm += t.get("rpm", 0.0)
	var kgf: float = sum / 300.0 / 9.81
	var ok: bool = check_numeric("bollard") and in_range(kgf, target("bollard_kgf"))
	report("punto fisso", ok, "%.0f kgf (bersaglio %s) a %.0f giri/min" % [kgf, target("bollard_kgf"), rpm / 300.0])
	await teardown()


## Corsa in linea retta da fermo: velocita' massima, tempo per la planata, assetto, gobba.
func run_speed(label: String, trim_deg = null) -> Dictionary:
	await setup()
	if trim_deg != null:
		boat.call("DebugSetTrimDegrees", float(trim_deg))
	drive(1.0)
	var run: float = target("run")
	var n := seconds(run)
	var plane_t := -1.0
	var max_pitch := -90.0
	var samples := PackedStringArray()
	var last_speed := 0.0
	var min_accel := 1e9
	var hump_speed := 0.0
	var pitch_sum := 0.0
	var speed_sum := 0.0
	var rpm_sum := 0.0
	var slip_sum := 0.0
	var tail := seconds(5)
	for i in n:
		await physics_frame
		var t := tel()
		var kn: float = t.get("speed_kn", 0.0)
		var fnv: float = t.get("fnv", 0.0)
		max_pitch = max(max_pitch, pitch())
		if plane_t < 0.0 and fnv > 2.2:
			plane_t = float(i) / 60.0
		if i % 30 == 0:
			var accel := (kn - last_speed) * 0.514444 * 2.0
			if i > 60 and kn < 0.9 * speed_sum / max(1, i) + 1e9 and fnv > 0.8 and fnv < 2.2 and accel < min_accel:
				min_accel = accel
				hump_speed = kn
			last_speed = kn
			if i % 120 == 0:
				samples.append("%ds:%.1fkn/%.1f°/%.0f" % [i / 60, kn, pitch(), t.get("rpm", 0.0)])
		if i >= n - tail:
			pitch_sum += pitch()
			speed_sum += kn
			rpm_sum += t.get("rpm", 0.0)
			slip_sum += t.get("slip", 0.0)
	var res := {
		"speed": speed_sum / tail, "pitch": pitch_sum / tail, "rpm": rpm_sum / tail, "slip": slip_sum / tail,
		"plane_t": plane_t, "max_pitch": max_pitch, "hump_accel": min_accel, "hump_speed": hump_speed,
		"trim": tel().get("trim_deg", 0.0), "vent": tel().get("ventilating", false),
	}
	print("[trial] %-16s %-12s %s" % [preset, label, " ".join(samples)])
	check_numeric(label)
	await teardown()
	return res


func trial_speed() -> void:
	var r: Dictionary = await run_speed("speed")
	var planing: bool = target("plane_s") != null
	var ok: bool = in_range(r["speed"], target("speed_kn"))
	if planing:
		ok = ok and in_range(r["plane_t"], target("plane_s")) and in_range(r["pitch"], target("plane_trim"))
	report("velocita'", ok, "%.1f kn (bersaglio %s), assetto %.1f° (max %.1f° nella gobba), planata a %.1f s, %.0f giri/min, regresso %.0f%%, trim %.1f°, accelerazione minima %.2f m/s² a %.1f kn" % [
		r["speed"], target("speed_kn"), r["pitch"], r["max_pitch"], r["plane_t"], r["rpm"], r["slip"] * 100.0, r["trim"], r["hump_accel"], r["hump_speed"]])


## Trim ottimo intermedio (C5): velocita' massima a trim fisso.
func trial_trim() -> void:
	await setup()
	var kind := drive_kind()
	await teardown()
	if kind != "outboard":
		return
	var line := PackedStringArray()
	var best := -1e9
	var best_trim := 0.0
	var first := 0.0
	var last := 0.0
	for trim in [-6.0, -2.0, 2.0, 6.0, 10.0, 14.0]:
		var r: Dictionary = await run_speed("trim %+.0f" % trim, trim)
		line.append("%+.0f°: %.1f kn/%.1f°%s" % [trim, r["speed"], r["pitch"], " V" if r["vent"] else ""])
		if r["speed"] > best:
			best = r["speed"]
			best_trim = trim
		if trim == -6.0:
			first = r["speed"]
		last = r["speed"]
	var ok: bool = best_trim > -6.0 and best_trim < 14.0 and first < best
	report("trim", ok, "%s → ottimo a %+.0f°" % [", ".join(line), best_trim])


func trial_turn() -> void:
	await setup()
	drive(1.0)
	await steps(seconds(target("run") * 0.6))
	var v0 := knots()
	var start_heading := heading()
	var start_pos := boat.global_position
	var right := boat.global_basis.x
	drive(1.0, 1.0)
	var n := seconds(max(25.0, float(target("run")) * 0.4))
	var diameter := -1.0
	var heel_sum := 0.0
	var drift_sum := 0.0
	var speed_sum := 0.0
	var count := 0
	var turned := 0.0
	var last_h := start_heading
	var initial_heel := 0.0
	var vent := 0
	for i in n:
		await physics_frame
		var h := heading()
		turned += wrapf(h - last_h, -180.0, 180.0)
		last_h = h
		if i == seconds(1.0):
			initial_heel = heel()
		if diameter < 0.0 and abs(turned) >= 180.0:
			diameter = abs((boat.global_position - start_pos).dot(right))
		if abs(turned) >= 90.0:
			heel_sum += heel()
			var v := boat.linear_velocity
			var f := -boat.global_basis.z
			drift_sum += rad_to_deg(Vector2(f.x, f.z).angle_to(Vector2(v.x, v.z)))
			speed_sum += knots()
			count += 1
			if tel().get("ventilating", false):
				vent += 1
	var length: float = tel()["hull_length"]
	var c: float = max(count, 1)
	var steady_heel: float = heel_sum / c
	var loss: float = 1.0 - (speed_sum / c) / max(v0, 0.01)
	var ok: bool = check_numeric("turn") and diameter > 0.0 and turned > 0.0
	report("virata", ok, "a dritta da %.1f kn: diametro %.1f m = %.1f L, sbandamento a regime %+.1f° (%s), iniziale %+.1f°, deriva %.1f°, perdita di velocita' %.0f%%, ventilazione %d%% del tempo" % [
		v0, diameter, diameter / length, steady_heel, "verso l'interno" if steady_heel > 0.0 else "verso l'esterno", initial_heel,
		abs(drift_sum / c), loss * 100.0, int(100.0 * vent / c)])
	await teardown()


func trial_stop() -> void:
	await setup()
	drive(1.0)
	await steps(seconds(target("run") * 0.6))
	var v0 := knots()
	var p0 := boat.global_position
	var h0 := heading()
	var fwd := -boat.global_basis.z
	drive(-1.0)
	var n := seconds(120.0)
	var t_stop := -1.0
	var dist := 0.0
	var lateral := 0.0
	for i in n:
		await physics_frame
		if boat.linear_velocity.dot(fwd) <= 0.0:
			t_stop = float(i) / 60.0
			dist = (boat.global_position - p0).dot(fwd)
			lateral = (boat.global_position - p0).dot(boat.global_basis.x)
			break
	var length: float = tel()["hull_length"]
	report("arresto", check_numeric("stop") and t_stop > 0.0,
		"da %.1f kn: fermo in %.1f s e %.0f m (%.1f L), deviazione laterale %+.1f m, prua %+.0f°" % [v0, t_stop, dist, dist / length, lateral, wrapf(heading() - h0, -180.0, 180.0)])
	await teardown()


func trial_reverse() -> void:
	await setup()
	var h0 := heading()
	drive(-1.0)
	await steps(seconds(20.0))
	var turn: float = wrapf(heading() - h0, -180.0, 180.0)
	var t := tel()
	# Elica destrorsa: in retromarcia poppa a sinistra = prua a dritta (turn > 0). Vale per l'entrobordo;
	# il fuoribordo ha un effetto evolutivo lieve e in retromarcia e' instabile di rotta (lo specchio
	# guida): la' il verso dipende da piccole asimmetrie, conta solo che si governi (informativa).
	var outboard := drive_kind() == "outboard"
	var ok: bool = check_numeric("reverse") and (outboard or turn > 0.0)
	report("retromarcia", ok, "20 s indietro: prua %+.1f° (atteso > 0, effetto evolutivo), %.1f kn" % [turn, t.get("speed_kn", 0.0)])
	await teardown()


## Colpo d'elica: timone tutto a dritta e motore avanti da fermi (solo entrobordo).
func trial_kick() -> void:
	await setup()
	if drive_kind() != "inboard":
		await teardown()
		return
	var h0 := heading()
	var p0 := boat.global_position
	drive(1.0, 1.0)
	await steps(seconds(max(4.0, float(target("settle")) * 0.2)))
	var turn: float = wrapf(heading() - h0, -180.0, 180.0)
	var moved := (boat.global_position - p0).length()
	drive(0.0, 1.0)
	# Senza flusso il timone non fa nulla: in folle e fermi la prua non deve girare.
	await setup_still()
	var h1 := heading()
	await steps(seconds(4.0))
	var idle_turn: float = wrapf(heading() - h1, -180.0, 180.0)
	var ok: bool = check_numeric("kick") and turn > 3.0 and abs(idle_turn) < 1.0
	report("colpo d'elica", ok, "prua %+.1f° spostandosi di %.1f m; in folle e fermi col timone a dritta %+.2f°" % [turn, moved, idle_turn])
	await teardown()


func setup_still() -> void:
	drive(0.0, 1.0)
	place(0.0, heading())
	await steps(seconds(2.0))
	place(0.0, heading())


func trial_thruster() -> void:
	await setup()
	if not tel().get("thruster", false):
		await teardown()
		return
	var h0 := heading()
	drive(0.0, 0.0, 1.0)
	await steps(seconds(20.0))
	var still: float = wrapf(heading() - h0, -180.0, 180.0)
	drive(0.0, 0.0, 0.0)
	await teardown()
	await setup()
	drive(0.5)
	await steps(seconds(60.0))
	var v := knots()
	var h1 := heading()
	drive(0.5, 0.0, 1.0)
	await steps(seconds(20.0))
	var moving: float = wrapf(heading() - h1, -180.0, 180.0)
	report("elica di prua", check_numeric("thruster") and still > 2.0 and abs(moving) < still,
		"20 s da fermi: prua %+.1f°; a %.1f kn: %+.1f°" % [still, v, moving])
	await teardown()


func trial_waves() -> void:
	for sea in [3.0, 4.0]:
		# Prua in gradi bussola; le onde arrivano da scirocco (135°, WindFromDegrees di Water.tscn).
		for case in [["prua", 135.0, 1.0], ["traverso", 45.0, 0.5], ["poppa", 315.0, 1.0]]:
			await setup(sea)
			place(0.0, case[1])
			drive(case[2])
			var n := seconds(60.0)
			var slam := 0.0
			var max_heel := 0.0
			var max_pitch := 0.0
			var airborne := 0
			var capsized := false
			var speed := 0.0
			for i in n:
				await physics_frame
				var t := tel()
				slam = max(slam, t.get("slam_g", 0.0))
				max_heel = max(max_heel, abs(heel()))
				max_pitch = max(max_pitch, abs(pitch()))
				if t.get("immersed_area", 1.0) <= 0.0:
					airborne += 1
				if boat.global_basis.y.y < 0.0:
					capsized = true
				speed += t.get("speed_kn", 0.0)
			var ok: bool = check_numeric("waves")
			report("onde %d %s" % [int(sea), case[0]], ok, "%.1f kn medi, sbandamento max %.0f°, beccheggio max %.0f°, impatto max %.1f g, in volo %.1f s%s" % [
				speed / n, max_heel, max_pitch, slam, airborne / 60.0, ", CAPOVOLTA" if capsized else ""])
			await teardown()

