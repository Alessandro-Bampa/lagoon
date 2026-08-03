---
name: building-cutaway
description: Camera isometrica ruotabile e cutaway degli edifici — rotazione a scatti con Q/E, piani culled quando ci si entra, muri lato camera che sfumano a puntini. Carica questa skill quando tocchi IsometricCamera, la rotazione o l'inclinazione della visuale, world/shaders/WorldSurface.gdshader, BuildingVolume, BuildingRegistry, BuildingCullController, RenderLayers, CursorMask/ShotMask, il layer fisico building_cover, il CullMask di una camera, quando aggiungi una mesh o un edificio al mondo, quando un muro non sfuma, un tetto non sparisce o una stanza resta al buio, o quando si mira al soffitto.
---

# Vedere il giocatore in isometrica

La camera è ortogonale, con **inclinazione e distanza fisse** (40° di pitch, 16 m) e **imbardata ruotabile dal giocatore** a scatti di 45° con Q/E. Senza intervento, entrare in un edificio significa guardare un tetto, e stare in una stanza significa guardare la faccia esterna di un muro.

Tre meccanismi, e solo tre. Tenerli distinti è la cosa più importante di questa skill.

| | rotazione | culling per piano | sfumatura dei muri |
|---|---|---|---|
| **cosa fa** | gira la visuale attorno all'avatar | toglie dalla resa i piani sopra al tuo | rende granulari i muri fra te e la camera |
| **quando** | sempre, su input | solo dentro un edificio | solo dentro un edificio |
| **su cosa decide** | il giocatore | quote dei piani (**autorate**) | render layer + direzione della camera (**autorato**) |
| **chi lo fa** | `IsometricCamera` | `BuildingCullController` + `BuildingVolume` | idem |

**Fuori dagli edifici non sfuma niente.** Un muro isolato, una roccia o la murata di una barca che ti coprono restano pieni: la risposta è **girare la camera**. È una lacuna dichiarata, ed è il motivo per cui la rotazione esiste.

---

## 1. La rotazione della camera

`IsometricCamera` espone `CurrentYawDegrees`, l'imbardata **effettiva e già interpolata**. È la fonte di verità per chiunque debba allineare qualcosa alla visuale.

> **Non reintrodurre una costante di yaw da tenere in sincrono a mano.** C'era, si chiamava `PlayerController.CameraYawDegrees`, ed era corretta solo finché la camera non ruotava. Chi ha bisogno dell'angolo lo chiede alla camera.

Scelte prese, con il loro perché:

- **Scatti di 45°, non rotazione libera.** Otto orientamenti, tutti allineati alla geometria squadrata del mondo. Con angoli qualunque muri e solai si presentano storti e si perde la lettura isometrica; ed è anche ciò che tiene stabile il significato del WASD, che si sposta a scatti insieme alla camera invece di scivolare sotto le dita.
- **Il bersaglio non si normalizza in [0, 360).** `_targetYawDegrees` cresce o cala senza limiti, così l'interpolazione prende sempre la strada corta e non fa mai un giro completo passando per lo zero.
- **L'input si legge in `_UnhandledInput`**, non in `_Process`: è un evento discreto, e così una UI modale che consuma i tasti lo intercetta prima, senza dover consultare `GameManager.UiModalOpen` dentro la camera.
- **Solo la camera `Current`** reagisce. Ogni avatar, anche remoto, porta con sé una `PlayerCamera`: senza quella guardia si ruoterebbero quattro camere insieme.
- **La camera continua a non ruotare da sola.** Il rinculo (`AddKick`) è solo una traslazione sul piano immagine, e il `LookAt` si fa sulla posizione **base**, senza la scossa: applicandolo alla posizione scossa la camera ruoterebbe di poco a ogni colpo per riportare l'avatar al centro.

### Cosa NON ha avuto bisogno di modifiche, e perché

- **La mira.** `AimResolver` lavora su `camera.ProjectRayOrigin` / `ProjectRayNormal`, che seguono la camera qualunque sia il suo orientamento. Nessuna matematica della mira assume 45°.
- **Il timone.** `VehicleInput` usa comandi **relativi al veicolo** e non allo schermo — già prima per convenzione, e ora anche per necessità: uno sterzo relativo alla camera cambierebbe significato a ogni scatto di visuale.
- **Lo shroud.** Lavora in spazio mondo proprio per essere immune ai movimenti della camera (skill `vision-fog` §6).

### Cosa invece dipende dalla rotazione

`BuildingVolume.CollectCameraSideMeshes` prende la direzione "verso la camera" come **parametro** e va richiamata a ogni interrogazione: l'insieme dei muri che stanno fra la camera e il giocatore cambia mentre si gira. Un valore calcolato una volta all'ingresso resterebbe fermo sull'orientamento iniziale — e il sintomo sarebbe *«dopo aver ruotato, sfumano i muri sbagliati»*.

---

## 2. Il culling per piano

Autorato, perché **nessuna misura può dedurre "quanti piani ha questo edificio"**.

### Render layer

`core/Utils/RenderLayers.cs` è la fonte di verità, speculare a `[layer_names]` in `project.godot`.

| layer | costante | contenuto |
|---|---|---|
| 1 | `Always` | terreno, personaggi, pickup, VFX, **ShroudQuad** — mai culled |
| 2-7 | `building_floor_0..5` | struttura del piano N, **solaio compreso** |

Tre regole che non si negoziano:

1. **Il layer 1 resta sempre acceso.** Ci sta il quad dello Shroud, che non imposta `Layers` e quindi eredita il layer 1: spegnerlo fa sparire la nebbia dinamica.
2. **Si parte da `NonBuildingMask`, non da `Always`.** Il controller deve solo *togliere* i layer degli edifici. Partendo da `Always` si spegnerebbero in silenzio i layer 8-20 per il solo giocatore locale — bug che si scopre mesi dopo, quando qualcuno li userà.
3. **La copertura del piano N sta sul layer del piano N+1.** Il soffitto del piano N *è* il pavimento del piano N+1: è lo stesso solaio, e metterlo sul layer N+1 lo fa sparire da sotto e apparire da sopra con una sola regola. Ne discende che il tetto vero ha bisogno di un indice **oltre l'ultimo piano abitabile** — un indice che non è mai "il piano corrente". Senza, stando all'ultimo piano si guarderebbe il proprio tetto.

I piani **sotto** quello corrente restano accesi di proposito: nasconderli lascerebbe vedere il cielo attraverso il vano scala.

### Autorare un edificio

`world/scenes/Buildings/TestBuilding.tscn` è il template.

- **Nomi dei nodi**: `Floor0`, `Floor1`, … con un `Floor{N}` **in più** dei piani abitabili, che contiene il tetto. `BuildingVolume` li risolve per nome in `_Ready`; non sono export, perché un array di nodi da riempire a mano è il posto dove si sbaglia l'ordine.
- **Render layer delle mesh**: `2 + N` per il piano N. Comanda sia il culling sia la selezione dei muri da sfumare.
- **Collision layer**: i muri restano su `1` (`world`); solai, soffitti e tetti su `64` (`building_cover`).
- **Export del root**: `Footprint`, `FloorHeights` (quote locali crescenti), `TopHeight` (**poco sotto** la superficie calpestabile del tetto, o chi ci sale conta ancora come "all'ultimo piano"), `ExitHysteresis`. I test avvengono in coordinate locali via `ToLocal`, quindi un edificio ruotato funziona.
- **Porte e finestre sono VARCHI, non nodi**: si ottengono spezzando il muro in più `StaticBody3D`. `TestBuilding` ha una porta sul lato +Z (fra `WallZPos0A` e `WallZPos0B`) e una finestra sul lato +X (`WallXPos0A/B/Sill/Head`, varco da y=1 a y=2).

**Niente `Area3D` trigger.** Le bande di quota sono un test matematico: nessun ordinamento `body_entered`/`body_exited` da sbrogliare sulle scale fra piani adiacenti, e la stessa domanda si può fare per un punto qualunque senza che nessuno ci debba camminare dentro. È anche la convenzione del progetto: la prossimità è sempre polling su registry statici.

---

## 3. La sfumatura dei muri

Dentro una stanza il giocatore **quasi mai è coperto dai muri**: con la camera a 40° il raggio verso l'avatar scavalca un muro di 3 m dopo ~3.6 m, quindi al centro di una stanza ciò che copre è il *soffitto*, già tolto dal culling. Alla lettera è corretto lasciare i muri pieni; da giocare è inservibile, perché entrare in un edificio significa volerne vedere l'interno.

`BuildingVolume.CollectCameraSideMeshes` applica quattro filtri, e ognuno esiste per un errore concreto:

- **il piano si legge dal RENDER LAYER**, non dal nodo che contiene la mesh. I muri rivolti alla camera stanno spesso raggruppati a parte per comodità di modellazione (in `TestBuilding` sotto `Shell`), e fidarsi della gerarchia li perde tutti in silenzio;
- **solo superfici verticali**: se la dimensione minore dell'ingombro è Y è un solaio o una rampa, e sfumarlo aprirebbe un buco *sotto* al giocatore;
- **solo muri PERIMETRALI** (`IsOnPerimeter`): il centro deve stare a ridosso di uno dei quattro lati dell'`Footprint`, in coordinate locali. Sfumano **solo i muri esterni**;
- **solo il lato della camera**: il centro della mesh deve stare oltre l'origine dell'edificio nella direzione di vista, presa sul solo piano orizzontale. Dei quattro perimetrali restano i due o tre fra la camera e l'interno.

> **Il test di perimetro non è deducibile da quello di direzione**, ed è l'errore che il sistema ha fatto. Un tramezzo che per caso sta nella metà rivolta all'osservatore passa il test del lato camera: il sintomo è un interno visto attraverso i propri divisori, che non si legge più come stanze ma come un unico volume a chiazze.
>
> Corollario: **`Footprint` deve corrispondere all'edificio vero.** È un export, e con un valore lasciato al default su un edificio più grande quasi ogni mesh risulta "vicina al bordo" — il filtro passa e non filtra niente. Se sfumano superfici che non dovrebbero, controlla quel valore prima del codice.

`IsOnPerimeter` chiede la vicinanza a **uno solo** dei due assi: un muro lungo X sta al bordo in Z e in mezzo in X. Chiederli entrambi selezionerebbe i quattro angoli e nient'altro.

`BuildingCullController` tiene una **memoria** dei muri sfumati (`_fades`) con valore e flag "voluto". Non è cosmetica:

- la selezione cambia **a scatti** — a ogni cambio di piano e a ogni rotazione — e senza interpolazione i muri commuterebbero di colpo;
- un muro che esce dalla selezione deve rientrare **sfumando**, non sparendo dalla lista;
- **il ripristino è obbligatorio**: `fade` e `CastShadow` vivono sull'istanza della mesh, non sulla camera, quindi un muro dimenticato resterebbe granulare per il resto della partita anche a chilometri di distanza. Da qui `RestoreAllFades` in `_ExitTree`.

Uscendo da un edificio i muri **non tornano pieni di colpo**: smettono solo di essere voluti e rientrano con l'interpolazione.

### L'ordine dentro `_Process` non è arbitrario

Prima `UpdateBuilding`, che a ogni cambio di piano riaccende **tutte** le ombre del piano visibile (`ApplyFloorShadows`), poi `AdvanceFades`, che le rispegne sui muri sfumati. Invertirli lascerebbe l'ombra piena di un muro trasparente per tutti i frame fino al prossimo cambio di piano.

Il `CullMask` non tocca la shadow map: un muro granulare che continuasse a proiettare un'ombra **piena** lascerebbe la stanza al buio sotto una parete che si vede attraverso. È il bug più facile da introdurre e il più difficile da attribuire.

---

## 4. Il materiale

Una superficie sfuma **solo se** usa `world/shaders/WorldSurface.gdshader`. Rimpiazza `StandardMaterial3D` per tutta la geometria di mondo; `shader_parameter/albedo` prende il posto di `albedo_color`.

> **I due modi di fallire sono entrambi muti. Sono la prima cosa da controllare quando "non funziona".**
> 1. **Materiale sbagliato**: `SetInstanceShaderParameter("fade", …)` su un materiale che non dichiara quel parametro **non è un errore**, il valore si perde e basta. È così che il sistema si è rotto una volta, sostituendo il materiale di un muro per risolvere un problema di flickering.
> 2. **Mesh non raggiunta dai filtri** di `CollectCameraSideMeshes` (§3): render layer sbagliato, ingombro che non risulta verticale, o centro troppo lontano dal bordo dell'`Footprint`.

`fade` è un **parametro d'istanza** (`instance uniform` + `SetInstanceShaderParameter`): un solo materiale condiviso, un valore per mesh, nessuna duplicazione di risorse. Non duplicare il materiale per superficie.

`Mat_Water` in `TestLevel.tscn` resta di proposito uno `StandardMaterial3D`: non è un ostacolo che debba sfumare, ed è già trasparente con `depth_draw_mode = 1`, valore che lo Shroud richiede.

### Il materiale è OPACO, e deve restarlo. Non scrivere mai `ALPHA`.

La granularità si ottiene con **`discard` su una soglia ordinata** (Bayer 4×4 ancorata allo schermo), non mescolando in alpha. È la tecnica standard del genere — *dithered/masked transparency* — e non è una preferenza estetica: mescolare in alpha fa uscire il materiale dalla coda opaca, e con lui saltano **tre cose insieme**.

Questo è già successo una volta. I sintomi, che sembrano tre bug diversi e sono uno solo:

| sintomo | causa |
|---|---|
| il pavimento si buca e sotto compare lo skybox | l'ordinamento passa a "distanza del centro dell'AABB". Il pavimento 40×40 ha il centro all'origine e finisce disegnato *dopo* un muro vicino alla camera |
| il cono di visione si rompe sulle superfici sfumate | il depth buffer non è più affidabile, e lo Shroud (skill `vision-fog`) ci ricostruisce sopra la posizione mondo |
| le etichette degli oggetti a terra finiscono dietro ai muri | `Label3D` con `no_depth_test` si affida a essere disegnata **dopo** il mondo, garanzia che nella coda trasparente non esiste più |

**Nessuno dei tre si ripara con un render mode.** Si riparano restando opachi: non toccare `ALPHA` e lasciare `depth_draw_opaque`, che è il default e qui è corretto proprio perché il materiale *è* opaco. (`depth_draw_opaque` non significa "scrivi sempre la profondità": significa «scrivi la profondità *solo* nel caso opaco». Su un materiale trasparente non la scrive mai — ed è la trappola in cui si cade cercando di salvare l'approccio in alpha.)

Il prezzo del `discard` è che i frammenti scartati non scrivono profondità, quindi lo Shroud campiona la geometria dietro il buco. È accettato: dietro un muro c'è la stanza, a 30 cm, e la nebbia varia su scala di metri.

**A fade 0 non si scarta nulla e non si paga niente**: il mondo a riposo è geometria opaca normale. Sparisce anche lo sfarfallio fra facce complanari, che veniva dall'ordinamento per oggetto della coda trasparente.

### Taratura

- **`max_discard` (0.72).** La frazione **non** scartata è ciò che tiene l'accenno della forma, e serve che ci sia. Provato a 0.88 per rendere le superfici «più trasparenti»: la grana si sfalda e il risultato è *meno* leggibile, non più.
- **`cell_pixels` (3.0).** Il reticolo è ancorato allo **schermo**, non alla UV: la grana resta uguale comunque siano orientate e scalate le mesh — e resta uguale mentre la camera ruota.

---

## 5. Il sistema rimosso: apertura per campo visivo

C'è stato un impianto in cui **qualunque** superficie del mondo si apriva quando il personaggio poteva vedere ciò che essa nascondeva alla camera: ogni frammento campionava la maschera polare del ventaglio lungo la colonna che occludeva, con un gate di piano e un blob attorno all'avatar, e la resa era contorno + velo. È stato **rimosso**: sulla carta è coerente e non ha bisogno di nulla di autorato, in gioco produceva più problemi di quanti ne risolvesse — mezzo mondo che si apre mentre ci si muove, e nessun modo di prevedere cosa sarebbe diventato trasparente.

Se un giorno lo si riprende, quattro cose vanno sapute **prima** di ricominciare, perché sono già costate:

1. **La sottrazione di `SurfaceBias` dal ventaglio non è opzionale.** Il ventaglio sfonda di un metro oltre la superficie che lo ferma, apposta, per lo shroud (skill `vision-fog` §5). Chi se lo tiene tratta come visibile un metro *dietro* ogni muro, e ogni muro apre sé stesso: il sintomo canonico è stare all'angolo **fuori** da un edificio e vedere trasparenti i due muri esterni, che non lasciano vedere niente.
2. **Serve un gate di piano**, o il cono taglia il palazzo per tutta l'altezza a fetta di torta, e i piani superiori aperti sporcano anche la visuale del piano terra. Non è tarabile: la forma è sbagliata, non la dimensione.
3. **La sagoma dell'osservatore va trattata a parte** e deve scavalcare il gate: con la camera a 40° il tetto copre il terreno fino a ~7 m dietro di sé, quindi copre te, ma appartiene a un piano oltre il tuo. Senza l'eccezione si è invisibili dietro ogni edificio.
4. **Il contorno tramite guscio invertito NON funziona** con un materiale che si buca col `discard`: `next_pass` + `cull_front` + crescita lungo la normale riempie il buco invece di orlarlo, perché le back-face cresciute restano visibili solo finché la mesh originale le copre — ed è proprio quella che il discard ha tolto. Il risultato è una sagoma piena del colore del contorno.

La maschera polare del campo visivo continua a esistere (`VisionMask`, skill `vision-fog`) perché la usa lo shroud: chi riprendesse la strada la troverebbe già pronta, e **deve** passare da lì invece di costruirsi una nozione parallela di visibilità.

---

## 6. Rete: perché tutto sta sul giocatore

Ogni avatar, anche remoto, porta con sé una `PlayerCamera`, e solo quella locale ha `Current = true` (`PlayerNetworkSync`). Ne discende che:

- la guardia `GetParent().IsMultiplayerAuthority()` in `_Ready` **non è un'ottimizzazione ma correttezza**, come in `ShroudRenderer` e `VisionMask`; e nella camera la guardia equivalente è `Current`;
- un manager montato sull'**edificio**, con un campo `CurrentFloor`, sarebbe corretto solo in singleplayer: con quattro giocatori in stanze diverse i quattro peer si sovrascriverebbero lo stesso campo. L'edificio non può sapere "in che piano si è", perché la domanda **non ha una sola risposta**. Lo stesso vale per l'orientamento della camera: quattro giocatori possono guardare da quattro parti diverse.

`BuildingVolume` è quindi un dato passivo, `BuildingRegistry` un lookup statico sullo stampo di `VehicleRegistry`, e il controller vive sotto `Player`.

Nessuna RPC, nessuna proprietà replicata, nessuna modifica alla fisica. Due giocatori vedono due cose diverse, ed è l'unico comportamento corretto in cooperativa.

---

## 7. Le due maschere della mira

Prima erano una sola (`AimMask`) ed è **la ragione dei due bug più fastidiosi** del sistema. Sono due domande diverse e hanno due risposte:

- **`CursorMask`** — *cosa sto guardando*. Dipende dal cutaway, quindi dal singolo peer. Non contiene `BuildingCover`.
- **`ShotMask`** = `CursorMask | BuildingCover` — *cosa esiste davvero*. Usata da `AimResolver.TraceShot`, host-side, identica su tutti i peer. **Un solaio ferma il proiettile.**

Ma la maschera non basta, perché i **muri** dei piani superiori stanno su `World` e restano solidi anche mentre il cutaway li rende invisibili: il cursore ci si posava sopra, e il sintomo era *"al piano terra si mira il soffitto"*. La soluzione non è catalogare geometria layer per layer, è **tagliare il raggio**: `AimResolver.ResolveAimPoint` prende un `ceilingHeight` (da `BuildingCullController.AimCeilingHeight`, girato da `WeaponInput`) e fa partire il raggio da dove attraversa quel piano. Vale anche per la geometria che verrà.

Sotto, il piano di ripiego resta a `groundHeight + ChestHeight` con `groundHeight = CharacterMotor.ResolvedFeetY`: il proprio livello, non zero.

**Il cursore si ferma sulle superfici sfumate.** Le vedi attraverso ma non ci miri oltre — ed è onesto, perché il colpo si fermerebbe comunque lì.

---

## 8. Lacune volute

- **Fuori dagli edifici non sfuma niente.** Rocce, murate, muri isolati restano pieni: la risposta è ruotare la camera (§1). È la conseguenza diretta della rimozione del §5.
- **La sfumatura è per `MeshInstance3D` intera**, non un alone attorno alla sagoma.
- **Il culling è per piano**, non per stanza.
- **Una mesh senza il materiale giusto non partecipa**, in silenzio. È il meccanismo di opt-out (§4).

---

## 9. Verifica manuale

Nel `TestLevel` l'edificio di prova è a `(-12, 0, -10)`, con la porta sul lato +Z e la finestra sul lato +X.

Singleplayer:

1. **Q ed E** ruotano la visuale a scatti di 45°, l'avatar resta al centro, l'inclinazione e la distanza non cambiano;
2. dopo aver ruotato, **il WASD resta coerente con lo schermo**: "avanti" va verso l'alto dell'inquadratura;
3. il **reticolo resta sotto il cursore** a ogni orientamento, e i colpi partono dove punta;
4. ruotando mentre si spara, la scossa da rinculo non fa ruotare la visuale;
5. **dentro, al piano terra**: i muri rivolti alla camera sfumano **sempre**, anche stando al centro; i divisori interni no;
6. **ruotando da dentro**: sfumano i muri del nuovo lato camera e rientrano quelli vecchi, morbidamente, senza sfarfallio;
7. **dentro**: tetto e piano superiore spariscono (culling), la stanza **non resta al buio**;
8. uscendo, tutto torna pieno senza superfici rimaste granulari; rientrando, sfuma di nuovo;
9. il reticolo non sale sul soffitto; i colpi si fermano sul solaio;
10. **fuori**: nulla sfuma, nemmeno stando dietro a un muro — è la lacuna dichiarata (§8), non un difetto.

Multi-istanza (2 finestre, ENet locale):

11. A e B ruotano ciascuno la propria camera senza influenzarsi; nessuna mesh resta granulare per l'uno a causa dell'altro;
12. nessun giocatore o NPC cade attraverso i solai (verifica di `PlayerBodyMask`: `Player.tscn` = 103, headroom 97, `NpcCharacter.tscn` = 65).

Per far compilare gli shader senza aprire l'editor vedi la ricetta in `vision-fog` §9: lanciare `TestLevel` da solo **non basta**, perché senza rete non viene istanziato nessun Player.
