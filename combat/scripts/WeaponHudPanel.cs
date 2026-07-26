using Godot;

namespace Lagoon;

/// <summary>
/// Pannello di combattimento della HUD: salute (sempre) e stato dell'arma (solo se impugnata).
///
/// Segue le regole di layout della skill ui-hud: dimensioni in pixel fisse e ancoraggio, mai
/// coordinate assolute. Si posiziona in basso a destra sopra la fascia della hotbar, prendendo
/// l'altezza di quest'ultima da
/// <see cref="HotbarSlotView.SlotSize"/> invece di duplicare un numero magico.
///
/// Nessuno stato proprio: legge le proprieta' replicate di <see cref="WeaponController"/> e
/// <see cref="HealthComponent"/>. Anche il conteggio dei colpi arriva dall'host — se la HUD e il
/// server non fossero d'accordo, il server ha ragione per costruzione.
/// </summary>
public partial class WeaponHudPanel : Control
{
    private const int PanelWidth = 240;
    private const int PanelHeight = 92;
    private const int Margin = 16;
    private const int BarHeight = 10;

    private static readonly Color PanelColor = new(0.08f, 0.08f, 0.09f, 0.72f);
    private static readonly Color HealthColor = new(0.72f, 0.24f, 0.22f);
    private static readonly Color HealthBackColor = new(0.2f, 0.2f, 0.22f, 0.9f);
    private static readonly Color ReloadColor = new(0.95f, 0.75f, 0.30f);

    private readonly WeaponController _weapon;
    private readonly HealthComponent _health;

    private PanelContainer _panel = null!;
    private Label _weaponLabel = null!;
    private Label _ammoLabel = null!;
    private ProgressBar _healthBar = null!;
    private VBoxContainer _weaponBlock = null!;
    private bool _reloadingShown;

    public WeaponHudPanel(WeaponController weapon, HealthComponent health)
    {
        _weapon = weapon;
        _health = health;
    }

    public override void _Ready()
    {
        // Copre lo schermo ma lascia passare il mouse: solo il pannello e' un elemento reale.
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        _panel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        _panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = PanelColor,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = 8,
            ContentMarginBottom = 8,
        });
        _panel.CustomMinimumSize = new Vector2(PanelWidth, PanelHeight);
        AddChild(_panel);

        // Ancorato all'angolo in basso a destra, sollevato di quanto occupa la hotbar.
        _panel.SetAnchorsPreset(LayoutPreset.BottomRight);
        _panel.OffsetLeft = -(PanelWidth + Margin);
        _panel.OffsetRight = -Margin;
        _panel.OffsetTop = -(PanelHeight + HotbarSlotView.SlotSize + 24);
        _panel.OffsetBottom = -(HotbarSlotView.SlotSize + 24);

        var column = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        column.AddThemeConstantOverride("separation", 4);
        _panel.AddChild(column);

        _healthBar = new ProgressBar
        {
            CustomMinimumSize = new Vector2(0, BarHeight),
            ShowPercentage = false,
            MinValue = 0,
            MaxValue = _health.MaxHealth,
            Value = _health.CurrentHealth,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _healthBar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = HealthBackColor });
        _healthBar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = HealthColor });
        column.AddChild(_healthBar);

        _weaponBlock = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        _weaponBlock.AddThemeConstantOverride("separation", 2);
        column.AddChild(_weaponBlock);

        _weaponLabel = new Label { MouseFilter = MouseFilterEnum.Ignore };
        _weaponLabel.AddThemeFontSizeOverride("font_size", 13);
        _weaponBlock.AddChild(_weaponLabel);

        _ammoLabel = new Label
        {
            MouseFilter = MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _ammoLabel.AddThemeFontSizeOverride("font_size", 22);
        _weaponBlock.AddChild(_ammoLabel);
    }

    public override void _Process(double delta)
    {
        _healthBar.MaxValue = _health.MaxHealth;
        _healthBar.Value = _health.CurrentHealth;

        WeaponDefinition? weapon = _weapon.HeldWeapon;
        _weaponBlock.Visible = weapon != null;
        if (weapon == null)
            return;

        _weaponLabel.Text = weapon.DisplayName;

        // L'override del colore si applica solo al cambio di stato: rifarlo ogni frame
        // scatenerebbe una notifica di tema inutile a 60 Hz.
        if (_weapon.Reloading != _reloadingShown)
        {
            _reloadingShown = _weapon.Reloading;
            if (_reloadingShown)
                _ammoLabel.AddThemeColorOverride("font_color", ReloadColor);
            else
                _ammoLabel.RemoveThemeColorOverride("font_color");
        }

        _ammoLabel.Text = _weapon.Reloading
            ? "RICARICA…"
            : $"{_weapon.MagazineAmmo} / {_weapon.ReserveAmmo}";
    }
}
