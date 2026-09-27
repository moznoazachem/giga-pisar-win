// Application entry: tray icon, push-to-talk wiring, model bootstrap.
//
// Flow: single-instance check -> settings -> tray icon -> model (download on
// first run) -> recognizer warm-up -> keyboard hook. Hold the key: record.
// Release: recognize and insert the text where the caret is.

using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Windows;
using Forms = System.Windows.Forms;

namespace GigaPisar.App;

public partial class PisarApp : Application
{
    public static readonly string Version = ReadVersion();
    public const string SiteUrl = "https://gigapisar.github.io";
    public const string RepoUrl = "https://github.com/moznoazachem/giga-pisar-win";

    private static Mutex? _instanceMutex;
    /// <summary>A second launch (Start menu, desktop shortcut) signals the running instance to open Settings.</summary>
    private static EventWaitHandle? _showSettingsSignal;
    private const string ShowSettingsSignalName = "GigaPisar.ShowSettings";
    private static bool JustUpdated;

    /// <summary>Peak below this (about -75 dBFS) is digital silence: nothing reached the input at all.</summary>
    private const float SilenceFloor = 0.0002f;

    /// <summary>Quiet takes are scaled up to this peak before recognition; the model expects normal speech levels.</summary>
    private const float TargetPeak = 0.5f;
    private const float MaxGain = 200f;

    /// <summary>Takes shorter than this are treated as an accidental key press.</summary>
    private const int MinTakeSamples = Recorder.SampleRate / 4;

    private Settings _settings = new();
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _iconIdle;
    private System.Drawing.Icon? _iconBusy;
    private IntPtr _busyIconHandle;
    private Core.Recognizer? _recognizer;
    private KeyboardHook? _hook;
    private readonly Recorder _recorder = new();
    private OverlayWindow? _overlay;
    private SettingsWindow? _settingsWindow;
    private UpdateWindow? _updateWindow;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "--transcribe")
            return CliTranscribe(args[1], args[2]);
        if (args.Length >= 1 && args[0] == "--overlay-demo")
            return OverlayDemo();
        if (args.Length >= 2 && args[0] == "--mic-test")
            return MicTest(args[1]);
        if (args.Length >= 3 && args[0] == "--brain-test")
            return BrainTest(args[1], args[2]);
        if (args.Length >= 2 && args[0] == "--brain-download")
            return BrainDownload(args[1]);
        if (args.Length >= 3 && args[0] == "--shot")
            return WindowShot(args[1], args[2], args.Length >= 4 ? args[3] : "ru", args.Length >= 5 ? args[4] : null);
        if (args.Length >= 2 && args[0] == "--settings-shot")
            return SettingsShot(args[1], args.Length >= 3 ? args[2] : "ru");
        JustUpdated = args.Length >= 1 && args[0] == "--updated";

        _instanceMutex = new Mutex(true, "GigaPisar.SingleInstance", out bool first);
        if (!first)
        {
            try
            {
                // We were started by the user and may take the foreground; pass that right on to the running instance.
                Native.AllowSetForegroundWindow(Native.ASFW_ANY);
                if (EventWaitHandle.TryOpenExisting(ShowSettingsSignalName, out var signal)) signal.Set();
            }
            catch { }
            return 0;
        }
        _showSettingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsSignalName);

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

    private static string ReadVersion()
    {
        var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        int plus = info.IndexOf('+');
        return plus > 0 ? info[..plus] : info;
    }

    /// <summary>Diagnostics: records two seconds from the default microphone and writes backend, sample count and peak to a file.</summary>
    private static int MicTest(string outPath)
    {
        try
        {
            var rec = new Recorder();
            rec.StartAsync().GetAwaiter().GetResult();
            Thread.Sleep(2000);
            var samples = rec.StopAsync().GetAwaiter().GetResult();
            File.WriteAllText(outPath, $"backend={rec.Backend} devices={Recorder.DeviceCount} default={Recorder.DefaultDeviceName()} samples={samples.Length} seconds={samples.Length / (double)Recorder.SampleRate:F2} peak={20 * Math.Log10(Math.Max(rec.TakePeak, 1e-9)):F0}dBFS\n");
            return 0;
        }
        catch (Exception e)
        {
            File.WriteAllText(outPath, "ERROR: " + e);
            return 1;
        }
    }

    /// <summary>Diagnostics: runs the saved Brain settings on the text of <paramref name="inPath"/>.</summary>
    private static int BrainTest(string inPath, string outPath)
    {
        try
        {
            var s = Settings.Load();
            s.Save();   // rewrites the file, so a plain-text key from the first Brain build gets encrypted
            var sw = Stopwatch.StartNew();
            var input = File.ReadAllText(inPath).Trim();
            var cmd = Brain.ParseCommand(input);
            var log = new StringBuilder();
            var cleaned = Brain.TransformAsync(s, cmd?.body ?? input, cmd?.command,
                status => log.Append($"[{sw.ElapsedMilliseconds} ms] {status}\n"), CancellationToken.None).GetAwaiter().GetResult();
            File.WriteAllText(outPath, $"brain={s.Brain} server={SpeechCleanup.HostOf(s.CleanupEndpointUrl)} command={cmd?.command ?? "(every take)"} ms={sw.ElapsedMilliseconds}\n{log}{cleaned}\n");
            LocalBrain.Stop();
            return 0;
        }
        catch (Exception e)
        {
            File.WriteAllText(outPath, "ERROR: " + e);
            return 1;
        }
    }

    /// <summary>
    /// Screenshots for articles: opens a window filled with example values (nothing is saved),
    /// captures exactly its frame from the screen and exits. Needs the display on.
    /// kind: settings | server.
    /// </summary>
    private static int WindowShot(string kind, string outPath, string lang, string? keyFile)
    {
        var app = new PisarApp();
        app.InitializeComponent();
        app.Startup += async (_, _) =>
        {
            L.Apply(lang == "en" ? UiLanguage.English : UiLanguage.Russian);
            var s = new Settings
            {
                Brain = BrainSource.Server,
                CleanupEndpointUrl = "https://api.deepseek.com/v1",
                // A real key from a file shows the "key works" state; the file is the caller's to delete.
                CleanupApiKey = keyFile != null && File.Exists(keyFile) ? File.ReadAllText(keyFile).Trim() : "sk-00000000000000000000000000000000",
                CleanupModel = "deepseek-flash",
            };
            Window w = kind == "server"
                ? new CleanupSettingsWindow(s, () => { })
                : new SettingsWindow(s, () => { }, () => { }, _ => Task.CompletedTask);
            w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            w.Topmost = true;
            w.Show();
            w.Activate();
            await Task.Delay(kind == "server" ? 4000 : 1500);   // the server window loads the model list first
            var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out var r, System.Runtime.InteropServices.Marshal.SizeOf<Native.RECT>()) != 0)
                Native.GetWindowRect(hwnd, out r);
            using (var bmp = new System.Drawing.Bitmap(r.Right - r.Left, r.Bottom - r.Top))
            {
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                    g.CopyFromScreen(r.Left, r.Top, 0, 0, bmp.Size);
                bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
            }
            app.Shutdown();
        };
        return app.Run();
    }

    /// <summary>Design aid: renders the Settings window into a PNG (works with the display asleep).</summary>
    private static int SettingsShot(string outPath, string lang)
    {
        var app = new PisarApp();
        app.InitializeComponent();
        app.Startup += async (_, _) =>
        {
            L.Apply(lang == "en" ? UiLanguage.English : UiLanguage.Russian);
            var s = Settings.Load();
            var w = new SettingsWindow(s, () => { }, () => { }, _ => Task.CompletedTask);
            w.Show();
            await Task.Delay(1500);
            var root = (FrameworkElement)w.Content;
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(root);
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
                96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, System.Windows.Media.PixelFormats.Pbgra32);
            var bg = new System.Windows.Shapes.Rectangle { Width = root.ActualWidth, Height = root.ActualHeight, Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20)) };
            bg.Measure(new Size(root.ActualWidth, root.ActualHeight)); bg.Arrange(new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            bmp.Render(bg);
            bmp.Render(root);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using (var f = File.Create(outPath)) enc.Save(f);
            app.Shutdown();
        };
        return app.Run();
    }

    /// <summary>Diagnostics: the local Brain download without the window, progress written to <paramref name="outPath"/>.</summary>
    private static int BrainDownload(string outPath)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            int lastPercent = -1;
            var progress = new SyncProgress(p =>
            {
                int pct = p.Total > 0 ? (int)(100 * p.Received / p.Total) : -1;
                if (p.Stage != "download" || pct / 10 != lastPercent / 10)
                {
                    lastPercent = pct;
                    File.AppendAllText(outPath, $"[{sw.Elapsed.TotalSeconds:F0}s] {p.Stage} {pct}%\n");
                }
            });
            LocalBrain.DownloadAsync(progress, CancellationToken.None).GetAwaiter().GetResult();
            File.AppendAllText(outPath, $"OK in {sw.Elapsed.TotalSeconds:F0}s, downloaded={LocalBrain.Downloaded}\n");
            return 0;
        }
        catch (Exception e)
        {
            File.AppendAllText(outPath, "ERROR: " + e + "\n");
            return 1;
        }
    }

    private sealed class SyncProgress(Action<ModelDownloader.Progress> report) : IProgress<ModelDownloader.Progress>
    {
        public void Report(ModelDownloader.Progress value) => report(value);
    }

    /// <summary>Design aid: shows the overlay with synthetic levels for a few seconds, then the "recognizing" state.</summary>
    private static int OverlayDemo()
    {
        var app = new PisarApp();
        app.InitializeComponent();
        app.Startup += async (_, _) =>
        {
            L.Apply(UiLanguage.Russian);
            var overlay = new OverlayWindow(new Settings(), () => { });
            overlay.ShowListening(null, demo: true);
            await Task.Delay(6000);
            overlay.ShowRecognizing();
            await Task.Delay(3000);
            overlay.ShowHint(L.T("Тишина на входе. Проверьте микрофон", "Silence on input. Check the microphone"));
            await Task.Delay(3000);
            app.Shutdown();
        };
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
        _iconBusy = MakeBusyIcon(_iconIdle, out _busyIconHandle);
        _tray = new Forms.NotifyIcon
        {
            Icon = _iconIdle,
            Text = L.T("Гига Писарь", "Giga Pisar"),
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _tray.DoubleClick += (_, _) => ShowSettings();
        new Thread(() =>
        {
            while (_showSettingsSignal!.WaitOne() && !_lifetime.IsCancellationRequested)
                Dispatcher.BeginInvoke(ShowSettings);
        }) { IsBackground = true, Name = "show-settings-signal" }.Start();

        if (!Core.Recognizer.ModelExists(Settings.ModelDir))
        {
            var ok = await new DownloadWindow(Settings.ModelDir).RunAsync();
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
            string why = ex.ToString().Contains("NativeMethods", StringComparison.Ordinal) || ex is DllNotFoundException
                ? L.T("Не загрузилась библиотека распознавания ONNX Runtime. Обычно это значит, что в Windows нет библиотек Visual C++. Поставьте их с сайта Microsoft (aka.ms/vs/17/release/vc_redist.x64.exe) и запустите Писаря снова.",
                      "The ONNX Runtime library did not load. Usually Windows is missing the Visual C++ runtime. Install it from Microsoft (aka.ms/vs/17/release/vc_redist.x64.exe) and start Pisar again.")
                : ex.Message;
            MessageBox.Show(L.T("Не удалось загрузить модель распознавания.", "Could not load the speech model.") + "\n\n" + why,
                L.T("Гига Писарь", "Giga Pisar"), MessageBoxButton.OK, MessageBoxImage.Error);
            Quit();
            return;
        }

        _recorder.TakeTooLong += () => Dispatcher.BeginInvoke(() => _ = HandleReleaseAsync());
        try
        {
            _hook = new KeyboardHook(_settings.HotkeyVk);
            _hook.Pressed += () => Dispatcher.BeginInvoke(() => _ = HandlePressAsync());
            _hook.Released += () => Dispatcher.BeginInvoke(() => _ = HandleReleaseAsync());
        }
        catch (Exception ex)
        {
            Log.Write($"hook failed: {ex}");
        }

        SetStatus(null);
        if (JustUpdated)
        {
            Updater.Cleanup();
            _tray.ShowBalloonTip(6000, L.T($"Гига Писарь обновлён до {Version}", $"Giga Pisar updated to {Version}"),
                L.T("Всё готово, можно диктовать.", "All set, dictate away."), Forms.ToolTipIcon.None);
        }
        _ = UpdateLoopAsync();
        if (!_settings.FirstRunDone)
        {
            _settings.FirstRunDone = true;
            _settings.Save();
            _tray.ShowBalloonTip(8000, L.T("Гига Писарь готов", "Giga Pisar is ready"),
                L.T($"Поставьте курсор в любой текст, зажмите {Settings.HotkeyTitle(_settings.HotkeyVk)} и говорите. Отпустите, и текст появится сам.",
                    $"Put the cursor in any text, hold {Settings.HotkeyTitle(_settings.HotkeyVk)} and speak. Release, and the text appears by itself."),
                Forms.ToolTipIcon.None);
        }
    }

    // ── updates ──────────────────────────────────────────────────

    private async Task UpdateLoopAsync()
    {
        try
        {
            await Task.Delay(Updater.FirstCheckDelay, _lifetime.Token);
            while (!_lifetime.IsCancellationRequested)
            {
                if (_settings.CheckUpdates) await CheckForUpdatesAsync(silent: true);
                await Task.Delay(Updater.CheckInterval, _lifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task CheckForUpdatesAsync(bool silent)
    {
        if (_updateWindow != null) { _updateWindow.Activate(); return; }
        UpdateInfo? info;
        try { info = await Updater.CheckAsync(_lifetime.Token); }
        catch (OperationCanceledException) { return; }
        if (info == null)
        {
            if (!silent)
                _tray?.ShowBalloonTip(4000, L.T("Гига Писарь", "Giga Pisar"),
                    L.T($"У вас последняя версия, {Version}.", $"You have the latest version, {Version}."), Forms.ToolTipIcon.None);
            return;
        }
        _updateWindow = new UpdateWindow(info, Quit);
        _updateWindow.Closed += (_, _) => _updateWindow = null;
        _updateWindow.Show();
        _updateWindow.Activate();
    }

    // ── push-to-talk ─────────────────────────────────────────────

    private async Task HandlePressAsync()
    {
        if (_busy || _recognizer == null || _recorder.IsRecording) return;
        try
        {
            await _recorder.StartAsync();
        }
        catch (Exception ex)
        {
            Log.Write($"mic failed: {ex.GetType().Name}: {ex.Message} (devices: {Recorder.DeviceCount})");
            string why = Recorder.DeviceCount == 0
                ? L.T("Windows не видит ни одного устройства записи. Подключите микрофон или включите его в Параметрах звука, раздел «Ввод».",
                      "Windows sees no recording device. Connect a microphone or enable one in Sound settings, Input.")
                : ex is UnauthorizedAccessException || ex.Message.Contains("0x80070005") || ex.Message.Contains("denied", StringComparison.OrdinalIgnoreCase)
                ? L.T("Windows не даёт доступ к микрофону. Параметры → Конфиденциальность и защита → Микрофон: включите «Доступ к микрофону» и «Разрешить классическим приложениям доступ к микрофону».",
                      "Windows denies microphone access. Settings, Privacy and security, Microphone: turn on microphone access and let desktop apps use the microphone.")
                : L.T($"Не удалось открыть микрофон: {ex.Message}", $"Could not open the microphone: {ex.Message}");
            _tray?.ShowBalloonTip(8000, L.T("Микрофон недоступен", "Microphone unavailable"), why, Forms.ToolTipIcon.Warning);
            return;
        }
        if (_tray != null) _tray.Icon = _iconBusy;
        if (_settings.ShowOverlay)
        {
            _overlay ??= new OverlayWindow(_settings, _settings.Save);
            _overlay.ShowListening(_recorder);
        }
    }

    private async Task HandleReleaseAsync()
    {
        if (!_recorder.IsRecording || _busy) return;
        _busy = true;
        var overlay = _settings.ShowOverlay ? _overlay : null;
        try
        {
            var samples = await _recorder.StopAsync();
            overlay?.ShowRecognizing();

            float peak = _recorder.TakePeak;
            // The model invents words when fed silence or flat noise. Absolute level is a poor
            // test (a line-level receiver on a mic jack sits 50 dB down), so we look for speech
            // dynamics: loud stretches well above the quiet ones.
            bool silent = peak < SilenceFloor || !HasSpeechDynamics(samples);
            Log.Write($"take {samples.Length / (double)Recorder.SampleRate:F1}s peak {20 * Math.Log10(Math.Max(peak, 1e-9)):F0} dBFS silent={silent}");

            string text = "";
            string? brainFailure = null;
            bool cleanupReturnedEmpty = false;
            if (!silent && samples.Length > MinTakeSamples)
            {
                if (_settings.KeepLastRecording)
                    Core.AudioUtils.WriteWav(samples, Recorder.SampleRate, Settings.LastTakePath);

                // Windows input levels vary wildly (a line-level receiver on a mic jack can sit
                // 50 dB down). Normalize quiet takes so the model sees ordinary speech.
                if (peak < TargetPeak)
                {
                    float gain = Math.Min(TargetPeak / peak, MaxGain);
                    for (int i = 0; i < samples.Length; i++) samples[i] *= gain;
                }
                text = await Task.Run(() => _recognizer!.Transcribe(samples, Recorder.SampleRate));
                if (text.Length > 0 && _settings.Brain != BrainSource.Off)
                {
                    var cmd = Brain.ParseCommand(text);
                    if (cmd != null || _settings.BrainEveryTake)
                    {
                        string body = cmd?.body ?? text;
                        try
                        {
                            if (cmd != null) Log.Write($"brain command, {body.Length} chars");
                            if (!await ConfirmBrainMemoryAsync()) throw new BrainException(
                                L.T("мало свободной памяти, запуск отменён", "not enough free memory, start cancelled"));
                            text = await Brain.TransformAsync(_settings, body, cmd?.command,
                                status => { overlay?.ShowStatus(status); SetStatus(status); }, _lifetime.Token);
                            cleanupReturnedEmpty = text.Length == 0;
                        }
                        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
                        catch (Exception ex)
                        {
                            Log.Write($"brain failed: {ex.GetType().Name}: {ex.Message}");
                            brainFailure = ex is BrainException ? ex.Message : null;
                            brainFailure ??= _settings.Brain == BrainSource.Server
                                ? L.T("сервер не ответил", "the server did not answer")
                                : L.T("нейронка не ответила", "the Brain did not answer");
                            text = body;   // the command itself is never inserted
                        }
                        finally { SetStatus(null); }
                    }
                }
            }

            if (text.Length > 0)
            {
                overlay?.HideNow();
                var mode = _settings.InsertMode;
                var result = await Task.Run(() => TextInserter.Insert(text, mode));
                if (result == InsertResult.Blocked)
                    Hint(L.T("Это окно запущено от администратора, вставить туда нельзя. Текст лежит в буфере обмена.",
                             "That window runs as administrator; typing into it is blocked. The text is on the clipboard."));
                else if (brainFailure != null)
                    Hint(L.T($"Мозг не справился: {brainFailure}. Вставлен текст без правки.",
                             $"The Brain failed: {brainFailure}. Inserted the text as recognized."));
            }
            else if (samples.Length <= MinTakeSamples || cleanupReturnedEmpty)
            {
                overlay?.HideNow();
            }
            else
            {
                Hint(silent
                    ? L.T("Тишина на входе. Проверьте микрофон и его громкость: " + Recorder.DefaultDeviceName(),
                          "Silence on input. Check the microphone and its level: " + Recorder.DefaultDeviceName())
                    : L.T("Не разобрал. Попробуйте ещё раз ближе к микрофону.",
                          "Could not make it out. Try again closer to the microphone."));
            }
        }
        catch (Exception ex)
        {
            Log.Write($"take failed: {ex}");
            overlay?.HideNow();
        }
        finally
        {
            _busy = false;
            if (_tray != null) _tray.Icon = _iconIdle;
        }
    }

    /// <summary>
    /// Before a cold start of the local Brain: if the model will not fit into free memory,
    /// Windows starts paging and the start drags on for minutes. Better to ask first.
    /// </summary>
    private Task<bool> ConfirmBrainMemoryAsync()
    {
        if (_settings.Brain != BrainSource.Local || LocalBrain.Running) return Task.FromResult(true);
        var (_, free) = LocalBrain.Memory();
        ulong need = LocalBrain.MemoryNeeded;
        if (free == 0 || free >= need) return Task.FromResult(true);
        var answer = System.Windows.MessageBox.Show(
            L.T($"Свободно {LocalBrain.Gb(free)} ГБ памяти, а Мозгу нужно около {LocalBrain.Gb(need)} ГБ. Он всё равно запустится, но Windows начнёт выгружать другие программы на диск, и ждать можно несколько минут. Закройте тяжёлые программы и попробуйте снова, или запускайте так.",
                $"{LocalBrain.Gb(free)} GB of memory is free and the Brain needs about {LocalBrain.Gb(need)} GB. It will still start, but Windows will page other apps to disk and it may take minutes. Close heavy apps and try again, or go ahead anyway."),
            L.T("Памяти впритык", "Memory is tight"), System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        return Task.FromResult(answer == System.Windows.MessageBoxResult.Yes);
    }

    /// <summary>Switches the Brain; for the local one, downloads engine and model first (asking before 2 GB).</summary>
    public async Task SelectBrainAsync(BrainSource source)
    {
        if (source == BrainSource.Server && !Brain.ServerConfigured(_settings))
        {
            ShowSettings();
            _settingsWindow?.OpenServerSettings();
            return;
        }
        if (source == BrainSource.Local && !LocalBrain.Downloaded)
        {
            var (total, _) = LocalBrain.Memory();
            string ram = total > 0 && total < LocalBrain.RecommendedRamBytes
                ? L.T($"\n\nВ этом компьютере {LocalBrain.Gb(total)} ГБ памяти, а Мозгу комфортно от 8 ГБ. Работать будет, но медленно и тесно.",
                      $"\n\nThis computer has {LocalBrain.Gb(total)} GB of memory; the Brain is comfortable from 8 GB. It will work, but slowly and tightly.")
                : "";
            var ok = System.Windows.MessageBox.Show(
                L.T($"Мозг на компьютере: нейросеть {LocalBrain.ModelTitle} и движок llama.cpp, около 2 ГБ. Скачиваются один раз, потом всё работает без интернета.\n\nПока Мозг работает, он занимает около 2,5 ГБ памяти и сам выгружается через 15 минут без дела. Правка фразы на обычном ноутбуке занимает от нескольких секунд до десяти.{ram}\n\nСкачать?",
                    $"The Brain on this computer: the {LocalBrain.ModelTitle} model and the llama.cpp engine, about 2 GB. Downloaded once, then everything works offline.\n\nWhile working it takes about 2.5 GB of memory and unloads itself after 15 idle minutes. Editing a phrase takes a few seconds up to ten on an ordinary laptop.{ram}\n\nDownload?"),
                L.T("Мозг на компьютере", "Brain on this computer"), System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (ok != System.Windows.MessageBoxResult.Yes) return;
            var window = new DownloadWindow(
                L.T("Скачиваю Мозг", "Downloading the Brain"),
                L.T($"Нейросеть {LocalBrain.ModelTitle} и движок llama.cpp, около 2 ГБ. Если связь оборвётся, скачивание продолжится с того же места.",
                    $"The {LocalBrain.ModelTitle} model and the llama.cpp engine, about 2 GB. If the connection drops, the download resumes where it stopped."),
                LocalBrain.DownloadAsync);
            if (!await window.RunAsync()) return;
        }
        if (source != BrainSource.Local) LocalBrain.Stop();
        _settings.Brain = source;
        ApplySettings();
        _settingsWindow?.Localize();
        if (source != BrainSource.Off)
            _tray?.ShowBalloonTip(6000, L.T("Мозг включён", "Brain is on"),
                L.T("Скажите в конце фразы: «Писарь, исправь», «Писарь, сократи» или «Писарь, переведи на английский».",
                    "End a phrase with \"Pisar, fix it\", \"Pisar, make it shorter\" or \"Pisar, translate into English\" (in Russian)."),
                Forms.ToolTipIcon.None);
    }

    /// <summary>Short feedback for the user: on the overlay when it is enabled, otherwise as a balloon.</summary>
    private void Hint(string text)
    {
        if (_settings.ShowOverlay)
        {
            _overlay ??= new OverlayWindow(_settings, _settings.Save);
            _overlay.ShowHint(text);
        }
        else
        {
            _tray?.ShowBalloonTip(4000, L.T("Гига Писарь", "Giga Pisar"), text, Forms.ToolTipIcon.None);
        }
    }

    /// <summary>True when the take has bursts (speech) rather than a flat floor (silence or hum).</summary>
    private static bool HasSpeechDynamics(float[] samples)
    {
        const int window = Recorder.SampleRate / 10;   // 100 ms
        const double minLoudToQuietRatio = 2;          // permissive on purpose: a wrong "silence" verdict hides real speech
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
        return loud / quiet > minLoudToQuietRatio;
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
        // Always visible, so it is clear whether text leaves the computer.
        var brain = new Forms.ToolStripMenuItem("");
        var brainOff = new Forms.ToolStripMenuItem(L.T("Выключен", "Off"));
        var brainLocal = new Forms.ToolStripMenuItem("");
        var brainServer = new Forms.ToolStripMenuItem("");
        brainOff.Click += (_, _) => _ = SelectBrainAsync(BrainSource.Off);
        brainLocal.Click += (_, _) => _ = SelectBrainAsync(BrainSource.Local);
        brainServer.Click += (_, _) => _ = SelectBrainAsync(BrainSource.Server);
        brain.DropDownItems.AddRange(new Forms.ToolStripItem[] { brainOff, brainLocal, brainServer });
        menu.Items.Add(brain);
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
        menu.Items.Add(L.T("Проверить обновления", "Check for updates"), null, (_, _) => _ = CheckForUpdatesAsync(silent: false));
        menu.Items.Add(L.T("Сайт проекта", "Project website"), null, (_, _) => Open(SiteUrl));
        menu.Items.Add(L.T("Исходный код", "Source code"), null, (_, _) => Open(RepoUrl));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(L.T("Выход", "Quit"), null, (_, _) => Quit());
        menu.Opening += (_, _) =>
        {
            autostart.Checked = Autostart.IsEnabled();
            string host = SpeechCleanup.HostOf(_settings.CleanupEndpointUrl);
            brain.Text = _settings.Brain switch
            {
                BrainSource.Local => L.T("Мозг: на компьютере", "Brain: on this computer"),
                BrainSource.Server => L.T($"Мозг: {host}", $"Brain: {host}"),
                _ => L.T("Мозг: выключен", "Brain: off"),
            };
            brainOff.Checked = _settings.Brain == BrainSource.Off;
            brainLocal.Checked = _settings.Brain == BrainSource.Local;
            brainServer.Checked = _settings.Brain == BrainSource.Server;
            brainLocal.Text = LocalBrain.Downloaded
                ? L.T($"На компьютере ({LocalBrain.ModelTitle})", $"On this computer ({LocalBrain.ModelTitle})")
                : L.T("На компьютере (скачать 2 ГБ)…", "On this computer (download 2 GB)…");
            brainServer.Text = host.Length > 0
                ? L.T($"Свой сервер ({host})", $"Own server ({host})")
                : L.T("Свой сервер или облако…", "Own server or cloud…");
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
            _settingsWindow = new SettingsWindow(_settings, ApplySettings, () => { _overlay?.Unpin(); _settings.OverlayX = null; _settings.OverlayY = null; _settings.Save(); }, SelectBrainAsync);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Show();
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        // Windows may refuse the foreground to a tray app; a topmost blink still brings the window above the rest.
        _settingsWindow.Topmost = true;
        _settingsWindow.Activate();
        _settingsWindow.Topmost = false;
    }

    private void ApplySettings()
    {
        _settings.Save();
        if (_hook != null) _hook.HotkeyVk = _settings.HotkeyVk;
        if (!_settings.ShowOverlay) _overlay?.HideNow();

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
        _lifetime.Cancel();
        LocalBrain.Stop();
        _hook?.Dispose();
        _recorder.Dispose();
        _overlay?.Close();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _recognizer?.Dispose();
        _iconBusy?.Dispose();
        if (_busyIconHandle != IntPtr.Zero) Native.DestroyIcon(_busyIconHandle);
        _iconIdle?.Dispose();
        Shutdown();
    }

    // ── icons ────────────────────────────────────────────────────

    private static System.Drawing.Icon LoadIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(path)) return new System.Drawing.Icon(path, 32, 32);
        return System.Drawing.SystemIcons.Application;
    }

    /// <summary>Same icon with a red dot: "listening". The HICON must be destroyed by the caller.</summary>
    private static System.Drawing.Icon MakeBusyIcon(System.Drawing.Icon idle, out IntPtr handle)
    {
        using var bmp = idle.ToBitmap();
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int d = bmp.Width * 7 / 16;
        using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 226, 61, 61));
        using var pen = new System.Drawing.Pen(System.Drawing.Color.White, Math.Max(1, bmp.Width / 16));
        g.FillEllipse(brush, bmp.Width - d, bmp.Height - d, d - 1, d - 1);
        g.DrawEllipse(pen, bmp.Width - d, bmp.Height - d, d - 1, d - 1);
        handle = bmp.GetHicon();
        return System.Drawing.Icon.FromHandle(handle);
    }
}
