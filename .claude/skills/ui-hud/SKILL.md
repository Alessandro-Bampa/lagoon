---
name: ui-hud
description: UI, HUD, menu, cursore e comandi. Carica quando tocchi ui/, la HUD, un pannello, un menu, un popup, la scala UI, risoluzione, volumi, fullscreen, VSync, distanza di visione, nebbia, sensibilità del mouse, la cattura del mouse, oppure quando assegni o cambi un tasto/azione di input (PauseMenu, MainMenu, SettingsService, InputBindings, PlayerHud, PlayerInput, FirstPersonCamera, HitMarkerOverlay, WeaponHudPanel).
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
| HUD (inventario, barra rapida, conferma di colpo, pannello arma) | `CanvasLayer` creato da `PlayerHud`, solo avatar locale | 10 |

Ogni nuovo elemento di HUD va aggiunto a `PlayerHud._hudRoot` (eredita rect e reattività alla scala).

## 3. Cursore e sguardo

- In gioco il mouse è **catturato** e muove lo sguardo (`FirstPersonCamera._Input`, solo a mouse
  catturato e se è la camera corrente — al timone in terza persona il mouse è di `BoatChaseCamera`, da `ScreenRelative` così la scala UI non altera la sensibilità).
- **`PlayerHud` è l'unico proprietario di `Input.MouseMode`**: lo rilascia quando l'inventario è aperto
  o `GameManager.UiModalOpen` è vero, lo ricattura alla chiusura, lo rende visibile in `_ExitTree`.
  Chi apre una UI alza il proprio stato e basta.
- **Non c'è reticolo**: si mira con le tacche dell'arma (skill `combat-shooting`). Al centro dello
  schermo compare solo la conferma di colpo a segno (`HitMarkerOverlay`), che sparisce con inventario
  o modale.
- Sensibilità: `SettingsService.MouseSensitivity` (0.1–3.0), sezione `[gameplay]`.

## 4. Menu di pausa e impostazioni

- `toggle_menu` (Esc) apre `PauseMenu`: radice → Impostazioni → Comandi; Esc risale una pagina.
  Raggiungibile anche dal `MainMenu` (`Open(settingsOnly: true)`).
- **`SettingsService` è l'unico proprietario dei valori** (`user://settings.cfg`) e l'unico che tocca
  `DisplayServer`/`AudioServer`. La UI legge e scrive le sue proprietà, poi `ApplyAll()`.
- **Fps**: tetto in primo piano = `application/run/max_fps` (160) letto all'avvio; VSync dalla
  preferenza salvata (`[display] vsync`), che **vince** sul `project.godot`. Finestra non in primo
  piano → `BackgroundMaxFps` (30): la seconda istanza dei test multi-istanza costava quanto la
  prima (editor + due istanze = GPU al 100 %, ~300 W; dopo, a giochi dietro: ~60 %, 213 W).
  `SettingsService._Process` confronta a ogni frame la finestra in primo piano del sistema (su
  Windows `GetForegroundWindow`, radice compresa: l'istanza incorporata nella scheda Gioco conta
  come davanti quando lo è l'editor), perché `WindowIsFocused` e le notifiche FocusIn/Out restano
  sbagliate per una finestra nata senza focus. Escluso con `--fixed-fps` e in headless.
- **Mai `GetTree().Paused`**: fermerebbe solo il peer locale. Una UI modale alza
  `GameManager.UiModalOpen`, che sospende il solo input di gameplay.
- "Esci dalla partita" chiude il processo: manca una disconnessione pulita (va con l'host migration).
- **Distanza di visione e nebbia** (`[graphics]`: `render_distance` 0–3, `fog_density` 0–1).
  `SettingsService` le tiene e basta: `ApplyAll` emette `GraphicsChanged` se sono cambiate, e le
  applica `WorldRenderSettings` della mappa (`Laguna.tscn`; livelli, metri e misure nella skill
  `venice-terrain` §5). La distanza è **dell'host**: replicata, il client usa il minimo fra la sua e
  quella dell'host, e la colonna valore mostra "Host: N km" quando è l'host a limitarla. La nebbia è
  solo locale; anche a 0 % copre metà del bordo del terreno caricato. Le voci dell'`OptionButton`
  vengono da `WorldRenderSettings.Levels` (chiavi `UI_SETTINGS_RENDER_*`), rigenerate al cambio lingua.

## 5. Comandi predefiniti

Nel codice si usa sempre il **nome dell'azione**; nei testi `Loc.KeyFor("azione")`.

| Azione | Tasto | Gestita da |
|---|---|---|
| sguardo | mouse | `FirstPersonCamera` |
| `move_*` | WASD / frecce | `PlayerInput` — relative allo sguardo. **Al timone**: W/S = una tacca della manetta per pressione, A/D = governo tenuto (`VehicleInput`, skill `vehicles-boats` §12) |
| `jump` · `sprint` · `crouch` | Spazio · Shift · Ctrl | `PlayerInput` (crouch anche a interruttore). **A nuoto**: Spazio tenuto = risali, premuto contro un bordo = issati fuori; Ctrl tenuto = immergiti (sempre a pressione); Shift = nuoto veloce. Nessuna azione nuova |
| `fire` | mouse sx | `WeaponInput` (auto in `_Process`, semiauto in `_UnhandledInput`) |
| `aim` | mouse dx (tenuto o interruttore) | `PlayerInput.ReadAim` → zoom |
| `reload` | **R** | `WeaponInput._UnhandledInput`, solo a inventario chiuso |
| `rotate_item` | **R** | `PlayerHud._Input`, solo a inventario aperto |
| `weapon_slot_1/2/3` | 1 2 3 | `WeaponInput` (ripremere rinfodera) |
| `quick_slot_4…0` | 4…0 | `PlayerHud._Input` |
| `toggle_inventory` | Tab | `PlayerHud._Input` |
| `interact` | **F** | `PlayerHud._Input` (oggetto guardato: breve = raccogli/apri, lunga = menu) oppure `VehicleInput._UnhandledInput` (timone) |
| `quick_drop` | Backspace | `PlayerHud._Input` |
| `vehicle_trim_up` / `vehicle_trim_down` · `vehicle_thruster_left` / `vehicle_thruster_right` | E / Q · Z / X | `VehicleInput`, solo al timone (trim del fuoribordo, elica di prua; skill `vehicles-boats` §12). Gruppo "Barche" |
| `vehicle_camera_toggle` · `vehicle_camera_reset` | V · C | `VehicleInput._UnhandledInput`, solo al timone: prima/terza persona e visuale verso la prua (`BoatChaseCamera`, skill `vehicles-boats` §9). Gruppo "Barche" |
| `debug_sea_up` / `debug_sea_down` / `debug_sea_exposure` | PagSu / PagGiu' / Home | `SeaStateDebugControls`, solo nei livelli di prova delle onde, solo host. Non riassegnabili (non in `Groups`) |
| `toggle_menu` | Esc | `PauseMenu` — non riassegnabile. A inventario aperto è di `PlayerHud` (chiude l'ultima finestra, poi l'inventario): `PauseMenu` si astiene se `GameManager.InventoryOpen` |

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
