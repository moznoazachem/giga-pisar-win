// Puts recognized text into whatever has keyboard focus.
//
// Default: put the text on the clipboard, press Ctrl+V, restore the
// clipboard. Alternative: synthesize Unicode key events, which leaves the
// clipboard alone but some apps (Windows 11 Notepad among them) garble when
// characters arrive faster than they read keyboard state, so we feed them
// in small batches with a pause.

using System.Windows;

namespace GigaPisar.App;

public static class TextInserter
{
    public static void Insert(string text, InsertMode mode)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (mode == InsertMode.Paste) Paste(text);
        else Type(text);
    }

    public static void Type(string text)
    {
        var inputs = new List<Native.INPUT>(text.Length * 2);
        foreach (char ch in text)
        {
            if (ch == '\n')
            {
                inputs.Add(Key(0x0D, 0, 0));
                inputs.Add(Key(0x0D, 0, Native.KEYEVENTF_KEYUP));
                continue;
            }
            if (ch == '\r') continue;
            inputs.Add(Key(0, ch, Native.KEYEVENTF_UNICODE));
            inputs.Add(Key(0, ch, Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP));
        }

        const int batch = 16;   // 8 characters per call
        var size = System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>();
        for (int i = 0; i < inputs.Count; i += batch)
        {
            var slice = inputs.GetRange(i, Math.Min(batch, inputs.Count - i)).ToArray();
            Native.SendInput((uint)slice.Length, slice, size);
            Thread.Sleep(8);
        }
    }

    public static void Paste(string text)
    {
        IDataObject? saved = null;
        try { saved = Clipboard.GetDataObject(); } catch { }

        try
        {
            Clipboard.SetDataObject(text, true);
        }
        catch (Exception e)
        {
            Log.Write($"clipboard set failed, typing instead: {e.Message}");
            Type(text);
            return;
        }

        var inputs = new[]
        {
            Key(Native.VK_CONTROL, 0, 0),
            Key(Native.VK_V, 0, 0),
            Key(Native.VK_V, 0, Native.KEYEVENTF_KEYUP),
            Key(Native.VK_CONTROL, 0, Native.KEYEVENTF_KEYUP),
        };
        Native.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());

        if (saved != null)
        {
            // Give the target app time to read the clipboard before restoring it.
            var restore = saved;
            Task.Delay(400).ContinueWith(_ =>
            {
                try { Application.Current.Dispatcher.Invoke(() => Clipboard.SetDataObject(restore, true)); }
                catch { }
            });
        }
    }

    private static Native.INPUT Key(ushort vk, ushort scan, uint flags) => new()
    {
        type = Native.INPUT_KEYBOARD,
        u = new Native.INPUTUNION { ki = new Native.KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } },
    };
}
