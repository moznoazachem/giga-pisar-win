// Application entry: tray icon, push-to-talk wiring, model bootstrap.
//
// Flow: single-instance check -> settings -> tray icon -> model (download on
// first run) -> recognizer warm-up -> keyboard hook. Hold the key: record.
// Release: recognize and insert the text where the caret is.

using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace GigaPisar.App;

public partial class PisarApp : Application
{
    public const string Version = "1.0.0";
    public const string SiteUrl = "https://gigapisar.github.io";
    public const string RepoUrl = "https://github.com/moznoazachem/giga-pisar-win";

    private static Mutex? _instanceMutex;

    /// <summary>Peak below this (about -75 dBFS) is digital silence: nothing reached the input at all.</summary>
    private const float SilenceFloor = 0.0002f;

    /// <summary>Quiet takes are scaled up to this peak before recognition; the model expects normal speech levels.</summary>
    private const float TargetPeak = 0.5f;
    private const float MaxGain = 200f;

    private Settings _settings = new();
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _iconIdle;
    private System.Drawing.Icon? _iconBusy;
    private Core.Recognizer? _recognizer;
    private KeyboardHook? _hook;
    private readonly Recorder _recorder = new();
    private OverlayWindow? _overlay;
    private SettingsWindow? _settingsWindow;
    private bool _busy;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "--transcribe")
            return CliTranscribe(args[1], args[2]);

        _instanceMutex = new Mutex(true, "GigaPisar.SingleInstance", out bool first);
        if (!first) return 0;

        var app = new PisarApp();
        app.InitializeComponent();
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Write($"unhandled: {e.Exception}");
            e.Handled = true;
        };
        app.Startup += app.OnStartup;
        return app.Run();
    }

    /// <summary>Headless mode for testing: recognize a WAV file, write the text to a file.</summary>
    private static int CliTranscribe(string wavPath, string outPath)
    {
        try
        {
            var modelDir = Environment.GetEnvironmentVariable("PISAR_MODEL_DIR") is { Length: > 0 } env ? env : Settings.ModelDir;
            var sw = Stopwatch.StartNew();
            using var rec = new Core.Recognizer(modelDir);
            var load = sw.Elapsed.TotalSeconds;
            var (samples, rate) = Core.AudioUtils.ReadWav(wavPath);
            sw.Restart();
            var text = rec.Transcribe(samples, rate);
            var run = sw.Elapsed.TotalSeconds;
            File.WriteAllText(outPath,
                $"load={load:F2}s recognize={run:F2}s audio={(double)samples.Length / rate:F1}s rss={Process.GetCurrentProcess().WorkingSet64 / 1048576}MB\n{text}\n");
            return 0;
        }
        catch (Exception e)
        {
            File.WriteAllText(outPath, "ERROR: " + e);
            return 1;
        }
    }

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        _settings = Settings.Load();
        L.Apply(_settings.Language);
        Log.Write($"start v{Version}");

        _iconIdle = LoadIcon();
        _iconBusy = MakeBusyIcon(_iconIdle);
        _tray = new Forms.NotifyIcon
        {
            Icon = _iconIdle,
            Text = L.T("Гига Писарь", "Giga Pisar"),
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _tray.DoubleClick += (_, _) => ShowSettings();

        if (!Core.Recognizer.ModelExists(Settings.ModelDir))
        {
            var ok = await new DownloadWindow().RunAsync(Settings.ModelDir);
            if (!ok) { Quit(); return; }
        }

        SetStatus(L.T("Загружаю модель…", "Loading the model…"));
        try
        {
            _recognizer = await Task.Run(() => new Core.Recognizer(Settings.ModelDir));
        }
        catch (Exception ex)
        {
            Log.Write($"model load failed: {ex}");
            MessageBox.Show(L.T("Не удалось загрузить модель распознавания.", "Could not load the speech model.") + "\n\n" + ex.Message,
                L.T("Гига Писарь", "Giga Pisar"), MessageBoxButton.OK, MessageBoxImage.Error);
            Quit();
            return;
        }

        try
        {
            _hook = new KeyboardHook(_settings.HotkeyVk);
            _hook.Pressed += OnKeyPressed;
            _hook.Released += OnKeyReleased;
        }
        catch (Exception ex)
        {
            Log.Write($"hook failed: {ex}");
        }

        SetStatus(null);
        if (!_settings.FirstRunDone)
        {
            _settings.FirstRunDone = true;
            _settings.Save();
            // A dictation tool is useless when it is not running: start with Windows unless the user opts out.
            try { Autostart.Set(true); } catch (Exception ex) { Log.Write($"autostart failed: {ex.Message}"); }
            _tray.ShowBalloonTip(8000, L.T("Гига Писарь готов", "Giga Pisar is ready"),
                L.T($"Поставьте курсор в любой текст, зажмите {Settings.HotkeyTitle(_settings.HotkeyVk)} и говорите. Отпустите, и текст появится сам.",
                    $"Put the cursor in any text, hold {Settings.HotkeyTitle(_settings.HotkeyVk)} and speak. Release, and the text appears by itself."),
                Forms.ToolTipIcon.None);
        }
    }

    // ── push-to-talk ─────────────────────────────────────────────

    private void OnKeyPressed()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_busy || _recognizer == null || _recorder.IsRecording) return;
            try
            {
                _recorder.Start();
            }
            catch (Exception ex)
            {
                Log.Write($"mic failed: {ex.Message}");
                _tray?.ShowBalloonTip(5000, L.T("Микрофон недоступен", "Microphone unavailable"),
                    L.T("Проверьте, что микрофон подключён и разрешён в Параметрах, раздел Конфиденциальность, Микрофон.",
                        "Check that a microphone is connected and allowed in Settings, Privacy, Microphone."),
                    Forms.ToolTipIcon.Warning);
                return;
            }
            if (_tray != null) _tray.Icon = _iconBusy;
            if (_settings.ShowOverlay)
            {
                _overlay ??= new OverlayWindow();
                _overlay.ShowListening(_recorder);
            }
        });
    }

    private void OnKeyReleased()
    {
        Dispatcher.BeginInvoke(async () =>
        {
            if (!_recorder.IsRecording) return;
            var samples = _recorder.Stop();
            _overlay?.ShowRecognizing();
            _busy = true;
            try
            {
                if (_settings.KeepLastRecording)
                    Core.AudioUtils.WriteWav(samples, Recorder.SampleRate, Path.Combine(Settings.LocalDataDir, "last.wav"));

                string text = "";
                float peak = _recorder.TakePeak;
                // The model invents words when fed silence or flat noise. Absolute level is a poor
                // test (a line-level receiver on a mic jack sits 50 dB down), so we look for speech
                // dynamics: loud stretches well above the quiet ones.
                bool silent = peak < SilenceFloor || !HasSpeechDynamics(samples);
                Log.Write($"take {samples.Length / (double)Recorder.SampleRate:F1}s peak {20 * Math.Log10(Math.Max(peak, 1e-9)):F0} dBFS silent={silent}");
                if (!silent && samples.Length > Recorder.SampleRate / 4)   // shorter than a quarter second is a slip
                {
                    // Windows input levels vary wildly (a line-level receiver on a mic jack can sit
                    // 50 dB down). Normalize quiet takes so the model sees ordinary speech.
                    if (peak < TargetPeak)
                    {
                        float gain = Math.Min(TargetPeak / peak, MaxGain);
                        for (int i = 0; i < samples.Length; i++) samples[i] *= gain;
                        Log.Write($"take peak {20 * Math.Log10(peak):F0} dBFS, gain x{gain:F0}");
                    }
                    text = await Task.Run(() => _recognizer!.Transcribe(samples, Recorder.SampleRate));
                }

                if (text.Length > 0)
                {
                    _overlay?.HideNow();
                    TextInserter.Insert(text, _settings.InsertMode);
                }
                else if (_overlay != null)
                {
                    // Silence in, nothing out: say so instead of quietly doing nothing.
                    _overlay.ShowHint(silent
                        ? L.T("Тишина на входе. Проверьте микрофон и его громкость: " + Recorder.DefaultDeviceName(),
                              "Silence on input. Check the microphone and its level: " + Recorder.DefaultDeviceName())
                        : L.T("Не разобрал. Попробуйте ещё раз ближе к микрофону.",
                              "Could not make it out. Try again closer to the microphone."));
                }
            }
            catch (Exception ex)
            {
                Log.Write($"recognize failed: {ex}");
                _overlay?.HideNow();
            }
            finally
            {
                _busy = false;
                if (_tray != null) _tray.Icon = _iconIdle;
            }
        });
    }

    /// <summary>True when the take has bursts (speech) rather than a flat floor (silence or hum).</summary>
    private static bool HasSpeechDynamics(float[] samples)
    {
        const int window = Recorder.SampleRate / 10;   // 100 ms
        int n = samples.Length / window;
        if (n < 3) return true;   // too short to judge; let the model decide
        var rms = new double[n];
        for (int w = 0; w < n; w++)
        {
            double sum = 0;
            for (int i = w * window; i < (w + 1) * window; i++) sum += samples[i] * samples[i];
            rms[w] = Math.Sqrt(sum / window);
        }
        Array.Sort(rms);
        double quiet = rms[n / 4] + 1e-7;          // lower quartile: the floor
        double loud = rms[n - 1 - n / 20];         // near the top, ignoring one-off clicks
        return loud / quiet > 2;   // permissive on purpose: a wrong "silence" verdict hides real speech
    }

    // ── tray ─────────────────────────────────────────────────────

    private Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        var title = new Forms.ToolStripMenuItem(L.T($"Гига Писарь {Version}", $"Giga Pisar {Version}")) { Enabled = false };
        var hint = new Forms.ToolStripMenuItem("") { Enabled = false };
        menu.Items.Add(title);
        menu.Items.Add(hint);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(L.T("Настройки…", "Settings…"), null, (_, _) => ShowSettings());
        var autostart = new Forms.ToolStripMenuItem(L.T("Запускать при входе в Windows", "Start when I sign in")) { CheckOnClick = true };
        autostart.Click += (_, _) => Autostart.Set(autostart.Checked);
        menu.Items.Add(autostart);

        var language = new Forms.ToolStripMenuItem(L.T("Язык", "Language"));
        foreach (var (choice, name) in new[] { (UiLanguage.Auto, L.T("Как в системе", "Same as system")), (UiLanguage.Russian, "Русский"), (UiLanguage.English, "English") })
        {
            var item = new Forms.ToolStripMenuItem(name) { Checked = _settings.Language == choice };
            item.Click += (_, _) => { _settings.Language = choice; ApplySettings(); };
            language.DropDownItems.Add(item);
        }
        menu.Items.Add(language);

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(L.T("Сайт проекта", "Project website"), null, (_, _) => Open(SiteUrl));
        menu.Items.Add(L.T("Исходный код", "Source code"), null, (_, _) => Open(RepoUrl));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(L.T("Выход", "Quit"), null, (_, _) => Quit());
        menu.Opening += (_, _) =>
        {
            autostart.Checked = Autostart.IsEnabled();
            hint.Text = _recognizer == null ? L.T("Модель ещё не загружена", "Model not loaded yet")
                : L.T($"Зажмите {Settings.HotkeyTitle(_settings.HotkeyVk)} и говорите", $"Hold {Settings.HotkeyTitle(_settings.HotkeyVk)} and speak");
        };
        return menu;
    }

    private void SetStatus(string? status)
    {
        if (_tray == null) return;
        _tray.Text = status == null ? L.T("Гига Писарь", "Giga Pisar") : L.T($"Гига Писарь: {status}", $"Giga Pisar: {status}");
    }

    public void ShowSettings()
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(_settings, ApplySettings);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void ApplySettings()
    {
        _settings.Save();
        if (_hook != null) _hook.HotkeyVk = _settings.HotkeyVk;

        bool wasRussian = L.Russian;
        L.Apply(_settings.Language);
        if (wasRussian != L.Russian)
        {
            // Language changed: rebuild everything that carries text.
            if (_tray != null) { _tray.ContextMenuStrip?.Dispose(); _tray.ContextMenuStrip = BuildMenu(); }
            SetStatus(null);
            _settingsWindow?.Localize();
        }
    }

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private void Quit()
    {
        _hook?.Dispose();
        _recorder.Dispose();
        _overlay?.Close();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _recognizer?.Dispose();
        Shutdown();
    }

    // ── icons ────────────────────────────────────────────────────

    private static System.Drawing.Icon LoadIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(path)) return new System.Drawing.Icon(path, 32, 32);
        return System.Drawing.SystemIcons.Application;
    }

    /// <summary>Same icon with a red dot: "listening".</summary>
    private static System.Drawing.Icon MakeBusyIcon(System.Drawing.Icon idle)
    {
        using var bmp = idle.ToBitmap();
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int d = bmp.Width * 7 / 16;
        using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 226, 61, 61));
        using var pen = new System.Drawing.Pen(System.Drawing.Color.White, Math.Max(1, bmp.Width / 16));
        g.FillEllipse(brush, bmp.Width - d, bmp.Height - d, d - 1, d - 1);
        g.DrawEllipse(pen, bmp.Width - d, bmp.Height - d, d - 1, d - 1);
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }
}
