---
name: character-wearables
description: Corpo base del personaggio e vestiario — uomo 1,80 m (MakeHuman/MPFB2) sullo scheletro UAL, capi indossabili (vestiti, elmetti, armature, rig, zaini) skinnati sugli stessi ossi, regioni del corpo nascoste per strato, varianti in prima persona sul viewmodel, e la PIPELINE "immagini -> asset vestibile" (ricette JSON + template parametrici in Blender). Carica quando tocchi wearables/, tools/blender/charlib.py, tools/blender/build_character_base.py, tools/blender/wearables/, assets/models/characters/Character_Base*.glb, assets/models/wearables/, WearableDefinition, CharacterAppearance, CharacterOutfit, ViewmodelOutfit, BodyRegion, character_psx*.gdshader, gli slot Top/Legs/Footwear/Gloves, quando l'utente passa immagini di un capo da creare, o un vestito compenetra il corpo, sparisce, lascia buchi, non segue le animazioni o non compare in prima persona.
---

# Corpo base e vestiario

Tecnica dei giochi grandi, riassunta: **ogni capo e' una mesh skinnata sugli stessi ossi del corpo**
(Unreal *Leader Pose*, Tarkov), **pesi copiati dal corpo** (il capo si deforma come la pelle sotto) e
**regioni del corpo nascoste** sotto i capi (partizioni di Bethesda, *hidden selections* di DayZ):
un bit per regione nello shader, per istanza. Gli strati si separano gonfiando i gusci di un offset
fisso (come le gabbie di Roblox). Niente simulazione di stoffa.

## 1. Corpo base: `Character_Base.glb`

```
"<blender 5.2>" --background --python tools/blender/build_character_base.py -- [preview] [noexport]
```

- Richiede l'estensione **MPFB2** (installata in Blender 5.2, profilo utente) e il pacchetto
  `makehuman_system_assets_cc0.zip` estratto in `%APPDATA%/Blender Foundation/Blender/5.2/extensions/.user/user_default/mpfb/data`.
  Il mirror veloce e' `files.makehumancommunity.org` (files2 va a ~70 KB/s). Asset CC0, codice GPL
  (solo strumento). Lo script **resetta la scena**: sempre in un Blender in background.
- Maschio caucasico, proxy `male_generic` decimato *collapse* a ~8k triangoli (testa e mani
  leggibili; `male1591` ha le dita fuse, l'*un-subdivide* fonde facce a cavallo delle cuciture UV),
  occhi low-poly, statura **1,80 m esatti** (scala uniforme finale). Atlante 512 px: pelle MakeHuman
  ridotta + boxer, capelli rasati, barba e sopracciglia dipinti per posizione 3D, grana PSX.
- **Scheletro UAL invariato** in nomi, gerarchia, direzione e rollio degli ossi: si spostano solo le
  teste sui giunti MakeHuman. Le 43 clip UAL (rotazioni locali) restano valide e sono riesportate nel
  glb; l'import usa la stessa `BoneMap` di prima (skill `character-animation`).
- Si mettono in T-pose UAL **solo gli arti** del rig MakeHuman: le clavicole UAL partono 7 cm davanti
  al petto e vanno indietro di 14 cm, forzarle porterebbe le spalle dietro la schiena.
- `EyeHeight` resta 1,62: gli occhi del corpo nella posa `Idle` UAL sono a ~1,60 (a riposo 1,69).
- Regioni (`charlib.REGIONS` = `BodyRegion.cs`, stesso ordine): osso dominante, poi geometria per
  testa (cranio / fascia occhi / mandibola) e caviglia. **Colore di vertice R = indice / 255**: l'export
  glTF lo scrive come lineare normalizzato, arriva identico in `COLOR.r` (errore < 0,2 / 255).

## 2. Runtime in Godot (`wearables/`)

| Pezzo | Ruolo |
|---|---|
| `WearableDefinition : ItemDefinition` | `Layer`, `CoversRegions`; modello per convenzione `assets/models/wearables/<id>.glb`, prima persona `wearables/fp/<id>_FP.glb` |
| `CharacterAppearance` | figlio del Player/NPC, host-autoritativo, replica `WornItemIds` (on change). L'host lo ricava da `PlayerInventory.HostStateChanged`; gli NPC da `DefaultWornItemIds`. Nessuna RPC: indossare passa dagli intenti gia' validati dell'inventario |
| `CharacterOutfit` | nodo `Outfit` di `CharacterRig`, ricevitore puro (il bridge gli scrive `Worn`). Aggancia le mesh allo `Skeleton3D` del corpo (`Wear_<id>_<n>`), assegna lo shader, calcola le maschere, chiede al rig `RefreshFirstPersonBody()` |
| `ViewmodelOutfit` | nodo `Outfit` del `Viewmodel` (solo proprietario): nasconde `FPS_Arms_Mesh` del pack e aggancia allo scheletro FP `Character_Base_FP.glb` + le varianti FP dei capi |
| `character_psx.gdshader` / `_cloth` | stesso codice (`.gdshaderinc`): nearest, niente speculare, `instance uniform int hidden_regions` -> `discard` (anche nell'ombra). Il corpo e' a una faccia, i capi a due (interno scurito) |

Maschere: una regione coperta da un capo di strato N si nasconde sul corpo e sui capi di strato < N
(`CharacterOutfit.HiddenBy`). Strati: 1 a contatto (pantaloni, guanti, passamontagna), 2 sopra
(giacca, calzature, occhiali, cuffie), 3 fascia e corpetto, 4 rig, zaino, elmetto.

**Regola d'oro di `CoversRegions`: solo regioni RACCHIUSE per intero.** Il corpetto non copre `Chest`
(la regione include le spalle): nasconderla aprirebbe un buco sulle spalle. Gli oggetti che non
racchiudono nulla (elmetto, cuffie, rig, zaino, fascia) hanno `CoversRegions = 0` e stanno fuori
grazie all'offset del loro strato.

`FirstPersonBodyView` copia anche i capi e passa la maschera alle copie; sui capi (meta `CullMeta`,
messo da `CharacterOutfit`) le parti alte si TOLGONO invece di collassare: guardando in basso resta
l'orlo della giacca, non un sacco. Le mesh senza nulla da nascondere si copiano comunque, perche'
arretrino col corpo.

**Import**: ogni `wearables/<id>.glb` va importato con la STESSA `BoneMap` del corpo (gli ossi
diventano `Hips`, `Spine`... e la `Skin` si lega per nome allo scheletro del corpo); senza, il capo
resta a riposo o esplode. La pipeline scrive l'`.import` giusto se manca; le varianti FP no (lo
scheletro del viewmodel non e' mappato).

## 3. Pipeline "immagini -> asset" (`tools/blender/wearables/`)

> **Strada maestra: skill `asset-ingest`.** Quando c'e' un modello sorgente (zaino, elmetto, giacca
> fatti bene) si usa quella: semplifica il modello vero e ne trasforma la texture originale, poi lo
> adatta al corpo con `tools/blender/ingest/outfit.py`. Le ricette qui sotto restano per i capi
> semplici senza modello (fasce, guanti, passamontagna) e come base dei gusci. Lo zaino a ricetta e'
> stato sostituito da quello d'ingresso (`ingest/manifests/backpack.json`): non ricrearlo da ricetta.
> Uscite comuni (corpo di riferimento, export, `.tres`, anteprima) in `wearables/wexport.py`.

```
"<blender 5.2>" --background --factory-startup --python tools/blender/wearables/build_wearable.py -- tools/blender/wearables/recipes/<id>.json [preview] [noexport]
"<blender 5.2>" --background --factory-startup --python tools/blender/wearables/build_wearable.py -- body_fp
```

Flusso quando l'utente passa una o piu' immagini:

1. **Guarda le immagini** e descrivi il capo: tipo, slot, strato, materiali, colori dominanti, dettagli.
2. **Scrivi la ricetta** `recipes/<id>.json` (partire da una simile). Colori: campionati dalle foto
   e spenti verso la palette del gioco (niente saturazioni piene). Le immagini si possono anche
   proiettare (dettaglio `image`) per scritte e motivi.
3. **Costruisci con `preview noexport`** e confronta il mosaico `%TEMP%/wearables/<id>.png` (davanti,
   lato, dietro, 3/4, corsa, accovacciato, mira, seduto, nuoto) con le immagini. Itera sulla ricetta.
4. **Esporta** (senza `noexport`): glb indossato, modello a terra, variante FP se tocca le braccia,
   `.tres` (`Layer`, `CoversRegions`, categoria per i nuovi) e righe in `locales/items.csv` (`names`).
5. Icona: `tools/bake_item_icons.gd` (non headless). Reimport e collaudo (§5).

### Ricetta

Campi: `item_id`, `slot` (`Top`, `Legs`, `Footwear`, `Gloves`, `Headwear`, `Earpiece`, `FaceCover`,
`Eyewear`, `Armband`, `BodyArmor`, `ChestRig`, `ArmoredRig`, `Backpack`), `layer`, `colors` (nome ->
`#rrggbb`), `texture_size` (128/256/512), `ground` (`lay` disteso, `stand` in piedi), `parts`,
`details`, `covers` (facoltativo: senza, si calcola — soglia 95 % della regione racchiusa), `item`
(`width`, `height`, `weight` per i nuovi), `names` (`name`/`short`/`desc`: `[en, it]`), `wear`.

Posizioni: espressioni sui giunti UAL del corpo — `"pelvis+0.05"` (quota z), `"hand_l.x-0.03"`,
`"Head.y+0.01"`, `"eye"` (quota degli occhi in z, fronte del viso in y), `"ground"`, numeri.
Giunti: `pelvis spine_01..03 neck_01 Head clavicle_* upperarm_* lowerarm_* hand_* thigh_* calf_*
foot_* ball_*` e dita.

| Template | Parametri principali | Per |
|---|---|---|
| `shell` | `regions`, `clip` (`axis` z o `x_abs`, `min`/`max`), `offset`, `loosen` (osso, quote, ampiezza), `smooth`, `hem_snap` | maglie, giacche, pantaloni, guanti, stivali, passamontagna. Pesi e regioni COPIATI dal corpo; bordi sui piani di taglio raddrizzati |
| `band` | `bone`+`at` oppure `z` (+`center_bone`, `tilt`), `width`, `offset`, `regions`, `search_radius` | colletti, polsini, cinture, fasce, elastici |
| `panel` | `face` (front/back/left/right), `center`, `size`, `depth`, `curve`, `corner`, `res` | piastre, pannelli del rig, schienali |
| `box` | `face`, `at`, `size` [w,h,d], `bevel`, `follow`, `tilt` | tasche, pouch, sacca dello zaino |
| `strap` | `points` ([x, z, faccia] o [x, y, z]), `width`, `thickness` | spallacci, bretelle, archetti |
| `cap` | `offset`, `rim_front`, `rim_back`, `lip` | elmetti |
| `cup` | `center`, `axis`, `radius`, `depth`, `scale`, `bone` | padiglioni, lenti |
| `sole` | `thickness`, `grow`, `height` | suole |

Comuni: `material` (`canvas`, `cotton`, `denim`, `knit`, `nylon`, `cordura`, `rubber`, `leather`,
`paint`, `plastic`, `lens`, `metal`, `camo`), `color`, `mirror` (copia x -> -x con ossi `_l`<->`_r`),
`rigid_bone` + `rigidity` (irrigidisce verso un osso: piastre, zaini).
Dettagli dipinti per posizione 3D: `rect` (toppe, patte, lenti), `line` (cuciture, cerniere,
`dashed`, `range`), `image` (proiezione `front`/`back` di una foto in un rettangolo `rect`).
Filtri: `parts` (indici delle parti), `face`, `mirror`.

Il capo esce con **la stessa armatura del corpo** (reimportata dal glb: stesso riposo, stesse bind),
una mesh, una texture. Texture cotta dalla posizione 3D: niente cuciture visibili.

### Prima persona (`fp_arms.py`)

Il viewmodel resta quello del pack (scheletro `FP_Arms`, clip, arma, tacche, `AimPitchDegrees`). Le
braccia del corpo e dei capi si portano sul suo riposo (letto da `VM_Rifle.glb`): per ogni osso
UAL -> FP una trasformazione (terna direzione + asse indice->mignolo, scala sulla lunghezza: il pack
ha braccia ~18 % piu' corte, di traverso sqrt), media pesata sui pesi UAL; avambraccio diviso fra
`twist_1..3_arm`; busto sulla clavicola del suo lato e pesato su `spine`. Il pack originale non serve.

## 4. Lacune volute

- Nessuna simulazione di stoffa (eventuale `SpringBoneSimulator3D` per cinghie).
- Nessun corpo femminile; nessun capello in mesh (dipinti).
- L'armatura non protegge (e' combattimento). I vestiti non hanno griglie (scelta dell'utente).
- Il pack originale non e' nel repo: la mesh delle braccia del pack si nasconde a runtime, i
  `VM_*.glb` non sono rigenerati.
- Fra capi dello stesso strato non c'e' mascheratura: due capi di strato 4 sovrapposti (rig e zaino
  sulle spalle) possono toccarsi.

## 5. Verifica

```
Godot --path . --resolution 1280x720 --script tools/playtest_outfit.gd -- <cartella> [tp|fp|all]
```

Fila di personaggi con 6 combinazioni di capi in 4 pose (fermi, corsa, accovacciati, mira), davanti e
dietro, primi piani; poi il giocatore vestito in prima persona (maniche e guanti sul viewmodel,
gambe guardando in basso, ricarica).

```
Godot --path . --headless --script tools/playtest_outfit_net.gd -- host
Godot --path . --headless --script tools/playtest_outfit_net.gd -- client     (subito dopo)
```

Rete: l'host mette elmetto e fascia al giocatore del client (`HostGiveItem`); entrambi i processi
stampano `WornItemIds` e i pezzi agganciati per ogni giocatore, prima e dopo: devono coincidere.
Verificato il 20/11/2026: 5 pezzi (kit iniziale) -> 7 su host e client.

Anche `tools/playtest_fps.gd fp`: le tacche devono restare al centro (lo scheletro FP non cambia,
quindi `AimPitchDegrees` resta valido).

## 6. Lavoro aperto

- Guardando dritto in basso si vede l'orlo della giacca con le tasche dei pantaloni; si puo' rifinire
  (taglio piu' basso, o vista dedicata delle gambe).
- Le icone dei capi nuovi non sono cotte (`tools/bake_item_icons.gd`, non headless).
- Il bordo del collo del passamontagna e i fori restano un po' seghettati alla risoluzione del corpo.
- Nessun controllo automatico delle compenetrazioni nelle pose: oggi si guarda il mosaico.
