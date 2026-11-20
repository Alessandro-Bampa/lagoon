using Godot;

namespace Lagoon;

/// <summary>
/// Controlli di COLLAUDO del mare, per i livelli di prova (<c>WaveTestLevel.tscn</c>): PagSu/PagGiu'
/// cambiano lo stato del mare, Home passa da laguna (esposizione 0,5) a mare aperto (1). Una riga in
/// alto a sinistra mostra stato, livello e tasti.
///
/// Agisce solo sull'host (<see cref="WaterVolume.SetSeaState"/> non fa nulla sui client, e
/// l'esposizione di default non e' replicata: cambiarla su un client cambierebbe solo il suo disegno,
/// quindi li' il tasto e' ignorato). Le azioni <c>debug_sea_*</c> stanno nell'InputMap ma non in
/// <c>InputBindings.Groups</c>: non si riassegnano, non sono comandi di gioco.
/// </summary>
public partial class SeaStateDebugControls : Node
{
    [Export] public NodePath WaterPath { get; set; } = "../Water";

    /// Secondi di rampa fra uno stato e il successivo (una mareggiata vera monta in ore).
    [Export] public float RampSeconds { get; set; } = 4f;

    private WaterVolume? _water;
    private Label _label = null!;
    private float _target;

    public override void _Ready()
    {
        _water = GetNodeOrNull<WaterVolume>(WaterPath);
        _target = _water?.SeaState ?? 0f;

        var layer = new CanvasLayer { Layer = 5 };
        AddChild(layer);
        _label = new Label
        {
            Position = new Vector2(16, 12),
            AutoTranslateMode = AutoTranslateModeEnum.Disabled,
        };
        _label.AddThemeColorOverride("font_outline_color", Colors.Black);
        _label.AddThemeConstantOverride("outline_size", 6);
        layer.AddChild(_label);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_water == null || !_water.IsMultiplayerAuthority())
            return;

        if (@event.IsActionPressed("debug_sea_up"))
            _target = Mathf.Min(Mathf.Round(_target) + 1f, (float)SeaState.Storm);
        else if (@event.IsActionPressed("debug_sea_down"))
            _target = Mathf.Max(Mathf.Round(_target) - 1f, 0f);
        else if (@event.IsActionPressed("debug_sea_exposure"))
            _water.DefaultExposure = _water.DefaultExposure > 0.75f ? 0.5f : 1.0f;
        else
            return;

        _water.SetSeaState(_target, RampSeconds);
        GetViewport().SetInputAsHandled();
    }

    /// <summary>
    /// Riga della barca guidata dal giocatore locale (vuota se non e' al timone), dallo stato
    /// REPLICATO: velocita', giri, marcia, trim, assetto, sbandamento. Vale anche sui client.
    /// </summary>
    private string BoatLine()
    {
        int peer = Multiplayer.MultiplayerPeer != null ? Multiplayer.GetUniqueId() : NetworkConstants.HostPeerId;
        BoatController? boat = VehicleRegistry.FindByPilot(this, peer);
        if (boat == null)
            return "";
        Basis b = boat.GlobalBasis;
        float pitch = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(-b.Z.Y, -1f, 1f)));
        float heel = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(-b.X.Y, -1f, 1f)));
        float knots = new Vector2(boat.SyncLinearVelocity.X, boat.SyncLinearVelocity.Z).Length() / 0.514444f;
        // Tacca della manetta dalla leva replicata; tra parentesi il verso dell'elica, che durante il
        // cambio resta in folle.
        string prop = boat.SyncPropRpm > 1f ? "F" : boat.SyncPropRpm < -1f ? "R" : "N"; // i18n-ignore
        string gear = ThrottleNotches.Label(ThrottleNotches.FromLever(boat.SyncThrottle)) + " (" + prop + ")"; // i18n-ignore
        return Loc.T("UI_DEBUG_BOAT",
            Loc.Num(knots, "0.0"),
            Loc.Num(boat.SyncEngineRpm, "0"),
            gear,
            Loc.Num(Mathf.RadToDeg(boat.SyncTrim), "+0.0;-0.0"),
            Loc.Num(pitch, "+0.0;-0.0"),
            Loc.Num(heel, "+0.0;-0.0"),
            Loc.KeyFor("vehicle_trim_down"),
            Loc.KeyFor("vehicle_trim_up"),
            Loc.KeyFor("vehicle_thruster_left"),
            Loc.KeyFor("vehicle_thruster_right"),
            Loc.KeyFor("move_down"),
            Loc.KeyFor("move_up")) + "\n";
    }

    public override void _Process(double delta)
    {
        if (_water == null)
            return;

        var state = (SeaState)Mathf.Clamp(Mathf.RoundToInt(_water.SeaState), 0, (int)SeaState.Storm);
        string exposure = _water.DefaultExposure > 0.75f ? "UI_DEBUG_SEA_OPEN" : "UI_DEBUG_SEA_LAGOON";
        string boatLine = BoatLine();
        _label.Text = boatLine + Loc.T("UI_DEBUG_SEA",
            Loc.T("SEA_STATE_" + state.ToString().ToUpperInvariant()),
            Loc.Num(_water.SeaState, "0.0"),
            Loc.Num(_water.WaterLevel, "+0.00;-0.00"),
            Loc.T(exposure),
            Loc.KeyFor("debug_sea_up"),
            Loc.KeyFor("debug_sea_down"),
            Loc.KeyFor("debug_sea_exposure"));
    }
}
