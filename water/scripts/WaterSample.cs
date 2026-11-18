using Godot;

namespace Lagoon;

/// <summary>
/// Risultato di una lettura della superficie in un punto (vedi <see cref="WaterVolume.Sample"/>).
/// </summary>
public readonly record struct WaterSample(
    /// Quota mondo della superficie (marea + onde) sulla verticale del punto.
    float Height,
    /// Normale della superficie.
    Vector3 Normal,
    /// Velocita' delle particelle d'acqua in superficie (orbitale delle onde): per il trascinamento.
    Vector3 Velocity,
    /// Esposizione alle onde 0..1 (vedi <see cref="WaterMap"/>).
    float Exposure);
