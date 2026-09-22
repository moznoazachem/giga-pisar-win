// Waveform -> log-mel spectrogram, exactly what the encoder expects.
//
// This is the most fragile part of the pipeline: any deviation from the
// original torchaudio feature extractor yields garbage, not "slightly worse"
// text. The math mirrors giga_core.py (Python) and Features.swift (macOS)
// step by step.
//
// The Fourier transform is done as a plain dot product against precomputed
// cosine/sine tables: a 320-sample window is not a power of two and the
// sizes are small enough that a straight matmul is fast.

using System.Numerics;

namespace GigaPisar.Core;

public sealed class Features
{
    public FeatureConfig Config { get; }

    private readonly int _nFreqs;
    private readonly float[] _window;   // [nFft]
    private readonly float[] _cos;      // [nFreqs][nFft], row per frequency bin
    private readonly float[] _sin;      // [nFreqs][nFft]
    private readonly float[] _fb;       // [nMels][nFreqs], row per mel band

    public Features(FeatureConfig cfg)
    {
        Config = cfg;
        _nFreqs = cfg.NFft / 2 + 1;

        // Periodic Hann window, same as torch.hann_window(periodic=True).
        _window = new float[cfg.WinLength];
        for (int i = 0; i < cfg.WinLength; i++)
            _window[i] = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / cfg.WinLength));

        _cos = new float[_nFreqs * cfg.NFft];
        _sin = new float[_nFreqs * cfg.NFft];
        for (int k = 0; k < _nFreqs; k++)
        {
            for (int n = 0; n < cfg.NFft; n++)
            {
                double a = 2.0 * Math.PI * k * n / cfg.NFft;
                _cos[k * cfg.NFft + n] = (float)Math.Cos(a);
                _sin[k * cfg.NFft + n] = (float)(-Math.Sin(a));
            }
        }

        _fb = MelFilterbank(_nFreqs, 0.0, cfg.SampleRate / 2.0, cfg.NMels, cfg.SampleRate);
    }

    /// <summary>Number of frames the model will see for a given sample count.</summary>
    public int OutLen(int samples) =>
        Config.Center ? samples / Config.HopLength + 1
                      : (samples - Config.WinLength) / Config.HopLength + 1;

    private static double HzToMel(double f) => 2595.0 * Math.Log10(1.0 + f / 700.0);
    private static double MelToHz(double m) => 700.0 * (Math.Pow(10.0, m / 2595.0) - 1.0);

    /// <summary>Triangular filters as torchaudio.functional.melscale_fbanks(norm=None), transposed to [nMels][nFreqs].</summary>
    private static float[] MelFilterbank(int nFreqs, double fMin, double fMax, int nMels, int sampleRate)
    {
        double top = sampleRate / 2.0;
        var allFreqs = new double[nFreqs];
        for (int i = 0; i < nFreqs; i++) allFreqs[i] = top * i / (nFreqs - 1);

        double mMin = HzToMel(fMin), mMax = HzToMel(fMax);
        var fPts = new double[nMels + 2];
        for (int i = 0; i < nMels + 2; i++)
            fPts[i] = MelToHz(mMin + (mMax - mMin) * i / (nMels + 1));

        var fDiff = new double[nMels + 1];
        for (int i = 0; i < nMels + 1; i++) fDiff[i] = fPts[i + 1] - fPts[i];

        var fb = new float[nMels * nFreqs];
        for (int m = 0; m < nMels; m++)
        {
            for (int i = 0; i < nFreqs; i++)
            {
                double down = -(fPts[m] - allFreqs[i]) / fDiff[m];
                double up = (fPts[m + 2] - allFreqs[i]) / fDiff[m + 1];
                fb[m * nFreqs + i] = (float)Math.Max(0.0, Math.Min(down, up));
            }
        }
        return fb;
    }

    /// <summary>
    /// Waveform -> features laid out as [nMels, frames] row-major (the encoder
    /// input shape is [1, nMels, frames]). Returns the frame count.
    /// </summary>
    public (float[] values, int frames) Compute(ReadOnlySpan<float> wave)
    {
        var cfg = Config;
        float[] x;
        if (cfg.Center)
        {
            int pad = cfg.NFft / 2;
            x = new float[wave.Length + 2 * pad];
            for (int i = 0; i < pad; i++) x[i] = wave[Math.Min(pad - i, wave.Length - 1)];
            wave.CopyTo(x.AsSpan(pad));
            for (int i = 0; i < pad; i++) x[pad + wave.Length + i] = wave[Math.Max(0, wave.Length - 2 - i)];
        }
        else
        {
            x = wave.ToArray();
        }

        int n = Math.Max(0, (x.Length - cfg.NFft) / cfg.HopLength + 1);
        if (n == 0) return (Array.Empty<float>(), 0);

        var frame = new float[cfg.NFft];
        var power = new float[_nFreqs];
        var outp = new float[cfg.NMels * n];

        for (int f = 0; f < n; f++)
        {
            int off = f * cfg.HopLength;
            for (int j = 0; j < cfg.NFft; j++) frame[j] = x[off + j] * _window[j];

            for (int k = 0; k < _nFreqs; k++)
            {
                var row = k * cfg.NFft;
                float re = Dot(frame, _cos.AsSpan(row, cfg.NFft));
                float im = Dot(frame, _sin.AsSpan(row, cfg.NFft));
                power[k] = re * re + im * im;
            }

            for (int m = 0; m < cfg.NMels; m++)
            {
                float mel = Dot(power, _fb.AsSpan(m * _nFreqs, _nFreqs));
                mel = Math.Clamp(mel, 1e-9f, 1e9f);
                outp[m * n + f] = MathF.Log(mel);
            }
        }
        return (outp, n);
    }

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int n = a.Length, i = 0, w = Vector<float>.Count;
        var acc = Vector<float>.Zero;
        for (; i <= n - w; i += w)
            acc += new Vector<float>(a.Slice(i, w)) * new Vector<float>(b.Slice(i, w));
        float s = Vector.Sum(acc);
        for (; i < n; i++) s += a[i] * b[i];
        return s;
    }
}
