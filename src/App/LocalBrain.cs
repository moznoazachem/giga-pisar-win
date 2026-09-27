// The Brain on this computer: llama.cpp's llama-server with a small Qwen model,
// the same pair the macOS app uses. Nothing goes online except the one-time
// download of the engine (~18 MB) and the model (~2 GB), both pinned by SHA-256.
//
// The server starts on the first command (a couple of seconds on a fast CPU,
// longer on a laptop), listens on a random loopback port with a random API key,
// and is stopped after 15 idle minutes because it holds ~2.5 GB of memory.
// It lives in a job object, so it dies together with Pisar even on a crash.

using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace GigaPisar.App;

public static class LocalBrain
{
    /// <summary>
    /// Whether "on this computer" is offered in the menus. Off until its own release
    /// (with the model mirror on GitHub); someone who already chose it keeps seeing it.
    /// </summary>
    public const bool Offered = false;

    public const string EngineTag = "b10701";
    private const string EngineUrl = "https://github.com/ggml-org/llama.cpp/releases/download/b10701/llama-b10701-bin-win-cpu-x64.zip";
    private const string EngineSha256 = "84ecf626a9893a7701a5883480b06fb91043ee9cb76de10c5aaeea43cfc7c680";

    public const string ModelTitle = "Qwen3 4B";
    public const string ModelFile = "Qwen3-4B-Instruct-2507-Q3_K_M.gguf";
    /// <summary>Our GitHub mirror first (fast from Russia, where Hugging Face is slow or blocked), then the original.</summary>
    private static readonly string[] ModelUrls =
    {
        "https://github.com/moznoazachem/giga-pisar-win/releases/download/brain-models/Qwen3-4B-Instruct-2507-Q3_K_M.gguf",
        "https://huggingface.co/unsloth/Qwen3-4B-Instruct-2507-GGUF/resolve/main/Qwen3-4B-Instruct-2507-Q3_K_M.gguf",
    };
    private const string ModelSha256 = "9c6e0763577125a994a9bea0bbd7a737ac4498b8a6a4e0f788727553af1806c9";
    public const long ModelBytes = 2_075_618_400;

    /// <summary>Below this much RAM the Brain would crowd everything else out; we say so before downloading.</summary>
    public const ulong RecommendedRamBytes = 8UL << 30;
    private const int ContextTokens = 2048;
    private static readonly TimeSpan IdleStop = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan DownloadIdleTimeout = TimeSpan.FromSeconds(60);

    public static string Dir => Path.Combine(Settings.LocalDataDir, "brain");
    private static string EngineDir => Path.Combine(Dir, "engine-" + EngineTag);
    private static string ServerExe => Path.Combine(EngineDir, "llama-server.exe");
    public static string ModelPath => Path.Combine(Dir, ModelFile);
    public static string LogPath => Path.Combine(Settings.LocalDataDir, "brain.log");

    public static bool Downloaded =>
        File.Exists(ServerExe) && File.Exists(ModelPath) && new FileInfo(ModelPath).Length == ModelBytes;

    // ── memory ───────────────────────────────────────────────────

    /// <summary>Model file plus context and llama.cpp overhead.</summary>
    public static ulong MemoryNeeded => (ulong)ModelBytes + (600UL << 20);

    public static (ulong total, ulong available) Memory()
    {
        var m = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        return Native.GlobalMemoryStatusEx(ref m) ? (m.ullTotalPhys, m.ullAvailPhys) : (0, 0);
    }

    public static string Gb(ulong bytes) => (bytes / (double)(1UL << 30)).ToString("0.0");

    // ── download ─────────────────────────────────────────────────

    /// <summary>Fetches the engine and the model (resuming a partial model), verifies both, unpacks the engine.</summary>
    public static async Task DownloadAsync(IProgress<ModelDownloader.Progress> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        long need = ModelBytes + (200L << 20);
        if (File.Exists(ModelPath + ".part")) need -= new FileInfo(ModelPath + ".part").Length;
        var free = new DriveInfo(Path.GetPathRoot(Dir)!).AvailableFreeSpace;
        if (free < need)
            throw new ModelDownloadException(DownloadFailure.NoSpace, $"only {free >> 20} MB free, need {need >> 20}");

        using var handler = new SocketsHttpHandler { DefaultProxyCredentials = CredentialCache.DefaultCredentials };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GigaPisar/" + PisarApp.Version + " (Windows)");

        try
        {
            if (!File.Exists(ServerExe))
            {
                var zip = Path.Combine(Dir, "engine.zip.part");
                if (File.Exists(zip)) File.Delete(zip);
                await FetchAsync(http, EngineUrl, zip, 64L << 20, null, ct);
                Verify(zip, EngineSha256, "engine");
                progress.Report(new ModelDownloader.Progress(0, 0, "unpack"));
                await Task.Run(() =>
                {
                    var temp = EngineDir + ".tmp";
                    if (Directory.Exists(temp)) Directory.Delete(temp, true);
                    ZipFile.ExtractToDirectory(zip, temp);
                    if (Directory.Exists(EngineDir)) Directory.Delete(EngineDir, true);
                    Directory.Move(temp, EngineDir);
                }, ct);
                File.Delete(zip);
            }

            if (!(File.Exists(ModelPath) && new FileInfo(ModelPath).Length == ModelBytes))
            {
                var part = ModelPath + ".part";
                for (int i = 0; ; i++)
                {
                    try
                    {
                        await FetchAsync(http, ModelUrls[i], part, ModelBytes, progress, ct);
                        break;
                    }
                    catch (Exception e) when (i + 1 < ModelUrls.Length && e is HttpRequestException or ModelDownloadException or OperationCanceledException
                                              && !ct.IsCancellationRequested)
                    {
                        // Same bytes everywhere (checked by SHA-256 below), so the next source continues the same .part.
                        Log.Write($"brain model source {i} failed: {e.Message}; trying the next one");
                    }
                }
                progress.Report(new ModelDownloader.Progress(0, 0, "verify"));
                try { await Task.Run(() => Verify(part, ModelSha256, "model"), ct); }
                catch (InvalidDataException) { File.Delete(part); throw; }   // a bad file must not be resumed
                File.Move(part, ModelPath, overwrite: true);
            }
            progress.Report(new ModelDownloader.Progress(1, 1, "done"));
        }
        catch (ModelDownloadException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (IOException e) when (e.HResult == unchecked((int)0x80070070))
        {
            throw new ModelDownloadException(DownloadFailure.NoSpace, e.Message, e);
        }
        catch (InvalidDataException e)
        {
            throw new ModelDownloadException(DownloadFailure.Corrupt, e.Message, e);
        }
        catch (Exception e)
        {
            throw new ModelDownloadException(DownloadFailure.Network, e.Message, e);
        }
    }

    /// <summary>Downloads url into path, continuing from what is already there (HTTP Range).</summary>
    private static async Task FetchAsync(HttpClient http, string url, string path, long expected,
        IProgress<ModelDownloader.Progress>? progress, CancellationToken ct)
    {
        long have = File.Exists(path) ? new FileInfo(path).Length : 0;
        if (have > expected) { File.Delete(path); have = 0; }

        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(DownloadIdleTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token);
        if (have > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) return;   // already complete
        response.EnsureSuccessStatusCode();
        if (have > 0 && response.StatusCode != HttpStatusCode.PartialContent) have = 0;               // server ignored Range

        long total = response.Content.Headers.ContentLength is long len ? have + len : expected;
        if (total > expected) throw new InvalidDataException($"{Path.GetFileName(path)} is larger than expected: {total}");

        await using var net = await response.Content.ReadAsStreamAsync(idle.Token);
        await using var file = new FileStream(path, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write);
        var buffer = new byte[1 << 16];
        long received = have;
        var lastReport = DateTime.MinValue;
        int n;
        while ((n = await net.ReadAsync(buffer, idle.Token)) > 0)
        {
            idle.CancelAfter(DownloadIdleTimeout);
            await file.WriteAsync(buffer.AsMemory(0, n), ct);
            received += n;
            if (received > expected) throw new InvalidDataException("download larger than expected");
            if (progress != null && (DateTime.UtcNow - lastReport).TotalMilliseconds > 100)
            {
                progress.Report(new ModelDownloader.Progress(received, total, "download"));
                lastReport = DateTime.UtcNow;
            }
        }
        if (received != total)
            throw new ModelDownloadException(DownloadFailure.Network, $"incomplete download: {received} of {total}");
    }

    private static void Verify(string path, string sha256, string what)
    {
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (actual != sha256) throw new InvalidDataException($"checksum mismatch for the {what}");
    }

    public static void DeleteModel()
    {
        Stop();
        try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); } catch (Exception e) { Log.Write($"brain delete failed: {e.Message}"); }
    }

    // ── server ───────────────────────────────────────────────────

    private static readonly object Gate = new();
    private static Process? _server;
    private static IntPtr _job;
    private static int _port;
    private static string _apiKey = "";
    private static System.Threading.Timer? _idleTimer;

    public static bool Running { get { lock (Gate) return _server is { HasExited: false }; } }
    public static string EndpointUrl => $"http://127.0.0.1:{_port}/v1";
    public static string ApiKey => _apiKey;

    /// <summary>Starts the server if needed and waits until it answers /health. Reports elapsed seconds while starting.</summary>
    public static async Task EnsureStartedAsync(Action<int>? startingTick, CancellationToken ct)
    {
        Process server;
        lock (Gate)
        {
            if (_server is { HasExited: false }) { TouchIdle(); return; }
            server = StartProcess();
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var sw = Stopwatch.StartNew();
        int lastTick = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (server.HasExited)
                throw new BrainException(L.T($"нейронка упала при запуске, подробности в {LogPath}",
                                             $"the Brain crashed on start, details in {LogPath}"));
            try
            {
                var body = await http.GetStringAsync($"http://127.0.0.1:{_port}/health", ct);
                if (body.Contains("ok")) break;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
            if (sw.Elapsed > StartTimeout)
            {
                Stop();
                throw new BrainException(L.T("нейронка не поднялась за полторы минуты", "the Brain did not come up within 90 seconds"));
            }
            int sec = (int)sw.Elapsed.TotalSeconds;
            if (sec >= 3 && sec != lastTick) { lastTick = sec; startingTick?.Invoke(sec); }
            await Task.Delay(400, ct);
        }
        Log.Write($"brain up in {sw.Elapsed.TotalSeconds:F1}s on port {_port}");
        TouchIdle();
    }

    private static Process StartProcess()
    {
        Stop();
        _port = FreePort();
        _apiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var psi = new ProcessStartInfo(ServerExe)
        {
            WorkingDirectory = EngineDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "-m", ModelPath, "--host", "127.0.0.1", "--port", _port.ToString(), "--api-key", _apiKey,
                                  "-c", ContextTokens.ToString(), "--no-webui" })
            psi.ArgumentList.Add(a);

        var log = new StreamWriter(LogPath, append: false) { AutoFlush = true };
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        bool closed = false;
        void Write(string? line)
        {
            if (line == null) return;
            lock (log) { if (!closed) try { log.WriteLine(line); } catch { } }
        }
        p.OutputDataReceived += (_, e) => Write(e.Data);
        p.ErrorDataReceived += (_, e) => Write(e.Data);
        p.Exited += (_, _) => { lock (log) { closed = true; log.Dispose(); } };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        AttachToJob(p);
        _server = p;
        Log.Write($"brain starting: {ModelFile}, engine {EngineTag}");
        return p;
    }

    /// <summary>Kill-on-close job: if Pisar exits or crashes, Windows ends the server too.</summary>
    private static void AttachToJob(Process p)
    {
        try
        {
            if (_job == IntPtr.Zero)
            {
                _job = Native.CreateJobObject(IntPtr.Zero, null);
                var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                Native.SetInformationJobObject(_job, Native.JobObjectExtendedLimitInformation, ref info,
                    (uint)Marshal.SizeOf<Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
            }
            if (!Native.AssignProcessToJobObject(_job, p.Handle))
                Log.Write($"brain job assign failed: {Marshal.GetLastWin32Error()}");
        }
        catch (Exception e) { Log.Write($"brain job failed: {e.Message}"); }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static void TouchIdle()
    {
        _idleTimer?.Dispose();
        _idleTimer = new System.Threading.Timer(_ =>
        {
            Log.Write("brain idle for 15 minutes, releasing memory");
            Stop();
        }, null, IdleStop, Timeout.InfiniteTimeSpan);
    }

    public static void Stop()
    {
        lock (Gate)
        {
            _idleTimer?.Dispose();
            _idleTimer = null;
            try { if (_server is { HasExited: false }) _server.Kill(); } catch { }
            _server?.Dispose();
            _server = null;
        }
    }
}

public sealed class BrainException(string reason) : Exception(reason);
