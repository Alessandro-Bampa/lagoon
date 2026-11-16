---
name: venice-building-kit
description: Kit modulari dei palazzi veneziani (stile PSX/HL2) per la generazione procedurale di edifici — `venice` pulito e `venice_ruin` in stile S.T.A.L.K.E.R. con pezzi rotti. Carica quando tocchi assets/models/buildings/, tools/blender/build_venice_kit.py, quando scrivi o modifichi un generatore di edifici, aggiungi un modulo (muro, finestra, porta, tetto, scala, pavimento, maceria), cambi il colore di una facciata, lavori sui vetri trasparenti (glass_psx), o un modulo risulta ruotato, sfalsato, senza collisione o con texture sfocate.
---

# Kit dei palazzi veneziani

Tutto generato da `tools/blender/build_venice_kit.py` (texture con numpy, mesh da coordinate):
nessun pack esterno, nessun `.blend` da conservare. **Lo script è la sorgente**: non ritoccare a
mano i `.gltf`, si rigenerano.

```
python tools/blender/blender_client.py tools/blender/build_venice_kit.py                       # i due kit
python tools/blender/blender_client.py tools/blender/build_venice_kit.py venice_ruin preview   # un kit + palazzetto di prova e render in %TEMP%
```

Due kit dalla stessa sorgente, in `assets/models/buildings/<kit>/`:

| Kit | Cos'è |
|---|---|
| `venice` | pulito: 41 moduli, ~2.300 triangoli, vetri opachi |
| `venice_ruin` | stile S.T.A.L.K.E.R.: **gli stessi 41 moduli con gli stessi nomi e misure** (texture sporche, intonaco caduto, muschio, vetri trasparenti) + 20 pezzi rotti (§3). 61 moduli, ~3.400 triangoli |

Stesso nome = stesso ingombro: un generatore passa da un kit all'altro cambiando solo la cartella, e
può mescolarli. Ogni kit ha un `<modulo>.gltf` + `.bin` per modulo e `textures/*.png` **condivise**
(i `.gltf` le referenziano per percorso: per questo non sono `.glb`, che Godot spacchetterebbe in
una copia delle texture per file). `RUIN` nello script decide tutto: `grime()` sporca ogni texture,
`MODULES` marca i moduli solo-rovina col terzo campo.

## 1. Griglia e assi (coordinate Godot)

- Cella **2 × 2 m**, piano **3 m** da pavimento a pavimento: 2,7 m netti + 0,3 m di solaio.
- **Moduli di bordo** (`ext_*`, `int_*`, `floor_edge`): origine al centro del bordo, a quota
  pavimento del piano. Corrono lungo X. Nei muri esterni la faccia esterna sta su `z = 0` e guarda
  **+Z**; lo spessore (0,3 m) va verso −Z, cioè dentro la cella. I muri interni (0,16 m) sono
  centrati sulla linea di griglia.
- **Moduli di cella** (`floor_*`, `ceiling_*`, `roof_*`, `stair_landing`): origine al centro della
  cella. I soffitti stanno già a +2,7 m: si posano con la stessa origine del pavimento del piano.
- Per gli altri tre lati si ruota attorno a Y: lato est (esterno a +X) = +90°, nord = 180°, ovest = −90°.
- I muri **non hanno testate**: agli spigoli convessi le facce esterne si incontrano a filo e i
  corpi si compenetrano. Serve solo il pezzo d'angolo (sotto).

## 2. Moduli

| Modulo | Note |
|---|---|
| `ext_wall`, `ext_window_rect`, `ext_window_arch`, `ext_window_gothic` | muro cieco; finestre con cornice in pietra, davanzale, scuri verdi aperti |
| `ext_window_rect_shut`, `ext_window_arch_shut`, `ext_window_gothic_shut` | le stesse finestre con gli **scuri chiusi** (superficie `shutters`, dentro l'imbotte; il vetro resta dietro) |
| `ext_window_grille` | finestrella alta con inferriata (piano terra, mezzanini) |
| `ext_window_high` | finestrella alta (1,85–2,55 m): **l'unica finestra da mettere sul fondo di un vano scala**, sta sopra il pianerottolo |
| `ext_door`, `ext_portal` | varco 1,1 × 2,3 m e portale ad arco 1,3 m (porta d'acqua). Senza anta: `door_ext`, `door_portal` |
| `ext_balcony` | portafinestra gotica con poggiolo calpestabile e balaustra |
| `ext_quadrifora` | **modulo doppio (4 m)**: origine al centro delle due celle |
| `ext_plinth`, `ext_stringcourse`, `ext_cornice` | zoccolo (piano terra, da saltare sotto le porte), marcapiano (alla quota di ogni solaio), cornicione con gronda (alla quota di colmo muro: `piani × 3`) |
| `ext_corner_convex`, `ext_cornice_corner` | cantonale bugnato e angolo del cornicione. Origine sullo spigolo, i due muri partono verso **+X e −Z**; ruotare di 90° per gli altri spigoli |
| `ext_corner_concave` | chiude dal lato stanza la tacca degli spigoli concavi (piante a L) |
| `roof_slope` | falda, gronda a +Z come i muri. Sale di 0,8 m per cella: l'anello successivo va a +0,8 m |
| `roof_corner_outer`, `roof_corner_inner` | displuvio e compluvio, angolo basso a (−X, +Z) |
| `roof_ridge`, `roof_ridge_end`, `roof_peak` | chiudono le strisce larghe una cella (colmo lungo X; testata chiusa a −X) |
| `chimney` | camino a campana rovesciata; affonda 0,6 m sotto l'origine |
| `int_wall`, `int_door`, `int_arch`, `int_post` | tramezzi; `int_post` copre incroci, angoli e testate libere. Anta: `door_int` |
| `floor_terrazzo`, `floor_planks`, `floor_paving` | un quad; `floor_paving` (masegni) va bene anche per corti e fondamenta |
| `ceiling_plaster`, `ceiling_beams` | soffitto del piano; le travi corrono lungo Z |
| `floor_edge` | testa del solaio attorno ai vani scala |
| `stair_u` | **la scala da usare di norma.** 2 × 2 celle, origine al centro del bordo d'ingresso. Rampa a sinistra (−X) verso −Z fino al pianerottolo a 1,5 m, rampa a destra che torna indietro: **arriva sullo stesso bordo da cui parte**, quindi sul pavimento del piano di sopra per costruzione, e si impila identica a ogni piano |
| `stair_straight` | 1 × 2 celle, sale 3 m verso **−Z** partendo dall'origine. Arriva sul bordo opposto: la cella dopo l'arrivo **dev'essere pavimento** del piano di sopra, mai un muro |

**Muri attorno a `stair_u`**: i tre lati del vano diversi dall'ingresso devono essere muri
(esterni o tramezzi). Sul fondo va `ext_wall` o `ext_window_high`; sui fianchi `ext_wall`: una
finestra normale verrebbe tagliata a metà dal pianerottolo (1,5 m) o dalle rampe. La scala rientra
di 13 cm da quei tre lati (`STAIR_INSET`): a filo cella sbucava dalla faccia esterna e attraversava
i vetri. `stair_straight` rientra di soli 2 cm e di fianco vuole un muro cieco o il pavimento.

**Porte e vano scala**: sui tramezzi che chiudono il vano non va mai `int_door`/`int_arch`: si
aprirebbero contro il fianco di una rampa. Al vano si accede solo dal lato d'ingresso.

**Spessore del solaio**: soffitto (2,7 m) e pavimento del piano sopra (3 m) sono due quad
separati, e i tramezzi arrivano a 2,7 m. Su **ogni lato in cui una cella col pavimento confina con
una senza** (vano scala, cavedio) va un `floor_edge` alla quota del pavimento, con la faccia verso
il vuoto (la faccia guarda +Z: ruotare come un muro esterno visto dal vuoto). Senza, dal vano si
vede dentro lo spessore. I `floor_*_broken` hanno già il loro bordo.

Sopra una scala vanno omessi pavimento e soffitto di **tutte** le celle del suo ingombro (4 per
`stair_u`, 2 per `stair_straight`), e solo quelle. A 1,8 m di larghezza la rampa dritta lasciava una fessura di 10 cm per lato col pavimento del
piano. Sono solette con
l'intradosso, non blocchi pieni fino a terra: un blocco pieno, impilato, toglie l'altezza alla rampa
di sotto (per questo `stair_half` e `stair_landing` non esistono più). Le ante hanno
l'origine sul cardine e nessuna collisione: aprirle è compito del gioco.

## 3. Pezzi rotti (solo `venice_ruin`)

| Modulo | Note |
|---|---|
| `ext_wall_breach`, `int_wall_breach` | breccia fino a terra: **ci si passa** |
| `ext_wall_hole` | squarcio alto, non praticabile |
| `ext_wall_ruin_hl`, `_low`, `_lh` (e `int_wall_ruin_*`) | muro con la sommità crollata. Gli estremi stanno sempre a quota piena o a 1 m (`LOW`): si accostano come `[pieno][hl][low]…[low][lh][pieno]`, lungo +X del modulo. Sopra non va messo nulla |
| `ext_window_rect_broken`, `ext_window_gothic_broken` | vetri sfondati; la rettangolare ha un solo scuro, appeso |
| `ext_window_arch_boarded` | sbarrata con assi inchiodate |
| `floor_terrazzo_broken`, `floor_planks_broken` | solaio sfondato: **si cade di sotto** |
| `ceiling_plaster_broken`, `ceiling_beams_broken` | stesso foro (`HOLE`): vanno sotto un pavimento `_broken`, nella stessa cella del piano inferiore |
| `roof_slope_broken`, `roof_slope_bare` | falda col foro e travetti a vista; falda senza coppi (nessuna collisione) |
| `rubble_pile`, `rubble_small` | cumuli di macerie (1,8 m e 0,9 m), da posare sotto fori e brecce |

- Ogni rottura mostra `brick_core` (sezione di muratura): creste, bordi delle brecce, teste dei solai.
- Nella rovina tutti i `roof_*` hanno anche l'**intradosso** (`boards`): coi soffitti sfondati il
  tetto si vede da sotto. Nel kit pulito no, perché lì un soffitto c'è sempre.
- Le parti che non devono fermare nessuno (vetri rotti, scuro appeso) stanno in un secondo oggetto
  `<modulo>_loose`, senza collisione: è `builder.nocol` nello script.

## 3b. Materiali e colori

I nomi delle superfici sono stabili: `facade`, `plaster`, `stone`, `stone_blocks`, `dentil`,
`window`, `shutter`, `shutters`, `grille`, `balustrade`, `door_ext`, `door_int`, `wood`, `boards`,
`ceiling`, `terrazzo`, `planks`, `paving`, `roof`; nella rovina anche `glass` (al posto di
`window`), `glass_broken`, `brick_core`, `rubble`, `roof_bare`.

- **Vetri trasparenti**: `textures/glass.png` è RGBA con lo stesso disegno di `window.png` (telaio
  opaco, lastre semitrasparenti più sporche ai bordi, alfa a 8 livelli con dithering).
  `assets/models/buildings/materials/` contiene i materiali Godot pronti: `glass_psx.tres` (pulito),
  `glass_psx_dirty.tres`, `glass_psx_broken.tres`. Nel kit pulito i vetri sono **opachi**: per
  vedere dentro basta sostituire la superficie `window` con `glass_psx.tres` (stesse UV). Nella
  rovina sono già trasparenti (`glass`, `alphaMode: BLEND`, che Godot importa come
  *alpha + depth pre-pass*).

- **Colore della facciata**: i moduli escono con `facade_ochre`. Il generatore sostituisce la
  superficie `facade` con un materiale che usa `textures/facade_<colore>.png` (`ochre`, `red`,
  `pink`, `yellow`, `grey`, `brick`). Per il **piano terra** usare `facade_<colore>_base.png`:
  stessa UV, con umidità di risalita e mattoni scoperti. Le texture di facciata sono 2 × 3 m
  (128 × 192 px), una per modulo: la variante `_base` non va usata ai piani alti.
- Ogni `.gltf` importa i **propri** materiali (stesso nome, risorse distinte): per ridurre i draw
  call il generatore deve sostituirli con materiali condivisi, cercandoli per nome di superficie.
- Filtro *nearest* e roughness 1 arrivano dal glTF. Lo **specular 0 no** (Godot ignora
  `KHR_materials_specular`): chiamare `RetroMaterials.Apply(radice)` o impostarlo sui materiali
  condivisi.
- 64 px/m, palette a 15 bit con dithering. I `.png.import` nascono dallo script con
  `compress/mode=0`: la compressione VRAM impasta pixel così grossi.

## 3c. Cambiare i colori

Tre livelli, dal più globale al più locale:

1. **Tutto il gioco** — `PALETTE` e `FACADES` in testa a `build_venice_kit.py` sono i colori di base
   (scuri, intonaco interno, zoccolatura, pietra, coppi, legno, portoni, mattoni, muffa; e i sei
   intonaci di facciata). Si cambia il valore e si rilancia lo script: ogni texture che lo usa viene
   ridipinta in entrambi i kit, e la rovina ci passa sopra lo stesso `grime()`. Aggiungere una riga a
   `FACADES` crea `facade_<nome>.png` e `facade_<nome>_base.png` in tutti e due i kit.
2. **Un edificio** — il generatore sceglie un colore di facciata e lo applica a **tutta** la
   superficie `facade` dell'edificio (variante `_base` al piano terra, camino compreso). È questo
   che tiene coerente un palazzo: un solo materiale condiviso per superficie, per edificio.
3. **Una variazione** — `albedo_color` del materiale moltiplica la texture: tinte leggere (scuri
   più sbiaditi, un intonaco più freddo) senza nuove texture. Restare vicini al bianco: una tinta
   forte si mangia il dithering.

Non ricolorare i PNG a mano: lo script li riscrive.

## 3d. Lo stile, per ogni asset nuovo

Le regole che rendono coerente il kit, da rispettare anche per oggetti non edilizi:

- **64 px per metro**, texture piccole (64–128 px) e ripetute; mai texture grandi "uniche".
- Ogni texture passa da `psx()`: **5 bit per canale con dithering ordinato 4×4**. È la grana comune.
- Filtro **nearest** (con mipmap), **roughness 1, specular 0, niente normal map**, niente metallic.
- Ombreggiatura **piatta** (nessuno smooth), archi a 6–8 segmenti, dettaglio dipinto nella texture
  e non modellato. Ordine di grandezza: 10–150 triangoli per modulo.
- Trasparenze: ritaglio a soglia (`MASK`) per grate e balaustre, alfa a 8 livelli per i vetri.
- Mondo rovinato = stessa texture pulita + `grime()` (toglie colore, scurisce, colature, muffa):
  non si dipinge una versione sporca a parte.
- Import Godot senza compressione VRAM (`compress/mode=0`).

Per un modulo nuovo: una funzione `tex_*` (se serve una texture), una riga in `MATERIALS`, una
funzione `m_*` che riempie un `Builder`, una riga in `MODULES`. Per un asset fatto a mano in
Blender: stesse regole, e la texture va passata dalle stesse `psx()`/`grime()` (lo script non è importabile come modulo: esegue tutto al caricamento, quindi vanno copiate o lo script va diviso).

## 4. Collisioni

- Gli oggetti si chiamano `<modulo>-col`: Godot genera `MeshInstance3D` + `StaticBody3D` trimesh
  sul layer 1 (`CollisionLayers.World`). Fasce decorative e ante non ne hanno.
- Le scale hanno **rampe convesse** (`-convcolonly`, una per rampa più il pianerottolo) al posto
  dei gradini, lungo la linea dei nasi (40°).
- **Nessun materiale è double-sided**: vetri, grate e balaustre hanno due facce contrapposte,
  perché un trimesh non ferma chi arriva dal retro. Vale per ogni modulo nuovo.
- Con `MultiMesh` le collisioni non vengono: vanno ricostruite a parte.

## 5. Trappole dell'export

- `use_selection` dell'exporter glTF legge la selezione **vera** del view layer: l'override del
  contesto da solo esporta un file vuoto. Lo script seleziona davvero e fallisce se manca `meshes`.
- `export_keep_originals=True` è ciò che tiene le texture come file condivisi.
- Alfa → `Math(ROUND)` → BSDF è il disegno di nodi che l'exporter traduce in `alphaMode: MASK`;
  alfa collegata direttamente dà `BLEND` (i vetri).
- I `.tres` del vetro e i `.png.import` sono scritti **solo se mancano**: dopo il primo import
  appartengono a Godot. Per rigenerarli vanno cancellati.

## 6. Lacune volute

- Il generatore è l'addon `addons/venice_builder/` (skill `venice-builder`): monta i moduli sulle
  piante OSM reali. `preview()` nello script Blender resta l'esempio minimo di posizioni e rotazioni.
- I moduli d'angolo e quelli di cella (tetti, pavimenti) valgono solo ad angoli retti: su piante
  oblique il generatore li sostituisce con geometria propria. I moduli di bordo reggono una scala
  in X fra 0,75 e 1,3.
- Niente ringhiere per le scale, abbaini, altane, arredi.
- Rovina: non ci sono versioni rotte di portali, quadrifora, poggiolo, scale e angoli del tetto;
  il vetro integro ferma i colpi (è nella collisione del muro), quello rotto no.
