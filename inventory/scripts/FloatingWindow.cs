using Godot;

namespace Lagoon;

/// <summary>
/// Finestra pop-up trascinabile per la barra del titolo, base comune di
/// <see cref="ContainerWindow"/> e <see cref="InspectWindow"/>. Se ne possono tenere aperte piu'
/// contemporaneamente e spostarle sullo schermo per trasferire oggetti da un contenitore all'altro.
/// </summary>
public partial class FloatingWindow : PanelContainer
{
    private const int TitleBarHeight = 26;

    private readonly string _title;
    private VBoxContainer _body = null!;
    private bool _dragging;
    private Vector2 _dragOffset;

    /// Contenuto della finestra: le sottoclassi ci aggiungono i propri widget.
    protected VBoxContainer Body => _body;

    public FloatingWindow(string title)
    {
        _title = title;
        MouseFilter = MouseFilterEnum.Stop;
    }

    public override void _Ready()
    {
        // Si dimensiona sul contenuto e resta dove viene posizionata (niente layout di container).
        var panel = new StyleBoxFlat
        {
            BgColor = new Color(0.10f, 0.10f, 0.13f, 0.97f),
            BorderColor = new Color(0.40f, 0.42f, 0.48f),
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 6,
            ContentMarginBottom = 8,
        };
        AddThemeStyleboxOverride("panel", panel);

        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", 6);
        AddChild(outer);

        outer.AddChild(BuildTitleBar());

        _body = new VBoxContainer();
        _body.AddThemeConstantOverride("separation", 6);
        outer.AddChild(_body);

        BuildContent();
        // Fuori da un container il Control non si dimensiona da solo: adattalo al contenuto.
        CallDeferred(MethodName.FitAndClamp);
    }

    /// Svuota il corpo e lo ricostruisce (usato quando lo stato replicato cambia).
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

    /// Adatta la finestra al contenuto e la riporta dentro l'area visibile.
    private void FitAndClamp()
    {
        ResetSize();
        ClampToParent();
    }

    /// Impedisce che una finestra finisca (o resti) fuori dallo schermo: trascinata oltre il bordo
    /// non sarebbe piu' recuperabile. Vale anche all'apertura, se il viewport e' piccolo o la scala
    /// UI e' alta e la posizione a cascata cadrebbe fuori.
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
        var bar = new HBoxContainer { CustomMinimumSize = new Vector2(0, TitleBarHeight) };
        bar.AddThemeConstantOverride("separation", 8);

        var title = new Label
        {
            Text = _title,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore, // il trascinamento lo gestisce la finestra
        };
        bar.AddChild(title);

        var close = new Button { Text = "X", CustomMinimumSize = new Vector2(24, 0) };
        close.Pressed += QueueFree;
        bar.AddChild(close);

        return bar;
    }

    public override void _GuiInput(InputEvent @event)
    {
        // Trascinamento dalla fascia del titolo (la parte alta della finestra).
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left } click)
        {
            if (click.Pressed && click.Position.Y <= TitleBarHeight)
            {
                _dragging = true;
                _dragOffset = click.Position;
                MoveToFront();
                AcceptEvent();
            }
            else if (!click.Pressed)
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
