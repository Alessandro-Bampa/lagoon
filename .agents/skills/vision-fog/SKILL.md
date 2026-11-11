---
name: vision-fog
description: Modifica campo visivo locale, occlusione, shroud polare, visibilità dei personaggi e shader di profondità.
---

# Visione e shroud

- La visione è presentazione locale: legge stato replicato e non replica niente. Non disabilitare hitbox o fisica quando un'entità non è visibile.
- Forma corrente: passivo 12 m a 360°; in mira cono 30 m/34° unito a bolla periferica 6 m/360°. Il calcolo è sul piano XZ.
- `VisibilityGate` cambia solo `Visual.Visible`, mostra subito e nasconde con breve ritardo. Il player locale non si nasconde a sé stesso.
- `VisionBlockerMask` include mondo, veicoli e `building_cover`, ma non hitbox o ponte.
- Lo shroud usa una texture polare `RayCount × 1`, non un SubViewport cartesiano. `SurfaceBias` serve solo alla resa del bordo, non a `CanSeePoint`.
- Le global shader devono essere dichiarate in `[shader_globals]` prima della compilazione; `VisionGlobals` resta l'unico scrittore.
- Godot 4.7 richiede un uniform `hint_depth_texture`; la ricostruzione usa profondità grezza senza conversione OpenGL. Il quad resta nel cull mask e con margine/AABB ampi.
- Materiali trasparenti estesi devono scrivere profondità se lo shroud deve aderire alla loro superficie.

Diagnosi: prima usa `Shroud.gdshader` `debug_mode=1` per verificare che la ricostruzione resti ancorata al mondo; poi controlla maschera, quote degli ostacoli e log shader. Per una prova headless istanzia anche un player, altrimenti lo shader può non compilare affatto.
