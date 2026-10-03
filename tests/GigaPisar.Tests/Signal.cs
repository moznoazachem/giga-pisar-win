namespace GigaPisar.Tests;

/// <summary>Test signals in the sample formats WASAPI delivers, and measurements of what comes out at 16 kHz.</summary>
internal static class Signal
{
    /// <summary>Interleaved frames of a sine; channel c carries gains[c] times it.</summary>
    public static float[] Tone(int rate, double freq, double amp, double seconds, params double[] gains)
    {
        int frames = (int)(rate * seconds), channels = gains.Length;
        var x = new float[frames * channels];
        for (int i = 0; i < frames; i++)
            for (int c = 0; c < channels; c++) x[i * channels + c] = (float)(gains[c] * amp * Math.Sin(2 * Math.PI * freq * i / rate));
        return x;
    }

    public static byte[] Encode(float[] samples, string kind) => kind switch
    {
        "pcm16" => samples.SelectMany(v => BitConverter.GetBytes((short)Math.Round(v * 32767))).ToArray(),
        "pcm24" => samples.SelectMany(v => { int i = (int)Math.Round(v * 8388607); return new[] { (byte)i, (byte)(i >> 8), (byte)(i >> 16) }; }).ToArray(),
        "pcm32" => samples.SelectMany(v => BitConverter.GetBytes((int)Math.Round(v * 2147483647.0))).ToArray(),
        "float32" => samples.SelectMany(BitConverter.GetBytes).ToArray(),
        _ => throw new ArgumentException(kind),
    };

    /// <summary>Feeds the bytes to the converter in packets of so many frames and collects the output.</summary>
    public static float[] Run(RecorderInternals.ConvertFn convert, byte[] data, int frameBytes, int packetFrames)
    {
        var output = new List<float>();
        for (int at = 0; at < data.Length; at += packetFrames * frameBytes)
            output.AddRange(convert(data.AsSpan(at, Math.Min(packetFrames * frameBytes, data.Length - at))).ToArray());
        return output.ToArray();
    }

    /// <summary>The freq component of a 16 kHz signal from sample skip on: its amplitude, and the
    /// largest deviation of the signal from that sine (a glitch at a packet boundary shows here).</summary>
    public static (double Amplitude, double MaxDeviation) Fit(float[] x, double freq, int skip)
    {
        int n = x.Length - skip;
        double s = 0, c = 0;
        for (int i = skip; i < x.Length; i++)
        {
            double w = 2 * Math.PI * freq * i / 16000;
            s += x[i] * Math.Sin(w);
            c += x[i] * Math.Cos(w);
        }
        s *= 2.0 / n;
        c *= 2.0 / n;
        double worst = 0;
        for (int i = skip; i < x.Length; i++)
        {
            double w = 2 * Math.PI * freq * i / 16000;
            worst = Math.Max(worst, Math.Abs(x[i] - (s * Math.Sin(w) + c * Math.Cos(w))));
        }
        return (Math.Sqrt(s * s + c * c), worst);
    }

    public static double Rms(float[] x, int skip) => Math.Sqrt(x.Skip(skip).Average(v => (double)v * v));
}
