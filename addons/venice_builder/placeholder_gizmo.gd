@tool
extends EditorNode3DGizmoPlugin

## Disegna il cilindro dei segnaposto OSM e lo rende CLICCABILE nella vista 3D.
## Un MeshInstance3D figlio non basterebbe: l'editor seleziona con un clic solo
## cio' che ha un gizmo con triangoli di collisione (o appartiene alla scena).

const RADIUS := 0.6
const HEIGHT := 2.4

var _faces: PackedVector3Array
var _mesh: CylinderMesh


func _init() -> void:
	create_material("pending", OsmPlaceholder.COLOR_PENDING)
	create_material("done", OsmPlaceholder.COLOR_DONE)
	_mesh = CylinderMesh.new()
	_mesh.top_radius = RADIUS
	_mesh.bottom_radius = RADIUS
	_mesh.height = HEIGHT
	_mesh.radial_segments = 8
	_mesh.rings = 1
	_faces = _mesh.get_faces()


func _get_gizmo_name() -> String:
	return "OsmPlaceholder"


func _has_gizmo(node: Node3D) -> bool:
	return node is OsmPlaceholder


func _redraw(gizmo: EditorNode3DGizmo) -> void:
	gizmo.clear()
	var node := gizmo.get_node_3d() as OsmPlaceholder
	if node == null:
		return
	var offset := Transform3D(Basis.IDENTITY, Vector3(0.0, node.marker_height() + HEIGHT * 0.5, 0.0))
	gizmo.add_mesh(_mesh, get_material("done" if node.is_generated() else "pending", gizmo), offset)
	# Triangoli di collisione nella stessa posizione del disegno.
	var arrays := []
	arrays.resize(Mesh.ARRAY_MAX)
	arrays[Mesh.ARRAY_VERTEX] = offset * _faces
	var solid := ArrayMesh.new()
	solid.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)
	gizmo.add_collision_triangles(solid.generate_triangle_mesh())
