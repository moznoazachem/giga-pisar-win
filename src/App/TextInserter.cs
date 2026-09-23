// Puts recognized text into whatever has keyboard focus.
//
// Default: put the text on the clipboard, press Ctrl+V, restore the previous
// clipboard. The text is marked so Windows keeps it out of Clipboard History
// and the cloud clipboard. Alternative: synthesize Unicode key events, which
// leaves the clipboard alone; some apps (Windows 11 Notepad among them) garble
// fast input, so characters go in small batches with a pause.
//
// Runs on a worker thread; clipboard calls are marshalled to the UI thread
// because WPF's Clipboard requires STA.

using System.Runtime.InteropServices;
using System.Windows;

namespace GigaPisar.App;

public enum InsertResult { Done, Blocked }

public static class TextInserter
{
    private const int CharsPerBatch = 8;
    private const int BatchPauseMs = 8;
    private const int ClipboardRestoreDelayMs = 800;
    private const int ErrorAccessDenied = 5;

    public static InsertResult Insert(string text, InsertMode mode)
    {
        if (string.IsNullOrEmpty(text)) return InsertResult.Done;
        return mode == InsertMode.Paste ? Paste(text) : Type(text);
    }

    public static InsertResult Type(string text)
    {
        var inputs = new List<Native.INPUT>(text.Length * 2);
        foreach (char ch in text)
        {
            if (ch == '\n')
            {
                inputs.Add(Key(Native.VK_RETURN, 0, 0));
                inputs.Add(Key(Native.VK_RETURN, 0, Native.KEYEVENTF_KEYUP));
                continue;
            }
            if (ch == '\r') continue;
            inputs.Add(Key(0, ch, Native.KEYEVENTF_UNICODE));
            inputs.Add(Key(0, ch, Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP));
        }

        const int batch = CharsPerBatch * 2;
        for (int i = 0; i < inputs.Count; i += batch)
        {
            var slice = inputs.GetRange(i, Math.Min(batch, inputs.Count - i)).ToArray();
            if (!Send(slice)) return InsertResult.Blocked;
            Thread.Sleep(BatchPauseMs);
        }
        return InsertResult.Done;
    }

    public static InsertResult Paste(string text)
    {
        var ui = Application.Current.Dispatcher;
        IDataObject? saved = ui.Invoke(Snapshot);

        bool placed = ui.Invoke(() =>
        {
            try
            {
                var data = new DataObject();
                data.SetData(DataFormats.UnicodeText, text);
                // Windows honours these formats: no Clipboard History entry, no cloud sync.
                data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[4]));
                data.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));
                data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[4]));
                Clipboard.SetDataObject(data, true);
                return true;
            }
            catch (Exception e)
            {
                Log.Write($"clipboard set failed, typing instead: {e.Message}");
                return false;
            }
        });
        if (!placed) return Type(text);

        var ok = Send(new[]
        {
            Key(Native.VK_CONTROL, 0, 0),
            Key(Native.VK_V, 0, 0),
            Key(Native.VK_V, 0, Native.KEYEVENTF_KEYUP),
            Key(Native.VK_CONTROL, 0, Native.KEYEVENTF_KEYUP),
        });
        if (!ok) return InsertResult.Blocked;   // our text stays on the clipboard so the user can paste by hand

        if (saved != null)
        {
            // Give the target app time to read the clipboard, then put the old content back,
            // but only if nobody (including the user) has changed the clipboard meanwhile.
            Thread.Sleep(ClipboardRestoreDelayMs);
            ui.Invoke(() =>
            {
                try
                {
                    if (Clipboard.ContainsText() && Clipboard.GetText() == text)
                        Clipboard.SetDataObject(saved, true);
                }
                catch (Exception e) { Log.Write($"clipboard restore failed: {e.Message}"); }
            });
        }
        return InsertResult.Done;
    }

    /// <summary>Deep copy of the current clipboard: the live object belongs to another app and dies when we replace it.</summary>
    private static IDataObject? Snapshot()
    {
        try
        {
            var live = Clipboard.GetDataObject();
            if (live == null) return null;
            var copy = new DataObject();
            int formats = 0;
            foreach (var format in live.GetFormats(false))
            {
                try
                {
                    var value = live.GetData(format, false);
                    if (value == null) continue;
                    copy.SetData(format, value);
                    formats++;
                }
                catch { /* delayed-render or private format: skip it */ }
            }
            return formats > 0 ? copy : null;
        }
        catch (Exception e)
        {
            Log.Write($"clipboard snapshot failed: {e.Message}");
            return null;
        }
    }

    private static bool Send(Native.INPUT[] inputs)
    {
        uint sent = Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
        if (sent == inputs.Length) return true;
        int error = Marshal.GetLastWin32Error();
        Log.Write($"SendInput sent {sent}/{inputs.Length}, error {error}" + (error == ErrorAccessDenied ? " (target runs elevated)" : ""));
        return false;
    }

    private static Native.INPUT Key(ushort vk, ushort scan, uint flags) => new()
    {
        type = Native.INPUT_KEYBOARD,
        u = new Native.INPUTUNION { ki = new Native.KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } },
    };
}
