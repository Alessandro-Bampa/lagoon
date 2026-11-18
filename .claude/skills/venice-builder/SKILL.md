---
name: venice-builder
description: Addon `venice_builder` — generatore procedurale dei palazzi veneziani dai dati OpenStreetMap. Carica quando tocchi addons/venice_builder/, OsmLayer, OsmPlaceholder, building_generator.gd, osm_extract.py, quando generi o rigeneri edifici su una mappa, importi segnaposto OSM, aggiungi un nuovo tipo di elemento OSM (strade, alberi, canali), o un edificio generato esce sfalsato, senza scala, senza materiali, con muri che sbucano agli spigoli.
---

# Venice Builder: da OpenStreetMap ai palazzi del kit

Addon editor in `addons/venice_builder/`. Prende le impronte OSM (già in coordinate mondo) e
monta ogni edificio coi kit `venice` / `venice_ruin` (skill `venice-building-kit`).
**È GDScript di proposito**: tooling di sola generazione. In gioco gira solo `OsmChunk._ready`
(lega gli edifici al LOD lontano del loro settore, §4b). Gli edifici generati sono scene fatte solo
di `MeshInstance3D`, `OccluderInstance3D` e `StaticBody3D` (layer 1, World): nessuna logica, niente
da replicare in rete.

## 1. Flusso d'uso (editor)

1. Nella scena della mappa aggiungere un nodo **`OsmLayer`** (trasformata identità).
2. **"1. Scarica da Overpass"**: con `map_json` (default `world/maps/laguna/map.json`) lancia
   `tools/osm_extract.py --map` e riscrive tutti i livelli della mappa in `<mappa>/osm/layers`
   (a tasselli, con cache: rilanciare riprende); senza, scarica `bbox` in `osm_json`. Serve
   Python nel PATH. Un export di overpass-turbo si converte a mano con `--input`.
3. **"2. Importa i segnaposto"**: un `OsmPlaceholder` (cilindro arancione nel baricentro) per ogni
   edificio non ancora importato. La chiave è `osm_id`: rilanciare non duplica nulla.
4. Selezionare uno o più segnaposto → nel gruppo *Generazione* dell'ispettore scegliere **kit**
   (Normale / Rovina), **facciata** (Casuale o uno dei sei colori), seme, piani, interni →
   **"Genera edifici"** nella barra della vista 3D (o il pulsante *Genera* del singolo nodo).
   Il cilindro diventa verde e sale sopra il tetto; rigenerare sostituisce.
5. `OsmLayer` ha anche *Genera tutti i mancanti*, *Rigenera tutti quelli già generati* (da usare
   dopo ogni modifica al generatore o al kit) e *Rimuovi tutti*.

Ogni edificio è salvato in `output_directory/<nome segnaposto>.scn` e istanziato come figlio
`Building` del segnaposto; accanto nascono `far/<nome>.res` e `chunks/<settore>.res` (§4b). La porzione di prova (744 edifici) pesa ~260 MB e 12,8 M di triangoli
se generata tutta: decidere cosa versionare.

### Modificare i singoli blocchi

Con **un solo** segnaposto generato selezionato, accendere **"Modifica blocchi"** nella barra: un
clic su un muro esterno, un tramezzo, un pavimento (da sopra) o un soffitto (da sotto) apre il menu
con i moduli/materiali possibili, il colore di facciata di quel blocco e *Ripristina*. La scelta va
in `OsmPlaceholder.overrides` (chiave del blocco → modulo) e l'edificio viene rigenerato.

- Chiavi: `w:<lato>:<modulo>:<piano>` muri esterni (`…#c` = colore), `p:<i>:<j>:<lato>:<piano>`
  tramezzi (bordo di cella della griglia degli interni, §3b), `f:<piano>` pavimento, `c:<piano>`
  soffitto. `"-"` = nessun modulo (varco).
- Le chiavi sono **posizionali**: restano valide finché pianta, piani, seme e interni non cambiano.
  Cambiare kit le conserva (stessi nomi di modulo; quelli solo-rovina ricadono sul muro pieno).
  Gli override salvati prima della generazione a griglia (tramezzi `p:<linea>:<tratto>:…`, indici
  dei moduli di facciata) non corrispondono più: *Azzera le modifiche ai blocchi*.
- Gli override si applicano **dopo** i tiri di dado e la convalida: sostituire un blocco non cambia
  il resto (e nessuno ricontrolla la scelta fatta a mano).
- La tabella dei blocchi (chiave, tipo, modulo, trasformata, ingombro) sta nei metadati `blocks`
  del nodo `Building`; `OsmPlaceholder.pick_block(origine, direzione)` la interroga con un raggio.
  Un edificio generato prima di questa funzione non ha la tabella: va rigenerato.
- Pavimenti e soffitti sono per piano intero, non per stanza. Tetto, scala e fasce non sono blocchi.

**"Palette"** = colore di facciata. Non servono kit duplicati per colore: i due kit contengono già
le sei texture `facade_<colore>` (+ `_base` per il piano terra) e il generatore sceglie il materiale.

## 2. File

| File | Ruolo |
|---|---|
| `osm_layer.gd` (`OsmLayer`) | scaricamento, import dei segnaposto, generazione/rimozione, salvataggio delle scene |
| `osm_chunk.gd` (`OsmChunk`) | settore della griglia: contiene i segnaposto e il `Far` fuso; `wire()` imposta i visibility parent |
| `osm_placeholder.gd` (`OsmPlaceholder`) | impostazioni per edificio, `overrides` dei blocchi, `pick_block` |
| `placeholder_gizmo.gd` | disegna il cilindro e lo rende **cliccabile** (triangoli di collisione del gizmo: un `MeshInstance3D` figlio non è selezionabile con un clic) |
| `building_generator.gd` | impronta → `Node3D`: facciate, convalida piano/facciata, montaggio e fusione |
| `floor_planner.gd` | griglia delle celle, scelta della scala, pianta di ogni piano (stanze, muri, porte). Solo dati, nessuna geometria (§3b) |
| `kit_library.gd` | moduli del kit come liste di triangoli + **materiali condivisi** |
| `polygon_tools.gd` | semplificazione, triangolazione con buchi, restringimento, intersezioni |
| `terrain_sampler.gd` | quota d'appoggio letta dalle region Terrain3D su disco (`terrain_directory`, default `world/maps/laguna/terrain`). Terra = sopra quota 0: è il terreno generato da OSM, skill `venice-terrain` |
| `plugin.gd` | registra il gizmo, la barra "Genera edifici / Rimuovi / Modifica blocchi", il menu di sostituzione |
| `tools/osm_extract.py` | Overpass → JSON a livelli in coordinate mondo (costanti di georeferenziazione in testa). Con `--landcover` scarica solo coperture e acque, a tasselli: è la sorgente del terreno |
| `tools/osm_terrain.py`, `tools/terrain_import.gd` | terreno dai dati OSM → region di Terrain3D: skill `venice-terrain` |

Collaudo senza editor: uno script `extends SceneTree` che crea `osm_layer.gd`, chiama
`import_placeholders()` e `generate_placeholders(lista)` — funziona headless, anche fuori dall'albero.

## 3. Come viene montato un edificio

Le piante reali sono oblique e mai multiple di 2 m: il montaggio è **misto**.

- **Pianta**: anelli riportati al baricentro (`footprint_center`, lo stesso punto del segnaposto),
  raddrizzati con tolleranza 0,2 m, esterno ad area positiva e cortili negativa. Così la normale
  verso fuori di un lato a→b è sempre `(d.y, -d.x)`.
  - *Piante sovrapposte*: OSM a volte sovrappone di qualche centimetro due edifici accostati, e
    i muri compenetrati sfarfallano. `_clip_to_neighbours` toglie la parte comune alla pianta
    **più piccola** delle due (l'altra resta intera: nessuna fessura). Statistica `clipped`
    (~10% degli edifici della porzione di prova).
- **Piani**: `building:levels`, altrimenti altezza / 3,2; ogni piano è alto 3 m (il kit).
- **Facciate**: ogni lato è diviso in moduli **scalati in X**. Se il lato è parallelo (entro ~10°)
  alla griglia degli interni i tagli cadono **sulle linee di griglia** (`_slot_cuts`): un tramezzo
  arriva sempre fra due moduli e le finestre stanno al centro delle celle; i ritagli alle estremità
  si fondono col modulo vicino (< 0,5 m) o restano moduli stretti. Sui lati obliqui `round(L / 2)`
  moduli uguali. Finestre e porte solo con scala fra 0,75 e 1,3; fuori da lì muro cieco.
  - *Muri in comune*: un modulo il cui esterno cade dentro l'impronta di un vicino resta cieco,
    senza zoccolo né marcapiano.
  - *Lato principale* (più moduli liberi): porta verso il centro (non sempre al centro), poggiolo
    al piano nobile sopra la porta.
  - Ritmo delle finestre (tutte / alterne / a coppie) e quota di grate al piano terra tirati per
    edificio. Piano terra finestrelle con grata; piano nobile gotiche/ad arco; sopra rettangolari/ad arco.
    Varianti `_shut`, e in rovina `_broken` / `_boarded` / `ext_wall_hole` / `ext_wall_breach`.
- **Spigoli** (i muri del kit non hanno testate):
  - *acuti* (< 90°): la faccia interna di un muro sbucherebbe dalla facciata dell'altro → i due
    muri si accorciano di `0,3 / tan(angolo)` e la punta è chiusa da due facce generate;
  - *rientranti*: una faccia d'intonaco chiude la tacca fra le due testate, dal lato stanza;
  - *cornicione*: una corsa per lato, allungata sugli spigoli convessi perché i profili si incrocino.
- **Solai, soffitti, tetto, fondazione**: generati sulla pianta vera con le texture del kit.
  Il tetto è un padiglione tronco (falda di 2,5 m lungo tutto il perimetro, cortili compresi, e
  colmo piatto). La fondazione scende sotto il punto più basso del terreno.
- **Interni** (scala, stanze, tramezzi, porte): decisi piano per piano prima di montare nulla, §3b.
- **Fusione**: tutto finisce in poche mesh con una superficie per materiale (`Shell`; `Interior`
  con `visibility_range_end`, che contiene anche solai e soffitti), più un trimesh unico e i
  convessi delle rampe. Il resto dei nodi è descritto in §4b.

## 3b. Interni: un piano alla volta, convalidato prima di montarlo

`generate()` decide tutto l'edificio **prima** di emettere geometria, dal basso verso l'alto: ogni
scelta dipende da ciò che c'è già. Il pianificatore (`floor_planner.gd`) lavora su una **griglia di
celle da 2 m** allineata al lato più lungo, con l'origine sulla **faccia interna** dei muri esterni.
Celle `full` (tutte dentro: le sole su cui sta una scala), `usable` (dentro per più di metà: fanno
stanza), di frangia (seguono la stanza della cella `parent`).

1. **Dati generali**: lati e moduli di facciata, muri in comune coi vicini, **tipo di edificio**
   (`kind_for`), porta d'ingresso, scala.

   | Tipo | Quando (celle usabili `n`) | Com'è |
   |---|---|---|
   | `CONDOMINIO` | `n ≥ 40`, oppure ≥ 4 piani e `n ≥ 14` | tromba delle scale **chiusa da muri**; piano terra con androne dalla porta alla scala; a ogni piano pianerottolo/corridoio su cui si aprono le porte delle unità; ogni unità divisa in stanze. Un piano può ripetere quello di sotto (35%) |
   | `PADRONALE` | 2–3 piani e `n ≥ 12` | casa unica: salone ampio (30–45% del piano terra) con la scala **aperta** dentro, stanze più grandi, archi verso il salone. Con 2 piani la scala può essere `stair_straight` lungo un muro |
   | `CASA` | il resto | casa piccola: `stair_u_narrow` fra tre muri, poche stanze |

2. **Scala** (`find_stair`): una posizione per tutto l'edificio (le scale a U si impilano sullo
   stesso vano), scelta fra tutte quelle in cui vano e sbarco sono celle `full`. Punteggio: non
   togliere finestre, stare contro i muri, non tagliare in due il piano, più un po' di caso.
   Ogni piano **parte da qui**: celle della scala tolte, sbarco = prima cella del disimpegno.
3. **Pianta del piano** (`plan_floor`): disimpegno (`rooms[0]`, cresce a strisce: `_inflate`, o è
   un corridoio scavato verso le unità che non lo toccano: `_path`), poi stanze per tagli lungo le
   linee di griglia (`_bsp`: pezzi equilibrati, taglio sul lato corto, caso). Muri = bordi fra
   celle di stanze diverse; porte = albero che parte dal disimpegno (ogni stanza è raggiungibile),
   più qualche porta in più. Una stanza rimasta senza un bordo adatto a una porta **si fonde** con
   una vicina. Tramezzi tagliati sul muro esterno vero (`clip`); `int_post` su incroci, angoli e
   testate libere.
4. **Convalida contro la facciata** (`_plan_floor` + `_plan_facade`): per ogni piano si provano
   fino a 4 piante e si tiene quella con meno conflitti; poi
   - tramezzo che arriva dentro la zona dell'apertura di un modulo → il modulo diventa cieco
     (statistica `blinded`); se è la porta d'ingresso si toglie il tramezzo;
   - accanto alle rampe muro cieco; sul fondo di una scala a U al più `ext_window_high`;
   - ogni stanza con un muro libero verso l'esterno riceve almeno una finestra.

Dopo: `_emit_facades`, solai (foro dove arriva una scala), `_emit_stairs`, `_emit_partitions`.

- **Nessuna ringhiera contro un muro.** La scala si monta come `<tipo>_core` più le ringhiere
  esterne (`_rail_l`, `_rail_r`, `_rail_back`) solo sui lati liberi; lo stesso per `stair_rail`
  attorno al foro. Il controllo è uno solo: `_edge_walled` (muro esterno o tramezzo su quel bordo
  di cella).
- **Muri del vano fra un piano e l'altro.** I tramezzi sono alti 2,7 m ma nel vano il soffitto non
  c'è: su ogni tramezzo con una cella di scala accanto `_wall_riser` aggiunge, dal lato della
  scala, la fascia da 2,7 a 3 m e la sua testa. Senza, salendo si vede dentro lo spessore del muro
  (le facce posteriori non si disegnano: un muro visto di taglio sembra un foglio). I pilastrini
  hanno la testa per lo stesso motivo.
- **Intonaco fatto a mano**: la texture copre 3 m da terra con V = 1 al pavimento, come nei moduli.
  Una faccia generata con altre UV sposta lo zoccolo dipinto rispetto ai muri accanto.

- Collaudo: `settings["debug"] = true` mette in `last_stats()["plans"]` la pianta di ogni piano in
  caratteri (`#` scala, `.` disimpegno, lettere = stanze). Statistiche: `kind`, `stair`, `blinded`,
  `isolated`. Sulla porzione di prova (760 edifici, ~96 s senza LOD): 6,5% degli edifici a più piani
  resta senza scala (interno più stretto di 2 m × 6 m di celle intere), contro il 12% di prima.
- Ogni piano ha il suo generatore casuale (`hash([seme, piano, tentativo])`): cambiare un piano non
  sposta i dadi degli altri.
- Aggiungere un tipo di scala: modulo nel kit, una riga in `_stair_types` (`w` celle di larghezza,
  `enclosed`, `straight`), regole dei parapetti in `_emit_stairs`. Il vano è sempre profondo 2 celle.

## 4. Materiali condivisi

`kit_library.gd` salva una sola copia di ogni materiale in
`assets/models/buildings/materials/<kit>/<superficie>.tres` (creati al primo uso, specular 0) e
tutti gli edifici puntano a quelli. Ritoccare un `.tres` (per esempio `albedo_color`) cambia
tutti gli edifici di quel kit senza rigenerare. Se si rigenera il kit da Blender con texture nuove
i `.tres` restano validi (puntano ai PNG); vanno cancellati solo se cambia un materiale del glTF.

## 4b. Prestazioni: cosa esce da un edificio e perché

Struttura della scena: `OsmLayer / Chunk_<x>_<z> (OsmChunk) / B_way_… (OsmPlaceholder) / Building`.
I settori sono una griglia di `chunk_size` (100 m); un segnaposto sta nel settore del suo baricentro.
Le scene vecchie (segnaposto figli diretti del layer) vengono smistate da sole al primo
*Importa* / *Genera* (`organize_chunks`).

| Nodo | Cos'è | Perché |
|---|---|---|
| `Shell` | guscio, mesh indicizzata con LOD automatici (`ImporterMesh`), `cast_shadow` spento | da vicino |
| `ShadowCaster` | tutto il guscio opaco in **una** superficie di sole posizioni, `SHADOWS_ONLY` | ogni cascata d'ombra costa 1 draw call per edificio invece di ~13. Vetri, grate e balaustre ne sono esclusi (`NO_SHADOW`): il sole entra dalle finestre |
| `Interior` | solai, soffitti, tramezzi, scale; sparisce oltre `interior_range` (60 m) | da fuori non si vedono |
| `Occluder` | `ArrayOccluder3D`: un quad per ogni fila di moduli `ext_wall` dello stesso piano + la pianta alla quota di gronda | solo ciò che è **davvero opaco**: un volume pieno nasconderebbe interni e loot visti dalle finestre |
| `Chunk/Far` | LOD lontano di tutto il settore fuso in una mesh (`far/*.res` dei singoli edifici → `chunks/*.res`) | oltre `far_range` (120 m dal centro del settore) un settore costa ~12 draw call |

- **LOD lontano di un modulo** (`kit_library.gd`, `FAR_KEEP`): solo le facce rivolte in fuori di
  `facade`, `window`, `glass` (reso opaco: `far_glass`), `shutter`, `shutters`. Via pietra, intonaco
  interno, balaustre. Se la facciata ha un buco (`holed`, misurato sull'area) dietro va un quad
  `far_dark`, così non si vede attraverso. Tetto e fondazione vanno tali e quali. ~19% dei triangoli.
- **Lo scambio vicino/lontano lo fa il motore** con `visibility_parent`: `Shell`, `ShadowCaster` e
  `Interior` hanno come genitore di visibilità il `Far` del loro settore, quindi si vedono solo
  quando quello è nascosto. Niente buchi né doppioni al confine. Il legame non si può salvare
  dentro un'istanza: lo rimette `OsmChunk.wire()` in `_ready` e dopo ogni generazione.
- **Non fondere gli edifici vicini fra loro**: si perderebbero culling per edificio, occlusione,
  portata degli interni e la rigenerazione del singolo edificio. La fusione vale solo per `Far`.
- **Occlusion culling**: acceso in `project.godot` (`rendering/occlusion_culling`). Costa CPU
  (~0,3 ms sulla porzione di prova) e rende di più quanto più la scena è fitta.
- Misure sulla porzione di prova (120 edifici, 720p): draw call da ~2.500 a ~800 a livello strada
  (~325–790 con l'occlusione), da ~2.240 a ~350 dall'alto; buffer delle mesh da 162 a 148 MB.

### Loot negli edifici (predisposizione)

Il generatore calcola `loot_points`: punti sul pavimento a 0,7 m da un muro, uno o due per stanza
della pianta (uno nel disimpegno), fuori da scala e sbarco, **solo ai piani raggiungibili** (senza
scala: solo il piano terra). Stanno nei
metadati di `Building` (locali all'edificio) e, aggregati, in quelli del settore (`OsmChunk`,
coordinate locali del settore). Sono **dati, non nodi**:

- li legge l'host (CLAUDE.md §3) per decidere dove e quando far comparire gli oggetti: non servono
  le mesh caricate, e sopravvivono allo streaming dei settori;
- gli oggetti di loot **non vanno messi dentro `Building`** (rigenerare lo sostituisce): vanno sotto
  un nodo di gioco, meglio se raggruppati per settore, così l'occlusione li scarta da sola;
- lo stato persistente (cosa è stato preso, quando rinasce) va tenuto per indice del punto dentro
  il settore, non per nodo: l'ordine dei punti è deterministico a parità di pianta e impostazioni.

## 5. Trappole

- **`Vector2` è a 32 bit e le coordinate mondo sono ~15 km**: area e baricentro vanno calcolati
  relativi al primo vertice (`polygon_tools.gd`), altrimenti il baricentro sbaglia di centinaia di
  metri. Vale per ogni nuova formula sulle impronte grezze.
- Gli script non nominano `EditorInterface` direttamente (esistono anche in gioco): passano da
  `Engine.get_singleton`. Solo `plugin.gd` può.
- I materiali delle superfici usate solo da geometria generata (pavimenti, tetto) si ricavano da
  un modulo "portatore": tabella `CARRIERS` in `kit_library.gd`. Una superficie nuova va aggiunta lì.
- `PackedScene.instantiate()` senza `GEN_EDIT_STATE_INSTANCE` fa salvare nella scena della mappa
  una copia dei metadati di ogni edificio (la tabella dei blocchi: 10 MB su 120 edifici).
- Prima di spostare un nodo in un altro ramo (`organize_chunks`) il suo `owner` va azzerato e
  rimesso dopo, anche al figlio `Building`.
- La mesh d'ombra di `ArrayMesh` (`shadow_mesh`) è usata anche dal depth prepass e va superficie
  per superficie: non può essere una geometria semplificata. Per questo `ShadowCaster` è un nodo a sé.
- Una classe `class_name` nuova non esiste negli script lanciati headless finché l'editor non ha
  riscandito: fra gli script dell'addon ci si riferisce per `preload`.
- **I gizmo delle collisioni sono il costo maggiore nell'editor**, non le mesh: un trimesh da
  ~14.000 triangoli a edificio disegnato in filo di ferro e riempimento porta la GPU da ~2 ms a
  10–50 ms (misurato con `--debug-collisions`). Per questo `Collision` è generato con
  `visible = false` (la fisica non guarda la visibilità, l'editor nasconde il gizmo) e le shape con
  `debug_fill = false`. In gioco `--debug-collisions` le disegna comunque, e resta lento.
  Prima di ottimizzare altro guardare *Tempo CPU* contro *Tempo GPU* nella vista 3D.
- Le facce di collisione fatte a mano devono avere il verso giusto (`_face_quad`): il trimesh ha
  `backface_collision` spento e ferma solo chi arriva dalla faccia frontale.
- Dopo aver rigenerato il kit da Blender gli edifici già generati tengono la collisione vecchia
  (è cotta nella scena): *Rigenera tutti quelli già generati*.
- La mappa vera è `world/maps/laguna/` (skill `venice-terrain`): i suoi edifici stanno in
  `osm/layers/buildings.json` (63.000 impronte su 1260 km²), che è il default di `osm_json`.
  La vecchia scena di prova in `demo/` (coordinate UTM 32) non esiste più: gli edifici generati
  lì non combaciano con la georeferenziazione nuova e vanno rigenerati.
- Con 63.000 edifici non si importano tutti i segnaposto insieme: la generazione per settori
  e lo streaming delle loro scene sono ancora da fare (§6).

## 6. Lacune volute

- Solo il livello `buildings`. Per strade, alberi, canali: nuovo valore di `osm_layer` sul
  segnaposto, un colore del cilindro, un generatore dedicato scelto in `generate_placeholders`.
- I `building:part` sono ignorati: i monumenti (Palazzo Ducale, chiese, campanili) escono come
  palazzi qualunque. Vanno fatti a mano o esclusi.
- **Nessuno streaming**: tutti i settori sono caricati all'avvio (mesh e collisioni). I chunk sono
  già l'unità giusta: per una città intera vanno salvati come scene a sé e caricati per distanza
  (`ResourceLoader.load_threaded_request`). L'host deve tenere caricate le collisioni attorno a
  **ogni** giocatore e NPC; i client solo attorno a sé.
- La collisione è un trimesh unico da ~3.000 triangoli a edificio (erano ~13.900): il kit dà ai
  muri con aperture una collisione semplificata (skill `venice-building-kit` §4) e il generatore
  fonde i moduli "pieni" (`solid` in `kit_library.gd`: muri ciechi e finestre chiuse) in due sole
  facce per fila e per piano (`_collision_walls`). Il resto sono tramezzi, brecce e solai.
- Un materiale per superficie: ~13 draw call per guscio vicino. Per scendere ancora servirebbe uno
  shader unico con le facciate in un `Texture2DArray`, al posto dei `.tres` condivisi.
- Niente ante alle porte, tetti rotti, crolli parziali; nessuna porta d'acqua.
- Interni: pavimento e soffitto restano per piano, non per stanza; le stanze non hanno una
  destinazione (cucina, camera…) né arredi; i piani non condividono i muri portanti (ogni piano ha
  la sua pianta, salvo il "piano tipo" dei condomini); una sola scala per edificio, anche nei
  palazzi molto grandi; la rampa dritta solo negli edifici a due piani.
- Nessun undo nell'editor: la generazione scrive file. Per tornare indietro, *Rimuovi*.
