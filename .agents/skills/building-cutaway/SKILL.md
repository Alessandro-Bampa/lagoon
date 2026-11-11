---
name: building-cutaway
description: Modifica camera isometrica, edifici, culling per piano, dissolvenza dei muri, render layer e maschere di mira.
---

# Camera ed edifici isometrici

## Contratti

- La camera ruota a scatti di 45°; movimento e logica dipendente dalla vista devono leggere `IsometricCamera.CurrentYawDegrees`, non assumere uno yaw fisso.
- `BuildingVolume` descrive footprint e altezze. I figli `FloorN` usano render layer dedicati; `BuildingCullController` decide localmente quali piani la camera disegna.
- Il cutaway è presentazione per-peer: non replica stato e non cambia collisioni, hitbox o autorità.
- `WorldSurface.gdshader` deve restare opaco. La sfumatura usa `discard`; scrivere `ALPHA` sposterebbe il materiale nella passata trasparente e romperebbe profondità/shroud.
- Mantieni distinti `CursorMask` (dove si posa il cursore) e `ShotMask` (cosa ferma un colpo). `building_cover` è escluso dal cursore ma incluso nei colpi e nell'occlusione visiva.
- Render layer e collision layer sono sistemi diversi: aggiorna `RenderLayers`, `CollisionLayers`, `project.godot` e i letterali nelle scene coerentemente.

## Flusso

Prima di cambiare una mesh o un materiale del mondo, verifica se partecipa a cutaway, profondità dello shroud o mira. Per un nuovo edificio, configura `BuildingVolume`, piani numerati e materiale condiviso; evita script speciali per singola mesh.

Verifica da più angoli camera, dentro/fuori l'edificio e su ogni piano. Controlla che soffitti invisibili non catturino il cursore, ma continuino a fermare colpi e linea di vista.
