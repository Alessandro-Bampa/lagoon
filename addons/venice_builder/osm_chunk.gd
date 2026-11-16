@tool
class_name OsmChunk
extends Node3D

## Settore della griglia di un OsmLayer: contiene i segnaposto (e quindi gli
## edifici) il cui baricentro cade nel suo quadrato, piu' `Far`, la mesh del LOD
## lontano di tutti quegli edifici fusa in una sola (una superficie per materiale).
##
## Da vicino si disegnano i singoli edifici, cosi' frustum e occlusion culling
## lavorano edificio per edificio; oltre `OsmLayer.far_range` dal centro del
## chunk si disegna solo `Far`. Lo scambio lo fa il motore (visibility parent):
## un edificio e' visibile solo quando il `Far` del suo chunk e' nascosto, quindi
## non ci sono ne' buchi ne' doppioni al confine.
##
## E' l'unico script dell'addon che fa qualcosa in gioco, e solo in `_ready`: il
## legame col `Far` non si puo' salvare dentro le scene degli edifici, che sono
## istanze. Nessuna logica di gioco: niente da replicare in rete.
##
## Metadato `loot_points` (PackedVector3Array, coordinate locali del chunk): i
## punti in cui il gioco puo' far comparire il loot negli edifici del settore.

const FAR_NODE := "Far"
const BUILDING_NODE := "Building"


func _ready() -> void:
	wire()


## Lega gli edifici del chunk al suo `Far`. Da richiamare dopo ogni generazione.
func wire() -> void:
	var far := get_node_or_null(FAR_NODE) as GeometryInstance3D
	for placeholder in get_children():
		var building := placeholder.get_node_or_null(BUILDING_NODE)
		if building == null:
			continue
		for child in building.get_children():
			var geometry := child as GeometryInstance3D
			if geometry != null:
				geometry.visibility_parent = geometry.get_path_to(far) if far != null and is_inside_tree() else NodePath()
