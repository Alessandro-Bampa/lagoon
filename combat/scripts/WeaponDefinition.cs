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
/// Il rinculo e' quello di uno sparatutto in prima persona, in tre parti indipendenti: la
/// DISPERSIONE (il cono in cui l'host devia il colpo: stretta in mira, larga dal fianco, in
/// movimento e sparando di fila), il rinculo della VISUALE (locale: la camera sale e scarta, ed e'
/// cio' che si compensa col mouse) e quello VISIVO dell'arma (in <see cref="WeaponAnimationSet"/>).
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

    /// Distanza massima del raggio: oltre, il colpo non esiste.
    [Export] public float MaxRangeMeters { get; set; } = 60f;

    // ====================================================================================
    //  Dispersione: il cono entro cui l'HOST devia il colpo (semiangolo, in gradi)
    // ====================================================================================

    /// Cono sparando dal fianco, da fermi.
    [Export] public float HipSpreadDegrees { get; set; } = 2.2f;

    /// Cono in mira (tasto destro), da fermi: il primo colpo va dove puntano le tacche.
    [Export] public float AimSpreadDegrees { get; set; } = 0.2f;

    /// Cono aggiunto dal movimento, a piena velocita' di corsa; in proporzione sotto.
    [Export] public float MoveSpreadDegrees { get; set; } = 1.8f;

    /// Cono aggiunto mentre si e' in aria.
    [Export] public float AirSpreadDegrees { get; set; } = 3.0f;

    /// Quanto del cono resta da accovacciati.
    [Export] public float CrouchSpreadFactor { get; set; } = 0.75f;

    /// Cono che si accumula a ogni colpo sparando di fila ("bloom").
    [Export] public float BloomPerShotDegrees { get; set; } = 0.35f;

    /// Velocita' con cui il bloom si riassorbe, in gradi al secondo.
    [Export] public float BloomRecoveryDegreesPerSecond { get; set; } = 5f;

    /// Tetto del solo bloom.
    [Export] public float MaxBloomDegrees { get; set; } = 3f;

    /// Quanto del bloom resta in mira.
    [Export] public float AimBloomFactor { get; set; } = 0.35f;

    // ====================================================================================
    //  Rinculo della VISUALE: locale al tiratore, e' cio' che si compensa col mouse
    // ====================================================================================

    /// Di quanto sale la visuale a ogni colpo, in gradi.
    [Export] public float RecoilPitchDegrees { get; set; } = 1.1f;

    /// Scarto laterale casuale a ogni colpo, in gradi (+/-).
    [Export] public float RecoilYawDegrees { get; set; } = 0.45f;

    /// Deriva laterale costante a ogni colpo, in gradi (positiva = verso sinistra): da' alla
    /// raffica una direzione riconoscibile invece di un tremolio simmetrico.
    [Export] public float RecoilYawBiasDegrees { get; set; } = 0.12f;

    /// Rollio casuale della visuale a ogni colpo, in gradi (+/-).
    [Export] public float RecoilRollDegrees { get; set; } = 0.5f;

    /// Quanto del rinculo resta in mira (arma appoggiata alla spalla).
    [Export] public float AimRecoilFactor { get; set; } = 0.7f;

    /// Quanto del rinculo resta da accovacciati.
    [Export] public float CrouchRecoilFactor { get; set; } = 0.8f;

    /// Prontezza con cui la visuale raggiunge il rinculo, in 1/s: piu' alta = piu' secco.
    [Export] public float RecoilSnappiness { get; set; } = 30f;

    /// Velocita' con cui la visuale rientra dopo il colpo, in 1/s.
    [Export] public float RecoilReturnSpeed { get; set; } = 7f;

    // ====================================================================================
    //  Cadenza e munizioni
    // ====================================================================================

    [Export] public float RoundsPerMinute { get; set; } = 600f;

    /// True = fuoco continuo tenendo premuto; false = un colpo per pressione.
    [Export] public bool Automatic { get; set; } = true;

    /// Durata della ricarica: va tenuta uguale alla clip <c>reload</c> del viewmodel.
    [Export] public float ReloadSeconds { get; set; } = 2.2f;

    /// Tempo dopo l'estrazione in cui l'arma non spara ne' ricarica (clip <c>draw</c>).
    [Export] public float DrawSeconds { get; set; } = 0.5f;

    /// RISERVATO (non ancora usato): velocita' del proiettile in m/s, per quando il tiro passera' da
    /// hitscan a proiettile simulato.
    [Export] public float ProjectileSpeed { get; set; } = 400f;

    // ====================================================================================
    //  Animazione
    // ====================================================================================

    /// <summary>
    /// Come l'arma si vede e si anima: viewmodel in prima persona, modello in terza, tempi di
    /// mira, rinculo visivo. Tutto cio' che e' RESA sta li'; qui resta cio' che calcola l'host.
    ///
    /// Puo' essere null: un'arma senza set funziona ma non si vede. Il layer di animazione non
    /// deve mai assumere che ci sia.
    /// </summary>
    [Export] public WeaponAnimationSet? AnimationSet { get; set; }

    // ====================================================================================
    //  Formule condivise
    // ====================================================================================

    /// Intervallo minimo fra due colpi, in millisecondi.
    public float ShotIntervalMsec => RoundsPerMinute <= 0f ? 0f : 60000f / RoundsPerMinute;

    /// <summary>
    /// Dispersione totale (semiangolo del cono di tiro, in gradi). UNICA formula: la usa l'host
    /// per tirare il dado, a partire dal solo stato replicato del tiratore.
    /// </summary>
    /// <param name="aiming">Arma all'occhio.</param>
    /// <param name="crouching">Accovacciato.</param>
    /// <param name="grounded">A terra.</param>
    /// <param name="moveFraction">Velocita' orizzontale come frazione di quella di corsa (0..1).</param>
    /// <param name="bloom">Bloom accumulato dai colpi precedenti, in gradi.</param>
    public float SpreadDegrees(bool aiming, bool crouching, bool grounded, float moveFraction, float bloom)
    {
        float spread = aiming ? AimSpreadDegrees : HipSpreadDegrees;
        spread += MoveSpreadDegrees * Mathf.Clamp(moveFraction, 0f, 1f);
        if (!grounded)
            spread += AirSpreadDegrees;
        if (crouching)
            spread *= CrouchSpreadFactor;

        return spread + bloom * (aiming ? AimBloomFactor : 1f);
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
