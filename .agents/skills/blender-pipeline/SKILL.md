---
name: blender-pipeline
description: Crea, importa o verifica asset 3D, rig e clip tra Blender e Godot; usa anche il Blender MCP del progetto.
---

# Pipeline Blender → glTF → Godot

## Sorgenti e strumenti

- Gli script in `tools/blender/` sono la fonte di verità. Non rifinire a mano output che verranno rigenerati.
- L'estensione attesa è Blender Lab `mcp` su `127.0.0.1:9876`, non i pacchetti incompatibili `blender-mcp`/`blender-mcp-server`.
- Per ispezioni brevi usa l'MCP `blender`; per rebuild ripetibili usa `python tools/blender/blender_client.py <script.py>`.
- Il codice remoto Blender deve assegnare `result` a un dizionario JSON-serializzabile. Gli operatori che richiedono contesto vanno eseguiti con `view3d_override`; preferisci API dati/bmesh quando possibile.

## Invarianti asset

- Unità metriche, scala oggetto `(1,1,1)`, origini coerenti e piedi a quota zero.
- `Body_Base` e `Armature_Character` condividono fit e rig Mixamo-compatible; non passare il rig base attraverso Mixamo.
- Mantieni nomi/gerarchia delle ossa e roll misurati. Le clip Mixamo rimuovono prefissi `mixamorig\d*:` e scalano la traslazione del bacino; il gameplay non usa root motion.
- I delta additivi e le pose derivate dal rest si generano in Godot, non attraverso glTF.
- `build_animation_library.py` è additivo: se riporta `lost`, fermati e recupera le sorgenti prima di salvare il `.glb`.

## Rebuild

Per il corpo:

```powershell
python tools/blender/blender_client.py tools/blender/build_character.py
python tools/blender/verify_glb.py
```

Per una libreria animazioni, esegui il builder Blender pertinente, reimporta in Godot, poi rigenera nell'ordine pose d'impugnatura, delta, crouch e BlendTree. La skill `character-animation` contiene il contratto runtime e la strategia per il prossimo refactor.

Concludi con `tools/verify_godot_import.gd`; per clip o rig esegui anche `tools/verify_animation_runtime.gd` e controlla visivamente la silhouette nella camera isometrica.
