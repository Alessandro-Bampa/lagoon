---
name: combat-shooting
description: Sistema di tiro e danno del gioco (Fase 3 del prototipo). Carica questa skill quando tocchi la cartella combat/, oppure quando si parla di armi, sparare, danno, salute/HP, morte, hitbox, mira, reticolo, munizioni, ricarica, rinculo, dispersione/precisione, traccianti, collision layer 3D, o i file WeaponController, WeaponDefinition, WeaponInput, WeaponVisual, HealthComponent, HitboxComponent, AimResolver, ShotEffects, CrosshairOverlay, WeaponHudPanel, TargetDummy, CollisionLayers.
---

# Sistema di tiro (Fase 3)

Arma **hitscan** (nessun proiettile simulato: `WeaponDefinition.ProjectileSpeed` è dichiarato ma inutilizzato), mira col cursore, munizioni e ricarica reali, danno interamente host-authoritative.

Vive in `combat/scripts/` (11 script) + `combat/scenes/TargetDummy.tscn` + `core/Utils/CollisionLayers.cs`.

---

## 1. Come il pattern `RequestHit` di CLAUDE.md §3 è realizzato davvero

Lo snippet in CLAUDE.md §3 è **didattico**: mostra la forma del pattern, non la firma da usare. Una `RequestHit(int damage)` chiamabile dal client è **esplicitamente rifiutata** in questo progetto, perché lascia al client decidere *chi* ha colpito e *quanto* danno ha fatto — cioè esattamente ciò che il §3.2 vieta. Falsificarla sarebbe banale.

La realizzazione reale separa i ruoli:

| Ruolo | Chi | Come |
|---|---|---|
| Intento | client | `WeaponController.RequestFire(Vector3 aimPoint)` — RPC `AnyPeer`. Il client invia **solo il punto verso cui sta mirando**. Mai un bersaglio, mai un'origine, mai un ammontare di danno. |
| Calcolo | host | Ricava l'origine da `PlayerController.ResolvedSyncPosition` (lo stato *replicato* del tiratore, non `GlobalPosition`), tira il dado della dispersione, ri-traccia il raggio, applica il falloff. Dalla Fase 4 **non** si usa `SyncPosition` grezza: può essere in coordinate locali a un'imbarcazione (skill `vehicles-boats` §1). |
| Applicazione | host | `HealthComponent.ApplyDamage(...)` — metodo **normale, non RPC**. Non è raggiungibile dalla rete per costruzione. |
| Propagazione | rete | La salute è una proprietà replicata da un `MultiplayerSynchronizer` a **tutti** i peer. |
| Estetica | host → tutti | `WeaponController.BroadcastShot(...)` — RPC `Authority`, `Unreliable`. Traccianti e vampe, nessun effetto sullo stato. |

---

## 2. Mappa delle autorità

| Nodo | Autorità | Perché |
|---|---|---|
| `Player` (root) + `Synchronizer` | peer proprietario | Movimento client-authoritative (Fase 1) |
| `Player/Inventory` | **host** | Nessun client crea oggetti |
| `Player/Health` + `Health/Sync` | **host** | Nessun client decide di essere vivo |
| `Player/Weapon` + `Weapon/Sync` | **host** | Nessun client decide di aver colpito |
| `Player/WeaponInput`, `Player/Hud` | — | Solo locali; leggono `GetParent().IsMultiplayerAuthority()` |
| `TargetDummy/Health` + `Sync` | **host** | |

**Ordine di `_EnterTree` — attenzione a non romperlo.** `PlayerController._EnterTree` marchia *ricorsivamente* l'intero sottoalbero col peer proprietario. `HealthComponent` e `WeaponController` fanno poi `SetMultiplayerAuthority(HostPeerId)` nel proprio `_EnterTree`, anch'esso ricorsivo: è per questo che **i loro `MultiplayerSynchronizer` devono essere nodi *figli*, non fratelli sotto la root del Player** — altrimenti resterebbero sull'autorità del peer e la replica partirebbe dal lato sbagliato. Vale per qualunque nodo host-autoritativo aggiunto in futuro sotto `Player`.

**Visibilità della replica.** L'inventario fa push del proprio stato al *solo* proprietario (il contenuto delle tasche altrui non riguarda nessuno). Salute e stato dell'arma invece si replicano a **tutti** i peer: servono per le barre HP, per vedere chi impugna cosa, e sono il criterio di completamento della fase.

---

## 3. Flusso di un colpo

```
client            WeaponInput: cursore -> AimResolver.ResolveAimPoint (raycast)
  │                                       ↓ Vector3 aimPoint (INTENTO)
  ├── RpcId(host, RequestFire, aimPoint)
  │
host              validazioni in ordine: mittente == proprietario · arma è un WeaponDefinition ·
  │               l'istanza è ANCORA nel suo slot equip · non in ricarica · caricatore > 0 ·
  │               cadenza (RPM, tolleranza 10% per il jitter) · aimPoint finito ·
  │               distanza > MaxRange -> CLAMP, non rifiuto (mirare al cielo è legittimo)
  │               origine = PlayerController.SyncPosition + 1.1 m   ← stato REPLICATO, mai
  │                                                                   GlobalPosition né dati client
  │               dispersione (RNG host) -> IntersectRay -> HitboxComponent.ApplyDamage
  │
  ├── proprietà replicate (MagazineAmmo, RecoilSpread, CurrentHealth del bersaglio) -> tutti
  └── Rpc(BroadcastShot, origin, end, hit)  -> tutti, Unreliable, solo estetica
```

Le RPC di `WeaponController` seguono la stessa triade di `PlayerInventory`: `SubmitX()` (host = locale, client = `RpcId(host)`) → `RequestX` (`AnyPeer`, `ValidateSender()`) → proprietà replicate.

`HoldStillValid()` è il guard che decade l'impugnatura se l'arma esce dal suo slot equip (spostata, lasciata cadere, messa nello zaino). È chiamato prima di sparare e di ricaricare, e su ogni `PlayerInventory.HostStateChanged`.

---

## 4. Collision layer

Prima della Fase 3 tutto stava sul layer 1, quindi un raycast di mira avrebbe colpito la capsula di chi spara. Lo schema (`[layer_names]` in `project.godot`, rispecchiato da `core/Utils/CollisionLayers.cs`):

| # | Nome | Chi | Mask |
|---|---|---|---|
| 1 | `world` | `TestLevel/Floor` e geometria statica | 1 |
| 2 | `players` | `Player` (`CharacterBody3D`) | 1\|2\|3 |
| 3 | `enemies` | `TargetDummy`, futuri nemici | 1 |
| 4 | `hitbox` | `HitboxComponent` (`Area3D`) | **0** — non interroga mai, viene solo interrogata |
| 5 | `vehicles` | scafo delle imbarcazioni (`RigidBody3D`) | 1 — **mai** nella maschera dei player |
| 6 | `vehicle_deck` | ponte e parapetti (`AnimatableBody3D`) | **0** — è il ponte che i player calpestano |

Ogni query di tiro usa `AimMask = world | hitbox | vehicles | vehicle_deck`, `CollideWithAreas = true`, ed esclude il RID della *propria* hitbox. **La barca ferma i proiettili** come la geometria statica: chi sta dietro la murata è al coperto. Scafo e ponte sono separati perché la collisione in Godot è simmetrica e un giocatore che toccasse lo scafo lo spingerebbe via (vedi skill `vehicles-boats` §2). Conseguenze: un raggio **non colpisce mai il corpo fisico** di un giocatore (solo la sua hitbox), quindi il fuoco amico e l'immunità a sé stessi diventano esatti anziché approssimati per distanza.

Un nuovo tipo di entità danneggiabile va montato con la stessa coppia: corpo sul proprio layer + `HitboxComponent` (`Area3D`) su layer 4 con `HealthPath` verso il suo `HealthComponent`.

---

## 5. Dispersione e rinculo

`WeaponDefinition.SpreadDegrees(aimDistance, recoil)` è l'**unica** implementazione della formula: `BaseSpread + SpreadPerRange · clamp(distanza/EffectiveRange) + rinculo`, con tetto `MaxSpread`. La usano sia l'host per tirare il dado sia `CrosshairOverlay` per disegnare l'anello, quindi l'area mostrata è davvero quella in cui il colpo può cadere. Mirare più lontano allarga l'anello: è il requisito "più miri distante, più l'arma è imprecisa".

**Il dado si tira solo sull'host.** Nessuno scambio di seed. Ne discende la regola: **il client non disegna mai un tracciante predittivo**, perché non sa dove è andato il proprio colpo — il tracciante arriva con `BroadcastShot`, cioè ~1 RTT dopo. La vampa alla bocca è invece immediata e locale, e basta a rendere reattivo il feedback. L'alternativa (seed condiviso) richiederebbe che host e client concordino *anche* sull'origine, cosa che non fanno (vedi §7).

Il rinculo, senza camera in prima persona da far rinculare, si manifesta come: dispersione accumulata e replicata (l'anello del reticolo "fiorisce" e si richiude), scossa locale della camera (`IsometricCamera.AddKick`, sola traslazione — **la camera non ruota mai**, la matematica di `AimResolver` assume un orientamento fisso), e arretramento della mesh dell'arma su tutti i peer.

**Cosa si replica**: `RecoilSpread` (il solo contributo di rinculo), non la dispersione totale. Il termine di distanza dipende da dove punta il cursore del singolo giocatore, quindi il reticolo lo aggiunge in locale passando `RecoilSpread` alla stessa formula dell'host.

---

## 6. Mira con camera ortogonale

`AimResolver.ResolveAimPoint(camera, mousePos, exclude)`: raycast dal cursore contro `AimMask`, con fallback sull'intersezione col piano orizzontale a `ChestHeight = 1.1f`.

Con la proiezione **ortogonale** `ProjectRayNormal` restituisce la stessa direzione per ogni pixel dello schermo; varia solo l'origine. Conseguenza accettata: il raggio può agganciare un bersaglio che in coordinate mondo sta "dietro" al giocatore, ma che sullo schermo è sotto al cursore. È il comportamento intuitivo — si mira a quel che si vede — e l'host valida comunque la distanza massima.

La posizione del mouse va sempre letta con `GetViewport().GetMousePosition()`: è in coordinate logiche, quindi resta corretta sotto `ContentScaleFactor` (vedi skill `ui-hud`).

---

## 7. Limite noto accettato: nessuna lag compensation

L'host ri-traccia da `SyncPosition`, che per un tiratore remoto è vecchio fino a ~1 RTT. Chi si muove veloce può vedere il tracciante partire leggermente dietro al proprio avatar, e un bersaglio spostatosi durante l'RTT può essere mancato pur sembrando colpito sul client.

Il rimedio (buffer storico delle posizioni per peer + rewind lato host) va implementato **insieme** alla validazione anti-cheat del movimento, già rimandata in Fase 1: sono lo stesso lavoro.

---

## 8. Altre lacune volute

- **Nessuna conseguenza della morte**: a 0 HP `IsDead` diventa vero ma un giocatore resta in piedi e continua a giocare. Morte, rianimazione e loot del cadavere sono post-prototipo. I cadaveri riuseranno il meccanismo `Anchored` dei contenitori nel mondo (vedi skill `inventory-tarkov`).
- **Fuoco amico attivo**: voluto, serve a validare il danno fra peer.
- **Il caricatore non è un item di griglia**: vive come stato host-side in `WeaponController._magazines` (per `InstanceId`, così sopravvive al cambio d'arma). È il motivo per cui le voci "Scarica caricatore"/"Svuota munizioni" del menu contestuale dell'inventario restano disabilitate.
- **Attributi dichiarati e non usati** in `WeaponDefinition`: `ProjectileSpeed`, `Penetration`, `Caliber`. Servono a fissare la superficie ora, così i `.tres` non vanno rifatti quando arriveranno proiettili fisici e armature.

---

## 9. Verifica manuale

Multi-istanza locale (CLAUDE.md §6), trasporto `LocalEnet`, finestra A host e finestra B client.

**Criterio di completamento della fase**: B (non-host) spara a un manichino del `TestLevel` e la `Label3D` sopra il bersaglio cala **della stessa quantità su tutte le finestre**; a zero il manichino diventa rosso su entrambe e respawna dopo 6 s, di nuovo in sincrono.

Altri controlli utili: arma replicata (B preme 1, A vede il box in mano), niente autocolpo (mirare ai propri piedi non toglie HP), l'anello cresce mirando lontano e fiorisce col fuoco automatico, R ricarica solo a inventario chiuso.
