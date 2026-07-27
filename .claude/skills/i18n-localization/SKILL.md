---
name: i18n-localization
description: Traduzione delle stringhe di gioco (IT/EN, fallback EN). Carica questa skill quando tocchi locales/, aggiungi o modifichi un testo visibile all'utente, crei un nuovo item o un nuovo pannello di UI, lavori su Loc, SettingsService.Language o il selettore Lingua, oppure quando compaiono chiavi grezze a video (UI_..., ITEM_...) o warning "[Loc] Chiave di traduzione mancante".
---

# Internazionalizzazione

Regola fondante: **nel codice non si scrive mai testo naturale.** Ogni stringa che il giocatore legge esiste come **chiave** in `locales/`, risolta a runtime.

Lingue: **italiano** e **inglese**. Fallback: **inglese** — una chiave tradotta solo in EN resta leggibile ovunque, quindi una traduzione italiana mancante degrada, non rompe.

---

## 1. Due formati, due cicli di vita

| | Cosa ci va | Come lo carica Godot |
|---|---|---|
| `locales/ui.csv`, `locales/items.csv` | Stringhe **brevi**: menu, HUD, inventario, errori di rete, nomi/descrizioni/effetti degli oggetti | Importate (`csv_translation`) → generano `<nome>.<locale>.translation` **accanto al CSV** |
| `locales/dialogue/*.po` | Testi **lunghi**: battute, note trovate nel mondo, briefing di quest | Caricati **nativamente**, nessun passo di import |

Non è ridondanza. Il CSV mette le lingue in colonne affiancate — perfetto per una label da tre parole, illeggibile per un paragrafo. Il PO gestisce testo multiriga, `msgctxt` e plurali, e non richiede un reimport a ogni modifica.

`TranslationServer` fonde tutto in **un unico spazio di chiavi**: la divisione è organizzativa, il codice non sa da quale file arrivi una chiave.

> **Dopo aver modificato un `.csv` serve un reimport**, altrimenti il `.translation` resta vecchio:
> `Godot_v4.7.1-stable_mono_win64_console.exe --path "c:/repositories/lagoon" --headless --import`
> I `.po` no: si rileggono così come sono.

I `.translation` generati sono **committati**, come i `.import`: una clonazione fresca deve poter girare senza aprire prima l'editor.

### Fallback: perché non compare in `project.godot`
`internationalization/locale/fallback` vale già `"en"` per default in Godot, quindi l'editor **non lo serializza** quando lo si imposta a quel valore: riscriverlo a mano è inutile, viene rimosso al primo salvataggio. Il comportamento è verificato (con `locale/test="fr"` tutto ricade su EN, nessuna chiave grezza).

Lo stesso vale per i commenti dentro `project.godot`: l'editor li elimina riscrivendo il file. Le spiegazioni vanno qui, non lì.

---

## 2. Convenzione delle chiavi

`AMBITO_SOTTOAMBITO_NOME`, in `SCREAMING_SNAKE_CASE` ASCII.

| Prefisso | Ambito |
|---|---|
| `UI_MENU_*` | Menu principale |
| `UI_PAUSE_*` / `UI_SETTINGS_*` | Menu di pausa e impostazioni |
| `UI_INV_*` / `UI_INSPECT_*` | Inventario, finestre, menu contestuale |
| `UI_HUD_*` | HUD di gioco, prompt nel mondo, pannello arma |
| `NET_ERR_*` | Errori di rete mostrati in UI |
| `SLOT_*` / `CATEGORY_*` / `UI_MOUSE_*` | Derivate da un enum (vedi §4) |
| `ITEM_<ITEMID>_NAME` / `_DESC` / `_EFFECT` | Oggetti (vedi §3) |
| `DLG_*` | Dialoghi, solo nei `.po` |

> **Una chiave contiene sempre almeno un underscore.** È ciò che la distingue da un identificatore maiuscolo qualsiasi (`"SFX"`, `"X"`), e su cui si basa il riconoscimento automatico in `tools/check-i18n.ps1`.

### Segnaposto, mai concatenazione
Il valore contiene `{0}`, `{1}`; il codice passa gli argomenti:

```csharp
// SBAGLIATO: fissa l'ordine delle parole dell'italiano su tutte le lingue
label.Text = Loc.T("UI_INV_WEIGHT_PREFIX") + peso + " kg";

// GIUSTO
label.Text = Loc.T("UI_INV_WEIGHT", Loc.Num(peso), Loc.Num(max, "0"));
```

**I numeri passano da `Loc.Num`**, che formatta in `InvariantCulture`. Il separatore decimale non deve cambiare con la lingua: cambierebbe anche la larghezza dell'elemento che lo contiene, e la UI ha dimensioni in pixel fisse (skill `ui-hud`).

---

## 3. Oggetti: le chiavi si derivano dall'ItemId

I `.tres` in `resources/items/` **non contengono testo**. `ItemDefinition` espone:

```csharp
public string NameKey        => $"ITEM_{ItemId.ToUpperInvariant()}_NAME";
public string DisplayName    => Loc.T(NameKey);
public string Description    => Loc.TOrEmpty(DescriptionKey);  // "" se assente
public string Effect         => Loc.TOrEmpty(EffectKey);       // "" se assente
```

Creare un item significa quindi: un `.tres` con `ItemId`, e **due o tre righe in `locales/items.csv`**. Non esiste un campo in cui scrivere un nome per sbaglio.

- `DisplayName` è una **proprietà calcolata**: non memorizzarla in un campo, cambia con la lingua.
- `Description` ed `Effect` sono **opzionali**: chiave assente → stringa vuota, nessun warning. La UI salta la riga.
- `ItemDatabase._Ready` segnala in debug ogni item senza `_NAME`: una dimenticanza si vede all'avvio, non aprendo l'inventario.

> **La rete non è toccata.** Sul filo viaggia solo l'`ItemId`. Due giocatori con lingue diverse vedono nomi diversi dello stesso identico oggetto, senza alcuna desincronizzazione (CLAUDE.md §3).

Stessa logica per gli enum: `EquipmentSlotView.SlotLabel` e `InspectWindow.CategoryLabel` costruiscono `SLOT_<NOME>` / `CATEGORY_<NOME>` dal nome del valore. Aggiungere un valore all'enum = aggiungere una riga al CSV, e il check script segnala quella dimenticata.

---

## 4. Come si traduce, caso per caso

### Testo statico in una scena `.tscn`
Si scrive **la chiave** nella proprietà `text`. Ci pensa l'**auto-translate** dei `Control`, che li riaggiorna anche da solo al cambio lingua. Nessun codice.

### Testo statico in un `Control` creato da codice
Idem: assegna la chiave, l'auto-translate la risolve appena il nodo entra nell'albero.

```csharp
var back = new Button { Text = "UI_INV_BACK_TO_GROUND" };
```

### Testo composto o dinamico
Serve `Loc.T` esplicito, **e va disattivato l'auto-translate**:

```csharp
var label = new Label
{
    Text = Loc.T("UI_INV_WEIGHT", a, b),
    AutoTranslateMode = AutoTranslateModeEnum.Disabled,
};
```

> **Perché disattivarlo.** Il nodo riceve un risultato già tradotto; un secondo passaggio su un risultato non è mai voluto. Oggi è innocuo (nessuna corrispondenza), ma diventa un bug il giorno in cui una traduzione coincide con una chiave.

### `PopupMenu` e `Window`
**Non passano dall'auto-translate**: le voci vanno tradotte a mano con `Loc.T` (vedi `ItemContextMenu`, il menu a terra di `PlayerHud`). È lo stesso motivo per cui ogni popup deve passare da `SettingsService.ApplyToPopup` per la scala UI (skill `ui-hud` §1).

### Placeholder scritti a runtime
Una label il cui testo viene sempre riscritto dal codice (il `"100%"` degli slider, il `"100 / 100"` di `TargetDummy`) dichiara `auto_translate_mode = 2` nella scena: documenta l'intento e il check script la esenta.

### Simboli
`"X"` sul bottone di chiusura non è testo naturale: si marca la riga con `// i18n-ignore` e si mette il significato nel `TooltipText`, che invece è tradotto.

### Tasti nei prompt
**Mai scrivere la lettera nel testo.** `Loc.KeyFor("interact")` la legge dall'InputMap: il giorno in cui un tasto cambia, i prompt seguono senza toccare le traduzioni. La chiave contiene il segnaposto (`"{0}   [{1}]"`).

### Messaggi di sola console
`GD.Print`, `GD.PrintErr`, `GD.PushWarning` **non sono UI**: restano in italiano, sono per chi sviluppa. Il check script li ignora.

### Errori di rete
`NetworkManager` traduce con le chiavi `NET_ERR_*` **prima** di chiamare `Fail(...)`. La firma di `Fail(string)` non cambia: riceve un messaggio già pronto, che `MainMenu` incornicia in `UI_MENU_STATUS_ERROR`.

---

## 5. Cambio lingua a runtime

`SettingsService` è l'unico proprietario (come per scala UI, audio, finestra):

- `Language` vale `"system"` (segue `OS.GetLocaleLanguage()`), `"it"` o `"en"`;
- `ApplyLocale()` è **la prima** cosa che fa `ApplyAll()`, così ciò che viene mostrato dopo è già nella lingua giusta;
- persistita in `user://settings.cfg`, sezione `[locale]`;
- emette `LanguageChanged` per chi non è un `Control`.

Godot notifica tutti i nodi con `NOTIFICATION_TRANSLATION_CHANGED`. I testi statici si aggiornano da soli; **quelli composti no**. Chi ne ha li rigenera:

```csharp
public override void _Notification(int what)
{
    if (what == NotificationTranslationChanged)
        Refresh();
}
```

Già fatto in `InventoryScreen` (che chiama `Rebuild()`), `MainMenu` (riga di stato), `ItemPickup` (prompt 3D), `PauseMenu` (voce "Sistema" dell'elenco lingue). `WeaponHudPanel` non serve: riscrive tutto ogni frame in `_Process`. `ItemContextMenu` e `InspectWindow` nemmeno: sono ricostruiti a ogni apertura.

> **Una Label che mostra testo già tradotto non può ritradursi da sé.** Chi deve poterla aggiornare conserva **chiave e argomenti**, non il risultato (vedi `MainMenu._statusKey`).

Le voci dell'elenco lingue sono **endonimi** (`Italiano`, `English`), non tradotte: chi ha avviato per sbaglio in una lingua che non conosce deve ritrovare la propria.

---

## 6. Controllo automatico

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools\check-i18n.ps1
```

Da lanciare **prima di considerare conclusa una fase** e dopo aver aggiunto UI o oggetti (nessuna CI in questa fase, CLAUDE.md §6). Tre famiglie di segnalazioni:

- **LETTERALI** — testo naturale assegnato a `.Text`, `.TooltipText`, `.PlaceholderText`, `.Title`, o passato a `AddItem`/`SetItemTooltip`; testo non-chiave nelle scene.
- **MANCANTI** — chiave usata dal codice o da una scena ma assente dai cataloghi.
- **INCOMPLETE** — riga di CSV con una colonna di lingua vuota, chiave duplicata, o chiave mai referenziata.

Le chiavi generate per convenzione (`ITEM_`, `SLOT_`, `CATEGORY_`, `UI_MOUSE_`, `DLG_`) non compaiono come letterali nel codice: lo script le esenta dal controllo "mai referenziata". **Se introduci una nuova famiglia costruita a runtime, aggiungi il prefisso a `$conventionPrefixes`**, altrimenti verrà segnalata come orfana.

Lo script è in **ASCII puro** di proposito: Windows PowerShell 5.1 legge gli script senza BOM come ANSI, e un accento nel sorgente diventa un errore di parsing.

---

## 7. Stato attuale e lacune volute

- **Nessun sistema di dialogo.** I `.po` contengono tre voci `DLG_SAMPLE_*` di esempio, che validano il flusso end-to-end ma non sono usate da nessuna UI. Il runtime (NPC, scelte, avanzamento) arriverà con le quest, e riuserà questi file senza rework di i18n.
- **Nessun plurale in uso.** `Loc.N` esiste ed è corretto, ma richiede chiavi con `msgid_plural` in un `.po`; nessuna stringa attuale ne ha bisogno. Non risolvere i plurali con un `if (n == 1)` nel codice: le regole di pluralità non coincidono fra lingue.
- **Nessuna traduzione dei nomi delle azioni di input**: non esiste ancora una schermata di rimappatura tasti.
- `Loc.KeyFor` copre tastiera e mouse; **non copre il gamepad** (nessun supporto controller nel progetto).
