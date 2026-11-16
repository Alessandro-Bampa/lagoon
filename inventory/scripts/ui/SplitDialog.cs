using System;
using Godot;

namespace Lagoon;

/// <summary>
/// Dialogo di divisione di uno stack (rilascio su celle libere tenendo Ctrl): cursore e campo
/// numerico sincronizzati, da 1 a stack-1, valore iniziale a meta'. Invio conferma, Esc annulla
/// (lo gestisce la schermata, che chiude l'ultima finestra aperta).
/// </summary>
public partial class SplitDialog : FloatingWindow
{
    private readonly int _max;
    private readonly Action<int> _confirm;
    private HSlider _slider = null!;
    private SpinBox _spin = null!;

    /// <param name="stack">Dimensione dello stack da dividere (almeno 2).</param>
    public SplitDialog(string itemName, int stack, Action<int> confirm)
        : base(Loc.T("UI_INV_SPLIT_TITLE", itemName))
    {
        _max = Math.Max(1, stack - 1);
        _confirm = confirm;
    }

    protected override void BuildContent()
    {
        int initial = Math.Max(1, (_max + 1) / 2);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);

        _slider = new HSlider
        {
            MinValue = 1,
            MaxValue = _max,
            Step = 1,
            Value = initial,
            CustomMinimumSize = new Vector2(220, 0),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        _spin = new SpinBox { MinValue = 1, MaxValue = _max, Step = 1, Value = initial };

        _slider.ValueChanged += value => _spin.SetValueNoSignal(value);
        _spin.ValueChanged += value => _slider.SetValueNoSignal(value);
        _spin.GetLineEdit().TextSubmitted += _ => Confirm();

        row.AddChild(_slider);
        row.AddChild(_spin);
        Body.AddChild(row);

        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        buttons.AddThemeConstantOverride("separation", 6);
        Button cancel = InvTheme.FlatButton("UI_INV_CANCEL");
        cancel.Pressed += QueueFree;
        Button ok = InvTheme.FlatButton("UI_INV_CONFIRM");
        ok.Pressed += Confirm;
        buttons.AddChild(cancel);
        buttons.AddChild(ok);
        Body.AddChild(buttons);

        _spin.GetLineEdit().CallDeferred(Control.MethodName.GrabFocus);
    }

    private void Confirm()
    {
        _spin.Apply();
        int amount = Mathf.Clamp((int)_spin.Value, 1, _max);
        QueueFree();
        _confirm(amount);
    }
}
