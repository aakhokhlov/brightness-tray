using System.Runtime.InteropServices;

namespace BrightnessTray.Interop;

/// <summary>
/// The shell never forwards WM_MOUSEWHEEL to notify icons (verified against
/// the NOTIFYICON_VERSION_4 protocol), so wheel-over-icon is implemented with
/// a low-level mouse hook: on every wheel event the cursor position is matched
/// against the tray icon rectangle (Shell_NotifyIconGetRect).
/// The hook is installed on the UI thread, which pumps messages.
/// </summary>
internal sealed class TrayWheelMonitor : IDisposable
{
    private readonly NativeMethods.HookProcDelegate _proc; // keep the delegate alive
    private readonly IntPtr _hook;
    private readonly Func<IntPtr> _iconWindow;
    private readonly Func<uint> _iconId;
    private bool _disposed;

    /// <summary>Raised with the scrolled notches (positive = up). Only while the cursor is over the tray icon.</summary>
    public event Action<int>? Wheel;

    public TrayWheelMonitor(Func<IntPtr> iconWindow, Func<uint> iconId)
    {
        _iconWindow = iconWindow;
        _iconId = iconId;
        _proc = HookProc;
        _hook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_MOUSE_LL, _proc,
            NativeMethods.GetModuleHandleW(null), 0);
    }

    private IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (uint)wParam == NativeMethods.WM_MOUSEWHEEL)
        {
            var info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            if (IsCursorOverIcon(info.pt))
            {
                short delta = (short)(info.mouseData >> 16);
                Wheel?.Invoke(delta / 120);
            }
        }

        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private bool IsCursorOverIcon(NativeMethods.POINT pt)
    {
        IntPtr hwnd = _iconWindow();
        if (hwnd == IntPtr.Zero) return false;

        var identifier = new NativeMethods.NOTIFYICONIDENTIFIER
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONIDENTIFIER>(),
            hWnd = hwnd,
            uID = _iconId(),
        };

        var rect = new NativeMethods.RECT();
        bool over =
            NativeMethods.ShellApi.GetRect(ref identifier, ref rect) == 0 &&
            pt.X >= rect.Left && pt.X < rect.Right &&
            pt.Y >= rect.Top && pt.Y < rect.Bottom;

        return over;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hook != IntPtr.Zero) _ = NativeMethods.UnhookWindowsHookEx(_hook);
    }
}
