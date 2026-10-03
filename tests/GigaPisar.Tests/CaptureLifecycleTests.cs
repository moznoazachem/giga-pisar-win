using System.Diagnostics;
using System.Runtime.InteropServices;
using GigaPisar.App;
using NAudio.CoreAudioApi;
using Xunit;

namespace GigaPisar.Tests;

/// <summary>Opening and stopping a take, against a simulated NAudio capture thread (see FakeWasapiRecorder).</summary>
public class CaptureLifecycleTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>A recorder in the middle of a take, as StartAsync leaves it once the device is open.</summary>
    private static (Recorder Recorder, FakeWasapiRecorder Fake, CaptureHandle Capture) Recording(
        Func<int, float>? signal = null, Func<int, AudioClientBufferFlags>? flags = null)
    {
        var recorder = new Recorder();
        var fake = new FakeWasapiRecorder();
        var capture = RecorderInternals.NewCapture(recorder, fake);
        fake.Start(signal: signal, flags: flags);
        capture.WaitUntilRunning();
        RecorderInternals.SetTake(recorder, capture);
        return (recorder, fake, capture);
    }

    [Fact]
    public async Task ReleaseStopsTheTakeAndReturnsItsAudio()
    {
        var (recorder, fake, _) = Recording(s => 0.5f * MathF.Sin(2 * MathF.PI * 440 * s / 16000));
        Assert.True(recorder.IsRecording);
        Assert.True(fake.WaitForPackets(20));

        var clock = Stopwatch.StartNew();
        var samples = await recorder.StopAsync();

        Assert.True(fake.Ended(), "the capture thread is still running");
        Assert.InRange(clock.ElapsedMilliseconds, 0, 1000);
        Assert.True(samples.Length >= 20 * 160, $"{samples.Length} samples");
        Assert.InRange(recorder.TakePeak, 0.49f, 0.51f);
        Assert.False(recorder.IsRecording);
    }

    [Fact]
    public async Task StopSentWhileIAudioClientStartIsStillRunningIsNotLost()
    {
        // NAudio writes Capturing once IAudioClient::Start returns, over a stop that came earlier.
        // Start takes 2.5 s here, so the stop has to be sent again after that.
        var fake = new FakeWasapiRecorder();
        var capture = RecorderInternals.NewCapture(new Recorder(), fake);
        fake.Start(startDelayMs: 2500);
        Thread.Sleep(20);

        var clock = Stopwatch.StartNew();
        await capture.CloseAsync().WaitAsync(TimeSpan.FromSeconds(8), Cancel);

        Assert.True(fake.Ended());
        Assert.InRange(clock.ElapsedMilliseconds, 2400, 8000);
    }

    [Fact]
    public async Task DeviceThatHoldsOnCostsTheReleaseAtMostStopTimeout()
    {
        var recorder = new Recorder();
        var fake = new FakeWasapiRecorder();
        var capture = RecorderInternals.NewCapture(recorder, fake);
        fake.Start(startDelayMs: 3000);
        RecorderInternals.SetTake(recorder, capture);
        var log = Log.Mark();

        var clock = Stopwatch.StartNew();
        await recorder.StopAsync();

        Assert.InRange(clock.ElapsedMilliseconds, 1800, 4000);   // StopTimeout is 2 s
        Assert.Contains(log(), line => line.Contains("did not let go in time"));
        Assert.True(fake.Ended(6000), "the capture was not stopped in the background");
    }

    [Fact]
    public void StartFailureOnTheCaptureThreadReachesThePress()
    {
        var fake = new FakeWasapiRecorder();
        var capture = RecorderInternals.NewCapture(new Recorder(), fake);
        var failure = new COMException("Core Audio error 0x88890004", unchecked((int)0x88890004));
        fake.Start(failAtStart: failure);

        Assert.Same(failure, Assert.ThrowsAny<COMException>(capture.WaitUntilRunning));
        Assert.True(fake.Ended(), "the failed capture was not released");
    }

    [Fact]
    public void DeviceThatDoesNotStartGivesUpAfterOpenTimeoutAndIsStoppedWhenItDoes()
    {
        var fake = new FakeWasapiRecorder();
        var capture = RecorderInternals.NewCapture(new Recorder(), fake);
        var clock = Stopwatch.StartNew();
        fake.Start(startDelayMs: 6000);

        Assert.Throws<TimeoutException>(capture.WaitUntilRunning);
        Assert.InRange(clock.ElapsedMilliseconds, 4800, 9000);   // OpenTimeout is 5 s
        Assert.True(fake.Ended(6000), "the capture that started late was left running");
    }

    [Fact]
    public async Task TapThatEndsBeforeTheDeviceOpensClosesTheLateCapture()
    {
        var recorder = new Recorder();
        var finishOpen = RecorderInternals.SetPendingTake(recorder);
        Assert.True(recorder.IsRecording);

        var stop = recorder.StopAsync();   // the key comes up while the device is still opening
        Assert.False(recorder.IsRecording);   // at once, so nothing treats it as a live take any more

        var fake = new FakeWasapiRecorder();
        var capture = RecorderInternals.NewCapture(recorder, fake);
        fake.Start();
        capture.WaitUntilRunning();
        finishOpen(capture);   // the open completes after the release
        await stop.WaitAsync(TimeSpan.FromSeconds(5), Cancel);

        Assert.True(fake.Ended(2000), "the capture opened after the release was left running");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DigitalSilenceFromTheConvertedStreamSwitchesToTheDeviceFormat(bool exactZeros)
    {
        var (recorder, fake, _) = Recording(exactZeros ? null : s => 0.001f * MathF.Sin(s));
        Assert.True(fake.WaitForPackets(110));   // more than a second
        await recorder.StopAsync();

        Assert.Equal(exactZeros, RecorderInternals.PrefersDeviceFormat(recorder));
    }

    [Fact]
    public async Task GapAfterTheFirstPacketIsCountedAndLogged()
    {
        // The first packet after Start usually carries the discontinuity flag; a later one means lost audio.
        var (recorder, fake, capture) = Recording(
            _ => 0.1f, packet => packet is 0 or 5 ? AudioClientBufferFlags.DataDiscontinuity : AudioClientBufferFlags.None);
        Assert.True(fake.WaitForPackets(8));
        var log = Log.Mark();

        await recorder.StopAsync();

        Assert.Equal(1, capture.Gaps);
        Assert.Contains(log(), line => line.Contains("dropped audio 1 time(s)"));
    }

    [Fact]
    public void DisposeDuringATakeStopsIt()
    {
        var (recorder, fake, _) = Recording();
        var clock = Stopwatch.StartNew();

        recorder.Dispose();

        Assert.True(fake.Ended(), "Dispose left the capture running");
        Assert.InRange(clock.ElapsedMilliseconds, 0, 2000);
    }
}
