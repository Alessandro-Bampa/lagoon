---
name: ai-npc
description: Modifica o diagnostica NPC umani, navigazione, waypoint, replica host e collegamento al rig condiviso in ai/.
---

# NPC umani

Lavora su `ai/` tenendo separati movimento, decisione e resa.

## Contratti

- `NpcController` eredita da `CharacterMotor`: non duplicare gravità, pendenze, parkour o locomozione.
- Gli NPC sono host-authoritative. Imposta l'autorità in `_EnterTree`; non aggiungere RPC `AnyPeer` direttamente all'NPC senza un nuovo caso d'uso validato.
- I waypoint sono locali allo spawn. Mantieni il fallback di guida diretta quando `NavigationAgent3D` non restituisce un prossimo punto utile: `TestLevel` può funzionare senza navmesh.
- `NpcAnimationBridge` pilota lo stesso `CharacterAnimator` del player usando solo stato già replicato. `animation/` non deve dipendere da `player/`.
- Stato attuale: walker con waypoint, salute e hitbox. Percezione, combattimento e behavior tree/state chart non sono ancora implementati.

Quando aggiungi IA reale, crea componenti di percezione/decisione separati che pilotano il controller; valuta `godot_state_charts` solo quando serve un comportamento concreto.

## Verifica

- Esegui `dotnet build Lagoon.csproj`.
- Per modifiche a movimento o animazione esegui `tools/verify_animation_runtime.gd`: copre rig condiviso, assi locali, turn-in-place e camminata su pendenza senza navmesh.
- In due istanze `LocalEnet`, l'NPC deve avere la stessa traiettoria e posa su host e client.
