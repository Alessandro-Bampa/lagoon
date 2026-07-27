using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace Lagoon;

/// <summary>
/// Unico punto d'accesso alle traduzioni. Ogni stringa visibile all'utente passa da qui (o
/// dall'auto-translate dei <c>Control</c> nelle scene <c>.tscn</c>): nel codice non si scrive mai
/// testo naturale. Vedi la skill <c>i18n-localization</c> per la convenzione delle chiavi.
///
/// Statico e senza stato di proposito: serve anche a chi non e' un <c>Node</c> — in particolare a
/// <see cref="ItemDefinition"/>, che e' una <c>Resource</c> e risolve i propri nomi da chiave.
///
/// Le traduzioni sono registrate in <c>project.godot</c> (§ internationalization): i CSV di
/// <c>locales/</c> per le stringhe brevi, i <c>.po</c> per i dialoghi lunghi. Il fallback e'
/// l'inglese, quindi una chiave tradotta solo in EN resta leggibile in qualunque lingua.
/// </summary>
public static class Loc
{
    /// Chiavi gia' segnalate come mancanti: una traduzione assente in un _Process girerebbe
    /// altrimenti a 60 warning al secondo.
    private static readonly HashSet<string> ReportedMissing = new();

    /// <summary>
    /// Testo tradotto per <paramref name="key"/>. Se la chiave non esiste in nessuna traduzione
    /// registrata, <see cref="TranslationServer"/> restituisce la chiave stessa: la si mostra
    /// comunque (meglio di una stringa vuota) e in debug si segnala una volta sola.
    /// </summary>
    public static string T(string key)
    {
        if (string.IsNullOrEmpty(key))
            return "";

        if (TryT(key, out string text))
            return text;

        ReportMissing(key);
        return key;
    }

    /// <summary>
    /// Come <see cref="T(string)"/>, con sostituzione dei segnaposto posizionali <c>{0}</c>,
    /// <c>{1}</c>, ... Sono i segnaposto a rendere traducibile una frase composta: concatenare
    /// pezzi di testo fissa l'ordine delle parole dell'italiano su tutte le lingue.
    ///
    /// La formattazione usa <see cref="CultureInfo.InvariantCulture"/>: il separatore decimale dei
    /// pesi e delle percentuali non deve cambiare con la lingua della UI, altrimenti cambia anche
    /// la larghezza degli elementi che li contengono (la UI ha dimensioni in pixel fisse, skill ui-hud).
    /// </summary>
    public static string T(string key, params object[] args)
    {
        string pattern = T(key);
        if (args.Length == 0)
            return pattern;

        try
        {
            return string.Format(CultureInfo.InvariantCulture, pattern, args);
        }
        catch (System.FormatException)
        {
            // Traduzione con segnaposto sbagliati (es. "{0" o piu' argomenti di quelli previsti):
            // non far cadere la UI per un errore di dati.
            GD.PushWarning($"[Loc] Segnaposto non validi nella traduzione di '{key}': \"{pattern}\"");
            return pattern;
        }
    }

    /// <summary>
    /// Traduzione di una chiave OPZIONALE: ritorna false se la chiave non esiste, senza segnalare
    /// nulla. Serve ai testi che possono legittimamente mancare (la descrizione o l'effetto di un
    /// item che non ne ha uno), dove un warning sarebbe rumore e non un difetto.
    /// </summary>
    public static bool TryT(string key, out string text)
    {
        text = "";
        if (string.IsNullOrEmpty(key))
            return false;

        string translated = TranslationServer.Translate(key);
        // Convenzione di TranslationServer: chiave non trovata -> restituisce la chiave stessa.
        if (translated == key)
            return false;

        text = translated;
        return true;
    }

    /// Testo tradotto, oppure stringa vuota se la chiave non esiste (nessun warning).
    public static string TOrEmpty(string key) => TryT(key, out string text) ? text : "";

    /// <summary>
    /// Forma singolare/plurale: <c>Loc.N("UI_X_ONE", "UI_X_MANY", n)</c>. Le regole di pluralita'
    /// non coincidono fra lingue (l'inglese ne ha 2, altre fino a 6): delegarle a
    /// <see cref="TranslationServer"/> e' l'unico modo corretto, un <c>if (n == 1)</c> nel codice no.
    /// Richiede chiavi definite in un <c>.po</c> con <c>msgid_plural</c>.
    /// </summary>
    public static string N(string key, string pluralKey, int n)
        => TranslationServer.TranslatePlural(key, pluralKey, n);

    /// <summary>
    /// Etichetta del tasto associato a un'azione dell'InputMap, per i prompt del tipo "[F] Apri".
    /// Si legge dall'InputMap invece di scrivere la lettera nel testo: il giorno in cui un tasto
    /// cambia (o diventa riassegnabile) i prompt seguono da soli. Vedi la skill ui-hud §4.
    ///
    /// Ritorna "?" se l'azione non esiste o non ha eventi da tastiera/mouse associati.
    /// </summary>
    public static string KeyFor(string inputAction)
    {
        if (!InputMap.HasAction(inputAction))
            return "?";

        foreach (InputEvent inputEvent in InputMap.ActionGetEvents(inputAction))
        {
            switch (inputEvent)
            {
                case InputEventKey key:
                    // PhysicalKeycode: il progetto mappa i tasti per posizione fisica (WASD resta
                    // WASD su layout AZERTY), quindi l'etichetta va risolta da li'.
                    Key code = key.PhysicalKeycode != Key.None
                        ? DisplayServer.KeyboardGetKeycodeFromPhysical(key.PhysicalKeycode)
                        : key.Keycode;
                    if (code != Key.None)
                        return OS.GetKeycodeString(code);
                    break;

                case InputEventMouseButton mouse:
                    return T($"UI_MOUSE_{mouse.ButtonIndex.ToString().ToUpperInvariant()}");
            }
        }

        return "?";
    }

    /// <summary>
    /// Numero formattato in modo indipendente dalla lingua, per essere inserito in un segnaposto.
    /// <paramref name="format"/> usa la sintassi standard .NET (es. <c>"0.##"</c>).
    /// </summary>
    public static string Num(float value, string format = "0.##")
        => value.ToString(format, CultureInfo.InvariantCulture);

    private static void ReportMissing(string key)
    {
        if (!OS.IsDebugBuild() || !ReportedMissing.Add(key))
            return;

        GD.PushWarning($"[Loc] Chiave di traduzione mancante: '{key}' (locale={TranslationServer.GetLocale()})");
    }
}
