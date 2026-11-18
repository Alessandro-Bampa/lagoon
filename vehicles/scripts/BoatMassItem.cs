using Godot;

namespace Lagoon;

/// <summary>
/// Una massa fissa a bordo (solo dati): zavorra, serbatoio, stiva, sovrastruttura, attrezzatura.
/// Posizione nello spazio del modello dello scafo (come gli attacchi <c>Mount_*</c>); la scatola
/// <see cref="Size"/> ne da' l'inerzia propria (zero = massa puntiforme).
/// </summary>
[GlobalClass, Tool]
public partial class BoatMassItem : Resource
{
    [Export] public string Label { get; set; } = "";
    [Export] public float Mass { get; set; } = 100f;
    [Export] public Vector3 Position { get; set; }
    [Export] public Vector3 Size { get; set; }
}
