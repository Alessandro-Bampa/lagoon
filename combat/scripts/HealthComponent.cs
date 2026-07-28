using Godot;

namespace Lagoon;

/// <summary>
/// Salute di un'entita' danneggiabile (giocatore, manichino, futuri nemici). Segue CLAUDE.md §3:
/// l'autorita' e' SEMPRE l'host, chiunque sia il proprietario dell'avatar.
///
/// Differenza importante rispetto a <see cref="PlayerInventory"/>: l'inventario fa push del proprio
/// stato al SOLO proprietario, perche' il contenuto delle tasche altrui non riguarda gli altri
/// giocatori. La salute invece deve essere coerente per TUTTI i peer (barre HP, feedback dei colpi,
/// criterio di completamento della Fase 3), quindi viene replicata da un
/// <c>MultiplayerSynchronizer</c> figlio con visibilita' pubblica di default.
///
/// Il synchronizer deve essere FIGLIO di questo nodo, non un fratello sotto la root del Player:
/// <see cref="PlayerController._EnterTree"/> marchia ricorsivamente tutto il sottoalbero col peer
/// proprietario, e il <c>SetMultiplayerAuthority</c> qui sotto (anch'esso ricorsivo) e' cio' che
/// riporta sia questo nodo sia il suo <c>Sync</c> sull'host.
///
/// <see cref="ApplyDamage"/> NON e' una RPC di proposito: il pattern <c>RequestHit</c> del §3 e'
/// realizzato a monte da <see cref="WeaponController.RequestFire"/>, dove il client invia l'INTENTO
/// (il punto di mira) e l'host ricalcola il colpo. Una RPC che accetti direttamente un ammontare di
/// danno lascerebbe al client dettare il risultato, che e' esattamente cio' che il §3 vieta.
/// </summary>
public partial class HealthComponent : Node
{
    /// Emesso su tutti i peer quando la salute replicata cala (per feedback visivo).
    [Signal]
    public delegate void HealthChangedEventHandler(int current, int max);

    /// Emesso SOLO sull'host, nel momento in cui il danno viene applicato.
    [Signal]
    public delegate void DamagedEventHandler(int amount, int attackerPeerId);

    /// Emesso SOLO sull'host, quando la salute raggiunge zero.
    [Signal]
    public delegate void DiedEventHandler(int attackerPeerId);

    /// <summary>
    /// Emesso su TUTTI i peer quando l'entita' incassa un colpo, con la direzione di VOLO del
    /// proiettile in coordinate mondo. E' l'evento estetico per la hit reaction animata: nel
    /// payload di rete viaggia SOLO la direzione — mai l'ammontare di danno, che resta un calcolo
    /// host-side propagato dalla salute replicata (CLAUDE.md §3).
    /// </summary>
    [Signal]
    public delegate void HitReactionEventHandler(Vector3 worldDirection);

    // Stato replicato dal MultiplayerSynchronizer figlio (vedi le .tscn che montano il componente).
    [Export] public int MaxHealth { get; set; } = 100;
    [Export] public int CurrentHealth { get; set; } = 100;
    [Export] public bool IsDead { get; set; }

    /// Ultimo valore osservato in locale, per emettere <see cref="HealthChanged"/> anche sui peer
    /// remoti (che ricevono la proprieta' replicata senza alcuna callback).
    private int _lastSeenHealth = -1;

    public override void _EnterTree()
    {
        // Sovrascrive il set ricorsivo per-peer fatto da PlayerController._EnterTree: la salute e'
        // autoritativa lato host. Ricorsivo, quindi copre anche il Sync figlio.
        SetMultiplayerAuthority(NetworkConstants.HostPeerId);
    }

    public override void _Ready()
    {
        _lastSeenHealth = CurrentHealth;
    }

    public override void _Process(double delta)
    {
        // Polling della proprieta' replicata: sui peer remoti non esiste un "setter callback".
        if (CurrentHealth == _lastSeenHealth)
            return;

        _lastSeenHealth = CurrentHealth;
        EmitSignal(SignalName.HealthChanged, CurrentHealth, MaxHealth);
    }

    /// <summary>
    /// Applica danno. No-op se questo peer non e' l'host: e' il punto in cui si concentra tutta la
    /// logica di danno del gioco (§3.2).
    /// </summary>
    public void ApplyDamage(int amount, int attackerPeerId, Vector3 hitDirection = default)
    {
        if (!IsMultiplayerAuthority() || IsDead || amount <= 0)
            return;

        CurrentHealth = Mathf.Max(0, CurrentHealth - amount);
        EmitSignal(SignalName.Damaged, amount, attackerPeerId);

        // Estetica del colpo incassato: host -> tutti, sul modello di BroadcastShot. Parte DOPO
        // l'applicazione del danno, cosi' un colpo rifiutato non produce mai una reazione.
        if (hitDirection.LengthSquared() > 0.0001f)
            Rpc(MethodName.BroadcastHitReaction, hitDirection);

        if (CurrentHealth > 0)
            return;

        IsDead = true;
        EmitSignal(SignalName.Died, attackerPeerId);
    }

    /// <summary>
    /// Estetica: host -> tutti (§3.3). Ogni peer riemette l'evento come segnale LOCALE; i bridge di
    /// animazione lo trasformano in <c>CharacterAnimator.TriggerHitReaction</c>. Unreliable di
    /// proposito: un flinch perso non desincronizza nulla, lo stato vero e' la salute replicata.
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true,
        TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    public void BroadcastHitReaction(Vector3 worldDirection) =>
        EmitSignal(SignalName.HitReaction, worldDirection);

    /// Imposta salute e stato di morte lato host (respawn, cure, debug). No-op sui client.
    public void HostSetHealth(int value)
    {
        if (!IsMultiplayerAuthority())
            return;

        CurrentHealth = Mathf.Clamp(value, 0, MaxHealth);
        IsDead = CurrentHealth <= 0;
    }

    /// Riporta l'entita' a piena salute lato host.
    public void HostRevive() => HostSetHealth(MaxHealth);
}
