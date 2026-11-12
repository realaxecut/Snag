using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Snag.Services;

/// <summary>
/// Nuclear mode's keyboard lock: a low-level keyboard hook that swallows keys everywhere (Alt+F4, Alt+Tab, the
/// Windows key, Esc, everything) except the ones <c>allow</c> lets through.
/// <list type="bullet">
/// <item>Ctrl+Alt+Delete still works: Windows handles it before any app can see it. That's the emergency exit.</item>
/// <item>The hook belongs to Snag's process: it's gone the moment Snag closes or crashes, and Windows skips it if
/// Snag ever stops responding, so the keyboard can never be left stuck.</item>
/// </list>
/// </summary>
public sealed class KeyboardLock : IDisposable
{
    private const int LowLevelKeyboardHook = 13;

    private readonly HookProc _callback; // kept alive here: Windows calls it for as long as the hook exists
    private readonly Func<int, bool> _allow;
    private IntPtr _hook;

    /// <param name="allow">Given a virtual-key code, true lets that key through.</param>
    public KeyboardLock(Func<int, bool> allow)
    {
        _allow = allow;
        _callback = OnKey;
        using var module = Process.GetCurrentProcess().MainModule;
        _hook = SetWindowsHookEx(LowLevelKeyboardHook, _callback, GetModuleHandle(module?.ModuleName), 0);
        if (_hook == IntPtr.Zero) Log.Error($"Couldn't lock the keyboard (Windows error {Marshal.GetLastWin32Error()})");
        else Log.Info("Keyboard locked");
    }

    private IntPtr OnKey(int code, IntPtr message, IntPtr data)
    {
        // The first field of KBDLLHOOKSTRUCT is the virtual-key code.
        if (code >= 0 && !_allow(Marshal.ReadInt32(data))) return 1;
        return CallNextHookEx(_hook, code, message, data);
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        Log.Info("Keyboard unlocked");
    }

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookId, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
