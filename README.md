# Lagoon

RPG open world cooperativo, visuale isometrica, ambientazione survival/action stile S.T.A.L.K.E.R.
con inventario a griglia stile Escape from Tarkov. Multiplayer cooperativo (fino a 4) con architettura
**Listen Server / Host-Player** su GodotSteam. Vedi [CLAUDE.md](CLAUDE.md) per l'architettura completa.

Stato attuale: **Fase 3 — Shooting** (Fase 1 movimento e Fase 2 inventario concluse).

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

## Come lanciare e testare
Il gioco va verificato in **multi-istanza locale** (CLAUDE.md §6):

1. In editor: `Debug > Run Multiple Instances` → imposta **2** istanze.
2. Avvia il progetto (F5). Si aprono due finestre con il menu di avvio.
3. Due trasporti disponibili dal menu:
   - **Locale (ENet)** — consigliato per il test sullo stesso PC, nessuna dipendenza:
     - Finestra A → **Host (Locale ENet)**.
     - Finestra B → **Join (Locale ENet)** (campo vuoto = `127.0.0.1`).
   - **Steam** — path primario di produzione (richiede Steam attivo + addon installati):
     - Finestra A → **Host (Steam)**: mostra il **Lobby ID**.
     - Finestra B → incolla il Lobby ID nel campo → **Join (Steam)**.

## Comandi

| Cosa | Tasto |
|---|---|
| Movimento | **WASD** / frecce |
| Inventario | **Tab** |
| Interagisci col mondo | **F** (breve = raccogli/apri, tenuto = menu contestuale) |
| Ruota l'oggetto trascinato | **R** *(a inventario aperto)* |
| Impugna arma / rinfodera | **1** primaria · **2** secondaria · **3** pistola |
| Spara | **Mouse sinistro** |
| Ricarica | **R** *(a inventario chiuso)* |
| Slot rapidi consumabili | **4 5 6 7 8 9 0** |
| Scarta l'oggetto sotto il cursore | **Backspace** |
| Menu / impostazioni | **Esc** |

Il tuo avatar è **verde**, gli altri **rossi**. Ogni giocatore parte con fucile e pistola già
equipaggiati e 60 munizioni di riserva.

**Criteri di completamento verificati:**
- *Fase 1* — entrambe le istanze si vedono muovere reciprocamente senza scatti evidenti; la camera
  isometrica segue solo il player locale.
- *Fase 3* — un client **non-host** spara a un manichino del `TestLevel` e l'HP mostrato sopra il
  bersaglio cala **in modo identico su tutte le finestre**; a zero il manichino diventa rosso e
  respawna dopo 6 secondi, di nuovo in sincrono.

## Limiti noti del prototipo (dichiarati, non nascosti)
- **Host migration non implementata**: se l'host chiude, la sessione termina per tutti.
- **Movimento client-authoritative**: ogni peer è autorità del proprio avatar. La validazione
  server-side dell'input è rimandata. Danno e inventario (Fasi 2/3) sono invece pienamente
  server-authoritative.
- **Nessuna lag compensation nel tiro**: l'host ri-traccia il colpo dalla posizione *replicata* del
  tiratore, vecchia fino a ~1 RTT. Un bersaglio che si muove velocemente può essere
  mancato pur sembrando colpito sullo schermo del client.
- **Il tracciante appare ~1 RTT dopo lo sparo**: la dispersione la tira solo l'host, quindi il client
  non può prevedere dove è finito il proprio colpo. Il lampo alla bocca è invece immediato e locale.
- **Fuoco amico attivo**: voluto in questa fase, serve a validare il danno fra peer.
- **Nessuna conseguenza della morte**: a 0 HP un giocatore resta in piedi e continua a giocare. Le
  regole di morte/rianimazione/loot del cadavere arrivano dopo il prototipo.
- **Trasporto ENet locale**: è un fallback di sviluppo per il test multi-istanza, non il trasporto di
  produzione (che è Steam).
