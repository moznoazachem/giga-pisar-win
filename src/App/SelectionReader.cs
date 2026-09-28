// Reads the text selected in the app the user works in, for Brain commands on a
// selection ("select, hold the key, say what to do with it"), like the macOS app.
//
// UI Automation first: no side effects and no false positives. Only when the app does
// not expose its text that way at all (LibreOffice, some Qt and Java apps) do we press
// Ctrl+C for the user and put the clipboard back, as the macOS app does. An app that
// answers "nothing selected" through UI Automation is believed: VS Code, for one,
// copies the whole line on Ctrl+C without a selection. Terminals are skipped (Ctrl+C
// means "stop" there), and so are spreadsheets, where a cell is always selected and
// every dictation would turn into a command on it.

using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace GigaPisar.App;

public static class SelectionReader
{
    private const int MaxChars = 20_000;
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(1500);

    private static readonly string[] TerminalClasses =
        ["ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "mintty", "PuTTY", "VirtualConsoleClass"];

    /// <summary>Spreadsheets: a cell is always selected, so Ctrl+C would always "find" a selection.</summary>
    private static readonly string[] SpreadsheetClasses = ["XLMAIN"];
    private static readonly string[] SpreadsheetTitles = ["LibreOffice Calc", "OpenOffice Calc", "WPS Spreadsheets"];

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);

    /// <summary>What UI Automation could tell: the text, "nothing selected", or "this app does not say".</summary>
    private enum Answer { Text, Nothing, Unsupported }

    /// <summary>A read that has not finished yet (a hung app): no new ones pile up behind it.</summary>
    private static Task<string?>? _inFlight;

    /// <summary>The selected text of the focused control, or null when there is none or it cannot be read in time.</summary>
    public static async Task<string?> TryGetAsync()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || IsTerminal(fg)) return null;
        if (_inFlight is { IsCompleted: false }) return null;
        bool spreadsheet = IsSpreadsheet(fg);
        // UI Automation calls can block on a busy app; they run on a worker thread and we stop waiting after a moment.
        var work = _inFlight = Task.Run(() =>
        {
            var (answer, text) = Read();
            if (answer != Answer.Unsupported || spreadsheet) return text;
            var copied = TextInserter.CopySelection();
            if (copied != null) Log.Write($"selection: via Ctrl+C, {copied.Length} chars");
            return copied is { Length: > MaxChars } ? copied[..MaxChars] : copied;
        });
        var done = await Task.WhenAny(work, Task.Delay(Timeout));
        if (done != work) { Log.Write("selection: UI Automation timed out"); return null; }
        return work.Result;
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(128);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static bool IsTerminal(IntPtr hwnd) => TerminalClasses.Contains(ClassOf(hwnd));

    private static bool IsSpreadsheet(IntPtr hwnd)
    {
        if (SpreadsheetClasses.Contains(ClassOf(hwnd))) return true;
        var sb = new StringBuilder(512);
        GetWindowText(hwnd, sb, sb.Capacity);
        var title = sb.ToString();
        return SpreadsheetTitles.Any(t => title.EndsWith(t, StringComparison.OrdinalIgnoreCase));
    }

    private static (Answer, string?) Read()
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused == null) return (Answer.Unsupported, null);
            if (!focused.TryGetCurrentPattern(TextPattern.Pattern, out var p) || p is not TextPattern text) return (Answer.Unsupported, null);
            var ranges = text.GetSelection();
            if (ranges.Length == 0) return (Answer.Nothing, null);
            var sb = new StringBuilder();
            foreach (TextPatternRange r in ranges)
            {
                sb.Append(r.GetText(MaxChars - sb.Length));
                if (sb.Length >= MaxChars) break;
            }
            var s = sb.ToString();
            return s.Trim().Length == 0 ? (Answer.Nothing, null) : (Answer.Text, s);
        }
        catch (Exception e)
        {
            Log.Write($"selection: {e.GetType().Name}");
            return (Answer.Unsupported, null);
        }
    }
}
