# Sistema attuale

Leggi questo riferimento solo quando devi mantenere, diagnosticare o migrare l'implementazione esistente.

## Flusso e risorse

`CharacterMotor` pubblica stato → `PlayerAnimationBridge`/`NpcAnimationBridge` → `CharacterAnimator` → `CharacterBlendTree` e rig procedurali.

`CharacterBlendTree.tres` è generato da `tools/build_animation_tree.gd`. Combina:

- locomozione unica walk/run/crouch/air;
- `HoldMask`, posa assoluta filtrata sulle otto ossa delle braccia;
- aim offset additivo a cinque pose;
- fire e hit additivi;
- jump, land e vault/mantle full-body;
- `AimRig`, `FootIkRig`, `WeaponGripRig` e `VaultIkRig` per correzioni geometriche.

Librerie montate dal rig:

| Libreria | Contenuto | Generatore |
|---|---|---|
| default | clip assolute Mixamo/procedurali | Blender/glTF |
| `add` | delta aim/fire/hit | `build_additive_clips.gd` |
| `hold` | pose assolute delle braccia | `build_weapon_poses.gd` |
| `crouch` | crouch con braccia animate | `build_crouch_clips.gd` |

Ordine di rebuild dopo una modifica alle sorgenti: Blender se necessario → reimport Godot → hold → additive → crouch → BlendTree.

## Accoppiamenti da verificare

- I BlendSpace2D richiedono triangoli validi e input dentro il rombo; `CharacterAnimator.ClampToDiamond` protegge il sistema attuale.
- Le clip cicliche richiedono `loop_mode` nel `.import`; il suffisso `_loop` viene rimosso dall'importatore e non va usato come nome stabile.
- Ogni nodo filtrato deve continuare a sincronizzare gli ingressi; oggi il filtro strutturale importante è `HoldMask`.
- `AimYawRangeDeg=60` e `AimPitchRangeDeg=45` devono combaciare col builder dei delta finché l'aim space attuale esiste.
- `VaultDuration`, `MantleDuration` e `HardLandingLockSeconds` devono combaciare con clip e motion warping.
- La mano di supporto è un vincolo sull'arma e deve risolversi dopo i modificatori che muovono rachide/braccio; durante parkour cede al `VaultIkRig`.
- Il bacino ha un solo writer che combina atterraggio e foot IK.

## Trappole Godot già confermate

- Dopo la passata, `Skeleton3D` ripristina la posa sorgente: misura l'output dei modifier in `_process_modification`, `skeleton_updated` o tramite `BoneAttachment3D`.
- `TwoBoneIK3D` richiede un `pole_node`; crealo deferred dopo l'ingresso nell'albero.
- L'ordine dei `SkeletonModifier3D` segue i figli dello skeleton: rendilo intenzionale, non dipendente dall'ordine casuale della scena.
- Per delta Godot la neutralità è la rest pose, non `Quaternion.Identity`. I delta attuali vengono generati contro lo skeleton Godot perché il round-trip glTF non preserva in modo affidabile questo contratto.
- Oltre ±90° la decomposizione Euler YXZ può cambiare rappresentazione: misura/ruota direzioni e basis, non sommare pitch alla componente Euler della presa.
- `intersect_ray` non rileva una forma se l'origine è già al suo interno con le opzioni correnti.

## Stato aperto

Mancano morte/ragdoll e clip dedicate di turn-in-place. Esistono clip armate legacy non referenziate nel `.glb`; non ricablarle automaticamente. Il prossimo refactor può sostituire questi compromessi, ma deve aggiornare insieme pipeline, runtime, NPC e test.
