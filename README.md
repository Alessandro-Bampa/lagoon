# Lagoon

RPG open world cooperativo, visuale isometrica, ambientazione survival/action stile S.T.A.L.K.E.R.
con inventario a griglia stile Escape from Tarkov. Multiplayer cooperativo (fino a 4) con architettura
**Listen Server / Host-Player** su GodotSteam. Vedi [CLAUDE.md](CLAUDE.md) per l'architettura completa.

Stato attuale: **Fase 1 — Movimento** (roadmap in CLAUDE.md §8).

## Requisiti
- **Godot 4.7 .NET** (build con supporto C#, non la standard).
- **.NET SDK 8.0+** (verificato con 9.0.x).
- Per il trasporto Steam: client **Steam in esecuzione** + addon GodotSteam (vedi
  [addons/README-STEAM.md](addons/README-STEAM.md)).

## Setup
1. Installare gli addon GodotSteam seguendo [addons/README-STEAM.md](addons/README-STEAM.md)
   (necessario solo per il trasporto Steam; il trasporto locale ENet funziona senza).
2. Buildare la solution C#:
   ```
   dotnet build Lagoon.sln
   ```
3. Aprire il progetto in Godot 4.7 .NET (importa le scene, ricostruisce le UID).

## Come lanciare e testare la Fase 1
La Fase 1 va verificata in **multi-istanza locale** (CLAUDE.md §9/§10):

1. In editor: `Debug > Run Multiple Instances` → imposta **2** istanze.
2. Avvia il progetto (F5). Si aprono due finestre con il menu di avvio.
3. Due trasporti disponibili dal menu:
   - **Locale (ENet)** — consigliato per il test sullo stesso PC, nessuna dipendenza:
     - Finestra A → **Host (Locale ENet)**.
     - Finestra B → **Join (Locale ENet)** (campo vuoto = `127.0.0.1`).
   - **Steam** — path primario di produzione (richiede Steam attivo + addon installati):
     - Finestra A → **Host (Steam)**: mostra il **Lobby ID**.
     - Finestra B → incolla il Lobby ID nel campo → **Join (Steam)**.
4. Muoviti con **WASD** / frecce. Il tuo avatar è **verde**, gli altri **rossi**.

**Criterio di completamento Fase 1 (§8):** entrambe le istanze si vedono muovere reciprocamente
senza scatti evidenti; la camera isometrica segue solo il player locale.

## Limiti noti del prototipo (dichiarati, non nascosti — CLAUDE.md §12)
- **Host migration non implementata**: se l'host chiude, la sessione termina per tutti.
- **Movimento client-authoritative in Fase 1**: ogni peer è autorità del proprio avatar (coerente con
  §8). La validazione server-side dell'input e la lag-compensation sono rimandate. Danno/inventario
  (Fasi 2/3) restano pienamente server-authoritative.
- **Trasporto ENet locale**: è un fallback di sviluppo per il test multi-istanza, non il trasporto di
  produzione (che è Steam).
