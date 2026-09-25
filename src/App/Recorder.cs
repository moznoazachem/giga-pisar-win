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

    private IWaveIn? _capture;
    private ManualResetEventSlim? _stopped;
    private readonly List<float> _samples = new(SampleRate * 30);
    private readonly object _gate = new();
    private float _levelSinceRead;
    private float _takePeak;
    private bool _capHit;
    private double _noiseDb = double.NaN;
    private double _peakDb = double.NaN;

    // WASAPI path: device-format bytes go through a resampler to 16 kHz mono.
    private BufferedWaveProvider? _wasapiBuffer;
    private ISampleProvider? _wasapiResampled;
    private readonly float[] _wasapiChunk = new float[SampleRate];

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
    public float TakePeak => _takePeak;

    public bool IsRecording { get; private set; }

    /// <summary>Which API the current take uses; for the log.</summary>
    public string Backend { get; private set; } = "";

    /// <summary>Raised on the capture thread when the take reaches <see cref="MaxTake"/>.</summary>
    public event Action? TakeTooLong;

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

    public Task StartAsync()
    {
        lock (_gate) { _samples.Clear(); _levelSinceRead = 0; _takePeak = 0; _capHit = false; _noiseDb = double.NaN; _peakDb = double.NaN; }
        var stopped = new ManualResetEventSlim(false);
        _stopped = stopped;
        IsRecording = true;

        // NAudio captures SynchronizationContext.Current in the capture object's constructor and
        // posts RecordingStopped through it. Built on a pool thread there is no context, so the
        // event fires directly on the capture thread and StopAsync does not have to wait for a UI
        // thread that may be busy.
        return Task.Run(() =>
        {
            Exception? first = null;
            // PISAR_CAPTURE=wasapi forces the fallback path (testing aid).
            bool forceWasapi = Environment.GetEnvironmentVariable("PISAR_CAPTURE") == "wasapi";
            var attempts = forceWasapi
                ? new Func<ManualResetEventSlim, IWaveIn>[] { OpenWasapi }
                : new Func<ManualResetEventSlim, IWaveIn>[] { s => OpenWaveIn(-1, s), s => OpenWaveIn(0, s), OpenWasapi };
            foreach (var attempt in attempts)
            {
                try
                {
                    _capture = attempt(stopped);
                    return;
                }
                catch (Exception e)
                {
                    first ??= e;
                    Log.Write($"capture attempt failed: {e.GetType().Name}: {e.Message}");
                }
            }
            IsRecording = false;
            throw first!;
        });
    }

    private IWaveIn OpenWaveIn(int deviceNumber, ManualResetEventSlim stopped)
    {
        var wi = new WaveInEvent
        {
            DeviceNumber = deviceNumber,   // -1 is WAVE_MAPPER: the default input device
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 40,
            NumberOfBuffers = 4,
        };
        wi.DataAvailable += OnPcm16Data;
        wi.RecordingStopped += (_, _) => { try { stopped.Set(); } catch (ObjectDisposedException) { } };
        try
        {
            wi.StartRecording();
        }
        catch
        {
            wi.DataAvailable -= OnPcm16Data;
            wi.Dispose();
            throw;
        }
        Backend = deviceNumber == -1 ? "waveIn/default" : "waveIn/0";
        return wi;
    }

    private IWaveIn OpenWasapi(ManualResetEventSlim stopped)
    {
        var capture = new WasapiCapture();   // default capture endpoint, shared mode, device format
        // ReadFully=false: when the buffer runs dry Read returns 0 instead of padding with silence,
        // otherwise the drain loop below would never end.
        _wasapiBuffer = new BufferedWaveProvider(capture.WaveFormat) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromSeconds(2), ReadFully = false };
        ISampleProvider samples = _wasapiBuffer.ToSampleProvider();
        if (capture.WaveFormat.Channels > 1) samples = new StereoToMonoSampleProvider(samples) { LeftVolume = 0.5f, RightVolume = 0.5f };
        _wasapiResampled = new WdlResamplingSampleProvider(samples, SampleRate);
        capture.DataAvailable += OnWasapiData;
        capture.RecordingStopped += (_, _) => { try { stopped.Set(); } catch (ObjectDisposedException) { } };
        try
        {
            capture.StartRecording();
        }
        catch
        {
            capture.DataAvailable -= OnWasapiData;
            capture.Dispose();
            throw;
        }
        Backend = $"wasapi/{capture.WaveFormat.SampleRate}Hz/{capture.WaveFormat.Channels}ch";
        return capture;
    }

    private void OnPcm16Data(object? sender, WaveInEventArgs e)
    {
        int n = e.BytesRecorded / 2;
        var chunk = n <= _wasapiChunk.Length ? _wasapiChunk : new float[n];
        for (int i = 0; i < n; i++) chunk[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
        Append(chunk.AsSpan(0, n));
    }

    private void OnWasapiData(object? sender, WaveInEventArgs e)
    {
        _wasapiBuffer!.AddSamples(e.Buffer, 0, e.BytesRecorded);
        int n;
        int guard = 0;
        while ((n = _wasapiResampled!.Read(_wasapiChunk, 0, _wasapiChunk.Length)) > 0 && guard++ < 16)
        {
            Append(_wasapiChunk.AsSpan(0, n));
            if (n < _wasapiChunk.Length) break;
        }
    }

    private void Append(ReadOnlySpan<float> chunk)
    {
        double sum = 0;
        bool hitCap = false;
        lock (_gate)
        {
            foreach (var v in chunk)
            {
                _samples.Add(v);
                sum += v * v;
                float a = Math.Abs(v);
                if (a > _takePeak) _takePeak = a;
            }
            if (chunk.Length > 0)
            {
                double rms = Math.Sqrt(sum / chunk.Length);
                double db = 20 * Math.Log10(Math.Max(rms, 1e-7));
                if (double.IsNaN(_noiseDb)) { _noiseDb = db; _peakDb = db + MeterMinSpanDb; }
                _noiseDb = Math.Min(db, _noiseDb + MeterFloorRiseDb);   // floor: drops at once, rises slowly
                _peakDb = Math.Max(db, _peakDb - MeterPeakFallDb);      // ceiling: rises at once, falls slowly
                double span = Math.Max(_peakDb - _noiseDb, MeterMinSpanDb);
                float level = (float)Math.Clamp((db - _noiseDb) / span, 0, 1);
                _levelSinceRead = Math.Max(_levelSinceRead, level);
            }
            if (!_capHit && _samples.Count >= MaxTake.TotalSeconds * SampleRate) { _capHit = true; hitCap = true; }
        }
        if (hitCap) TakeTooLong?.Invoke();
    }

    /// <summary>Stops capture and returns everything recorded since StartAsync().</summary>
    public Task<float[]> StopAsync()
    {
        var capture = _capture;
        var stopped = _stopped;
        _capture = null;
        _stopped = null;
        IsRecording = false;
        if (capture == null) return Task.FromResult(Array.Empty<float>());

        return Task.Run(() =>
        {
            try
            {
                capture.StopRecording();
                stopped?.Wait(500);
            }
            catch (Exception e) { Log.Write($"capture stop: {e.Message}"); }
            finally
            {
                capture.DataAvailable -= OnPcm16Data;
                capture.DataAvailable -= OnWasapiData;
                capture.Dispose();
                stopped?.Dispose();
                _wasapiBuffer = null;
                _wasapiResampled = null;
            }
            lock (_gate)
            {
                var result = _samples.ToArray();
                _samples.Clear();
                _levelSinceRead = 0;
                return result;
            }
        });
    }

    public void Dispose()
    {
        if (_capture != null) StopAsync().GetAwaiter().GetResult();
    }
}
