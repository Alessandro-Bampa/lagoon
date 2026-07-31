using Godot;

namespace Lagoon;

/// <summary>
/// Nasconde il personaggio a cui e' agganciato quando l'avatar LOCALE non lo vede. Va montato come
/// figlio del root del personaggio, che deve avere un nodo <c>Visual</c>.
///
/// COSA NON FA, ed e' la parte importante: non tocca la hitbox, ne' il corpo fisico, ne' alcuna
/// proprieta' replicata. Solo <c>Visual.Visible</c>.
///
/// La tentazione di disattivare anche la hitbox ("se non lo vedi non devi poterlo colpire") e'
/// SBAGLIATA in cooperativa: la hitbox e' unica e globale, mentre la visione e' individuale, quindi
/// spegnerla perche' IO non vedo il nemico lo renderebbe incolpibile anche ai miei compagni. E
/// sarebbe comunque una decisione presa sul client a proposito di stato di gioco, cioe' esattamente
/// cio' che CLAUDE.md §3 vieta — e falsificabile da un client modificato.
///
/// Ne discende una conseguenza dichiarata e voluta: si puo' sparare "a memoria" verso l'ultima
/// posizione nota e colpire. E' una scelta di gioco, non una dimenticanza. Se un giorno si volesse
/// il contrario, il posto giusto e' una validazione host-side dentro
/// <c>WeaponController.RequestFire</c>, mai qui.
///
/// Gira su ogni peer con il proprio risultato, senza guardie di autorita': e' interamente locale e
/// cosmetico. Sta sia sugli NPC sia sui GIOCATORI — un compagno fuori dal proprio campo visivo
/// sparisce come qualunque altro personaggio, che e' la conseguenza diretta della visione
/// individuale. L'unica eccezione e' il proprio avatar, che non si nasconde mai a se' stesso.
/// </summary>
public partial class VisibilityGate : Node
{
    /// Spegnibile per il debug di altri sistemi senza smontare il nodo dalla scena.
    [Export] public bool Enabled { get; set; } = true;

    /// <summary>
    /// Ritardo prima di nascondere, in secondi. La comparsa e' invece IMMEDIATA.
    ///
    /// L'asimmetria e' voluta: un nemico che entra nel campo visivo non deve mai apparire in
    /// ritardo, perche' sarebbe un'informazione dovuta e negata; uno che ne esce puo' svanire con
    /// calma. Un'isteresi simmetrica sembrerebbe piu' pulita e penalizzerebbe solo il giocatore.
    /// Serve comunque, perche' senza un bersaglio sul bordo del cono sfarfalla a ogni micro-
    /// movimento della mira.
    /// </summary>
    [Export] public float HideDelay { get; set; } = 0.2f;

    /// Intervallo fra due interrogazioni, in secondi. Non serve la frequenza di frame: e' un
    /// raycast per personaggio, e a 15 Hz il ritardo massimo e' gia' sotto la soglia percettiva.
    [Export] public float QueryInterval { get; set; } = 1f / 15f;

    private Node3D _visual = null!;
    private Node3D _owner = null!;

    /// Esito dell'ultima interrogazione. Aggiornato a <see cref="QueryInterval"/>, consumato ogni frame.
    private bool _seen = true;

    private float _queryTimer;
    private float _hideTimer;
    private bool _shown = true;

    public override void _Ready()
    {
        _owner = GetParent<Node3D>();
        _visual = _owner.GetNode<Node3D>("Visual");

        // Sfasamento iniziale: senza, tutti gli NPC di una scena interrogherebbero nello stesso
        // frame e i raycast si accumulerebbero in un picco periodico invece di distribuirsi.
        _queryTimer = GD.Randf() * QueryInterval;
    }

    public override void _Process(double delta)
    {
        if (!Enabled)
        {
            SetShown(true);
            return;
        }

        _queryTimer -= (float)delta;
        if (_queryTimer <= 0f)
        {
            _queryTimer = QueryInterval;

            VisionSource? vision = VisionRegistry.Local(this);

            // Nessuna sorgente = nessun occultamento. Trattare il null come "non visto" renderebbe
            // il mondo vuoto prima che l'avatar locale esista (menu, caricamento, spawn).
            //
            // E soprattutto: NON ci si nasconde a se' stessi. Il confronto e' con il proprietario
            // della sorgente locale, non con IsMultiplayerAuthority(): sull'host quest'ultima e'
            // vera anche per gli NPC (che sono host-autoritativi), e l'host smetterebbe di
            // nasconderli del tutto.
            _seen = vision == null
                || vision.GetParent() == _owner
                || vision.CanSee(_owner);
        }

        if (_seen)
        {
            // Comparsa immediata, e il conto alla rovescia riparte da capo.
            _hideTimer = 0f;
            SetShown(true);
            return;
        }

        _hideTimer += (float)delta;
        if (_hideTimer >= HideDelay)
            SetShown(false);
    }

    private void SetShown(bool value)
    {
        if (_shown == value)
            return;

        _shown = value;
        _visual.Visible = value;
    }
}
