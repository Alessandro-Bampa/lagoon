---
name: ai-npc
description: Personaggi non giocanti umani. Carica quando tocchi ai/, NpcController, NpcAnimationBridge, NpcCharacter.tscn, quando aggiungi comportamenti a un NPC, quando un NPC resta immobile o non si anima, o quando lavori su NavigationRegion3D e navmesh.
---

# NPC

`ai/`. Movimento in `core/Motion/CharacterMotor.cs`, animazione in `animation/`, salute e hitbox in `combat/`.

## Cosa c'è

Un umano che **cammina fra waypoint** con lo stesso corpo, rig e layer procedurali del giocatore, più
`HealthComponent` e `HitboxComponent`. **Nessuna IA** (percezione, inseguimento, tiro): è un prototipo
a sé. Serve a collaudare che `CharacterMotor` sia davvero condiviso (`NpcController` non duplica nulla)
e a guardare l'animazione da fuori.

## Autorità: host

Un NPC non ha input: lo calcola l'host e lo replica. `SetMultiplayerAuthority(HostPeerId)` in
`_EnterTree` (non `_Ready`: il Synchronizer figlio eredita all'ingresso nell'albero). **In `ai/` non
esiste nessuna RPC `AnyPeer`**: un'eventuale richiesta di un giocatore va sul nodo del giocatore.

Replica: come il giocatore meno `SyncAnchorId`. `SyncAimYaw/Pitch/Aiming` ci sono già per gli NPC
armati; oggi `SyncAimYaw = SyncFacing`. Corpo su layer 3 `enemies`, mask 1.

## Navigazione

`NavigationAgent3D` con **ripiego a guida diretta** verso il waypoint: senza navmesh l'NPC resterebbe
immobile, sintomo che si scambia per un bug d'animazione. La condizione del ripiego **non** può essere
il solo `IsNavigationFinished()`: senza navmesh l'agente non dichiara mai finito e
`GetNextPathPosition()` restituisce la posizione corrente. Si guarda anche se il punto di rotta coincide
con dove si è. `TestLevel` non ha ancora un `NavigationRegion3D`.

`Waypoints` in coordinate **locali allo spawn**; array vuoto = NPC fermo.

## Animazione

`NpcAnimationBridge` è il gemello di `PlayerAnimationBridge` e legge solo stato replicato. Le velocità
(`WalkSpeed`…) vanno **prese dal controller**, non ridichiarate: definiscono i blend space e se divergono
si finisce in T-pose senza errori. Per armare un NPC: `WeaponPose` nel bridge + `SyncAimYaw`,
`SyncAimPitch`, `SyncAiming` dal controller.

## Verifica

`tools/verify_animation_runtime.gd` (NPC sullo stesso rig, rampa senza navmesh). A mano: `NpcWalker` nel
`TestLevel` deve muoversi identico su host e client.

## IA futura

Non dentro `NpcController`: percezione e stati in nodi separati sotto `ai/` che gli scrivono la
destinazione, come `PlayerInput` pilota `PlayerController`. Addon disponibili: `godot_state_charts`, `limboai`.
