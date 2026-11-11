---
name: inventory-tarkov
description: Modifica inventario a griglia, equipaggiamento, contenitori annidati, loot, pickup, drag and drop, peso e hotbar.
---

# Inventario a griglia

## Contratti

- Il modello è host-authoritative; la UI locale invia richieste e non muta lo stato.
- `ItemTransfer.Execute` è il collo di bottiglia per gli spostamenti fra `ItemAddress`: conserva extract → rehome → insert e commit atomico del mondo.
- Un fallimento può eseguire auto-stow nel rollback; `PlayerInventory.RequestMove` deve quindi pubblicare lo stato anche quando `Execute` restituisce `false`.
- `ItemAddress` e gli enum attraversano rete/risorse come interi. Non cambiare valori esistenti. Gli id sono positivi e unici nel proprio spazio, non globali fra payload mondo e player.
- `PlayerInventoryModel.Deserialize` sostituisce griglie e istanze: UI e finestre devono risolvere di nuovo gli indirizzi, non conservare riferimenti lunghi.
- Il mondo si identifica con `ItemPickup.Uid`; ignora nodi queued-for-deletion durante un replace. `Anchored` apre un contenitore e ne impedisce il pickup.
- Anti-ciclo e peso devono restare centralizzati. Un nuovo percorso di mutazione host chiama `HostPushState`/`PushState`.

## Limiti attuali

Non esistono merge/split stack, swap equip diretto, effetti reali dei quick slot, durabilità o cadaveri. Implementali solo se richiesti, estendendo il modello e la rete insieme.

Per UI o item carica anche `ui-hud` e `i18n-localization`. Verifica build e, per mutazioni, host + client con pickup, drop, container annidati, rollback e late join.
