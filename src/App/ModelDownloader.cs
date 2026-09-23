// First-run download of the recognition model: a tar.gz from GitHub Releases,
// unpacked into %LOCALAPPDATA%\GigaPisar\model and verified file by file
// against known SHA-256 hashes. Runs once; the app checks the folder on every start.

using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

namespace GigaPisar.App;

public enum DownloadFailure { Network, NoSpace, Corrupt }

public sealed class ModelDownloadException(DownloadFailure kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public DownloadFailure Kind { get; } = kind;
}

public static class ModelDownloader
{
    public const string ModelUrl =
        "https://github.com/moznoazachem/giga-pisar-cli/releases/latest/download/gigaam-v3-onnx-int8.tar.gz";

    /// <summary>GigaAM v3 e2e_rnnt, int8 ONNX export. The archive may be repacked; the files inside must not change.</summary>
    private static readonly Dictionary<string, string> ExpectedSha256 = new(StringComparer.OrdinalIgnoreCase)
    {
        ["v3_e2e_rnnt_encoder.onnx"] = "dbea5c6158413e34b3707b99b65ec394c63cbd32da4164311f86dd65f86563d6",
        ["v3_e2e_rnnt_decoder.onnx"] = "a0f27fd86246d57cbe2c7138f3355591fd039e3e1015fd4ff2fd2d3d2d4d319d",
        ["v3_e2e_rnnt_joint.onnx"] = "8bf573aca80d99ca4226aa0f9d43398998280ec505705955b5c6d92238b3e6d5",
        ["v3_e2e_rnnt_tokenizer.model"] = "828c12c991019eef952a960661f25a92d6ad279591e2ea466b4aeddf1d20a18a",
    };

    private const long MaxArchiveBytes = 2L << 30;          // sanity cap: the real archive is ~200 MB
    private const long RequiredFreeBytes = 1L << 30;        // archive + unpacked copy + headroom
    private const int ReadBufferBytes = 1 << 16;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    public sealed record Progress(long Received, long Total, string Stage);

    public static async Task DownloadAsync(string targetDir, IProgress<Progress> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Settings.LocalDataDir);
        var archive = Path.Combine(Settings.LocalDataDir, "model.tar.gz.part");
        var temp = targetDir + ".tmp";

        try
        {
            var free = new DriveInfo(Path.GetPathRoot(Settings.LocalDataDir)!).AvailableFreeSpace;
            if (free < RequiredFreeBytes)
                throw new ModelDownloadException(DownloadFailure.NoSpace, $"only {free >> 20} MB free");

            await FetchAsync(archive, progress, ct);

            progress.Report(new Progress(0, 0, "unpack"));
            await Task.Run(() => Unpack(archive, temp, ct), ct);

            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
            Directory.Move(temp, targetDir);
            progress.Report(new Progress(1, 1, "done"));
        }
        catch (ModelDownloadException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (IOException e) when (e.HResult == unchecked((int)0x80070070))   // ERROR_DISK_FULL
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
        finally
        {
            try { if (File.Exists(archive)) File.Delete(archive); } catch { }
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
        }
    }

    private static async Task FetchAsync(string archive, IProgress<Progress> progress, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler { DefaultProxyCredentials = CredentialCache.DefaultCredentials };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GigaPisar/" + PisarApp.Version + " (Windows)");

        // No overall deadline (slow links are fine), but a stalled connection gives up after IdleTimeout.
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(IdleTimeout);

        using var response = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, idle.Token);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? -1;
        if (total > MaxArchiveBytes) throw new ModelDownloadException(DownloadFailure.Corrupt, $"archive too large: {total}");

        await using var net = await response.Content.ReadAsStreamAsync(idle.Token);
        await using var file = File.Create(archive);
        var buffer = new byte[ReadBufferBytes];
        long received = 0;
        int n;
        var lastReport = DateTime.MinValue;
        while ((n = await net.ReadAsync(buffer, idle.Token)) > 0)
        {
            idle.CancelAfter(IdleTimeout);
            await file.WriteAsync(buffer.AsMemory(0, n), ct);
            received += n;
            if (received > MaxArchiveBytes) throw new ModelDownloadException(DownloadFailure.Corrupt, "archive too large");
            if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 100)
            {
                progress.Report(new Progress(received, total, "download"));
                lastReport = DateTime.UtcNow;
            }
        }
        if (total > 0 && received != total)
            throw new ModelDownloadException(DownloadFailure.Network, $"incomplete download: {received} of {total}");
        progress.Report(new Progress(received, total, "download"));
    }

    private static void Unpack(string archive, string temp, CancellationToken ct)
    {
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        Directory.CreateDirectory(temp);

        using (var fs = File.OpenRead(archive))
        using (var gz = new GZipStream(fs, CompressionMode.Decompress))
        using (var tar = new TarReader(gz))
        {
            while (tar.GetNextEntry() is { } entry)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.EntryType != TarEntryType.RegularFile && entry.EntryType != TarEntryType.V7RegularFile) continue;
                var name = Path.GetFileName(entry.Name);
                if (name.Length == 0 || name.StartsWith("._")) continue;   // AppleDouble junk
                if (entry.Length > MaxArchiveBytes) throw new InvalidDataException($"entry too large: {name}");
                entry.ExtractToFile(Path.Combine(temp, name), overwrite: true);
            }
        }

        foreach (var (name, expected) in ExpectedSha256)
        {
            var path = Path.Combine(temp, name);
            if (!File.Exists(path)) throw new InvalidDataException($"missing {name}");
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (actual != expected) throw new InvalidDataException($"checksum mismatch for {name}");
        }
        if (!File.Exists(Path.Combine(temp, Core.Recognizer.ModelName + ".yaml")))
            throw new InvalidDataException("missing model config");
    }
}
