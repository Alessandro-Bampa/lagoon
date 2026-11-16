using System;
using Godot;

namespace Lagoon;

/// Dialogo "Tag" di un contenitore: nome breve e un colore dalla tavolozza.
public partial class TagDialog : FloatingWindow
{
    private readonly Action<string, int> _confirm;
    private readonly string _initialName;
    private int _color;
    private LineEdit _name = null!;
    private HBoxContainer _swatches = null!;

    public TagDialog(ItemInstance container, Action<string, int> confirm)
        : base(Loc.T("UI_INV_TAG_TITLE", container.Definition.DisplayName))
    {
        _confirm = confirm;
        _initialName = container.TagName;
        _color = container.TagColor;
    }

    protected override void BuildContent()
    {
        _name = new LineEdit
        {
            Text = _initialName,
            MaxLength = InventoryConfig.Current.TagMaxLength,
            PlaceholderText = "UI_INV_TAG_PLACEHOLDER",
            CustomMinimumSize = new Vector2(240, 0),
        };
        _name.TextSubmitted += _ => Confirm();
        Body.AddChild(_name);

        _swatches = new HBoxContainer();
        _swatches.AddThemeConstantOverride("separation", 4);
        for (int i = 0; i < InvTheme.TagColors.Length; i++)
        {
            int index = i;
            var swatch = new Button
            {
                CustomMinimumSize = new Vector2(24, 24),
                FocusMode = FocusModeEnum.None,
            };
            swatch.Pressed += () =>
            {
                _color = index;
                RefreshSwatches();
            };
            _swatches.AddChild(swatch);
        }
        Body.AddChild(_swatches);
        RefreshSwatches();

        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        buttons.AddThemeConstantOverride("separation", 6);
        Button cancel = InvTheme.FlatButton("UI_INV_CANCEL");
        cancel.Pressed += QueueFree;
        Button ok = InvTheme.FlatButton("UI_INV_CONFIRM");
        ok.Pressed += Confirm;
        buttons.AddChild(cancel);
        buttons.AddChild(ok);
        Body.AddChild(buttons);

        _name.CallDeferred(Control.MethodName.GrabFocus);
    }

    private void RefreshSwatches()
    {
        for (int i = 0; i < _swatches.GetChildCount(); i++)
        {
            var swatch = (Button)_swatches.GetChild(i);
            Color border = i == _color ? Colors.White : new Color(0f, 0f, 0f, 0.6f);
            StyleBoxFlat style = InvTheme.Box(InvTheme.TagColors[i], border);
            style.BorderWidthLeft = style.BorderWidthTop = style.BorderWidthRight = style.BorderWidthBottom =
                i == _color ? 2 : 1;
            foreach (string state in new[] { "normal", "hover", "pressed" })
                swatch.AddThemeStyleboxOverride(state, style);
        }
    }

    private void Confirm()
    {
        string name = _name.Text;
        QueueFree();
        _confirm(name, _color);
    }
}
