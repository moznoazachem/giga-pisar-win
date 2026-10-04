// Microphone capture at 16 kHz mono.
//
// First choice is the classic waveIn API: the Windows audio engine resamples
// from whatever the device runs at, so the model gets exactly the format it was
// trained on. When waveIn refuses (some drivers, Bluetooth profiles, odd
// setups) we fall back to WASAPI on the default endpoint and resample
// ourselves.

using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GigaPisar.App;

public sealed class Recorder : IDisposable
{
    public const int SampleRate = 16000;

    /// <summary>A take longer than this is stopped by itself: the key is stuck or the hook died.</summary>
    public static readonly TimeSpan MaxTake = TimeSpan.FromMinutes(5);

    private static readonly int MaxSamples = checked((int)(MaxTake.TotalSeconds * SampleRate));
    private Take? _take;
    private long _recordingId;
    private bool _disposed;
    private readonly object _gate = new();
    private float _levelSinceRead;
    private float _takePeak;
    private double _noiseDb = double.NaN;
    private double _peakDb = double.NaN;

    private sealed class Take
    {
        public readonly long Id;
        public readonly List<float> Samples = new(SampleRate);
        public Attempt? Capture;
        public Task Opening = Task.CompletedTask;
        public Task? Closing;
        public Task<float[]>? Result;
        public bool StopRequested;
        public bool Discard;
        public bool Accepting = true;
        public bool CapHit;

        public Take(long id) => Id = id;
    }

    private sealed class Attempt
    {
        public required IWaveIn Device;
        public MMDevice? Endpoint;
        public BufferedWaveProvider? Buffer;
        public ISampleProvider? Resampled;
        public readonly float[] Chunk = new float[SampleRate];
        public readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public EventHandler<WaveInEventArgs>? OnData;
        public EventHandler<StoppedEventArgs>? OnStopped;
    }

    /// <summary>The display meter adapts to the input: it tracks the noise floor and the loudest
    /// recent buffer and stretches the bars between them, so a quiet line-level receiver and a hot
    /// USB microphone both fill the wave. Recognition uses its own gain; this is display only.</summary>
    private const double MeterMinSpanDb = 15;    // never stretch a span narrower than this
    private const double MeterFloorRiseDb = 0.2; // noise floor creeps up this much per 40 ms buffer (5 dB/s)
    private const double MeterPeakFallDb = 0.4;  // ceiling falls this much per buffer (10 dB/s)

    /// <summary>Loudness of the buffers since the last read, 0..1, shaped for a VU-style display.</summary>
    public float Level
    {
        get { lock (_gate) { var p = _levelSinceRead; _levelSinceRead = 0; return p; } }
    }

    /// <summary>Loudest absolute sample of the current take, 0..1.</summary>
    public float TakePeak { get { lock (_gate) return _takePeak; } }

    public bool IsRecording { get { lock (_gate) return _take is { StopRequested: false, Accepting: true }; } }
    public bool HasTake { get { lock (_gate) return _take != null; } }
    public long RecordingId { get { lock (_gate) return _recordingId; } }

    /// <summary>Which API the current take uses; for the log.</summary>
    public string Backend { get; private set; } = "";

    /// <summary>Raised on the capture thread when the take reaches <see cref="MaxTake"/>.</summary>
    public event Action<long>? TakeTooLong;
    public event Action<long>? RecordingEnded;

    /// <summary>Number of waveIn recording devices Windows reports.</summary>
    public static int DeviceCount
    {
        get { try { return WaveInEvent.DeviceCount; } catch { return 0; } }
    }

    /// <summary>Friendly name of the Windows default input device.</summary>
    public static string DefaultDeviceName()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            return device.FriendlyName;
        }
        catch { return L.T("не найден", "none found"); }
    }

    private DateTime _lastStop = DateTime.MinValue;

    /// <summary>Milliseconds between the previous take's stop and this start (diagnostics for quick re-presses).</summary>
    public double GapMs { get; private set; }

    public Task StartAsync()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_take != null) throw new InvalidOperationException("The previous recording has not been closed");
            GapMs = _lastStop == DateTime.MinValue ? -1 : (DateTime.UtcNow - _lastStop).TotalMilliseconds;
            _levelSinceRead = 0;
            _takePeak = 0;
            _noiseDb = double.NaN;
            _peakDb = double.NaN;
            Backend = "";
            var take = _take = new Take(++_recordingId);
            take.Opening = Task.Run(() => OpenTake(take));
            return take.Opening;
        }
    }

    private void OpenTake(Take take)
    {
        Exception? first = null;
        bool forceWasapi = Environment.GetEnvironmentVariable("PISAR_CAPTURE") == "wasapi";
        var factories = forceWasapi
            ? new Func<Attempt>[] { OpenWasapi }
            : new Func<Attempt>[] { () => OpenWaveIn(-1), () => OpenWaveIn(0), OpenWasapi };
        foreach (var factory in factories)
        {
            lock (_gate) if (take.StopRequested) return;
            Attempt? attempt = null;
            try
            {
                attempt = factory();
                var owned = attempt;
                attempt.OnData = (_, e) => OnData(take, owned, e);
                attempt.OnStopped = (_, e) =>
                {
                    owned.Stopped.TrySetResult();
                    if (e.Exception != null) Log.Write($"capture stopped: {e.Exception.Message}");
                    bool notify;
                    lock (_gate)
                    {
                        var current = ReferenceEquals(_take, take) && ReferenceEquals(take.Capture, owned);
                        notify = current && !take.StopRequested && !take.CapHit;
                        if (current) take.Accepting = false;
                    }
                    if (notify) RecordingEnded?.Invoke(take.Id);
                };
                attempt.Device.DataAvailable += attempt.OnData;
                attempt.Device.RecordingStopped += attempt.OnStopped;
                lock (_gate)
                {
                    take.Capture = attempt;
                    take.Accepting = !take.Discard && !take.CapHit;
                }
                attempt.Device.StartRecording();
                lock (_gate)
                    Backend = attempt.Device is WaveInEvent wave
                        ? wave.DeviceNumber == -1 ? "waveIn/default" : "waveIn/0"
                        : $"wasapi/{attempt.Device.WaveFormat.SampleRate}Hz/{attempt.Device.WaveFormat.Channels}ch";
                return;
            }
            catch (Exception e)
            {
                if (attempt != null)
                {
                    DisposeAttempt(attempt);
                    lock (_gate) take.Capture = null;
                }
                first ??= e;
                Log.Write($"capture attempt failed: {e.GetType().Name}: {e.Message}");
            }
        }
        throw first!;
    }

    private static Attempt OpenWaveIn(int deviceNumber) => new()
    {
        Device = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 40,
            NumberOfBuffers = 4,
        }
    };

    private static Attempt OpenWasapi()
    {
        using var enumerator = new MMDeviceEnumerator();
        var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        WasapiCapture? capture = null;
        try
        {
            capture = new WasapiCapture(endpoint);
            var buffer = new BufferedWaveProvider(capture.WaveFormat)
            {
                DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromSeconds(2), ReadFully = false,
            };
            ISampleProvider samples = buffer.ToSampleProvider();
            if (capture.WaveFormat.Channels > 1)
                samples = new StereoToMonoSampleProvider(samples) { LeftVolume = 0.5f, RightVolume = 0.5f };
            return new Attempt
            {
                Device = capture, Endpoint = endpoint, Buffer = buffer,
                Resampled = new WdlResamplingSampleProvider(samples, SampleRate),
            };
        }
        catch
        {
            capture?.Dispose();
            endpoint.Dispose();
            throw;
        }
    }

    private void OnData(Take take, Attempt attempt, WaveInEventArgs e)
    {
        attempt.Ready.TrySetResult();
        bool hitCap = false;
        lock (_gate)
        {
            if (!ReferenceEquals(_take, take) || !ReferenceEquals(take.Capture, attempt) || !take.Accepting) return;
            if (attempt.Buffer == null)
            {
                int count = e.BytesRecorded / 2;
                var chunk = count <= attempt.Chunk.Length ? attempt.Chunk : new float[count];
                for (int i = 0; i < count; i++) chunk[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
                hitCap = Append(take, chunk.AsSpan(0, count));
            }
            else
            {
                attempt.Buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                int count;
                int guard = 0;
                while ((count = attempt.Resampled!.Read(attempt.Chunk, 0, attempt.Chunk.Length)) > 0 && guard++ < 16)
                {
                    hitCap |= Append(take, attempt.Chunk.AsSpan(0, count));
                    if (count < attempt.Chunk.Length || !take.Accepting) break;
                }
            }
        }
        if (hitCap)
        {
            _ = CloseDeviceAsync(take);
            TakeTooLong?.Invoke(take.Id);
        }
    }

    private bool Append(Take take, ReadOnlySpan<float> chunk)
    {
        chunk = chunk[..Math.Min(chunk.Length, MaxSamples - take.Samples.Count)];
        int needed = take.Samples.Count + chunk.Length;
        if (needed > take.Samples.Capacity)
            take.Samples.Capacity = Math.Min(MaxSamples, Math.Max(needed, take.Samples.Capacity * 2));
        double sum = 0;
        foreach (var v in chunk)
        {
            take.Samples.Add(v);
            sum += v * v;
            float a = Math.Abs(v);
            if (a > _takePeak) _takePeak = a;
        }
        if (chunk.Length > 0)
        {
            double rms = Math.Sqrt(sum / chunk.Length);
            double db = 20 * Math.Log10(Math.Max(rms, 1e-7));
            if (double.IsNaN(_noiseDb)) { _noiseDb = db; _peakDb = db + MeterMinSpanDb; }
            _noiseDb = Math.Min(db, _noiseDb + MeterFloorRiseDb);
            _peakDb = Math.Max(db, _peakDb - MeterPeakFallDb);
            double span = Math.Max(_peakDb - _noiseDb, MeterMinSpanDb);
            float level = (float)Math.Clamp((db - _noiseDb) / span, 0, 1);
            _levelSinceRead = Math.Max(_levelSinceRead, level);
        }
        if (take.CapHit || take.Samples.Count < MaxSamples) return false;
        take.CapHit = true;
        take.Accepting = false;
        return true;
    }

    /// <summary>Stops capture and returns everything recorded since StartAsync().</summary>
    public Task<float[]> StopAsync(bool discard = false)
    {
        lock (_gate)
        {
            var take = _take;
            if (take == null) return Task.FromResult(Array.Empty<float>());
            take.StopRequested = true;
            take.Discard |= discard;
            if (take.Discard) take.Accepting = false;
            if (take.Result == null || take.Result.IsFaulted)
                take.Result = Task.Run(() => FinishTakeAsync(take));
            return take.Result;
        }
    }

    private async Task<float[]> FinishTakeAsync(Take take)
    {
        await CloseDeviceAsync(take).ConfigureAwait(false);
        lock (_gate)
        {
            take.Accepting = false;
            var result = take.Discard ? Array.Empty<float>() : take.Samples.ToArray();
            take.Samples.Clear();
            take.Samples.Capacity = 0;
            if (ReferenceEquals(_take, take)) _take = null;
            _lastStop = DateTime.UtcNow;
            _levelSinceRead = 0;
            return result;
        }
    }

    private Task CloseDeviceAsync(Take take)
    {
        lock (_gate)
        {
            if (take.Closing == null || take.Closing.IsFaulted)
                take.Closing = Task.Run(() => CloseDeviceCoreAsync(take));
            return take.Closing;
        }
    }

    private async Task CloseDeviceCoreAsync(Take take)
    {
        try { await take.Opening.ConfigureAwait(false); }
        catch { }
        var attempt = take.Capture;
        if (attempt == null) return;

        // WaveInEvent queues its recording thread after StartRecording returns. A stop before
        // that thread enters DoRecording can be overwritten by its Capturing state. Wait for
        // the first callback, then retry stopping until RecordingStopped confirms completion.
        await Task.WhenAny(attempt.Ready.Task, attempt.Stopped.Task, Task.Delay(500)).ConfigureAwait(false);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        try
        {
            while (!attempt.Stopped.Task.IsCompleted)
            {
                attempt.Device.StopRecording();
                await Task.WhenAny(attempt.Stopped.Task, Task.Delay(100)).ConfigureAwait(false);
                if (!attempt.Stopped.Task.IsCompleted && DateTime.UtcNow >= deadline)
                    throw new TimeoutException("The microphone did not confirm that recording stopped");
            }
        }
        catch
        {
            lock (_gate) take.Accepting = false;
            throw;
        }
        lock (_gate) take.Accepting = false;
        DisposeAttempt(attempt);
        lock (_gate) take.Capture = null;
    }

    private static void DisposeAttempt(Attempt attempt)
    {
        attempt.Device.DataAvailable -= attempt.OnData;
        attempt.Device.RecordingStopped -= attempt.OnStopped;
        try { attempt.Device.Dispose(); }
        finally { attempt.Endpoint?.Dispose(); }
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        StopAsync(discard: true).GetAwaiter().GetResult();
    }
}
