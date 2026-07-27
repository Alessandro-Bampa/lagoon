using Godot;

namespace Lagoon;

/// <summary>
/// Come un'arma va impugnata e animata. Dato puro senza side-effect, come
/// <see cref="ItemDefinition"/> e <see cref="WeaponDefinition"/> (CLAUDE.md §5): descrive la posa,
/// non la applica.
///
/// E' una <c>Resource</c> a se' e non un blocco di campi su <see cref="WeaponDefinition"/> per due
/// ragioni:
///  - piu' armi condividono la STESSA posa (ogni fucile impugna come un fucile), quindi il dato e'
///    riusabile: un solo <c>.tres</c> referenziato da tutte le armi a due mani;
///  - un giorno serviranno pose anche a oggetti che non sono armi (una torcia, un attrezzo), che non
///    hanno danno ne' cadenza di fuoco.
///
/// Aggiungere un'arma nuova NON tocca il layer di locomozione ne' la macchina a stati one-shot: basta
/// un <c>.tres</c> di questo tipo (o riusarne uno esistente) e referenziarlo da
/// <see cref="WeaponDefinition.AnimationSet"/>.
/// </summary>
[GlobalClass]
public partial class WeaponAnimationSet : Resource
{
    // ====================================================================================
    //  Posa upper-body
    // ====================================================================================

    /// <summary>
    /// Nome della clip di posa applicata a busto e braccia dal layer arma. E' un nome TECNICO interno
    /// (mai mostrato all'utente), quindi resta in inglese come i nomi delle clip Mixamo — vedi la
    /// convenzione nella skill <c>blender-pipeline</c>.
    /// </summary>
    [Export] public string HoldPose { get; set; } = "";

    /// Clip one-shot dello sparo, sovrapposta alla locomozione senza interromperla.
    [Export] public string FirePose { get; set; } = "";

    /// <summary>
    /// True per le armi a due mani (fucile): entrambe le mani vanno in IK sull'arma. False per le armi
    /// a una mano (pistola, coltello): solo la mano dominante.
    /// </summary>
    [Export] public bool IsTwoHanded { get; set; } = true;

    // ====================================================================================
    //  Presa (target IK)
    // ====================================================================================

    /// <summary>
    /// Posizione dell'impugnatura rispetto al bone della mano dominante, in metri. E' il bersaglio
    /// dell'IK della mano: se l'arma "scivola" dalla mano, si corregge qui, non nell'animazione.
    /// </summary>
    [Export] public Vector3 GripOffset { get; set; } = Vector3.Zero;

    /// Rotazione dell'impugnatura rispetto al bone della mano dominante, in gradi.
    [Export] public Vector3 GripRotationDegrees { get; set; } = Vector3.Zero;

    /// <summary>
    /// Posizione della mano di supporto sull'arma (calcio/astina), rispetto all'arma stessa. Ignorata
    /// quando <see cref="IsTwoHanded"/> e' false.
    /// </summary>
    [Export] public Vector3 SupportGripOffset { get; set; } = new(0f, 0f, 0.25f);

    // ====================================================================================
    //  Rinculo procedurale
    // ====================================================================================

    /// Arretramento dell'arma a ogni colpo, in metri. Procedurale: non esiste una clip di rinculo.
    [Export] public float RecoilKickBack { get; set; } = 0.04f;

    /// Rotazione verso l'alto dell'arma a ogni colpo, in gradi.
    [Export] public float RecoilKickUpDegrees { get; set; } = 3.0f;

    /// Velocita' di riassorbimento del rinculo procedurale, in frazione al secondo.
    [Export] public float RecoilRecoverySpeed { get; set; } = 10.0f;
}
