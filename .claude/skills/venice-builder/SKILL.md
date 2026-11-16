---
name: venice-builder
description: Addon `venice_builder` — generatore procedurale dei palazzi veneziani dai dati OpenStreetMap. Carica quando tocchi addons/venice_builder/, OsmLayer, OsmPlaceholder, building_generator.gd, osm_extract.py, quando generi o rigeneri edifici su una mappa, importi segnaposto OSM, aggiungi un nuovo tipo di elemento OSM (strade, alberi, canali), o un edificio generato esce sfalsato, senza scala, senza materiali, con muri che sbucano agli spigoli.
---

# Venice Builder: da OpenStreetMap ai palazzi del kit

Addon editor in `addons/venice_builder/`. Prende le impronte OSM (già in coordinate mondo) e
monta ogni edificio coi kit `venice` / `venice_ruin` (skill `venice-building-kit`).
**È GDScript di proposito**: tooling di sola generazione, in gioco non esegue nulla. Gli edifici
generati sono scene fatte solo di `MeshInstance3D` + `StaticBody3D` (layer 1, World): nessuna
logica, niente da replicare in rete.

## 1. Flusso d'uso (editor)

1. Nella scena della mappa aggiungere un nodo **`OsmLayer`** (trasformata identità).
2. **"1. Scarica da Overpass"**: lancia `tools/osm_extract.py --bbox <sud,ovest,nord,est>` e
   riscrive `osm_json`. Serve Python nel PATH. In alternativa un export di overpass-turbo si
   converte a mano con `--input` (vedi la testata dello script).
3. **"2. Importa i segnaposto"**: un `OsmPlaceholder` (cilindro arancione nel baricentro) per ogni
   edificio non ancora importato. La chiave è `osm_id`: rilanciare non duplica nulla.
4. Selezionare uno o più segnaposto → nel gruppo *Generazione* dell'ispettore scegliere **kit**
   (Normale / Rovina), **facciata** (Casuale o uno dei sei colori), seme, piani, interni →
   **"Genera edifici"** nella barra della vista 3D (o il pulsante *Genera* del singolo nodo).
   Il cilindro diventa verde e sale sopra il tetto; rigenerare sostituisce.
5. `OsmLayer` ha anche *Genera tutti i mancanti* e *Rimuovi tutti*.

Ogni edificio è salvato in `output_directory/<nome segnaposto>.scn` e istanziato come figlio
`Building` del segnaposto. La porzione di prova (744 edifici) pesa ~260 MB e 12,8 M di triangoli
se generata tutta: decidere cosa versionare.

### Modificare i singoli blocchi

Con **un solo** segnaposto generato selezionato, accendere **"Modifica blocchi"** nella barra: un
clic su un muro esterno, un tramezzo, un pavimento (da sopra) o un soffitto (da sotto) apre il menu
con i moduli/materiali possibili, il colore di facciata di quel blocco e *Ripristina*. La scelta va
in `OsmPlaceholder.overrides` (chiave del blocco → modulo) e l'edificio viene rigenerato.

- Chiavi: `w:<lato>:<modulo>:<piano>` muri esterni (`…#c` = colore), `p:<linea>:<tratto>:<modulo>:<piano>`
  tramezzi, `f:<piano>` pavimento, `c:<piano>` soffitto. `"-"` = nessun modulo (varco).
- Le chiavi sono **posizionali**: restano valide finché pianta, piani e interni non cambiano.
  Cambiare kit le conserva (stessi nomi di modulo; quelli solo-rovina ricadono sul muro pieno).
- Gli override si applicano **dopo** i tiri di dado: sostituire un blocco non cambia il resto.
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
| `osm_placeholder.gd` (`OsmPlaceholder`) | impostazioni per edificio, `overrides` dei blocchi, `pick_block` |
| `placeholder_gizmo.gd` | disegna il cilindro e lo rende **cliccabile** (triangoli di collisione del gizmo: un `MeshInstance3D` figlio non è selezionabile con un clic) |
| `building_generator.gd` | impronta → `Node3D`. Tutta la logica di montaggio |
| `kit_library.gd` | moduli del kit come liste di triangoli + **materiali condivisi** |
| `polygon_tools.gd` | semplificazione, triangolazione con buchi, restringimento, intersezioni |
| `terrain_sampler.gd` | quota d'appoggio letta dalle region Terrain3D su disco |
| `plugin.gd` | registra il gizmo, la barra "Genera edifici / Rimuovi / Modifica blocchi", il menu di sostituzione |
| `tools/osm_extract.py` | Overpass → JSON a livelli in coordinate mondo (costanti di georeferenziazione in testa) |

Collaudo senza editor: uno script `extends SceneTree` che crea `osm_layer.gd`, chiama
`import_placeholders()` e `generate_placeholders(lista)` — funziona headless, anche fuori dall'albero.

## 3. Come viene montato un edificio

Le piante reali sono oblique e mai multiple di 2 m: il montaggio è **misto**.

- **Pianta**: anelli riportati al baricentro (`footprint_center`, lo stesso punto del segnaposto),
  raddrizzati con tolleranza 0,2 m, esterno ad area positiva e cortili negativa. Così la normale
  verso fuori di un lato a→b è sempre `(d.y, -d.x)`.
- **Piani**: `building:levels`, altrimenti altezza / 3,2; ogni piano è alto 3 m (il kit).
- **Facciate**: per ogni lato `n = round(L / 2)` moduli **scalati in X** per riempirlo esatto.
  Finestre e porte solo con scala fra 0,75 e 1,3; fuori da lì muro cieco.
  - *Muri in comune*: un modulo il cui esterno cade dentro l'impronta di un vicino resta cieco,
    senza zoccolo né marcapiano.
  - *Lato principale* (più moduli liberi): porta al centro, poggiolo al piano nobile.
  - Piano terra finestrelle con grata; piano nobile gotiche/ad arco; sopra rettangolari/ad arco.
    Varianti `_shut`, e in rovina `_broken` / `_boarded` / `ext_wall_hole` / `ext_wall_breach`.
- **Spigoli** (i muri del kit non hanno testate):
  - *acuti* (< 90°): la faccia interna di un muro sbucherebbe dalla facciata dell'altro → i due
    muri si accorciano di `0,3 / tan(angolo)` e la punta è chiusa da due facce generate;
  - *rientranti*: una faccia d'intonaco chiude la tacca fra le due testate, dal lato stanza;
  - *cornicione*: una corsa per lato, allungata sugli spigoli convessi perché i profili si incrocino.
- **Solai, soffitti, tetto, fondazione**: generati sulla pianta vera con le texture del kit.
  Il tetto è un padiglione tronco (falda di 2,5 m lungo tutto il perimetro, cortili compresi, e
  colmo piatto). La fondazione scende sotto il punto più basso del terreno.
- **Scala**: si cerca un vano 4×4 m + 2 m liberi davanti, allineato al lato più lungo, il più
  vicino possibile a un muro. Dentro: `stair_u` a ogni piano, tramezzi sui tre lati, `floor_edge`
  attorno al foro, e all'ultimo piano un tramezzo sopra la prima rampa. I moduli di facciata
  accanto al vano diventano ciechi. **Se non ci sta, i piani alti restano senza scala** (~12% degli
  edifici della porzione di prova: quelli larghi meno di ~4,7 m); il log lo conta.
- **Tramezzi**: perpendicolari al lato lungo ogni ~5 m, una porta ciascuno, mai nel vano scala.
- **Fusione**: tutto finisce in due mesh (`Shell`, e `Interior` con `visibility_range_end`) con una
  superficie per materiale, più un trimesh unico e i convessi delle rampe.

## 4. Materiali condivisi

`kit_library.gd` salva una sola copia di ogni materiale in
`assets/models/buildings/materials/<kit>/<superficie>.tres` (creati al primo uso, specular 0) e
tutti gli edifici puntano a quelli. Ritoccare un `.tres` (per esempio `albedo_color`) cambia
tutti gli edifici di quel kit senza rigenerare. Se si rigenera il kit da Blender con texture nuove
i `.tres` restano validi (puntano ai PNG); vanno cancellati solo se cambia un materiale del glTF.

## 5. Trappole

- **`Vector2` è a 32 bit e le coordinate mondo sono ~15 km**: area e baricentro vanno calcolati
  relativi al primo vertice (`polygon_tools.gd`), altrimenti il baricentro sbaglia di centinaia di
  metri. Vale per ogni nuova formula sulle impronte grezze.
- Gli script non nominano `EditorInterface` direttamente (esistono anche in gioco): passano da
  `Engine.get_singleton`. Solo `plugin.gd` può.
- I materiali delle superfici usate solo da geometria generata (pavimenti, tetto) si ricavano da
  un modulo "portatore": tabella `CARRIERS` in `kit_library.gd`. Una superficie nuova va aggiunta lì.
- `demo/` è ignorata da git. La scena di prova è `demo/test_map_ve_kit.tscn` (mappa
  `venice_map_nobuildings.scn`, prodotta da `build_map_scene.gd -- --no-buildings`, più l'`OsmLayer`).

## 6. Lacune volute

- Solo il livello `buildings`. Per strade, alberi, canali: nuovo valore di `osm_layer` sul
  segnaposto, un colore del cilindro, un generatore dedicato scelto in `generate_placeholders`.
- I `building:part` sono ignorati: i monumenti (Palazzo Ducale, chiese, campanili) escono come
  palazzi qualunque. Vanno fatti a mano o esclusi.
- Nessun LOD né occlusione: generare tutta una città richiede prima una strategia di streaming.
- Niente ante alle porte, ringhiere, tetti rotti, crolli parziali; i tramezzi possono finire
  contro una finestra; nessuna porta d'acqua.
- Nessun undo nell'editor: la generazione scrive file. Per tornare indietro, *Rimuovi*.
