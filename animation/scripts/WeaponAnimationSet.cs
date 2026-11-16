using Godot;

namespace Lagoon;

/// <summary>
/// Come un'arma si VEDE e si anima: modelli, tempi e rinculo visivo. Dato puro senza side-effect,
/// come <see cref="ItemDefinition"/> e <see cref="WeaponDefinition"/> (CLAUDE.md §5).
///
/// Sta in una <c>Resource</c> a se' e non su <see cref="WeaponDefinition"/> perche' quella descrive
/// il GIOCO (danno, dispersione, rinculo della mira: cio' che l'host calcola), questa la RESA
/// (cio' che ogni peer disegna). Nulla di cio' che sta qui influisce sull'esito di un colpo.
///
/// I due modelli escono da <c>tools/blender/build_weapon_viewmodels.py</c> e condividono le
/// convenzioni: canna lungo -Z, clip con nomi uniformi. Aggiungere un'arma = un <c>.tres</c> di
/// questo tipo, senza toccare alberi di animazione ne' codice.
/// </summary>
[GlobalClass]
public partial class WeaponAnimationSet : Resource
{
    // ====================================================================================
    //  Modelli
    // ====================================================================================

    /// <summary>
    /// Viewmodel in prima persona (<c>VM_*.glb</c>): braccia + arma con le clip <c>idle</c>,
    /// <c>walk</c>, <c>run</c>, <c>idle_aim</c>, <c>fire</c>, <c>reload</c>, <c>draw</c>...
    /// Lo vede solo il giocatore locale.
    /// </summary>
    [Export] public PackedScene? ViewmodelScene { get; set; }

    /// <summary>
    /// Arma per la terza persona (<c>W_*.glb</c>): mesh statica con i marcatori <c>Muzzle</c>,
    /// <c>EyeHip</c>, <c>EyeAim</c>. La vedono gli altri giocatori, e fa l'ombra dell'arma del
    /// giocatore locale. La presa delle mani NON sta qui: si misura sul viewmodel
    /// (<see cref="ViewmodelHandPose"/>).
    /// </summary>
    [Export] public PackedScene? WorldScene { get; set; }

    // ====================================================================================
    //  Mira (ADS)
    // ====================================================================================

    /// Campo visivo verticale mentre si mira, in gradi.
    [Export] public float AimFovDegrees { get; set; } = 55.0f;

    /// Tempo per portare l'arma all'occhio (e per riabbassarla), in secondi.
    [Export] public float AimSeconds { get; set; } = 0.18f;

    /// <summary>
    /// Correzione del viewmodel IN MIRA: di quanto ruota verso l'alto attorno alla camera, in
    /// gradi. La posa di mira del pack tiene la punta del mirino poco sotto l'asse della camera;
    /// i colpi partono lungo quell'asse, quindi qui la si porta esattamente al centro dello
    /// schermo. E' una ROTAZIONE attorno all'occhio e non una traslazione apposta: sposta tutta
    /// l'immagine dell'arma della stessa quantita', mentre alzare l'arma muoverebbe la tacca di
    /// mira (vicina) molto piu' del mirino (lontano) e romperebbe l'allineamento.
    /// Si misura su uno screenshot: scarto in pixel / (meta' altezza) * tan(AimFov / 2).
    /// </summary>
    [Export] public float AimPitchDegrees { get; set; }

    // ====================================================================================
    //  Rinculo VISIVO dell'arma (il rinculo della mira sta in WeaponDefinition)
    // ====================================================================================

    /// Arretramento dell'arma a ogni colpo, in metri.
    [Export] public float KickBack { get; set; } = 0.035f;

    /// Impennata dell'arma a ogni colpo, in gradi.
    [Export] public float KickPitchDegrees { get; set; } = 2.5f;

    /// Rollio casuale dell'arma a ogni colpo, in gradi (+/-).
    [Export] public float KickRollDegrees { get; set; } = 1.5f;

    /// Quanto del rinculo visivo resta mirando (l'arma e' appoggiata alla spalla).
    [Export] public float AimKickFactor { get; set; } = 0.45f;

    /// Rigidezza della molla che riporta l'arma in posizione, in 1/s^2.
    [Export] public float KickStiffness { get; set; } = 260.0f;

    /// Smorzamento della stessa molla, in 1/s.
    [Export] public float KickDamping { get; set; } = 22.0f;
}
