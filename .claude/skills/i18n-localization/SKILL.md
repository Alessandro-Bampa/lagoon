---
name: i18n-localization
description: Traduzione delle stringhe (IT/EN, fallback EN). Carica quando tocchi locales/, aggiungi o modifichi un testo visibile all'utente, crei un item o un pannello di UI, aggiungi un'azione di input, lavori su Loc o sul selettore Lingua, o compaiono chiavi grezze a video (UI_..., ITEM_...) o il warning "[Loc] Chiave di traduzione mancante".
---

# Internazionalizzazione

**Nel codice non si scrive mai testo naturale**: solo chiavi. Fallback inglese, quindi una traduzione
italiana mancante degrada ma non rompe.

## 1. File

| | Contenuto | Caricamento |
|---|---|---|
| `locales/ui.csv`, `items.csv` | stringhe brevi | importati → `<nome>.<locale>.translation` accanto al CSV, **committati** |
| `locales/dialogue/*.po` | testi lunghi, plurali | nativi, nessun import |

> **Dopo aver toccato un `.csv` serve il reimport**, o il `.translation` resta vecchio:
> `Godot_v4.7.1-stable_mono_win64_console.exe --path . --headless --import`

Il fallback `en` è il default di Godot e l'editor non lo serializza in `project.godot`: non riscriverlo.

## 2. Chiavi

`AMBITO_SOTTOAMBITO_NOME`, ASCII maiuscolo, **sempre almeno un underscore**.

| Prefisso | Ambito |
|---|---|
| `UI_MENU_` · `UI_PAUSE_` · `UI_SETTINGS_` · `UI_BIND_` | menu, pausa, impostazioni, rimappatura |
| `UI_INV_` · `UI_INSPECT_` · `UI_HUD_` | inventario, HUD, prompt nel mondo |
| `NET_ERR_` | errori di rete (tradotti da `NetworkManager` prima di `Fail`) |
| `UI_ACTION_<AZIONE>` | azioni dell'InputMap (`InputBindings.LabelKey`) |
| `ITEM_<ITEMID>_NAME` / `_DESC` / `_EFFECT` | oggetti: derivate dall'`ItemId`, `_DESC`/`_EFFECT` opzionali |
| `SLOT_` · `CATEGORY_` · `UI_MOUSE_` | derivate dal nome di un valore di enum |
| `DLG_` | dialoghi, solo nei `.po` |

Aggiungere un item, un valore di enum o un'azione riassegnabile = **aggiungere la riga al CSV**.
`ItemDatabase` segnala all'avvio gli item senza `_NAME`; le azioni dimenticate mostrano la chiave grezza.

## 3. Come si traduce

| Caso | Regola |
|---|---|
| Testo statico (scena o `Control` da codice) | la **chiave** in `text`: ci pensa l'auto-translate |
| Testo composto o dinamico | `Loc.T("CHIAVE", args)` **e** `AutoTranslateMode = Disabled` |
| Segnaposto | `{0}`, `{1}` nel valore; mai concatenare. I numeri passano da `Loc.Num` (cultura invariante) |
| `PopupMenu` / `Window` | non passano dall'auto-translate: `Loc.T` a mano |
| Label riscritta ogni volta dal codice | `auto_translate_mode = 2` nella scena |
| Simboli (`"X"`) | `// i18n-ignore` sulla riga, significato nel `TooltipText` |
| Tasti nei prompt | `Loc.KeyFor("azione")`, mai la lettera nel testo (copre tastiera e mouse, non il gamepad) |
| `GD.Print*` | non è UI: resta in italiano |

`DisplayName` di un item è una **proprietà calcolata**: non memorizzarla, cambia con la lingua.

## 4. Cambio lingua

`SettingsService.Language` (`"system"`, `"it"`, `"en"`), applicata per prima in `ApplyAll()`, salvata in
`[locale]`. I testi statici si aggiornano da soli; **quelli composti no**: chi ne ha li rigenera in
`_Notification(NotificationTranslationChanged)` e per poterlo fare conserva **chiave e argomenti**, non il
risultato. Le voci dell'elenco lingue sono endonimi, non tradotte.

## 5. Controllo

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools\check-i18n.ps1
```

Segnala LETTERALI, chiavi MANCANTI, righe INCOMPLETE/duplicate/mai referenziate. Le segnalazioni sotto
`demo/` sono di scene di terze parti: ignorarle. Una nuova famiglia di chiavi costruite a runtime va
aggiunta a `$conventionPrefixes`. Lo script è ASCII puro di proposito (PowerShell 5.1).

## 6. Lacune volute

Nessun sistema di dialogo (i `.po` hanno tre voci `DLG_SAMPLE_*` di prova). Nessun plurale in uso: `Loc.N`
esiste e richiede `msgid_plural`; mai risolvere i plurali con `if (n == 1)`.
