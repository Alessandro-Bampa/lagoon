---
name: water-ocean
description: Acqua del mondo — onde di Gerstner identiche su CPU e GPU, marea, stato del mare (calmo…tempesta), galleggiamento di barche e oggetti, colore per zona (mare, laguna, rii), shader con trasparenza, rifrazione, riflessi e schiuma, velo sott'acqua. Carica quando tocchi water/, resources/water/, water_map.py, WaterVolume, WaterSurface, WaveSpectrum, WaterMap, WaterPalette, Buoyancy, FloatingBody, water.gdshader, underwater.gdshader, quando qualcosa deve galleggiare o sapere la quota dell'acqua, quando un evento o il meteo deve cambiare marea o mare, o l'acqua si vede male (righe scure, trasparenze sbagliate, onde che "nuotano", barca che non segue le onde).
---

# Acqua: onde, marea, galleggiamento, resa

```
water/scripts/WaveSpectrum.cs   banco di 12 onde di Gerstner + stati del mare (CPU)    ┐ stesso
water/shaders/water.gdshader    le stesse onde in vertex, ottica in fragment (GPU)    ┘ contratto
water/scripts/WaterVolume.cs    stato autoritativo (host): orologio, marea, mare; Sample()
water/scripts/WaveLocalFrame.cs banco d'onde in float attorno a un punto (letture in massa, §3b)
water/scripts/WaterSurface.cs   resa per peer: griglia sulla camera, uniformi, colore, velo
water/scripts/WaterMap.cs       zone (16 m) + esposizione alle onde (32 m) della mappa
water/scripts/Buoyancy.cs       spinta + trascinamento per punti (barche e oggetti)
water/scripts/FloatingBody.cs   RigidBody che galleggia, host-autoritativo (casse, boe)
water/scenes/Water.tscn         WaterVolume + Sync + Surface: si istanzia nel livello
resources/water/*.tres          WaterPalette per zona (mare, laguna, acqua_bassa, valle, rio)
addons/venice_builder/tools/water_map.py   genera world/maps/<mappa>/water/ (gitignored)
tools/playtest_water.gd         collaudo: view (screenshot), physics (numeri), perf (ms GPU)
tools/playtest_swim.gd          collaudo del nuoto: test (livello di prova), laguna, net-host/net-client
tools/playtest_water_net.gd     collaudo di rete: host + client ENet, righe da confrontare per ora
water/scripts/SeaStateDebugControls.cs   tasti di prova (PagSu/PagGiu', Home) + riga di stato
world/scenes/MainWaveTest.tscn  livello di prova: mare aperto, pontile, barchini (skill vehicles-boats §9)
```

## 1. Il contratto CPU ⇄ GPU (da non rompere)

Le barche galleggiano sulle onde calcolate in C# (`WaterVolume.Sample`), i giocatori vedono quelle
dello shader. **Ogni modifica alla formula va fatta in due posti**: `WaveSpectrum.Evaluate`/
`AmplitudesAt` e `vertex()` di `water.gdshader`; lettura dell'esposizione: `WaterMap.ExposureAt` e
`exposure_at` (bilineare, centri dei texel a +0,5, bordi bloccati, byte/255).

- **Componenti fisse**: lunghezze d'onda (3–160 m), direzioni e fasi si generano dal seme e dal
  vento una volta sola. Lo stato del mare cambia solo le ampiezze. Cambiare λ o direzione in corsa
  fa saltare la fase di migliaia di radianti lontano dall'origine: un cambio di vento richiederà
  una dissolvenza fra due banchi.
- **Precisione**: fase `k·x − ω·t + φ` in `double` in C#; allo shader arriva per onda la fase
  all'origine della griglia, già avvolta (`ShaderPhase`). La GPU fa solo `k·(posizione locale)`.
  Mai usare `TIME` o coordinate mondo grandi nello shader: le texture di dettaglio usano
  `tile_origin` (origine mod 1024; i periodi delle texture devono dividere 1024) e offset di
  scorrimento calcolati in C#.
- **Due spettri** per onda (`wave_b`: aperto xy, riparato zw) mescolati dall'esposizione: 0 acqua
  ferma, 0,5 laguna (spettro corto, Hs ≤ 0,9 m), 1 mare (Hs fino a 5 m). Spettro asimmetrico in
  log λ (stretto verso le lunghe, largo verso le corte); tetti k·A ≤ 0,25 e Σk·H ≤ 0,9 (oltre,
  Gerstner fa il cappio).
- **Quota sopra un punto**: Gerstner sposta anche in orizzontale; `Sample` inverte a punto fisso
  (4 iterazioni). La velocità restituita è quella delle particelle: serve al trascinamento.
- **Orologi**: `PhysicsTime` avanza nei passi fisici (fisica), `RenderTime` = lo stesso più la
  frazione del passo (disegno). Un solo orologio: la barca non scivola rispetto all'onda.

## 2. Rete (CLAUDE.md §3)

`WaterVolume` è host (`_EnterTree`), statico nel livello, `Sync` figlio in modalità Always a 4 Hz:
`SyncWaveTime`, `SyncSeaState`, `SyncWaterLevel`. Il client corregge l'orologio dolcemente (>0,5 s
di errore: riallineo) e **estrapola** mare e livello con la pendenza degli ultimi due pacchetti
(max 0,6 s: se l'host tace, la rampa si ferma); senza, a 4 Hz restava ~0,5 s indietro.
Misura con due processi (`tools/playtest_water_net.gd host|client`, marea 3600× e tempesta in 2 s):
orologio entro 14 ms, livello entro 3 cm, quota della barca entro 4 cm a regime; all'avvio di una
rampa brusca il mare resta indietro ~1 s (servono due pacchetti per stimare la pendenza).
**Solo l'host calcola il galleggiamento**;
sul client le onde servono solo a disegnare, e barca/casse arrivano come pose replicate.
Il ritardo di presentazione della barca sul client (~0,1 s) non è compensato sulle onde: lacuna.

**Gestore del mondo / meteo** (girerà sull'host): `SetSeaState(stato, secondi)` (0 calmo,
1 poco mosso, 2 mosso, 3 agitato, 4 tempesta, continuo; enum `SeaState`), `SetSurge(metri,
secondi)` (sovralzo: scirocco = acqua alta), `TideClockHours` (ora di marea; l'orologio del mondo,
quando esisterà, la scriverà qui e `TideTimeScale` andrà a 0). Niente overload: Godot non li
distingue e il metodo non sarebbe più chiamabile da GDScript.

## 3. Marea

Quattro armoniche reali di Venezia (M2, S2, K1, O1; escursione sizigiale ~1,2 m) × `TideScale`,
accelerate da `TideTimeScale` (default 60: un'ora di marea al minuto) + sovralzo. Effetti sulla
mappa (quote in skill venice-terrain §2): velme (−0,15) emergono in bassa, barene (+0,3) vanno
sotto con l'alta di sizigie, San Marco (+1,0) con alta + sovralzo ≳ 0,5 m. `SurfaceY` = livello
medio con la marea, senza onde (lo usa solo la rete di sicurezza contro il vuoto di
`PlayerController`, a `SurfaceY − 40`). Chi nuota o galleggia legge `Sample()`, mai `SurfaceY`.

## 3b. Cosa offre l'acqua alla fisica

- `Sample(p)`: quota, normale, velocita' delle particelle (onde + `Current`), esposizione.
- `SampleNear(p, ref offset, iterazioni)`: stessa cosa ma l'inversione di Gerstner riparte dallo
  spostamento del passo prima (lo scafo per triangoli campiona centinaia di vertici: 2 iterazioni).
- `PrepareLocal(frame, centro)` + `SampleLocal(frame, p, ref offset, iterazioni)`: come `SampleNear`
  per molte letture vicine nello stesso passo (`WaveLocalFrame`: fase grande in double una volta per
  onda e avvolta, il resto in float come lo shader; onde spente saltate; esposizione del centro).
  Stessi numeri di `SampleNear` nelle prove della barca, ~3× piu' veloce.
- `PeakWavenumber(esposizione)`: k del picco dello spettro corrente, per attenuare con la profondita'.
- `Density` (1025), `WindVelocity` (a 10 m, dalla direzione delle onde; `WindSpeedOverride` < 0 =
  dallo stato del mare: calmo 1,5 … tempesta 17 m/s), `Current` (corrente uniforme X/Z, sommata a
  `Sample`: la seguono barche, casse e chi nuota). Dati del livello, non replicati: li usa solo l'host.

## 4. Galleggiamento (`Buoyancy.Apply`)

Le barche su misura NON lo usano piu': hanno lo scafo per triangoli (skill vehicles-boats §10).
`Buoyancy` resta per le casse (`FloatingBody`).

Per punto (`Marker3D` figli di `Floaters`): spinta ∝ immersione sotto la superficie CON le onde,
satura a `MaxSubmerge`, auto-bilanciata (`BuoyancyRatio` 2 = a metà); smorzamento verticale
**relativo all'acqua** (non al mondo: la barca segue l'onda invece di esserne frenata, e fuori
dall'acqua nessuna forza la trattiene → può volare); trascinamento orizzontale opzionale (casse:
vanno con le onde; barca: 0, ha la chiglia). Barca: 6 punti (prua, mezzo, poppa). Misure
(`playtest_water.gd physics`): mare aperto in tempesta, prua contro le onde a 6,8 m/s →
sussulto 2,4 m, beccheggio 13°, mai in volo: per salti veri serve più velocità (ThrustForce) o la
portanza di planata (non fatta).

`FloatingBody`: layer 5 `vehicles`, maschera world+vehicles: il giocatore non le spinge (Jolt lo
tratta da massa infinita), la barca sì. Statiche nel livello: per crearle a runtime serve uno spawner.

## 5. Resa (`WaterSurface` + `water.gdshader`)

- **Griglia stirata** (`WaterGridMesh`): una mesh, passo 0,5 m entro 20 m poi crescita geometrica
  fino a 30 km; agganciata alla camera a multipli del passo interno (al centro i vertici cadono
  sempre sugli stessi punti: niente onde che nuotano). `UV2.x` = passo: lo shader spegne le onde
  più corte di 2–4 passi. Qualità: Low 1 m/×1,06 (~100k vertici, niente SSR), Medium 0,5/×1,05
  (~170k, SSR 12 passi), High 0,5/×1,035 (~275k, SSR 24). Misura 1080p RTX 3070 Ti: **acqua
  0,55–0,75 ms GPU a ogni qualità** con poca acqua a schermo (San Marco). Dove l'acqua riempie la
  vista costa di più (19/11/2026, laguna presso Torcello, High): metà schermo 1,4 ms (SSR 0,4),
  vista dall'alto 1,9 ms (SSR 1,1); frame intero 2,0 ms in High, 1,16 ms in Low.
- **Ottica**: il fondale (screen texture, rifratto) attraversa l'acqua con Beer-Lambert per canale
  (`absorption`), la torbidità lo sostituisce col `scatter_color` illuminato (ALBEDO); Fresnel di
  Schlick; cielo e sole da Godot (SPECULAR), oggetti a schermo da SSR (rifiuta i colpi sotto la
  superficie: il fondale non si riflette). Schiuma: riva (profondità verticale < 8 cm, anche
  attorno a scafi e gambe) e creste (Jacobiano < 0,75, ammontare dallo stato del mare).
- **Profondità 0 = piano lontano** (Z invertita): spessore infinito. Senza, verso il `far` il mare
  diventava trasparente.
- **Facce posteriori**: "da sotto" solo se `camera_underwater`; da sopra il retro di un'onda ripida
  è ancora acqua vista da sopra. Normali piegate verso la camera se le voltano le spalle.
- **Cielo**: nei livelli con acqua `ground_bottom_color`/`ground_horizon_color` del
  `ProceduralSkyMaterial` = colore dell'orizzonte. Altrimenti i dorsi delle onde visti di taglio
  riflettono il "terreno" marrone del cielo: righe scure sulle creste.
- **Velo sott'acqua**: quad a schermo intero (`underwater.gdshader`, alfa, priorità 100) quando
  la camera è sotto `Sample().Height − near`; nebbia limitata dalla superficie.
- **Colore per zona**: ogni 0,5 s la zona sotto la camera (o l'acqua più vicina entro 40/120 m)
  sceglie la `WaterPalette` che elenca quella classe; sfuma in `PaletteBlendSeconds` (6 s). Resa
  locale: ogni giocatore vede il colore della propria zona.
- Materiali **duplicati** in `_Ready`: le uniformi per frame non devono finire nel `.tscn`.
  `WaterSurface`, `WaterVolume`, `WaterPalette` sono `[Tool]` (onde visibili nell'editor; una
  risorsa letta da un tool senza `[Tool]` è un segnaposto e il cast fallisce).
- **Editor: niente ridisegno continuo.** Ogni scrittura su un materiale fa ridisegnare la vista 3D;
  con le uniformi riscritte a ogni frame l'editor restava a ~140 fps e su MainLaguna teneva la GPU
  all'80 % a gioco fermo. Ora `AnimateInEditor` è **spento di default** (onde ferme ma visibili, i
  valori dell'ispettore si applicano con un passo a tempo nullo), l'orologio è fermo comunque
  mentre il gioco è avviato dall'editor (`AnimatingInEditor`), e `WaterSurface` nell'editor scrive
  lo shader solo se la griglia scatta (subito) o se cambia lo stato visibile (orologio, livello,
  mare, banco d'onde, rettangolo; al più 30 Hz). Nell'editor il colore della zona scatta senza
  sfumatura. Chi aggiunge un'uniforme che dipende da altro deve metterlo nella firma di
  `EditorNeedsUpdate`, sennò nell'editor non si aggiorna. Dopo una build un nodo già aperto tiene il
  valore che aveva (il vecchio default era acceso): riaprire la scena.
  Con "Visualizza tempo di frame" acceso la vista è rimasta comunque a ~145 fps (il pannello
  aggiorna le sue etichette a ogni frame, e ogni aggiornamento chiede un frame nuovo); mentre il gioco è avviato
  l'editor non scende ai 10 fps da finestra senza focus.
- `clip_rect` = `ExtentXZ`: in `TestLevel` l'acqua resta nel bacino 60 × 60 m.
- **Ricarica dell'assembly nell'editor**: a ogni build i campi privati dei nodi `[Tool]` si
  azzerano e `_Ready` non riparte. `WaterVolume` e `WaterSurface` si reinizializzano da soli
  (`Initialize()` richiamata se `_spectrum`/`_mesh` sono null; `WaterSurface` toglie i figli
  `Grid`/`Underwater` rimasti). Un nuovo campo creato in `_Ready` va inizializzato lì dentro.

## 5b. Provare le onde

Ispettore di `Water` (nella scena del livello o in `Water.tscn`), valori applicati subito, anche
nell'albero remoto a gioco avviato (lì solo sull'host): `InitialSeaState` (0 calmo … 4 tempesta),
`DefaultExposure` (0,5 laguna, 1 mare aperto: senza mappa, in `Water.tscn` e `TestLevel`, il mare
grande si vede solo a 1), `WindFromDegrees`, `WaveSeed`, `WaveCount`, `InitialSurge`,
`TideStartHours`. `ExtentXZ` di `Water.tscn` è 60 × 60: per guardare il mare allargarlo. La
**velocità** delle onde non è un parametro (c = √(gλ/2π): le lunghe corrono di più, quindi cresce
con lo stato del mare); per vederle scorrere nell'editor accendere `AnimateInEditor`, poi
`EditorTimeScale` le rallenta o accelera.

## 6. Mappa dell'acqua

```
python addons/venice_builder/tools/water_map.py --map world/maps/laguna/map.json   # ~20 s
```
Dalle classi (`classes/`) e, fuori dal poligono, dall'alfa dell'atlante lontano. Zone: classe
d'acqua prevalente dove l'acqua è ≥ 1/4 della cella (i rii sopravvivono). Esposizione per classe
(mare 1, laguna 0,5, canale 0,45, acqua bassa/velma 0,35, valle 0,25, ghebo 0,2, rio 0,08, terra 0)
sfocata ~110 m: le onde si spengono da sole verso riva. PNG importati come `Image`
(`importer="image"`), letti dalla CPU; l'esposizione va allo shader come `ImageTexture` L8.
Senza mappa: `DefaultExposure` e `DefaultPalette`.

## 7. Scelte rispetto ad altri giochi

| Tecnica | Chi | Qui |
|---|---|---|
| Somma di Gerstner, CPU = GPU | Unreal Water, AC III/Black Flag (onde lunghe), molti survival | **sì**: deterministica, interrogabile dall'host, banda zero |
| FFT (Tessendorf) + readback GPU | Sea of Thieves, Crest, Atlas | no: lettura asincrona dalla GPU, l'host dovrebbe renderizzare |
| Clipmap / griglia sulla camera | Crest, Unreal, Far Cry | griglia stirata unica (niente crepe), LOD per onda |
| Attenuazione delle onde da dati (riva/fondale) | Unreal (distance field), Crest (depth cache) | mappa d'esposizione dalle classi OSM |
| Profondità + screen texture (assorbimento, rifrazione, schiuma) | standard; guida gameidea.org | sì, con Beer-Lambert per canale |
| Riflessi planari | GTA V, Half-Life 2 | no (scena ×2); SSR + cielo |
| Colore per regione | AC Odyssey, Crest (water bodies) | tavolozza della zona della camera |

## 8. Nuoto del giocatore

Il motore (`CharacterMotor`, `core/`) non conosce l'acqua: `PlayerController` campiona
`Sample(posizione)` e gli passa quota e velocità delle particelle (`StepSwim`). Movimento
client-autoritativo come la camminata (CLAUDE.md §3, eccezione dichiarata); la modalità viaggia in
`SyncSwimming`, nello stesso `SceneReplicationConfig` della posizione, e `PlayerController.Mode`
la deduce (`Swimming`) su ogni peer.

- **Entrata/uscita**: si nuota quando la superficie vera supera i piedi di `SwimEnterDepth` (1,3 m,
  il petto); si torna a piedi coi piedi sul fondo e meno di `SwimExitDepth` (0,95). Isteresi anche
  di tempo (`SwimModeMinSeconds`): con le onde, sulla riva, si cambierebbe a ogni cresta. Fuori da
  `Contains()` si esce subito.
- **Verticale**: molla verso gli occhi a `SwimEyeAboveSurface` (0,2 m) sopra l'onda + velocità
  verticale dell'onda vicino alla superficie (si sale e scende con lei), smorzata da
  `SwimVerticalResponse`. Salto tenuto = su (in superficie +0,25 m), accovacciarsi tenuto = giù
  (sempre a pressione, anche con l'accovacciamento a interruttore), lasciati = si torna a galla a
  `SwimBuoyancySpeed`. Oltre 0,6 m sopra la quota di galleggiamento vale la gravità (cavo d'onda).
  Orizzontale: `SwimSpeed` 1,6 / `SwimSprintSpeed` 2,6 m/s, più metà della velocità delle particelle.
- **Uscire dall'acqua**, tre casi. (1) Riva in pendenza dolce: `MoveAndSlide` la sale da sé. (2)
  Sponda in pendenza ripida (i rii nel terreno della laguna sono a ~60°: né pavimento né parete):
  spingendoci contro ci si arrampica (`PushingIntoBank`) finché i piedi non superano
  `SwimClimbMaxAboveWater`. (3) Parete verticale (banchina, scafo): Spazio = `TryStartSwimClimb`,
  la stessa arrampicata del parkour con la parete cercata a piu' quote dal pelo dell'acqua in su
  (−0,1 … +0,35 m: lo specchio di un barchino pesca 0,1 m) e il limite misurato dall'ACQUA: 0,6 m,
  oppure 1,6 m entro 1 m da un nodo del gruppo `swim_ladder` (`world/scenes/Props/SwimLadder.tscn`
  sul molo di `TestLevel`; sulle barche la scaletta accessorio). Un bordo sottile con spazio oltre
  (falchetta) si scavalca invece di salirci sopra.
- **Barca**: lo scafo è solido per chi nuota grazie a `SwimCollision` (fasciame esterno, layer 6,
  skill vehicles-boats §6); se il bordo della manovra è una barca, la traiettoria la segue
  (`CarryManeuver`).
- A nuoto non si spara né si ricarica (`WeaponController.HandsFree`, host) e non si prende il timone.
- Collaudo: `tools/playtest_swim.gd` (vedi l'intestazione). Misure, mare 4: occhi sopra l'onda
  min/max 0,03/0,41 m nel bacino (esposizione 0,45), −0,08/0,58 in mare aperto (1,0), 0,05/0,48
  in laguna aperta della mappa; passo massimo 0,3 m ogni 0,1 s (nessuno scatto).

## 9. Lacune volute

Annegamento (fiato, danno) e la rete di sicurezza contro il vuoto è l'unico rientro; viewmodel di
nuoto (in prima persona l'arma esce di vista e basta); nuoto 3D seguendo lo sguardo (si sale e
scende solo coi tasti); schizzi all'entrata; acqua bassa che rallenta chi cammina; onde che bagnano il
ponte, scie e spruzzi, interazione onde-barca (la barca non scava l'acqua),
dispersione in acque basse, un solo vento per livello (niente rotazione), qualità non ancora nelle
impostazioni (`WaterSurface.Quality` a mano), caustiche, nessun compenso del ritardo di
presentazione sul client.
