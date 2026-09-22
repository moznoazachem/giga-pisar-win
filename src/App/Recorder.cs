// Microphone capture at 16 kHz mono through the classic waveIn API.
// The Windows audio engine resamples from whatever the device runs at,
// so the model gets exactly the format it was trained on.

using NAudio.Wave;

namespace GigaPisar.App;

public sealed class Recorder : IDisposable
{
    public const int SampleRate = 16000;

    private WaveInEvent? _waveIn;
    private readonly List<float> _samples = new(SampleRate * 30);
    private readonly ManualResetEventSlim _stopped = new(false);
    private readonly object _gate = new();
    private float _peak;
    private float _takePeak;

    /// <summary>Loudest absolute sample of the last take, 0..1. Below ~0.01 the microphone is effectively silent.</summary>
    public float TakePeak => _takePeak;

    /// <summary>Loudness of the latest buffer, 0..1, shaped for a VU-style display.</summary>
    public float Level
    {
        get { lock (_gate) { var p = _peak; _peak = 0; return p; } }
    }

    public bool IsRecording { get; private set; }

    /// <summary>Name of the default input device, or a note that there is none.</summary>
    public static string DefaultDeviceName()
    {
        try
        {
            if (WaveInEvent.DeviceCount == 0) return L.T("не найден", "none found");
            return WaveInEvent.GetCapabilities(0).ProductName;
        }
        catch { return L.T("не найден", "none found"); }
    }

    public void Start()
    {
        lock (_gate) { _samples.Clear(); _peak = 0; _takePeak = 0; }
        _stopped.Reset();

        var wi = new WaveInEvent
        {
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 40,
            NumberOfBuffers = 4,
        };
        wi.DataAvailable += OnData;
        wi.RecordingStopped += (_, _) => _stopped.Set();
        // StartRecording captures SynchronizationContext.Current for its RecordingStopped
        // event. Started from the UI thread, that event would be posted to the very thread
        // Stop() blocks on; a thread-pool thread has no context, so it fires directly.
        Task.Run(() =>
        {
            try
            {
                wi.DeviceNumber = -1;   // WAVE_MAPPER: the default input device
                wi.StartRecording();
            }
            catch
            {
                wi.DeviceNumber = 0;
                wi.StartRecording();
            }
        }).GetAwaiter().GetResult();
        _waveIn = wi;
        IsRecording = true;
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        double sum = 0;
        int n = e.BytesRecorded / 2;
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
                double db = 20 * Math.Log10(Math.Max(rms, 1e-6));
                float level = (float)Math.Clamp((db + 50) / 45, 0, 1);   // -50 dB silence … -5 dB loud
                _peak = Math.Max(_peak, level);
            }
        }
    }

    /// <summary>Stops capture and returns everything recorded since Start().</summary>
    public float[] Stop()
    {
        var wi = _waveIn;
        _waveIn = null;
        IsRecording = false;
        if (wi == null) return Array.Empty<float>();

        wi.StopRecording();
        _stopped.Wait(500);
        wi.DataAvailable -= OnData;
        wi.Dispose();

        lock (_gate)
        {
            var result = _samples.ToArray();
            _samples.Clear();
            _peak = 0;
            return result;
        }
    }

    public void Dispose()
    {
        if (_waveIn != null) Stop();
        _stopped.Dispose();
    }
}
