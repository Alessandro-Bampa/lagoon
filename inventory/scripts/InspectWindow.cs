using Godot;

namespace Lagoon;

/// <summary>
/// Scheda "Ispeziona": doppio click su un'arma o un pezzo di equipaggiamento. Mostra icona e
/// statistiche dell'oggetto.
///
/// L'area MODULI/DURABILITA' e' predisposta ma vuota: allegati, usura e smontaggio dei componenti
/// restano fuori dal prototipo. La Fase 3 (skill combat-shooting) ha introdotto solo la balistica
/// di base in <see cref="WeaponDefinition"/>; qui resta il gancio, senza costruire sistemi
/// speculativi (CLAUDE.md §7).
/// </summary>
public partial class InspectWindow : FloatingWindow
{
    private readonly ItemInstance _item;

    public InspectWindow(ItemInstance item) : base(Loc.T("UI_INSPECT_TITLE", item.Definition.DisplayName))
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
        stats.AddChild(new Label { Text = def.DisplayName, AutoTranslateMode = AutoTranslateModeEnum.Disabled });
        stats.AddChild(Dim(Loc.T("UI_INSPECT_CATEGORY", CategoryLabel(def.Category))));
        stats.AddChild(Dim(Loc.T("UI_INSPECT_SIZE", def.Width, def.Height)));
        stats.AddChild(Dim(Loc.T("UI_INSPECT_WEIGHT", Loc.Num(def.Weight))));
        if (_item.StackCount > 1)
            stats.AddChild(Dim(Loc.T("UI_INSPECT_QUANTITY", _item.StackCount)));
        if (def.IsContainer)
            stats.AddChild(Dim(Loc.T("UI_INSPECT_CAPACITY", def.ContainerColumns, def.ContainerRows)));
        top.AddChild(stats);

        Body.AddChild(top);

        // Descrizione ed effetto sono opzionali: gli oggetti che non li hanno non mostrano righe vuote.
        string description = def.Description;
        if (!string.IsNullOrEmpty(description))
        {
            Body.AddChild(new HSeparator());
            Body.AddChild(Wrapped(description));
        }

        string effect = def.Effect;
        if (!string.IsNullOrEmpty(effect))
            Body.AddChild(Wrapped(effect));

        Body.AddChild(new HSeparator());
        Body.AddChild(new Label { Text = "UI_INSPECT_MODS_TITLE" });
        Body.AddChild(Dim(Loc.T("UI_INSPECT_MODS_LOCKED")));
    }

    /// Riga secondaria. Riceve testo GIA' tradotto, quindi l'auto-translate va disattivato:
    /// un secondo passaggio su un risultato non e' mai voluto (skill i18n-localization).
    private static Label Dim(string text)
    {
        var label = new Label { Text = text, AutoTranslateMode = AutoTranslateModeEnum.Disabled };
        label.AddThemeColorOverride("font_color", new Color(0.68f, 0.68f, 0.74f));
        return label;
    }

    /// Paragrafo a capo automatico, per i testi lunghi (descrizione, effetto).
    private static Label Wrapped(string text)
    {
        var label = new Label
        {
            Text = text,
            AutoTranslateMode = AutoTranslateModeEnum.Disabled,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(320, 0),
        };
        label.AddThemeColorOverride("font_color", new Color(0.78f, 0.78f, 0.84f));
        return label;
    }

    /// Chiave derivata dal nome dell'enum, come per gli slot: <c>Weapon</c> -> <c>CATEGORY_WEAPON</c>.
    private static string CategoryLabel(ItemCategory category)
        => Loc.T($"CATEGORY_{category.ToString().ToUpperInvariant()}");
}
