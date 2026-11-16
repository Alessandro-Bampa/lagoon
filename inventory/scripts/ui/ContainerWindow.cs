using Godot;

namespace Lagoon;

/// <summary>
/// Finestra di un oggetto aperto con doppio click: le griglie di un contenitore e/o gli slot di un
/// oggetto che ne ha (il pozzetto del caricatore di un'arma).
///
/// Non tiene riferimenti all'istanza attraverso un sync (la replica ricostruisce tutto): ricorda
/// l'<c>InstanceId</c> e a ogni aggiornamento lo ri-risolve. Se l'oggetto e' stato solo spostato la
/// finestra resta aperta; se non e' piu' raggiungibile si chiude.
/// </summary>
public partial class ContainerWindow : FloatingWindow
{
    private readonly InventoryScreen _screen;

    public int ItemId { get; }
    private int _space;

    public ContainerWindow(InventoryScreen screen, ItemInstance item, int space)
        : base(TitleOf(item))
    {
        _screen = screen;
        ItemId = item.InstanceId;
        _space = space;
    }

    private static string TitleOf(ItemInstance item)
        => item.TagName.Length > 0
            ? Loc.T("UI_INV_WINDOW_TAGGED", item.Definition.DisplayName, item.TagName)
            : item.Definition.DisplayName;

    public override void _Ready()
    {
        base._Ready();

        Button sort = InvTheme.FlatButton("UI_INV_SORT");
        sort.TooltipText = Loc.T("UI_INV_SORT_TOOLTIP");
        sort.Pressed += () =>
        {
            ItemInstance? item = _screen.FindAnywhere(ItemId, ref _space);
            if (item != null)
                _screen.SortContainer(item, _space);
        };
        TitleButtons.AddChild(sort);
        sort.Visible = _screen.FindAnywhere(ItemId, ref _space)?.IsContainer ?? false;
    }

    /// Ricostruisce il contenuto dallo stato corrente; chiude la finestra se l'oggetto e' sparito.
    public void Refresh()
    {
        ItemInstance? item = _screen.FindAnywhere(ItemId, ref _space);
        if (item == null)
        {
            QueueFree();
            return;
        }
        SetTitle(TitleOf(item));
        RebuildBody();
    }

    protected override void BuildContent()
    {
        ItemInstance? item = _screen.FindAnywhere(ItemId, ref _space);
        if (item == null)
            return;

        if (item.SlotItems.Length > 0)
        {
            var slots = new HBoxContainer();
            slots.AddThemeConstantOverride("separation", InvTheme.Gap);
            for (int i = 0; i < item.SlotItems.Length; i++)
            {
                var box = new VBoxContainer();
                box.AddThemeConstantOverride("separation", 2);
                box.AddChild(InvTheme.Header($"UI_INV_ITEMSLOT_{item.Definition.Slots[i].SlotId.ToUpperInvariant()}"));
                box.AddChild(new SlotView(
                    _screen, ItemAddress.SlotOf(_space, item.InstanceId, i), item.SlotItems[i],
                    new Vector2(1.5f, 2f)));
                slots.AddChild(box);
            }
            Body.AddChild(slots);
        }

        if (item.IsContainer)
            Body.AddChild(new ContainerLayoutView(_screen, item, _space));
    }
}
