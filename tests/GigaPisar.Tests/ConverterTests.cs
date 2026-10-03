using NAudio.Wave;
using Xunit;
using static GigaPisar.Tests.Signal;

namespace GigaPisar.Tests;

/// <summary>Recorder.Converter: WASAPI packets in any format to 16 kHz mono.</summary>
public class ConverterTests
{
    // Shared-mode mix formats come as WAVEFORMATEXTENSIBLE: here 32-bit float, front left and right.
    private static readonly WaveFormat Float48kStereo = new WaveFormatExtensible(48000, 32, 2, true, 32, 3);

    [Fact]
    public void EngineConvertedStreamIsOnlyScaled()
    {
        var y = RecorderInternals.Converter(new WaveFormat(16000, 16, 1))(Encode([0f, 0.5f, -1f, 0.25f], "pcm16")).ToArray();
        AssertClose([0f, 0.5f, -1f, 0.25f], y, 1e-4);
    }

    [Fact]
    public void Pcm24IsSignExtendedAndChannelsAreAveraged()
    {
        var y = RecorderInternals.Converter(new WaveFormat(16000, 24, 2))(Encode([0.5f, -0.25f, -0.5f, 0.75f], "pcm24")).ToArray();
        AssertClose([0.125f, 0.125f], y, 1e-6);
    }

    [Fact]
    public void Pcm32IsScaled()
    {
        var y = RecorderInternals.Converter(new WaveFormat(16000, 32, 1))(Encode([0.75f, -0.5f], "pcm32")).ToArray();
        AssertClose([0.75f, -0.5f], y, 1e-6);
    }

    [Fact]
    public void DeviceFormatBecomes16kHzWithoutGlitchesAtPacketBoundaries()
    {
        var y = Run(RecorderInternals.Converter(Float48kStereo), Encode(Tone(48000, 1000, 0.8, 1, 1, 1), "float32"), 8, 480);
        Assert.InRange(y.Length, 15950, 16010);   // a second in, a second out (less the few samples the filter holds back)
        var (amplitude, deviation) = Fit(y, 1000, 100);
        Assert.InRange(amplitude, 0.79, 0.81);
        Assert.InRange(deviation, 0, 0.01);
    }

    [Fact]
    public void ContentAbove8kHzIsFilteredOutNotFoldedDown()
    {
        // Without a low-pass, 12 kHz at 48 kHz would come out of the decimation as a 4 kHz tone.
        var y = Run(RecorderInternals.Converter(Float48kStereo), Encode(Tone(48000, 12000, 0.8, 1, 1, 1), "float32"), 8, 480);
        Assert.InRange(Rms(y, 100), 0, 0.8 / Math.Sqrt(2) / 100);   // at least 40 dB down
    }

    [Fact]
    public void NonIntegerRatioFrom44k1()
    {
        var y = Run(RecorderInternals.Converter(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2)), Encode(Tone(44100, 1000, 0.8, 1, 1, 1), "float32"), 8, 441);
        Assert.InRange(y.Length, 15950, 16010);
        var (amplitude, deviation) = Fit(y, 1000, 100);
        Assert.InRange(amplitude, 0.79, 0.81);
        Assert.InRange(deviation, 0, 0.01);
    }

    [Fact]
    public void Narrowband8kHzIsUpsampled()
    {
        var y = Run(RecorderInternals.Converter(new WaveFormat(8000, 16, 1)), Encode(Tone(8000, 1000, 0.8, 1, 1), "pcm16"), 2, 80);
        Assert.InRange(y.Length, 15900, 16010);   // the sinc filter holds back 32 input samples, 4 ms at 8 kHz
        var (amplitude, deviation) = Fit(y, 1000, 100);
        Assert.InRange(amplitude, 0.79, 0.81);
        Assert.InRange(deviation, 0, 0.01);
    }

    [Fact]
    public void TwentyFourBitsInA32BitContainer()
    {
        var format = new WaveFormatExtensible(48000, 32, 2, false, 24, 3);
        var y = Run(RecorderInternals.Converter(format), Encode(Tone(48000, 1000, 0.8, 1, 1, 1), "pcm32"), 8, 480);
        var (amplitude, deviation) = Fit(y, 1000, 100);
        Assert.InRange(amplitude, 0.79, 0.81);
        Assert.InRange(deviation, 0, 0.01);
    }

    [Fact]
    public void ArrayMicrophoneChannelsAreAveraged()
    {
        var y = Run(RecorderInternals.Converter(WaveFormat.CreateIeeeFloatWaveFormat(48000, 4)), Encode(Tone(48000, 1000, 0.8, 1, 1, 1, 0, 0), "float32"), 16, 480);
        Assert.InRange(Fit(y, 1000, 100).Amplitude, 0.39, 0.41);
    }

    [Fact]
    public void EightBitPcmIsRefused()
    {
        Assert.Throws<NotSupportedException>(() => RecorderInternals.Converter(new WaveFormat(16000, 8, 1)));
    }

    [Fact]
    public void DescribeNamesRateChannelsAndSamples()
    {
        Assert.Equal("48000Hz/2ch/float32", RecorderInternals.Describe(Float48kStereo));
        Assert.Equal("16000Hz/1ch/pcm16", RecorderInternals.Describe(new WaveFormat(16000, 16, 1)));
    }

    private static void AssertClose(float[] expected, float[] actual, double tolerance)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.True(Math.Abs(expected[i] - actual[i]) <= tolerance, $"sample {i}: expected {expected[i]}, got {actual[i]}");
    }
}
