using Godot;

namespace Lagoon;

/// <summary>
/// Aspetto dell'inventario: colori, misure e piccoli costruttori di widget. Tutte le dimensioni
/// derivano da <see cref="Cell"/>, che la schermata ricalcola sullo spazio disponibile (fino al
/// massimo di <see cref="InventoryConfig.CellPx"/>): l'impianto a tre colonne sta cosi' anche in
/// una finestra piccola, senza toccare la matematica in celle.
/// </summary>
public static class InvTheme
{
    /// Lato di una cella in pixel logici. Stato di sola presentazione, scritto da InventoryScreen.
    public static int Cell { get; set; } = 64;

    // --- palette --------------------------------------------------------------------------
    public static readonly Color Backdrop = new(0.02f, 0.025f, 0.03f, 0.90f);
    public static readonly Color PanelBg = new(0.055f, 0.06f, 0.065f, 0.94f);
    public static readonly Color PanelBorder = new(0.22f, 0.23f, 0.24f);
    public static readonly Color WindowBg = new(0.07f, 0.075f, 0.08f, 0.98f);
    public static readonly Color WindowBorder = new(0.38f, 0.38f, 0.36f);
    public static readonly Color TitleBar = new(0.12f, 0.125f, 0.13f);
    public static readonly Color CellBg = new(0f, 0f, 0f, 0.35f);
    public static readonly Color CellLine = new(1f, 1f, 1f, 0.08f);
    public static readonly Color SlotBg = new(0f, 0f, 0f, 0.42f);
    public static readonly Color SlotBorder = new(1f, 1f, 1f, 0.14f);
    public static readonly Color Text = new(0.86f, 0.86f, 0.82f);
    public static readonly Color TextDim = new(0.58f, 0.59f, 0.58f);
    public static readonly Color TextFaint = new(0.58f, 0.59f, 0.58f, 0.45f);
    public static readonly Color Accent = new(0.78f, 0.72f, 0.52f);
    public static readonly Color Valid = new(0.235f, 0.69f, 0.263f, 0.35f);
    public static readonly Color Invalid = new(0.753f, 0.224f, 0.169f, 0.38f);
    public static readonly Color ValidBorder = new(0.35f, 0.85f, 0.4f, 0.9f);
    public static readonly Color TargetBorder = new(0.95f, 0.85f, 0.35f, 0.95f);
    public static readonly Color Blocked = new(0.75f, 0.2f, 0.16f, 0.75f);
    public static readonly Color WeightOk = new(0.86f, 0.86f, 0.82f);
    public static readonly Color WeightWarn = new(0.93f, 0.78f, 0.25f);
    public static readonly Color WeightBad = new(0.86f, 0.28f, 0.22f);

    /// Colori disponibili per il tag di un contenitore (l'indice e' cio' che si salva).
    public static readonly Color[] TagColors =
    {
        new(0.86f, 0.86f, 0.82f), new(0.86f, 0.3f, 0.26f), new(0.93f, 0.6f, 0.2f),
        new(0.93f, 0.82f, 0.3f), new(0.4f, 0.78f, 0.4f), new(0.35f, 0.72f, 0.85f),
        new(0.45f, 0.5f, 0.92f), new(0.75f, 0.45f, 0.88f),
    };

    public static Color TagColor(int index) => TagColors[Mathf.PosMod(index, TagColors.Length)];

    public static Font Font => ThemeDB.FallbackFont;

    // --- misure derivate dalla cella -------------------------------------------------------
    public static int LabelSize => Mathf.Clamp(Cell / 5, 9, 12);
    public static int CounterSize => Mathf.Clamp(Cell / 5 + 1, 10, 13);
    public static int HeaderSize => Mathf.Clamp(Cell / 5 + 1, 10, 13);
    public static int Gap => Mathf.Max(4, Cell / 8);

    // --- costruttori ----------------------------------------------------------------------

    public static StyleBoxFlat Box(Color bg, Color border, int margin = 0)
        => new()
        {
            BgColor = bg,
            BorderColor = border,
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            ContentMarginLeft = margin,
            ContentMarginTop = margin,
            ContentMarginRight = margin,
            ContentMarginBottom = margin,
        };

    /// Etichetta di sezione in maiuscoletto. <paramref name="key"/> e' una chiave: la traduce
    /// l'auto-translate, e <c>Uppercase</c> si applica al testo gia' tradotto.
    public static Label Header(string key)
    {
        var label = new Label
        {
            Text = key,
            Uppercase = true,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", HeaderSize);
        label.AddThemeColorOverride("font_color", TextDim);
        return label;
    }

    /// Etichetta con testo GIA' tradotto (composto o dinamico): niente auto-translate.
    public static Label Plain(string text, Color? color = null, int size = 0)
    {
        var label = new Label
        {
            Text = text,
            AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", size > 0 ? size : 13);
        label.AddThemeColorOverride("font_color", color ?? Text);
        return label;
    }

    /// Pulsante piatto coerente col resto della schermata. <paramref name="key"/> = chiave di traduzione.
    public static Button FlatButton(string key)
    {
        var button = new Button { Text = key, FocusMode = Control.FocusModeEnum.None };
        button.AddThemeFontSizeOverride("font_size", 12);
        button.AddThemeColorOverride("font_color", Text);
        button.AddThemeStyleboxOverride("normal", Box(new Color(1f, 1f, 1f, 0.05f), PanelBorder, 6));
        button.AddThemeStyleboxOverride("hover", Box(new Color(1f, 1f, 1f, 0.12f), WindowBorder, 6));
        button.AddThemeStyleboxOverride("pressed", Box(new Color(1f, 1f, 1f, 0.18f), Accent, 6));
        return button;
    }
}
