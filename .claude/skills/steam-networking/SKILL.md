---
name: steam-networking
description: Integrazione GodotSteam e binding C#, versioni compatibili, trasporti di rete e setup lobby. Carica questa skill quando tocchi NetworkManager, gli addon in addons/godotsteam*, steam_appid.txt, la creazione o l'ingresso in una lobby, il trasporto ENet locale, oppure quando compaiono errori di GDExtension o "Nonexistent function" legati a Steam. Non serve per la logica di gameplay in rete (autorità, RPC, replica): quella è CLAUDE.md §3.
---

# GodotSteam + C#

## 1. Cose da sapere prima di toccare qualcosa

- GodotSteam **non ha bindings C# ufficiali**: il supporto C# è fornito da un addon di terze parti (`GodotSteam_CSharpBindings`), compatibile con Godot 4.4+ e specifiche versioni di GodotSteam (verifica compatibilità con la versione installata prima di aggiornare l'uno o l'altro).
- Aggiornamenti di Godot, di GodotSteam e dei bindings C# **non sono garantiti sincroni**: quando aggiorni una delle tre parti, verifica changelog e issue tracker del binding C# prima di aggiornare in produzione.
- Per test locali senza un AppID Steam reale si usa `steam_appid.txt` con **480** (Spacewar, AppID di test ufficiale Valve). Serve comunque un client Steam locale in esecuzione per inizializzare l'API.
- La rete Steam (`SteamNetworkingSockets`) gestisce NAT traversal/relay automaticamente: non serve occuparsi di port-forwarding manuale come con ENet puro — è uno dei motivi per cui è stata scelta.

## 2. Versioni e approccio C# (deciso in Fase 1)

- Versioni di riferimento: **GodotSteam GDExtension 4.20.1** (Steamworks SDK 1.64, variante Godot 4.7) e **binding C# `LauraWebdev/GodotSteam_CSharpBindings` 1.1.0**. Procedura d'installazione in `addons/README-STEAM.md`.
- **Gap di versione noto**: i binding 1.1.0 sono generati contro GodotSteam 4.6.1, la GDExtension per 4.7 è la 4.20.1 (differenze quasi sempre additive — verificare a runtime).
- **`NetworkManager.cs` non dipende dai binding a compile-time**: chiama Steam in modo *late-bound* (`Engine.GetSingleton("Steam")` + `ClassDB.Instantiate("SteamMultiplayerPeer")`). Vantaggi: il progetto compila anche senza addon installati, e resta robusto al gap di versione. I nomi di metodi/segnali Steam sono isolati come costanti in `NetworkManager` per adattarli facilmente alla versione installata.

  **Non sostituire il late-binding con riferimenti tipizzati ai binding**: farebbe fallire la build su qualunque macchina senza addon e ricreerebbe l'accoppiamento alla versione.

## 3. Trasporti

`NetworkManager.TransportMode`:

| Modo | Uso | Note |
|---|---|---|
| `Steam` | Primario, di produzione | Lobby Steam, relay automatico |
| `LocalEnet` | **Solo sviluppo** | ENet su `127.0.0.1:27015`, per il test multi-istanza sullo stesso PC (CLAUDE.md §6), dato che il P2P Steam mono-macchina è scomodo |

Tutto il gameplay è **agnostico al trasporto** (lavora sull'API Multiplayer di alto livello di Godot), quindi lo switch non tocca la logica. Costanti in `core/Utils/NetworkConstants.cs`: `DefaultPort = 27015`, `SteamAppId = 480`, `MaxPlayers = 4`, `HostPeerId = 1`.

## 4. Gestione peer

`NetworkManager` è l'unico a parlare col trasporto; notifica il resto del gioco via `EventBus`:

- `PeerJoined(long peerId)` / `PeerLeft(long peerId)` — emessi **solo dall'host** (`if (Multiplayer.IsServer())`), perché solo l'host decide gli spawn. `BecomeHost` emette `PeerJoined(HostPeerId)` a mano per sé stesso.
- `ConnectedToServer()`, `NetworkError(string)`.

`GameWorld` è l'unico consumatore di `PeerJoined`/`PeerLeft` e fa lo spawn/despawn degli avatar.

## 5. Errori attesi e benigni

Se gli addon non sono installati (o il `.dll` è il placeholder `~libgodotsteam...dll`), a ogni avvio compaiono:

```
Can't open GDExtension dynamic library: 'res://addons/godotsteam/godotsteam.gdextension'
SCRIPT ERROR: Invalid call. Nonexistent function 'get_godotsteam_version' in base 'Steam'
Parameter "p_control" is null.  (remove_control_from_bottom_panel)
```

**Non sono causati da modifiche al progetto.** Il progetto è progettato per girare senza l'addon: `NetworkManager` è late-bound e il trasporto `LocalEnet` non richiede Steam. Non "aggiustarli".

## 6. Limiti noti

- **Host migration non implementata**: se l'host esce, la sessione termina per tutti. `PauseMenu` → "Esci dalla partita" chiude il processo perché non esiste una disconnessione pulita con ritorno al menu; le due cose vanno implementate insieme.
