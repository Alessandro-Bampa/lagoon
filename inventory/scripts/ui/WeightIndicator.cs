using Godot;

namespace Lagoon;

/// <summary>
/// Indicatore del peso trasportato: testo e barra sottile con i marcatori delle soglie di
/// sovraccarico. Bianco sotto la prima soglia, giallo fino alla critica, rosso oltre.
/// </summary>
public partial class WeightIndicator : Control
{
    private float _weight;

    public WeightIndicator()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        CustomMinimumSize = new Vector2(0, 34);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
    }

    public void SetWeight(float weightKg)
    {
        _weight = weightKg;
        QueueRedraw();
    }

    public override void _Draw()
    {
        InventoryConfig cfg = InventoryConfig.Current;
        Color color = _weight <= cfg.OverweightStartKg ? InvTheme.WeightOk
            : _weight <= cfg.OverweightCriticalKg ? InvTheme.WeightWarn
            : InvTheme.WeightBad;

        string text = Loc.T("UI_INV_WEIGHT", Loc.Num(_weight, "0.0"));
        DrawString(InvTheme.Font, new Vector2(0, 15), text, HorizontalAlignment.Left, -1, 14, color);

        var bar = new Rect2(0, 24, Size.X, 4);
        DrawRect(bar, new Color(1f, 1f, 1f, 0.1f));
        float max = Mathf.Max(cfg.OverweightMaxKg, 0.01f);
        DrawRect(new Rect2(bar.Position, new Vector2(bar.Size.X * Mathf.Clamp(_weight / max, 0f, 1f), 4)), color);

        // Marcatori alle soglie.
        foreach (float threshold in new[] { cfg.OverweightStartKg, cfg.OverweightCriticalKg })
        {
            float x = bar.Size.X * Mathf.Clamp(threshold / max, 0f, 1f);
            DrawLine(new Vector2(x, 21), new Vector2(x, 31), new Color(1f, 1f, 1f, 0.5f), 1f);
        }
    }
}
