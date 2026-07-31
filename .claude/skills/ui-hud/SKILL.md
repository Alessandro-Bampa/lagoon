---
name: ui-hud
description: Regole di layout della UI, scala dell'interfaccia, menu di pausa/impostazioni, HUD di gioco e mappa dei comandi. Carica questa skill quando tocchi ui/, la HUD, un pannello, un menu, un popup, la risoluzione o la scala UI, i volumi audio, il fullscreen, il VSync, oppure quando devi assegnare o cambiare un tasto/azione di input. File tipici: PauseMenu, MainMenu, SettingsService, PlayerHud, InventoryScreen, GridPanelView, HotbarSlotView, CrosshairOverlay, WeaponHudPanel, default_bus_layout.tres.
---

# UI, HUD e comandi

Regola fondante: **la UI ha dimensione in pixel fissa; sono gli ancoraggi a riposizionarla, non lo scaling automatico dell'engine.**

---

## 1. Pixel fissi + ancoraggio dinamico

- Risoluzione base di riferimento **1920×1080**, dichiarata in `project.godot` insieme a `window/stretch/mode="disabled"`, `window/stretch/aspect="expand"`, `window/stretch/scale=1.0`. Con lo stretch disabilitato un pannello di 100×100 resta 100×100 pixel fisici a qualunque risoluzione: passando a 2K/4K la finestra guadagna spazio e gli elementi si ridistribuiscono lungo i bordi anziché ingrandirsi.
- Di conseguenza **ogni elemento va ancorato, non posizionato**: `LayoutPreset` (`TopLeft`, `BottomWide`, `FullRect`, `CenterContainer` per i pannelli modali) + `MarginContainer` per la distanza costante dal bordo + `HBox`/`VBox` per l'ordinamento interno. Niente coordinate assolute — unica eccezione dichiarata: le finestre pop-up trascinabili (`FloatingWindow`), posizionabili dall'utente per definizione, che vengono comunque *clampate* dentro l'area visibile.
- Le griglie in pixel (`GridPanelView.CellSize = 48`, `HotbarSlotView.SlotSize = 56`) dichiarano solo `CustomMinimumSize`: **non scrivere mai `Size` a mano** su un `Control` dentro un container o con anchor impostati.
- L'unica leva di scala è **`Window.ContentScaleFactor`** sulla finestra root, esposta come slider **"Scala UI"** (0.75×–2.0×). È il meccanismo documentato da Godot per questo scenario: con stretch `disabled`, `scale = 2.0` significa "1 unità della scena = 2×2 pixel". Poiché agisce sul viewport, **tutto l'albero 2D continua a lavorare in coordinate logiche 1:1**: la matematica in pixel dell'inventario (hit-test in `GridPanelView.CellAt`, rect di `PlayerHud.SyncHudRect`) resta valida senza modifiche. Vale anche per la mira: `GetViewport().GetMousePosition()` è in coordinate logiche.
- **Eccezione da ricordare**: i `PopupMenu` sono `Window` separate e non ereditano il fattore della root. Ogni popup creato in codice va passato a `SettingsService.ApplyToPopup(...)`.

---

## 2. Dove vive la UI

| Cosa | Dove | Layer |
|---|---|---|
| `MainMenu`, `PauseMenu` | `CanvasLayer` `UI` di `world/scenes/Main.tscn` | 20 |
| HUD di gioco (inventario, hotbar, reticolo, pannello arma) | `CanvasLayer` creato a runtime da `PlayerHud` | 10 |

`PlayerHud` costruisce la HUD **solo per l'avatar locale** (`GetParent().IsMultiplayerAuthority()`), dentro un `Control` `_hudRoot` con `TopLevel = true` e `MouseFilter = Ignore`, risincronizzato col viewport da `SyncHudRect()` su `SizeChanged` e su `SettingsService.UiScaleChanged`.

**Ogni nuovo elemento di HUD va aggiunto a `_hudRoot`**, non a un `CanvasLayer` proprio: eredita gratis il rect corretto e la reattività al cambio di scala. L'ordine di aggiunta è l'ordine di disegno (l'ultimo sta sopra).

---

## 3. Menu di pausa (ESC)

- **ESC** (azione `toggle_menu`) apre `ui/scenes/PauseMenu.tscn`: pagina radice (Riprendi / Impostazioni / Esci dalla partita / Esci dal gioco), sotto-pagina Impostazioni con **Lingua, Scala UI, Schermo intero, VSync, Mira e Accovacciamento a interruttore, Volumi (Master/Musica/Effetti)**, e sotto-pagina **Comandi** per la rimappatura dei tasti (§7). ESC risale di una pagina alla volta invece di chiudere tutto.
- Lo stesso pannello è raggiungibile dal `MainMenu` (`Open(settingsOnly: true)`), così le opzioni esistono anche prima di entrare in partita.
- `autoload/SettingsService.cs` è **l'unico proprietario dei valori**: li carica/salva su `user://settings.cfg` (`ConfigFile`) e li applica (`ContentScaleFactor`, `DisplayServer`, `AudioServer`). La UI non tocca mai direttamente i server. I bus audio `Master/Music/SFX` sono definiti in `default_bus_layout.tres` (nessuna sorgente sonora esiste ancora: i bus servono perché gli slider abbiano un bersaglio reale).
- **Il menu NON mette in pausa l'albero.** `GetTree().Paused` fermerebbe solo il peer locale desincronizzandolo dagli altri (CLAUDE.md §3). Il mondo continua a girare; è sospeso solo l'input locale di gameplay, tramite il flag `GameManager.UiModalOpen` letto da `PlayerInput.ReadMovement()`, `PlayerHud._Input()` e `WeaponInput`.
- **Limite noto accettato**: "Esci dalla partita" chiude il processo. Il `NetworkManager` non supporta una disconnessione pulita con ritorno al menu principale (coerente con l'assenza di host migration); da implementare insieme a quella.

Qualunque nuova UI modale deve alzare `GameManager.UiModalOpen`, mai `GetTree().Paused`.

---

## 4. Comandi

| Azione | Tasto | Gestita da | Note |
|---|---|---|---|
| `move_up/down/left/right` | WASD / frecce | `PlayerInput` | Ruotate di `CameraYawDegrees` |
| `toggle_inventory` | Tab | `PlayerHud._Input` | |
| `interact` | **F** | `PlayerHud._Input` | Breve = raccogli/apri, lunga = menu contestuale. Consumata **solo se il pickup vince sul timone** — vedi sotto |
| `interact` | **F** | `VehicleInput._UnhandledInput` | Prendi / lascia il timone. Raggiunta quando `PlayerHud` non consuma |
| `rotate_item` | **R** | `PlayerHud._Input` | Consumata **solo a inventario aperto** — vedi sotto |
| `reload` | **R** | `WeaponInput._UnhandledInput` | Raggiunta **solo a inventario chiuso** |
| `quick_slot_4…0` | 4 5 6 7 8 9 0 | `PlayerHud._Input` | Hotbar consumabili |
| `weapon_slot_1/2/3` | 1 2 3 | `WeaponInput` | Impugna WeaponPrimary / WeaponSecondary / Sidearm; ripremere rinfodera |
| `fire` | Mouse sinistro | `WeaponInput` | Automatico in `_Process`, semiauto in `_UnhandledInput` |
| `aim` | **Mouse destro** (tenuto o a interruttore) | `PlayerInput.ReadAim` | Stance di mira: busto sul mirino, turn-in-place, strafe armato (skill `character-animation`) |
| `jump` | Spazio | `PlayerInput.ReadJumpPressed` | Evento (`IsActionJustPressed`), vietato da accovacciati |
| `sprint` | Shift (tenuto) | `PlayerInput.ReadSprint` | |
| `crouch` | Ctrl (tenuto o a interruttore) | `PlayerInput.ReadCrouch` | |
| `quick_drop` | Backspace | `PlayerHud._Input` | |
| `toggle_menu` | Esc | `PauseMenu` | |

**Il mouse destro nel MONDO e' `aim`; nell'INVENTARIO e' il menu contestuale.** Non confliggono:
`GridPanelView`/`EquipmentSlotView` sono `Control` che gestiscono il click grezzo, e con una UI
modale aperta `PlayerInput.ReadAim` e' comunque azzerato da `UiModalOpen`.

**R è legato di proposito a due azioni.** La disambiguazione non è un `if` sparso ma la pipeline di input di Godot: `PlayerHud` usa `_Input` e consuma `rotate_item` **solo se la schermata inventario è visibile** (`PlayerHud.InventoryOpen`); altrimenti l'evento resta *unhandled* e arriva a `WeaponInput._UnhandledInput` come `reload`.

**F è legato a due sistemi** con lo stesso meccanismo, ma il discriminante non è una flag booleana: è una **distanza**. Alla pressione `PlayerHud` interroga `VehicleInteraction.VehicleWins(...)` — al timone vince sempre il veicolo, altrimenti vince il candidato più vicino fra pickup (3.5 m) e timone (3.0 m) — e consuma solo se vince il pickup. Se non vince nessuno, **nessuno consuma**. Dettagli e trappola del rilascio nella skill `vehicles-boats` §5.

**I tasti della tabella sono i predefiniti, non delle costanti.** Sono riassegnabili dal giocatore
(§ 7), quindi nel codice non si scrive mai un tasto: si usa il **nome dell'azione**, e nei testi
`Loc.KeyFor("azione")` (§5). L'unico tasto fisso è **Esc**: annulla la cattura di un nuovo binding,
quindi `toggle_menu` è escluso dalla rimappatura per non poter diventare irraggiungibile.

**Se un nuovo sistema rivendica un tasto già assegnato, usa lo stesso meccanismo invece di aggiungere un tasto**: chi ha il contesto più specifico usa `_Input` e consuma solo quando quel contesto è attivo; chi ha il contesto di default usa `_UnhandledInput`.

`PlayerHud` usa `_Input` e non `_UnhandledInput` per un motivo preciso: `toggle_inventory` è su Tab, che altrimenti verrebbe consumato dalla navigazione focus dei `Control`. Consuma solo le azioni che riconosce, chiamando `GetViewport().SetInputAsHandled()`.

---

## 5. Testo

Nessuna label, voce di menu o tooltip contiene testo scritto nel codice: si usa una **chiave** di traduzione (`Loc.T("UI_...")`, o la chiave direttamente nella proprietà `text` di un `.tscn`). I tasti nei prompt si leggono dall'InputMap con `Loc.KeyFor("azione")`, mai scritti a mano nella stringa — così una rimappatura non lascia indietro il testo. I `PopupMenu` non passano dall'auto-translate, come non ereditano la scala UI: vanno tradotti a mano. Dettagli nella skill `i18n-localization`.

---

## 6. Cursore

Il cursore del sistema resta **sempre visibile e mai catturato**: tutto l'inventario è drag & drop e dipende dal cursore reale. Il reticolo (`CrosshairOverlay`) si disegna *attorno* alla posizione del mouse invece di sostituirla, con `MouseFilter = Ignore`, e sparisce quando l'inventario o una modale prendono il controllo.

Non introdurre `Input.MouseMode = Captured` né `SetCustomMouseCursor` senza rivalutare tutto il drag & drop.

---

## 7. Rimappatura dei comandi e opzioni di gameplay

Le Impostazioni hanno una terza pagina, **Comandi** (`%BindingsPage` in `PauseMenu.tscn`), raggiungibile dalla pagina Impostazioni. Esc naviga a ritroso una pagina alla volta: Comandi → Impostazioni → pagina radice → chiusura.

`core/Utils/InputBindings.cs` è la sorgente di verità di **quali azioni sono riassegnabili** (`InputBindings.Groups`, raggruppate per Movimento / Combattimento / Inventario). Le righe della schermata sono costruite da lì in `PauseMenu.BuildBindingRows()`: **aggiungere un'azione riassegnabile significa aggiungere una voce a `Groups` e una riga `UI_ACTION_<AZIONE>` a `locales/ui.csv`**, non toccare la scena.

Regole decise, da non ribaltare senza motivo:

- **Una riassegnazione sostituisce tutti gli eventi dell'azione** con quello scelto. Le alternative di progetto (frecce oltre a WASD) si perdono fino a "Ripristina predefiniti": è l'unico comportamento prevedibile con una riga per azione.
- Si accettano **tasti e i tre pulsanti principali del mouse**, cioè esattamente ciò che `Loc.KeyFor` sa etichettare. Rotella, gamepad e combinazioni con modificatori non sono assegnabili (Shift tenuto mentre si preme F assegna F).
- I tasti si salvano per **posizione fisica** (`PhysicalKeycode`), come i binding di `project.godot`.
- **I doppioni sono permessi**, non corretti d'ufficio: nel progetto ce ne sono di voluti (R è sia `rotate_item` sia `reload`, §4). La schermata segnala il conflitto in una riga di suggerimento e lascia decidere.
- Durante la cattura, `PauseMenu._Input` **consuma ogni evento**: altrimenti il tasto premuto per riassegnare verrebbe anche eseguito. Il rilascio con cui si conferma va ingoiato a parte (`_swallowRelease`), perché i `BaseButton` scattano al rilascio e riaprirebbero subito la cattura.

**Persistenza**: `SettingsService` resta l'unico proprietario. In `user://settings.cfg`, sezione `[input]`, si salvano **solo gli scostamenti** dai binding di progetto, in forma testuale (`key:87`, `mouse:2`) — così cambiare un default in `project.godot` lo propaga a chi non l'ha mai toccato. `ApplyBindings()` riparte sempre dai default catturati all'avvio da `InputBindings.CaptureDefaults()` e ci riapplica sopra gli override: è idempotente e si può richiamare a ogni `ApplyAll()`.

**Mira e accovacciamento a interruttore** (`SettingsService.AimToggle` / `CrouchToggle`, sezione `[gameplay]`): la differenza fra "tenuto" e "interruttore" esiste **solo in `PlayerInput`**, che espone sempre uno stato booleano; `PlayerController` e `CharacterMotor` non sanno nulla della modalità. Il fronte di salita si calcola in `PlayerInput.UpdateToggles()`, una volta per frame di fisica (guardia su `Engine.GetPhysicsFrames()`): le `Read*` sono chiamate più volte per frame e un interruttore che commuta a ogni lettura commuterebbe due volte. Lo stato dei tasti si campiona sempre, anche a UI modale aperta, così rilasciare il tasto dentro l'inventario non si presenta come una nuova pressione alla chiusura; ciò che si sospende è la commutazione. Gli interruttori si azzerano al timone e quando si torna alla modalità "tenuto".
