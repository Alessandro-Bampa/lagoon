---
name: ui-hud
description: Modifica HUD, menu, popup, layout, scala UI, impostazioni e azioni/input del progetto.
---

# UI, HUD e input

- UI a pixel fissi con anchor/container; evita coordinate assolute salvo finestre trascinabili, che vanno clampate al viewport.
- La scala utente passa solo da `Window.ContentScaleFactor` tramite `SettingsService`. Applica la scala separatamente ai `PopupMenu`.
- Gli elementi HUD locali vivono sotto `_hudRoot` di `PlayerHud`; non creare CanvasLayer indipendenti senza motivo.
- Una UI modale imposta `GameManager.UiModalOpen`; non mettere in pausa il SceneTree in multiplayer.
- `SettingsService` possiede persistenza e applicazione di display, audio, lingua, gameplay e binding.
- Le azioni sono riassegnabili: usa nomi InputMap e `Loc.KeyFor`, non tasti hardcoded.
- Per azioni condivise usa la pipeline `_Input`/`_UnhandledInput` e consuma l'evento solo nel contesto specifico. Mantieni coerente il proprietario tra pressione e rilascio.
- Un testo visibile richiede `i18n-localization`.

Verifica almeno 1920×1080 e una risoluzione/aspect diverso, due valori di scala UI, apertura/chiusura modali e conflitti fra azioni condivise (R, F, mouse destro).
