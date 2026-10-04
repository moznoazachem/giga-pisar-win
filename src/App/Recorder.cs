// Microphone capture at 16 kHz mono through WASAPI (NAudio 3's WasapiRecorder).
//
// First choice is shared mode with the audio engine's own conversion: we ask for 16 kHz mono PCM
// and Windows resamples from whatever the device runs at, so the model gets exactly the format it
// was trained on. Some endpoints refuse that request, or are reported to answer it with pure
// digital silence; then we capture in the device's own format and downmix and resample here.
//
// The classic waveIn API is deliberately not used: security software (Kaspersky) blocks
// WaveInEvent's waveInOpen, and NAudio.WinMM is not referenced at all, so that code is
// not even present in the shipped binary.

using System.Buffers.Binary;
using System.Runtime.ExceptionServices;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace GigaPisar.App;

public sealed class Recorder : IDisposable
{
    public const int SampleRate = 16000;

    /// <summary>A take longer than this is stopped by itself: the key is stuck or the hook died.</summary>
    public static readonly TimeSpan MaxTake = TimeSpan.FromMinutes(5);

    /// <summary>How long a press waits for the device to start, and a release for it to stop.</summary>
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The engine buffer. Packets still arrive once per engine period (about 10 ms); this is
    /// only headroom for a capture thread that runs late, past which the engine drops audio.</summary>
    private const int BufferMilliseconds = 200;

    /// <summary>Digital silence (-120 dBFS): no real input, however quiet, gets this low.</summary>
    private const double SilentRms = 1e-6;

    private Task<Capture>? _take;   // from StartAsync until StopAsync takes it over
    // Capture in the device's own format: set once the engine's conversion fails us, or by
    // PISAR_CAPTURE=device (testing aid for the fallback path).
    private volatile bool _deviceFormat = Environment.GetEnvironmentVariable("PISAR_CAPTURE") == "device";
    // Some drivers refuse an event-driven stream (E_INVALIDARG at Initialize); polling is the fallback.
    private volatile bool _polling;
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
    private const double MeterMinSpanDb = 15;          // never stretch a span narrower than this
    private const double MeterFloorRiseDbPerSec = 5;   // the noise floor creeps up this fast
    private const double MeterPeakFallDbPerSec = 10;   // the ceiling falls this fast

    /// <summary>Loudness of the buffers since the last read, 0..1, shaped for a VU-style display.</summary>
    public float Level
    {
        get { lock (_gate) { var p = _levelSinceRead; _levelSinceRead = 0; return p; } }
    }

    /// <summary>Loudest absolute sample of the current take, 0..1.</summary>
    public float TakePeak => _takePeak;

    /// <summary>From the press until the release, including while the device is still opening.</summary>
    public bool IsRecording => _take is { IsFaulted: false };

    /// <summary>How the last take was captured (device format, who converts it); for the log.</summary>
    public string Backend { get; private set; } = "";

    /// <summary>Raised on the capture thread when the take reaches <see cref="MaxTake"/>.</summary>
    public event Action? TakeTooLong;

    /// <summary>Number of active recording devices Windows reports.</summary>
    public static int DeviceCount
    {
        get
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                return devices.Count;
            }
            catch { return 0; }
        }
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

    private static string NotResponding =>
        L.T($"устройство не ответило за {OpenTimeout.TotalSeconds:0} секунд", $"the device did not respond within {OpenTimeout.TotalSeconds:0} seconds");

    private DateTime _lastStop = DateTime.MinValue;

    /// <summary>Milliseconds between the previous take's stop and this start (diagnostics for quick re-presses).</summary>
    public double GapMs { get; private set; }

    public Task StartAsync()
    {
        GapMs = _lastStop == DateTime.MinValue ? -1 : (DateTime.UtcNow - _lastStop).TotalMilliseconds;
        lock (_gate) { _samples.Clear(); _levelSinceRead = 0; _takePeak = 0; _capHit = false; _noiseDb = double.NaN; _peakDb = double.NaN; }
        var take = OpenAsync();
        _take = take;
        return take;
    }

    private async Task<Capture> OpenAsync()
    {
        // Opened on a pool thread: opening a WASAPI stream can block for a moment and the app calls
        // this from the UI thread. It also keeps NAudio from capturing the UI's SynchronizationContext,
        // so RecordingStopped is raised on the capture thread and a stop never waits for a busy UI.
        var open = Task.Run(Open);
        try
        {
            return await open.WaitAsync(OpenTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A driver that does not answer must cost this press, not dictation for good. If the
            // device opens after all, nobody owns it: close it at once.
            _ = open.ContinueWith(t => t.Result.CloseAsync(), TaskContinuationOptions.OnlyOnRanToCompletion);
            Log.Write($"capture: the device did not open within {OpenTimeout.TotalSeconds:0} s");
            throw new TimeoutException(NotResponding);
        }
    }

    /// <summary>Opens the preferred capture path, or the next one when that fails: format by Windows or
    /// by the device, event-driven or polled.</summary>
    private Capture Open()
    {
        bool fmt = _deviceFormat, poll = _polling;
        var attempts = new[] { (fmt, poll), (!fmt, poll), (fmt, !poll), (!fmt, !poll) };
        Exception? first = null;
        foreach (var (deviceFormat, polling) in attempts)
        {
            try
            {
                var capture = Capture.Open(this, deviceFormat, polling);
                _deviceFormat = deviceFormat;   // keep using what works
                _polling = polling;
                if (capture.Backend != Backend) Log.Write($"capture: {capture.Backend}");
                Backend = capture.Backend;
                return capture;
            }
            catch (Exception e) when (e is not TimeoutException)   // a device that does not answer is not asked twice
            {
                first ??= e;
                Log.Write($"capture attempt failed ({(deviceFormat ? "device format" : "16 kHz by Windows")}, {(polling ? "polled" : "event-driven")}): {e.GetType().Name}: {e.Message} (0x{e.HResult:X8})");
            }
        }
        throw first!;   // the first failure is the one to explain to the user
    }

    private void Append(ReadOnlySpan<float> chunk)
    {
        if (chunk.IsEmpty) return;
        double sum = 0;
        bool hitCap = false;
        lock (_gate)
        {
            // Past the cap nothing more is kept, even if the UI is slow to end the take.
            if (_capHit) return;
            foreach (var x in chunk)
            {
                float v = Math.Clamp(x, -1f, 1f);
                _samples.Add(v);
                sum += v * v;
                float a = Math.Abs(v);
                if (a > _takePeak) _takePeak = a;
            }
            double rms = Math.Sqrt(sum / chunk.Length);
            // Digital silence says nothing about the room, and the engine often flags the first packet
            // after Start as silent. Taken in, it would pin the noise floor at -140 dB, and the bars would
            // stand high on plain room noise until the floor crept back up: most of a take.
            if (rms >= SilentRms)
            {
                double seconds = chunk.Length / (double)SampleRate;
                double db = 20 * Math.Log10(rms);
                if (double.IsNaN(_noiseDb)) { _noiseDb = db; _peakDb = db + MeterMinSpanDb; }
                _noiseDb = Math.Min(db, _noiseDb + MeterFloorRiseDbPerSec * seconds);   // floor: drops at once, rises slowly
                _peakDb = Math.Max(db, _peakDb - MeterPeakFallDbPerSec * seconds);      // ceiling: rises at once, falls slowly
                double span = Math.Max(_peakDb - _noiseDb, MeterMinSpanDb);
                float level = (float)Math.Clamp((db - _noiseDb) / span, 0, 1);
                _levelSinceRead = Math.Max(_levelSinceRead, level);
            }
            if (!_capHit && _samples.Count >= MaxTake.TotalSeconds * SampleRate) { _capHit = true; hitCap = true; }
        }
        if (hitCap) TakeTooLong?.Invoke();
    }

    /// <summary>Stops capture and returns everything recorded since StartAsync().</summary>
    public async Task<float[]> StopAsync()
    {
        var take = _take;
        _take = null;   // for the caller the take is over now, even while the device is still opening
        if (take == null) return Array.Empty<float>();

        Capture capture;
        try
        {
            // A tap can end before the device is open. Wait for the open (it gives up by itself after
            // OpenTimeout); otherwise the capture would start after this stop with nobody to stop it.
            capture = await take.ConfigureAwait(false);
        }
        catch { return Array.Empty<float>(); }   // the open failed, and whoever awaited StartAsync has been told

        try
        {
            await capture.CloseAsync().WaitAsync(StopTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException) { Log.Write("capture: the device did not let go in time; it is closed in the background"); }
        catch (Exception e) { Log.Write($"capture stop: {e.Message}"); }
        _lastStop = DateTime.UtcNow;

        float[] result;
        lock (_gate)
        {
            result = _samples.ToArray();
            _samples.Clear();
            // A long take grew the list to tens of MB; give that back instead of keeping it until exit.
            if (_samples.Capacity > SampleRate * 30) _samples.Capacity = SampleRate * 30;
            _levelSinceRead = 0;
        }
        if (capture.Gaps > 0) Log.Write($"capture: the engine dropped audio {capture.Gaps} time(s) in this take; the capture thread fell behind");
        // Some endpoints are reported to deliver the engine-converted stream as pure digital silence while
        // their own format works (RustAudio/cpal#1200, cjpais/Handy#2141). A full second of exact zeros is
        // not a quiet room: capture in the device format from the next take on. A muted microphone looks
        // the same, and then the switch costs nothing.
        if (!capture.DeviceFormat && result.Length >= SampleRate && _takePeak == 0)
        {
            _deviceFormat = true;
            Log.Write("capture: the converted stream was digital silence; using the device format from now on");
        }
        return result;
    }

    public void Dispose()
    {
        // Quit: close a take in progress, even one whose device is still opening, but never hold the
        // exit up for a driver that does not answer.
        if (_take != null) StopAsync().Wait(StopTimeout);
    }

    /// <summary>One open WASAPI stream: the device, NAudio's recorder on it, and how its packets
    /// become 16 kHz mono.</summary>
    private sealed class Capture
    {
        private readonly Recorder _owner;
        private readonly MMDevice _device;
        private readonly WasapiRecorder _recorder;
        private readonly Converter _converter;
        // Completed by RecordingStopped, with whatever tore the capture thread down (null on a requested stop).
        private readonly TaskCompletionSource<Exception?> _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _packets;

        public bool DeviceFormat { get; }
        public string Backend { get; }

        /// <summary>Packets after which the engine reported lost audio.</summary>
        public int Gaps { get; private set; }

        private Capture(Recorder owner, MMDevice device, WasapiRecorder recorder, bool deviceFormat, bool polling)
        {
            _owner = owner;
            _device = device;
            _recorder = recorder;
            DeviceFormat = deviceFormat;
            _converter = new Converter(recorder.WaveFormat);   // throws for a device format we cannot read
            Backend = (deviceFormat
                ? $"wasapi/{Converter.Describe(recorder.WaveFormat)}/converted-here"
                : $"wasapi/{MixFormatOf(device)}/converted-by-windows") + (polling ? "/polled" : "");
            recorder.DataAvailable += OnData;
            recorder.RecordingStopped += OnStopped;
        }

        /// <summary>Opens the Windows default input and returns once it is really capturing.</summary>
        public static Capture Open(Recorder owner, bool deviceFormat, bool polling)
        {
            MMDevice device;
            using (var enumerator = new MMDeviceEnumerator())
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            WasapiRecorder? recorder = null;
            Capture capture;
            try
            {
                var builder = new WasapiRecorderBuilder()
                    .WithDevice(device)
                    .WithBufferLength(BufferMilliseconds)
                    .WithMmcssThreadPriority("Audio");
                if (polling) builder = builder.WithPollingSync();
                if (!deviceFormat) builder = builder.WithFormat(new WaveFormat(SampleRate, 16, 1));   // shared mode: the engine converts
                recorder = builder.Build();
                capture = new Capture(owner, device, recorder, deviceFormat, polling);
                recorder.StartRecording();
            }
            catch
            {
                recorder?.Dispose();   // no capture thread yet, so this does not wait
                device.Dispose();
                throw;
            }
            capture.WaitUntilRunning();
            return capture;
        }

        private static string MixFormatOf(MMDevice device)
        {
            try
            {
                using var client = device.CreateAudioClient();
                return Converter.Describe(client.MixFormat);
            }
            catch { return "unknown"; }
        }

        /// <summary>NAudio starts the stream on its own thread after StartRecording returns. Wait for it:
        /// a failure there has to reach the press (and the other capture path), not end as a silent take.</summary>
        private void WaitUntilRunning()
        {
            if (!SpinWait.SpinUntil(() => _recorder.CaptureState != CaptureState.Starting, OpenTimeout))
            {
                _ = CloseAsync();   // it may start yet: this stops it then
                throw new TimeoutException(NotResponding);
            }
            if (_recorder.CaptureState == CaptureState.Capturing) return;
            var error = _stopped.Task.GetAwaiter().GetResult();   // RecordingStopped follows the state at once
            Release();
            if (error != null) ExceptionDispatchInfo.Throw(error);
            throw new IOException("capture stopped as soon as it started");
        }

        private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
        {
            // The first packet after Start usually carries this flag; on a later one the engine has
            // dropped audio because this thread fell behind.
            if ((flags & AudioClientBufferFlags.DataDiscontinuity) != 0 && _packets > 0) Gaps++;
            _packets++;
            _owner.Append(_converter.Convert(buffer));
        }

        private static readonly TimeSpan CloseGiveUp = TimeSpan.FromSeconds(30);

        private void OnStopped(object? sender, StoppedEventArgs e)
        {
            // A device unplugged or taken over mid-take tears the capture thread down with an exception;
            // without this the take would come back silent with nothing in the log to say why.
            if (e.Exception != null) Log.Write($"capture failed: {e.Exception.GetType().Name}: {e.Exception.Message}");
            _stopped.TrySetResult(e.Exception);
        }

        /// <summary>Stops the stream and lets go of the device. Called once per capture.</summary>
        public async Task CloseAsync()
        {
            _recorder.DataAvailable -= OnData;   // nothing more goes into the take
            // NAudio 3.1.0's capture thread writes Capturing right after IAudioClient::Start returns, which
            // overwrites a StopRecording that came earlier (the older WasapiCapture guarded this case,
            // WasapiRecorder does not). So keep asking until the thread itself says it has finished.
            var giveUp = DateTime.UtcNow + CloseGiveUp;
            for (int wait = 10; ; wait = Math.Min(wait * 2, 250))
            {
                _recorder.StopRecording();
                if (await Task.WhenAny(_stopped.Task, Task.Delay(wait)).ConfigureAwait(false) == _stopped.Task) break;
                if (DateTime.UtcNow > giveUp)
                {
                    // A driver stuck inside a call: stop asking. Disposing would join a thread that never ends.
                    Log.Write("capture: the device never confirmed the stop; left alone");
                    return;
                }
            }
            Release();
        }

        private void Release()
        {
            _recorder.Dispose();   // the capture thread has finished, so the join inside returns at once
            _device.Dispose();
        }
    }

    /// <summary>Turns the packets of one stream into 16 kHz mono: 16, 24 or 32-bit PCM or 32-bit float,
    /// any channel count and rate. The engine-converted stream (16 kHz mono PCM16) only needs scaling.</summary>
    private sealed class Converter
    {
        private enum Sample { Pcm16, Pcm24, Pcm32, Float32 }

        private readonly Sample _sample;
        private readonly int _channels;
        private readonly int _frameBytes;
        private readonly WdlResampler? _resampler;   // only when the stream is not at 16 kHz already
        private readonly double _outPerIn;
        private float[] _mono = Array.Empty<float>();
        private float[] _out = Array.Empty<float>();

        public Converter(WaveFormat format)
        {
            var f = Standard(format);
            _sample = (f.Encoding, f.BitsPerSample) switch
            {
                (WaveFormatEncoding.Pcm, 16) => Sample.Pcm16,
                (WaveFormatEncoding.Pcm, 24) => Sample.Pcm24,
                (WaveFormatEncoding.Pcm, 32) => Sample.Pcm32,
                (WaveFormatEncoding.IeeeFloat, 32) => Sample.Float32,
                _ => throw new NotSupportedException($"capture format {Describe(format)} is not supported"),
            };
            if (f.Channels < 1) throw new NotSupportedException($"capture format {Describe(format)} is not supported");
            _channels = f.Channels;
            _frameBytes = f.BitsPerSample / 8 * f.Channels;
            if (f.SampleRate != SampleRate)
            {
                // Windowed sinc: a real low-pass, so what the device hears above 8 kHz does not fold back
                // into the speech band.
                _resampler = new WdlResampler();
                _resampler.SetMode(false, 0, true);
                _resampler.SetFeedMode(true);   // each packet goes in whole, so nothing is ever padded with silence mid-take
                _resampler.SetRates(f.SampleRate, SampleRate);
                _outPerIn = (double)SampleRate / f.SampleRate;
            }
        }

        /// <summary>WASAPI mix formats come as WAVEFORMATEXTENSIBLE; this is the plain PCM or float equivalent.</summary>
        private static WaveFormat Standard(WaveFormat format) =>
            format is WaveFormatExtensible x ? x.ToStandardWaveFormat() : format;

        public static string Describe(WaveFormat format)
        {
            var f = Standard(format);
            string kind = f.Encoding switch
            {
                WaveFormatEncoding.IeeeFloat => "float",
                WaveFormatEncoding.Pcm => "pcm",
                var other => other.ToString(),
            };
            return $"{f.SampleRate}Hz/{f.Channels}ch/{kind}{f.BitsPerSample}";
        }

        /// <summary>Converts one packet. The result is only valid until the next call.</summary>
        public ReadOnlySpan<float> Convert(ReadOnlySpan<byte> packet)
        {
            int frames = packet.Length / _frameBytes;
            if (_mono.Length < frames) _mono = new float[frames];
            for (int i = 0, s = 0; i < frames; i++)
            {
                float sum = 0;
                for (int c = 0; c < _channels; c++) sum += Read(packet, s++);
                _mono[i] = sum / _channels;
            }
            if (_resampler == null) return _mono.AsSpan(0, frames);

            int n = _resampler.ResamplePrepare(frames, 1, out var input);
            _mono.AsSpan(0, n).CopyTo(input);
            int room = (int)(frames * _outPerIn) + 16;   // this packet's share and then some: the filter holds a few samples back each time
            if (_out.Length < room) _out = new float[room];
            int made = _resampler.ResampleOut(_out, n, room, 1);
            return _out.AsSpan(0, made);
        }

        private float Read(ReadOnlySpan<byte> p, int s) => _sample switch
        {
            Sample.Pcm16 => BinaryPrimitives.ReadInt16LittleEndian(p[(s * 2)..]) / 32768f,
            Sample.Pcm24 => (p[s * 3] | p[s * 3 + 1] << 8 | (sbyte)p[s * 3 + 2] << 16) / 8388608f,
            Sample.Pcm32 => BinaryPrimitives.ReadInt32LittleEndian(p[(s * 4)..]) / 2147483648f,
            _ => BinaryPrimitives.ReadSingleLittleEndian(p[(s * 4)..]),
        };
    }
}
