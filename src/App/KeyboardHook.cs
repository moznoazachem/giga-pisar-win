// Global push-to-talk key. A low-level keyboard hook sees the key before any
// application does; we swallow it so the app underneath never notices the
// modifier being held.
//
// The hook lives on its own thread with its own message loop. Windows delivers
// every keystroke in the system to that thread and silently removes the hook
// if the callback is not serviced within a few hundred milliseconds, so it must
// never share a thread with anything that can block (audio device opening,
// clipboard access, recognition).

using System.Runtime.InteropServices;

namespace GigaPisar.App;

public sealed class KeyboardHook : IDisposable
{
    private readonly Native.LowLevelKeyboardProc _proc;   // kept alive for the unmanaged side
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private IntPtr _hook;
    private uint _threadId;
    private Exception? _installError;
    private volatile int _hotkeyVk;
    private bool _down;
    private bool _leftCtrlDown;
    private int _activeWinVk;
    private int _activeSingleVk;

    public int HotkeyVk { get => _hotkeyVk; set => _hotkeyVk = value; }

    /// <summary>Raised on the hook thread; handlers must return immediately (marshal to the UI thread).</summary>
    public event Action? Pressed;
    public event Action? Released;

    public KeyboardHook(int hotkeyVk)
    {
        _hotkeyVk = hotkeyVk;
        _proc = Callback;
        _thread = new Thread(Run) { IsBackground = true, Name = "GigaPisar.KeyboardHook" };
        _thread.Start();
        _ready.Wait();
        if (_installError != null) throw _installError;
    }

    private void Run()
    {
        _threadId = Native.GetCurrentThreadId();
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            _installError = new InvalidOperationException("SetWindowsHookEx failed: " + Marshal.GetLastWin32Error());
        _ready.Set();
        if (_hook == IntPtr.Zero) return;

        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
        Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            bool injected = (info.flags & Native.LLKHF_INJECTED) != 0;
            if (!injected)
            {
                int msg = (int)wParam;
                bool keyDown = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
                bool keyUp = msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP;
                int vk = info.vkCode == Native.VK_CONTROL
                    ? (info.flags & Native.LLKHF_EXTENDED) != 0 ? 0xA3 : 0xA2
                    : (int)info.vkCode;

                if (vk == 0xA2)
                {
                    if (keyDown) _leftCtrlDown = true;
                    if (keyUp)
                    {
                        _leftCtrlDown = false;
                        if (_activeWinVk != 0 && _down) { _down = false; Released?.Invoke(); }
                    }
                }

                if (vk == _activeWinVk)
                {
                    if (keyUp)
                    {
                        _activeWinVk = 0;
                        if (_down) { _down = false; Released?.Invoke(); }
                    }
                    return 1;
                }

                if (vk == _activeSingleVk)
                {
                    if (keyUp)
                    {
                        _activeSingleVk = 0;
                        if (_down) { _down = false; Released?.Invoke(); }
                    }
                    return 1;
                }

                if (_hotkeyVk == Settings.LeftCtrlWinHotkey)
                {
                    if (keyDown && _leftCtrlDown && (vk == 0x5B || vk == 0x5C))
                    {
                        _activeWinVk = vk;
                        _down = true;
                        Pressed?.Invoke();
                        return 1;
                    }
                }
                else if (vk == _hotkeyVk && keyDown)
                {
                    _activeSingleVk = vk;
                    _down = true;
                    Pressed?.Invoke();
                    return 1;
                }
            }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0) Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
        _ready.Dispose();
    }
}
