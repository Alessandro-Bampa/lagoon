---
name: venice-terrain
description: Mappa open world della laguna (world/maps/laguna) e il suo terreno generato dai soli dati OpenStreetMap (niente DEM) — georeferenziazione locale, download OSM del poligono, classi (mare, laguna, acqua bassa, barena, velma, canale, rio, ghebo, isola, prato, bosco), quote di gioco, region di Terrain3D 1.1, streaming delle region (TerrainStreamer), distanza di visione e nebbia (WorldRenderSettings), terreno lontano. Carica quando tocchi world/maps/, map.json, osm_extract.py (--map/--landcover), osm_terrain.py, terrain_import.gd, TerrainStreamer.cs, far_terrain.gdshader, terrain_sampler.gd, la scena Laguna.tscn, quando aggiungi una classe o cambi una quota, quando il terreno esce sfalsato, con buchi, non si carica, scatta o fa crescere la memoria, o quando un sistema di gioco deve sapere se un punto è barena, velma o canale.
---

# Mappa della laguna e terreno da OpenStreetMap

La mappa vera è **`world/maps/laguna/`**: un poligono WGS84 da Chioggia a Cortellazzo, 1260 km²,
fornito dall'utente. Il DEM non serve in laguna (barene 0,02–0,30 m, velme quasi tutte senza
dato, nessun fondale): il terreno si **costruisce dalle classi OSM** con quote "di gioco".

```
world/maps/laguna/
  map.json          FONTE DI VERITÀ: poligono, georeferenziazione, cartelle      (git)
  Laguna.tscn       Terrain3D + TerrainStreamer + FarTerrain + acqua + luce      (git)
  terrain_assets.tres  la texture neutra che la color map tinge                  (git)
  osm/raw/          risposte Overpass compresse: lo SNAPSHOT (25 MB)            (gitignored)
  osm/layers/       un JSON per livello, coordinate della mappa (64 MB)          (gitignored)
  terrain/          350 region terrain3d*.res (1,9 GB)                           (gitignored)
  classes/          classes_<x>_<z>.png: id della classe per vertice (11 MB)     (gitignored)
  far/              far_atlas.png 6144² (8 m/px) + .json, per il terreno lontano (gitignored)
  build/            tile grezzi temporanei e preview.png (.gdignore)             (gitignored)
```
`osm/`, `classes/`, `build/` hanno un `.gdignore`: l'editor non scandisce né importa JSON e tile
(gli script li leggono con `FileAccess`). I dati si rigenerano da zero così (~20 minuti):

```
python addons/venice_builder/tools/osm_extract.py --map world/maps/laguna/map.json      # ~1 min, riprende dalla cache
python addons/venice_builder/tools/osm_terrain.py --map world/maps/laguna/map.json      # ~7 min, 14 processi
godot --path . --headless --script res://addons/venice_builder/tools/terrain_import.gd -- --map res://world/maps/laguna/map.json   # ~3 min, cancella i tile
python addons/venice_builder/tools/osm_terrain.py --map world/maps/laguna/map.json --far-only   # solo atlante lontano, dalle classi
```
Python: **solo stdlib + numpy** (nel Python di sistema non ci sono scipy, cv2, PIL, shapely).
`--only="x,z;x,z"` (col segno uguale: argparse prende "-4" per un'opzione) rigenera poche region
per collaudo e non tocca l'atlante.

## 1. Convenzioni (da non rompere)

- **Georeferenziazione** (`map.json` → `osm_extract.configure_georef`): trasversa di Mercatore
  LOCALE, WGS84, scala 1, centrata su lon0 12,4274 / lat0 45,3685. Origine Godot nel centro,
  **X est, Z sud**, 1 unità = 1 m; mappa entro ±23,9 km, region −12…11. San Marco ≈ (−6862, −7283),
  Burano ≈ (−735, −12948), Chioggia ≈ (−11656, 16604). Senza `--map` `osm_extract.py` usa ancora
  il vecchio UTM 32 + offset del DEM: non mescolare i due.
- **1 m per vertice**, region da **2048** (origine `loc * 2048`, riga = Z, colonna = X).
- **Mare a quota 0** = `WaterVolume.SurfaceY`. Ogni classe di terra sta **sopra +0,15**, ogni
  classe d'acqua **sotto −0,1**: `terrain_sampler.gd` usa `WATER_THRESHOLD = 0.0`.
- Le classi sono un **dato di gioco**: `classes/classes_<x>_<z>.png` (grigio = id della tabella §2).
  Servirà alle texture per classe (control map) e all'host per secche, spawn, raccolta: non va
  ricavato dalla quota.

## 2. Classi

Dipinte in ordine di priorità; la regola dice **su quali pixel** una classe può scrivere.

| id | classe | quota base | da dove (regola) |
|---|---|---|---|
| 0 | mare | −8 | lato destro di `natural=coastline` |
| 1 | terraferma | +1,5 | default: fuori dalla laguna e non mare (litorali, Lido, Cavallino, entroterra) |
| 2 | laguna aperta | −1,5 | esterno dei multipoligoni `water=lagoon` |
| 3 | acqua bassa | −0,6 | `natural=wetland` **senza sottotipo** sull'acqua; `water=shallow` |
| 4 | valle / stagno | −1,0 | `water=pond/basin/moat/…`, `produce=fish*`, `landuse=aquaculture` |
| 5 | isola | +1,0 | buchi della laguna (rive quasi a muro) |
| 6 | prato / campi | +1,1 | `landuse=grass/meadow/farmland/…`, `leisure=park/garden` (solo su terra) |
| 7 | bosco | +1,2 | `natural=wood/scrub`, `landuse=forest` (solo su terra) |
| 8 | spiaggia | +0,6 | `natural=beach/sand/shingle` |
| 9 | barena | +0,3 | `wetland=saltmarsh/marsh/reedbed/…`; `natural=wetland` senza sottotipo sulla terra |
| 10 | velma | −0,15 | `wetland=tidalflat`, `natural=mud` |
| 11 | canale | −4 (area) / profilo | aree `natural=water` in laguna; assi `waterway=canal/river/fairway` |
| 12 | rio | profilo | asse con nome `Rio…`/`Rielo…` |
| 13 | ghebo | profilo | `waterway=stream/drain/ditch/tidal_channel` (incide 5, 6, 7, 9, 10) |

- **Il tranello dei tag**: in laguna `natural=wetland` senza `wetland=*` indica specchi d'acqua
  bassa che non emergono (relazione "Laguna Sud", nota esplicita), non paludi. Le barene sono
  `wetland=saltmarsh` e stanno quasi tutte nei **buchi** del multipoligono laguna.
- **Canali**: approfondiscono solo l'acqua, mai la terra. Profilo a V: `−(2 + min(larghezza, 40)/10)`
  sull'asse, quota della classe ospite sul ciglio. Larghezza: `width`, altrimenti **dalle bricole**
  (`man_made=dolphin`: 2 × mediana della distanza dall'asse entro 80 m, almeno 3), altrimenti rio
  8 m, canale con nome 30 m, senza nome 12 m. Ghebi: 5 / 3 / 2 m.
- **Lagune annidate**: dalla più grande alla più piccola (esterno → 2, buchi → 5).
- **Mare senza poligono**: la costa OSM sono way aperte (terra a sinistra; in Godot Z cresce verso
  sud, quindi un tratto con dz > 0 ha la terra a +X). Riga per riga vale l'incrocio più vicino.

## 3. Quote

Quota base + rumore a valori in **coordinate mondo** → box blur (raggio 3, 3 passate) → vincoli
terra/acqua. Ogni region è calcolata con 24 m di bordo in più: niente cuciture. Isole e acqua
entro 2 m da un'isola hanno una sfumatura **corta** (riva di 2–3 m): un gradino secco da +1 a −4 a
1 m per vertice esce come una fila di **denti triangolari**. Le rive vere (fondamenta) andranno
fatte come geometria. Per cambiare un livello: tabella `CLASSES` in `osm_terrain.py` e si rigenera.

## 4. Region di Terrain3D 1.1 (`terrain_import.gd`)

- **Quote a 16 bit** (RH: < 1 cm d'errore) e color map in **due copie**: RGBA8 con mipmap e S3TC
  con mipmap. Terrain3D ne usa una sola: **nell'editor** scarta la compressa e usa la RGBA8 (con la
  sola compressa le tinte sparivano: terreno bianco nell'editor, giusto in gioco); **in gioco** usa
  la compressa (`get_active_color_map`) e libera l'altra (`keep_uncompressed_color = false`), quindi
  la RAM/VRAM in gioco è quella della S3TC. `Region.save()` comprime solo dentro l'editor e scarta
  la compressa prima di scrivere: l'import comprime da sé (`Image.compress_from_channels`) e salva
  con `ResourceSaver`. Region da 3–7 MB su disco.
- `location` non si salva (la dà il **nome del file**, `terrain3d-04_08.res` = (−4, 8));
  `version` sì: `1.10` (`Terrain3DData::CURRENT_DATA_VERSION`).
- La color map si vede solo se c'è almeno una texture in `Terrain3DAssets`
  (`terrain_assets.tres` → `assets/textures/terrain/terrain_neutral.png`). Gli asset vanno in un
  `.tres` a sé: con `free_editor_textures` Terrain3D li ricarica dal file entrando nell'albero.

## 5. Streaming (`world/scripts/TerrainStreamer.cs`)

Terrain3D 1.1 tiene in RAM e VRAM **ogni** region aggiunta (~45 MB l'una con la color map
compressa): 350 region non ci stanno. Lo streaming ufficiale arriverà in 1.2 (PR #1004, #1059 per
più bersagli); fino ad allora `TerrainStreamer` carica le region entro `Radius` dai bersagli e
scarica quelle oltre `Radius + 1`.

Misure del volo (40 km a 220 m/s, RTX 3070 Ti; quelle a camera ferma per livello sono più sotto): `Radius` 1 (default) → ~15 region, VRAM 1,0 GB,
RAM 0,66 GB, scatto peggiore ~180 ms a ogni confine di region; `Radius` 2 → ~35 region, 1,75 GB,
1,4 GB, ~330 ms. **I "fps" del volo sono il tetto del ritmo dei frame (~6,1 ms, 165 Hz), non il
costo della mappa**: a camera ferma, 1920×1080, rendering CPU 0,3 ms e GPU 1,7–2,3 ms (di cui
Terrain3D ~1,6 ms; acqua, piano lontano e ombre pochi decimi ciascuno). Per misurare usare
`RenderingServer.viewport_get_measured_render_time_cpu/gpu`, non il tempo fra frame. Gli scatti sono la ricostruzione degli
array di Terrain3D (∝ region caricate): a velocità di barca i confini arrivano ogni 2–3 minuti.

### Distanza di visione e nebbia (`world/scripts/WorldRenderSettings.cs`, nodo in `Laguna.tscn`)

`TerrainStreamer.Radius` in gioco non si scrive a mano: lo imposta `WorldRenderSettings` dal
livello scelto nelle impostazioni (skill `ui-hud` §4), insieme a densità della nebbia e `far` della
camera attiva. **La distanza la decide l'host** (`SessionRenderDistance`, replicata da un
`MultiplayerSynchronizer` figlio in modalità Always, 1 invio/s: il nodo è statico, lo stato di spawn
non viaggerebbe); un client usa `min(host, sua)` e, finché non l'ha ricevuta, il livello 0.
Verificato con due processi ENet: valore iniziale, cambio a partita in corso, client che scende.

Misure del 17/11/2026, RTX 3070 Ti, 1920×1080, V-Sync spento, camera ferma su San Marco
(−6600, −7150), nebbia 30 %. CPU/GPU = `viewport_get_measured_render_time_*` mediate su 200 frame,
a 60 m di quota / a 3 m (barca). VRAM = `RENDERING_INFO_VIDEO_MEM_USED` (le texture ne sono il
90 %): ~37 MB per region a regime. Scatto = frame più lungo attraversando un confine di region.

| Livello | r | Region (a regime) | Terreno vero ≥ | Far camera* | CPU | GPU 60 m / 3 m | VRAM | RAM | Carico | Scatto al confine |
|---|---|---|---|---|---|---|---|---|---|---|
| Bassa (default) | 1 | 9 (12 dopo il confine) | 2 km | 7,9 km | 0,28 ms | 1,26 / 1,06 ms | 0,82 GB | 0,44 GB | 0,2 s | 98 ms |
| Media | 2 | 25 (30) | 4 km | 15,9 km | 0,30 ms | 1,27 / 1,08 ms | 1,42 GB | 1,04 GB | 2,0 s | 227 ms |
| Alta | 3 | 49 (56) | 6 km | 23,8 km | 0,28 ms | 1,47 / 1,15 ms | 2,32 GB | 1,93 GB | 5,0 s | 543 ms |
| Molto alta | 4 | 80 (89) | 8 km | 30 km | 0,35 ms | 4,49 / 2,67 ms | 3,47 GB | 3,09 GB | 10,5 s | 892 ms |

\* con nebbia 30 %; a 0 % / 100 % il far della Bassa va da 11,6 a 2,7 km. Il costo di rendering
resta basso fino all'Alta: quello che cresce è **lo scatto a ogni confine** (la ricostruzione degli
array di Terrain3D, ∝ region) e la memoria. "Molto alta" va presa come limite: quasi un secondo di
blocco ogni 2 km. Il LOD del terreno a tre livelli (non fatto) è ciò che renderebbe sensati raggi grandi.

**Nebbia**: esponenziale. La densità si ricava dall'opacità voluta al **bordo garantito** del terreno
vero, `r × 2048 m` (bersaglio sul bordo della sua region): da 50 % (impostazione 0) a 95 % (1),
quindi abbassare la distanza infittisce la nebbia e il passaggio terreno vero → piano lontano resta
sempre almeno a metà velato. `fog_sky_affect` = la stessa opacità. Il `far` della camera attiva è
dove la nebbia arriva al 98 % (1–30 km): oltre non si vedrebbe nulla, e lì finiscono piano lontano
e acqua. La `fog_density` scritta nella scena (0,00012) vale solo nell'editor.

- **Bersagli**: i Node3D del gruppo `terrain_stream_target`, altrimenti la camera attiva; nell'editor
  (`[Tool]`) la camera della vista 3D con `EditorRadius`. L'host dovrà mettere nel gruppo anche i
  giocatori remoti e gli NPC; la collisione dinamica di Terrain3D però segue **un solo**
  `collision_target`: è una lacuna aperta per il multiplayer.
- **Camera di Terrain3D**: Terrain3D la prende una volta sola e non la cambia finché resta valida;
  con `PreviewCamera` di `Laguna.tscn` ancora nell'albero maglia di dettaglio e collisione restavano
  lì e l'avatar cadeva attraverso le isole. In gioco lo streamer gli passa a ogni frame la camera
  attiva (`FollowActiveCamera`).
- **Caricamento** a thread (`ResourceLoader.LoadThreadedRequest`), al più `MaxInsertionsPerFrame`
  per frame; all'avvio le 3×3 sotto i bersagli in modo bloccante.
- **Una ricostruzione per frame**: ogni `add_region`/`remove_region` con `update = true` ricostruisce
  TUTTI gli array (quote, controllo, colore di tutte le region caricate). Si chiamano con `false` e
  si fa un solo `UpdateMaps()` a fine frame. Prima: scatti di secondi e picchi di 16 GB di VRAM.
- **Evict vero**: `remove_region` segna la region come cancellata ma la tiene in `_regions` finché
  un `save_region` non la toglie — cancellando anche il file! Si chiama `save_region` su una
  cartella senza region (`user://terrain_evict_never_contains_regions`): la region esce dalla
  memoria e nessun file viene toccato. Non usare mai la cartella dei dati.
- **GC dopo ogni evict**: le `Variant` C# con cui la region passa a Terrain3D la tengono viva fino
  al finalizzatore (misurato: RAM da 1,4 a 7,5 GB in 40 km, poi D3D12 "out of memory" e crash).
  Niente `Terrain3DRegion.Bind` sulle region (attacca uno script C#): bastano `Set`/`Call`.
- **region_size**: Terrain3D 1.1 nasce con region da **256 m** e il valore scritto nella scena non
  sopravvive all'inizializzazione senza `data_directory`. Lo streamer lo impone (e anche la
  compressione della color map) prima di aggiungere region; se resta diverso, errore in console.
- Il nodo `Terrain3D` della scena ha `data_directory` **vuota**: con la cartella delle region
  l'editor proverebbe a caricarle tutte (17 GB).
- `DebugLog` stampa caricamenti, scaricamenti e RAM.

## 6. Terreno lontano (`world/shaders/far_terrain.gdshader`)

Un solo piano grande quanto la mappa a +0,25 m: disegna le terre dall'atlante 8 m/px (alfa =
terra, `discard` sull'acqua: la fa il piano d'acqua) e **non** si disegna dove una region è
caricata (`loaded_mask`, una texel per region, aggiornata dallo streamer). Le region fuori dal
poligono sono riempite nell'atlante: mare se il lato più vicino del poligono è quello di
**chiusura** (ultimo → primo vertice, Cortellazzo → Chioggia, che corre in mare aperto), terraferma
altrimenti. L'atlante si importa compresso in VRAM con mipmap (50 MB invece di 150).

## 7. Trappole del download (`osm_extract.py`)

- `--map`: tutto OSM dentro il poligono (`poly:` + tasselli da 0,05°, relazioni una volta sola),
  cache compressa in `osm/raw`: rilanciare riprende; per riscaricare, cancellare la cartella.
  `--landcover --bbox` resta per riquadri di sole coperture.
- Overpass pubblico: il 17/11/2026 `overpass-api.de`, `private.coffee` e `mail.ru` davano solo
  502/504; l'istanza dell'interfaccia di overpass-turbo (`api.overpass-turbo.eu/interpreter`)
  rispondeva in un secondo ed è la prima di `OVERPASS_URLS`. Un timeout lato server arriva come 200
  **troncato** con un `remark`: è trattato come errore.
- Il multipoligono "Laguna di Venezia" ha migliaia di buchi: niente test punto-in-anello quadratico.
- Tagging recente e in evoluzione (way del 2025–26): `osm/raw` è lo snapshot da tenere.

## 8. Lacune volute

- Niente texture per classe: solo la color map. Le texture PSX vanno scritte nella control map
  dalle classi, senza rigenerare le quote.
- Edifici, strade, segnaposto non ancora generati sulla mappa vera (i dati ci sono in `osm/layers`).
  Per giocarci c'è **`world/scenes/MainLaguna.tscn`**: copia di `Main.tscn` con `Laguna` al posto di
  `TestLevel` e `GameWorld.SpawnOrigin` in Piazza San Marco (−6960, 1, −7290; isola piatta a +1,00 m).
  Si lancia con "Esegui scena corrente" (anche a più istanze); `Main.tscn` resta sul livello di
  prova perché `tools/playtest_*.gd` lo usano. Gli avatar nascono in `GameWorld.SpawnPointFor(peerId)`
  (deterministico: il client si colloca da sé, lo spawner non replica la posizione) e i pickup
  portano la quota nei dati di spawn (`"y"`).
- Collisione del terreno attorno a un solo bersaglio (vedi §5). Niente argini delle valli da
  pesca, niente bit di navigazione nella control map. Acqua, onde e marea: skill `water-ocean`
  (la mappa dell'acqua `water/` si rigenera da `classes/` con `water_map.py`, ~20 s).
- `Terrain3DMaterial.world_background = 0` (NONE): fuori dalle region Terrain3D disegnerebbe un
  piano a quota 0, che con la marea diventava una secca finta in mare aperto. Lì la terra la
  disegna il piano lontano, l'acqua il piano d'acqua.
