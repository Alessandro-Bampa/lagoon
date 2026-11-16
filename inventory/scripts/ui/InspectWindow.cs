using System.Collections.Generic;
using Godot;

namespace Lagoon;

/// <summary>
/// Scheda "Ispeziona": icona grande, proprieta', contenuto del caricatore, filtri leggibili,
/// descrizione. E' una fotografia dell'oggetto al momento dell'apertura (non si aggiorna): se ne
/// possono tenere aperte piu' d'una per confrontare.
/// </summary>
public partial class InspectWindow : FloatingWindow
{
    private readonly ItemInstance _item;

    public InspectWindow(ItemInstance item)
        : base(Loc.T("UI_INSPECT_TITLE", item.Definition.DisplayName))
    {
        _item = item;
    }

    protected override void BuildContent()
    {
        ItemDefinition def = _item.Definition;

        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 12);
        // Anteprima in un riquadro fisso: anche un'arma 5x2 resta contenuta.
        top.AddChild(new ItemPreviewView
        {
            Item = _item,
            Contain = true,
            ShowName = false,
            CustomMinimumSize = new Vector2(144, 144),
        });

        var stats = new VBoxContainer();
        stats.AddThemeConstantOverride("separation", 2);
        stats.AddChild(InvTheme.Plain(def.DisplayName, InvTheme.Text, 15));
        stats.AddChild(Dim(Loc.T("UI_INSPECT_CATEGORY", CategoryLabel(def.Category))));
        stats.AddChild(Dim(Loc.T("UI_INSPECT_SIZE", def.Width, def.Height)));
        stats.AddChild(Dim(Loc.T("UI_INSPECT_WEIGHT", Loc.Num(_item.TotalWeight()))));
        if (_item.StackCount > 1)
            stats.AddChild(Dim(Loc.T("UI_INSPECT_QUANTITY", _item.StackCount)));
        if (def.IsContainer)
            stats.AddChild(Dim(Loc.T("UI_INSPECT_CAPACITY", def.CapacityCells)));

        switch (def)
        {
            case MagazineDefinition magazine:
                stats.AddChild(Dim(Loc.T("UI_INSPECT_CALIBER", magazine.Caliber)));
                stats.AddChild(Dim(Loc.T("UI_INSPECT_ROUNDS", _item.RoundCount, magazine.Capacity)));
                break;
            case AmmoDefinition ammo:
                stats.AddChild(Dim(Loc.T("UI_INSPECT_CALIBER", ammo.Caliber)));
                break;
            case WeaponDefinition weapon:
                stats.AddChild(Dim(Loc.T("UI_INSPECT_CALIBER", weapon.Caliber)));
                break;
        }
        top.AddChild(stats);
        Body.AddChild(top);

        // Contenuto del caricatore (suo o montato), dalla cima: il primo e' il prossimo colpo.
        ItemInstance? mag = InventoryRules.MagazineOf(_item);
        if (mag != null && mag.Rounds.Count > 0)
        {
            Body.AddChild(new HSeparator());
            Body.AddChild(InvTheme.Header("UI_INSPECT_MAG_CONTENTS"));
            for (int i = mag.Rounds.Count - 1; i >= 0; i--)
                Body.AddChild(Dim(Loc.T("UI_INSPECT_MAG_LINE", mag.Rounds[i].Count, mag.Rounds[i].Ammo.DisplayName)));
        }

        string accepts = DescribeFilter(def.ContentFilter);
        if (accepts.Length > 0)
        {
            Body.AddChild(new HSeparator());
            Body.AddChild(Wrapped(accepts));
        }

        // Descrizione ed effetto sono opzionali: chi non li ha non mostra righe vuote.
        if (def.Description.Length > 0)
        {
            Body.AddChild(new HSeparator());
            Body.AddChild(Wrapped(def.Description));
        }
        if (def.Effect.Length > 0)
            Body.AddChild(Wrapped(def.Effect));
    }

    /// "Accetta: Munizioni, Caricatore" / "Non accetta: Zaino, Arma".
    private static string DescribeFilter(ItemFilter? filter)
    {
        if (filter == null)
            return "";

        var lines = new List<string>();
        if (filter.IncludeCategories.Count > 0)
            lines.Add(Loc.T("UI_INSPECT_ACCEPTS", JoinCategories(filter.IncludeCategories)));
        if (filter.ExcludeCategories.Count > 0)
            lines.Add(Loc.T("UI_INSPECT_REJECTS", JoinCategories(filter.ExcludeCategories)));
        return string.Join("\n", lines);
    }

    private static string JoinCategories(Godot.Collections.Array<ItemCategory> categories)
    {
        var names = new List<string>();
        foreach (ItemCategory category in categories)
            names.Add(CategoryLabel(category));
        return string.Join(", ", names);
    }

    private static Label Dim(string text) => InvTheme.Plain(text, InvTheme.TextDim, 12);

    private static Label Wrapped(string text)
    {
        Label label = InvTheme.Plain(text, InvTheme.Text, 12);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.CustomMinimumSize = new Vector2(320, 0);
        return label;
    }

    /// Chiave derivata dal nome dell'enum: <c>Weapon</c> -> <c>CATEGORY_WEAPON</c>.
    public static string CategoryLabel(ItemCategory category)
        => Loc.T($"CATEGORY_{category.ToString().ToUpperInvariant()}");
}
