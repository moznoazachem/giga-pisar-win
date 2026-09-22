// First-run download of the recognition model: a tar.gz from GitHub Releases,
// unpacked into %LOCALAPPDATA%\GigaPisar\model. Runs once; the app checks
// the folder on every start.

using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http;

namespace GigaPisar.App;

public static class ModelDownloader
{
    public const string ModelUrl =
        "https://github.com/moznoazachem/giga-pisar-cli/releases/latest/download/gigaam-v3-onnx-int8.tar.gz";

    public sealed record Progress(long Received, long Total, string Stage);

    public static async Task DownloadAsync(string targetDir, IProgress<Progress> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Settings.LocalDataDir);
        var archive = Path.Combine(Settings.LocalDataDir, "model.tar.gz.part");

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GigaPisar/1.0 (Windows)");
        http.Timeout = TimeSpan.FromMinutes(30);

        using (var response = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? -1;
            await using var net = await response.Content.ReadAsStreamAsync(ct);
            await using var file = File.Create(archive);
            var buffer = new byte[1 << 16];
            long received = 0;
            int n;
            var lastReport = DateTime.MinValue;
            while ((n = await net.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, n), ct);
                received += n;
                if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 100)
                {
                    progress.Report(new Progress(received, total, "download"));
                    lastReport = DateTime.UtcNow;
                }
            }
            progress.Report(new Progress(received, total, "download"));
        }

        progress.Report(new Progress(0, 0, "unpack"));
        var temp = targetDir + ".tmp";
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        Directory.CreateDirectory(temp);

        await Task.Run(() =>
        {
            using var fs = File.OpenRead(archive);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var tar = new TarReader(gz);
            while (tar.GetNextEntry() is { } entry)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.EntryType != TarEntryType.RegularFile && entry.EntryType != TarEntryType.V7RegularFile) continue;
                var name = Path.GetFileName(entry.Name);
                if (name.Length == 0 || name.StartsWith("._")) continue;   // AppleDouble junk
                entry.ExtractToFile(Path.Combine(temp, name), overwrite: true);
            }
        }, ct);

        File.Delete(archive);
        if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
        Directory.Move(temp, targetDir);
        progress.Report(new Progress(1, 1, "done"));
    }
}
