using GigaPisar.App;
using Xunit;

namespace GigaPisar.Tests;

/// <summary>A real StartAsync where WASAPI is missing (Linux, macOS): every way of opening fails.</summary>
public class OpenFailureTests
{
    [Fact]
    public async Task BothCapturePathsAreTriedAndThePressGetsTheError()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "On Windows this would open the real default microphone.");
        var recorder = new Recorder();
        var log = Log.Mark();

        await Assert.ThrowsAnyAsync<Exception>(recorder.StartAsync);

        var attempts = log().Where(line => line.StartsWith("capture attempt failed")).ToList();
        Assert.Equal(2, attempts.Count);
        Assert.Contains("16 kHz by Windows", attempts[0]);   // the engine's conversion first,
        Assert.Contains("device format", attempts[1]);       // then the fallback
        Assert.False(RecorderInternals.PrefersDeviceFormat(recorder));   // a failure on both paths changes nothing
        Assert.False(recorder.IsRecording);   // the next press may try again
        Assert.Empty(await recorder.StopAsync());
        recorder.Dispose();
    }
}
