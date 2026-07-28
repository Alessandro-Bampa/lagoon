---
name: ai-npc
description: Personaggi non giocanti umani — movimento condiviso col giocatore, navigazione, autorita' host. Carica questa skill quando tocchi ai/, NpcController, NpcAnimationBridge, NpcCharacter.tscn, quando aggiungi comportamenti a un NPC, quando un NPC resta immobile o non si anima, o quando lavori su NavigationRegion3D e navmesh.
---

# NPC

Ambito: `ai/`. Il movimento vero e' in `core/Motion/CharacterMotor.cs`; l'animazione in
`animation/` (skill `character-animation`); salute e hitbox in `combat/` (skill `combat-shooting`).

## 1. Cosa c'e' e cosa NON c'e'

C'e' un personaggio umano che **cammina fra waypoint** con lo stesso corpo, lo stesso rig, le stesse
animazioni e gli stessi layer procedurali del giocatore. Ha `HealthComponent` e `HitboxComponent`,
quindi si puo' colpire.

**Non c'e' nessuna IA**: niente percezione, niente inseguimento, niente tiro, niente stati di
allerta. E' voluto — quella e' un prototipo a se' (CLAUDE.md §4: niente codice speculativo per
sistemi non ancora prototipati). Quello che c'e' serve a due scopi concreti:

1. **collaudare che il sistema di locomozione sia davvero condiviso.** `NpcController` eredita da
   `CharacterMotor` esattamente come `PlayerController` e non duplica una riga di gravita',
   accelerazione, pendenza, gradini o accovacciamento. Se qualcuno mettesse in `CharacterMotor` una
   dipendenza da `player/`, si romperebbe qui.
2. **poter guardare l'animazione da fuori.** Sul proprio avatar, con la camera addosso, scatti e
   scivolamenti non si notano; su un personaggio che cammina a dieci metri, si'.

## 2. Autorita': host, al contrario del giocatore

Il movimento del **giocatore** e' client-autoritativo: l'input e' suo, e ogni peer replica il proprio
avatar. Un NPC non ha input da nessuna parte, quindi lo calcola l'**host** e lo replica a tutti
(CLAUDE.md §3).

```csharp
public override void _EnterTree()
{
    SetMultiplayerAuthority(NetworkConstants.HostPeerId);
}
```

In `_EnterTree` e non in `_Ready`: il `MultiplayerSynchronizer` figlio eredita l'autorita' quando
entra nell'albero, e impostarla dopo lo lascerebbe con quella sbagliata.

**Conseguenza:** in `ai/` non esiste e non deve esistere nessuna RPC `AnyPeer`. Non c'e' un client
che possa chiedere qualcosa a un NPC — se un giorno servisse (un comando di squadra, un'interazione),
quella RPC va sul nodo del GIOCATORE che la richiede, non qui.

Proprieta' replicate (`NpcCharacter.tscn`): le stesse del giocatore **meno** `SyncAnchorId` — un NPC
non sale in barca — e con `SyncAimPitch` gia' presente, perche' quando gli NPC saranno armati serve
subito e aggiungerlo dopo vorrebbe dire ritoccare la scena invece che solo il codice.

## 3. Navigazione, e il ripiego che evita un sintomo muto

`NpcController` usa un `NavigationAgent3D`, **ma con un ripiego a guida diretta**: senza rotta, punta
dritto al waypoint.

Non e' pigrizia: senza `NavigationRegion3D` nel livello l'NPC resterebbe **immobile**, un sintomo che
si scambia immediatamente per un bug dell'animazione o dello spawn. Meglio camminare in linea retta e
prendersi gli ostacoli, che almeno e' visibile e dice la verita'.

**La condizione del ripiego NON puo' essere il solo `IsNavigationFinished()`, ed e' misurato.** La
prima versione lo assumeva ("senza navmesh l'agente dichiara subito arrivato"), ed e' falso: con una
mappa **valida** ma senza navmesh l'agente non dichiara mai la rotta finita e `GetNextPathPosition()`
restituisce la **posizione corrente**, cioe' direzione nulla. Il ripiego non si agganciava mai e
l'NPC restava fermo — esattamente il sintomo muto che quel codice esiste per evitare, in silenzio da
quando e' stato scritto. Oggi la condizione guarda anche il risultato: se il punto di rotta coincide
con dove siamo gia', rotta non ce n'e'. Lo copre `_verify_slope` in `verify_animation_runtime.gd`,
che fa camminare un NPC vero in un mondo senza navmesh.

**`TestLevel.tscn` non ha ancora un `NavigationRegion3D`**, quindi `NpcWalker` cammina in linea retta.
Quando ne verra' aggiunto uno con la navmesh bakeata, l'NPC comincera' a evitare gli ostacoli senza
che serva toccare il codice.

I `Waypoints` sono in coordinate **locali al punto di spawn**, non in coordinate mondo: cosi' la
stessa scena si puo' istanziare in piu' punti del livello senza riscrivere il percorso. Array vuoto =
NPC fermo, che e' comunque un caso utile (sentinella, bersaglio).

## 4. Animazione

`NpcAnimationBridge` e' il gemello di `PlayerAnimationBridge`: riempie le stesse proprieta' dello
stesso `CharacterAnimator`. Legge solo stato gia' replicato, quindi gira identico su host e client.

Oggi lascia `WeaponPose = null` (e `SyncAiming` resta false), quindi stance armata e mira
procedurale restano spente **da sole**, senza casi particolari. La ricostruzione della direzione di
mira (`CharacterAnimator.AimVector(SyncAimYaw, SyncAimPitch)`) e la derivata del facing
(`TurnRate`) sono GIA' collegate, identiche al bridge del giocatore.

Per armare un NPC bastera' che il controller: riempia `WeaponPose` nel bridge; scriva `SyncAimYaw`,
`SyncAimPitch` e `SyncAiming` (gia' replicati in `Repl_Npc`); e usi
`CharacterMotor.PlanAimFacing(SyncFacing, aimYaw, moving)` per decidere il facing del corpo — e' lo
stesso metodo del giocatore, con la stessa zona morta e isteresi del turn-in-place. Oggi
`NpcController` tiene `SyncAimYaw = SyncFacing` (busto solidale al corpo), che e' il comportamento
giusto da disarmato.

Le velocita' (`WalkSpeed`, `RunSpeed`, `CrouchSpeed`) vanno **prese dal controller**, non
ridichiarate nel bridge: definiscono la geometria dei blend space, e se divergono la locomozione
finisce fuori dai triangoli e va in T-pose senza un solo errore (skill `character-animation` §1.4).

## 5. Verifica

Il controllo automatico sta in `tools/verify_animation_runtime.gd`, sezione *NPC sullo stesso rig*:
la scena si carica, istanzia il `CharacterRig` condiviso, non e' in T-pose, ha il proprio bridge e
i layer procedurali (`FootIk`, `SupportHandIk`) risultano costruiti anche per lui.

Test manuale: `TestLevel` contiene `NpcWalker`, che percorre un rettangolo sul pavimento. In
multi-istanza (CLAUDE.md §6) l'NPC deve muoversi **identico** sulle due finestre — se sul client
scatta o scivola, il problema e' nella replica, non nell'animazione.

## 6. Quando arrivera' l'IA vera

Non aggiungerla dentro `NpcController`, che deve restare "come si muove un NPC". La percezione, la
macchina a stati e il comportamento vanno in nodi separati sotto `ai/`, e pilotano il controller
scrivendo la destinazione — esattamente come `PlayerInput` pilota `PlayerController` senza starci
dentro. Il progetto ha gia' l'addon `godot_state_charts` disponibile, da valutare in quel momento.
