using System.Reflection;
using System.Runtime.CompilerServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GigaPisar.Tests;

/// <summary>
/// A real NAudio WasapiRecorder object whose capture thread is simulated, so Recorder can be driven
/// without WASAPI. The thread does what NAudio 3.1.0's WasapiRecorder.CaptureThread does:
/// IAudioClient::Start (here a delay or a failure), then an unconditional Capturing that overwrites
/// any stop requested before it, then a packet every ~10 ms for as long as the state stays Capturing,
/// and on the way out Stopped and RecordingStopped.
/// </summary>
internal sealed class FakeWasapiRecorder
{
    private const BindingFlags Members = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly FieldInfo StateField = Get("captureState");
    private static readonly FieldInfo ThreadField = Get("captureThread");
    private static readonly FieldInfo FormatField = Get("waveFormat");
    private static readonly FieldInfo DataField = Get("DataAvailable");
    private static readonly FieldInfo StoppedField = Get("RecordingStopped");

    private Thread? _thread;
    private volatile int _packets;

    public WasapiRecorder Recorder { get; } = (WasapiRecorder)RuntimeHelpers.GetUninitializedObject(typeof(WasapiRecorder));

    /// <summary>Packets delivered so far: 160 samples (10 ms at 16 kHz) each.</summary>
    public int Packets => _packets;

    public FakeWasapiRecorder() => FormatField.SetValue(Recorder, new WaveFormat(16000, 16, 1));

    private CaptureState State
    {
        get => Recorder.CaptureState;
        set => StateField.SetValue(Recorder, value);
    }

    /// <summary>What StartRecording does once IAudioClient::Initialize has succeeded.</summary>
    /// <param name="signal">Sample value by sample index; silence when null.</param>
    /// <param name="flags">Buffer flags by packet index.</param>
    public void Start(int startDelayMs = 5, Exception? failAtStart = null,
                      Func<int, float>? signal = null, Func<int, AudioClientBufferFlags>? flags = null)
    {
        State = CaptureState.Starting;
        _thread = new Thread(() =>
        {
            Exception? error = null;
            try
            {
                Thread.Sleep(startDelayMs);
                if (failAtStart != null) throw failAtStart;
                State = CaptureState.Capturing;
                for (int sample = 0; State == CaptureState.Capturing;)
                {
                    Thread.Sleep(10);
                    if (State != CaptureState.Capturing) break;
                    var pcm = new byte[320];
                    for (int i = 0; i < 160; i++, sample++)
                        BitConverter.TryWriteBytes(pcm.AsSpan(i * 2), (short)Math.Round((signal?.Invoke(sample) ?? 0f) * 32767));
                    (DataField.GetValue(Recorder) as CaptureDataAvailableHandler)?.Invoke(pcm, flags?.Invoke(_packets) ?? AudioClientBufferFlags.None, 0, 0);
                    _packets++;
                }
            }
            catch (Exception e) { error = e; }
            finally
            {
                State = CaptureState.Stopped;
                (StoppedField.GetValue(Recorder) as EventHandler<StoppedEventArgs>)?.Invoke(Recorder, new StoppedEventArgs(error));
            }
        }) { IsBackground = true, Name = "fake WASAPI capture" };
        ThreadField.SetValue(Recorder, _thread);
        _thread.Start();
    }

    /// <summary>Whether the capture thread has ended, waiting for it up to the given time.</summary>
    public bool Ended(int timeoutMs = 0) => _thread!.Join(timeoutMs);

    public bool WaitForPackets(int count, int timeoutMs = 10000) => SpinWait.SpinUntil(() => _packets >= count, timeoutMs);

    private static FieldInfo Get(string name) =>
        typeof(WasapiRecorder).GetField(name, Members)
        ?? throw new MissingMemberException($"WasapiRecorder.{name} not found: NAudio changed, update FakeWasapiRecorder.");
}
