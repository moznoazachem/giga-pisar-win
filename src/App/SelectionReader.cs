// Reads the text selected in the app the user works in, for Brain commands on a
// selection ("select, hold the key, say what to do with it"), like the macOS app.
//
// UI Automation only, no synthetic Ctrl+C: the clipboard stays untouched and there
// are no false positives (VS Code, for one, copies the whole line when nothing is
// selected). Apps that do not expose their text to UI Automation simply get plain
// dictation. Terminals are skipped: there Ctrl+C means "stop", not "copy".

using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace GigaPisar.App;

public static class SelectionReader
{
    private const int MaxChars = 20_000;
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(900);

    private static readonly string[] TerminalClasses =
        ["ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "mintty", "PuTTY", "VirtualConsoleClass"];

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);

    /// <summary>A read that has not finished yet (a hung app): no new ones pile up behind it.</summary>
    private static Task<string?>? _inFlight;

    /// <summary>The selected text of the focused control, or null when there is none or it cannot be read in time.</summary>
    public static async Task<string?> TryGetAsync()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || IsTerminal(fg)) return null;
        if (_inFlight is { IsCompleted: false }) return null;
        // UI Automation calls can block on a busy app; they run on a worker thread and we stop waiting after a moment.
        var work = _inFlight = Task.Run(Read);
        var done = await Task.WhenAny(work, Task.Delay(Timeout));
        if (done != work) { Log.Write("selection: UI Automation timed out"); return null; }
        return work.Result;
    }

    private static bool IsTerminal(IntPtr hwnd)
    {
        var sb = new StringBuilder(128);
        GetClassName(hwnd, sb, sb.Capacity);
        return TerminalClasses.Contains(sb.ToString());
    }

    private static string? Read()
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused == null) return null;
            if (!focused.TryGetCurrentPattern(TextPattern.Pattern, out var p) || p is not TextPattern text) return null;
            var ranges = text.GetSelection();
            if (ranges.Length == 0) return null;
            var sb = new StringBuilder();
            foreach (TextPatternRange r in ranges)
            {
                sb.Append(r.GetText(MaxChars - sb.Length));
                if (sb.Length >= MaxChars) break;
            }
            var s = sb.ToString();
            return s.Trim().Length == 0 ? null : s;
        }
        catch (Exception e)
        {
            Log.Write($"selection: {e.GetType().Name}");
            return null;
        }
    }
}
