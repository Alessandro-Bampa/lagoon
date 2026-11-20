---
name: asset-ingest
description: Pipeline d'ingresso degli asset 3D da un modello sorgente "vero" (molti poligoni, texture PBR ad alta risoluzione) — smistamento per categoria (imbarcazione, vestito/equipaggiamento, arma), passi base comuni in Blender (orientamento, misura reale, semplificazione, cottura del colore ORIGINALE, resa nello stile del gioco) e sotto-pipeline di categoria. Carica quando l'utente dice "voglio inserire..." una nuova barca, un nuovo vestito/equipaggiamento o una nuova arma, quando passa un file .blend/.glb/.fbx/.obj da portare nel gioco, o quando tocchi tools/blender/ingest/.
---

# Ingresso degli asset

## 1. Smistamento

La richiesta arriva come "voglio inserire <cosa>" piu' un modello sorgente e la sua descrizione
(cos'e', categoria). Si sceglie la sotto-pipeline:

| Richiesta | Categoria | Sotto-pipeline | Stato |
|---|---|---|---|
| nuova imbarcazione | `boat` | `tools/blender/ingest/boat.py` + skill `vehicles-boats` §9-13 | automatica fino a `.glb` + `BoatConfig`; poi taratura con le prove |
| nuovo vestito / equipaggiamento | `outfit` | `tools/blender/ingest/outfit.py` + skill `character-wearables` | automatica |
| nuova arma | `weapon` | da definire (bocca di fuoco, parti mobili, viewmodel: skill `blender-pipeline` §3, `character-animation` §7) | base automatica, resto da fare |

Senza modello sorgente (solo immagini) un capo semplice si puo' ancora costruire coi template
parametrici di `character-wearables` §3, ma la strada maestra e' questa: **si parte da un modello
vero e lo si trasforma, non lo si reinventa**.

## 2. Comando

```
"<blender 5.2>" --background --factory-startup --python tools/blender/ingest/ingest.py -- tools/blender/ingest/manifests/<id>.json [preview] [noexport]
```

Un manifesto per asset in `tools/blender/ingest/manifests/`. Il file sorgente NON entra nel repo (come
i pack): il manifesto ne riporta il percorso, la descrizione e la licenza, che va anche in
`assets/models/LICENSES.md`.

```json
{
  "id": "backpack", "category": "outfit",
  "description": "cos'e', materiali, dettagli",
  "source": "C:/.../modello.blend", "license": "...",
  "source_front": "+Y", "source_up": "+Z",
  "size": {"axis": "z", "meters": 0.56},
  "budget": {"tris": 2600},
  "parts": {"main": {"role": "keep", "share": 0.5}, "strap": {"role": "keep", "share": 0.5},
            "buckle": {"role": "bake"}},
  "texture": {"bake_size": 2048, "size": 512, "saturation": 0.88, "value": 0.97, "ao_strength": 0.25},
  "names": {"name": ["en", "it"]},
  "outfit": { ... sezione della categoria ... }
}
```

- `source_front`: l'asse del file sorgente che diventa il "davanti" della categoria (`base.FRONT`):
  **outfit** = dove guarda chi lo indossa (per uno zaino: il lato degli spallacci); **boat** = la
  prua; **weapon** = la bocca di fuoco. Prima di scriverlo si RENDERIZZA il sorgente da piu' lati.
- `size`: ingombro reale lungo un asse, dopo l'orientamento. I modelli in giro hanno scale a caso
  (lo zaino di prova era alto 0,92 m: 1,7 volte il vero): **la misura la decide il manifesto**.
- `parts`: sottostringa del nome degli oggetti sorgente -> `keep` (geometria, con la sua quota
  `share` del budget) o `bake` (minuterie: fibbie, cerniere, viti, targhette, che restano SOLO nella
  texture). Ogni oggetto che non corrisponde a nessuna parte e' ignorato. Un sorgente fatto di UNA
  mesh sola (la lancia di prova) e' una parte sola `keep` con `share` 1.
- `texture_dir` (facoltativo): FBX/OBJ con le texture in una cartella a parte e i materiali senza
  immagine. A ogni materiale col Base Color scollegato `ingest.link_textures` collega il file che
  contiene il nome del materiale e `BaseColor`/`Albedo`/`Diffuse` (report: `textures`).

## 3. Passi base (`ingest/base.py`)

1. **Carica**: un `.blend` si APPENDE (solo gli oggetti, con materiali e immagini impacchettate:
   niente scene ne' script del file). `.glb/.gltf/.fbx/.obj` con gli importatori. Sempre in un
   Blender separato, in background, con `--factory-startup`.
2. **Normalizza**: trasformazioni applicate, terna (`source_front`, `source_up`) -> convenzione della
   categoria, scala alla misura reale.
3. **Semplifica**: ogni parte `keep` decimata (collapse) alla sua quota di triangoli, poi unite; ogni
   faccia ricorda la sua parte (attributo `part`). Budget indicativi: oggetto indossato 1.500-3.500,
   elmetto 500-1.500, barca e arma da stabilire con le loro pipeline.
4. **Cuoce il colore ORIGINALE**: UV nuove (smart project + impacchettamento: le cinghie decimate
   fanno centinaia di isole), poi Cycles "selected to active" dal pesante al leggero: tipo EMIT col
   *base color* del sorgente collegato all'emissione (niente luci, niente metallicita') e occlusione
   AO del pesante. Nessun colore e' inventato: si trasforma quello che c'e'.
5. **Stile del gioco** (`stylize`): media di blocchi fino alla risoluzione finale (256-512 px),
   saturazione e luminosita' appena giu', contrasto appena su, occlusione leggera, 5 bit per canale
   con dithering (`charlib.psx`). Materiale `psx_material`: filtro nearest, niente riflessi.
6. **Confronto** (`preview`): `%TEMP%/ingest/<id>_compare.png`, sopra il sorgente e sotto il risultato
   dalle stesse viste, e `<id>_texture.png`. Si controlla che forma e colori siano quelli originali.
   Se scuriscono: `value`, `ao_strength`; se la texture e' sfocata: `size` piu' alta o meno isole.

## 4. Sotto-pipeline OUTFIT (`ingest/outfit.py`)

Sezione `outfit` del manifesto: `slot`, `layer` (strati di `character-wearables` §2), `mount`
(`back`, `chest`, `head`, `none`), `top` (quota della cima del blocco rigido, espressione sui giunti),
`clearance` (distanza dal corpo), `conform` (parti morbide da adattare al busto), `strap_clearance`,
`blend_zone`, `max_edge`, `rigid_bone`, `rigidity`, `covers`, `ground`, `first_person`, `item`.

1. **Montaggio** sul corpo a riposo: centrato in x, cima a `top`, spinto fuori dal corpo finche' le
   parti rigide stanno a `clearance` (raggi verso il corpo).
2. **Parti morbide** (spallacci, cinghie): il modello le avvolge attorno a un busto generico, quasi
   mai il nostro (lo zaino di prova: anello profondo 17 cm contro un busto di 30). Quindi:
   - l'anello si **allunga in profondita'** in proporzione finche' arriva al petto;
   - si **infittisce** (spigoli > `max_edge`, solo fra zaino e corpo): un tratto decimato sopra la
     spalla sarebbe un segmento dritto dentro il busto;
   - ogni vertice si **proietta radialmente** dall'asse della colonna (fermo alla quota di
     `spine_03`, cosi' sopra la spalla il raggio entra dal trapezio e non dal collo aperto) sulla
     superficie del SOLO busto (braccia e testa escluse) + distanza + spessore locale conservato;
   - il peso della conformazione sale allontanandosi dal piano d'appoggio (`blend_zone`): le radici
     restano attaccate al blocco rigido.
3. **Pesi**: blocco rigido su `rigid_bone` con `rigidity`, parti conformate dai vertici del corpo vicini.
4. **Uscite**: le stesse dei capi a ricetta (`wearables/wexport.finalize`): glb indossato con
   `.import` e `BoneMap`, modello a terra, `.tres`, traduzioni, prima persona se serve, anteprima a
   mosaico `%TEMP%/wearables/<id>.png`.

Poi: reimport di Godot e `tools/playtest_outfit.gd` (primi piani davanti e dietro).

## 5. Sotto-pipeline BOAT (`ingest/boat.py`)

`ingest.py` le passa il leggero E il pesante (prua a +Y, misura reale). Produce
`assets/models/boats/<name>.glb` (+ `.import` con le texture non compresse, scritto solo se manca) e
`resources/boats/<id>.tres`; con `noexport` salva `%TEMP%/ingest/<id>_boat.blend`, con `preview`
`<id>_boat.png` (sopra il fianco con l'acqua alla DWL e la `HydroHull` in rosso trasparente, sotto
tre quarti). Esempio completo: `manifests/lancia_cabinata.json` (lancia da 12 m, 9 t, da un FBX
"Speed Boat" a mesh unica).

```json
"boat": {
  "name": "Lancia_Cabinata",                      // nome del .glb (default: id in TitleCase)
  "hull_min_length": 0.5,                         // pezzi dello scafo: componenti lunghe >= 0,5 L
  "waterline": {"displacement_kg": 9000},         // oppure {"draft": 0.55} (m sotto la DWL del punto piu' basso)
  "materials": {"Mat_1": "Hull", "Mat_2": "Deck", "Mat_3": "Interior", "Mat_4": "Metal"},
  "default_material": "Hull", "livery_grey": false,
  "glass": {"materials": ["Mat_3"], "color": [0.486, 0.547, 0.572], "tolerance": 0.05},
  "hydro": {"stations": 28, "points": 9, "max_keel_step": 0.15, "max_extra_stations": 10, "collision_stations": 9},
  "deck": {"tris": 2500, "boxes": [[-1.3, 1.3, -5.95, -5.2, 1.14, 1.24]]},   // x0 x1 y0 y1 z0 z1: ponticello di poppa
  "mounts": {                                     // metri nella terna FINALE: x dritta, y prua, z sopra la DWL
    "Seat_Helm": {"at": [0.64, -0.36, 1.0], "snap": "floor"},           // cala sul piano sotto
    "Mount_Ladder": {"at": [0.6, -5.93, 2.5], "snap": "floor", "offset": [0, 0, -0.06]},
    "Mount_Prop": {"at": [0, -5.0, -0.8], "rot": [3, 0, 0]}              // gradi
  },
  "console_mount": false,                         // true: Mount_Console = Seat_Helm + 0,25 m
  "config": {
    "template": "res://resources/boats/gozzo.tres",   // propulsione, idrodinamica, accessori
    "helm": "wheel", "console": null, "livery": null, "drop_mass_items": true,
    "hull_com_height": 0.4,                           // baricentro dello scafo, frazione chiglia->falchetta
    "set": {"Rudder.Area": 0.32, "Engine.MaxPowerKw": 55.0}   // campi delle sotto-risorse del modello
  }
}
```

Passi:

1. **Involucro**: solo le componenti connesse del pesante lunghe almeno `hull_min_length` (fasciame,
   falchetta, coperta; NON cabina, bitte, bulloni). Per stazione y la mezza sezione esterna si legge
   con raggi orizzontali da fuori (la falchetta e' dove il raggio smette di trovare il fasciame:
   manca o rientra di oltre 5 cm / 8%) e verticali da sotto (fondo quasi piano). Le facce colpite
   dal retro si saltano (il pagliolo del pozzetto visto da sotto oltre lo specchio arrotondato).
   La murata salendo non rientra mai: bottazzo e tumblehome diventano parete verticale.
2. **DWL**: prima stima dalle sezioni dense (90 stazioni), poi risolta sulla `HydroHull` stessa
   (bisezione sul volume tagliato, come `check_boat_model.py`) e ricostruita: le corde della carena
   semplificata stanno dentro le curve (~5% di volume in meno: 1,8 cm di pescaggio e LCB spostato).
   Origine a mezza lunghezza, in mezzeria, z = 0 sulla DWL.
3. **`HydroHull`** (pipeline-nuove-barche §4.2, metodo A): stazioni piu' fitte a prua + raffittimento
   adattivo dove la chiglia salta di oltre `max_keel_step` (specchio arrotondato, piede di prua).
   Ogni sezione a `points` punti alla STESSA frazione di perimetro (mediana di Douglas-Peucker
   stazione per stazione; l'ultimo 30% spezza i lati piu' lunghi, un fondo piatto non ha scarti);
   il punto interno piu' vicino alla DWL si sposta sulla DWL. Specchio, prua e coperta alla falchetta
   chiusi, saldata a 0,5 mm, triangolata (beauty), diagonali girate dove due triangoli si
   intersecano. `SwimCollision` = stesse sezioni senza coperta; `HullCollision` = 9 sezioni a 5 punti.
4. **Materiali per nome**: ogni faccia del leggero prende il materiale sorgente della faccia pesante
   piu' vicina, rinominato da `materials`; i vetri (`Glass`, alpha 0,35) dal colore della texture
   sorgente al centro UV della faccia (`glass.color` si campiona prima sulla texture). Tutti usano la
   texture cotta: senza livrea nella config la barca ha i colori originali (la livrea MOLTIPLICA:
   `livery_grey` porta i materiali di livrea a grigio chiaro).
5. **`DeckCollision`** = la resa decimata a `deck.tris` (pagliolo, cabina, murate, sedili come sono)
   + scatole `deck.boxes`.
6. **Vuoti** da `mounts` (`snap: floor` = raggio verso il basso sulla `DeckCollision`).
7. **`BoatConfig`** dal `template`: scafo nuovo, `uid` tolto, `set` applicato, sotto-risorse e
   risorse esterne rimaste senza riferimenti tolte. `HullMass` = dislocamento − masse del modello
   (motore al `Mount_Engine`/`Mount_Motor`, `MassItems` se tenuti); `HullCenterOfMass` perche' il
   baricentro TOTALE cada sulla verticale dell'LCB della `HydroHull` (assetto ~0).
8. **Controlli** (report `warnings`/`errors`): carena chiusa, coerente, senza autointersezioni ne'
   degeneri, triangoli (avviso solo oltre 1,5× i tetti 600 / 1000 / 1500: conta il costo per passo,
   e l'utente accetta qualche centinaio in piu' se serve alla forma), lato del fasciame bagnato ≤ L/10,
   scarto resa/carena nella fascia bagnata (`render_vs_hydro_m`, 90° percentile), attacchi
   subacquei sotto la DWL, GM stimato > 0 (KB + BM − KG dalle masse), posto di guida (pagliolo sotto
   i piedi, seduta a 0,46 ± 0,08 m 4 cm dietro, testa sotto il tetto, visuale come
   `check_boat_model.py`, vetri esclusi).

Poi: `check_boat_model.py` sul `.glb`, `Godot --headless --import`,
`boat_trials.gd -- res://resources/boats/<id>.tres` (statica, poi dinamica). Taratura (vehicles-boats
§9b passi 4-5) cambiando `config.set`/`mounts` nel manifesto e rigirando: il `.tres` si RISCRIVE.
In gioco (a mano, una volta): scena `vehicles/scenes/boats/<Nome>.tscn` ereditata da `Boat.tscn` con
`Assembly.Config`, istanza nel livello con `VehicleId` unico, nome del nodo nella lista di
`tools/playtest_boats.gd` (timone, guida, tempesta, salita dall'acqua).

Lancia di prova (12 m): `HydroHull` 1288 triangoli (28 stazioni + 10 di raffittimento), carena alla
DWL 9000 kg, pescaggio 0,53 m, bordo libero 1,2 m, GM 0,94 m, rollio 2,8 s, 8,7 kn con 55 kW,
virata 2,7 L, 0,57 ms/passo; galleggia in Godot a quota 0,000 e assetto 0,00°. Avviso noto: visuale al timone 7/21 (davanzale della cabina
vera a 1,0 m dal pagliolo, occhi a 1,2: si vede dritto, non in basso).

Lacune: niente sovrastrutture stagne nella `HydroHull` (la cabina non da' riserva ne' superficie al
vento); la ruota del modello resta nella resa (niente `Wheel`/`Hand_*`: con `console: null` il pilota
non ha le mani sul volante); elica e timone dell'entrobordo non sono nel modello e non vengono
inventati (solo gli attacchi); la misura reale va decisa da riferimenti interni (sedia, altezza della
cabina) quando il sorgente non la dice.

## 6. Sotto-pipeline WEAPON (da fare)

Base come sopra (bocca a +Y). Da definire: marcatori `Muzzle`/`Grip`/`Support`/`EyeHip`/`EyeAim`,
parti mobili separate e nominate (caricatore, otturatore, grilletto) per le clip, viewmodel (braccia
del corpo, skill `character-wearables` §3 prima persona) e `W_<arma>.glb` per la terza persona.

## 7. Trappole

- Il file sorgente puo' avere immagini non impacchettate: `load_source` avvisa
  (`[ingest] ATTENZIONE`). La cottura le vuole tutte.
- `size` va misurato DOPO l'orientamento (asse del gioco).
- Le minuterie `bake` finiscono nella texture solo dove la superficie leggera passa vicino
  (`ray_distance`): una fibbia che pende lontano dalla sacca sparisce. Se serve, la parte va `keep`.
- Un budget stretto + infittimento delle cinghie: il conteggio finale e' nel report (`tris`).
- Barca: `ray_distance`/`extrusion` troppo larghi fanno cuocere l'antivegetativa del fondo sulle
  murate di prua (macchie rosse): per uno scafo da 12 m a 5000 triangoli bastano 0,08 / 0,03.
- Barca: la resa decimata come `DeckCollision` lascia spesso una fessura fra cimasa e battagliola
  dove si sale dall'acqua, e la sonda dell'arrampicata non trova dove atterrare (la lancia: salita
  FALLITA). Rimedio: una scatola in `deck.boxes` dalla cimasa al primo piano interno (ponticello,
  vehicles-boats §6), poi `playtest_boats.gd -- <dir> <Nodo>` per la salita con la scaletta.
- Barca senza misura nel file (la lancia era lunga 24 unita'): la scala si prende da riferimenti
  interni — seduta a 0,46 m, altezza libera in cabina ~2 m — e il dislocamento dalla classe.
