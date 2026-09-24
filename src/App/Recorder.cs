// Microphone capture at 16 kHz mono through the classic waveIn API.
// The Windows audio engine resamples from whatever the device runs at,
// so the model gets exactly the format it was trained on.

using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GigaPisar.App;

public sealed class Recorder : IDisposable
{
    public const int SampleRate = 16000;

    /// <summary>A take longer than this is stopped by itself: the key is stuck or the hook died.</summary>
    public static readonly TimeSpan MaxTake = TimeSpan.FromMinutes(5);

    private WaveInEvent? _waveIn;
    private ManualResetEventSlim? _stopped;
    private readonly List<float> _samples = new(SampleRate * 30);
    private readonly object _gate = new();
    private float _levelSinceRead;
    private float _takePeak;
    private bool _capHit;
    private double _noiseDb = double.NaN;
    private double _peakDb = double.NaN;

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

    /// <summary>Raised on the capture thread when the take reaches <see cref="MaxTake"/>.</summary>
    public event Action? TakeTooLong;

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

        // WaveInEvent captures SynchronizationContext.Current in its constructor and posts
        // RecordingStopped through it. Built on a pool thread there is no context, so the
        // event fires directly on the capture thread and StopAsync does not have to wait for
        // a UI thread that may be busy.
        return Task.Run(() =>
        {
            WaveInEvent? wi = null;
            try
            {
                wi = Open(-1, stopped);   // WAVE_MAPPER: the default input device
            }
            catch
            {
                wi?.Dispose();
                wi = Open(0, stopped);
            }
            _waveIn = wi;
        });
    }

    private WaveInEvent Open(int deviceNumber, ManualResetEventSlim stopped)
    {
        var wi = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 40,
            NumberOfBuffers = 4,
        };
        wi.DataAvailable += OnData;
        wi.RecordingStopped += (_, _) => { try { stopped.Set(); } catch (ObjectDisposedException) { } };
        try
        {
            wi.StartRecording();
        }
        catch
        {
            wi.DataAvailable -= OnData;
            wi.Dispose();
            throw;
        }
        return wi;
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        double sum = 0;
        int n = e.BytesRecorded / 2;
        bool hitCap = false;
        lock (_gate)
        {
            for (int i = 0; i < n; i++)
            {
                float v = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
                _samples.Add(v);
                sum += v * v;
                float a = Math.Abs(v);
                if (a > _takePeak) _takePeak = a;
            }
            if (n > 0)
            {
                double rms = Math.Sqrt(sum / n);
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
        var wi = _waveIn;
        var stopped = _stopped;
        _waveIn = null;
        _stopped = null;
        IsRecording = false;
        if (wi == null) return Task.FromResult(Array.Empty<float>());

        return Task.Run(() =>
        {
            try
            {
                wi.StopRecording();
                stopped?.Wait(500);
            }
            catch (Exception e) { Log.Write($"waveIn stop: {e.Message}"); }
            finally
            {
                wi.DataAvailable -= OnData;
                wi.Dispose();
                stopped?.Dispose();
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
        if (_waveIn != null) StopAsync().GetAwaiter().GetResult();
    }
}
