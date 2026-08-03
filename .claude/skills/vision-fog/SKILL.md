---
name: vision-fog
description: Campo visivo e nebbia dinamica (shroud) — cono di visione variabile con la mira, occultamento dei personaggi non visti, maschera polare condivisa con gli shader. Carica questa skill quando tocchi vision/, VisionSource, VisionMask, VisibilityGate, ShroudRenderer, VisionRegistry, VisionGlobals, Shroud.gdshader, vision.gdshaderinc, quando lavori su linea di vista/occlusione/raggio visivo, quando un nemico non compare o non sparisce, quando lo shroud e' tutto nero o non si vede, o quando devi scrivere il primo shader di un nuovo sistema.
---

# Visione e shroud

Ambito: `vision/`. Cinque pezzi, un solo calcolo condiviso.

| Nodo | Dove sta | Che fa |
|---|---|---|
| `VisionSource` | figlio del root del personaggio | calcola cosa vede quel personaggio |
| `VisionMask` | figlio del root del giocatore | pubblica quel calcolo agli shader, come maschera polare |
| `ShroudRenderer` | figlio di `PlayerCamera` | annerisce a schermo cio' che sta fuori |
| `VisibilityGate` | figlio del personaggio da nascondere | nasconde chi non e' visto |
| `VisionRegistry` | statico, senza stato | trova la `VisionSource` dell'avatar locale (gruppo `local_vision`) |

---

## 1. La regola che tiene insieme il sistema

> **`CanSeePoint` e' la fonte di verita'. Il ventaglio di raggi (`Radii`) e' solo la sua
> discretizzazione, e ne esce UNA SOLA VOLTA — nella maschera di `VisionMask` — per servire tutti i
> consumatori a schermo.**

Cono e bolla si combinano in **un solo posto**, `VisionSource.RadiusAt(angle)`, e ci passano sia la
query puntuale sia il ventaglio. Se il rendering avesse parametri propri si otterrebbero nemici
visibili a schermo ma "non visti" dal gioco. L'unica divergenza accettata e' la risoluzione angolare
del ventaglio (`RayCount = 256`, ~1.4°).

Oggi il consumatore della maschera e' **uno solo**, `Shroud.gdshader`. C'e' stato un secondo lettore
— il materiale di mondo, che apriva le superfici quando il personaggio vedeva dietro di esse — ed e'
stato rimosso (skill `building-cutaway` §5). La maschera resta comunque un pezzo a se', in
`VisionMask` e `vision/shaders/vision.gdshaderinc`, e non e' stata riassorbita dentro lo shroud:
**non appartiene allo shroud**, che e' solo il primo ad averla letta. Chi tornera' a chiedersi
«questo punto si vede?» deve passare di li'.

## 2. Forma del campo visivo: cono + bolla, in UNIONE

Due profili interpolati su un solo scalare `_aimBlend`, piu' una bolla sempre attiva:

| | raggio | apertura |
|---|---|---|
| passivo | 12 m | **360°** |
| mira | 30 m | 34° |
| bolla periferica | 6 m | **360°**, sempre |

**Non esiste alcun settore cieco, in nessuno stato.** Il limite della visione e' sempre e solo la
DISTANZA: 12 m tutt'intorno a riposo, e in mira 30 m dentro il cono con 6 m tutt'intorno.

E' una decisione presa e poi **ribaltata**, quindi non riproporla senza chiedere. Il cuneo cieco alle
spalle c'era ed e' stato tolto su richiesta: in vista dall'alto il giocatore non ha un modo naturale
di "girarsi a controllare", perche' il corpo insegue il cursore — il cuneo rendeva il movimento
scomodo senza aggiungere tensione utile. Il compromesso rischio/ricompensa vive tutto sulla **mira**,
dove il cono si stringe a 34° e la consapevolezza a media distanza sui fianchi si perde davvero.

La bolla e' in **unione** col cono (massimo per ogni angolo), non in sostituzione: e' cio' che
impedisce di diventare ciechi a un metro mentre si mira lontano.

Il caso "giro completo" e' esplicito in `VisionSource.Covers`, non affidato al confronto
`offset <= halfFov`: a 360° la semi-apertura vale esattamente `PI`, cioe' il massimo che
`Mathf.AngleDifference` puo' restituire, e un arrotondamento in eccesso azzererebbe il singolo raggio
dritto alle spalle — una spina nera dietro il giocatore, che si legge come un guasto.

**Non aggiungere isteresi sulla transizione passivo↔mira.** `SyncAiming` e' gia' stabilizzato a monte
dal latch di hip-fire (`CharacterMotor.HipFireAimSeconds = 0.6f`) e dalla modalita' a interruttore
(`SettingsService.AimToggle`). Una seconda costante di tempo in serie si sente come ritardo.
L'interpolazione su `BlendSpeed` e' gia' un filtro passa-basso.

Il calcolo e' **solo sul piano XZ**: `SyncAimPitch` e' ignorato di proposito, in vista dall'alto un
cono inclinato verticalmente non e' ne' leggibile ne' desiderabile.

## 3. Rete: non c'e'

`VisionSource` non ha stato replicato, non ha RPC, non ha guardie di autorita'. Legge **solo
proprieta' gia' replicate** del `CharacterMotor` (`ResolvedSyncPosition`, `SyncAimYaw`, `SyncAiming`),
esattamente come `NpcAnimationBridge`, quindi produce lo stesso risultato su ogni peer a partire
dallo stesso stato.

La visione e' **individuale**: ogni peer ha una sola `VisionSource` nel gruppo `local_vision`, quella
del proprio avatar. Due finestre possono legittimamente mostrare occultamenti diversi dello stesso
NPC, e aperture diverse sullo stesso muro.

> **Le globali degli shader sono PER PROCESSO.** `VisionMask` e `BuildingCullController` le scrivono
> senza coordinarsi con nessuno, il che e' legittimo solo perche' il progetto assume **un solo avatar
> locale per processo** — la stessa invariante su cui poggia il gruppo `local_vision`. Se un giorno
> servisse lo split-screen, questo e' il primo punto che si rompe, e non se ne accorgerebbe nessuno
> subito: si vedrebbe solo il mondo aprirsi attorno al giocatore sbagliato.

`CharacterMotor.ResolvedSyncPosition` e' `virtual` **per questo sistema**: `VisionSource` lavora su
giocatori e NPC e non deve conoscerne il tipo concreto. Sul motore base e' l'identita'; lo sovrascrive
`PlayerController` (un giocatore in barca ha `SyncPosition` locale allo scafo).

## 4. Il divieto sull'hitbox

`VisibilityGate` tocca **solo `Visual.Visible`**. Mai la hitbox, mai il corpo fisico, mai una
proprieta' replicata.

Disattivare la hitbox di un nemico non visto sembra la cosa giusta e **non lo e' in cooperativa**: la
hitbox e' unica e globale, la visione e' individuale, quindi spegnerla perche' IO non vedo il nemico
lo renderebbe incolpibile anche ai compagni. Sarebbe inoltre una decisione presa sul client a
proposito di stato di gioco — cio' che CLAUDE.md §3 vieta — e falsificabile.

**Conseguenza dichiarata e voluta:** si puo' sparare "a memoria" all'ultima posizione nota e colpire.
Se un giorno si volesse il contrario, il posto giusto e' una validazione host-side dentro
`WeaponController.RequestFire`, mai nel gate. Oggi `combat/` non e' toccato da questo sistema.

La comparsa e' **immediata**, la scomparsa **ritardata** (`HideDelay = 0.2 s`). L'asimmetria e'
voluta: un nemico che entra nel campo visivo non deve mai apparire in ritardo. Il ritardo serve
comunque, perche' senza, un bersaglio sul bordo del cono sfarfalla a ogni micro-movimento della mira.

Il gate sta su `NpcCharacter.tscn` **e su `Player.tscn`**: con la visione individuale anche il
compagno fuori dal proprio cono sparisce, ed e' la conseguenza diretta di quella scelta, non un
effetto collaterale.

**Non ci si nasconde a se' stessi**, e il test giusto non e' `IsMultiplayerAuthority()`: sull'host
quella e' vera anche per gli NPC (host-autoritativi), e l'host smetterebbe di nasconderli del tutto.
Il confronto e' con il proprietario della sorgente locale: `vision.GetParent() == owner` → sempre
visibile.

Il gate **non** sta su `TargetDummy.tscn`: i manichini sono il criterio di collaudo del tiro (skill
`combat-shooting` §9), e renderli invisibili lo renderebbe inutilizzabile. `TargetDummy` non ha
nemmeno un nodo `Visual`.

## 5. Occlusione, e il bias

`CollisionLayers.VisionBlockerMask = World | Vehicles | BuildingCover`. Volutamente diversa da
`ShotMask`:

- **niente `Hitbox`**: un nemico non deve nascondere un altro nemico;
- **niente `VehicleDeck`**: i raggi corrono all'altezza del petto e il parapetto della barca sta su
  quel layer — includerlo renderebbe ciechi al timone;
- **`BuildingCover` c'e', ed e' obbligatorio**: un solaio e' opaco. Senza, l'unica cosa che separa due
  piani sarebbe la distanza ORIZZONTALE — che fra chi sta sopra e chi sta sotto e' quasi zero — e il
  risultato era vedere i personaggi al piano di sopra attraverso il pavimento. Sui raggi del ventaglio
  non cambia nulla: corrono orizzontali all'altezza dell'occhio e un solaio sta sempre sopra o sotto.

Limite noto: cio' che ferma un proiettile e cio' che ferma lo sguardo coincidono per mondo, solai e
scafi ma non sono lo stesso concetto. Una rete metallica (si vede attraverso, ferma i colpi) vorra' un
layer proprio, non un ritocco a questa costante.

### La quota del bersaglio non si clampa

`CanSeePoint` tira il raggio verso il punto **reale, quota compresa**. C'e' stato un
`Mathf.Max(worldPoint.Y, eye.Y - 1f)` che impediva al raggio di scendere, e con `BuildingCover` nella
maschera sarebbe stato il difetto simmetrico del precedente: dal piano di sopra si vedeva quello di
sotto, perche' il raggio restava sopra il solaio invece di attraversarlo. Il caso che quel clamp
voleva salvare — un bersaglio in piedi dietro un muretto basso — e' gia' coperto da `CanSee`, che mira
all'altezza dell'occhio e non all'origine del nodo (che sta ai piedi).

### `SurfaceBias`: la superficie che ti blocca la vista, TU LA VEDI

Il raggio scritto nella maschera supera il punto d'impatto di `SurfaceBias` (1.0 m, **solo per il
rendering**). Senza, il punto d'impatto cade esattamente su `r`, cioe' in mezzo alla sfumatura
`smoothstep(r - edge_softness, r, dist)`: **la faccia del muro finisce in ombra sotto la propria
ombra**, e i suoi spigoli sfarfallano perche' quella banda oscilla quando i raggi passano da
"colpisce" a "manca".

Va tenuto **>= `edge_softness`**, altrimenti la sfumatura ricade comunque sulla superficie. Il prezzo
e' una perdita di luce di pari entita' *dietro* l'ostacolo, che a spessori normali resta nascosta
dall'ostacolo stesso.

> **Chi un giorno decidesse se si vede ATTRAVERSO una superficie deve TOGLIERLO.** Il bias e' il
> valore giusto per lo shroud e sbagliato per tutti gli altri: chi se lo tiene tratta come visibile un
> metro **dietro** ogni muro, e il risultato e' che ogni muro si apre da solo — il sintomo canonico e'
> stare all'angolo *fuori* da un edificio e vedere trasparenti i due muri esterni che non lasciano
> vedere niente. E' un errore che il sistema ha gia' fatto.
>
> Per questo `vision_visibility` (`vision/shaders/vision.gdshaderinc`) tiene il `bias` come
> **parametro esplicito** anche se oggi lo si chiama solo con `0`: la sottrazione deve essere una
> scelta che si vede nella firma, non qualcosa da ricordare. Lato C# non c'e' piu' nessun consumatore
> del ventaglio, quindi non c'e' piu' nemmeno il `ClearReachAt` che faceva la stessa cosa; chi tornasse
> a leggere `Radii` da C# deve rifarla a mano.

`CanSeePoint` **non** usa il bias: il gate resta esatto, il margine e' un fatto di sola resa.

Gli spigoli restano il punto debole: fra due raggi adiacenti la maschera interpola linearmente fra
"corto" (muro) e "lungo" (aperto), e quale raggio colpisce cambia mentre ci si muove. Il rimedio e' la
risoluzione angolare — da qui `RayCount = 256`. Se servisse di meglio, la strada e' un secondo canale
nella texture (`Rgf`) che marchi i settori occlusi e ne annulli la sfumatura, non altri raggi.

**Un ostacolo occlude solo se supera l'ALTEZZA DELL'OCCHIO**, cioe' ~2.1 m in coordinate mondo
(`EyeHeight = AimResolver.ChestHeight = 1.1 m` sopra i piedi). In `TestLevel` solo `Wall` ci arriva
(~2.8 m) e proietta un'ombra vera; `Wall2` (~0.7 m) e il molo (~1.25 m) restano sotto e i raggi ci
passano **sopra** — corretto, non un difetto.

E' la diagnosi da fare per prima quando "l'occlusione non funziona": misura l'altezza dell'ostacolo,
non cercare il bug nel codice. E' anche il motivo per cui la **finestra** di `TestBuilding` sta a
cavallo di quella quota (varco da y=1 a y=2): una finestra sotto l'altezza dell'occhio sarebbe solo un
disegno, perche' nessun raggio ci passerebbe.

## 6. Lo shroud: maschera POLARE, non un SubViewport

L'istinto e' rasterizzare il poligono in una texture cartesiana con un `SubViewport`. **E' l'errore da
non fare.** I dati sono gia' polari (un raggio per settore), e passare per una texture cartesiana
perde informazione due volte e obbliga a risolvere tre problemi che altrimenti non esistono:
ricentraggio della finestra, *texel swimming* mentre si cammina, risoluzione.

Il numero che decide: 256² su una finestra di 64 m = 0.25 m/texel = **~19 pixel a schermo** per texel
(ortho `Size = 14` a 1080p → ~77 px/m). Il filtro bilineare su texel cosi' grossi fa **colare la luce
oltre gli spigoli dei muri**, cioe' cancella l'informazione tattica che lo shroud deve negare.

Maschera usata: `ImageTexture` **`RayCount`×1, `Image.Format.Rf`**, valore = lunghezza del raggio
normalizzata su `MaxRange`. `filter_linear, repeat_enable` fa interpolare la GPU fra raggi adiacenti e
gestisce gratis la cucitura a ±π. Bordo morbido = `smoothstep` sulla distanza: **nessun blur, nessuna
seconda passata**, morbidezza in metri anziche' in texel. Netto in angolo, morbido in distanza — la
distinzione che una maschera cartesiana sfocata non sa fare.

Funziona perche' la forma e' **star-shaped**: un solo raggio per angolo. L'unione cono+bolla lo e', e
la linea di vista lo e' per definizione (lungo un raggio non si e' visibili, poi occlusi, poi di nuovo
visibili).

`ImageTexture.CreateFromImage` **una volta** in `_Ready`, poi `Update(_img)` ogni frame: ricrearla
allocherebbe una RID nuova a ogni frame.

### `Darkness = 0.55`: e' un'OMBRA, non un nero pieno

**Provato a 1.0 e scartato: non riproporlo.** Il ragionamento che ci porta e' seducente — se fuori
dalla linea di vista non si vedesse *nulla*, l'interno di un edificio intravisto attraverso un muro
sfumato resterebbe nero finche' non lo si guarda davvero, e il cutaway sarebbe "onesto" per
costruzione. A schermo pero' il risultato e' un'altra cosa: il mondo attorno diventa una parete nera,
l'inquadratura si chiude addosso al giocatore e si perde il senso del luogo. Fuori dal cono deve
restare **penombra**.

Conseguenza accettata: dentro un edificio si **intravede** la pianta della stanza anche dove non si
ha linea di vista. Se un giorno la si volesse negare, la strada **non** e' alzare `Darkness` — che
vale per tutto il mondo, terreno aperto compreso — ma dare allo shroud il modo di sapere quali
frammenti stanno dentro un edificio, cosa che oggi non ha: il quad e' fullscreen e ricostruisce solo
una posizione mondo. Servirebbero le impronte degli edifici come globali, o un canale in piu' scritto
dalla geometria.

## 7. Le globali, e come si rompono in silenzio

`VisionMask` pubblica quattro globali: `vision_mask`, `vision_origin`, `vision_range`,
`vision_ground`. I nomi stanno **solo** in `VisionGlobals`, ed e' l'unico scrittore.

Tre cose da sapere, tutte imparate rompendole:

1. **Le dichiarazioni devono stare in `[shader_globals]` di `project.godot`.** Registrarle da codice
   con `RenderingServer.GlobalShaderParameterAdd` **non basta**: Godot le risolve da ProjectSettings
   quando lo shader *compila*, cioe' prima che qualunque autoload possa intervenire.
2. **Una globale mancante non fa fallire il gioco, fa un WARNING**: `Shader uses global parameter 'X',
   but it was removed at some point. Material will not display correctly.` Il materiale continua a
   disegnare con valori sballati. Cercalo nei log prima di dare la colpa alla logica.
3. **Un sampler2D globale funziona** (verificato su 4.7.1, D3D12), con hint e filtri:
   `global uniform sampler2D vision_mask : filter_linear, repeat_enable, hint_default_white;`. Il
   `"value"` in `project.godot` puo' restare vuoto: `hint_default_white` copre il primo frame, ed e'
   la scelta giusta — al primo frame si vede **tutto**, mai nero.

`vision_range` ha default **30** in `project.godot` e non 0, per la stessa ragione: con 0, prima che
`VisionMask` scriva, lo shroud annerirebbe l'intero schermo.

## 8. Trappole dello shader (Godot 4.7)

Sono la parte fragile. In ordine di quanto costano quando si sbagliano:

1. **`DEPTH_TEXTURE` non esiste piu' in 4.7.** Va dichiarato come uniform:
   `uniform sampler2D depth_texture : hint_depth_texture, filter_nearest;`. Usarlo come builtin fa
   **fallire la compilazione**, e un materiale che non compila semplicemente non disegna nulla — un
   quad invisibile si scambia per "lo shroud non funziona". Se lo shroud sparisce, **leggi prima i log
   del gioco**.
2. **Niente `return` anticipati dentro `fragment()`**: stesso sintomo muto, compilazione fallita in
   silenzio. Usa un ramo `if/else` su una variabile locale (vedi `debug_mode`). Dentro le **funzioni**
   il `return` e' invece normale e usato ovunque.
3. **Profondita' grezza.** `INV_PROJECTION_MATRIX * vec4(SCREEN_UV*2.0-1.0, depth, 1.0)`. Qualunque
   frammento con `depth * 2.0 - 1.0` viene da OpenGL/Godot 3 e da' una ricostruzione *plausibile ma
   sbagliata*, con errore crescente con la distanza. Con la proiezione ortogonale `w` resta 1: la
   divisione e' un no-op, ma va tenuta.
4. **Culling.** Con `POSITION` sovrascritta la geometria non sta dove Godot crede:
   `ExtraCullMargin = 16384` + `CustomAabb` enorme + `IgnoreOcclusionCulling`, altrimenti il quad
   sparisce a intermittenza. E `CastShadow = Off`, o la passata d'ombra proietta un'ombra vagante.
5. **Ordine di disegno.** `blend_mul` mette il materiale nella passata trasparente (necessario per
   leggere la profondita') e moltiplica il colore gia' presente senza copiare il backbuffer;
   `RenderPriority = 127` lo tiene sopra acqua, traccianti e particelle.
6. **Materiali trasparenti non scrivono profondita'.** `Mat_Water` in `TestLevel.tscn` ha
   `depth_draw_mode = 1` **per questo motivo**: senza, sui pixel d'acqua si leggerebbe il fondale a
   y = −4.5 e la maschera verrebbe campionata ~5 m fuori posto su tutta la laguna. Vale per ogni futura
   superficie trasparente ampia.
7. Niente `source_color` sul tint: e' un moltiplicatore lineare, non un colore sRGB.
8. **Il quad sta sul render layer visivo 1 e deve restarci.** `ShroudRenderer` non imposta `Layers`,
   quindi eredita il layer 1; chi tocca il `CullMask` della camera (skill `building-cutaway`) deve
   tenere quel bit sempre acceso, o la nebbia sparisce del tutto. Corollario dell'altro verso: una
   superficie **esclusa dal `CullMask` non scrive profondita'**, quindi lo shroud la attraversa e si
   campiona il pavimento sotto. Per un piano culled e' il comportamento voluto. **Non lo e' per un
   materiale trasparente**, ed e' il motivo per cui `world/shaders/WorldSurface.gdshader` resta OPACO
   e sfuma con `discard` (skill `building-cutaway`).

### Convenzione angolare — vale un errore di 90°

Il gioco misura l'imbardata come `Atan2(dir.X, dir.Z)` (X per primo); lo shader e il ventaglio usano
`atan(z, x)`, la convenzione matematica. La conversione e' `θ = PI/2 − yaw` e vive in
**`VisionSource.ShaderAngleOf`**, con un nome, non sparsa nelle formule. Sbagliarla produce un cono
ruotato di 90°, che si legge come "il sistema non funziona" invece che come un errore di segno.

## 9. Diagnostica

`Shroud.gdshader` ha `debug_mode`: `1` = scacchiera in coordinate mondo, `2` = profondita' grezza.
`ShroudRenderer.Enabled` spegne l'effetto senza smontare nulla.

**La prova della scacchiera e' il primo controllo da fare**, sempre, prima di dare la colpa alla
maschera o al ventaglio: con `debug_mode = 1` il motivo deve restare **ancorato al mondo** mentre si
cammina, e salire correttamente su molo, rampa e barca. Se scivola col giocatore, il problema e' la
ricostruzione; se resta fermo, la ricostruzione e' giusta e il difetto e' a valle.

Attenzione: con la camera **ortogonale** e far plane 4000, `debug_mode = 2` mostra quasi bianco
ovunque — la profondita' e' lineare in Z di vista e tutta la scena sta in una fetta sottilissima vicino
a 1.0. Non e' un guasto.

### Far compilare gli shader senza aprire l'editor

Gli errori di shader sono **muti a schermo**: si vedono solo nei log. Il gioco si puo' far girare per
qualche centinaio di frame e chiudere da solo:

```
Godot_v4.7.1-stable_mono_win64_console.exe --path "c:/repositories/lagoon" \
    res://world/scenes/Levels/TestLevel.tscn --quit-after 300
```

**Attenzione: cosi' non basta.** `GameWorld` istanzia l'avatar solo su `PeerJoined`, quindi lanciando
il livello da solo **non esiste nessun Player**, e ne' `Shroud.gdshader` ne' `VisionMask` vengono mai
toccati: il run risulta pulito anche se sono rotti. Per un collaudo vero serve una scena usa-e-getta
con `TestLevel.tscn` **e** `Player.tscn` istanziati insieme (il nodo del giocatore va chiamato `1`,
perche' `PlayerController` legge il nome come id del peer). Con quella, un nome di globale sbagliato
compare subito nei log come `Shader uses global parameter '...', but it was removed at some point`.

## 10. Costi

Il ventaglio si calcola **solo per l'avatar locale** (`ComputeFan`), in `_PhysicsProcess`, e si salta
del tutto se posizione e imbardata non sono cambiate oltre una soglia. `CanSeePoint` resta disponibile
su qualunque sorgente anche con `ComputeFan` falso: e' la porta che usera' la percezione IA quando
arrivera', senza toccare questi file.

`VisibilityGate` interroga a ~15 Hz con sfasamento iniziale casuale, cosi' gli agenti non interrogano
tutti nello stesso frame.

`VisionMask` lavora in `_Process` (render time) e non in `_PhysicsProcess`: `vision_origin` deve
combaciare con la posizione che la camera usa per disegnare *quel* frame, altrimenti la maschera vibra
rispetto alla geometria — e con lei il bordo dello shroud **e i buchi nei muri**.

## 11. Cosa NON c'e', di proposito

- **Nessuna percezione IA.** `NpcCharacter` ha il gate (per essere nascosto) ma **non** una
  `VisionSource` propria: non esiste una macchina a stati che consumi un avvistamento, e un segnale
  senza consumatori sarebbe codice speculativo (CLAUDE.md §4). Quando arrivera' l'IA, le si monta una
  `VisionSource` e si chiama `CanSeePoint` — il componente e' gia' agnostico dal tipo.
- **Nessuna visione condivisa** fra compagni. Se servisse, il punto di intervento e' `VisionRegistry`:
  il gruppo conterrebbe piu' sorgenti e l'unione la farebbero i chiamanti.
- **Nessun livello "esplorato"** alla RTS: il terreno e' sempre visibile, serve una sola maschera.
- **Nessun testo visibile all'utente**, quindi nessuna chiave in `locales/`.
