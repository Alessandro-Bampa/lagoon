---
name: vehicles-boats
description: Modifica barche, galleggiamento, timone, passeggeri, piattaforme mobili, ancore locali e replica veicoli.
---

# Veicoli e barche

## Contratti fragili

- `SyncPosition` del player è locale a `SyncAnchorId`; ogni calcolo mondo usa `ResolvedSyncPosition`. Posizione e anchor restano nello stesso replication config.
- Il peer proprietario calcola il movimento del player; la barca è simulata solo dall'host. I client congelano il rigid body e presentano stato replicato con extrapolazione limitata.
- `CarryWithAnchor` applica il delta completo di trasformata. Il ponte non deve aggiungere anche platform velocity: preserva l'esclusione del layer `vehicle_deck` da `platform_floor_layers`.
- Scafo e ponte restano separati: layer 5 `vehicles` per il rigid body, layer 6 `vehicle_deck` per l'`AnimatableBody3D` calpestabile. Il player non deve collidere con lo scafo.
- Rileva lo stato ricevuto dal segnale `MultiplayerSynchronizer.Synchronized`, non confrontando valori. Mantieni il limite di extrapolazione.
- Il pilota è stato host-side. Richieste timone validano distanza/identità; controlli `Unreliable` arrivano a frequenza fissa e scadono dopo timeout.
- Non reparentare `Player` sotto la barca.

## Verifica

In due istanze: passeggero fermo rispetto al ponte, nessuna doppia velocità, timone esclusivo, input azzerato dopo perdita pacchetti/disconnessione e client che non continua a simulare quando l'host tace. Ricontrolla anche mira e tiro dal ponte.
