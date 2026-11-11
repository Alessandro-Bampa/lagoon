---
name: steam-networking
description: Integrazione GodotSteam, binding C#, trasporti e lobby. Carica quando tocchi NetworkManager, addons/godotsteam*, steam_appid.txt, la creazione o l'ingresso in una lobby, il trasporto ENet locale, o compaiono errori di GDExtension o "Nonexistent function" legati a Steam. Non serve per autorità, RPC e replica: quelle sono CLAUDE.md §3.
---

# GodotSteam + C#

- **Versioni**: GodotSteam GDExtension 4.20.1 (variante Godot 4.7) + binding
  `LauraWebdev/GodotSteam_CSharpBindings` 1.1.0 (generati contro la 4.6.1: differenze quasi sempre
  additive, da verificare a runtime). Installazione in `addons/README-STEAM.md`. Aggiornando Godot,
  GodotSteam o i binding, controlla prima la compatibilità degli altri due.
- **`NetworkManager` è late-bound** (`Engine.GetSingleton("Steam")`,
  `ClassDB.Instantiate("SteamMultiplayerPeer")`, nomi di metodi e segnali come costanti). **Non
  sostituirlo con riferimenti tipizzati ai binding**: la build fallirebbe su ogni macchina senza addon.
- Test senza AppID reale: `steam_appid.txt` = **480** (Spacewar), con il client Steam in esecuzione.

## Trasporti

| `TransportMode` | Uso |
|---|---|
| `Steam` (0) | produzione: lobby, relay e NAT traversal automatici |
| `LocalEnet` (1) | **solo sviluppo**: ENet su `127.0.0.1:27015`, per il multi-istanza sullo stesso PC |

Il gameplay è agnostico al trasporto. Costanti in `NetworkConstants.cs`: `DefaultPort = 27015`,
`SteamAppId = 480`, `MaxPlayers = 4`, `HostPeerId = 1`.

## Peer

`NetworkManager` è l'unico a parlare col trasporto e notifica via `EventBus`: `PeerJoined`/`PeerLeft`
(**solo host**; `BecomeHost` emette `PeerJoined(HostPeerId)` per sé), `ConnectedToServer`, `NetworkError`.
`GameWorld` è l'unico consumatore di `PeerJoined`/`PeerLeft` e fa spawn e despawn degli avatar.

## Errori attesi e benigni

Senza addon (o col `.dll` placeholder `~libgodotsteam...`) a ogni avvio compaiono
`Can't open GDExtension dynamic library`, `Nonexistent function 'get_godotsteam_version'`,
`Parameter "p_control" is null (remove_control_from_bottom_panel)`. **Non vanno "aggiustati"**: il
progetto è fatto per girare senza l'addon, in `LocalEnet`.

## Limiti

Nessuna host migration: se l'host esce la sessione finisce, e "Esci dalla partita" chiude il processo.
Le due cose vanno implementate insieme.
