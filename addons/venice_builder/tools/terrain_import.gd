extends SceneTree

## Region di Terrain3D (1.1) dai tile di osm_terrain.py: terreno dai soli dati OSM.
##
## Per ogni region elencata in meta.json crea un Terrain3DRegion con
##   height_map            quote "di gioco", mare a 0 (salvate a 16 bit: < 1 cm d'errore)
##   compressed_color_map  tinta della classe, S3TC con mipmap: 1/4 della memoria in gioco
##   control_map vuota (texture 0) da sanitize_maps(): le texture per classe si
##               scriveranno dalle classi (<mappa>/classes) senza rigenerare le quote
## e la salva come terrain3d<x><z>.res nella cartella del terreno. Una region alla
## volta, headless: la mappa intera non sta in memoria (ne' qui ne' in gioco: la
## carica TerrainStreamer attorno al giocatore).
##
## Tooling di sola generazione (GDScript come il resto di venice_builder). Uso:
##   godot --path <progetto> --headless --script res://addons/venice_builder/tools/terrain_import.gd
##         -- --map res://world/maps/laguna/map.json [--keep-tiles]
##   oppure: -- <cartella dei tile> <cartella del terreno> [--keep-tiles]
## Senza --keep-tiles i tile grezzi (16 MB + 16 MB per region) si cancellano dopo
## l'import riuscito di ciascuna region.

const REGION_SIZE := 2048
## Terrain3DData::CURRENT_DATA_VERSION di Terrain3D 1.1.
const DATA_VERSION := 1.10


func _init() -> void:
	quit(_run())


func _run() -> int:
	var args := OS.get_cmdline_user_args()
	var keep_tiles := args.has("--keep-tiles")
	args.erase("--keep-tiles")
	var tiles_dir := ""
	var data_dir := ""
	if args.size() >= 2 and args[0] == "--map":
		var map_path: String = args[1]
		var cfg: Dictionary = JSON.parse_string(FileAccess.get_file_as_string(map_path))
		if cfg.is_empty():
			push_error("Impossibile leggere %s." % map_path)
			return 1
		var base := map_path.get_base_dir()
		tiles_dir = base.path_join(cfg["dirs"]["build"]).path_join("tiles")
		data_dir = base.path_join(cfg["dirs"]["terrain"])
	elif args.size() >= 2:
		tiles_dir = args[0]
		data_dir = args[1]
	else:
		push_error("Uso: -- --map <map.json>  oppure  -- <tile> <terreno>")
		return 1
	if not ClassDB.class_exists("Terrain3DRegion"):
		push_error("Terrain3D non e' caricato: serve l'addon terrain_3d (GDExtension).")
		return 1

	var meta_text := FileAccess.get_file_as_string(tiles_dir.path_join("meta.json"))
	if meta_text.is_empty():
		push_error("Manca %s: lancia prima osm_terrain.py." % tiles_dir.path_join("meta.json"))
		return 1
	var meta: Dictionary = JSON.parse_string(meta_text)
	if int(meta["region_size"]) != REGION_SIZE:
		push_error("region_size %s non supportata (attesa %d)." % [meta["region_size"], REGION_SIZE])
		return 1
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(data_dir))

	var t0 := Time.get_ticks_msec()
	var done := 0
	var skipped := 0
	for entry: Array in meta["regions"]:
		var loc := Vector2i(int(entry[0]), int(entry[1]))
		var err := _import_region(tiles_dir, data_dir, loc, float(meta.get("vertex_spacing", 1.0)), keep_tiles)
		if err == ERR_FILE_NOT_FOUND:
			skipped += 1           # gia' importata in un giro precedente (tile cancellati)
			continue
		if err != OK:
			return 1
		done += 1
		if done % 25 == 0:
			print("  ... %d region (%.0f s)" % [done, (Time.get_ticks_msec() - t0) / 1000.0])
	print("Region importate: %d (saltate %d gia' fatte) in %.1f s -> %s" % [
		done, skipped, (Time.get_ticks_msec() - t0) / 1000.0, data_dir])
	return OK


func _import_region(tiles_dir: String, data_dir: String, loc: Vector2i, spacing: float, keep_tiles: bool) -> Error:
	var tag := "%d_%d" % [loc.x, loc.y]
	var height_path := tiles_dir.path_join("height_%s.f32" % tag)
	var color_path := tiles_dir.path_join("color_%s.rgba" % tag)
	if not FileAccess.file_exists(height_path):
		return ERR_FILE_NOT_FOUND
	var count := REGION_SIZE * REGION_SIZE
	var height_bytes := FileAccess.get_file_as_bytes(height_path)
	var color_bytes := FileAccess.get_file_as_bytes(color_path)
	if height_bytes.size() != count * 4 or color_bytes.size() != count * 4:
		push_error("Tile %s incompleto: quote %d byte, colori %d byte (attesi %d)." % [tag, height_bytes.size(), color_bytes.size(), count * 4])
		return ERR_FILE_CORRUPT

	var region: Resource = ClassDB.instantiate("Terrain3DRegion")
	region.set("version", DATA_VERSION)
	region.set("region_size", REGION_SIZE)
	region.set("vertex_spacing", spacing)
	region.set("location", loc)
	region.set("height_map", Image.create_from_data(REGION_SIZE, REGION_SIZE, false, Image.FORMAT_RF, height_bytes))
	# Color map compressa S3TC con mipmap: in gioco Terrain3D usa quella (get_active_color_map),
	# 5,3 MB per region invece di 21 in RAM e VRAM. Region.save() comprime solo dentro l'editor
	# (IS_EDITOR) e scarta la mappa compressa prima di scrivere: per questo si comprime qui e si
	# salva con ResourceSaver.
	# Si salvano ENTRAMBE: nell'editor Terrain3D scarta la compressa e usa solo la non compressa
	# (con la sola compressa le tinte sparivano: terreno tutto bianco); in gioco usa la compressa
	# e libera l'altra (keep_uncompressed_color = false), quindi la memoria in gioco non cambia.
	# Su disco la non compressa costa poco: tinte piatte, zstd le schiaccia.
	var color := Image.create_from_data(REGION_SIZE, REGION_SIZE, false, Image.FORMAT_RGBA8, color_bytes)
	color.generate_mipmaps()
	var compressed := color.duplicate()
	compressed.compress_from_channels(Image.COMPRESS_S3TC, Image.USED_CHANNELS_RGBA)
	region.set("color_map", color)
	region.set("compressed_color_map", compressed)
	region.call("sanitize_maps")         # control map vuota (texture 0)
	region.call("calc_height_range")
	# Quote a 16 bit su disco (half float: < 1 cm d'errore fra -8 e +2 m); Terrain3D le
	# riporta a 32 bit quando carica la region.
	var height: Image = region.get("height_map")
	var height16 := height.duplicate()
	height16.convert(Image.FORMAT_RH)
	region.set("height_map", height16)

	# Nome di Terrain3D: "_" per indici >= 0, "-" per i negativi. La location non si
	# salva: Terrain3D (e TerrainStreamer) la ricavano dal nome del file.
	var path := data_dir.path_join("terrain3d%s%02d%s%02d.res" % [
		"_" if loc.x >= 0 else "-", absi(loc.x), "_" if loc.y >= 0 else "-", absi(loc.y)])
	var err := ResourceSaver.save(region, path, ResourceSaver.FLAG_COMPRESS)
	if err != OK:
		push_error("Salvataggio di %s fallito (errore %d)." % [path, err])
		return err
	if not keep_tiles:
		DirAccess.remove_absolute(ProjectSettings.globalize_path(height_path))
		DirAccess.remove_absolute(ProjectSettings.globalize_path(color_path))
	return OK
