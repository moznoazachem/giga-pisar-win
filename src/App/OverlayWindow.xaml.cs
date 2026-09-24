// Small floating pill near the text caret (or at the bottom of the active
// window's screen when the caret is unknown), the Windows counterpart of the
// macOS wave panel: thin bars that jump with the voice while listening, a calm
// ripple while recognizing, a short text for hints. Light or dark following the
// Windows app theme. Never takes focus: WS_EX_NOACTIVATE keeps the user's app
// in front. Drag it with the mouse and it stays where you left it.

using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace GigaPisar.App;

public partial class OverlayWindow : Window
{
    private const int BarCount = 13;
    private const double BarWidth = 3;
    private const double BarGap = 2;
    private const double BarMaxHeight = 22;
    private const double PillHeight = 30;
    private const double PillPadding = 14;       // equal on both sides; WPF's auto-size drifted, so widths are explicit
    private const double WindowMargin = 14;      // room for the shadow
    private const int HintMs = 2200;
    private static readonly double BarsWidth = BarCount * BarWidth + (BarCount - 1) * BarGap;

    // The website's wave palette, light to teal, one gradient per bar.
    private static readonly (string top, string bottom)[] Palette =
    {
        ("#a8e063", "#1fa03a"), ("#a8e063", "#1fa03a"), ("#9adf55", "#17963f"), ("#8ad84c", "#10884a"),
        ("#7fd648", "#0e9367"), ("#63cf62", "#009b82"), ("#4fc884", "#00a08c"), ("#3fc39b", "#00a08c"),
        ("#38bfa5", "#008f92"), ("#35bcb0", "#008699"), ("#35bcb0", "#008699"), ("#35bcb0", "#008699"),
        ("#35bcb0", "#008699"),
    };

    private readonly Rectangle[] _bars = new Rectangle[BarCount];
    private readonly float[] _heights = new float[BarCount];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Random _rng = new();
    private Recorder? _recorder;
    private float _smoothed;
    private int _hintSerial;
    private bool _demo;
    private bool _busyRipple;
    private double _ripplePhase;

    private readonly Settings _settings;
    private readonly Action _saveSettings;
    private bool _dragging;
    private Point _dragStart;          // screen pixels
    private Native.RECT _dragWindow;   // window rect when the drag began

    public OverlayWindow(Settings settings, Action saveSettings)
    {
        _settings = settings;
        _saveSettings = saveSettings;
        InitializeComponent();
        Pill.MouseLeftButtonDown += OnDragStart;
        Pill.MouseMove += OnDragMove;
        Pill.MouseLeftButtonUp += OnDragEnd;
        for (int i = 0; i < BarCount; i++)
        {
            var (top, bottom) = Palette[i % Palette.Length];
            var brush = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(top),
                                                (Color)ColorConverter.ConvertFromString(bottom), 90);
            var r = new Rectangle
            {
                Width = BarWidth, Height = BarWidth, RadiusX = BarWidth / 2, RadiusY = BarWidth / 2,
                Margin = new Thickness(i == 0 ? 0 : BarGap, 0, 0, 0), Fill = brush,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _bars[i] = r;
            Bars.Children.Add(r);
        }
        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) => Place();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST);
        };
    }

    public void ShowListening(Recorder? recorder, bool demo = false)
    {
        _hintSerial++;
        _recorder = recorder;
        _demo = demo;
        _busyRipple = false;
        ApplyTheme();
        Label.Visibility = Visibility.Collapsed;
        Bars.Visibility = Visibility.Visible;
        Resize(BarsWidth);
        Array.Clear(_heights);
        _smoothed = 0;
        foreach (var b in _bars) b.Height = BarWidth;
        Place();
        Appear();
        _timer.Start();
    }

    /// <summary>Recording is over, the model is working: bars keep moving with a calm ripple, no text.</summary>
    public void ShowRecognizing()
    {
        _recorder = null;
        _demo = false;
        _busyRipple = true;
        _ripplePhase = 0;
        if (!_timer.IsEnabled) _timer.Start();
    }

    public void HideNow()
    {
        _timer.Stop();
        _recorder = null;
        _busyRipple = false;
        Hide();
    }

    /// <summary>Shows a short message (e.g. "heard nothing") and hides after a moment.</summary>
    public async void ShowHint(string text)
    {
        _timer.Stop();
        _recorder = null;
        _busyRipple = false;
        ApplyTheme();
        Bars.Visibility = Visibility.Collapsed;
        Label.Text = text;
        Label.Visibility = Visibility.Visible;
        Label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Resize(Math.Ceiling(Label.DesiredSize.Width));
        Place();
        Appear();
        int mine = ++_hintSerial;
        await Task.Delay(HintMs);
        // A new take may have started meanwhile; only the latest hint may hide the pill.
        if (mine == _hintSerial && _recorder == null && !_demo) Hide();
    }

    // ── dragging ─────────────────────────────────────────────────

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!Native.GetWindowRect(hwnd, out _dragWindow)) return;
        _dragStart = PointToScreen(e.GetPosition(this));
        _dragging = true;
        Pill.CaptureMouse();
        e.Handled = true;
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var now = PointToScreen(e.GetPosition(this));
        int x = _dragWindow.Left + (int)(now.X - _dragStart.X);
        int y = _dragWindow.Top + (int)(now.Y - _dragStart.Y);
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Pill.ReleaseMouseCapture();
        var hwnd = new WindowInteropHelper(this).Handle;
        if (Native.GetWindowRect(hwnd, out var r))
        {
            // Remember the window's top-left; a differently sized pill (a hint) starts from the same edge.
            _settings.OverlayX = r.Left;
            _settings.OverlayY = r.Top;
            _saveSettings();
        }
        e.Handled = true;
    }

    /// <summary>Forget the dragged position: the pill follows the caret again.</summary>
    public void Unpin()
    {
        _settings.OverlayX = null;
        _settings.OverlayY = null;
        _saveSettings();
    }

    /// <summary>Pill and window sized around the content with equal side padding.</summary>
    private void Resize(double contentWidth)
    {
        Pill.Width = contentWidth + 2 * PillPadding;
        Pill.Height = PillHeight;
        Width = Pill.Width + 2 * WindowMargin;
        Height = PillHeight + 2 * WindowMargin;
    }

    private void Appear()
    {
        if (IsVisible) return;
        Show();
        // A quick pop, like a tooltip: 92% -> 100% and fade in over 120 ms.
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var d = TimeSpan.FromMilliseconds(120);
        Pill.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d) { EasingFunction = ease });
        PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1, d) { EasingFunction = ease });
        PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1, d) { EasingFunction = ease });
    }

    private void Tick()
    {
        if (_busyRipple)
        {
            // Gentle wave rolling across the bars: the app is alive, the text is coming.
            _ripplePhase += 0.28;
            for (int i = 0; i < BarCount; i++)
            {
                double s = 0.5 + 0.5 * Math.Sin(_ripplePhase - i * 0.55);
                _bars[i].Height = BarWidth + (0.18 + 0.32 * s) * (BarMaxHeight - BarWidth);
            }
            return;
        }

        float level = _demo
            ? (float)(0.25 + 0.55 * Math.Abs(Math.Sin(Environment.TickCount64 / 260.0)) * _rng.NextDouble() + 0.2 * _rng.NextDouble())
            : _recorder?.Level ?? 0;
        // fast attack, slow release: keeps the bars lively without flicker
        _smoothed = level > _smoothed ? _smoothed + (level - _smoothed) * 0.65f : _smoothed * 0.82f;
        for (int i = 0; i < BarCount; i++)
        {
            float center = 1f - Math.Abs(i - (BarCount - 1) / 2f) / ((BarCount - 1) / 2f);
            float target = _smoothed * (0.3f + 0.7f * center) * (0.65f + 0.35f * (float)_rng.NextDouble());
            _heights[i] = target > _heights[i] ? target : _heights[i] * 0.72f;
            _bars[i].Height = BarWidth + _heights[i] * (BarMaxHeight - BarWidth);
        }
    }

    /// <summary>Light or dark pill, following "Choose your default app mode" in Windows settings.</summary>
    private void ApplyTheme()
    {
        bool light = true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            light = key?.GetValue("AppsUseLightTheme") is not int v || v != 0;
        }
        catch { }
        if (light)
        {
            Pill.Background = new SolidColorBrush(Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF));
            Pill.BorderBrush = new SolidColorBrush(Color.FromArgb(0x1F, 0x00, 0x00, 0x00));
            Label.Foreground = new SolidColorBrush(Color.FromArgb(0xD9, 0x00, 0x00, 0x00));
        }
        else
        {
            Pill.Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x2C, 0x2C, 0x2C));
            Pill.BorderBrush = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
            Label.Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
        }
    }

    /// <summary>Positions the pill just below the caret, or at the bottom center of the active window's monitor.</summary>
    private void Place()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        uint dpi = Native.GetDpiForWindow(hwnd);
        double scale = dpi > 0 ? dpi / 96.0 : 1.0;

        int w = (int)Math.Ceiling(Width * scale);
        int h = (int)Math.Ceiling(Height * scale);

        var fg = Native.GetForegroundWindow();
        int x, y;
        if (_settings.OverlayX is int px && _settings.OverlayY is int py)
        {
            x = px;
            y = py;
        }
        else if (TryCaret(fg, out var caret))
        {
            x = caret.X - w / 2;
            y = caret.Y + (int)(6 * scale);
        }
        else
        {
            var mon = Native.MonitorFromWindow(fg != IntPtr.Zero ? fg : hwnd, Native.MONITOR_DEFAULTTONEAREST);
            var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
            Native.GetMonitorInfo(mon, ref mi);
            x = (mi.rcWork.Left + mi.rcWork.Right) / 2 - w / 2;
            y = mi.rcWork.Bottom - h - (int)(40 * scale);
        }

        // keep it on the monitor it landed on
        var pt = new Native.POINT { X = x + w / 2, Y = y + h / 2 };
        var m2 = Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (Native.GetMonitorInfo(m2, ref info))
        {
            x = Math.Clamp(x, info.rcWork.Left, Math.Max(info.rcWork.Left, info.rcWork.Right - w));
            y = Math.Clamp(y, info.rcWork.Top, Math.Max(info.rcWork.Top, info.rcWork.Bottom - h));
        }

        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>Caret position in screen pixels for classic Win32 edit controls. Browsers and Electron apps do not report it.</summary>
    private static bool TryCaret(IntPtr foreground, out Native.POINT bottomCenter)
    {
        bottomCenter = default;
        if (foreground == IntPtr.Zero) return false;
        uint thread = Native.GetWindowThreadProcessId(foreground, out _);
        var gti = new Native.GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.GUITHREADINFO>() };
        if (!Native.GetGUIThreadInfo(thread, ref gti) || gti.hwndCaret == IntPtr.Zero) return false;
        var r = gti.rcCaret;
        if (r.Bottom - r.Top <= 0 || r.Bottom - r.Top > 200) return false;
        var p = new Native.POINT { X = (r.Left + r.Right) / 2, Y = r.Bottom };
        if (!Native.ClientToScreen(gti.hwndCaret, ref p)) return false;
        bottomCenter = p;
        return true;
    }
}
