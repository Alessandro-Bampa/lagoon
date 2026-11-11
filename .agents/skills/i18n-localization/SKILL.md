---
name: i18n-localization
description: Aggiunge o modifica testi visibili, cataloghi IT/EN, chiavi item, cambio lingua e controlli i18n.
---

# Localizzazione

- Nessun testo naturale visibile nel codice. Usa chiavi `SCREAMING_SNAKE_CASE` in `locales/`; log di sviluppo esclusi.
- CSV per UI/item brevi, PO per dialoghi lunghi. Inglese è fallback.
- Dopo un CSV esegui il reimport Godot; i `.translation` generati sono versionati.
- Gli item derivano `ITEM_<ITEMID>_NAME|DESC|EFFECT` da `ItemId`; i `.tres` non contengono nomi localizzati.
- Testi statici dei `Control`: assegna la chiave e lascia auto-translate. Testi composti: `Loc.T`, `Loc.Num` e `AutoTranslateMode.Disabled`.
- `PopupMenu` e `Window` richiedono traduzione esplicita. I prompt dei tasti usano `Loc.KeyFor(action)`, mai una lettera hardcoded.
- Chi genera testo dinamico conserva chiave e argomenti e si aggiorna su `NotificationTranslationChanged`.

Verifica sempre con:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\check-i18n.ps1
```

Se introduci una nuova famiglia di chiavi costruita a runtime, aggiorna anche le convenzioni del checker.
