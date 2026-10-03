using GigaPisar.App;
using Xunit;

namespace GigaPisar.Tests;

/// <summary>The adaptive level meter behind the overlay's bars.</summary>
public class MeterTests
{
    [Fact]
    public void FollowsSpeechAndIgnoresTheSilentFirstPacket()
    {
        var recorder = new Recorder();
        var append = RecorderInternals.Append(recorder);
        var rng = new Random(1);
        var levels = new List<(double Time, float Level)>();
        double time = 0;
        int packets = 0, phase = 0;

        void Packet(float[] samples)
        {
            append(samples);
            time += samples.Length / 16000.0;
            if (++packets % 3 == 0) levels.Add((time, recorder.Level));   // the overlay reads about every 33 ms
        }
        float[] Room() => Enumerable.Range(0, 160).Select(_ => (float)(0.001 * Gauss(rng))).ToArray();   // -60 dBFS
        float[] Voice() => Enumerable.Range(0, 160).Select(_ => (float)(0.1414 * Math.Sin(2 * Math.PI * 300 * phase++ / 16000))).ToArray();   // -20 dBFS
        double Mean(double from, double to) => levels.Where(l => l.Time > from && l.Time <= to).Average(l => l.Level);

        Packet(new float[160]);   // WASAPI often flags the first packet after Start as silent: all zeros
        for (int i = 0; i < 200; i++) Packet(Room());
        for (int i = 0; i < 100; i++) Packet(Voice());
        for (int i = 0; i < 100; i++) Packet(Room());

        Assert.InRange(Mean(0.3, 2.0), 0.0, 0.25);    // room noise: low bars, not pinned high by the zero packet
        Assert.InRange(Mean(2.05, 3.0), 0.7, 1.0);    // speech fills them
        Assert.InRange(Mean(3.5, 4.0), 0.0, 0.25);    // and they drop back in the pause
    }

    [Fact]
    public void SamplesAreClampedSoTakePeakStaysWithinOne()
    {
        var recorder = new Recorder();
        RecorderInternals.Append(recorder)([1.5f, -2f]);
        Assert.Equal(1f, recorder.TakePeak);
    }

    private static double Gauss(Random r) => Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());
}
