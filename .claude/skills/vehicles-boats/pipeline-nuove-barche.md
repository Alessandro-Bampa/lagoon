# Pipeline delle nuove imbarcazioni: dal dato reale alla barca in acqua

Specifica operativa per aggiungere una barca a Lagoon. È il dettaglio del §9b di `SKILL.md`
(skill `vehicles-boats`): quello dice *cosa* fare, questo dice *con quali dati, con quale geometria,
con quali tolleranze e come si verifica*. Vale per una barca generata da script, modellata a mano
o ricavata da un modello CAD scaricato (GrabCAD e simili).

Legenda usata in tutto il documento:

- **[OGGI]**: comportamento o nome letto dal codice attuale (`vehicles/scripts/`), verificato.
- **[PROPOSTA]**: convenzione nuova che il codice non legge ancora. Va implementata prima di
  contarci; il modello la può già rispettare senza danni.
- Livelli di confidenza di un dato: **A** scheda ufficiale del costruttore o registro di classifica;
  **B** registro pubblico, tesi, capitolato, rivista tecnica; **C** derivato con una formula da dati A/B;
  **D** stima a giudizio. Ogni numero della scheda dati porta la sua lettera e la sua fonte.

---

## 0. Il quadro

Una barca nuova **non richiede codice**: è un modello `.glb` con nomi convenzionali più una
`BoatConfig` (`resources/boats/<nome>.tres`) che lo monta, e una scena ereditata da
`vehicles/scenes/Boat.tscn`. Lo stesso nucleo fisico muove tutte le classi (skill §10-§12):

```
 scheda dati ──► modello sorgente ──► 5 geometrie per ruolo ──► vuoti/attacchi ──► export .glb
 (§2, A–D)       (§3, CAD/a mano)     (§4)                       (§5)               (§6, controllo)
                                                                                        │
 gioco ◄── dinamica (boat_trials) ◄── statica (anteprima + statics) ◄── BoatConfig .tres ◄┘
 (§10)     (§9)                       (§8)                              (§7)
```

Regola d'oro dell'ordine: **geometria → masse → coefficienti**. Un errore di forma o di massa non si
corregge mai con un coefficiente idrodinamico: la barca sembrerebbe giusta in una prova e sbagliata
in tutte le altre (onde, carico, virata).

---

## 1. Riferimenti, unità, convenzioni (valgono ovunque)

| Cosa | Blender | Godot | Note |
|---|---|---|---|
| Prua | **+Y** | **−Z** | conversione automatica dell'export glTF con `+Y up` |
| Dritta (destra guardando a prua) | +X | +X | |
| Alto | +Z | +Y | |
| Unità | metri, `scale_length = 1` | metri | un CAD in mm va portato a metri **prima** di tutto (§3.3) |
| Origine del modello | piano di simmetria, a metà lunghezza al galleggiamento (LWL) o fuori tutto, **sulla linea d'acqua di progetto (DWL)** | idem | il barchino attuale fa eccezione (chiglia a z −0,30, DWL ≈ −0,15): eredità, non imitarla |
| Simmetria | scafo esattamente simmetrico rispetto a X = 0 | | uno scafo asimmetrico sbanda a riposo |
| Densità dell'acqua | | `WaterVolume.Density` = 1025 kg/m³ | anche anteprima e `statics` usano 1025 |
| Gravità | | `physics/3d/default_gravity` (9,8) | l'anteprima usa 9,81 |
| Assetto | | + = prua su | rapporto `Hydrostatics`, prove |
| Sbandamento | | + = dritta giù | `HydroHull.RightingArm` |
| Sterzo/timone | | + = vira a dritta | `OutboardDrive`, `ShaftDrive` |
| Trim del fuoribordo | | + = trim out (prua su) | |
| Elica destrorsa | | gira in senso orario vista da poppa in marcia avanti | `PropellerHand.Right` |

Nomi dei nodi: **unici in tutto il modello** (`BoatAssembly` usa `FindChild`, che restituisce il
primo), **senza punti** (`Mount_Prop.001` diventa un altro nome in Godot) e **senza i suffissi d'import
di Godot** (`-col`, `-colonly`, `-convcol`, `-convcolonly`, `-noimp`, `-rigid`, `-navmesh`, `-occ`,
`-occonly`, `-vehicle`, `-wheel`, `-loop`, `-cycle`): gli `.import` delle barche hanno
`nodes/use_name_suffixes=true` e trasformerebbero il nodo in qualcos'altro.

---

## 2. Fase 1 — La scheda dati

Prima di aprire Blender si compila la scheda (modello in Appendice A). Una barca senza scheda si
può fare, ma ogni valore diventa D e la taratura non ha un bersaglio.

### 2.1 Cosa chiede il simulatore, campo per campo

Fonte: **S** scheda/registro (dato reale), **M** misurato sul modello, **E** stimato con una formula
(§2.7), **T** tarato con le prove (§9), **·** il default va bene.

#### Scafo e masse — `BoatConfig`

| Campo | Unità | Fonte | Contenuto e regola |
|---|---|---|---|
| `Hull` | `.glb` | M | il modello con le geometrie di §4 e i vuoti di §5 |
| `HullMass` | kg | S→E | massa dello scafo "nudo" = dislocamento della condizione scelta − tutto ciò che si modella a parte (motore, `MassItems`). Vedi il bilancio dei pesi §7.2 |
| `HullCenterOfMass` | m (spazio del modello) | E→T | baricentro dello scafo nudo. X = 0. Y e Z si regolano su assetto a riposo e GM (§8) |
| `Hydro` | risorsa | T | `HydroCoefficients` (§7.3). Null = default planante |
| `Motor` | risorsa | S | `OutboardDefinition` (fuoribordo, anche piede poppiero approssimato, §11) |
| `Inboard` | risorsa | S | `InboardDefinition` (linea d'asse + timone). Alternativo a `Motor` |
| `Thruster` | risorsa | S | `BowThrusterDefinition`, opzionale |
| `FixedFoils` | risorse | S/M | chiglie, skeg, pinne (`FoilDefinition` con `Mount`) |
| `MassItems` | risorse | S/E | `BoatMassItem`: zavorra, serbatoi, batterie, sovrastrutture, stiva |
| `Helm` | enum | S | `Tiller` (barra del fuoribordo) o `Wheel` (volante su consolle) |
| `Console` | `.glb` | M | consolle col volante (solo `Wheel`) |
| `WheelTurnsLockToLock` | giri | S | meccanica ~2,5; idraulica 3-5; resa soltanto |
| `Accessories` | risorse | M | `BoatAccessory` (scaletta `swim_ladder`, fanale...). **Senza massa** [OGGI] |
| `Livery` | risorsa | · | colori per materiale (§4.1) |

#### Coefficienti idrodinamici — `HydroCoefficients`

Partire **sempre** da un preset esistente della stessa famiglia (§7.3) e toccare solo ciò che una
prova dice di toccare. Tutti T salvo `Hull`.

| Campo | Barchino (planante) | Gozzo (dislocante 7 m) | Peschereccio (20 m) | Ruolo |
|---|---|---|---|---|
| `Hull` | `Planing` | `Displacement` | `Displacement` | dissolvenza della resistenza d'onda in planata; `TransomRelief` attivo solo se ≠ Displacement |
| `FormFactorK` | 0,15 | 0,30 | 0,35 | k di (1+k)·C_F ITTC |
| `RoughnessDeltaCf` | 0 | 0 | 0 | carena sporca 0,0002-0,0008 |
| `PressureLinear` / `PressureQuadratic` / `PressureExponent` | 0,05 / 0,40 / 1 | 0,05 / 0,15 / 1 | 0,05 / 0,10 / 1 | pressione sulle facce che avanzano: è la planata |
| `SuctionLinear` / `SuctionQuadratic` / `SuctionExponent` | 0,05 / 0,35 / 1 | 0,05 / 0,10 / 1 | 0,05 / 0,05 / 1 | depressione sulle facce che si ritirano |
| `ReferenceSpeed` | 10 m/s | 3,5 m/s | 5 m/s | velocità di crociera (termine lineare) |
| `SuctionVentK` | 1 | 1 | 1 | limite di ventilazione |
| `LateralPressureScale` | 1 | 4 | 5,5 | presa laterale in deriva (dislocanti 4-6) |
| `PressureLeadingEdge` | 1 | 0 | 0 | pressione del fondo verso il bordo d'attacco (plananti) |
| `TransomRelief` | 0,5 | 0 | 0 | specchio ventilato in planata (mai 1) |
| `WaveResistanceScale` | 0,6 | 1,0 | 0,8 | scala della curva dei dislocanti |
| `WaveFadeFnv` | (1,0; 2,2) | idem | idem | Froude volumetrico di inizio/fine dissolvenza |
| `SlamK` / `SlamGammaMaxG` / `SlamExponent` | 0,5 / 3 / 2 | idem | idem | impatti |
| `SlamMaxG` | 10 | 4 | 2 | tetto: tender/skiff 10, open 6, cabinato 3, nave 2 |
| `AddedMass` (avanzo, deriva, sussulto) | 0,05 / 0,45 / 0,5 | 0,08 / 0,8 / 0,8 | 0,07 / 0,9 / 0,9 | frazioni della massa |
| `AddedInertia` (rollio, beccheggio, imbardata) | 0,35 / 0,6 / 0,45 | 0,4 / 0,8 / 0,7 | 0,4 / 0,9 / 0,8 | frazioni dell'inerzia |
| `SafetyLinear` | 0 / 0,05 / 0,3 | 0 / 0,3 / 1,5 | 0 / 0,3 / 1,5 | **solo** decadimento delle oscillazioni |
| `SafetyQuadratic` | 0 | 0 | 0 | |
| `SafetyAngular` | 3,5 / 0,6 / 0,3 | 1,6 / 1,5 / 0,4 | 0,9 / 1,5 / 0,4 | idem; il rollio si smorza qui |
| `AirCd` | 1 | 1 | 1 | vento sulle facce emerse della `HydroHull` |

> Vietato usare `Safety*` per ottenere velocità massima o virata (lo dice anche il codice): sono
> uno strato di sicurezza, non un modello.

#### Fuoribordo — `OutboardDefinition` (+ `EngineDefinition`, `GearboxDefinition`, `PropellerDefinition`, `FoilDefinition` del piede)

| Campo | Unità | Fonte | Regola |
|---|---|---|---|
| `Model` | `.glb` | M | contratto del modello in §5.3 |
| `Mass` | kg | S | peso a secco della scheda **+ olio + elica** (~+3-6 kg sui medi). Posizione automatica: attacco + (0, −0,3, +0,2) m, scatola 0,3 × 0,8 × 0,4 [OGGI] |
| `MaxSteerDegrees` | ° | S | 30-35 |
| `SteerSpeedDegrees` | °/s | · | 90 a barra; idroguida 40-60 |
| `TrimMinDegrees` / `TrimMaxDegrees` | ° | S | campo del trim idraulico (es. −6/+16). Senza power trim: posizioni del perno di regolazione |
| `TrimRateDegrees` | °/s | · | 3 |
| `AutoTrimCruiseDegrees`, `AutoTrimSpeedLow`/`High` | °, m/s | T | assistenza al trim |
| `MaxPropRpm` | giri/min | · | solo resa (effetto stroboscopico) |
| `LowerUnit` | risorsa | M | piede come profilo `Kind = LowerUnit`: `Area` del gambo+skeg immersi (0,045 m² a 25 cv, 0,06 a 40 cv), `Span` 0,35-0,4, `StallDegrees` 25 |

#### Motore — `EngineDefinition`

| Campo | Unità | Fonte | Regola |
|---|---|---|---|
| `Type` | enum | S | `Petrol` (farfalla diretta) o `Diesel` (regolatore di giri). Elettrico: `Diesel` + curva piatta (§11) |
| `MaxPowerKw` | kW | S | potenza **all'albero motore** al regime nominale. Fuoribordo: la scheda dà la potenza **all'elica** (ISO 8665) → dividere per `Gearbox.Efficiency` (0,96). 1 cv/PS = 0,7355 kW, 1 hp = 0,7457 kW |
| `RatedRpm` | giri/min | S | regime della potenza nominale |
| `IdleRpm` | giri/min | S | minimo (fuoribordo 750-950, diesel 600-900) |
| `LimiterRpm` | giri/min | S | fuoribordo: estremo alto del campo WOT + ~3%; diesel: regime massimo a vuoto (+5-8% del nominale) |
| `TorqueCurve` | curva | S/· | x = giri/nominali (0..1,2), y = coppia/coppia nominale. Null = curva tipica del tipo |
| `InertiaKgm2` | kg·m² | E | fuoribordo 25-40 cv 0,03-0,05; diesel 3 cil. 20-30 kW 0,3-0,5; diesel 6 cil. 300-400 kW 5-10 |
| `FrictionFraction` | – | · | 0,06 |
| `PumpingFraction` | – | · | 0,2 benzina; elettrico ~0,05 |
| `ThrottleTimeConstant` | s | S/· | benzina 0,1-0,2; diesel aspirato 0,5; turbo 0,5-2; elettrico 0,05 |
| `BsfcGPerKwh` | g/kWh | S | benzina 300-350, diesel 200-230 |
| `Mass` | kg | S | **solo entrobordo**: motore + invertitore, posto su `Mount_Engine` (scatola di lato ∛(m/800) m) |

#### Invertitore — `GearboxDefinition`

| Campo | Unità | Fonte | Regola |
|---|---|---|---|
| `RatioForward` / `RatioReverse` | – | S | dalla scheda (es. fuoribordo 2,00 / 2,08; Yanmar KM2P-1 2,21 o 2,62 avanti, 3,06 indietro) |
| `Efficiency` | – | · | 0,96 |
| `ShiftSeconds` | s | · | fuoribordo 0,2-0,5; idraulico marino 1-2 |
| `NeutralDwellSeconds` | s | · | 0,4 fuoribordo; 0,5-1 diesel |
| `MaxShiftRpm` | giri/min | · | ~1,4-1,8 × minimo (preset: 1500/850, 1100/800, 900/600) |

#### Elica — `PropellerDefinition`

| Campo | Unità | Fonte | Regola |
|---|---|---|---|
| `Diameter`, `Pitch` | m | S | pollici × 0,0254. Fuoribordo: l'elica di serie o quella che porta il regime WOT nel campo del costruttore |
| `Hand` | enum | S | destrorsa di norma; sinistrorsa sui motori controrotanti |
| `Kt0`, `Kq0`, `J0` | – | ·→T | 0 = dalla tabella P/D (serie B, 4 pale, Ae/A0 0,55-0,70). Si tarano: `Kq0` sul regime WOT, `Kt0` sul tiro a punto fisso |
| `ReverseEfficiency` | – | · | 0,45 fuoribordo, 0,6 asse |
| `Nozzle`, `NozzleK0`, `NozzleJn` | – | S | mantello Kort (pescherecci, rimorchiatori): K0 1,35-1,45 |
| `WakeFraction` w / `ThrustDeduction` t | – | E | fuoribordo 0-0,05 / 0,02-0,05; dislocante ad asse 0,2-0,35 / 0,15-0,25 |
| `PropWalkAhead` / `PropWalkAstern` / `PropWalkSpeed` | –, –, m/s | T | 0,015 / 0,045 / 1,2 fuoribordo; 0,03-0,04 / 0,10-0,12 / 1,5 asse |
| `VentilationDepthRatio`, `VentilationLoss` | – | · | 0,6 / 0,7 |
| `InertiaKgm2` | kg·m² | E | con l'acqua trascinata: **≈ 6-8 · D⁵** (regola dei preset: 0,26 m → 0,01; 0,40 m → 0,08; 1,6 m → 60) |

#### Entrobordo, timone, chiglie, elica di prua

| Risorsa / campo | Unità | Fonte | Regola |
|---|---|---|---|
| `InboardDefinition.RudderMaxDegrees` | ° | S | 35 |
| `InboardDefinition.RudderRateDegrees` | °/s | S | meccanica ~40; idraulica 3-5 (SOLAS: 35° → 30° dall'altra parte in 28 s) |
| Timone `FoilDefinition` (`Kind = Rudder`) `Area` | m² | S/E | area della pala. Minimo DNV: **A_R ≥ T·L/100 · (1 + 25 (B/L)²)** (gozzo 0,15 → 0,16 nel preset; peschereccio 1,68 → 2,1) |
| `Span` | m | M | altezza della pala |
| `AspectRatioMultiplier` | – | · | 1,5-2 se il bordo superiore è a filo dello scafo |
| `StallDegrees` | ° | · | 30-40 nella scia, 15-25 fuori |
| `LateralShielding`, `SlipstreamFactor`, `DragCoefficient`, `ReverseEfficiency` | – | · | 0,5 / 0,7 / 0,015 / 0,6 |
| Chiglia/skeg (`Kind = Keel`) `Area`, `Span`, `Chord`, `ChordSegments` | m², m, m, – | M | area laterale, altezza, lunghezza; 2-4 segmenti (uno solo non smorza l'imbardata) |
| `BowThrusterDefinition.MaxThrust` | N | S | ~10-12 kgf per kW del motore del tunnel (× 9,81) |
| `SpinUpSeconds`, `HalfEffectSpeed` | s, m/s | · | 0,7-0,8 / 1,0 |

#### Masse fisse — `BoatMassItem`

| Campo | Unità | Regola |
|---|---|---|
| `Label` | testo | solo per chi legge (non è testo di gioco) |
| `Mass` | kg | capacità × densità × riempimento (benzina 0,74 kg/L, gasolio 0,84, acqua 1,0) |
| `Position` | m (spazio del modello, assi Godot) | baricentro della voce |
| `Size` | m | ingombro (dà l'inerzia propria); zero = puntiforme |

#### Scena e gioco

| Nodo / campo | Default | Regola |
|---|---|---|
| `BoatController.VehicleId` | 1 | **unico nel livello** (lo usa `SyncAnchorId`) |
| `BoatController.CrewBodyMass` | 80 kg | peso di chi sta a bordo (+ inventario, dinamico) |
| `BoatController.HelmRange` | 3 m | distanza per prendere il timone |
| `BoatAssembly.PilotBodyCenter` | 1,0 m | `HelmSeat` = `Seat_Helm` + 1 m: non toccare (è il centro della capsula) |
| `BoatChaseCamera.*` | – | si adatta da sola alle mesh visibili |

### 2.2 Gerarchia delle fonti

1. **A** — scheda del costruttore (brochure tecnica, manuale del proprietario, targhetta CE), scheda
   del motore, registro di classifica (RINA/Lloyd's/DNV), fascicolo di stabilità.
2. **B** — registri pubblici (Fleet Register UE, banca dati della flotta di Chioggia), capitolati
   d'appalto, tesi universitarie, riviste di settore con prove in acqua.
3. **C** — derivato da A/B con le formule del §2.7 o misurato sul modello CAD (carena, LCB, area).
4. **D** — stima a giudizio, foto, video (periodo di rollio contato su un video, assetto a riposo da
   una foto di fianco).

Quando due fonti A discordano (succede: la stessa scheda Yamaha ha pesi diversi per gambo e
versione) si sceglie **la variante che si modella** e si annota quale.

### 2.3 Dove cercare

| Classe | Dove | Cosa si trova |
|---|---|---|
| Diporto (barchini, open, taxi) | brochure del cantiere, manuale CE (ISO 8666 dati principali, ISO 12217 stabilità, ISO 14946 carico massimo), prove delle riviste | LOA/LH, B, peso, portata, persone, potenza massima, categoria di progettazione, velocità |
| Servizio pubblico, lavoro (vaporetti, motobattelli, taxi, pilotine) | schede di registro (es. naviecapitani.it riporta i dati RINA), capitolati del Comune di Venezia e di AVM/ACTV, pagine dei fornitori di propulsione | Lpp, B, T, stazza, dislocamento, motore, giri, velocità di servizio |
| Pesca (pescherecci di Chioggia) | banca dati della flotta di Chioggia (UniPD), Fleet Register UE, ISPRA | LOA, stazza GT, kW, anno, attrezzo |
| Tradizionali veneziane (sandolo, topa, sanpierota, gondola, caorlina) | piani di costruzione (G. Penzo), schede del Museo Storico Navale, cataloghi dei cantieri tradizionali | forme (piani di linee!), L, B, peso |
| Motori | scheda tecnica del modello esatto (non la pagina di famiglia) e manuale d'officina | potenza, regime nominale e WOT, rapporto, peso, elica di serie, consumo |
| Eliche | tabelle del costruttore del motore, serie B di Wageningen | D × P, pale, rapporto d'area |

> La **stazza** (GT, TSL) è un volume convenzionale, **non una massa**: non usarla come
> dislocamento.

### 2.4 Trappole di lettura

- **Potenza dei fuoribordo**: dichiarata all'elica (ISO 8665); gli entrobordo all'albero motore.
  `MaxPowerKw` è all'albero: fuoribordo → dividere per 0,96.
- **cv/PS ≠ hp**: le schede europee danno cv metrici (0,7355 kW), quelle USA hp (0,7457 kW). Meglio
  il dato in kW. Yanmar usa "mhp" (= cv metrici).
- **Peso a secco** dei fuoribordo: di solito senza olio e senza elica, e per il gambo più corto.
- **Peso della barca**: in brochure spesso **senza motore** (fuoribordo) o "a vuoto" senza
  carburante. ISO 8666: barca leggera *m_LC* (scafo + dotazioni fisse + motore, senza carburante,
  acqua, persone), carico massimo *m_MTL*, dislocamento a pieno carico *m_LDC* = m_LC + m_MTL.
  Annotare sempre quale dei tre si è letto.
- **Immersione**: "con piede/elica" o "di scafo". La `HydroHull` vuole quella di scafo; l'elica si
  controlla a parte (§5).
- **Lunghezze**: LOA (fuori tutto, con pulpiti e piattaforme), LH (scafo, ISO 8666), LWL (al
  galleggiamento), Lpp (fra le perpendicolari, navi). La velocità di carena usa la LWL.
- **Rapporti di riduzione**: "26:13" = 2,00. Alcuni invertitori hanno la retromarcia diversa
  (KM2P-1: 3,06).

### 2.5 Conversioni

| Da | A | Fattore |
|---|---|---|
| pollici | m | × 0,0254 |
| piedi | m | × 0,3048 |
| libbre | kg | × 0,4536 |
| cv (PS) | kW | × 0,7355 |
| hp | kW | × 0,7457 |
| nodi | m/s | × 0,5144 |
| kgf | N | × 9,81 |
| galloni USA | L | × 3,785 |
| litri di benzina / gasolio / acqua | kg | × 0,74 / 0,84 / 1,0 |
| ∇ (m³) | Δ (kg) | × 1025 |

### 2.6 Dati di riferimento già raccolti

Motori (fuoribordo):

| Motore | Potenza | Regime | Rapporto | Peso a secco | Elica | Conf. |
|---|---|---|---|---|---|---|
| Yamaha F25 (2 cil.) | 25 cv | WOT 5000-6000 (fonte non ufficiale) | 2,08 (13:27); alcune versioni 2,00 | 57 kg barra, 64 kg comando a distanza | serie: 3 pale 9 7/8" × 11 1/4" (251 × 286 mm) | A/B |
| Yamaha F40 (3 cil., 747 cm³) | 40 hp @ 5500 | WOT 5000-6000 | 2,00 (26:13) | 97-101 kg (gambo corto, senza olio né elica); manuale d'officina: 83-89 kg per versioni precedenti | non indicata | A |
| Honda BF40 / BF50 | 40 hp @ 5500 / 50 hp @ 6000 | WOT 5000-6000 / 5500-6000 | 2,08 | 97 kg (gambo L); BF50 X 101 kg | venduta a parte | A |

Motori (entrobordo):

| Motore | Potenza | Regime | Peso | Invertitore | Conf. |
|---|---|---|---|---|---|
| Yanmar 3YM30AE | 21,3 kW (29 mhp) | 3200 | 127 kg senza invertitore, 137 kg con KM2P-1 | KM2P-1: 2,21 o 2,62 avanti / 3,06 indietro | A |
| Deutz BV 4S, 6 cil. (motobattello ACTV 80, 1974) | 123 kW | 1800 | – | – | B |

Barche:

| Barca | L | B | T | Massa | Propulsione | Prestazioni | Conf. |
|---|---|---|---|---|---|---|---|
| Taxi acqueo elettrico "Lucietta" (Serenella, 2025) | LOA 9,27 | 2,25 | 0,50 a vuoto | 4000 kg a vuoto, 5300 a pieno carico; 14 persone | 200 kW elettrico, piede poppiero duo-prop | servizio 11 kn, max stimata 30 kn a vuoto | A |
| Taxi acqueo ibrido (capitolato del Comune di Venezia) | 8,90-10,00 | 2,10-2,80 | – | – | – | – | B |
| Motobattello ACTV 80 (1974) | LOA 23,0, Lpp 20,99 | 4,22 | 1,415 max | Δ 25 t (stazza 24,84 GT) | Deutz 123 kW @ 1800 | – | B |
| MotoBattello 3 Serie 80 (ibrido 2025) | 23 | – | – | 229 passeggeri | ibrido FPT + Vulkan | 10,8 kn max | B |
| Vaporetto "Serie 90" (Wikipedia) | 23,93 | 4,22 | – | Δ 25 t, 210 passeggeri | 147 kW | – | B |
| Sanpierota ~7 m (catalogo di cantiere) | 6,96 | 2,25 | – | 460 kg | – | – | B |
| Topa (vetroresina, fuoribordo) | ~7 | – | – | – | fuoribordo a specchio piatto | – | D |
| Gondola | 10,8-11 | 1,4-1,6 | – | ~350 kg | remo | – | D |
| Volante di Chioggia (coppia a strascico) | ~30 | ~7 | – | – | – | – | D |

Confronto con le risorse attuali (da correggere quando si ritoccano):

| Risorsa | Valore oggi | Dato reale vicino | Nota |
|---|---|---|---|
| `outboard_25.tres` | 18,4 kW, 2,08, elica 0,26 × 0,28, **`Mass` 50 kg (default)** | F25: 57-64 kg, elica 0,251 × 0,286 | massa bassa di 7-14 kg |
| `outboard_40.tres` | 29,4 kW, 2,00, elica 0,30 × 0,33, `Mass` 82 kg | F40: 97-101 kg (+ olio, elica) | massa bassa di ~20 kg; potenza da dividere per 0,96 se la si vuole all'albero |
| `gozzo.tres` | 26 kW @ 3400, 220 kg, 2,5/2,5 | (35 cv) 3YM30AE: 21,3 kW @ 3200, 137 kg, 2,21-2,62/3,06 | placeholder: il motore va scelto e la retromarcia separata |

### 2.7 Stimare ciò che manca

**Dislocamento dalla carena.** Con la `HydroHull` pronta, `check_boat_model.py` (§6.2) dà il
volume sotto la DWL e l'anteprima di Godot l'immersione per una massa data. Se la scheda dà
l'immersione e non la massa: Δ = 1025 · ∇(T). Controllo di plausibilità col coefficiente di blocco
C_B = ∇ / (LWL · B_WL · T): plananti 0,35-0,45, gozzi e topi 0,40-0,50, pescherecci 0,50-0,60.

**Baricentro verticale (KG).** Il dato vero (prova di stabilità) esiste solo per le barche
commerciali. Altrimenti:
1. bilancio dei pesi (§7.2) con il baricentro di ogni voce;
2. per lo scafo nudo in vetroresina: centroide della superficie della `HydroHull` abbassato del
   10-20% dell'altezza di costruzione D (fondo e madieri pesano più delle murate);
3. **si regola KG sul GM bersaglio, non viceversa**: KG = KM − GM, con KM letto dall'anteprima.

**GM e periodo di rollio.** Formula del Codice IMO di stabilità integra 2008 (Parte A §2.3,
criterio meteorologico), approssimata ma buona come controllo:

```
T_rollio ≈ 2 · C · B / √GM        C = 0,373 + 0,023 · (B/d) − 0,043 · (LWL/100)
```

(B larghezza, d immersione media, LWL lunghezza al galleggiamento, metri). È tarata sulle navi
(vaporetto, peschereccio). Sulle barche piccole e poco immerse **sovrastima**: gozzo (B 2,4, d 0,55,
LWL 6,3, GM 0,57) → 3,0 s contro 2,3 misurati; barchino (GM 1,1, B/d ~6-11) → ~1,8 s contro 1,1.
Lì conta la banda di classe del §8. Viceversa, un periodo misurato su un video dà il GM.

**Raggi d'inerzia** (con la massa aggiunta li riporta l'anteprima): rollio 0,35-0,45 · B,
beccheggio e imbardata 0,22-0,28 · L. Se escono bassi, le masse sono troppo concentrate: dare una
`Size` alle `MassItems` o spostare masse ai lati/estremità come nella barca vera.

**Velocità.**
- Dislocanti: velocità di carena ≈ 2,43 · √LWL(m) nodi (la usa l'anteprima); i gozzi la superano
  del 5-15%.
- Plananti, formula di Crouch: **V(kn) = C / √(Δ(lb) / P(hp))**, P all'elica. C ≈ 150 barche da
  lavoro e runabout, 170-190 sportive, 210+ da corsa. Barchino 40 cv, ~570 kg in acqua → ~26 kn;
  taxi elettrico 200 kW a vuoto (4000 kg) con 30 kn dichiarati → C ≈ 172.

**Tiro a punto fisso** (ordine di grandezza; l'anteprima lo stima da motore ed elica):
fuoribordo con elica a passo lungo 6-10 kgf/kW; dislocante ad asse 11-15 kgf/kW; mantello Kort
15-22 kgf/kW.

**Regresso dell'elica** a velocità massima: plananti 10-15%, dislocanti 25-40%.

**Timone**: minimo DNV in tabella §2.1; barche piccole e pescherecci stanno di solito al 3-5% di L·T.

**Massa aggiunta**: partire dai preset; deriva e sussulto crescono con l'immersione relativa
(planante 0,45-0,5, dislocante 0,8-0,9).

---

## 3. Fase 2 — Il modello sorgente (CAD scaricato o fatto a mano)

### 3.1 Licenza, prima di tutto

I modelli di **GrabCAD sono per uso personale** salvo accordo con l'autore: per qualunque uso
commerciale (e un gioco pubblicato lo è) serve il permesso scritto di chi l'ha caricato; senza
risposta non si può usare. Regole:

- il modello CAD scaricato **non entra nel repository** (come i pack di `blender-pipeline`): si
  riscarica e si passa il percorso allo script;
- l'accordo con l'autore e il link vanno in `assets/models/LICENSES.md` nello stesso commit che
  aggiunge il `.glb` derivato;
- niente marchi né nomi commerciali nel gioco (livree e loghi li decide il gioco): i dati di una barca
  reale si usano come **dati di classe**.

### 3.2 Formati e import

| Formato | Come entra in Blender | Nota |
|---|---|---|
| STEP/STP, IGES | estensione d'import STEP (es. *STEP Importer* su extensions.blender.org; *CADPrep*, *StepIO* a pagamento; basate su OpenCASCADE) **oppure** convertitore esterno (FreeCAD, CAD Assistant) → glTF/OBJ | scegliere la **deflessione di tessellazione**: lineare ~1-2 mm per barche piccole, 5-10 mm per navi; angolare 10-15°. Più fine non serve: si ricostruisce comunque (§4) |
| SLDPRT, IPT, F3D | aprire nel CAD d'origine o farsi dare uno STEP | Blender non li legge |
| STL, OBJ | import nativo | spesso in mm e Y-up; triangoli sporchi |
| FBX, glTF | import nativo | controllare la scala |

Le estensioni STEP vanno controllate sulla versione di Blender in uso (sul 5.2 sono segnalati
problemi): in caso di dubbio convertire fuori da Blender.

### 3.3 Scala, assi, origine

1. Scena vuota in metri (`scale_length = 1`).
2. Import; **misurare LOA e B** del modello e confrontarle con la scheda: scarto ≤ 0,5%. Un fattore
   1000 (mm) o 25,4 (pollici) si riconosce subito.
3. Ruotare perché la prua sia **+Y** e l'alto **+Z**; applicare rotazione e scala (Ctrl+A).
4. Portare la **DWL a z = 0** (dalla scheda: immersione di scafo; in mancanza dal piano di linee o
   da una foto) e il piano di simmetria a x = 0.

### 3.4 Pulizia

1. Cancellare tutto ciò che non si vede o non serve: interni del motore, viteria, cavi, componenti
   annegati, facce interne, duplicati coincidenti.
2. **Tenere a parte** (servono per le masse e gli attacchi, §5 e §7.2): motore, asse e elica, timone,
   serbatoi, batterie, sedili, consolle. Il loro baricentro è un dato **C** migliore di qualunque stima.
3. `Merge by Distance` (0,1-0,5 mm), ricalcolo delle normali verso l'esterno.
4. Separare per parte e per materiale.
5. Simmetria: se il CAD è asimmetrico per errore, tenere metà scafo e specchiarla.

### 3.5 Cosa estrarre dal CAD

- **Pelle esterna dello scafo** (opera viva + opera morta fino alla falchetta): base della
  `HydroHull`, della `SwimCollision` e della carena di resa.
- **Linee**: falchetta, spigoli (chine), linea di chiglia, specchio. Gli spigoli sono la forma della
  planata: devono restare spigoli in ogni geometria derivata.
- **Ponti e pagliolati**: quote dei piani calpestabili (`DeckCollision`).
- **Posizioni**: centro elica e inclinazione dell'asse, asse del timone, baricentro del motore,
  tunnel dell'elica di prua, serbatoi, perno del fuoribordo sullo specchio.

---

## 4. Fase 3 — Le cinque geometrie per ruolo

Un modello di barca contiene **cinque** geometrie diverse, ognuna col suo scopo e le sue regole.
Mai usarne una per un altro ruolo (la mesh di resa come `HydroHull` è l'errore classico).

| Nodo | Ruolo | Chi lo legge | Layer | Visibile |
|---|---|---|---|---|
| (qualunque nome) | resa | Godot | – | sì |
| `HydroHull` | fisica dell'acqua: spinta, attrito, pressione, impatti, aria, massa (guscio) | `HydroHull.FromFaces`, `BoatMassModel.AddHullShell` | nessuno | no (nascosta da `BoatAssembly`) |
| `HullCollision` | urti con fondale, moli, altre barche | convesso sul `RigidBody3D` | 5 `vehicles` | no |
| `DeckCollision` | dove si cammina a bordo | trimesh su `Deck` (`AnimatableBody3D`) | 6 `vehicle_deck` | no |
| `SwimCollision` | parete esterna per chi nuota, appoggio per issarsi | trimesh su `Deck` | 6 | no |

Le quattro geometrie di ruolo **si esportano nascoste solo al render** (icona fotocamera), non
nascoste nel viewport né escluse dal view layer: un export "solo selezionati" salta gli oggetti
nascosti nel viewport.

### 4.1 Resa (stile PSX / Half-Life 2)

Budget di triangoli (LOD0, tutto ciò che si vede della barca, motore e consolle esclusi):

| Classe | Esempi | Triangoli | Note |
|---|---|---|---|
| Piccola ≤ 7 m | barchino, topa, sandolo, s'ciopon | 1500-3000 | oggi barchino 2172, gozzo placeholder 1748 |
| Media 7-12 m | gozzo, taxi, mototopo | 3000-6000 | cabina percorribile compresa |
| Grande 12-30 m | vaporetto, peschereccio | 6000-12000 | interni percorribili compresi |
| Fuoribordo / consolle | | 300-600 ciascuno | |

Regole di stile (le stesse del kit degli edifici, skill `venice-building-kit`, perché barche e città
devono avere la stessa grana):

- ombreggiatura **piatta**, spigoli vivi; i dettagli piccoli (bulloni, scritte, maniglie, griglie)
  **dipinti nella texture**, non modellati;
- **64 px per metro**, texture piccole (64-256 px) e ripetute; un atlante per barca ≤ 512 px;
  colori a 5 bit per canale con dithering ordinato 4 × 4 (la `psx()` del kit);
- filtro **nearest** con mipmap (in Blender: interpolazione *Closest*, arriva in Godot col glTF),
  roughness 1, specular 0, niente normal map, niente metallic (salvo `Metal`);
- **materiali per nome**: `Hull`, `Stripe`, `Deck`, `Interior`, `Cushion`, `Motor`, `MotorTrim` sono
  ricolorati dalla livrea (`BoatLivery`, su una copia del materiale); `Rubber`, `Metal`, `Glass`,
  `Gauge` no. Le texture dei materiali di livrea vanno dipinte **in scala di grigi chiari**: il
  colore della livrea le moltiplica;
- vetri: materiale `Glass` (unico con trasparenza);
- niente facce interne né coplanari sovrapposte (z-fighting sul mare si vede);
- parti mobili come oggetti separati col perno sull'asse vero (timone, elica, volante).

La carena di resa va ricavata **dalle stesse sezioni** della `HydroHull` (§4.2, metodo A, con più
punti per la silhouette): nella fascia bagnata le due superfici non devono distare più di 1 cm
(barche ≤ 12 m) o 3 cm (navi), altrimenti la linea d'acqua che si vede non è quella calcolata.

Abitabilità (barche in cui si cammina dentro), dai valori del giocatore (`Player.tscn`,
`CharacterMotor`): capsula raggio 0,35 m, alta 2,0 m in piedi e 1,2 m accovacciati, gradino massimo
0,35 m, pendenza massima 45°, scavalcamento 0,5-1,2 m.

| Elemento | Minimo | Note |
|---|---|---|
| Passaggio | 0,80 m netti | 0,9-1,0 dove si incrocia un altro giocatore |
| Altezza libera in piedi | 2,05 m | una cabina vera più bassa (taxi ~1,7 m): o si alza il tetto di 10-30 cm **nella sola resa e collisione del ponte**, mai nella `HydroHull`, o si accetta l'accovacciamento |
| Altezza libera accovacciati | 1,25 m | |
| Gradini | alzata ≤ 0,30 m (limite 0,35), pedata ≥ 0,25 m | scale ripide fra ponti: ≤ 45° o rampa |
| Parapetto / impavesata | ≥ 0,40 m perché non si esca camminando (gradino max 0,35) | fra 0,5 e 1,2 m si scavalca di proposito (vault): per impedirlo davvero, > 1,2 m |
| Bordo libero dove si sale dall'acqua | ≤ 0,6 m senza scaletta, ≤ 1,6 m con la scaletta | altrimenti da lì non si sale; il fasciame dove si sale dev'essere **verticale o rientrante** sopra l'acqua: una murata che sporge (V fino in coperta) per chi nuota è un soffitto, non una parete (vaporetto) |
| **Visuale al timone** | occhi = `Seat_Helm` + **1,20 m** (`FirstPersonCamera.SeatedEyeHeight`, fissato dalla posa seduta) ≥ davanzale del parabrezza + 0,25 m; ≥ 60% dei raggi del controllo liberi (§6.2) | in una timoneria chiusa i davanzali stanno a 1,2-1,4 m dal ponte: il pilota seduto sul pagliolo guarda il muro. Rimedio: **pedana** sotto `Seat_Helm` (vaporetto: 0,50 m, gradino di 0,25 m), mai alzare la sola camera. La consolle segue la pedana (mozzo della ruota ~0,75-0,85 m sopra i piedi, mani a ~0,86 m), ma la cima della ruota resta ≥ 0,1 m sotto gli occhi |

### 4.2 `HydroHull` — la geometria che decide il galleggiamento

È la parte più delicata: il solutore integra la pressione sui suoi triangoli (teorema della
divergenza), quindi **ogni buco, faccia girata o compenetrazione è un errore di spinta**.

Requisiti bloccanti (li controlla `check_boat_model.py`):

| Requisito | Perché |
|---|---|
| **Chiusa**: ogni lato condiviso da esattamente 2 triangoli (0 lati aperti, 0 non-manifold) | la spinta è un integrale su superficie chiusa |
| **Verso coerente**: due facce adiacenti percorrono il lato in versi opposti | Godot gira *tutte* le facce se il volume esce negativo, non una per una |
| **Nessuna autointersezione** né gusci compenetrati | il volume comune conterebbe due volte |
| Saldatura: vertici coincidenti entro 0,5 mm | Godot salda al millimetro (`FromFaces`); oltre restano fessure |
| Nessun triangolo degenere (area < 1e-6 m²) | Godot li scarta e lascia un buco |
| Caratteristica di Eulero = 2 per guscio | buchi o "maniglie" la rompono |
| Simmetrica rispetto a X = 0 | TCB ≠ 0 = sbandamento a riposo |

Requisiti di forma e risoluzione:

| Voce | Regola | Oggi |
|---|---|---|
| Triangoli | ≤ 600 (≤ 8 m), ≤ 1000 (8-15 m), ≤ 1500 (navi) | 500 / 848 / 1136 |
| Costo | ≈ 0,9 µs per triangolo per passo (build Debug, con onde); budget 0,3-0,5 ms per la barca del giocatore | 0,45 / 0,75 / 1,0 ms |
| Lato massimo del **fasciame bagnabile** (sotto DWL + 0,3 m, escluse le facce piane di specchio e dritto) | obiettivo ≤ L/15, limite L/10 | 0,44 / 0,66 / 1,35 m |
| Stazioni longitudinali | 20-26 (≤ 8 m), 30-36 (oltre); più fitte a prua | 20 / 26 / 34 |
| Punti per mezza sezione | 8-12, con un vertice **sulla DWL** e uno su **ogni spigolo** | |
| Aghi (qualità 4√3·A/Σl² < 0,1) nel fasciame | ≤ 2% dei triangoli | 0 / 2 / 0 |

Cosa includere e cosa no:

- **Fino alla falchetta, coperta chiusa alla falchetta** (non al pagliolo): senza un modello
  d'allagamento è l'approssimazione giusta. Conseguenza nota: una barca aperta non si riempie mai
  (lacuna di skill §14). Le barche con coperta chiudono alla coperta.
- **Sovrastrutture stagne** (cabina del taxi, timoneria del peschereccio): includerle come
  **gusci chiusi separati** appoggiati alla coperta (non compenetrati): danno riserva di
  galleggiamento a grandi sbandamenti e, soprattutto, **la superficie al vento** (`AirCd` agisce
  sulle facce emerse della sola `HydroHull`). Tende, parabrezza, tientibene: no.
- **Appendici sottili** (pinne di chiglia, skeg, timone, piede del fuoribordo, bulbo di una pinna):
  **fuori dalla `HydroHull`**, sono `Foil`. Il loro volume manca alla spinta: errore d'immersione
  ≈ V_app / A_galleggiamento. Se supera 1 cm (barche piccole) o 1% dell'immersione, la radice
  voluminosa (chiglia piena di un gozzo, bulbo di zavorra) va fusa nella carena **senza facce interne**,
  e il `Foil` resta per la portanza.
- **Aperture** (ombrinali, prese a mare, tunnel dell'elica di prua, pozzetto del motore): chiuse.
- Catamarani: due gusci chiusi separati sono ammessi.

Tre modi di costruirla, in ordine di preferenza:

**Metodo A — sezioni (consigliato, dal CAD o da un piano di linee).**
1. Copia della pelle esterna, chiusa provvisoriamente se serve.
2. N stazioni lungo Y, distribuite come in `hydro_hull()` di `build_lagoon_boats.py`
   (t = 1 − (1 − u)^1,15: più fitte a prua), più una stazione esatta sullo specchio e una sul dritto.
3. Per ogni stazione: taglio col piano y = cost. (`bmesh.ops.bisect_plane` su una copia), si prende
   la polilinea esterna da chiglia a falchetta sul lato x ≥ 0.
4. Ricampionamento per lunghezza d'arco a K punti, **con vertici forzati** su chiglia (x = 0),
   spigoli, DWL e falchetta.
5. Specchio della mezza sezione (stesso numero di punti per lato, x → −x esatto).
6. Raccordo fra sezioni consecutive a quadrilateri, specchio come poligono, coperta a quadrilateri
   da falchetta a falchetta, chiusura del dritto senza triangoli degeneri; saldatura 0,5 mm,
   normali verso l'esterno, triangolazione.
I passi 5-6 esistono già: `hydro_hull(parent, rings_fn, n_st)` e `swim_shell(...)` di
`build_lagoon_boats.py` vogliono solo una funzione `rings_fn(t)` che restituisca la sezione completa
alla stazione t. Lo script esegue tutto al caricamento (non è importabile): per uno scafo da CAD si
copiano le due funzioni in uno script dedicato o si divide il file.

**Metodo B — rimesh (rapido, solo dislocanti a sezione tonda).** Pelle chiusa → *Remesh* voxel
(voxel ≈ L/200) → *Decimate* (collapse) al budget → triangolazione → controllo. Arrotonda gli
spigoli: **non usarlo per i plananti**, a cui gli spigoli danno la planata.

**Metodo C — parametrico.** Come `build_gozzo`/`build_trawler` (`HullSpec`: L, B, T, falchetta,
piede di prua, sezioni a superellisse). Per i placeholder o quando non esistono forme; si sostituisce
poi tenendo gli stessi nomi.

### 4.3 `HullCollision`

- Godot ne fa **un solo convesso** (`CreateConvexShape(clean: true)`): basta uno scafo a 6-12
  stazioni (come oggi: barchino 38 triangoli, gozzo 304), ≤ ~100 vertici.
- Scafo fino alla coperta; niente sovrastrutture alte (gonfiano il convesso: la barca urterebbe i
  ponti con l'aria); niente appendici.
- Non serve alla massa né al galleggiamento.

### 4.4 `DeckCollision`

Trimesh calpestabile, sul layer 6. Fatta di **scatole semplici** (`collision_deck()`):

- pagliolo, panche, cofani, murate interne, scale, tetto della cabina se ci si sale;
- spessore ≥ 5 cm (niente piani a spessore zero: la capsula li attraversa nei passi lunghi);
- piani adiacenti **alla stessa quota** e senza scalini < 2 cm fra una scatola e l'altra (la capsula
  "inciampa"); scatole che si toccano, non compenetrate dove si cammina;
- **un piano subito dentro il bordo** dove si sale dall'acqua (ponticello di poppa, varco con la
  scaletta): senza, la sonda dell'arrampicata trova il fondo interno dello scafo (skill §6);
- le pareti della cabina e l'impavesata qui, non nella resa.

### 4.5 `SwimCollision`

Il fasciame esterno **aperto in alto** (stesse sezioni della `HydroHull`, senza coperta, con lo
specchio): parete per chi nuota (lo scafo del corpo rigido è sul layer 5, che il giocatore non vede)
e appoggio per issarsi. `swim_shell()` di `build_lagoon_boats.py` la costruisce dalle stesse
`rings_fn`. Chi sta dentro la barca non la tocca.

---

## 5. Fase 4 — Vuoti, attacchi e parti mobili

Tutti **Empty** di Blender (nodi `Node3D` in Godot), posizione e orientamento significativi. Assi
indicati in Blender e (fra parentesi) in Godot.

### 5.1 Scafo

| Nome | Obbl. | Posizione | Orientamento | Letto da |
|---|---|---|---|---|
| `Seat_Helm` | sì | **piedi** del pilota sul pagliolo (o sulla pedana: occhi 1,20 m sopra, vedi "Visuale al timone" §4.1); seduta 0,46 m sopra, centro del sedile 0,04 m dietro | nessuno | `BoatAssembly` → `HelmSeat` (+1 m) |
| `Seat_P1`, `Seat_P2`… | no | piedi dei passeggeri | – | [OGGI] nessuno (i passeggeri non si siedono): riservati |
| `Mount_Motor` | fuoribordo | perno del motore sul bordo dello specchio, in mezzeria | identità | `OutboardMotor` (origine del motore = perno del trim) |
| `Mount_Console` | volante | base della consolle, **0,25 m davanti a `Seat_Helm`**, stessa quota (il volante cade alle mani della clip `Driving`: 0,29 m avanti e 0,91 m su dal sedile) | identità | `BoatAssembly` |
| `Mount_Prop` | entrobordo | centro del mozzo dell'elica, ≥ 0,6·D sotto la DWL (ventilazione) | +Y locale (−Z) = spinta in avanti; rotazione X = **+inclinazione dell'asse** (6° gozzo, 3° peschereccio) | `ShaftDrive` |
| `Mount_Rudder` | entrobordo | centro di pressione della pala (~25% della corda dal bordo d'attacco, metà altezza); **a poppavia dell'elica e a meno di un diametro dal suo asse**, altrimenti niente scia sul timone | +Z (Y) = asse della pala; +Y (−Z) = verso prua | `ShaftDrive` |
| `Mount_Engine` | entrobordo | baricentro del motore + invertitore | – | `BoatMassModel` |
| `Mount_Thruster` | elica di prua | centro del tunnel, sotto la DWL di almeno un diametro del tunnel | – | `BowThruster` (spinge lungo X) |
| `Mount_Keel`, `Mount_Skeg` (o il nome scritto in `FoilDefinition.Mount`) | se c'è il profilo | **centro della lunghezza** del profilo (i segmenti si distribuiscono ± `Chord`/2) e **metà altezza** (la parte immersa si calcola ± `Span`/2) | +Y (−Z) = bordo d'attacco; +Z (Y) = apertura | `Foil` |
| `Mount_Ladder` | consigliato | piede della scaletta sul bordo d'imbarco | **+Y locale (−Z) verso l'interno** | accessorio `swim_ladder` |
| `Mount_NavLight` e altri `Mount_*` | no | dove va l'accessorio | come il modello dell'accessorio | `BoatAccessory.Mount` |

Parti mobili dell'entrobordo (oggetti mesh nello scafo): `Rudder` (ruota attorno a +Z di Blender,
Y di Godot, perno sull'asse della pala) e `Propeller` (ruota attorno al proprio asse Y di Blender,
Z di Godot). Vengono cercate **solo** se la config ha un `Inboard`.

### 5.2 Controlli automatici sugli attacchi

`check_boat_model.py` rifiuta `Mount_Prop`, `Mount_Rudder`, `Mount_Keel`, `Mount_Skeg`,
`Mount_Thruster` sopra la DWL. Il resto si vede nell'anteprima di Godot (§8): la massa del motore
(arancio) dov'è, l'elica sotto l'acqua.

### 5.3 Contratto dei modelli di motore e consolle

Se si ricava un fuoribordo o una consolle da un CAD, il modello deve rispettare:

| Modello | Origine | Nodi |
|---|---|---|
| Fuoribordo (`Outboard_*.glb`) | perno del trim sul bordo dello specchio | `Swivel` (asse di sterzo = +Z Blender / Y Godot), sotto di lui `Propeller` (ruota attorno al proprio Y Blender / Z Godot), `Thrust` (centro dell'elica; +Y Blender / −Z Godot = spinta), `Tiller` (barra, mesh), `TillerGrip` (mano del pilota). Materiali `Motor`, `MotorTrim` |
| Consolle (`Console_*.glb`) | = `Mount_Console` | `Wheel` (ruota attorno al piantone, Y Godot), figli `Hand_L`, `Hand_R` (prese delle mani) |

### 5.4 [PROPOSTA] Aree di personalizzazione in gioco

Le barche di base saranno spoglie (sedute, consolle, motore) e si allestiranno in gioco con
contenitori, casse e zone di stivaggio. Il codice oggi **non** lo supporta (allestimenti non
cambiabili a partita in corso, accessori senza massa: skill §14). Perché i modelli di oggi siano
pronti, si propone:

- **`Bay_<Nome>`**: Empty di tipo *Cube* (lato 2 a scala 1) con origine **al centro del piano
  d'appoggio**, assi della barca, **scala = metà delle dimensioni utili** (x mezza larghezza, y mezza
  lunghezza, z mezza altezza libera). Si legge tutto dalla trasformata: niente proprietà custom
  (l'export attuale ha `export_extras=False`).
- I limiti dell'area (massa massima, categorie ammesse, passo della griglia d'aggancio, es. 0,25 m)
  stanno nella `BoatConfig` [PROPOSTA: `BoatBay[]`], non nel modello: sono dati di gioco.
- Vincoli per il modello: piano d'appoggio **piatto e presente in `DeckCollision`**, altezza
  libera reale, nessun oggetto di resa dentro il volume, nessun `Mount_*` dentro.
- Vincoli fisici: la somma delle masse massime delle aree + equipaggio massimo ≤ **portata** della
  scheda (m_MTL); la statica (§8) va verificata **anche a pieno carico nelle aree più alte e più
  laterali** (GM e GZ restano nei limiti).
- Vincoli di rete (CLAUDE.md §3) per chi lo implementerà: richiesta d'intento all'host
  ("metti l'oggetto X nell'area Y, cella Z"), validata (distanza, spazio, massa); la massa entra in
  `BoatMassModel` come le masse dinamiche; la configurazione montata va replicata.

---

## 6. Fase 5 — Export, controllo, import

### 6.1 Export glTF da Blender

Come `export()` in `build_lagoon_boats.py`:

| Opzione | Valore | Perché |
|---|---|---|
| Formato | GLB | un file |
| `+Y up` | sì | assi di §1 |
| Selezione | tutti gli oggetti della barca (radice Empty all'origine) | |
| Applica modificatori | **sì** per un modello fatto a mano | un *Mirror* non applicato esporta mezzo scafo |
| Animazioni, skin, camere, luci | no | |
| Materiali | export | i nomi servono alla livrea |
| Trasformazioni | scala e rotazione applicate sulle mesh | |

Destinazione: `assets/models/boats/<Nome>.glb` (un motore o una consolle nuovi: file a parte).

### 6.2 Controllo automatico: `tools/blender/check_boat_model.py`

Da un Blender **separato** in background (svuota la scena: mai nel Blender dell'utente via MCP):

```
"C:\Program Files\Blender Foundation\Blender 5.2\blender.exe" --background --factory-startup --python-exit-code 3 --python tools/blender/check_boat_model.py -- assets/models/boats/<Nome>.glb [--dwl Z] [--band 0.3] [--console assets/models/boats/Console_<X>.glb]
```

Per le barche a volante passare **sempre** `--console` con la consolle della configurazione: la monta
su `Mount_Console` per il controllo della visuale (ruota e strumenti stanno davanti agli occhi).

Esce con 1 se fallisce un controllo bloccante (2 se il file non si apre, 3 per un'eccezione).
Controlla: nodi obbligatori (`HydroHull`, `HullCollision`, `DeckCollision`, `SwimCollision`,
`Seat_Helm`), nomi duplicati o con suffissi di Godot, attacchi subacquei sopra la DWL, `HydroHull`
chiusa / manifold / verso coerente / senza autointersezioni / Eulero, triangoli per ruolo e della
resa, lato massimo del fasciame bagnabile e aghi, dimensioni, la **carena alla DWL** (volume,
dislocamento ×1025, LCB, TCB, LWL, immersione, superficie bagnata) e la **visuale al timone**: dagli
occhi del pilota seduto (`Seat_Helm` + 1,20 m) 21 raggi verso prua (imbardata −30…+30° ogni 10°,
beccheggio 0/−8/−16°) contro tutta la resa tranne i vetri (`Glass`); libero = niente di opaco entro
2 m. Sotto il 30% di raggi liberi è un ERRORE (il pilota non vede fuori), sotto il 60% un avviso; le
righe `[visuale]` dicono quali raggi si fermano e a che distanza (0,3 m = ruota o mani, ~1 m =
parete o montante della timoneria).

Uscita sui modelli attuali (riferimento):

```
Gozzo_Placeholder   HydroHull 848 tri · 1 guscio · eulero 2 · 0 aperti · 0 incoerenti · 0 intersezioni
                    lato fasciame 0,66 m · L 7,0 · B 2,4 · carena DWL 4,23 m³ (4332 kg) · LCB −0,22 · LWL 6,3
Peschereccio_Pl.    HydroHull 1136 tri · lato fasciame 1,35 m · carena DWL 170,2 m³ (174,5 t) · LWL 18,5
Barchino_Tiller     HydroHull 500 tri · lato fasciame 0,44 m (con --dwl -0.15: 306 kg, LWL 3,36)
```

Il dislocamento alla DWL **deve tornare con la massa della scheda** nella condizione scelta (§7.2):
scarto ≤ 3%. Se non torna, la DWL è sbagliata o la forma non è quella vera; non si corregge con le masse.

### 6.3 Import in Godot

- Godot genera il `.glb.import` al primo import: confrontarlo con quello di un'altra barca
  (**non copiarlo**: contiene l'uid del file) e lasciare `nodes/use_name_suffixes=true` come le
  altre (da qui le regole sui nomi del §1).
- Senza editor: `Godot_v4.7.1-stable_mono_win64_console.exe --path . --headless --import`.
- Nessun avviso `[BoatAssembly] ... non ha una mesh HydroHull valida` né `non ha l'attacco ...`.

---

## 7. Fase 6 — `BoatConfig` e scena

### 7.1 File

1. `resources/boats/<nome>.tres` (`BoatConfig`): copiare quello della barca più simile
   (`barchino_*.tres` planante con fuoribordo, `gozzo.tres` dislocante ad asse, `peschereccio.tres`
   nave con mantello ed elica di prua) e sostituire i dati. Le sotto-risorse si possono tenere
   interne o condividere (`hydro_barchino.tres` è condivisa: cambiarla cambia entrambi i barchini).
2. `vehicles/scenes/boats/<Nome>.tscn`: scena ereditata da `Boat.tscn`, solo `Assembly.Config`
   impostato. Mai salvare i `.tscn` con BOM.
3. Nel livello: istanza della scena, posa, `VehicleId` unico.

### 7.2 Masse: il bilancio dei pesi

Si sceglie e si annota **una condizione di carico di riferimento** per la `.tres`. Consigliata:
**barca leggera + dotazioni + 50% dei consumabili, senza persone** (le persone sono masse dinamiche:
80 kg + inventario, nella posizione vera, aggiunte dall'host).

| Voce | Dove va | Posizione |
|---|---|---|
| Scafo strutturale, coperta, pagliolati, panche, **consolle e accessori** (non hanno massa propria [OGGI]), tientibene, impianti | `HullMass` | `HullCenterOfMass` |
| Fuoribordo (+ olio, elica) | `OutboardDefinition.Mass` | automatica sullo specchio |
| Piede poppiero (solo il piede) | `OutboardDefinition.Mass` | automatica |
| Motore entrobordo + invertitore (anche il motore di un piede poppiero) | `EngineDefinition.Mass` (o `MassItem`) | `Mount_Engine` |
| Asse, elica, timone, chiglia di zavorra, bulbo | `MassItem` | dal CAD |
| Serbatoi (capacità × densità × riempimento) | `MassItem` con `Size` | dal CAD/scheda |
| Batterie, acqua dolce, ancora e catena, dotazioni | `MassItem` | |
| Sovrastrutture pesanti, portale, verricelli | `MassItem` | |
| Zavorra fissa | `MassItem` | bassa, lungo la chiglia |
| Carico di stiva | `MassItem` (oggi) / area [PROPOSTA §5.4] | |
| Persone | — (dinamiche) | |

**HullMass = Δ(condizione) − Σ altre voci.** Esempio: barca a vuoto 4000 kg dichiarati con motore
e batterie → HullMass = 4000 − motore − batterie − serbatoi pieni al 50%...

Verifiche: il totale dell'anteprima (`Massa ... kg`) = Δ della condizione ± 1%; la somma delle
masse ≠ massa dello scafo "a vuoto" della brochure, se quella escludeva il motore.

### 7.3 Coefficienti: da dove partire

| Famiglia | Parti da | Cambia per prima cosa |
|---|---|---|
| Planante (skiff, open, taxi, barchino) | `hydro_barchino.tres` | `ReferenceSpeed` = crociera, `SlamMaxG` per la classe (open 6, cabinato 3) |
| Semi-planante / semi-dislocante (topa, mototopo, motoscafo da lavoro) | `hydro_barchino.tres` con `Hull = SemiDisplacement`, `WaveResistanceScale` 0,8, `LateralPressureScale` 2-3 | `WaveFadeFnv` |
| Dislocante piccolo (gozzo, sanpierota, sandolo a motore) | sotto-risorsa di `gozzo.tres` | `LateralPressureScale` |
| Nave (vaporetto, peschereccio) | sotto-risorsa di `peschereccio.tres` | `SafetyAngular.x` (rollio) |

### 7.4 Propulsione: dalla scheda ai campi

| Dato di scheda | Campo |
|---|---|
| Potenza (kW, all'elica per i fuoribordo) | `MaxPowerKw` (÷ 0,96 se all'elica) |
| Regime di potenza massima / campo WOT | `RatedRpm` / `LimiterRpm` (estremo alto + 3%) |
| Minimo | `IdleRpm` |
| Rapporto di riduzione | `RatioForward` / `RatioReverse` |
| Elica di serie D × P (pollici) | `Diameter` / `Pitch` (× 0,0254) |
| Peso a secco | `Mass` (fuoribordo) o `EngineDefinition.Mass` (entrobordo) |
| Consumo specifico | `BsfcGPerKwh` |
| Campo del trim | `TrimMinDegrees` / `TrimMaxDegrees` |

Elica giusta = **regime a tutto gas, alla velocità massima, dentro il campo WOT del costruttore**.
Sopra il campo: passo troppo corto o `Kq0` basso; sotto: troppo passo.

### 7.5 Governo e accessori

- `Helm = Tiller` solo col fuoribordo con barra (`TillerGrip` nel modello del motore); il pilota siede
  a dritta, barra a sinistra.
- `Helm = Wheel` + `Console`; l'entrobordo usa sempre il volante.
- Scaletta (`accessory_ladder.tres`, gruppo `swim_ladder`) su ogni barca col bordo libero > 0,6 m al
  punto d'imbarco.

---

## 8. Fase 7 — Statica (anteprima nell'editor + prova `statics`)

Aprire `vehicles/scenes/boats/<Nome>.tscn` (non un livello): l'`Assembly` mostra piano dell'acqua,
G (rosso), B (blu), masse (arancio) e il rapporto `Hydrostatics`; dopo ogni modifica dentro una
risorsa premere **Ricalcola anteprima**. Poi, senza editor:

```
Godot --path . --headless --fixed-fps 60 --script tools/boat_trials.gd -- res://vehicles/scenes/boats/<Nome>.tscn statics,drop,roll,heel
```

Criteri d'accettazione, nell'ordine (si correggono **masse e `HullCenterOfMass`**, non i coefficienti):

| Grandezza | Bersaglio |
|---|---|
| Massa | Δ della condizione ± 1% |
| Immersione di scafo | scheda ± 2 cm (≤ 12 m) o ± 3% (navi) |
| Assetto a riposo | da foto o scheda; in mancanza 0 ± 0,5° (dislocanti), 0…+1,5° prua su (plananti col fuoribordo) |
| Sbandamento a riposo | < 0,2° (G e scafo simmetrici) |
| GM | nella banda della classe (tabella sotto); ≥ 0,15 m sempre, ≥ 0,35 m pescherecci a ponte singolo (IMO) |
| Periodo di rollio | banda della classe; per le navi anche la formula IMO ± 20% (§2.7) |
| Raggi d'inerzia | rollio 0,35-0,45 · B, beccheggio 0,22-0,28 · L |
| Curva GZ (barche commerciali ≥ 24 m, Codice IMO 2008 Parte A §2.2) | GZ ≥ 0,20 m a 30° o oltre; massimo a ≥ 25°; aree ≥ 0,055 m·rad fino a 30°, ≥ 0,09 fino a 40°, ≥ 0,03 fra 30° e 40° |
| Riserva di galleggiamento (`riserva ×` nel rapporto: volume della `HydroHull` / carena) | ≥ 2 (i preset vanno da ~2 del peschereccio a oltre 5 delle barche aperte, chiuse alla falchetta) |
| `heel` (90 kg a 0,7 m o il peso della classe) | coincide con l'idrostatica |
| Pieno carico | ripetere con la portata a bordo (`DebugSetMass` o `MassItems` temporanei): bordo libero e GM ancora nei limiti |

Bande di classe (indicative; i valori dei preset sono misurati, gli altri da verificare con dati A/B):

| Classe | L | GM | Rollio | `SlamMaxG` |
|---|---|---|---|---|
| Skiff / barchino planante | 4-6 m | 0,8-1,6 m (barchino 1,1) | 1,0-1,6 s | 10 |
| Open / taxi planante | 7-10 m | 0,6-1,2 m | 1,8-2,8 s | 6 open, 3 cabinato |
| Gozzo, topa, sanpierota a motore | 6-9 m | 0,4-0,8 m (gozzo 0,57) | 2,3-3,0 s | 4 |
| Vaporetto, motobattello | 20-25 m | formula IMO dal periodo | da video | 2 |
| Peschereccio | 18-30 m | ≥ 0,35 m, tipico 0,5-1,0 | 6-9 s | 2 |

---

## 9. Fase 8 — Dinamica (`tools/boat_trials.gd`)

```
Godot --path . --headless --fixed-fps 60 --script tools/boat_trials.gd -- res://vehicles/scenes/boats/<Nome>.tscn
```

Senza voce in `TARGETS` la barca dà solo misure (PASS se finite). Per avere bersagli si aggiunge una
voce con le chiavi esistenti: `roll_period`, `speed_kn`, `bollard_kgf`, `plane_s`, `plane_trim`
(intervalli), `deck_y` (quota del ponte nello spazio della barca), `weight`/`arm` (peso della prova
`heel` e braccio), `settle`/`run` (secondi di assestamento e di corsa).

Ordine di taratura (uno alla volta, rilanciando le prove precedenti):

| # | Prova | Bersaglio | Si tocca |
|---|---|---|---|
| 1 | `bollard` | scheda, o regola kgf/kW di §2.7, ± 15% | `Kt0` (e `Kq0` per il regime) |
| 2 | `speed` | velocità di scheda alla condizione di scheda ± 5%; regime WOT nel campo; regresso (§2.7) | `PressureQuadratic` (c_p2), `WaveResistanceScale`; plananti anche `PressureLeadingEdge`/`TransomRelief` |
| 3 | `speed` (plananti) | uscita in planata 2-6 s, assetto in planata 3-6° | masse longitudinali prima, poi trim |
| 4 | `turn` | diametro: plananti 2-4 L, dislocanti 2,5-5 L (IMO: diametro tattico ≤ 5 L); verso dello sbandamento come la barca vera (i dislocanti sbandano verso l'esterno, i plananti a V verso l'interno) | area di timone/chiglia, `LateralPressureScale` |
| 5 | `stop`, `reverse`, `kick`, `thruster` | arresto ≤ 15 L (IMO); in retro la poppa va a sinistra con elica destrorsa | `PropWalk*`, `ReverseEfficiency` |
| 6 | `roll` | periodo (§8) e smorzamento ζ 0,05-0,15 | inerzia (masse), poi `SafetyAngular.x` |
| 7 | `waves` | mare 3 e 4, tre rotte: `InvalidSteps` = 0, `ClampedSteps` = 0, nessun NaN | `SlamK`, `SlamMaxG` |
| 8 | costo | ms per passo nel budget (§4.2) | triangoli della `HydroHull` |

---

## 10. Fase 9 — In gioco

1. `tools/playtest_boats.gd -- <cartella> <Barca>`: aggiungere il nome del nodo alla lista; prova
   timone, guida, salita dall'acqua (e dai varchi, se ci sono cancelli). **Guardare** lo screenshot
   `<Barca>_prima_persona_avanti`: sguardo dritto a prua da seduti, si deve vedere l'acqua oltre i
   vetri (il controllo a raggi del §6.2 non vede pedane mancanti nella collisione né il corpo).
2. Multi-istanza con ENet locale (CLAUDE.md §6): un passeggero remoto resta fermo rispetto al ponte;
   chiudendo la finestra del pilota la barca si ferma entro 0,5 s.
3. A piedi: tutti i passaggi percorribili (§4.1), nessun punto in cui si resta incastrati o si cade
   attraverso il ponte; dall'acqua si sale dove previsto.
4. Visuale in terza persona al timone (V): la distanza si adatta da sola; controllare che la camera
   non entri nella sovrastruttura.
5. Aggiornare la tabella degli allestimenti del §9 di `SKILL.md` (una riga: config, scafo,
   propulsione, misure).

---

## 11. Diagnosi rapida

| Sintomo | Causa probabile | Rimedio |
|---|---|---|
| Galleggia troppo alta o bassa | massa o scala del modello; DWL sbagliata | bilancio dei pesi, LOA del modello, `check_boat_model` (carena alla DWL) |
| Sbandata a riposo | scafo asimmetrico, G con X ≠ 0, `MassItem` laterale | TCB nel controllo, posizioni delle masse |
| Appruata / appoppata a riposo | LCG lontano da LCB | `HullCenterOfMass.z`, posizione del motore |
| Affonda o "esplode" al primo passo | `HydroHull` aperta, facce girate, gusci compenetrati | controllo; mai fidarsi della mesh di resa |
| Trema o saltella in acqua ferma | triangoli ad ago o troppo lunghi alla linea d'acqua | più stazioni, vertice sulla DWL |
| Rolla troppo veloce | GM troppo alto o inerzia troppo bassa (raggio < 0,35 B) | KG più alto, masse con `Size` |
| Si capovolge da sola | GM < 0 | KG troppo alto, zavorra |
| Velocità troppo alta senza resistenza d'onda | `Hull = Planing` su un dislocante | famiglia giusta |
| Plana su troppa superficie, assetto ~1° | `PressureLeadingEdge` 0 su un planante | 1 (come il barchino) |
| Motore fuori giri a tutto gas | elica sbagliata (`Pitch`, `Kq0`) | §7.4 |
| Timone senza effetto | `Mount_Rudder` non a poppavia dell'elica o lontano dal suo asse; pala sopra l'acqua | §5.1 |
| Elica che ventila sempre | `Mount_Prop` troppo vicino alla superficie | ≥ 0,6·D sotto la DWL |
| In virata scivola di 30° | dislocante con `LateralPressureScale` 1 | 4-6; chiglia in più segmenti |
| La barca urta i ponti con "l'aria" | sovrastruttura dentro `HullCollision` | solo scafo |
| Il giocatore cade nel ponte | `DeckCollision` sottile o mancante lì | scatole ≥ 5 cm |
| Chi nuota attraversa lo scafo | manca `SwimCollision` | §4.5 |
| Avviso "non ha l'attacco" | nome sbagliato o con punto | §1 |
| Mezzo scafo in Godot | modificatore *Mirror* non applicato | §6.1 |
| Oggetto di ruolo mancante | nascosto nel viewport all'export | nascondere solo al render |

---

## 12. Limiti attuali del simulatore che la pipeline deve rispettare

- **Una sola elica per barca**: niente bimotore né passo variabile (skill §14). Un bimotore si
  approssima con un'elica equivalente (potenza sommata, D × √2 a parità di carico).
- **Piede poppiero**: si approssima con `OutboardDefinition` (`Mount_Motor` sullo specchio, `Mass` =
  solo il piede) e il motore come massa interna. Il **duo-prop** (eliche controrotanti) non c'è:
  `PropWalk*` ≈ 0, la reazione di coppia resta.
- **Motore elettrico**: `Type = Diesel` (il regolatore tiene i giri), `TorqueCurve` piatta 1,0
  da 0 a 1, `ThrottleTimeConstant` 0,05, `PumpingFraction` ~0,05. Batterie come `MassItems` basse.
- **Idrogetto, vele, foil attivi**: non modellati.
- **Allagamento, acqua imbarcata, carichi liberi, cime, ancora, danni**: no (skill §14).
- **Accessori e consolle senza massa**; massa del fuoribordo in posizione fissa rispetto all'attacco.
- **Allestimento non modificabile a partita in corso** (§5.4 è una proposta).
- `Seat_P*` non letti; gli `ItemPickup` sul ponte non seguono la barca (skill §8).
- Ogni barca costa il modello completo anche lontana (niente LOD fisico): conta i triangoli.

---

## 13. Checklist finale

- [ ] Scheda dati compilata, ogni valore con fonte e confidenza; licenza del modello sorgente annotata
- [ ] Modello in metri, prua +Y, DWL a z = 0, simmetrico; LOA e B ± 0,5%
- [ ] Resa nel budget, stile PSX (piatto, 64 px/m, nearest, materiali per nome, livrea in grigi)
- [ ] `HydroHull` chiusa, coerente, senza intersezioni, nel budget, vertice sulla DWL e sugli spigoli
- [ ] `HullCollision` convessa semplice; `DeckCollision` a scatole con piano d'imbarco; `SwimCollision` aperta
- [ ] Attacchi con nome e orientamento giusti; subacquei sotto la DWL; timone nella scia
- [ ] Export: modificatori applicati, oggetti di ruolo esportati, nessun suffisso di Godot né punto nei nomi
- [ ] `check_boat_model.py` OK (con `--console` se a volante); dislocamento alla DWL = Δ di scheda ± 3%
- [ ] Visuale al timone: occhi sopra i davanzali, ≥ 60% raggi liberi, screenshot `prima_persona_avanti` guardato
- [ ] `BoatConfig` col bilancio dei pesi; condizione di carico annotata
- [ ] Anteprima e `statics`: massa, immersione, assetto, GM, rollio, raggi, GZ nei bersagli
- [ ] `boat_trials` in ordine: punto fisso, velocità, planata, virata, arresto, rollio, onde, costo
- [ ] Voce in `TARGETS`; riga nella tabella del §9 di `SKILL.md`
- [ ] Prova in gioco e in multi-istanza

---

## Appendice A — Scheda dati (da copiare)

```
BARCA:                         CLASSE:                    MODELLO SORGENTE / LICENZA:
CONDIZIONE DI CARICO DELLA .tres:

DIMENSIONI                      valore   unità  conf.  fonte (link, pagina)
  LOA / LH / LWL                         m
  B (massima) / B_WL                     m
  D (altezza di costruzione)             m
  T di scafo (immersione)                m
  Bordo libero (prua/mezzo/poppa)        m
  Altezza fuori acqua (ponti, tetto)     m

MASSE
  Barca leggera m_LC                     kg
  Carico massimo m_MTL / persone         kg / n
  Dislocamento a pieno carico m_LDC      kg
  Serbatoi carburante / acqua            L
  Zavorra fissa                          kg

CENTRI E STABILITÀ
  LCG / VCG (KG)                         m
  LCB                                    m
  GM / periodo di rollio                 m / s
  Categoria CE / criteri applicati

FORMA
  Famiglia (planante/semi/dislocante)
  Angolo di V allo specchio (deadrise)   °
  Spigoli (n., quote)
  Coefficiente di blocco C_B             –

PROPULSIONE
  Tipo (fuoribordo/asse/piede/elettrico)
  Motore (modello esatto)
  Potenza (kW, all'albero o all'elica?) / regime
  Campo WOT / minimo / limitatore        giri/min
  Peso motore (+ invertitore)            kg
  Rapporto avanti / indietro             –
  Elica D × P, pale, mano                pollici
  Inclinazione dell'asse                 °
  Timone: area, altezza, corda           m², m
  Chiglia/skeg: area, altezza, lunghezza m², m
  Elica di prua: potenza / spinta        kW / kgf

PRESTAZIONI BERSAGLIO
  Velocità max (alla condizione…)        kn
  Velocità di crociera / regime          kn / giri
  Tiro a punto fisso                     kgf
  Diametro di evoluzione                 L

GIOCO
  Posti, passaggi, ponti percorribili
  Punto d'imbarco dall'acqua (bordo libero)
  Aree di personalizzazione previste (dimensioni, portata)
```

## Appendice B — Esempio di lettura: taxi acqueo da 9,3 m (classe "Lucietta")

| Dato di scheda (A) | Uso nel progetto |
|---|---|
| LOA 9,27 m, B 2,25 m | controllo di scala del modello (§3.3) |
| Immersione a vuoto 0,50 m | DWL della condizione leggera; `check_boat_model --dwl` deve dare ~4000 kg |
| 4000 kg a vuoto, 5300 kg a pieno carico, 14 persone | condizione di riferimento: 4000 kg; pieno carico verificato in §8 con +1300 kg |
| 200 kW elettrico, piede duo-prop | `OutboardDefinition` (piede) + `EngineDefinition` `Diesel` a curva piatta; `PropWalk*` ≈ 0 (§12) |
| Batterie 182 kWh | `MassItems` bassi (massa: dalla scheda delle batterie, oppure D) |
| 11 kn servizio, 30 kn stimati a vuoto | Crouch: C ≈ 172 → a pieno carico ≈ 26 kn; bersaglio `speed_kn` |
| (manca) GM, rollio | formula IMO: C = 0,373 + 0,023·(2,25/0,5) − 0,043·0,085 ≈ 0,473; con GM 0,8 m → T ≈ 2,4 s, probabilmente meno (sovrastima sulle barche piccole); bersaglio vero da un video |

Nel gioco: niente nome né marchio; è "un taxi acqueo da 9 m".

---

## Fonti

Motori
- Yamaha F40 (scheda USA): https://yamahaoutboards.com/outboards/115-30-hp/40-30-hp
- Yamaha F25 (Nuova Zelanda): https://www.yamaha-motor.co.nz/products/marine/outboard/portable-four-stroke-2.5---25hp/f25
- Yamaha F25, elica di serie: https://defender.com/en_us/yamaha-25-hp-tiller-outboard-motor-f25-2025
- Honda BF40/BF50: https://marine.honda.com/outboards/models/midrange/BF40-50 ·
  https://hondanews.com/en-US/releases/release-454a382480e076509efb69004c34c712-honda-bf40-bf50-specifications
- Yanmar 3YM30AE: https://yanmar.com/media/global/com/product/marinepleasure/sailBoatPropulsion/catalog/Datasheet-3YM30AE.pdf

Barche
- Lucietta, scheda tecnica (Repower/Serenella): https://www.repower.com/media/znsc50uc/scheda_tecnica_lucietta.pdf
- Comune di Venezia, capitolato taxi acqueo: https://venezia.acquistitelematici.it/tender/documenti/2625/20221019100637-CAPITOLATO-TECNICO.pdf
- Motobattello ACTV 80 (dati di registro): https://www.naviecapitani.it/navi%20e%20capitani/gallerie%20navi/Navi%20servizio%20pubblico%20interno/schede/A/ACTV/ACTV%2080.htm
- MotoBattello 3, retrofit ibrido: https://www.vulkan.com/en/branches/marine/hybrid-and-electric-drive-solutions/reference-projects/actv-water-bus
- Vaporetto: https://en.wikipedia.org/wiki/Vaporetto
- Flotta di Chioggia (UniPD): https://chioggia.biologia.unipd.it/banche-dati/banca-dati-della-flotta-peschereccia/istruzioni-banca-dati-sulla-flotta-peschereccia
- Volante di Chioggia: https://www.dissapore.com/spesa/la-pesca-a-chioggia-cose-i-metodi-e-come-la-vita-a-bordo-di-un-peschereccio/
- Barche tradizionali: https://www.veniceboats.com/it-flotta-barche.htm ·
  https://www.visitmuve.it/wp-content/uploads/2018/09/SCHEDE-PADIGLIONE-DELLE-NAVI-ITA.pdf ·
  https://www.insula.it/images/pdf/resource/quadernipdf/Q12-12.pdf ·
  https://cdn.shopify.com/s/files/1/0819/7757/3707/files/sanpiera.pdf ·
  https://www.montagnadiviaggi.it/2022/03/barche-veneziane-nomi-caratteristiche.html

Norme e metodi
- Codice IMO di stabilità integra 2008, MSC.267(85) (criteri Parte A §2.2, periodo di rollio §2.3,
  pescherecci Parte B §2.1.3): https://wwwcdn.imo.org/localresources/en/KnowledgeCentre/IndexofIMOResolutions/MSCResolutions/MSC.267(85).pdf
- ISO 8666 (dati principali, condizioni di carico), ISO 12217 (stabilità delle piccole unità),
  ISO 14946 (carico massimo), ISO 8665 (potenza dei motori marini): norme a pagamento, citate per le definizioni.

Modelli e strumenti
- GrabCAD, uso dei modelli: https://help.grabcad.com/article/246-how-can-models-be-used-and-shared · termini: https://grabcad.com/terms
- Import STEP in Blender: https://extensions.blender.org/add-ons/step-importer/ ·
  https://superhivemarket.com/products/cadprep ·
  https://superhivemarket.com/products/stepio---blender-step-import--export-add-on
