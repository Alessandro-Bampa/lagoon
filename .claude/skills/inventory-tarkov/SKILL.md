---
name: inventory-tarkov
description: Inventario a griglia stile Tarkov. Carica quando tocchi inventory/, oppure si parla di inventario, griglia, tasche, zaino, rig, equipaggiamento, slot, item, peso e ingombro, contenitori annidati, raccogliere o lasciare oggetti, casse, loot, drag & drop, hotbar (PlayerInventory, PlayerInventoryModel, ItemAddress, ItemTransfer, InventoryGrid, ItemDefinition, ItemInstance, ItemDatabase, ItemPickup, InventoryScreen, GridPanelView, PlayerHud).
---

# Inventario a griglia

`inventory/`. Modello C# puro host-autoritativo, strato di rete sottile, UI locale che **non muta mai
nulla**. Sulla rete viaggia solo l'`ItemId`; i `.tres` non contengono testo (nomi in `locales/items.csv`,
skill `i18n-localization`).

| Strato | File | Dove gira |
|---|---|---|
| Tipi | `ItemDefinition` (+ sottoclassi), `ItemDatabase` | ovunque |
| Istanze | `ItemInstance`, `InventoryGrid`, `EquipmentSet`, `PlayerInventoryModel` | host; i client una copia in sola lettura |
| Regole | `ItemTransfer`, `ItemTree` | **solo host** |
| Rete | `PlayerInventory` | host, push al **solo proprietario** |
| Mondo | `ItemPickup`, `GameWorld` | spawn host, nodi visivi ovunque |
| UI | `InventoryScreen` e view, `PlayerHud` | solo peer locale |

## 1. `ItemAddress` (3 int: `Realm`, `A`, `B`)

| Realm | A | B |
|---|---|---|
| `PlayerGrid` | `-1` = tasche; positivo = `ContainerGrid` dell'item con quell'`InstanceId` | — |
| `PlayerEquip` | `(int)EquipSlotType` | — |
| `WorldContainer` | `Uid` dell'oggetto nel mondo | `InstanceId` del container nel payload; `0` = radice |
| `WorldLoose` | **destinazione: `0` = a terra ai propri piedi. Sorgente: `Uid` dell'oggetto.** | — |

## 2. `ItemTransfer.Execute` — l'unico collo di bottiglia

Ogni interazione (drag, quick move, equip, scarto, raccolta, saccheggio) è **uno** spostamento fra due
`ItemAddress`. Fasi: **Extract** (con rollback) → **Rehome** (nuovi id se cambia spazio) → **Insert**.

- Extract: `WorldLoose` entro `Reach` e **non `Anchored`**; `WorldContainer` entro `Reach`, mai la radice.
- Insert: `PlayerGrid` peso → griglia → anti-ciclo → piazza (`x < 0` = auto); `PlayerEquip` peso → slot
  del tipo giusto **e vuoto** (nessuno swap); `WorldContainer` senza limite di peso; `WorldLoose` sempre ok.
- **Il rollback è un auto-stow, non un ripristino in cella**: una mossa rifiutata può aver spostato
  l'item. Per questo `RequestMove` chiama `PushState()` **anche quando `Execute` ritorna false**.
- `SpaceKey`: `"player"`, `"world:{uid}"`, `"loose"`. Stesso spazio = stessa `ItemInstance`; altrimenti
  serializza e ricostruisce con id freschi. **Gli id di un payload nel mondo sono unici solo lì dentro.**
- **Il mondo si muta solo in `WorldSession.Commit()`**: è ciò che rende sicuro il rollback nullo di
  `WorldLoose`.

## 3. Invarianti

- **Nulla tiene un riferimento a griglie o istanze attraverso un sync**: `Deserialize` ricostruisce
  tutto. `ContainerWindow` ri-risolve dall'`ItemAddress`, `InventoryScreen.Rebuild()` ricrea le view.
- `ValidateSender()` in ogni `Request*`. `_ownerPeerId` viene da `GetParent().Name`: **il nodo Player si
  chiama come il peer id**.
- Ogni mutazione host-side chiama `HostPushState()`: emette `HostStateChanged`, a cui `WeaponController`
  si aggancia per munizioni e validità dell'arma impugnata.
- **Enum serializzati** (`EquipSlotType`, `RealmType`, `ItemCategory`): si appende in coda, mai riordinare.
  Le chiavi del formato (`id`, `item`, `x`, `y`, `rot`, `stack`, `grid`…) servono sync, payload e rehoming.
- Anti-ciclo: uno zaino non entra in sé stesso o in un discendente. Peso: `MaxLoad = 40 kg`, col
  contenuto; non vale per mondo e terra.
- Auto-stow: zaino → gilet → tasche, ma prima auto-**equip** se lo slot è libero.
- `InventoryGrid`: `Place` fallisce su un item già presente (va rimosso prima); `TryAutoPlace` prova non
  ruotato poi ruotato; `Contains` non ricorre nei contenitori annidati.
- `CountById`/`ConsumeById` scorrono solo le griglie (l'arma impugnata non è riserva) e rimuovono gli
  stack esauriti **dopo** l'iterazione.

## 4. Oggetti nel mondo

`ItemPickup` è solo visivo. Identità = `Uid` host-allocato (i nomi dei nodi hanno un seriale): **cerca
per `Uid` saltando `IsQueuedForDeletion()`**. `Payload` = albero completo, impostato dalla spawn function
su ogni peer. `Anchored` = cassa: F la apre invece di raccoglierla. `ReplacePickupPayload` = despawn +
respawn con stesso uid/posizione/`Anchored`: la spawn-data resta l'unica fonte di verità.

**Interazione in prima persona** (`PlayerHud`): il bersaglio di F è l'oggetto **guardato** — il più
allineato all'asse della camera entro ~20° — fra quelli entro `PickupRange` (3.5 m dal corpo). La portata
è quella che l'host rivalida; lo sguardo sceglie solo *quale*, in locale. Il prompt `Label3D` compare lì.

## 5. UI e drag & drop

- Aprire l'inventario (Tab) libera il cursore: lo gestisce `PlayerHud` (skill `ui-hud` §3). Lo sguardo si
  ferma, WASD no.
- Il payload del drag lo possiede il viewport (`GuiGetDragData()`), la presentazione `InventoryScreen`.
  Drop = `SubmitMove(from, id, to, x, y, rotated)`.
- La cella sotto il cursore è l'**angolo in alto a sinistra** dell'item.
- **`ItemView` resta `MouseFilter.Ignore`**, o i drop su celle occupate non arrivano alla griglia.
- La colonna "a terra" si aggiorna ogni 0.4 s, **non durante un drag**.

## 6. Non implementato di proposito

**Merge/split degli stack** (`MaxStack` è esportato ma nessuno lo legge: la lacuna più grande), effetto
dei quick slot (`RequestUse` decrementa e basta), voci d'arma nel menu contestuale (il caricatore è stato
di `WeaponController`), moduli/durabilità, swap sull'equip, cadaveri (riuseranno `Anchored`), test automatici.
