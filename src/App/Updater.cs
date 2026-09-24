// Over-the-air updates. A small JSON manifest in the repository says which
// version is current and where its installer lives; the app checks it on start
// and every few hours, downloads the installer, verifies its SHA-256 and runs
// it silently. The installer closes and relaunches the app.

using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GigaPisar.App;

public sealed class UpdateInfo
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("notes_en")] public string NotesEn { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

public static class Updater
{
    /// <summary>Manifest locations, tried in order. Add mirrors here.</summary>
    private static readonly string[] ManifestUrls =
    {
        "https://raw.githubusercontent.com/moznoazachem/giga-pisar-win/main/update.json",
    };

    /// <summary>Test aid: PISAR_UPDATE_URL overrides the manifest location and makes the first check immediate.</summary>
    private static readonly string? OverrideUrl = Environment.GetEnvironmentVariable("PISAR_UPDATE_URL") is { Length: > 0 } u ? u : null;

    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    public static readonly TimeSpan FirstCheckDelay = OverrideUrl != null ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(45);
    private const long MaxInstallerBytes = 512L << 20;

    public static string UpdatesDir => Path.Combine(Settings.LocalDataDir, "updates");

    /// <summary>Returns the manifest when it advertises a version newer than ours, otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        using var http = NewClient();
        foreach (var url in OverrideUrl != null ? new[] { OverrideUrl } : ManifestUrls)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(20));
                var json = await http.GetStringAsync(url + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds(), cts.Token);
                var info = JsonSerializer.Deserialize<UpdateInfo>(json);
                if (info == null || !Version.TryParse(info.Version, out var remote)) continue;
                if (!Version.TryParse(PisarApp.Version, out var local)) return null;
                Log.Write($"update check: local {local}, remote {remote}");
                bool trusted = info.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || OverrideUrl != null;
                return remote > local && trusted ? info : null;
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Log.Write($"update check failed ({url}): {e.Message}");
            }
        }
        return null;
    }

    /// <summary>Downloads the installer next to the app data, verifies it, returns its path.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<(long received, long total)> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(UpdatesDir);
        var path = Path.Combine(UpdatesDir, $"GigaPisar-Setup-{info.Version}.exe");
        var part = path + ".part";

        using var http = NewClient();
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            using var response = await http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, idle.Token);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? info.Size;
            if (total > MaxInstallerBytes) throw new InvalidDataException("installer too large");

            await using (var net = await response.Content.ReadAsStreamAsync(idle.Token))
            await using (var file = File.Create(part))
            {
                var buffer = new byte[1 << 16];
                long received = 0;
                int n;
                while ((n = await net.ReadAsync(buffer, idle.Token)) > 0)
                {
                    idle.CancelAfter(TimeSpan.FromSeconds(60));
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                    received += n;
                    if (received > MaxInstallerBytes) throw new InvalidDataException("installer too large");
                    progress.Report((received, total));
                }
            }

            await using (var check = File.OpenRead(part))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(check, ct)).ToLowerInvariant();
                if (!string.Equals(actual, info.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("checksum mismatch");
            }
            File.Move(part, path, overwrite: true);
            return path;
        }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { }
        }
    }

    /// <summary>Runs the installer silently. It replaces the files and relaunches the app; we exit right after.</summary>
    public static void Install(string installerPath)
    {
        // Keep the user's autostart choice: the installer's task would otherwise reset it to "on".
        var tasks = Autostart.IsEnabled() ? "autostart" : "!autostart";
        var psi = new System.Diagnostics.ProcessStartInfo(installerPath)
        {
            Arguments = $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /MERGETASKS=\"{tasks}\"",
            UseShellExecute = true,
        };
        System.Diagnostics.Process.Start(psi);
    }

    /// <summary>Removes downloaded installers from earlier updates.</summary>
    public static void Cleanup()
    {
        try
        {
            if (!Directory.Exists(UpdatesDir)) return;
            foreach (var f in Directory.GetFiles(UpdatesDir)) File.Delete(f);
        }
        catch { }
    }

    private static HttpClient NewClient()
    {
        var handler = new SocketsHttpHandler { DefaultProxyCredentials = System.Net.CredentialCache.DefaultCredentials };
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GigaPisar/" + PisarApp.Version + " (Windows)");
        http.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        return http;
    }
}
