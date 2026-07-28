using Godot;

namespace Lagoon;

/// <summary>
/// Definizione di un'arma: estende <see cref="ItemDefinition"/> con gli attributi balistici. Come la
/// classe base e' dato puro senza side-effect (CLAUDE.md §4/§5): descrive l'arma, non la usa.
///
/// E' una sottoclasse e non un blocco di campi opzionali su <see cref="ItemDefinition"/> perche' un
/// medkit non ha un rateo di fuoco. L'<see cref="ItemDatabase"/> non richiede modifiche: carica con
/// <c>ResourceLoader.Load&lt;ItemDefinition&gt;</c>, che restituisce gia' l'istanza derivata; i
/// consumatori fanno <c>def as WeaponDefinition</c>.
///
/// Fase 3 = hitscan puro: <see cref="ProjectileSpeed"/>, <see cref="Penetration"/> e
/// <see cref="Caliber"/> sono dichiarati ma non ancora letti da nessuno — servono a fissare la
/// superficie degli attributi ora, cosi' i <c>.tres</c> non vanno rifatti quando arriveranno i
/// proiettili fisici.
/// </summary>
[GlobalClass]
public partial class WeaponDefinition : ItemDefinition
{
    // ====================================================================================
    //  Danno
    // ====================================================================================

    /// Danno pieno per colpo a bruciapelo (prima del falloff e del moltiplicatore di hitbox).
    [Export] public int Damage { get; set; } = 25;

    /// Distanza oltre la quale il danno inizia a calare linearmente verso <see cref="MinDamageFactor"/>.
    [Export] public float FalloffStartMeters { get; set; } = 18f;

    /// Frazione minima di danno a <see cref="MaxRangeMeters"/> (0.4 = 40% del danno pieno).
    [Export] public float MinDamageFactor { get; set; } = 0.4f;

    /// RISERVATO (non ancora usato): penetrazione delle armature.
    [Export] public int Penetration { get; set; }

    /// RISERVATO (non ancora usato): calibro, per legare arma e munizione compatibile.
    [Export] public string Caliber { get; set; } = "";

    // ====================================================================================
    //  Portata
    // ====================================================================================

    /// Distanza di riferimento per la precisione: mirando piu' lontano la dispersione cresce.
    [Export] public float EffectiveRangeMeters { get; set; } = 30f;

    /// Distanza massima del raggio: oltre, il colpo non esiste.
    [Export] public float MaxRangeMeters { get; set; } = 60f;

    // ====================================================================================
    //  Precisione
    // ====================================================================================

    /// Dispersione minima, sempre presente anche a bruciapelo e ad arma ferma.
    [Export] public float BaseSpreadDegrees { get; set; } = 0.6f;

    /// Dispersione aggiuntiva a piena <see cref="EffectiveRangeMeters"/>: e' il "piu' miri distante,
    /// piu' l'arma e' imprecisa" — il colpo puo' deviare dal centro del reticolo.
    [Export] public float SpreadPerRangeDegrees { get; set; } = 2.5f;

    /// Tetto assoluto della dispersione, distanza e rinculo inclusi.
    [Export] public float MaxSpreadDegrees { get; set; } = 8f;

    // ====================================================================================
    //  Rinculo
    // ====================================================================================

    /// Dispersione accumulata a ogni colpo (il reticolo "fiorisce" sparando in automatico).
    [Export] public float RecoilPerShotDegrees { get; set; } = 0.7f;

    /// Velocita' di riassorbimento del rinculo, in gradi al secondo.
    [Export] public float RecoilRecoveryDegreesPerSecond { get; set; } = 4f;

    /// Tetto del solo contributo di rinculo.
    [Export] public float MaxRecoilSpreadDegrees { get; set; } = 5f;

    /// Ampiezza (in metri) della scossa locale della camera a ogni colpo. Puramente estetico.
    [Export] public float CameraKick { get; set; } = 0.06f;

    // ====================================================================================
    //  Cadenza e munizioni
    // ====================================================================================

    [Export] public float RoundsPerMinute { get; set; } = 600f;

    /// True = fuoco continuo tenendo premuto; false = un colpo per pressione.
    [Export] public bool Automatic { get; set; } = true;

    [Export] public int MagazineSize { get; set; } = 30;

    [Export] public float ReloadSeconds { get; set; } = 2.2f;

    /// <see cref="ItemDefinition.ItemId"/> della munizione consumata dalla ricarica.
    [Export] public string AmmoItemId { get; set; } = "ammo";

    /// RISERVATO (non ancora usato): velocita' del proiettile in m/s, per quando il tiro passera' da
    /// hitscan a proiettile simulato.
    [Export] public float ProjectileSpeed { get; set; } = 400f;

    // ====================================================================================
    //  Animazione
    // ====================================================================================

    /// <summary>
    /// Come l'arma va impugnata e animata. E' un riferimento a una <c>Resource</c> condivisa e non un
    /// blocco di campi qui, perche' tutte le armi a due mani impugnano allo stesso modo: un solo
    /// <c>.tres</c> serve l'intera categoria.
    ///
    /// Puo' essere null: un'arma senza set usa la posa disarmata, cioe' la sola locomozione. Il layer
    /// di animazione non deve mai assumere che ci sia.
    /// </summary>
    [Export] public WeaponAnimationSet? AnimationSet { get; set; }

    /// <summary>
    /// Modello 3D dell'arma (una scena, tipicamente un <c>.glb</c> di
    /// <c>assets/models/weapons/</c>), costruito nel FRAME DELLA PRESA: origine
    /// sull'impugnatura, canna lungo +Z. Null = placeholder geometrico di
    /// <see cref="WeaponVisual"/>, cosi' un'arma nuova compare comunque.
    /// </summary>
    [Export] public PackedScene? VisualScene { get; set; }

    // ====================================================================================
    //  Formule condivise
    // ====================================================================================

    /// Intervallo minimo fra due colpi, in millisecondi.
    public float ShotIntervalMsec => RoundsPerMinute <= 0f ? 0f : 60000f / RoundsPerMinute;

    /// <summary>
    /// Dispersione totale (semiangolo del cono di tiro, in gradi) per una data distanza di mira e un
    /// dato rinculo accumulato. UNICA fonte di verita' della formula: la usa l'host per tirare il
    /// dado e il reticolo per disegnare l'anello, cosi' quello che il giocatore vede corrisponde a
    /// quello che l'host calcola.
    /// </summary>
    public float SpreadDegrees(float aimDistance, float recoilDegrees)
    {
        float t = EffectiveRangeMeters <= 0f
            ? 0f
            : Mathf.Clamp(aimDistance / EffectiveRangeMeters, 0f, 1f);

        float spread = BaseSpreadDegrees + SpreadPerRangeDegrees * t + recoilDegrees;
        return Mathf.Min(spread, MaxSpreadDegrees);
    }

    /// Fattore di danno (0..1) alla distanza data: pieno fino a <see cref="FalloffStartMeters"/>,
    /// poi calo lineare fino a <see cref="MinDamageFactor"/> a <see cref="MaxRangeMeters"/>.
    public float DamageFactorAt(float distance)
    {
        if (distance <= FalloffStartMeters || MaxRangeMeters <= FalloffStartMeters)
            return 1f;

        float t = Mathf.Clamp(
            (distance - FalloffStartMeters) / (MaxRangeMeters - FalloffStartMeters), 0f, 1f);
        return Mathf.Lerp(1f, MinDamageFactor, t);
    }
}
