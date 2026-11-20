using System;
using Godot;

namespace Lagoon;

/// <summary>
/// Il banco di <see cref="WaveSpectrum"/> preparato per MOLTE letture vicine nello stesso istante (lo
/// scafo per triangoli: centinaia di vertici per passo). Stesso trucco dello shader (skill water-ocean
/// §1): la parte grande della fase, k·(D·origine) − ω·t + φ, si calcola UNA volta per onda in double e
/// si avvolge; per ogni punto resta k·(D·posizione locale), piccola, in float. Si saltano le onde
/// spente e l'esposizione e' quella dell'origine (la mappa e' sfocata su ~110 m: lungo uno scafo non
/// cambia). Stessa formula di <see cref="WaveSpectrum.Evaluate"/>: le onde restano quelle disegnate.
/// Nessuna allocazione dopo la costruzione.
/// </summary>
public sealed class WaveLocalFrame
{
    private readonly float[] _kx = new float[WaveSpectrum.MaxWaves];
    private readonly float[] _kz = new float[WaveSpectrum.MaxWaves];
    private readonly float[] _dirX = new float[WaveSpectrum.MaxWaves];
    private readonly float[] _dirZ = new float[WaveSpectrum.MaxWaves];
    private readonly float[] _k = new float[WaveSpectrum.MaxWaves];
    private readonly float[] _omega = new float[WaveSpectrum.MaxWaves];
    private readonly float[] _phase = new float[WaveSpectrum.MaxWaves];
    private readonly float[] _a = new float[WaveSpectrum.MaxWaves];
    private readonly float[] _h = new float[WaveSpectrum.MaxWaves];
    private int _count;

    /// Origine del riferimento locale (mondo, X/Z).
    public double OriginX { get; private set; }
    public double OriginZ { get; private set; }
    /// Esposizione usata per tutte le letture.
    public float Exposure { get; private set; }

    public void Prepare(WaveSpectrum spectrum, double originX, double originZ, double time, float exposure)
    {
        OriginX = originX;
        OriginZ = originZ;
        Exposure = exposure;
        _count = 0;
        for (int i = 0; i < spectrum.Count; i++)
        {
            spectrum.AmplitudesAt(i, exposure, out double a, out double h);
            if (a <= 0.0 && h <= 0.0)
                continue;
            ref readonly WaveSpectrum.Component c = ref spectrum.Components[i];
            int j = _count++;
            double p = spectrum.Phase(i, originX, originZ, time) % Math.Tau;
            _phase[j] = (float)(p < 0 ? p + Math.Tau : p);
            _kx[j] = (float)(c.K * c.DirX);
            _kz[j] = (float)(c.K * c.DirZ);
            _dirX[j] = (float)c.DirX;
            _dirZ[j] = (float)c.DirZ;
            _k[j] = (float)c.K;
            _omega[j] = (float)c.Omega;
            _a[j] = (float)a;
            _h[j] = (float)h;
        }
    }

    /// Spostamento orizzontale della particella che a riposo sta in (lx, lz), locali.
    public void Horizontal(float lx, float lz, out float dx, out float dz)
    {
        dx = 0f;
        dz = 0f;
        for (int i = 0; i < _count; i++)
        {
            float co = MathF.Cos(_kx[i] * lx + _kz[i] * lz + _phase[i]);
            dx += _h[i] * _dirX[i] * co;
            dz += _h[i] * _dirZ[i] * co;
        }
    }

    /// Come <see cref="WaveSpectrum.Evaluate"/>, nel riferimento locale.
    public void Evaluate(float lx, float lz, out Vector3 displacement, out Vector3 normal, out Vector3 velocity)
    {
        float dx = 0, dy = 0, dz = 0;
        float dpxdx = 1, dpxdz = 0, dpzdx = 0, dpzdz = 1, dydx = 0, dydz = 0;
        float vx = 0, vy = 0, vz = 0;
        for (int i = 0; i < _count; i++)
        {
            (float s, float co) = MathF.SinCos(_kx[i] * lx + _kz[i] * lz + _phase[i]);
            float a = _a[i], h = _h[i], dirX = _dirX[i], dirZ = _dirZ[i], k = _k[i], w = _omega[i];
            dy += a * s;
            dx += h * dirX * co;
            dz += h * dirZ * co;

            float hk = h * k * s;
            dpxdx -= hk * dirX * dirX;
            dpxdz -= hk * dirX * dirZ;
            dpzdx -= hk * dirX * dirZ;
            dpzdz -= hk * dirZ * dirZ;
            float ak = a * k * co;
            dydx += ak * dirX;
            dydz += ak * dirZ;

            vx += h * dirX * w * s;
            vz += h * dirZ * w * s;
            vy -= a * w * co;
        }
        displacement = new Vector3(dx, dy, dz);
        var tx = new Vector3(dpxdx, dydx, dpzdx);
        var tz = new Vector3(dpxdz, dydz, dpzdz);
        normal = tz.Cross(tx).Normalized();
        velocity = new Vector3(vx, vy, vz);
    }
}
