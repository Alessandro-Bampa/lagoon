using Godot;

namespace Lagoon;

/// <summary>
/// Costruisce la rappresentazione visiva di un item (icona o riquadro colorato di ripiego + conteggio
/// stack) dentro un <see cref="Control"/> della dimensione occupata in celle. Se l'item e' ruotato,
/// l'icona viene realmente RUOTATA di 90° (non solo ridimensionata), cosi' che uno sprite disegnato
/// "in piedi" appaia coricato. Condiviso da <see cref="ItemView"/> (item in griglia) e
/// <see cref="DragPreview"/> (anteprima di trascinamento).
/// </summary>
public static class ItemVisual
{
    /// Crea un Control di dimensione (celle occupate × CellSize) con la grafica dell'item.
    public static Control Build(ItemDefinition definition, bool rotated, int stackCount)
    {
        int cell = GridPanelView.CellSize;
        int occupiedW = rotated ? definition.Height : definition.Width;
        int occupiedH = rotated ? definition.Width : definition.Height;
        var size = new Vector2(occupiedW * cell, occupiedH * cell);

        var root = new Control
        {
            CustomMinimumSize = size,
            Size = size,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ClipContents = true,
        };

        Texture2D? icon = definition.ResolveIcon();
        if (icon != null)
            root.AddChild(BuildIcon(icon, definition, rotated, cell));
        else
            root.AddChild(BuildColorRect(definition));

        if (stackCount > 1)
            root.AddChild(BuildStackLabel(stackCount));

        return root;
    }

    /// <summary>
    /// Variante per un riquadro di dimensione arbitraria (es. uno slot di equipaggiamento, che ha
    /// una forma fissa indipendente dall'ingombro in celle dell'oggetto): l'icona viene scalata per
    /// ENTRARE nel riquadro mantenendo le proporzioni, e centrata. Senza questo un oggetto grande
    /// (zaino 2x3) deborderebbe dal proprio slot.
    /// </summary>
    public static Control BuildFitted(ItemDefinition definition, Vector2 box, int stackCount)
    {
        var root = new Control
        {
            CustomMinimumSize = box,
            Size = box,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ClipContents = true,
        };

        Texture2D? icon = definition.ResolveIcon();
        if (icon != null)
        {
            var tex = new TextureRect
            {
                Texture = icon,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                // Entra nel riquadro senza deformarsi e resta centrato.
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            tex.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            root.AddChild(tex);
        }
        else
        {
            root.AddChild(BuildColorRect(definition));
        }

        if (stackCount > 1)
            root.AddChild(BuildStackLabel(stackCount));

        return root;
    }

    private static Control BuildIcon(Texture2D icon, ItemDefinition definition, bool rotated, int cell)
    {
        // Dimensione dell'icona nell'orientamento "in piedi" (non ruotato).
        float uprightW = definition.Width * cell;
        float uprightH = definition.Height * cell;

        // NB: non impostare Size insieme agli anchor. Assegnare Size scrive gli offset del Control;
        // cambiare poi gli anchor SENZA azzerarli li somma alla dimensione del genitore, e l'icona
        // esce dal riquadro (venendo tagliata da ClipContents). I due rami qui sotto usano quindi
        // una tecnica sola per volta: anchor puri, oppure Size/Position puri.
        var tex = new TextureRect
        {
            Texture = icon,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };

        if (!rotated)
        {
            // Orientamento normale: la scatola occupata coincide con quella "in piedi", quindi
            // basta ancorare l'icona al riquadro (KeepAspectCentered la centra).
            tex.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            return tex;
        }

        // Ruotato: dimensione "in piedi" + rotazione di 90° attorno al centro. Il bounding box
        // risultante e' (uprightH x uprightW), cioe' la scatola occupata: centriamocelo dentro.
        tex.Size = new Vector2(uprightW, uprightH);
        tex.PivotOffset = new Vector2(uprightW / 2f, uprightH / 2f);
        tex.RotationDegrees = 90f;
        float occW = uprightH;
        float occH = uprightW;
        tex.Position = new Vector2(occW / 2f - uprightW / 2f, occH / 2f - uprightH / 2f);
        return tex;
    }

    private static Control BuildColorRect(ItemDefinition definition)
    {
        var rect = new ColorRect
        {
            Color = InventoryColors.ForCategory(definition.Category),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        return rect;
    }

    private static Control BuildStackLabel(int stackCount)
    {
        // Il conteggio resta sempre dritto (non ruota con l'icona).
        var label = new Label
        {
            Text = $"x{stackCount}",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        label.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        return label;
    }
}
