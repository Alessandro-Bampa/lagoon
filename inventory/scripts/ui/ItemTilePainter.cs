using Godot;

namespace Lagoon;

/// <summary>
/// Disegno di una tile: sfondo di categoria, icona (foto del modello) o segnaposto, nome breve,
/// contatore, tag. E' UNA routine condivisa da griglie, slot, anteprima del trascinamento e barra
/// rapida, cosi' un oggetto ha lo stesso aspetto ovunque.
/// </summary>
public static class ItemTilePainter
{
    /// <param name="rotated">Icona ruotata di 90° (oggetto ruotato in griglia).</param>
    /// <param name="contain">Icona scalata per ENTRARE nel riquadro (slot di equipaggiamento).</param>
    public static void Draw(
        CanvasItem canvas, Rect2 rect, ItemInstance item, bool rotated, bool contain = false,
        float alpha = 1f, bool showName = true)
    {
        ItemDefinition def = item.Definition;
        Color tint = InventoryColors.ForCategory(def.Category);

        canvas.DrawRect(rect, new Color(tint, 0.26f * alpha));
        canvas.DrawRect(rect.Grow(-0.5f), new Color(tint, 0.65f * alpha), filled: false, width: 1f);

        Texture2D? icon = def.ResolveIcon();
        var modulate = new Color(1f, 1f, 1f, alpha);
        if (icon != null)
            DrawIcon(canvas, rect, icon, rotated && !contain, modulate);
        else
            DrawPlaceholder(canvas, rect, def, tint, alpha);

        Font font = InvTheme.Font;
        float pad = 3f;
        float width = rect.Size.X - pad * 2f;

        if (showName)
        {
            int size = InvTheme.LabelSize;
            string name = Fit(font, def.ShortName, width, size);
            DrawText(canvas, font, new Vector2(rect.Position.X + pad, rect.Position.Y + size + 1f),
                name, width, size, new Color(InvTheme.Text, 0.92f * alpha));

            if (item.TagName.Length > 0)
            {
                string tag = Fit(font, item.TagName, width, size);
                DrawText(canvas, font, new Vector2(rect.Position.X + pad, rect.Position.Y + size * 2f + 3f),
                    tag, width, size, new Color(InvTheme.TagColor(item.TagColor), alpha));
            }
        }

        string counter = Counter(item);
        if (counter.Length > 0)
        {
            int size = InvTheme.CounterSize;
            DrawText(canvas, font, new Vector2(rect.Position.X + pad, rect.End.Y - 4f),
                counter, width, size, new Color(1f, 1f, 1f, alpha));
        }
    }

    /// Contatore in basso a destra, per priorita': stack, caricatore, arma (caricatore montato).
    public static string Counter(ItemInstance item)
    {
        if (item.StackCount > 1)
            return $"x{item.StackCount}"; // i18n-ignore
        if (item.Definition is MagazineDefinition magazine)
            return $"{item.RoundCount}/{magazine.Capacity}"; // i18n-ignore

        ItemInstance? mounted = item.MountedMagazine;
        if (mounted?.Definition is MagazineDefinition mountedDef)
            return $"{mounted.RoundCount}/{mountedDef.Capacity}"; // i18n-ignore
        return "";
    }

    private static void DrawIcon(CanvasItem canvas, Rect2 rect, Texture2D icon, bool rotated, Color modulate)
    {
        Rect2 area = rect.Grow(-3f);
        Vector2 texSize = icon.GetSize();
        if (texSize.X <= 0f || texSize.Y <= 0f)
            return;

        if (!rotated)
        {
            canvas.DrawTextureRect(icon, FitInside(area.Size, texSize, area.Position), tile: false, modulate);
            return;
        }

        // L'icona e' disegnata nelle proporzioni originali e ruotata di 90° attorno al centro:
        // prima della rotazione lo spazio disponibile ha i lati scambiati.
        var unrotated = new Vector2(area.Size.Y, area.Size.X);
        Rect2 fitted = FitInside(unrotated, texSize, -unrotated / 2f);
        canvas.DrawSetTransform(rect.GetCenter(), Mathf.Pi / 2f, Vector2.One);
        canvas.DrawTextureRect(icon, fitted, tile: false, modulate);
        canvas.DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
    }

    /// Rettangolo con le proporzioni di <paramref name="content"/> centrato in uno spazio <paramref name="box"/>.
    private static Rect2 FitInside(Vector2 box, Vector2 content, Vector2 origin)
    {
        float scale = Mathf.Min(box.X / content.X, box.Y / content.Y);
        Vector2 size = content * scale;
        return new Rect2(origin + (box - size) / 2f, size);
    }

    /// Senza icona: sigla dell'oggetto in grande e tenue al centro del riquadro.
    private static void DrawPlaceholder(CanvasItem canvas, Rect2 rect, ItemDefinition def, Color tint, float alpha)
    {
        Rect2 inner = rect.Grow(-Mathf.Min(rect.Size.X, rect.Size.Y) * 0.22f);
        canvas.DrawRect(inner, new Color(tint.Lightened(0.25f), 0.22f * alpha));
        canvas.DrawRect(inner, new Color(tint.Lightened(0.4f), 0.5f * alpha), filled: false, width: 1f);

        string name = def.ShortName;
        string glyph = name.Length > 2 ? name[..2].ToUpperInvariant() : name.ToUpperInvariant();
        int size = Mathf.Clamp((int)(Mathf.Min(rect.Size.X, rect.Size.Y) * 0.34f), 10, 40);
        Font font = InvTheme.Font;
        Vector2 text = font.GetStringSize(glyph, HorizontalAlignment.Left, -1, size);
        Vector2 pos = rect.GetCenter() + new Vector2(-text.X / 2f, size * 0.35f);
        canvas.DrawString(font, pos, glyph, HorizontalAlignment.Left, -1, size, new Color(1f, 1f, 1f, 0.5f * alpha));
    }

    /// Testo allineato a destra con una sottile ombra per restare leggibile sopra l'icona.
    private static void DrawText(
        CanvasItem canvas, Font font, Vector2 pos, string text, float width, int size, Color color)
    {
        canvas.DrawString(font, pos + Vector2.One, text, HorizontalAlignment.Right, width, size,
            new Color(0f, 0f, 0f, 0.85f * color.A));
        canvas.DrawString(font, pos, text, HorizontalAlignment.Right, width, size, color);
    }

    /// Tronca il testo finche' entra nella larghezza (una sola riga, niente a capo).
    private static string Fit(Font font, string text, float width, int size)
    {
        while (text.Length > 1 && font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X > width)
            text = text[..^1];
        return text;
    }
}
