# Lagoon — guida per Codex

## Progetto e stack

RPG open world cooperativo isometrico, survival/action, con inventario a griglia. Il target attuale è desktop Windows/Linux, fino a quattro giocatori, con architettura listen-server.

- Godot 4.7.1 .NET/Mono, C# e .NET 8, renderer Forward+ e Jolt Physics.
- La logica di gameplay è C#. Usa GDScript solo per tooling/editor quando è realmente più adatto.
- `GodotSteam` è late-bound in `NetworkManager`: il progetto deve compilare e funzionare in `LocalEnet` anche senza le librerie Steam native.
- Cartelle verticali per sistema (`combat/`, `inventory/`, `animation/`, ecc.). Non creare una cartella globale di script.

## Contratto di rete

Lo stato di gioco è deciso dall'host. Una RPC `AnyPeer` trasporta un intento e l'host valida mittente, distanza, cooldown e plausibilità prima di calcolare il risultato. Danno, inventario, NPC, veicoli e salute non sono mai decisi dal client. Replica stato continuo con `MultiplayerSynchronizer`; usa RPC authority per eventi cosmetici o geometrici.

Eccezione esistente: il movimento del player è calcolato dal peer proprietario e replicato. Non estendere questa eccezione ad altri sistemi. Nei calcoli host-side usa `PlayerController.ResolvedSyncPosition`, non `GlobalPosition` né `SyncPosition`: su una barca `SyncPosition` è locale all'ancora indicata da `SyncAnchorId`.

I componenti host-authoritative sotto `Player` impostano l'autorità in `_EnterTree`; il relativo `MultiplayerSynchronizer` deve essere un loro figlio, così eredita l'autorità corretta.

## Convenzioni

- C#: `PascalCase` per tipi e membri pubblici, `_camelCase` per campi privati, un tipo principale per file e nome file uguale al tipo.
- Commenti, log di sviluppo e documentazione di progetto in italiano.
- Recupera i nodi tipizzati in `_Ready`; evita lookup ripetuti nel runtime.
- Stato globale tramite Autoload Godot, non singleton statici C#.
- `resources/` contiene dati (`Resource`/`.tres`), non logica con side effect.
- Non aggiungere codice speculativo in `quests/` o `building/` finché il relativo prototipo non viene richiesto.
- Le stringhe visibili passano da `locales/` e `Loc`; i messaggi di sola console possono restare letterali.
- Gli enum serializzati o salvati nei `.tres` si estendono in coda: non riordinare né rinumerare i valori esistenti.
- Se cambi collision o render layer, aggiorna sia le costanti in `core/Utils/` sia i letterali nelle scene e `[layer_names]` in `project.godot`.

## File generati

Non modificare direttamente questi output:

- `animation/resources/CharacterBlendTree.tres` → `tools/build_animation_tree.gd`
- `animation/resources/WeaponHoldPoses.tres` → `tools/build_weapon_poses.gd`
- `animation/resources/AdditiveClips.tres` → `tools/build_additive_clips.gd`
- `animation/resources/CrouchClips.tres` → `tools/build_crouch_clips.gd`
- asset personaggio/animazioni Blender → script in `tools/blender/`

Modifica la sorgente e rigenera. Non sovrascrivere un `.glb` di animazioni se il log del builder riporta clip `lost`.

## Verifica

Scegli i controlli proporzionati all'ambito:

```powershell
dotnet build Lagoon.csproj
powershell -NoProfile -ExecutionPolicy Bypass -File tools\check-i18n.ps1
Godot --path . --headless --script tools/verify_godot_import.gd
Godot --path . --headless --script tools/verify_animation_runtime.gd
```

Il binario Godot può avere un nome/path diverso: individua prima l'eseguibile 4.7.x Mono disponibile. Per modifiche multiplayer, verifica anche due istanze con `LocalEnet`; non introdurre una CI o un framework di test generale senza richiesta.

## Skill di progetto

Le skill sono in `.agents/skills/` e vanno caricate solo per l'ambito pertinente:

- `character-animation`: rig, clip, AnimationTree, IK, parkour e refactor del sistema animazioni.
- `blender-pipeline`: modelli, rig, import/export glTF/Mixamo e Blender MCP.
- `combat-shooting`, `inventory-tarkov`, `vehicles-boats`, `ai-npc`: rispettivi sistemi gameplay.
- `vision-fog`, `building-cutaway`: visibilità, shader, camera e render layer.
- `ui-hud`, `i18n-localization`: UI, input e testi visibili.
- `steam-networking`: trasporti, lobby e addon Steam.

Prima di documentare una decisione, preferisci codice e test come fonte di verità. Aggiorna `AGENTS.md` solo per invarianti trasversali; le decisioni di un singolo sistema appartengono alla relativa skill.
