---
name: inventory-tarkov
description: Inventario a griglia stile Tarkov. Carica quando tocchi inventory/, oppure si parla di inventario, griglia, tasche, zaino, rig, pouch, equipaggiamento, slot, blocchi fra slot, item, categorie, filtri, peso e ingombro, contenitori annidati, stack, caricatori e munizioni, raccogliere o lasciare oggetti, casse, loot, trascinamento, barra rapida, icone degli oggetti (PlayerInventory, PlayerInventoryModel, InventoryOps, InventoryRules, EquipRules, ItemAddress, ItemCodec, InventoryGrid, ItemDefinition, MagazineDefinition, ItemInstance, ItemDatabase, ItemPickup, InventoryScreen, GridView, SlotView, QuickBar, PlayerHud).
---

# Inventario a griglia

`inventory/scripts/`. Modello C# puro host-autoritativo, strato di rete sottile, UI locale che **non
muta mai nulla**. Sulla rete viaggia solo l'`ItemId`; i `.tres` non contengono testo.

> **Stato (15/11/2026):** riscrittura appena conclusa. Compila e il collaudo headless copre kit
> iniziale, raccolta e apertura di una cassa. **Mai verificati a video**: layout, trascinamento,
> scambi, finestre, ricarica, client ENet. `tools/playtest_inventory.gd` e' il copione da finire.

| Strato | File | Dove gira |
|---|---|---|
| Dati | `ItemDefinition` (+ `data/`: `MagazineDefinition`, `AmmoDefinition`, `ItemGridDef`, `ItemSlotDef`, `ItemFilter`, `InventoryConfig`), `ItemCategory`, `ItemDatabase` | ovunque |
| Istanze | `ItemInstance`, `InventoryGrid`, `PlayerInventoryModel`, `core/ItemCodec` | host; i client una copia in sola lettura |
| Regole | `core/InventoryRules`, `core/EquipRules` — **senza effetti**, condivise host/UI | ovunque |
| Operazioni | `core/InventoryOps`, `core/ItemFactory` | **solo host** |
| Rete | `PlayerInventory` | host, push al **solo proprietario** |
| Mondo | `ItemPickup`, `GameWorld` | spawn host, nodi visivi ovunque |
| UI | `ui/` (`InventoryScreen`, `GridView`, `SlotView`, finestre, `QuickBar`), `PlayerHud` | solo peer locale |

Scelte prese contro la specifica originale, per CLAUDE.md: nessuna libreria `Inventory.Core`
separata, nessun test automatico, categorie come enum ad albero invece che risorse.

## 1. Modello: tutto e' un albero di `ItemInstance`

- Il giocatore ha **solo slot di equipaggiamento** (`EquipSlotType`, i 15 di Tarkov + 4 di
  abbigliamento). Anche le tasche sono un item (categoria `Pockets`, 4 griglie 1x1) indossato nel
  proprio slot e **non movibile**.
- **Abbigliamento** (`Top`, `Legs`, `Footwear`, `Gloves`, slot 20-23, categorie 31-34): solo estetico,
  nessuna griglia, colonna a destra degli slot in `InventoryScreen.RebuildGear`. Il kit iniziale veste
  giacca, pantaloni e stivali.
- Tutto cio' che si vede addosso e' un **`WearableDefinition`** (`Layer`, `CoversRegions`): capi,
  elmetto, cuffie, corpetto, rig, zaino. Li scrive la pipeline dei capi; come si vedono e come si
  replicano (`CharacterAppearance`, ricavato da `HostStateChanged`) e' nella skill `character-wearables`.
- `PlayerInventory.HostGiveItem(itemId)`: solo host, crea e sistema un oggetto come lo raccoglierebbe.
  Per collaudi e strumenti, non e' un intento di rete.
- Un'istanza puo' avere `Grids` (sotto-griglie da `ItemDefinition.GridSpecs`), `SlotItems` (oggi solo
  il pozzetto del caricatore di un'arma) e `Rounds` (pila LIFO di un caricatore: l'ultimo elemento e'
  il prossimo colpo).
- Ogni istanza sa dove si trova: `Parent`, `ParentGrid`, `ParentSlot`, `EquippedIn`. Li scrivono
  **solo** `InventoryGrid.Place/Remove`, `ItemInstance.SetSlot` e `PlayerInventoryModel.SetEquip`.
- **Gli `InstanceId` sono unici in tutta la sessione** (`ItemDatabase.AllocateInstanceId`, solo host):
  un item passa dall'inventario al mondo senza cambiare id. Non esiste piu' il "rehoming".
- Gli slot ammessi derivano dalla **categoria** (`EquipRules.Accepts`), non da un campo del `.tres`.
- Sottoclassi `[GlobalClass]` per le categorie con attributi propri; mai campi opzionali sulla base.

## 2. Indirizzi e spazi

- **Spazio**: `0` = il proprio inventario; `> 0` = `Uid` di un `ItemPickup`.
- **Sorgente** di un'operazione = `(spazio, InstanceId)`. **Destinazione** = `ItemAddress`
  (`Space`, `Owner`, `Type`: `Grid`/`Slot`/`Ground`, `Index`). `Slot` con `Owner = 0` e' uno slot del
  personaggio e `Index` e' un `EquipSlotType`.
- `InventorySpaces` risolve gli spazi per un giocatore: un pickup fuori portata non si risolve. La
  usano sia l'host sia la UI.

## 3. Operazioni: valida, poi muta

`PlayerInventory.Run` e' lo scheletro di ogni `Request*`: `ValidateSender` → `Session` → operazione →
`Commit()` → `PushState()` **sempre**, e `OperationRejected` al proprietario se fallisce.

- `InventoryOps` valida con `InventoryRules` **prima** di toccare qualcosa: un rifiuto non sposta
  nulla. Eccezioni volute, a piu' passi e sempre in stato valido: unione di stack + ricerca spazio
  (`Store`), `UnloadAmmo`.
- **Il mondo si muta solo in `Session.Commit()`** (despawn, `ReplacePickupPayload`, spawn a terra).
  L'host modifica direttamente `ItemPickup.Root`, poi il nodo viene sostituito.
- Intenti: `Move` (cella/slot/terra, `amount` = dividi, slot occupato = scambio), `DropOnItem`
  (priorita': unisci → carica munizioni → monta caricatore → inserisci), `QuickMove`, `QuickEquip`,
  `Pickup`, `LoadAmmo`, `UnloadAmmo`, `Sort`, `SetTag`, `Unpack`, `Use`, `AssignQuickSlot`.
- Ordine dei controlli di `CanPlace`: limiti → sovrapposizione → auto-contenimento → filtro →
  filtro a cascata (`FilterAppliesToDescendants`, il pouch) → annidamento.
- Priorita' di stivaggio per categoria in `InventoryRules.StoragePriority`; pouch e slot speciali non
  sono mai destinazioni automatiche.

## 4. Invarianti

- **Nulla tiene un riferimento a un'istanza attraverso un sync**: sul client `Deserialize` ricostruisce
  tutto. La UI ricorda gli id e ri-risolve (`InventoryScreen.FindAnywhere`, `Revalidate`).
- **Enum serializzati** (`EquipSlotType`, `ItemCategory`, `InvError`, `ItemSlotKind`,
  `ItemAddress.Kind`): si appende in coda. I valori 3 e 4 di `EquipSlotType` sono ritirati; 7 e 9 li
  usa `tools/playtest_fps.gd`.
- Ogni mutazione host-side finisce in `HostPushState()`, che emette `HostStateChanged`: ci si aggancia
  `WeaponController` per caricatore, riserva e validita' dell'arma impugnata.
- **Sparare non fa push dell'inventario**: il conteggio sulla tile si riallinea al push successivo
  (`PlayerHud` chiede lo stato a ogni apertura).
- `_ownerPeerId` viene da `GetParent().Name`: il nodo Player si chiama come il peer id.
- Config in `resources/inventory_config.tres` (`InventoryConfig.Current`): nessun numero nel codice.

## 5. Oggetti nel mondo

`ItemPickup` e' solo visivo; identita' = `Uid` host-allocato (**cerca per `Uid` saltando
`IsQueuedForDeletion()`**). `Payload` = albero completo; `Root` lo deserializza una volta. `Anchored` =
cassa: F la apre. Il modello a terra e' `assets/models/items/<ItemId>.glb` se esiste, altrimenti un cubo:
si mostra alla scala **nativa** del file, per `ItemDefinition.WorldModelScale` (default 1).

## 6. UI

- **Niente drag & drop integrato di Godot**: `InventoryScreen` e' il controllore. Le viste si
  registrano come `IDropTarget`; una finestra copre i bersagli dietro di se'.
- `GridView` disegna celle e tile in un solo `_Draw` (nessun nodo per tile). La vista "a terra" e' una
  griglia virtuale impacchettata con **proxy**, mai con le radici vere.
- L'anteprima verde/rossa chiama le stesse `InventoryRules` dell'host.
- `InvTheme.Cell` si ricalcola sullo spazio disponibile (massimo `CellPx`): tutte le misure ne derivano.
- Esc a inventario aperto e' di `PlayerHud` (`GameManager.InventoryOpen` fa astenere `PauseMenu`).
- Errori: `InvError` → chiave `UI_INV_ERR_<NOME>`. Slot dell'oggetto: `UI_INV_ITEMSLOT_<ID>`.

## 7. Aggiungere un oggetto

`.tres` in `resources/items/` + righe `ITEM_<ID>_NAME` (e `_SHORT`, `_DESC`) in `locales/items.csv` +
eventuale `.glb` in `assets/models/items/`. Icona: `tools/bake_item_icons.gd` (**non** headless)
fotografa il modello in `assets/textures/items/<ItemId>.png`; senza icona la tile mostra un segnaposto.

## 8. Non implementato

Modding delle armi (dimensione dinamica, piegatura), perquisizione, esame, Found in Raid, pin/lock,
azioni a tempo, stash fuori raid, effetto dei consumabili (`Use` decrementa e basta), audio, cadaveri.
La penalita' di movimento da sovraccarico e' calcolata (`PlayerInventoryModel.SpeedFactorFor`) ma
**non collegata** a `CharacterMotor`. Non c'e' un limite rigido di peso.
