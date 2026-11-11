---
name: steam-networking
description: Gestisce GodotSteam, binding C#, lobby, trasporto Steam/LocalEnet e problemi di GDExtension.
---

# Steam e trasporti

- `NetworkManager` è l'unico punto che parla col trasporto; gameplay e replica usano l'API multiplayer Godot.
- Mantieni late-bound `Engine.GetSingleton("Steam")`/`ClassDB.Instantiate`: non introdurre dipendenze compile-time dai binding.
- `Steam` è il trasporto di produzione; `LocalEnet` su localhost è il percorso di test multi-istanza.
- Versioni installate: GodotSteam GDExtension 4.20.1 per Godot 4.7 e binding C# 1.1.0. Verifica compatibilità prima di aggiornare engine, estensione o binding.
- `steam_appid.txt` locale può usare 480 e resta ignorato da git.
- Spawn/despawn player passano da eventi host di `NetworkManager` a `GameWorld`.
- Host migration e ritorno pulito al menu non sono implementati; non simulare una soluzione parziale.

Gli errori di caricamento delle librerie Steam native possono essere benigni in un ambiente senza addon funzionante: conferma prima che `dotnet build` e `LocalEnet` siano realmente rotti.
