using Godot;

namespace Lagoon;

/// <summary>
/// Livrea di una barca: un colore per ogni materiale "per nome" dei modelli generati da
/// <c>tools/blender/build_lagoon_boats.py</c> (Hull, Stripe, Deck, Interior, Cushion, Motor,
/// MotorTrim). Solo dati (CLAUDE.md §4). <see cref="BoatAssembly"/> ricolora una copia dei materiali
/// del modello, quindi due barche con livree diverse non si influenzano.
///
/// <c>[Tool]</c> perche' la legge <see cref="BoatAssembly"/>, che costruisce la barca anche
/// nell'editor (senza, la risorsa sarebbe un segnaposto e il cast fallirebbe).
/// </summary>
[GlobalClass, Tool]
public partial class BoatLivery : Resource
{
    [Export] public Color Hull { get; set; } = new(0.36f, 0.40f, 0.45f);
    [Export] public Color Stripe { get; set; } = new(0.08f, 0.08f, 0.09f);
    [Export] public Color Deck { get; set; } = new(0.62f, 0.65f, 0.68f);
    [Export] public Color Interior { get; set; } = new(0.86f, 0.87f, 0.88f);
    [Export] public Color Cushion { get; set; } = new(0.92f, 0.92f, 0.90f);
    [Export] public Color Motor { get; set; } = new(0.18f, 0.20f, 0.23f);
    [Export] public Color MotorTrim { get; set; } = new(0.06f, 0.06f, 0.07f);

    /// Colore per il materiale di quel nome, o null se la livrea non lo tocca (gomma, metallo, vetro).
    public Color? ColorFor(string materialName) => materialName switch
    {
        "Hull" => Hull,
        "Stripe" => Stripe,
        "Deck" => Deck,
        "Interior" => Interior,
        "Cushion" => Cushion,
        "Motor" => Motor,
        "MotorTrim" => MotorTrim,
        _ => null,
    };
}
