using System;
using Godot;

namespace Lagoon;

/// <summary>
/// Stato del mare, sulla scala Douglas italiana (accorpata). Il valore che gira nel gioco e' un
/// <c>float</c> continuo fra questi gradi (<see cref="WaterVolume.SeaState"/>): 2,5 sta fra mosso e
/// agitato, e un cambio di tempo e' una rampa, non uno scatto.
/// </summary>
public enum SeaState
{
    /// Douglas 0-1: specchio d'acqua, increspature.
    Calm = 0,
    /// Douglas 2 (poco mosso): onde fino a mezzo metro.
    Slight = 1,
    /// Douglas 3 (mosso): 0,5-1,25 m.
    Moderate = 2,
    /// Douglas 4-5 (molto mosso / agitato): 1,25-4 m.
    Rough = 3,
    /// Douglas 6+ (molto agitato, tempesta).
    Storm = 4,
}

/// <summary>
/// Banco di onde di Gerstner, IDENTICO su CPU (galleggiamento sull'host) e GPU (shader
/// <c>water.gdshader</c>). E' il contratto da cui dipende tutto il resto: se lo shader e questa classe
/// divergono, le barche galleggiano su onde diverse da quelle disegnate.
///
/// <para><b>Componenti fisse, ampiezze variabili.</b> Lunghezze d'onda, direzioni e fasi iniziali si
/// generano UNA volta dal seme e dalla direzione del vento e non cambiano mai: cambiare la lunghezza
/// d'onda di un'onda in corsa ne farebbe saltare la fase (lontano dall'origine, di migliaia di
/// radianti). Lo stato del mare sposta solo le AMPIEZZE lungo lo spettro: col mare calmo pesano le
/// onde corte, in tempesta quelle lunghe.</para>
///
/// <para><b>Due spettri.</b> Ogni onda ha un'ampiezza "mare aperto" e una "riparata" (laguna, corta e
/// limitata dal fetch). L'esposizione locale (<see cref="WaterMap"/>, 0..1) le mescola: 0 = acqua ferma
/// (rii), 0,5 = spettro riparato pieno, 1 = mare aperto. In laguna una tempesta fa onde corte e
/// ripide, in mare onde lunghe e alte: con un solo spettro scalato non verrebbe.</para>
///
/// <para><b>Precisione.</b> La fase si calcola in <c>double</c>: k·x + ω·t a 24 km e dopo ore di gioco
/// non sta in un float. Allo shader arriva una fase per onda gia' comprensiva dell'origine della
/// griglia e del tempo, avvolta in [0, 2π): la GPU fa solo k·(posizione locale), piccola.</para>
/// </summary>
public sealed class WaveSpectrum
{
    /// Deve coincidere con la dimensione degli array <c>wave_a</c>/<c>wave_b</c> dello shader.
    public const int MaxWaves = 12;

    public const float Gravity = 9.81f;

    /// Parametri di uno stato del mare per i due spettri.
    public readonly record struct SeaParams(
        float OpenHeight, float OpenPeakWavelength,
        float ShelteredHeight, float ShelteredPeakWavelength,
        float Choppiness, float DetailStrength, float CrestFoam);

    /// <summary>
    /// Un grado per ogni <see cref="SeaState"/>. Altezza = altezza significativa Hs (media del terzo
    /// piu' alto), in metri. Mare aperto: Adriatico settentrionale, mare corto (fetch limitato): i
    /// periodi di picco sono piu' brevi dell'oceano a parita' di Hs. Laguna: bassi fondali e fetch di
    /// pochi km, in bora forte si arriva a 0,6-0,9 m di onda corta.
    /// </summary>
    public static readonly SeaParams[] Presets =
    {
        //            Hs mare, λp mare, Hs laguna, λp laguna, Q,    dettaglio, schiuma creste
        new(0.06f,   4f,       0.03f,     3f,        0.30f, 0.20f,     0.00f), // calmo
        new(0.35f,   14f,      0.12f,     5f,        0.45f, 0.40f,     0.05f), // poco mosso
        new(0.90f,   32f,      0.30f,     9f,        0.60f, 0.60f,     0.25f), // mosso
        new(2.50f,   66f,      0.55f,     14f,       0.75f, 0.80f,     0.60f), // agitato
        new(5.00f,   110f,     0.90f,     20f,       0.85f, 1.00f,     0.95f), // tempesta
    };

    /// Una componente del banco: tutto costante, tranne le ampiezze (<see cref="OpenAmplitude"/> ...).
    public readonly struct Component
    {
        public readonly double DirX, DirZ, K, Omega, Phase0;
        public readonly float Wavelength;

        public Component(double dirX, double dirZ, float wavelength, double phase0)
        {
            DirX = dirX;
            DirZ = dirZ;
            Wavelength = wavelength;
            K = Math.Tau / wavelength;
            // Dispersione in acque profonde. In laguna l'acqua e' bassa (ω = sqrt(g k tanh(k h))), ma
            // una fase dipendente dalla profondita' non sarebbe piu' lineare nel tempo ne' uguale fra
            // punti vicini: si accetta l'errore, che per onde corte su 1,5 m e' piccolo.
            Omega = Math.Sqrt(Gravity * K);
            Phase0 = phase0;
        }
    }

    public readonly Component[] Components;

    /// Ampiezza verticale A e spostamento orizzontale H = Q·A, mare aperto e riparato.
    public readonly float[] OpenAmplitude = new float[MaxWaves];
    public readonly float[] OpenHorizontal = new float[MaxWaves];
    public readonly float[] ShelteredAmplitude = new float[MaxWaves];
    public readonly float[] ShelteredHorizontal = new float[MaxWaves];

    public int Count => Components.Length;

    /// Stato a cui sono state calcolate le ampiezze (-1 = mai).
    public float State { get; private set; } = -1f;

    /// Parametri interpolati dello stato corrente (dettaglio e schiuma li legge lo shader).
    public SeaParams Current { get; private set; }

    /// <param name="windFromDegrees">Da dove soffia il vento, gradi bussola (0 = nord, 90 = est):
    /// le onde viaggiano nel verso opposto. 135 = scirocco.</param>
    public WaveSpectrum(int seed, float windFromDegrees, int count = MaxWaves,
        float minWavelength = 3f, float maxWavelength = 160f)
    {
        count = Math.Clamp(count, 1, MaxWaves);
        var rng = new Random(seed);

        // Mondo: X est, Z sud. Il vento "da" θ (bussola) spinge le onde verso θ + 180°.
        double travel = (windFromDegrees + 180.0) * Math.PI / 180.0;
        double baseX = Math.Sin(travel);
        double baseZ = -Math.Cos(travel);
        double baseAngle = Math.Atan2(baseZ, baseX);

        Components = new Component[count];
        double ratio = count > 1 ? Math.Pow(maxWavelength / minWavelength, 1.0 / (count - 1)) : 1.0;
        for (int i = 0; i < count; i++)
        {
            float wavelength = (float)(minWavelength * Math.Pow(ratio, i));
            // Lo swell lungo arriva compatto dal vento, il mare corto e' piu' disordinato.
            double t = count > 1 ? (double)i / (count - 1) : 1.0;
            double spread = (55.0 - 40.0 * t) * Math.PI / 180.0;
            double angle = baseAngle + (rng.NextDouble() * 2.0 - 1.0) * spread;
            Components[i] = new Component(Math.Cos(angle), Math.Sin(angle), wavelength, rng.NextDouble() * Math.Tau);
        }

        SetSeaState(0f);
    }

    /// Interpola i <see cref="Presets"/> (λ di picco in scala logaritmica).
    public static SeaParams Interpolate(float state)
    {
        state = Math.Clamp(state, 0f, Presets.Length - 1);
        int i = Math.Min((int)state, Presets.Length - 2);
        float f = state - i;
        SeaParams a = Presets[i], b = Presets[i + 1];
        return new SeaParams(
            Mathf.Lerp(a.OpenHeight, b.OpenHeight, f),
            Mathf.Exp(Mathf.Lerp(Mathf.Log(a.OpenPeakWavelength), Mathf.Log(b.OpenPeakWavelength), f)),
            Mathf.Lerp(a.ShelteredHeight, b.ShelteredHeight, f),
            Mathf.Exp(Mathf.Lerp(Mathf.Log(a.ShelteredPeakWavelength), Mathf.Log(b.ShelteredPeakWavelength), f)),
            Mathf.Lerp(a.Choppiness, b.Choppiness, f),
            Mathf.Lerp(a.DetailStrength, b.DetailStrength, f),
            Mathf.Lerp(a.CrestFoam, b.CrestFoam, f));
    }

    /// Ricalcola le ampiezze per lo stato dato (12 onde: costa nulla, ma si salta se non cambia).
    public void SetSeaState(float state)
    {
        if (Math.Abs(state - State) < 1e-4f)
            return;
        State = state;
        Current = Interpolate(state);
        FillSpectrum(Current.OpenHeight, Current.OpenPeakWavelength, Current.Choppiness, OpenAmplitude, OpenHorizontal);
        FillSpectrum(Current.ShelteredHeight, Current.ShelteredPeakWavelength, Current.Choppiness,
            ShelteredAmplitude, ShelteredHorizontal);
    }

    /// <summary>
    /// Distribuisce Hs sulle componenti con una campana ASIMMETRICA in log(λ) attorno al picco (stretta
    /// verso le onde lunghe, larga verso le corte, come gli spettri misurati: sopra il picco l'energia
    /// cala in fretta, sotto resta una coda di mare corto e ripido), normalizzata in
    /// modo che 4·sqrt(Σ A²/2) = Hs. Poi due tetti: ripidita' k·A ≤ 0,25 per onda (oltre, un'onda vera
    /// frange) e Σ k·H ≤ 0,9 (oltre, Gerstner fa il cappio: la superficie si ripiega su se stessa).
    /// </summary>
    private void FillSpectrum(float hs, float peak, float choppiness, float[] amp, float[] hor)
    {
        const double sigmaLong = 0.35;
        const double sigmaShort = 1.0;
        double sumW2 = 0.0;
        Span<double> w = stackalloc double[MaxWaves];
        for (int i = 0; i < Count; i++)
        {
            double l = Math.Log(Components[i].Wavelength / peak);
            double sigma = l > 0 ? sigmaLong : sigmaShort;
            w[i] = Math.Exp(-l * l / (2.0 * sigma * sigma));
            sumW2 += w[i] * w[i];
        }

        double c = sumW2 > 0 ? hs / 4.0 * Math.Sqrt(2.0 / sumW2) : 0.0;
        double foldSum = 0.0;
        for (int i = 0; i < Count; i++)
        {
            double a = Math.Min(w[i] * c, 0.25 / Components[i].K);
            amp[i] = (float)a;
            hor[i] = (float)(choppiness * a);
            foldSum += Components[i].K * hor[i];
        }

        if (foldSum > 0.9)
        {
            float s = (float)(0.9 / foldSum);
            for (int i = 0; i < Count; i++)
                hor[i] *= s;
        }

        for (int i = Count; i < MaxWaves; i++)
            amp[i] = hor[i] = 0f;
    }

    /// <summary>
    /// Ampiezze effettive di un'onda all'esposizione data: stessa formula dello shader
    /// (<c>wave_amplitudes</c>). Sotto 0,5 lo spettro riparato si spegne verso l'acqua ferma, sopra
    /// sfuma verso il mare aperto.
    /// </summary>
    public void AmplitudesAt(int i, float exposure, out double a, out double h)
    {
        float sheltered = Math.Clamp(exposure * 2f, 0f, 1f);
        float open = Math.Clamp(exposure * 2f - 1f, 0f, 1f);
        a = Mathf.Lerp(ShelteredAmplitude[i] * sheltered, OpenAmplitude[i], open);
        h = Mathf.Lerp(ShelteredHorizontal[i] * sheltered, OpenHorizontal[i], open);
    }

    /// Fase di un'onda nel punto mondo (x, z) all'istante t: θ = k·(D·x) − ω·t + φ.
    public double Phase(int i, double x, double z, double time)
    {
        ref readonly Component c = ref Components[i];
        return c.K * (c.DirX * x + c.DirZ * z) - c.Omega * time + c.Phase0;
    }

    /// <summary>
    /// Fase da passare allo shader per una griglia centrata in (originX, originZ): la parte grande
    /// di k·x e ω·t, calcolata in double e avvolta in [0, 2π).
    /// </summary>
    public float ShaderPhase(int i, double originX, double originZ, double time)
    {
        double p = Phase(i, originX, originZ, time) % Math.Tau;
        return (float)(p < 0 ? p + Math.Tau : p);
    }

    /// <summary>
    /// Spostamento di Gerstner della particella che a riposo sta in (x0, z0) — non del punto (x0, z0)
    /// della superficie spostata: per quello serve <see cref="WaterVolume.Sample"/>, che inverte lo
    /// spostamento orizzontale. Restituisce anche le derivate (per la normale) e la velocita' della
    /// particella (per il trascinamento: un corpo fermo fra le onde viene spinto avanti e indietro).
    /// </summary>
    public void Evaluate(double x0, double z0, double time, float exposure,
        out Vector3 displacement, out Vector3 normal, out Vector3 velocity)
    {
        double dx = 0, dy = 0, dz = 0;
        double dpxdx = 1, dpxdz = 0, dpzdx = 0, dpzdz = 1, dydx = 0, dydz = 0;
        double vx = 0, vy = 0, vz = 0;

        for (int i = 0; i < Count; i++)
        {
            AmplitudesAt(i, exposure, out double a, out double h);
            if (a <= 0.0 && h <= 0.0)
                continue;

            ref readonly Component c = ref Components[i];
            double th = Phase(i, x0, z0, time);
            double s = Math.Sin(th), co = Math.Cos(th);

            dy += a * s;
            dx += h * c.DirX * co;
            dz += h * c.DirZ * co;

            double hk = h * c.K * s;
            dpxdx -= hk * c.DirX * c.DirX;
            dpxdz -= hk * c.DirX * c.DirZ;
            dpzdx -= hk * c.DirX * c.DirZ;
            dpzdz -= hk * c.DirZ * c.DirZ;
            double ak = a * c.K * co;
            dydx += ak * c.DirX;
            dydz += ak * c.DirZ;

            // d/dt di (h·D·cos θ, a·sin θ) con dθ/dt = −ω.
            vx += h * c.DirX * c.Omega * s;
            vz += h * c.DirZ * c.Omega * s;
            vy -= a * c.Omega * co;
        }

        displacement = new Vector3((float)dx, (float)dy, (float)dz);
        var tx = new Vector3((float)dpxdx, (float)dydx, (float)dpzdx);
        var tz = new Vector3((float)dpxdz, (float)dydz, (float)dpzdz);
        normal = tz.Cross(tx).Normalized();
        velocity = new Vector3((float)vx, (float)vy, (float)vz);
    }

    /// Solo lo spostamento orizzontale (per l'inversione a punto fisso di <see cref="WaterVolume.Sample"/>).
    public void Horizontal(double x0, double z0, double time, float exposure, out double dx, out double dz)
    {
        dx = 0;
        dz = 0;
        for (int i = 0; i < Count; i++)
        {
            AmplitudesAt(i, exposure, out _, out double h);
            if (h <= 0.0)
                continue;
            ref readonly Component c = ref Components[i];
            double co = Math.Cos(Phase(i, x0, z0, time));
            dx += h * c.DirX * co;
            dz += h * c.DirZ * co;
        }
    }
}
