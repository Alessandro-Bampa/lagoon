using Godot;

namespace Lagoon;

/// <summary>
/// Finestra trascinabile per la barra del titolo: base di contenitori, schede Ispeziona e dialoghi.
/// Se ne possono tenere aperte piu' d'una; un click la porta in primo piano. Non e' una finestra
/// nativa: e' un pannello dentro il layer finestre della schermata, cosi' stile e ordine restano
/// sotto controllo (ed eredita la scala UI).
/// </summary>
public partial class FloatingWindow : PanelContainer
{
    private const int TitleBarHeight = 28;

    private string _title;
    private Label _titleLabel = null!;
    private VBoxContainer _body = null!;
    private bool _dragging;
    private Vector2 _dragOffset;

    /// Contenuto della finestra: le sottoclassi ci aggiungono i propri widget.
    protected VBoxContainer Body => _body;

    /// Riga a destra del titolo, per i pulsanti della sottoclasse (ordina...).
    protected HBoxContainer TitleButtons { get; private set; } = null!;

    /// <param name="title">Testo GIA' tradotto (nome dell'item o <c>Loc.T</c>).</param>
    public FloatingWindow(string title)
    {
        _title = title;
        MouseFilter = MouseFilterEnum.Stop;
    }

    public override void _Ready()
    {
        AddThemeStyleboxOverride("panel", InvTheme.Box(InvTheme.WindowBg, InvTheme.WindowBorder));

        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", 0);
        AddChild(outer);
        outer.AddChild(BuildTitleBar());

        var margin = new MarginContainer();
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 8);
        outer.AddChild(margin);

        _body = new VBoxContainer();
        _body.AddThemeConstantOverride("separation", 6);
        margin.AddChild(_body);

        BuildContent();
        // Fuori da un container il Control non si dimensiona da solo: adattalo al contenuto.
        CallDeferred(MethodName.FitAndClamp);
    }

    protected void SetTitle(string title)
    {
        _title = title;
        if (_titleLabel != null)
            _titleLabel.Text = title;
    }

    /// Svuota il corpo e lo ricostruisce (lo stato replicato e' cambiato).
    protected void RebuildBody()
    {
        foreach (Node child in _body.GetChildren())
        {
            _body.RemoveChild(child);
            child.QueueFree();
        }
        BuildContent();
        CallDeferred(MethodName.FitAndClamp);
    }

    /// Le sottoclassi popolano <see cref="Body"/> qui.
    protected virtual void BuildContent() { }

    private void FitAndClamp()
    {
        ResetSize();
        ClampToParent();
    }

    /// Una finestra trascinata fuori dallo schermo non sarebbe piu' recuperabile.
    private void ClampToParent()
    {
        if (GetParent() is not Control parent)
            return;

        Vector2 limit = parent.Size - Size;
        Position = new Vector2(
            Mathf.Clamp(Position.X, 0f, Mathf.Max(0f, limit.X)),
            Mathf.Clamp(Position.Y, 0f, Mathf.Max(0f, limit.Y)));
    }

    private Control BuildTitleBar()
    {
        var bar = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        bar.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = InvTheme.TitleBar,
            ContentMarginLeft = 10,
            ContentMarginRight = 4,
            ContentMarginTop = 2,
            ContentMarginBottom = 2,
        });

        var row = new HBoxContainer
        {
            CustomMinimumSize = new Vector2(0, TitleBarHeight - 4),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        row.AddThemeConstantOverride("separation", 6);
        bar.AddChild(row);

        _titleLabel = InvTheme.Plain(_title, InvTheme.Accent, 13);
        _titleLabel.Uppercase = true;
        _titleLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _titleLabel.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(_titleLabel);

        TitleButtons = new HBoxContainer();
        TitleButtons.AddThemeConstantOverride("separation", 4);
        row.AddChild(TitleButtons);

        // "X" e' un simbolo: il significato lo porta il tooltip, che e' localizzato.
        Button close = InvTheme.FlatButton("");
        close.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        close.Text = "X"; // i18n-ignore
        close.TooltipText = Loc.T("UI_WINDOW_CLOSE_TOOLTIP");
        close.CustomMinimumSize = new Vector2(24, 0);
        close.Pressed += QueueFree;
        row.AddChild(close);

        return bar;
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left } click)
        {
            if (click.Pressed)
            {
                MoveToFront();
                if (click.Position.Y <= TitleBarHeight)
                {
                    _dragging = true;
                    _dragOffset = click.Position;
                }
                AcceptEvent();
            }
            else
            {
                _dragging = false;
            }
            return;
        }

        if (_dragging && @event is InputEventMouseMotion motion)
        {
            Position += motion.Position - _dragOffset;
            ClampToParent();
            AcceptEvent();
        }
    }
}
