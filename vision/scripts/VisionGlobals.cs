using Godot;

namespace Lagoon;

/// <summary>
/// Nomi dei <c>global shader parameter</c> con cui il campo visivo raggiunge gli shader. Unico posto
/// in cui questi nomi compaiono lato C#: una stringa sbagliata non produce alcun errore, il valore
/// si perde e basta.
///
/// PERCHE' DELLE GLOBALI E NON UNIFORM DI MATERIALE: i consumatori sono due shader diversi
/// (<c>Shroud.gdshader</c> e <c>WorldSurface.gdshader</c>) su decine di materiali distinti. Con
/// uniform di materiale ci sarebbe una copia del campo visivo per ogni materiale del mondo, da
/// tenere allineate a mano; con le globali c'e' una sola scrittura per frame e una sola fonte di
/// verita'.
///
/// LE DICHIARAZIONI DEVONO STARE IN <c>[shader_globals]</c> DI <c>project.godot</c>. Registrarle da
/// codice con <c>RenderingServer.GlobalShaderParameterAdd</c> NON basta: Godot le risolve da
/// ProjectSettings quando lo shader COMPILA, cioe' prima che qualunque autoload possa intervenire.
/// Una globale mancante fa fallire la compilazione del materiale e le superfici smettono di
/// disegnare del tutto — sintomo muto, salvo il messaggio nei log.
/// </summary>
public static class VisionGlobals
{
    // ====================================================================================
    //  Scritte ogni frame da VisionMask (solo sull'avatar locale)
    // ====================================================================================

    /// Maschera polare del ventaglio: <c>RayCount</c>x1, R32F, r = lunghezza del raggio / <see cref="Range"/>.
    public static readonly StringName Mask = "vision_mask";

    /// Posizione XZ dell'occhio in coordinate mondo: il centro della maschera polare.
    public static readonly StringName Origin = "vision_origin";

    /// Metri corrispondenti a r = 1.0 nella maschera.
    public static readonly StringName Range = "vision_range";

    /// Quota mondo dei piedi dell'osservatore. La usa lo shroud per il ramo "cielo", dove non c'e'
    /// geometria su cui ricostruire una posizione.
    public static readonly StringName Ground = "vision_ground";
}
