# Asset di terze parti in `assets/models/`

| Asset | File nel progetto | Autore | Licenza |
|---|---|---|---|
| Universal Animation Library [Standard] | `characters/Mannequin.glb` (solo sorgente di scheletro e clip), clip dentro `characters/Character_Base.glb` | Quaternius — https://quaternius.com | CC0 1.0 Universal (pubblico dominio) |
| Corpo base (uomo 1,80 m) | `characters/Character_Base.glb`, `characters/Character_Base_FP.glb` | Progetto (`tools/blender/build_character_base.py`) da asset MakeHuman `makehuman_system_assets` (proxy `male_generic`, pelle `young_caucasian_male`, occhi low-poly) generati con MPFB2 | Asset MakeHuman CC0 1.0; MPFB2 (GPL-3) e' solo lo strumento, non e' nel gioco |
| Zaino d'assalto militare (`backpack`) | `wearables/backpack.glb`, `items/backpack.glb` | modello sorgente `military-tactical-backpack.blend` fornito dall'utente, convertito da `tools/blender/ingest/ingest.py` (`ingest/manifests/backpack.json`) | **DA INDICARE**: autore e licenza del sorgente |
| Capi indossabili | `wearables/`, `wearables/fp/`, `items/<capo>.glb` | Progetto (`tools/blender/wearables/build_wearable.py`, ricette in `tools/blender/wearables/recipes/`) | Originale, nessun asset di terzi |
| Retro Weapon Pack V1 | `weapons/VM_*.glb`, `weapons/W_*.glb` | kuptchi | Uso libero in progetti personali e commerciali, attribuzione non richiesta (dal `Readme.pdf` del pack) |
| Kit dei palazzi veneziani (pulito e rovina) | `buildings/venice/`, `buildings/venice_ruin/`, `buildings/materials/` | Progetto (generato da `tools/blender/build_venice_kit.py`) | Originale, nessun asset di terzi |
| Barchino da laguna, fuoribordo, consolle, accessori | `boats/` (tranne il vaporetto) | Progetto (generato da `tools/blender/build_lagoon_boats.py`) | Originale, nessun asset di terzi, nessun marchio |
| Vaporetto (sovrastruttura) | `boats/Vaporetto.glb`, `boats/Vaporetto_Worn.glb` | This work is based on "2010 Vaporetto" (https://sketchfab.com/3d-models/2010-vaporetto-71b8ec3054e14f62887d6da7ac1f5789) by r_ka (https://sketchfab.com/ruslan.kindruk) licensed under CC-BY-4.0 (http://creativecommons.org/licenses/by/4.0/) | **CC-BY-4.0: l'attribuzione va riportata nei crediti del gioco.** Modificato da `tools/blender/build_vaporetto.py`: riscalato, scafo, ponti, timoneria, consolle, cancelli e texture rifatti; logo, targhe e cartelli dell'esercente rimossi |
| Consolle del vaporetto | `boats/Console_Vaporetto.glb`, `boats/Console_Vaporetto_Worn.glb` | Progetto (generata da `tools/blender/build_vaporetto.py`) | Originale |

I pack originali non sono nel repository: i file qui sopra sono copie o conversioni
(`tools/blender/build_weapon_viewmodels.py`, vedi la skill `blender-pipeline`). Lo stesso vale per il
modello sorgente del vaporetto (`scene.gltf` di Sketchfab): si riscarica e se ne passa il percorso a
`build_vaporetto.py`.
