# Installazione GodotSteam (addon di terze parti)

Questi addon sono **binari di terze parti** e **non sono committati** nel repo: vanno scaricati e
posizionati manualmente. Devono combaciare con **Godot 4.7**.

> Nota architetturale: il gioco NON dipende dai binding C# a *compile time*. `NetworkManager.cs` chiama
> Steam in modo "late-bound" (`Engine.GetSingleton("Steam")` + `ClassDB.Instantiate("SteamMultiplayerPeer")`),
> quindi il progetto **compila anche senza questi addon** e usa il fallback ENet locale. Gli addon
> servono per attivare il trasporto Steam a runtime (e i binding C# restano utili per usare altre
> feature Steam — amici, achievement — con API C# ergonomica in futuro).

## 1. GodotSteam GDExtension (Godot 4.7)
Versione di riferimento: **v4.20.1** (Steamworks SDK 1.64), variante **Godot 4.7 / Windows**.

1. Scarica lo zip dalla Godot Asset Library (asset `2445`, "GodotSteam GDExtension 4.4+") **oppure** da
   `https://codeberg.org/godotsteam/godotsteam/releases`, scegliendo la build per **Godot 4.7 / Windows**.
2. Estrai il **contenuto dello zip nella root del progetto** (`lagoon/`). Deve risultare:
   `addons/godotsteam/` con `godotsteam.gdextension`, i `.dll` e `steam_api64.dll`.
3. Riavvia Godot: le classi `Steam` (singleton) e `SteamMultiplayerPeer` diventano disponibili.

## 2. Binding C# GodotSteam (opzionale per la Fase 1)
Versione di riferimento: **LauraWebdev/GodotSteam_CSharpBindings release 1.1.0**.

1. Scarica `godotsteam-csharpbindings-1.1.0.zip` dalle Releases del repo
   `https://github.com/LauraWebdev/GodotSteam_CSharpBindings`.
2. Estrai in modo da ottenere `addons/godotsteam_csharpbindings/` (contiene il gluecode `Steam.cs`).
3. `dotnet build Lagoon.sln` per ricompilare includendo il gluecode.

> ⚠️ **Gap di versione**: i binding 1.1.0 sono generati contro **GodotSteam 4.6.1**, mentre la
> GDExtension per 4.7 è la **4.20.1**. Le differenze sono quasi sempre additive (nuovi metodi), quindi
> init/lobby dovrebbero funzionare; verifica a runtime. Se una firma è cambiata, rigenera il gluecode
> col `godotsteam-patcher` incluso nel repo dei binding, oppure affidati all'interop già usato in
> `NetworkManager.cs` (non richiede i binding).

## 3. steam_appid.txt + client Steam
- Il file `steam_appid.txt` (root del progetto) contiene `480` (Spacewar, AppID di test Valve). È
  **gitignorato** (CLAUDE.md §10). Sostituiscilo con l'AppID reale quando disponibile.
- Il **client Steam deve essere in esecuzione e loggato** perché l'API si inizializzi.

## 4. Verifica
- Apri Godot: nessun errore di caricamento GDExtension.
- Avvia il progetto e usa **Host (Steam)** dal menu: se compare un Lobby ID, l'integrazione funziona.
- Se qualcosa non va, il menu mostra l'errore e resta comunque utilizzabile il trasporto **Locale ENet**.
