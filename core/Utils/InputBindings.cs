using System.Collections.Generic;
using Godot;

namespace Lagoon;

/// <summary>
/// Rimappatura dei comandi: elenco delle azioni riassegnabili, snapshot dei binding di progetto e
/// (de)serializzazione compatta di un evento di input.
///
/// Non possiede stato di gioco: lo stato "quale tasto fa cosa" vive nell'<c>InputMap</c> di Godot, e
/// gli scostamenti dai valori di progetto sono persistiti da <see cref="SettingsService"/> (unico
/// proprietario delle impostazioni, skill ui-hud §3). Qui restano solo i default catturati all'avvio,
/// che servono per il "ripristina predefiniti" e per capire quali binding vanno salvati.
///
/// <b>Scelte dichiarate</b>
/// <list type="bullet">
/// <item>Una riassegnazione sostituisce <b>tutti</b> gli eventi dell'azione con quello scelto: le
/// alternative dichiarate in <c>project.godot</c> (frecce direzionali oltre a WASD) si perdono fino
/// al ripristino dei predefiniti. E' l'unico comportamento prevedibile con una sola riga per azione.</item>
/// <item>Si accettano solo tasti e i tre pulsanti principali del mouse: e' esattamente cio' che
/// <see cref="Loc.KeyFor"/> sa etichettare (nessun supporto gamepad nel progetto).</item>
/// <item>I tasti si salvano per <b>posizione fisica</b> (<c>PhysicalKeycode</c>), come i binding di
/// progetto: WASD resta WASD su un layout AZERTY.</item>
/// <item><c>toggle_menu</c> non e' riassegnabile: Esc e' il tasto con cui si annulla la cattura di
/// un nuovo binding, quindi deve restare raggiungibile in ogni configurazione.</item>
/// <item>I doppioni sono <b>permessi</b>, non corretti d'ufficio: il progetto ne ha di voluti (R e'
/// sia <c>rotate_item</c> sia <c>reload</c>, disambiguati dalla pipeline di input — skill ui-hud §4).
/// La UI segnala il conflitto e lascia decidere al giocatore.</item>
/// </list>
/// </summary>
public static class InputBindings
{
    /// Un gruppo di azioni nella schermata dei comandi: titolo tradotto + azioni, nell'ordine mostrato.
    public readonly record struct ActionGroup(string TitleKey, string[] Actions);

    public static readonly ActionGroup[] Groups =
    {
        new("UI_BIND_GROUP_MOVEMENT", new[]
        {
            "move_up", "move_down", "move_left", "move_right", "jump", "sprint", "crouch",
            "camera_rotate_left", "camera_rotate_right",
        }),
        new("UI_BIND_GROUP_COMBAT", new[]
        {
            "fire", "aim", "reload", "weapon_slot_1", "weapon_slot_2", "weapon_slot_3",
        }),
        new("UI_BIND_GROUP_INVENTORY", new[]
        {
            "toggle_inventory", "interact", "rotate_item", "quick_drop",
            "quick_slot_4", "quick_slot_5", "quick_slot_6",
            "quick_slot_7", "quick_slot_8", "quick_slot_9", "quick_slot_0",
        }),
    };

    /// Eventi dichiarati in <c>project.godot</c>, catturati prima di applicare qualunque override.
    private static readonly Dictionary<string, List<InputEvent>> Defaults = new();

    private static bool _captured;

    /// <summary>
    /// Fotografa i binding di progetto. Va chiamata una sola volta all'avvio, <b>prima</b> di
    /// applicare gli override salvati: dopo non sarebbero piu' i predefiniti.
    /// </summary>
    public static void CaptureDefaults()
    {
        if (_captured)
            return;

        _captured = true;
        foreach (ActionGroup group in Groups)
        foreach (string action in group.Actions)
        {
            var events = new List<InputEvent>();
            if (InputMap.HasAction(action))
                foreach (InputEvent inputEvent in InputMap.ActionGetEvents(action))
                    events.Add(inputEvent);
            else
                GD.PushWarning($"[InputBindings] Azione riassegnabile inesistente nell'InputMap: '{action}'");

            Defaults[action] = events;
        }
    }

    /// Tutte le azioni riassegnabili, nell'ordine dei gruppi.
    public static IEnumerable<string> AllActions()
    {
        foreach (ActionGroup group in Groups)
        foreach (string action in group.Actions)
            yield return action;
    }

    /// Chiave di traduzione dell'etichetta di un'azione (convenzione, vedi skill i18n-localization §2).
    public static string LabelKey(string action) => $"UI_ACTION_{action.ToUpperInvariant()}";

    // ====================================================================================
    //  Applicazione
    // ====================================================================================

    /// Rimette l'azione ai suoi eventi di progetto.
    public static void RestoreDefault(string action)
    {
        if (!InputMap.HasAction(action) || !Defaults.TryGetValue(action, out List<InputEvent>? events))
            return;

        InputMap.ActionEraseEvents(action);
        foreach (InputEvent inputEvent in events)
            InputMap.ActionAddEvent(action, inputEvent);
    }

    /// Rimette tutte le azioni riassegnabili ai loro eventi di progetto.
    public static void RestoreAllDefaults()
    {
        foreach (string action in AllActions())
            RestoreDefault(action);
    }

    /// Assegna all'azione un solo evento, sostituendo quelli presenti.
    public static void Assign(string action, InputEvent inputEvent)
    {
        if (!InputMap.HasAction(action))
            return;

        InputMap.ActionEraseEvents(action);
        InputMap.ActionAddEvent(action, inputEvent);
    }

    /// <summary>
    /// Normalizza un evento appena catturato nella forma minima che si sa salvare: solo tasto o solo
    /// pulsante del mouse, senza modificatori (Shift tenuto mentre si preme F assegna F, non Shift+F).
    /// Ritorna null se l'evento non e' assegnabile.
    /// </summary>
    public static InputEvent? Normalize(InputEvent inputEvent)
    {
        switch (inputEvent)
        {
            case InputEventKey key:
                Key physical = key.PhysicalKeycode != Key.None
                    ? key.PhysicalKeycode
                    : DisplayServer.KeyboardGetKeycodeFromPhysical(key.Keycode);
                return physical == Key.None ? null : new InputEventKey { PhysicalKeycode = physical };

            case InputEventMouseButton mouse when IsAssignableButton(mouse.ButtonIndex):
                return new InputEventMouseButton { ButtonIndex = mouse.ButtonIndex };

            default:
                return null;
        }
    }

    /// Solo i pulsanti che <see cref="Loc.KeyFor"/> sa etichettare: la rotella non e' un binding.
    private static bool IsAssignableButton(MouseButton button)
        => button is MouseButton.Left or MouseButton.Right or MouseButton.Middle;

    /// <summary>
    /// Prima azione riassegnabile, diversa da <paramref name="ignoredAction"/>, che risponde gia' a
    /// questo evento. Null se non ce ne sono.
    /// </summary>
    public static string? FindConflict(InputEvent inputEvent, string ignoredAction)
    {
        foreach (string action in AllActions())
        {
            if (action == ignoredAction || !InputMap.HasAction(action))
                continue;

            if (InputMap.ActionHasEvent(action, inputEvent))
                return action;
        }

        return null;
    }

    // ====================================================================================
    //  Persistenza
    // ====================================================================================

    /// <summary>
    /// Forma testuale compatta di un evento (<c>"key:87"</c>, <c>"mouse:2"</c>). Si salva una stringa
    /// e non l'oggetto perche' il file di configurazione resta leggibile e correggibile a mano, e non
    /// dipende dalla serializzazione degli <c>Object</c> dentro un <c>ConfigFile</c>.
    /// </summary>
    public static string? Serialize(InputEvent inputEvent) => inputEvent switch
    {
        InputEventKey key when key.PhysicalKeycode != Key.None => $"key:{(long)key.PhysicalKeycode}",
        InputEventMouseButton mouse => $"mouse:{(int)mouse.ButtonIndex}",
        _ => null,
    };

    /// Inversa di <see cref="Serialize"/>. Null se la stringa e' malformata (file modificato a mano).
    public static InputEvent? Deserialize(string text)
    {
        string[] parts = text.Split(':', 2);
        if (parts.Length != 2 || !long.TryParse(parts[1], out long value))
            return null;

        return parts[0] switch
        {
            "key" => new InputEventKey { PhysicalKeycode = (Key)value },
            "mouse" when IsAssignableButton((MouseButton)value) =>
                new InputEventMouseButton { ButtonIndex = (MouseButton)value },
            _ => null,
        };
    }

    /// <summary>
    /// True se l'azione e' oggi legata a un solo evento diverso dai suoi predefiniti: e' la condizione
    /// per cui vale la pena scriverla nel file di configurazione.
    /// </summary>
    public static bool DiffersFromDefault(string action)
    {
        if (!InputMap.HasAction(action) || !Defaults.TryGetValue(action, out List<InputEvent>? defaults))
            return false;

        Godot.Collections.Array<InputEvent> current = InputMap.ActionGetEvents(action);
        if (current.Count != defaults.Count)
            return true;

        for (int i = 0; i < current.Count; i++)
            if (Serialize(current[i]) != Serialize(defaults[i]))
                return true;

        return false;
    }

    /// Primo evento associato all'azione, o null se non ne ha.
    public static InputEvent? PrimaryEvent(string action)
    {
        if (!InputMap.HasAction(action))
            return null;

        Godot.Collections.Array<InputEvent> events = InputMap.ActionGetEvents(action);
        return events.Count > 0 ? events[0] : null;
    }
}
