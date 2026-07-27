---
name: character-animation
description: Sistema di animazione del personaggio — AnimationTree a layer, BlendSpace, one-shot, aggancio dell'arma alla mano, IK e procedurale. Carica questa skill quando tocchi animation/, il CharacterRig, il BlendTree, CharacterAnimator, PlayerAnimationBridge, TwoBoneIkModifier, WeaponGripRig, WeaponAnimationSet, quando aggiungi o rinomini una clip, quando il personaggio va in T-pose o le animazioni si fermano, quando agganci un'arma alla mano, o quando lavori su rinculo, mira verticale, piedi a terra, scavalcamento o ragdoll.
---

# Animazione del personaggio

Ambito: `animation/`, piu' `player/scripts/PlayerAnimationBridge.cs` e i due tool di verifica.
Le clip e il rig vengono da `blender-pipeline`; le armi da `combat-shooting`.

## 1. Le tre trappole mute

Sono i tre modi in cui questo sistema si rompe **senza un solo errore a runtime**. Tutti e tre
sono gia' costati una sessione di debug: prima di ipotizzare qualsiasi altra causa, escludi questi.

### 1.1 BlendSpace2D senza triangoli → T-pose

Un `AnimationNodeBlendSpace2D` in modalita' interpolata funziona per **triangolazione**: la posa e'
la combinazione baricentrica dei tre vertici del triangolo che contiene `blend_position`. Dove non
esiste un triangolo il nodo **non produce nulla** e lo scheletro ricade sulla rest pose — che per
`Body_Base` e' la **T-pose**.

Non e' un errore, non e' un warning, e `blend_position` esiste ed e' scrivibile lo stesso: un
controllo che verifica solo l'esistenza dei parametri **passa**.

Cause viste davvero:
- **due soli punti di blend** (erano `crouch_idle` e `crouch_fwd`): due punti sono collineari per
  definizione, zero triangoli, T-pose costante da accovacciati;
- **un punto collineare con altri due** (`run_fwd` a `(0,7)` in linea con `idle` e `walk_fwd`);
- **posizione richiesta fuori dall'inviluppo triangolato**: con i quattro punti direzionali a
  distanza `WalkSpeed`, la regione coperta e' il **rombo** `|x| + |y| <= WalkSpeed`, non il quadrato.
  In diagonale a piena velocita' si avrebbe `|x| = |y| = WalkSpeed/√2`, somma `1.41 * WalkSpeed`:
  fuori. Per questo `CharacterAnimator.ClampToDiamond` proietta sulla palla L1 **prima** di scrivere.

Rimedi, nell'ordine: due sole clip → **`AnimationNodeBlendSpace1D`** (in 1D non c'e' triangolazione);
un asse di velocita' in piu' → **nodo separato + `Blend2`**, mai un punto collineare in piu'.

**I triangoli espliciti non si possono usare.** Con `auto_triangles = false`, `ResourceSaver`
serializza `triangles` **prima** di `blend_point_N/pos`: al caricamento `add_triangle` rifiuta ogni
indice (`Index p_x = 0 is out of bounds`) e lo spazio arriva a zero triangoli, cioe' di nuovo la
T-pose. Si usa `auto_triangles = true` su un insieme di punti ben condizionato — nel nostro caso il
rombo con `idle` strettamente dentro l'inviluppo, che obbliga Delaunay ai quattro triangoli giusti —
e ci si affida al controllo di copertura automatico.

### 1.2 `loop_mode = LOOP_NONE` → animazioni ferme

L'importatore glTF mette `loop_mode = LOOP_NONE` su tutto cio' che non ha un suffisso `-loop` nel
nome, e **le clip Mixamo non ce l'hanno**. Una clip ciclica importata cosi' parte, arriva in fondo e
**congela sull'ultimo fotogramma**: il personaggio cammina per un secondo e poi scivola nel mondo con
le gambe immobili. Il blend space funziona benissimo — e' la clip che e' finita.

Il loop **non** si imposta in Blender ne' nel `.glb`. Si imposta in
`assets/models/animations/CharacterAnimations.glb.import`, sotto `_subresources`:

```ini
_subresources={
"animations": {
"walk_fwd": {
"settings/loop_mode": 1
}
}
}
```

`0` = nessun loop, `1` = lineare, `2` = ping-pong. **Aggiungendo una clip, aggiungi la sua riga qui**
e la sua categoria in `_verify_loop_modes` (vedi §5), altrimenti la verifica fallisce apposta.

**Non chiamare una clip `*_loop`.** Con `nodes/use_name_suffixes = true` l'importatore tratta
`-loop`/`_loop` come un *suffisso di comando*: imposta il loop **e lo strippa dal nome**. Una clip
chiamata `fall_loop` arriva in Godot come `fall`, e ogni riferimento nel `.tres` e nei tool punta a
un nome che non esiste — di nuovo un blend point muto. Il progetto usa una sola via, il `loop_mode`
esplicito nel `.import`: per questo la clip di caduta si chiama `fall_idle`.

### 1.3 `AnimationNodeSync.sync = false` su un nodo filtrato → gambe congelate

`sync = false` (il default) **ferma i frame dell'ingresso con peso 0**. Su un nodo **filtrato** e' un
bug: da armato `WeaponBlend` ha peso 1, quindi la locomozione ha peso 0 — ma resta **visibile sulle
gambe**, che il filtro upper-body non copre. Senza `sync` la locomozione smetterebbe di avanzare pur
essendo a schermo.

Regola: **ogni `Blend2`/`OneShot` con `filter_enabled = true` deve avere `sync = true`.** E' l'unica
forma dell'invariante "sparare o impugnare un'arma non ferma le gambe", ed e' verificata
automaticamente.

### 1.4 Il frame in headless dura ~7 ms, non 16,7

Riguarda le sonde, non il gioco, ma fa fallire i controlli per colpa dello strumento. In
`--headless` non c'e' vsync: contare i frame per aspettare la fine di una clip da' un'attesa lunga
meno della meta' del previsto. Dove conta la durata REALE di una clip — la vita dei one-shot — si
usa `_settle_seconds()`, che accumula `get_process_delta_time()`, non `_settle()` a frame.

## 2. Struttura del BlendTree

`animation/resources/CharacterBlendTree.tres` e' **generato**, non scritto a mano:

```
Godot_console.exe --path . --headless --script tools/build_animation_tree.gd
```

Modificalo **solo** in `tools/build_animation_tree.gd`. Editare il `.tres` a mano si perde alla
rigenerazione successiva, e i path delle track (`Armature_Character/Skeleton3D:Spine`) scritti a mano
si sbagliano in silenzio: un filtro con un path errato non da' errore, semplicemente non maschera.

```
WalkSpace  ──┐                     (BlendSpace2D, rombo WalkSpeed)
             ├─ MoveBlend ──┐      (Blend2, sync — peso = banda di velocita')
RunSpace   ──┘              │      (BlendSpace2D, rombo RunSpeed)
                            ├─ CrouchBlend ──┐   (Blend2, sync)
CrouchSpace ────────────────┘                │   (BlendSpace2D, rombo CrouchSpeed)
                                             ├─ AirBlend ──┐  (Blend2, sync)
FallClip (fall_idle) ────────────────────────┘             │
                                                           │
RifleIdle  ──┐                                             │
             ├─ WeaponPose ────────────────────────────────┼─ WeaponBlend ──┐ (FILTRATO, sync)
PistolIdle ──┘   (Transition, xfade 0.15)                  │                │
                                                           │                ├─ Fire ──┐ (FILTRATO, sync)
                                              FireClip ────────────────────-┘         │
                                                                                      ├─ Jump ──┐
                                        JumpClip ── JumpScale (TimeScale) ────────────┘         │
                                                                                                ├─ Land ── output
                                                                       LandClip (land_hard) ────┘
```

I **tre spazi di locomozione sono identici a raggio diverso**: cinque punti a rombo (idle al centro
+ 4 cardinali). Un solo helper li costruisce tutti e tre, `_directional_space(idle, prefisso, raggio)`.

Perche' cosi':
- **camminata e corsa sono due spazi, non uno.** La corsa messa dentro lo spazio di camminata come
  punto in piu' sull'asse Y sarebbe collineare con `idle` e `walk_fwd` (§1.1). Separandoli, ogni
  spazio resta un rombo ben condizionato.
- **le diagonali non hanno clip proprie, e non devono averne.** Un punto a `(2.83, 2.83)` cade nel
  triangolo `idle–fwd–right` e viene sintetizzato per combinazione baricentrica. Mixamo non ha
  camminate diagonali affidabili e il blend delle cardinali e' la soluzione standard: non cercarle.
- **`WeaponPose` e' un `Transition`, non un blend space.** Fra "reggi fucile" e "reggi pistola" non
  esiste una via di mezzo sensata da interpolare.
- **`WeaponBlend` e' un `Blend2` filtrato, non un `Add2`.** Senza clip-delta un additivo sommerebbe
  due pose assolute e raddoppierebbe le trasformazioni. Qui la posa dell'arma **sostituisce**
  l'upper body e lascia intatte le gambe.
- **la maschera upper-body include le clavicole** (`LeftShoulder`/`RightShoulder`): senza, la spalla
  resterebbe alla posa di corsa e il braccio si staccherebbe visibilmente dal busto.
- **`AirBlend` sta PRIMA del layer arma**, cosi' cadendo si continua a impugnare l'arma.
- **`Land` e' l'ultimo one-shot**, su tutto il corpo, e parte solo oltre `HardLandingSpeed`.
- **`JumpScale`.** `jump` e' un arco completo di 1,03 s, ma la durata reale del volo la decidono
  `JumpVelocity` e `Gravity` (`2·v/g`, ~0,6 s con i valori attuali). Senza riscalare, il personaggio
  atterra mentre la clip e' ancora a mezz'aria.

## 3. Parametri esposti

`CharacterAnimator.cs` e' l'**unico** punto di accoppiamento fra C# e struttura dell'albero: se
rinomini un nodo, i `const` in cima a quel file vanno aggiornati (e cosi' i due tool di verifica).

| Parametro | Chi lo scrive | Significato |
|---|---|---|
| `WalkSpace/blend_position` | `CharacterAnimator` | velocita' locale (X = destra, Y = avanti) in m/s, **proiettata sul rombo** di raggio `WalkSpeed` |
| `RunSpace/blend_position` | `CharacterAnimator` | la stessa velocita', proiettata sul rombo di raggio `RunSpeed` |
| `MoveBlend/blend_amount` | `CharacterAnimator` | peso della corsa: `clamp01((‖v‖−Walk)/(Run−Walk))` |
| `CrouchSpace/blend_position` | `CharacterAnimator` | la stessa velocita', proiettata sul rombo di raggio `CrouchSpeed` |
| `AirBlend/blend_amount` | `CharacterAnimator` | in aria (da `Grounded`), smorzato |
| `Land/request` | `TriggerLand()` | solo oltre `HardLandingSpeed` |
| `CrouchBlend/blend_amount` | `CharacterAnimator` | accovacciato, smorzato |
| `WeaponBlend/blend_amount` | `CharacterAnimator` | armato, smorzato |
| `WeaponPose/transition_request` | `CharacterAnimator` | `"rifle"` / `"pistol"` |
| `Fire/request` · `Jump/request` | `TriggerFire()` / `TriggerJump()` | one-shot |
| `JumpScale/scale` | `TriggerJump()` | `durata clip / JumpFlightTime` |

La stessa velocita' va scritta in **tutti e tre** gli spazi, ognuno proiettato sul proprio rombo:
sono alternativi fra loro, e chi ha peso 0 continua comunque ad avanzare (`sync`) per non ripartire
da un tempo vecchio quando torna in scena.

Il peso della corsa e' una pura banda di velocita'. Il "fattore di avantezza" che c'era prima —
serviva a non mostrare la corsa frontale mentre si andava di lato — **non serve piu'**, ora che
`RunSpace` ha tutti e quattro gli assi.

Lo smorzamento e' **esponenziale** (`1 − exp(−k·dt)`), non `clamp(k·dt)`. Conta perche'
`CharacterAnimator` gira in `_Process` (frame di render) mentre il movimento che lo alimenta gira in
`_PhysicsProcess` (tick fisso): i due passi non coincidono quasi mai, e con la forma ingenua la
locomozione risulterebbe piu' o meno reattiva a seconda del frame rate.

## 4. Chi parla con chi

```
PlayerController  ──(stato replicato)──>  PlayerAnimationBridge  ──>  CharacterAnimator  ──>  AnimationTree
                                                                      WeaponGripRig      ──>  BoneAttachment3D + IK
```

**`CharacterAnimator` e' un ricevitore puro** e va tenuto tale: non conosce `PlayerController`, non
interroga il `Multiplayer`, non valida niente. E' cosi' che lo stesso rig servira' agli NPC senza che
`animation/` dipenda da `player/`. Chi lo pilota scrive nelle proprieta' pubbliche e chiama i
`Trigger*`.

**`PlayerAnimationBridge` legge SOLO stato gia' replicato** (`SyncLocalVelocity`, `SyncCrouching`,
`SyncGrounded`, `HeldItemId`) e gira identico sul peer proprietario e su quelli remoti — e' questo
che rende gli avatar remoti coerenti **senza inviare un solo dato in piu' per l'animazione**. Non
aggiungere proprieta' al `SceneReplicationConfig` per l'animazione: se serve uno stato nuovo, quasi
sempre si deriva da quelli esistenti.

Gli eventi one-shot (`Jumped`, `Landed`, `ShotResolved`) arrivano da RPC che ogni peer riemette come
segnale **locale**: il bridge ascolta e basta. Nel payload non viaggia mai un esito di gioco
(CLAUDE.md §3) — `Landed` porta la velocita' d'impatto, che e' una grandezza fisica e decide solo
quanto flette il bacino.

Tutto il contenuto di `animation/` e' **pura resa**: gira su ogni peer a partire da stato gia'
replicato, non produce stato di gioco, non va replicato e non richiede autorita'. Non metterci
validazione.

## 5. Verifiche automatiche

Due tool, entrambi da lanciare dopo ogni modifica al rig o alle clip:

```
Godot_console.exe --path . --headless --script tools/verify_godot_import.gd       # struttura
Godot_console.exe --path . --headless --script tools/verify_animation_runtime.gd  # comportamento
```

`verify_godot_import.gd` controlla scheletro, skin, scala, track, **`loop_mode` per categoria**,
esistenza dei parametri, **copertura a triangoli di ogni BlendSpace2D** (campiona il rombo e pretende
che ogni campione cada in un triangolo) e **`sync` su ogni nodo filtrato**.

`verify_animation_runtime.gd` fa girare davvero l'albero e misura le ossa: **ventuno** combinazioni
di locomozione/corsa/crouch/aria/arma che **non devono essere la T-pose** (tutti e quattro gli assi
per ciascuno spazio, piu' le diagonali), dieci secondi di camminata che non deve congelarsi, una
raffica che non deve fermare le gambe, la caduta che deve ciclare, i one-shot di salto e atterraggio
che devono rientrare, e l'aggancio dell'arma alla mano.

Gli errori GodotSteam nelle run headless sono attesi e benigni.

## 6. Aggiungere un'arma senza toccare la locomozione

1. Crea (o riusa) un `WeaponAnimationSet` in `animation/resources/`. Ne bastano due —
   `two_handed.tres` e `one_handed.tres` — perche' ogni fucile impugna come un fucile.
2. Referenzialo da `WeaponDefinition.AnimationSet` nel `.tres` dell'arma.
3. Fine. **Non toccare l'albero, non aggiungere clip, non toccare `CharacterAnimator`.**

`WeaponAnimationSet` porta posa (`HoldPose`, `FirePose`, `IsTwoHanded`), presa (`GripOffset`,
`GripRotationDegrees`, `SupportGripOffset`) e rinculo (`RecoilKickBack`, `RecoilKickUpDegrees`,
`RecoilRecoverySpeed`). `WeaponGripRig` legge solo quello.

Il vincolo di budget e' **≤ 30 clip totali**: mai una clip per combinazione arma × movimento. Se
un'arma "richiede" una clip di locomozione nuova, la risposta e' quasi sempre una posa upper-body in
piu' sul layer arma.

## 7. Aggancio dell'arma alla mano

`WeaponGripRig` (nodo `GripRig` in `CharacterRig.tscn`) crea **da codice**, in `_Ready`:
un `BoneAttachment3D` su `RightHand`, un `GripPoint` sotto di esso, il bersaglio della mano di
supporto e il modificatore IK.

Da codice e non nella scena perche' il rig arriva da `Body_Base.glb`, che si rigenera: figli aggiunti
a mano dentro una scena istanziata da un `.glb` si perdono o si sdoppiano al reimport. Costruirli in
`_Ready` li lega ai **nomi** dei bone, che sono stabili.

`WeaponVisual` (in `Player.tscn`, sotto `Visual/WeaponMount`) ricalca ogni frame la trasformata del
`GripPoint` quando il rig c'e', e ricade sul vecchio offset fisso quando non c'e' — un bersaglio di
prova senza rig deve comunque mostrare l'arma. Il rinculo passa da `PlayRecoil()` sul rig, che
conosce i valori dichiarati dall'arma; `KickDistance`/`KickRecoverySpeed` su `WeaponVisual` restano
solo per il caso senza rig.

**Trappola.** `TwoBoneIkModifier.TargetPath` viene risolto in `_Ready`. Chi costruisce il rig da
codice deve usare `TargetNode`, non `TargetPath`: un `NodePath` si puo' calcolare solo quando i due
nodi sono gia' nell'albero, cioe' **dopo** `_Ready`, e a quel punto assegnarlo non ha piu' effetto —
l'IK resta spento in silenzio.

## 8. Lacune volute e lavoro aperto

Sono scelte, non dimenticanze. Non "sistemarle" senza leggere il motivo.

**Le 20 clip attuali coprono tutti e quattro gli assi** per camminata, corsa e crouch, piu' idle
neutra, caduta, salto, atterraggio duro e le pose d'arma. **Mancano ancora** (porterebbero a ~26,
dentro il budget di 30): scavalcamento, 4 reazioni direzionali ai colpi, posa iniziale di morte.

Procedura per aggiungerne una: scaricare da Mixamo (*FBX Binary*, *Without Skin*, *In Place* dove
l'opzione esiste) → riga in `CLIPS` di `tools/blender/build_animation_library.py` → rigenerare la
libreria → `loop_mode` nel `.import` (§1.2) → categoria in `_verify_loop_modes` → rigenerare
l'albero → lanciare le due suite.

*Se la spunta "In Place" non c'e' su Mixamo*, di norma e' perche' la clip non trasla e va bene
com'e'. Se invece finisce in `skipped` nel log del build, aggiungila a `FORCE_IN_PLACE`: la pipeline
la appiattisce con `mx.flatten_root_motion()` invece di scartarla, azzerando la traslazione
orizzontale del bacino e lasciando la verticale. E' quello che si fa per `land_hard`, che avanza di
34 cm. Limite: toglie la traslazione, non la rotazione, quindi su una clip che curva resta un po' di
deriva angolare.

*Trappola dell'asse*: il controllo "in place" misura la traslazione **orizzontale** del bacino, che
nello spazio locale dell'osso `Hips` e' su **X e Z**, non su Y — l'asse Y corre lungo l'osso.
Confonderli fa scartare come root motion qualunque salto, o appiattire l'arco verticale.

*Trappola del modulo*: Blender resta aperto fra un'esecuzione e l'altra e tiene i moduli in
`sys.modules`. Senza il `importlib.reload(mx)` in cima a `build_animation_library.py`, una modifica a
`mixamo_common.py` verrebbe **ignorata** e si continuerebbe a girare con la versione vecchia, senza
il minimo segnale.

**Atterraggio a due regimi.** Oltre `HardLandingSpeed` parte la clip `land_hard`; sotto, resta la
sola flessione procedurale del bacino, proporzionale all'impatto e con rientro esponenziale. Sono
**alternativi**, non sommati: la clip contiene gia' la propria flessione, e sovrapporle farebbe
sprofondare il personaggio nel pavimento. Manca ancora la clip di atterraggio morbido.

**`TwoBoneIkModifier` non applica ancora la posa — non attivarlo.** Il modificatore **viene
eseguito** (verificato: 31 chiamate su 31 frame, con scheletro, bersaglio e indici di osso tutti
risolti) e la soluzione trigonometrica gira, ma la posa scritta non arriva allo scheletro: lo
spostamento misurato dell'estremita' e' di **0,002 m** verso un bersaglio distante **0,111 m**.
Provate e scartate due vie di scrittura, `SetBoneGlobalPose` e la conversione in posa locale con
`SetBonePosePosition`/`SetBonePoseRotation`. Resta da capire l'ordine esatto fra `AnimationMixer`,
modificatori e ricomposizione delle pose in 4.7 — probabilmente le pose di partenza vanno lette in
un altro momento del ciclo. Per questo `WeaponGripRig.EnableSupportHandIk` e' **false**: acceso e non
funzionante sarebbe peggio che assente, perche' sembrerebbe fatto. **L'aggancio dell'arma alla mano
destra non dipende da questo** ed e' verificato funzionante.

**Fase D non conclusa.** Restano, tutte bloccate a valle dell'IK: piedi a terra (raycast per piede +
IK + abbassamento bacino, con limite di pendenza da dichiarare), mira verticale procedurale
(rotazione di `Spine1`/`Spine2`/`Head` — **non** pose di aim offset: la camera e' ortogonale a
inclinazione fissa, pitch 40°, quindi il pitch reale varia di pochi gradi e lo yaw fra corpo e mira
e' ~0 per costruzione, visto che da armato l'avatar si orienta verso il cursore; una griglia di pose
interpolerebbe un intervallo che qui quasi non esiste), e il **motion warping** per lo scavalcamento
(Godot non ha un nodo nativo: `ShapeCast3D` misura altezza e bordo, il codice deforma la traiettoria
della radice su una curva normalizzata perche' l'atterraggio cada sul bordo misurato, mani in IK sul
bordo durante la finestra di contatto — **una sola clip generica**, mai una per altezza, intervallo
dichiarato 0,5–1,2 m).

**Fase E non iniziata.** Danno direzionale e morte. Il contratto e' gia' deciso:
`HealthComponent.ApplyDamage` acquisisce la **direzione** del colpo, calcolata host-side in
`WeaponController.RequestFire` che ha gia' `shotDir`; RPC one-shot separate dalla validazione,
`BroadcastHitReaction(Vector3 direction)` e `BroadcastDeath(Vector3 impulse)`, sul modello di
`WeaponController.BroadcastShot`; **nessun ammontare di danno nel payload** — una RPC `AnyPeer`
accetta un intento, non un risultato (CLAUDE.md §3). Morte: disattivare l'`AnimationTree`, attivare
`PhysicalBoneSimulator3D`, applicare l'impulso; **nessun bone sincronizzato via rete**, si replica
solo l'evento morte piu' l'impulso iniziale. Nota onesta: `PhysicalBoneSimulator3D` **non espone un
peso di blend** animato→fisico — la transizione senza scatti si ottiene inizializzando i
`PhysicalBone3D` sulla posa animata corrente con le velocita' del personaggio. Serve anche generare
la catena di physical bone per `Body_Base`, che **non esiste ancora**.

**Fase di camminata e corsa non allineate.** `walk_fwd` dura 1,067 s e `run_fwd` 0,667 s: nel
crossfade fra i due i piedi possono risultare fuori fase per un istante. `sync = true` li fa avanzare
entrambi ma **non** li mette in fase — servirebbe un ritaglio delle clip. Accettato. Lo stesso vale
dentro ogni spazio: `walk_back` dura 1,233 s contro gli 1,067 s degli altri tre assi, quindi le
diagonali che coinvolgono l'indietro sono le meno pulite.

**`WeaponGripRig` cerca lo Skeleton3D scendendo nell'albero dei fratelli.** Se un giorno un avatar
avesse due scheletri, prende il primo.
