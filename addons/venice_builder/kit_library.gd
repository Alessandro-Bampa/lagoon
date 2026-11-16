@tool
extends RefCounted

## Legge i moduli di un kit (assets/models/buildings/<kit>/<modulo>.gltf) e li
## tiene come liste di triangoli pronte da fondere in un edificio, piu' i
## materiali CONDIVISI del kit.
##
## Ogni .gltf importa i propri materiali (stesso nome, risorse distinte): qui
## se ne salva una sola copia per nome in assets/models/buildings/materials/<kit>/
## e tutti gli edifici generati puntano a quella. Ritoccare uno di quei .tres
## (una tinta in albedo_color, per dire) cambia tutti gli edifici del kit.

const KITS_DIR := "res://assets/models/buildings/"
const MATERIALS_DIR := "res://assets/models/buildings/materials/"
const FACADES: PackedStringArray = ["ochre", "red", "pink", "yellow", "grey", "brick"]

## Modulo da cui ricavare il materiale di una superficie, quando serve prima
## che un modulo che la usa sia stato caricato.
const CARRIERS := {"terrazzo": "floor_terrazzo", "planks": "floor_planks", "paving": "floor_paving",
	"ceiling": "ceiling_plaster", "roof": "roof_slope", "stone_blocks": "ext_plinth",
	"plaster": "int_wall", "facade": "ext_wall", "stone": "ext_stringcourse", "wood": "ceiling_beams"}

var kit := ""
var _modules: Dictionary = {}    # nome -> {surfaces, faces, convex} oppure null
var _materials: Dictionary = {}  # nome -> Material


func _init(kit_name: String) -> void:
	kit = kit_name


func has(module_name: String) -> bool:
	return module(module_name) != null


## {surfaces: [{mat, verts, normals, uvs}], faces: PackedVector3Array, convex: [PackedVector3Array]}
func module(module_name: String) -> Variant:
	if not _modules.has(module_name):
		_modules[module_name] = _load_module(module_name)
	return _modules[module_name]


## Materiale condiviso. `material_name` e' il nome della superficie del kit,
## oppure "facade_<colore>" / "facade_<colore>_base" per gli intonaci.
func material(material_name: String) -> Material:
	if _materials.has(material_name):
		return _materials[material_name]
	var path := "%s%s/%s.tres" % [MATERIALS_DIR, kit, material_name]
	var mat: Material = null
	if ResourceLoader.exists(path):
		mat = load(path)
	elif CARRIERS.has(material_name):
		# Superficie usata da geometria generata (solai, tetto): il materiale si
		# prende dal modulo del kit che lo porta.
		module(CARRIERS[material_name])
		return _materials.get(material_name)
	elif material_name.begins_with("facade_"):
		var base := material("facade")
		var texture_path := "%s%s/textures/%s.png" % [KITS_DIR, kit, material_name]
		if base != null and ResourceLoader.exists(texture_path):
			var copy: BaseMaterial3D = base.duplicate()
			copy.albedo_texture = load(texture_path)
			mat = _store(copy, material_name, path)
	_materials[material_name] = mat
	return mat


func _load_module(module_name: String) -> Variant:
	var path := "%s%s/%s.gltf" % [KITS_DIR, kit, module_name]
	if not ResourceLoader.exists(path):
		return null
	var scene: PackedScene = load(path)
	if scene == null:
		return null
	var root := scene.instantiate()
	var data := {"surfaces": [], "faces": PackedVector3Array(), "convex": []}
	_collect(root, Transform3D.IDENTITY, data)
	root.free()
	return data


func _collect(node: Node, xform: Transform3D, data: Dictionary) -> void:
	if node is Node3D:
		xform = xform * (node as Node3D).transform
	if node is MeshInstance3D and (node as MeshInstance3D).mesh != null:
		var mesh := (node as MeshInstance3D).mesh
		for s in mesh.get_surface_count():
			var source := mesh.surface_get_material(s)
			var mat_name := source.resource_name if source != null else "plaster"
			_adopt_material(mat_name, source)
			var arrays := mesh.surface_get_arrays(s)
			var verts: PackedVector3Array = arrays[Mesh.ARRAY_VERTEX]
			var normals: PackedVector3Array = arrays[Mesh.ARRAY_NORMAL]
			var uvs: PackedVector2Array = arrays[Mesh.ARRAY_TEX_UV]
			var indices = arrays[Mesh.ARRAY_INDEX]
			if indices != null and indices.size() > 0:
				# Si scioglie l'indice una volta sola: fondere i moduli diventa
				# un semplice append di array, senza cicli per vertice.
				var v2 := PackedVector3Array()
				var n2 := PackedVector3Array()
				var u2 := PackedVector2Array()
				v2.resize(indices.size()); n2.resize(indices.size()); u2.resize(indices.size())
				for i in indices.size():
					var k: int = indices[i]
					v2[i] = verts[k]
					n2[i] = normals[k]
					u2[i] = uvs[k]
				verts = v2; normals = n2; uvs = u2
			data["surfaces"].append({"mat": mat_name, "verts": xform * verts,
				"normals": Transform3D(xform.basis.orthonormalized(), Vector3.ZERO) * normals, "uvs": uvs})
	elif node is CollisionShape3D:
		var shape := (node as CollisionShape3D).shape
		if shape is ConcavePolygonShape3D:
			data["faces"].append_array(xform * (shape as ConcavePolygonShape3D).get_faces())
		elif shape is ConvexPolygonShape3D:
			data["convex"].append(xform * (shape as ConvexPolygonShape3D).points)
	for child in node.get_children():
		_collect(child, xform, data)


## La prima volta che un nome di superficie compare, il suo materiale diventa
## quello condiviso del kit (salvato su disco se non c'e' gia').
func _adopt_material(mat_name: String, source: Material) -> void:
	if _materials.has(mat_name) or source == null:
		return
	var path := "%s%s/%s.tres" % [MATERIALS_DIR, kit, mat_name]
	if ResourceLoader.exists(path):
		_materials[mat_name] = load(path)
		return
	var copy: Material = source.duplicate()
	if copy is BaseMaterial3D:
		# Godot ignora KHR_materials_specular: lo specular 0 dello stile va messo qui.
		(copy as BaseMaterial3D).metallic_specular = 0.0
		(copy as BaseMaterial3D).roughness = 1.0
	_materials[mat_name] = _store(copy, mat_name, path)


func _store(mat: Material, mat_name: String, path: String) -> Material:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(path.get_base_dir()))
	mat.resource_name = mat_name
	var err := ResourceSaver.save(mat, path)
	if err != OK:
		push_warning("[venice_builder] Impossibile salvare %s (errore %d): materiale non condiviso." % [path, err])
		return mat
	mat.take_over_path(path)
	return mat
