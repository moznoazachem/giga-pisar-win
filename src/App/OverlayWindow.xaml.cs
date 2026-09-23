// Small floating card near the text caret (or at the bottom of the active
// window's screen when the caret is unknown) with live microphone bars in the
// project's green palette, like the wave on the website. While listening the
// card shows only the wave; text appears for "recognizing" and hints.
// Never takes focus: WS_EX_NOACTIVATE keeps the user's app in front.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace GigaPisar.App;

public partial class OverlayWindow : Window
{
    private const int BarCount = 12;
    private static readonly (string top, string bottom)[] Palette =
    {
        ("#a8e063", "#1fa03a"), ("#a8e063", "#1fa03a"), ("#9adf55", "#17963f"), ("#8ad84c", "#10884a"),
        ("#7fd648", "#0e9367"), ("#63cf62", "#009b82"), ("#4fc884", "#00a08c"), ("#3fc39b", "#00a08c"),
        ("#38bfa5", "#008f92"), ("#35bcb0", "#008699"), ("#35bcb0", "#008699"), ("#35bcb0", "#008699"),
    };
    private readonly Rectangle[] _bars = new Rectangle[BarCount];
    private readonly float[] _heights = new float[BarCount];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Random _rng = new();
    private Recorder? _recorder;
    private float _smoothed;
    private int _hintSerial;
    private const int HintMs = 2200;

    public OverlayWindow()
    {
        InitializeComponent();
        for (int i = 0; i < BarCount; i++)
        {
            var (top, bottom) = Palette[i % Palette.Length];
            var brush = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(top),
                                                (Color)ColorConverter.ConvertFromString(bottom), 90);
            var r = new Rectangle
            {
                Width = 6, Height = 6, RadiusX = 3, RadiusY = 3,
                Margin = new Thickness(2, 0, 2, 0), Fill = brush,
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

    public void ShowListening(Recorder recorder)
    {
        _hintSerial++;
        _recorder = recorder;
        Label.Visibility = Visibility.Collapsed;
        Bars.Visibility = Visibility.Visible;
        Array.Clear(_heights);
        _smoothed = 0;
        Place();
        if (!IsVisible) Show();
        _timer.Start();
    }

    public void ShowRecognizing()
    {
        _timer.Stop();
        _recorder = null;
        Label.Text = L.T("Распознаю…", "Recognizing…");
        Label.Visibility = Visibility.Visible;
        foreach (var b in _bars) b.Height = 6;
    }

    public void HideNow()
    {
        _timer.Stop();
        _recorder = null;
        Hide();
    }

    /// <summary>Shows a short message (e.g. "heard nothing") and hides after a moment.</summary>
    public async void ShowHint(string text)
    {
        _timer.Stop();
        _recorder = null;
        Bars.Visibility = Visibility.Collapsed;
        Label.Text = text;
        Label.Visibility = Visibility.Visible;
        Place();
        if (!IsVisible) Show();
        int mine = ++_hintSerial;
        await Task.Delay(HintMs);
        // A new take may have started meanwhile; only the latest hint may hide the card.
        if (mine == _hintSerial && _recorder == null) Hide();
    }

    private void Tick()
    {
        float level = _recorder?.Level ?? 0;
        // fast attack, slow release: keeps the bars lively without flicker
        _smoothed = level > _smoothed ? _smoothed + (level - _smoothed) * 0.6f : _smoothed * 0.82f;
        for (int i = 0; i < BarCount; i++)
        {
            float center = 1f - Math.Abs(i - (BarCount - 1) / 2f) / ((BarCount - 1) / 2f);
            float target = _smoothed * (0.35f + 0.65f * center) * (0.7f + 0.3f * (float)_rng.NextDouble());
            _heights[i] = target > _heights[i] ? target : _heights[i] * 0.75f;
            _bars[i].Height = 6 + _heights[i] * 24;
        }
    }

    /// <summary>Positions the panel just below the caret, or at the bottom center of the active window's monitor.</summary>
    private void Place()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        uint dpi = Native.GetDpiForWindow(hwnd);
        double scale = dpi > 0 ? dpi / 96.0 : 1.0;

        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        int w = (int)Math.Ceiling(DesiredSize.Width * scale);
        int h = (int)Math.Ceiling(DesiredSize.Height * scale);

        var fg = Native.GetForegroundWindow();
        int x, y;
        if (TryCaret(fg, out var caret))
        {
            x = caret.X - w / 2;
            y = caret.Y + (int)(8 * scale);
        }
        else
        {
            var mon = Native.MonitorFromWindow(fg != IntPtr.Zero ? fg : hwnd, Native.MONITOR_DEFAULTTONEAREST);
            var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
            Native.GetMonitorInfo(mon, ref mi);
            x = (mi.rcWork.Left + mi.rcWork.Right) / 2 - w / 2;
            y = mi.rcWork.Bottom - h - (int)(48 * scale);
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
