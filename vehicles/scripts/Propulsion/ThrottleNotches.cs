using Godot;

namespace Lagoon;

/// <summary>
/// Manetta a tacche del pilota: W/S spostano la leva di una tacca, che resta dov'e' finche' non la si
/// muove di nuovo (skill vehicles-boats §12). Tacche: R2 · R1 · N · 1 · 2 · 3 · 4.
///
/// La tacca e' solo un modo di produrre la leva continua −1..1 che l'host gia' valida (CLAUDE.md §3:
/// e' un intento, la posizione della manetta): l'host non conosce le tacche, quindi collaudi e
/// <c>DebugThrottle</c> possono ancora chiedere una leva qualunque.
///
/// Scelte (assunzione dichiarata): 4 marce avanti, dal minimo in marcia (manovra) a tutta forza
/// (crociera veloce); 2 indietro, il minimo per manovrare in porto e "indietro tutta" per frenare.
/// </summary>
public static class ThrottleNotches
{
    public const int MinNotch = -2;
    public const int MaxNotch = 4;

    /// <summary>
    /// Richiesta al motore (0 = minimo in marcia, 1 = tutta forza) per ogni tacca avanti, 1..4. Benzina:
    /// apertura della farfalla; diesel: frazione fra minimo e giri nominali.
    /// </summary>
    private static readonly float[] AheadDemand = { 0f, 0.35f, 0.65f, 1f };

    /// Richiesta per ogni tacca indietro, R1..R2.
    private static readonly float[] AsternDemand = { 0f, 1f };

    /// Leva −1..1 corrispondente alla tacca (0 = folle). Fuori dall'intervallo si satura.
    public static float Lever(int notch)
    {
        notch = Mathf.Clamp(notch, MinNotch, MaxNotch);
        if (notch == 0)
            return 0f;
        float demand = notch > 0 ? AheadDemand[notch - 1] : AsternDemand[-notch - 1];
        return MarineEngine.LeverFor(notch > 0 ? 1 : -1, demand);
    }

    /// Tacca piu' vicina a una leva (per la resa: la leva replicata del pilota).
    public static int FromLever(float lever)
    {
        int best = 0;
        float bestErr = float.MaxValue;
        for (int n = MinNotch; n <= MaxNotch; n++)
        {
            float err = Mathf.Abs(Lever(n) - lever);
            if (err < bestErr)
            {
                bestErr = err;
                best = n;
            }
        }
        return best;
    }

    /// Nome breve della tacca: "R2", "R1", "N", "1".."4". Simboli di strumento, non testo da tradurre.
    public static string Label(int notch) => notch switch // i18n-ignore
    {
        0 => "N", // i18n-ignore
        < 0 => "R" + (-notch), // i18n-ignore
        _ => notch.ToString(),
    };
}
