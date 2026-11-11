---
name: ui-hud
description: UI, HUD, menu, cursore e comandi. Carica quando tocchi ui/, la HUD, un pannello, un menu, un popup, la scala UI, risoluzione, volumi, fullscreen, VSync, sensibilità del mouse, la cattura del mouse, oppure quando assegni o cambi un tasto/azione di input (PauseMenu, MainMenu, SettingsService, InputBindings, PlayerHud, PlayerInput, FirstPersonCamera, CrosshairOverlay, WeaponHudPanel).
---

# UI, HUD e comandi

## 1. Layout: pixel fissi, ancoraggi dinamici

- Base 1920×1080, stretch `disabled`, aspect `expand`: un pannello 100×100 resta tale a ogni
  risoluzione. **Si ancora, non si posiziona** (`LayoutPreset`, `MarginContainer`, `HBox`/`VBox`). Unica
  eccezione: le `FloatingWindow` trascinabili, clampate nell'area visibile.
- Mai scrivere `Size` su un `Control` in un container: solo `CustomMinimumSize`.
- Unica leva di scala: `Window.ContentScaleFactor` (slider "Scala UI", 0.75–2.0). L'albero 2D resta in
  coordinate logiche, quindi la matematica in pixel dell'inventario non cambia.
- I `PopupMenu` sono `Window` separate: **non** ereditano scala né auto-translate. Ogni popup creato in
  codice passa da `SettingsService.ApplyToPopup` e traduce le voci con `Loc.T`.

## 2. Dove vive

| Cosa | Dove | Layer |
|---|---|---|
| `MainMenu`, `PauseMenu` | `CanvasLayer` `UI` di `Main.tscn` | 20 |
| HUD (inventario, hotbar, reticolo, pannello arma) | `CanvasLayer` creato da `PlayerHud`, solo avatar locale | 10 |

Ogni nuovo elemento di HUD va aggiunto a `PlayerHud._hudRoot` (eredita rect e reattività alla scala).

## 3. Cursore e sguardo

- In gioco il mouse è **catturato** e muove lo sguardo (`FirstPersonCamera._Input`, solo a mouse
  catturato, da `ScreenRelative` così la scala UI non altera la sensibilità).
- **`PlayerHud` è l'unico proprietario di `Input.MouseMode`**: lo rilascia quando l'inventario è aperto
  o `GameManager.UiModalOpen` è vero, lo ricattura alla chiusura, lo rende visibile in `_ExitTree`.
  Chi apre una UI alza il proprio stato e basta.
- Il reticolo sta al **centro** dello schermo; sparisce con inventario o modale.
- Sensibilità: `SettingsService.MouseSensitivity` (0.1–3.0), sezione `[gameplay]`.

## 4. Menu di pausa e impostazioni

- `toggle_menu` (Esc) apre `PauseMenu`: radice → Impostazioni → Comandi; Esc risale una pagina.
  Raggiungibile anche dal `MainMenu` (`Open(settingsOnly: true)`).
- **`SettingsService` è l'unico proprietario dei valori** (`user://settings.cfg`) e l'unico che tocca
  `DisplayServer`/`AudioServer`. La UI legge e scrive le sue proprietà, poi `ApplyAll()`.
- **Mai `GetTree().Paused`**: fermerebbe solo il peer locale. Una UI modale alza
  `GameManager.UiModalOpen`, che sospende il solo input di gameplay.
- "Esci dalla partita" chiude il processo: manca una disconnessione pulita (va con l'host migration).

## 5. Comandi predefiniti

Nel codice si usa sempre il **nome dell'azione**; nei testi `Loc.KeyFor("azione")`.

| Azione | Tasto | Gestita da |
|---|---|---|
| sguardo | mouse | `FirstPersonCamera` |
| `move_*` | WASD / frecce | `PlayerInput` — relative allo sguardo |
| `jump` · `sprint` · `crouch` | Spazio · Shift · Ctrl | `PlayerInput` (crouch anche a interruttore) |
| `fire` | mouse sx | `WeaponInput` (auto in `_Process`, semiauto in `_UnhandledInput`) |
| `aim` | mouse dx (tenuto o interruttore) | `PlayerInput.ReadAim` → zoom |
| `reload` | **R** | `WeaponInput._UnhandledInput`, solo a inventario chiuso |
| `rotate_item` | **R** | `PlayerHud._Input`, solo a inventario aperto |
| `weapon_slot_1/2/3` | 1 2 3 | `WeaponInput` (ripremere rinfodera) |
| `quick_slot_4…0` | 4…0 | `PlayerHud._Input` |
| `toggle_inventory` | Tab | `PlayerHud._Input` |
| `interact` | **F** | `PlayerHud._Input` (oggetto guardato: breve = raccogli/apri, lunga = menu) oppure `VehicleInput._UnhandledInput` (timone) |
| `quick_drop` | Backspace | `PlayerHud._Input` |
| `toggle_menu` | Esc | `PauseMenu` — non riassegnabile |

**Tasto conteso fra due sistemi → niente tasto nuovo**: chi ha il contesto più specifico usa `_Input` e
consuma **solo** quando quel contesto è attivo; il default usa `_UnhandledInput`. R si disambigua con
`InventoryOpen`; F con `VehicleInteraction.VehicleWins` (deciso una volta alla pressione, il rilascio
segue lo stesso proprietario — skill `vehicles-boats`). `PlayerHud` usa `_Input` perché Tab verrebbe
altrimenti mangiato dalla navigazione focus.

## 6. Rimappatura

- `InputBindings.Groups` è la fonte di verità delle azioni riassegnabili. **Aggiungerne una = voce in
  `Groups` + riga `UI_ACTION_<AZIONE>` in `locales/ui.csv`**, non si tocca la scena.
- Una riassegnazione sostituisce **tutti** gli eventi dell'azione. Solo tasti e i tre pulsanti del
  mouse, per posizione fisica. I doppioni sono permessi e solo segnalati.
- Si salvano solo gli scostamenti (`[input]`, forma `key:87` / `mouse:2`); `ApplyBindings()` riparte
  dai default catturati all'avvio, quindi è idempotente. **La sezione `[input]` contiene solo azioni**:
  altre impostazioni vanno in `[gameplay]`.
- Durante la cattura `PauseMenu._Input` consuma ogni evento e ingoia il rilascio (`_swallowRelease`).
- Mira/crouch a interruttore: la differenza esiste **solo** in `PlayerInput.UpdateToggles()`, una volta
  per frame di fisica; i tasti si campionano anche a UI aperta, si sospende solo la commutazione.
