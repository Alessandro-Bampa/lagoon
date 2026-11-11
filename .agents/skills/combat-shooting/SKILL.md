---
name: combat-shooting
description: Modifica armi hitscan, mira, danno, salute, munizioni, hitbox, effetti di tiro e collision mask in combat/.
---

# Combattimento e tiro

## Autorità

- Il client invia solo `aimPoint` tramite `SubmitFire`/`RequestFire`. L'host ricava origine da `ResolvedSyncPosition`, applica dispersione, raycast, falloff e danno.
- Non accettare dal client bersaglio, origine, danno o esito. `HealthComponent.ApplyDamage` resta un metodo host-side, non una RPC.
- Stato persistente (`CurrentHealth`, caricatore, ricarica, rinculo) è replicato. Tracciante, vampa e flinch sono eventi cosmetici authority, preferibilmente `Unreliable`.
- Fuoco e ricarica devono rispettare `SyncVaulting`; cambiare arma può restare consentito.

## Geometria e dati

- I raycast colpiscono `HitboxComponent`, non il body fisico del player. Escludi la propria hitbox.
- Mantieni separate `CursorMask` e `ShotMask`; barche e coperture edilizie possono fermare i colpi anche quando non sono un target del cursore.
- `WeaponDefinition.SpreadDegrees` è la formula condivisa da host e reticolo; il dado della dispersione si tira solo sull'host.
- `WeaponAnimationSet` descrive posa/presa/recoil. Per cambiare il sistema di animazione consulta `character-animation` e non aggiungere locomozioni per arma.
- Stato attuale: hitscan, caricatore host-side, nessuna morte gameplay, penetrazione o proiettile fisico.

## Verifica

Compila e prova con host + client `LocalEnet`: danno identico su tutti, nessun autocolpo, arma e munizioni replicate, ricarica/fuoco rifiutati nei casi invalidi. Per reazioni, prese o recoil esegui anche la suite runtime animazioni.
