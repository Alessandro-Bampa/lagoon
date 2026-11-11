---
name: character-animation
description: Modifica o rifattorizza rig, clip, AnimationTree, bridge, IK, parkour, pose armate e layer procedurali del personaggio.
---

# Animazione del personaggio

Ambito: `animation/`, bridge player/NPC, `CharacterMotor` quando alimenta pose o parkour, tool di generazione e verifica.

## Scegli il tipo di lavoro

Per una correzione compatibile con il sistema attuale, leggi [references/current-system.md](references/current-system.md), individua la sorgente generatrice e conserva i contratti dei bridge.

Per un refactor o per aggiungere il set di animazioni mancante, non assumere che l'attuale BlendTree, i layer o Mixamo siano vincoli. Prima:

1. inventaria clip disponibili, stati gameplay e difetti visivi osservabili;
2. definisci il set desiderato e la tecnica per ogni responsabilità (clip, blend space/state machine, additive, IK/procedurale, eventuale root motion);
3. separa locomozione condivisa, postura/arma e one-shot per evitare una matrice arma × movimento;
4. preserva o migra esplicitamente il contratto `CharacterMotor → bridge → CharacterAnimator`, inclusi NPC e replica;
5. aggiorna builder, risorse e verifiche nello stesso cambiamento; rimuovi infrastruttura vecchia solo dopo equivalenza osservabile.

Valuta nuove tecniche in base a qualità, authoring ripetibile, multiplayer e costo di manutenzione. Root motion non può diventare autoritativo per accidente: se viene usato per la resa, chiarisci come resta coerente col movimento di gameplay.

## Invarianti indipendenti dall'implementazione

- `animation/` è resa pura e non dipende da `player/` o da `Multiplayer`; player e NPC usano lo stesso rig tramite bridge.
- I bridge leggono stato replicato o eventi cosmetici, non introducono RPC per scegliere pose.
- Gli assi pubblicati sono X = destra, Y = avanti. Mira e flinch usano lo stesso riferimento.
- Un solo proprietario scrive ogni trasformazione procedurale; ordina esplicitamente vincoli sovrapposti.
- Risorse generate si modificano nel builder, non nel `.tres` risultante.
- Valida la direzione della canna e i contatti reali, non solo l'esistenza dei parametri o la posizione delle mani.

## Verifica minima

```powershell
dotnet build Lagoon.csproj
Godot --path . --headless --script tools/verify_godot_import.gd
Godot --path . --headless --script tools/verify_animation_runtime.gd
```

Completa con ispezione visiva in stance, tutte le direzioni, transizioni walk/run/crouch/air, armi, mira estrema, parkour, pendenze e NPC. Un refactor è concluso solo quando i test verificano comportamento e contatti del nuovo design, non i nomi dei nodi del vecchio.
