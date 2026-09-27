// User settings stored as JSON in %APPDATA%\GigaPisar\settings.json.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GigaPisar.App;

public enum InsertMode { Type, Paste }

public sealed class Settings
{
    public const int LeftCtrlWinHotkey = 0x10000;

    /// <summary>Virtual-key code of the push-to-talk key. Default: right Ctrl.</summary>
    public int HotkeyVk { get; set; } = 0xA3;
    public InsertMode InsertMode { get; set; } = InsertMode.Paste;
    public bool ShowOverlay { get; set; } = true;
    public bool KeepLastRecording { get; set; } = false;
    public bool FirstRunDone { get; set; } = false;
    public UiLanguage Language { get; set; } = UiLanguage.Auto;
    public bool CheckUpdates { get; set; } = true;
    public bool CleanupEnabled { get; set; }
    public string CleanupEndpointUrl { get; set; } = "";
    /// <summary>Plain API key in memory only; on disk it lives in <see cref="CleanupApiKeyProtected"/>.</summary>
    [JsonIgnore]
    public string CleanupApiKey { get; set; } = "";

    /// <summary>API key encrypted with DPAPI for the current Windows user (base64).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CleanupApiKeyProtected
    {
        get => CleanupApiKey.Length == 0 ? null : Protect(CleanupApiKey);
        set => CleanupApiKey = Unprotect(value);
    }

    /// <summary>Read-only migration of a plain-text key written by the first cleanup build.</summary>
    [JsonPropertyName("CleanupApiKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyCleanupApiKey
    {
        get => null;
        set { if (!string.IsNullOrEmpty(value) && CleanupApiKey.Length == 0) CleanupApiKey = value; }
    }
    public string CleanupModel { get; set; } = "";
    public string CleanupPrompt { get; set; } = SpeechCleanup.DefaultPrompt;

    /// <summary>Where the user dragged the overlay to (screen pixels, window top-left); null means "follow the caret".</summary>
    public int? OverlayX { get; set; }
    public int? OverlayY { get; set; }

    public static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GigaPisar");
    public static string LocalDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GigaPisar");
    public static string ModelDir => Path.Combine(LocalDataDir, "model");
    public static string SettingsPath => Path.Combine(AppDataDir, "settings.json");
    public static string LogPath => Path.Combine(LocalDataDir, "pisar.log");
    public static string LastTakePath => Path.Combine(LocalDataDir, "last.wav");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new Settings();
        }
        catch (Exception e) { Log.Write($"settings load failed: {e.Message}"); }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception e) { Log.Write($"settings save failed: {e.Message}"); }
    }

    private static readonly byte[] KeyEntropy = Encoding.UTF8.GetBytes("GigaPisar.CleanupApiKey");

    private static string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), KeyEntropy, DataProtectionScope.CurrentUser));

    private static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), KeyEntropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception e)
        {
            // Another user or machine: the key cannot be recovered, ask for it again.
            Log.Write($"api key unprotect failed: {e.GetType().Name}");
            return "";
        }
    }

    public static readonly (int vk, string ru, string en)[] HotkeyChoices =
    {
        (0xA3, "Правый Ctrl", "Right Ctrl"),
        (LeftCtrlWinHotkey, "Левый Ctrl + Win", "Left Ctrl + Win"),
        (0xA5, "Правый Alt", "Right Alt"),
        (0xA1, "Правый Shift", "Right Shift"),
        (0x14, "Caps Lock", "Caps Lock"),
        (0x91, "Scroll Lock", "Scroll Lock"),
        (0x2D, "Insert", "Insert"),
    };

    public static string HotkeyTitle(int vk)
    {
        foreach (var (k, ru, en) in HotkeyChoices) if (k == vk) return L.T(ru, en);
        return L.T($"клавиша {vk:X2}", $"key {vk:X2}");
    }
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Settings.LocalDataDir);
                var fi = new FileInfo(Settings.LogPath);
                if (fi.Exists && fi.Length > 1_000_000) fi.MoveTo(Settings.LogPath + ".1", overwrite: true);
                File.AppendAllText(Settings.LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break the app */ }
    }
}
