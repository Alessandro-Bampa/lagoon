using Godot;

namespace Lagoon;

/// <summary>
/// Scheda "Ispeziona": doppio click su un'arma o un pezzo di equipaggiamento. Mostra icona e
/// statistiche dell'oggetto.
///
/// L'area MODULI/DURABILITA' e' predisposta ma vuota: allegati, usura e smontaggio dei componenti
/// dipendono dal sistema armi, che CLAUDE.md §8 colloca in Fase 3 (la cartella <c>combat/</c> e'
/// tuttora vuota). Qui resta il gancio, senza costruire sistemi speculativi (§11).
/// </summary>
public partial class InspectWindow : FloatingWindow
{
    private readonly ItemInstance _item;

    public InspectWindow(ItemInstance item) : base($"Ispeziona — {item.Definition.DisplayName}")
    {
        _item = item;
    }

    protected override void BuildContent()
    {
        ItemDefinition def = _item.Definition;

        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 12);
        // Anteprima in un riquadro fisso: anche un'arma 5x2 resta contenuta.
        top.AddChild(ItemVisual.BuildFitted(def, new Vector2(128, 128), _item.StackCount));

        var stats = new VBoxContainer();
        stats.AddThemeConstantOverride("separation", 2);
        stats.AddChild(new Label { Text = def.DisplayName });
        stats.AddChild(Dim($"Categoria: {CategoryLabel(def.Category)}"));
        stats.AddChild(Dim($"Ingombro: {def.Width}x{def.Height} celle"));
        stats.AddChild(Dim($"Peso: {def.Weight:0.##} kg"));
        if (_item.StackCount > 1)
            stats.AddChild(Dim($"Quantita': {_item.StackCount}"));
        if (def.IsContainer)
            stats.AddChild(Dim($"Capacita': {def.ContainerColumns}x{def.ContainerRows}"));
        top.AddChild(stats);

        Body.AddChild(top);

        Body.AddChild(new HSeparator());
        Body.AddChild(new Label { Text = "Moduli e durabilita'" });
        Body.AddChild(Dim("Disponibili con il sistema armi (Fase 3)."));
    }

    private static Label Dim(string text)
    {
        var label = new Label { Text = text };
        label.AddThemeColorOverride("font_color", new Color(0.68f, 0.68f, 0.74f));
        return label;
    }

    private static string CategoryLabel(ItemCategory category) => category switch
    {
        ItemCategory.Container => "Contenitore",
        ItemCategory.Equipment => "Equipaggiamento",
        ItemCategory.Weapon => "Arma",
        ItemCategory.Consumable => "Consumabile",
        _ => "Generico",
    };
}
