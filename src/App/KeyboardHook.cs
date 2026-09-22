// Global push-to-talk key. A low-level keyboard hook sees the key before any
// application does; we swallow it so the app underneath never notices the
// modifier being held.

namespace GigaPisar.App;

public sealed class KeyboardHook : IDisposable
{
    private readonly Native.LowLevelKeyboardProc _proc;   // kept alive for the unmanaged side
    private IntPtr _hook;
    private bool _down;

    public int HotkeyVk { get; set; }
    public event Action? Pressed;
    public event Action? Released;

    public KeyboardHook(int hotkeyVk)
    {
        HotkeyVk = hotkeyVk;
        _proc = Callback;
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException("SetWindowsHookEx failed: " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            bool injected = (info.flags & Native.LLKHF_INJECTED) != 0;
            if (!injected && info.vkCode == (uint)HotkeyVk)
            {
                int msg = (int)wParam;
                if (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN)
                {
                    if (!_down) { _down = true; Pressed?.Invoke(); }
                    return 1;   // swallow, including auto-repeat
                }
                if (msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP)
                {
                    if (_down) { _down = false; Released?.Invoke(); }
                    return 1;
                }
            }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
    }
}
