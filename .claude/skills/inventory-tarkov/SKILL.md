---
name: inventory-tarkov
description: Sistema di inventario a griglia stile Escape from Tarkov (Fase 2). Carica questa skill quando tocchi inventory/, oppure quando si parla di inventario, griglia, tasche, zaino, gilet/rig, equipaggiamento, slot, oggetti/item, peso e ingombro, contenitori annidati, raccogliere o lasciare cadere oggetti, casse, loot, drag & drop, hotbar/menu rapido, o i file PlayerInventory, PlayerInventoryModel, ItemAddress, ItemTransfer, InventoryGrid, ItemDefinition, ItemInstance, ItemDatabase, ItemPickup, InventoryScreen, GridPanelView.
---

# Inventario a griglia (Fase 2)

28 script in `inventory/scripts/` + `inventory/scenes/ItemPickup.tscn`. Modello puro C# host-autoritativo, layer di rete sottile, UI locale che non muta mai nulla.

Questa skill documenta **gli invarianti che non vanno rotti**, non l'elenco dei file.

---

## 1. Stratificazione

| Strato | File | Dove gira |
|---|---|---|
| Dati (tipi) | `ItemDefinition` (+ sottoclassi come `WeaponDefinition`), `ItemCategory`, `EquipSlotType`, `ItemDatabase` (autoload) | ovunque, statico di build |
| Dati (istanze) | `ItemInstance`, `InventoryGrid`, `EquipmentSet`, `PlayerInventoryModel` | modello autoritativo host; i client ne tengono una copia in sola lettura |
| Regole | `ItemTransfer`, `ItemTree` | **solo host** |
| Rete | `PlayerInventory` | host autoritativo, push al solo proprietario |
| Mondo | `ItemPickup`, `GameWorld` | spawn solo host, nodi visivi su tutti |
| UI | `InventoryScreen` e ~13 view | **solo peer locale**, mai muta stato |

Sulla rete non viaggia mai una definizione: solo l'`ItemId` (stringa), che host e client risolvono identicamente con `ItemDatabase`.

> **I `.tres` non contengono testo.** Nome, descrizione ed effetto di un item sono chiavi derivate dall'`ItemId` (`ITEM_<ID>_NAME` / `_DESC` / `_EFFECT`) e vivono in `locales/items.csv`. Creare un item significa quindi anche aggiungere le sue righe lì, altrimenti `ItemDatabase` lo segnala all'avvio. Vedi la skill `i18n-localization`.

---

## 2. `ItemAddress` — lo schema di indirizzamento

`readonly struct` con 3 soli int (`Realm`, `A`, `B`), apposta per attraversare una RPC senza serializzazione custom.

| Realm | A | B |
|---|---|---|
| `PlayerGrid` (0) | `containerId`: `-1` = tasche (`PlayerInventoryModel.PocketsContainerId`); positivo = la `ContainerGrid` dell'item con quell'`InstanceId` | — |
| `PlayerEquip` (1) | `(int)EquipSlotType` | — |
| `WorldContainer` (2) | `Uid` dell'oggetto nel mondo | `InstanceId` del container dentro il payload; **`0` = l'item radice del payload** |
| `WorldLoose` (3) | **come destinazione: `0` = "a terra ai piedi del giocatore". Come sorgente: lo `Uid` dell'oggetto raccolto.** | — |

Quel doppio significato di `WorldLoose.A` è la cosa meno ovvia del sistema. `ItemAddress.Ground()` costruisce la forma-destinazione; la forma-sorgente si costruisce a mano (`new ItemAddress(RealmType.WorldLoose, uid)`).

Factory: `Pockets()`, `PlayerGridAt(id)`, `Equip(slot)`, `WorldContainerAt(uid, containerInstanceId = 0)`, `Ground()`. Alias di lettura sugli stessi due int: `Slot`/`ContainerId`/`WorldItemUid` leggono `A`, `ContainerInstanceId` legge `B`.

---

## 3. `ItemTransfer` — l'unico collo di bottiglia

Quasi ogni interazione (trascinamento, quick move, equip rapido, scarto, raccolta, saccheggio, voci del menu contestuale) è **UNO spostamento fra due `ItemAddress`**, gestito qui. È il motivo per cui le regole vivono in un punto solo invece di essere ripetute in una RPC per combinazione.

```csharp
public static bool Execute(Context ctx, ItemAddress from, int itemId, ItemAddress to, int x, int y, bool rotated)
```

`Context`: `Model`, `World` (GameWorld), `Resolve` (ItemId→definizione), `PlayerPosition`, `Reach` (= `PlayerInventory.PickupRange`, 3.5 m).

Tre fasi: **Extract** (estrae e restituisce un `rollback`) → **Rehome** (riassegna gli id se l'item cambia spazio) → **Insert** (valida e piazza). Su fallimento gira il rollback; su successo `session.Commit()`.

### Regole di validazione, nell'ordine
- **Extract / WorldLoose**: pickup esistente, entro `Reach`, e **non `Anchored`** (le casse non si raccolgono, si aprono).
- **Extract / WorldContainer**: pickup entro `Reach`, payload deserializzabile, e l'item trovato **non deve essere la radice** del suo albero.
- **Insert / PlayerGrid**: `FitsLoad` (peso) → griglia risolvibile → anti-ciclo → `TryAutoPlace` (se `x < 0`) o `Place(x, y, rotated)`.
- **Insert / PlayerEquip**: `FitsLoad` → `EquipInstance`, che impone tipo di slot corrispondente **e slot vuoto** (non esiste swap diretto).
- **Insert / WorldContainer**: pickup entro `Reach` → griglia → anti-ciclo. **Nessun controllo di peso**: i contenitori nel mondo non hanno carico massimo.
- **Insert / WorldLoose**: sempre riuscito, accoda uno spawn a `ctx.PlayerPosition`.

### Il rollback è un auto-stow, non un restore-in-place
Il rollback lato giocatore è `Model.TryStoreInstance(item)`: **rimette l'oggetto dove capita** (zaino → gilet → tasche, o auto-equip se lo slot è libero), non nella cella di partenza. Quindi una mossa rifiutata può comunque aver spostato l'item.

> È esattamente per questo che `PlayerInventory.RequestMove` chiama `PushState()` **anche quando `Execute` ritorna false**. Non "ottimizzarlo" in `if (changed) PushState()`: il client resterebbe con una vista sbagliata.

### `SpaceKey` e riassegnazione degli id
`"player"` per entrambi i realm giocatore, `"world:{uid}"` per ogni oggetto nel mondo, `"loose"` per terra. Se sorgente e destinazione hanno la stessa chiave, si riusa **lo stesso oggetto `ItemInstance`** (è ciò che fa funzionare gli spostamenti dentro lo stesso contenitore). Altrimenti l'item viene serializzato e ricostruito con id freschi: dal modello (`_nextInstanceId`) se va al giocatore, da `ItemTree.MaxId + 1` se va in un contenitore nel mondo.

> **Gli id di un payload nel mondo sono unici solo dentro quel payload.** Non confrontarli mai con gli `InstanceId` di un giocatore.

### Il mondo si muta solo in `Commit()`
`WorldSession` accumula `_dirty`/`_removed`/`_toSpawn` e li applica solo alla fine. L'estrazione `WorldLoose` ha **rollback nullo** ed è sicura *solo* grazie a questo. Qualunque modifica futura che tocchi `GameWorld` prima di `Commit()` rompe l'atomicità.

---

## 4. `InventoryGrid`

Due strutture parallele: `List<ItemInstance> _items` e `int[] _cells` che contiene l'`InstanceId` occupante (`0` = libera). La collisione è O(area), non O(n²).

- `Place` chiama `CanPlace` **senza** ignore-id: piazzare un item già presente fallisce, va prima rimosso.
- `TryAutoPlace`: passata **non ruotata prima, poi ruotata**; ogni passata scorre `y` esterno, `x` interno (prima riga, da sinistra). Salta la passata ruotata se `Width == Height`.
- `Contains` guarda **solo il livello superiore** di questa griglia, non ricorre nei container annidati.

### Non esiste il merge degli stack
`ItemDefinition.MaxStack` è esportato e valorizzato nei `.tres` (`ammo` = 60) ma **non è letto da nessuna riga del progetto**. Due pile di munizioni nella stessa griglia restano separate; non c'è né merge né split. `StackCount` cambia solo via `TryPickup`, `RequestUse`, `RequestUnpack`, `ConsumeById`.

È la lacuna più grande e non è segnalata da alcun commento nel codice. Implementarla significa toccare `InventoryGrid.Place` e `ItemTransfer.Insert`.

---

## 5. Oggetti nel mondo

`ItemPickup` è **puramente visivo**: non possiede stato né logica.

- `Uid` (host-allocato) è l'**identità cross-peer**. I nomi dei nodi non lo sono: valgono `$"pk_{uid}_{serial}"`, col seriale apposta perché un `ReplacePickupPayload` non collida col nodo vecchio ancora in coda di distruzione.
- `Payload` è l'albero serializzato completo, impostato dalla spawn function **su ogni peer**: i client disegnano il contenuto di uno zaino a terra senza alcuna RPC in più.
- `Anchored` = contenitore fisso (cassa, e in futuro i cadaveri): F lo **apre** invece di raccoglierlo. Preservato attraverso `ReplacePickupPayload`.
- Gruppo `"world_item"` (`ItemPickup.GroupName`) per la ricerca lato UI; l'host usa `GameWorld.FindPickup(uid)`.

> **Cerca sempre per `Uid`, saltando `IsQueuedForDeletion()`.** Durante un replace il nodo vecchio con lo stesso uid può restare nell'albero per un frame.

`ReplacePickupPayload` è un despawn + respawn con **stesso uid, stessa posizione, stesso `Anchored`**: la spawn-data del `MultiplayerSpawner` resta l'unica fonte di verità, anche per chi entra a partita iniziata. Non aggiungere un canale laterale per i payload.

---

## 6. Ciclo di vita e riferimenti

`PlayerInventoryModel.Deserialize` **ricostruisce da zero** equipaggiamento e tasche a ogni `SyncFullState`: gli oggetti `InventoryGrid` e `ItemInstance` vengono sostituiti, non aggiornati.

> **Nulla può tenere un riferimento a lungo termine** a una griglia o a un'istanza attraverso un sync.

È il motivo per cui `ContainerWindow` ri-risolve la propria griglia dall'`ItemAddress` a ogni `Refresh()` (e si auto-distrugge se l'indirizzo non risolve più: saccheggiato o fuori portata), e per cui `InventoryScreen.Rebuild()` ricrea tutte le view.

---

## 7. Drag & drop

Lo stato è diviso di proposito: **il payload lo possiede il viewport di Godot** (valore di ritorno di `_GetDragData`, leggibile ovunque con `GuiGetDragData()`), **la presentazione la possiede `InventoryScreen`** (`DraggedDefinition`, `PendingRotated`).

Il drop diventa una riga sola:
```csharp
_screen.Inventory.SubmitMove(payload.From, payload.InstanceId, _address, x, y, _screen.PendingRotated);
```

Hit-test in `GridPanelView.CellAt`: `(int)(pos.X / CellSize)` clampato. La cella sotto il cursore diventa **l'angolo in alto a sinistra** dell'item — non c'è compensazione dell'offset di presa, ed è per questo che l'anteprima è centrata sul cursore.

> **`ItemView` deve restare `MouseFilter.Ignore`**, altrimenti i drop su celle occupate non raggiungono mai la `GridPanelView` sottostante.

`InventoryScreen._Process` aggiorna la colonna "a terra" ogni 0.4 s ma **salta l'aggiornamento durante un drag**: ricostruire distruggerebbe la sorgente del trascinamento.

---

## 8. Stabilità della serializzazione

Valori interi che viaggiano sulla rete **e** stanno dentro i `.tres`: `EquipSlotType`, `ItemAddress.RealmType`, `ItemCategory`. **Si appende in fondo, non si riordina né si rinumera mai.** (`SecureContainer = 10` è stato aggiunto in coda proprio per questo.)

Le chiavi del formato serializzato — `id`, `item`, `x`, `y`, `rot`, `stack`, `grid`/`cols`/`rows`/`items` — sono condivise da tre consumatori: il sync completo, i payload del mondo e il rehoming fra spazi di id. Cambiarne una le rompe tutte e tre insieme.

---

## 9. Altri invarianti

- **Il client non muta mai il modello.** Le uniche scritture lato client sono `Deserialize` dentro `SyncFullState` e alberi usa-e-getta costruiti per il solo disegno.
- `ValidateSender()` in ogni `Request*`: autorità + (`sender == 0` chiamata locale dell'host, oppure `sender == _ownerPeerId`). `_ownerPeerId` è ricavato da `GetParent().Name`: **il nome del nodo Player deve restare la stringa del peer id**.
- `PushState()` emette anche `HostStateChanged` lato host. `WeaponController` ci si aggancia per ricalcolare le munizioni di riserva e rivalidare l'arma impugnata. **Ogni nuovo percorso di mutazione host-side deve chiamare `HostPushState()`**, come fa la ricarica.
- Gli `InstanceId` sono host-allocati e strettamente positivi (partono da 1) — è ciò che permette a `-1` di indicare le tasche.
- **Anti-ciclo**: uno zaino non può finire dentro sé stesso o un proprio discendente. Applicato in `TryMove`, `TryStoreInstanceAt`, `TryAutoStore` e in entrambi i rami griglia di `ItemTransfer.Insert`. Non serve sull'equip.
- **Peso** (`MaxLoad = 40 kg`) imposto in esattamente 4 punti: `TryPickup`, `TryStoreInstance`, `TryStoreInstanceAt`, `FitsLoad`. Non imposto per contenitori nel mondo né per terra. Il conto usa `ItemInstance.TotalWeight()`, quindi uno zaino pieno pesa col suo contenuto.
- **Auto-stow**: ordine fisso zaino → gilet → tasche; ma `TryPickup`/`TryStoreInstance` preferiscono l'auto-**equip** se lo slot corrispondente è libero.
- **`AutoPlace`**: il sentinella è `PlayerInventory.AutoPlace = -1`, ma il test reale in `ItemTransfer.Insert` è `x < 0`. In quel caso `y` e `rotated` sono ignorati.
- `ConsumeById` rimuove gli stack esauriti **dopo** l'iterazione (mutare una griglia mentre la si scorre invalida l'enumerazione). `CountById`/`ConsumeById` scorrono solo le griglie: l'equipaggiamento è escluso di proposito (l'arma impugnata non è munizione di riserva).
- I quick slot vengono ripuliti in `TryDrop`, `Extract` e in `ConsumeById`, **ma non** in `TryMove`/`TryEquip`/`TryUnequip` (l'item è ancora posseduto). La regola "solo tasche o rig" è verificata all'assegnazione e mai rivalidata dopo.

---

## 10. Non implementato di proposito

- **Merge/split degli stack** (vedi §4) — la lacuna più rilevante.
- **Uso effettivo dei quick slot**: `Hotbar.SelectSlot` evidenzia soltanto. `RequestUse` decrementa lo stack ma non ha alcun effetto di gioco.
- **Voci del menu contestuale legate alle armi** (scarica caricatore, svuota munizioni, piega calcio): disabilitate perché il caricatore vive come stato host-side in `WeaponController`, non come item di griglia (vedi skill `combat-shooting`).
- **Moduli e durabilità** in `InspectWindow`: sezione presente, corpo vuoto.
- **Nessuno swap diretto** sull'equipaggiamento: uno slot occupato rifiuta il drop.
- **Cadaveri**: previsti riusando `Anchored` + `GameWorld.SpawnWorldContainer`.
- Nessun test automatico, nonostante il modello sia scritto apposta per essere puro C# e testabile.
