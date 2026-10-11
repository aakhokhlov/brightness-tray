using System.Runtime.InteropServices;

namespace BrightnessTray.Interop;

/// <summary>
/// Raw Win32 interop for the tray icon, the flyout window chrome,
/// the mouse-wheel hook and DDC/CI brightness control.
/// </summary>
internal static class NativeMethods
{
    // ----- window messages -----
    internal const uint WM_NULL = 0x0000;
    internal const uint WM_DESTROY = 0x0002;
    internal const uint WM_CONTEXTMENU = 0x007B;
    internal const uint WM_DISPLAYCHANGE = 0x007E;
    internal const uint WM_LBUTTONUP = 0x0202;
    internal const uint WM_RBUTTONUP = 0x0205;
    internal const uint WM_MOUSEWHEEL = 0x020A;
    internal const uint WM_APP_CALLBACK = 0x8000 + 1; // WM_APP + 1, tray callback
    internal const uint NIN_SELECT = 0x0400;     // WM_USER: icon selected (mouse or Enter/Space)
    internal const uint NIN_KEYSELECT = 0x0401;  // NIN_SELECT | NINF_KEY: keyboard selection
    internal const uint NIN_POPUPOPEN = 0x0406;  // hover/focus tooltip opened (not handled)
    internal const uint NIN_POPUPCLOSE = 0x0407; // hover/focus tooltip closed (not handled)

    // ----- notify icon -----
    internal const uint NIM_ADD = 0;
    internal const uint NIM_MODIFY = 1;
    internal const uint NIM_DELETE = 2;
    internal const uint NIM_SETVERSION = 4;
    internal const uint NIF_MESSAGE = 0x01;
    internal const uint NIF_ICON = 0x02;
    internal const uint NIF_TIP = 0x04;
    internal const uint NIF_SHOWTIP = 0x80;
    internal const uint NOTIFYICON_VERSION_4 = 4;

    // ----- low level mouse hook -----
    internal const int WH_MOUSE_LL = 14;

    // ----- window styles -----
    internal const int GWL_STYLE = -16;
    internal const int GWL_EXSTYLE = -20;
    internal const long WS_POPUP = 0x80000000;
    internal const long WS_CAPTION = 0x00C00000;
    internal const long WS_THICKFRAME = 0x00040000;
    internal const long WS_SYSMENU = 0x00080000;
    internal const long WS_EX_TOOLWINDOW = 0x00000080;

    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_FRAMECHANGED = 0x0020;

    internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    internal const int DWMWCP_ROUND = 2;

    internal const uint MONITOR_DEFAULTTONEAREST = 2;

    internal const uint IMAGE_ICON = 1;
    internal const uint LR_LOADFROMFILE = 0x0010;
    internal const uint LR_DEFAULTSIZE = 0x0040;

    // ----- popup menu -----
    internal const uint MF_STRING = 0x0000;
    internal const uint MF_SEPARATOR = 0x0800;
    internal const uint MF_CHECKED = 0x0008;
    internal const uint TPM_RETURNCMD = 0x0100;
    internal const uint TPM_RIGHTBUTTON = 0x0002;
    internal const uint TPM_BOTTOMALIGN = 0x0020;

    // ----- delegates -----
    internal delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    internal delegate IntPtr HookProcDelegate(int code, IntPtr wParam, IntPtr lParam);
    internal delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);

    // ----- structs -----
    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSW
    {
        public uint style;
        [MarshalAs(UnmanagedType.FunctionPtr)] public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct PHYSICAL_MONITOR
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }

    // ----- user32 -----
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassW(ref WNDCLASSW lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu,
        IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    internal static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint RegisterWindowMessageW(string message);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    internal static extern int GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y,
        int width, int height, uint flags);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr LoadImageW(IntPtr hInstance, string name, uint type,
        int cx, int cy, uint load);

    [DllImport("user32.dll")]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookExW(int idHook, HookProcDelegate lpfn,
        IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll")]
    internal static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    internal static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool AppendMenuW(IntPtr menu, uint flags, IntPtr idOrPosition, string text);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y,
        IntPtr hWnd, IntPtr parameters);

    // ----- dxva2 (DDC/CI) -----
    [DllImport("user32.dll")]
    internal static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("dxva2.dll")]
    internal static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, ref uint count);

    [DllImport("dxva2.dll")]
    internal static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count,
        [Out] PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll")]
    internal static extern bool DestroyPhysicalMonitor(IntPtr hMonitor);

    [DllImport("dxva2.dll")]
    internal static extern bool GetMonitorBrightness(IntPtr hMonitor, ref uint min, ref uint cur, ref uint max);

    [DllImport("dxva2.dll")]
    internal static extern bool SetMonitorBrightness(IntPtr hMonitor, uint value);

    // ----- shell: Shell_NotifyIcon moved from shellapi.dll into shell32.dll
    // on newer Windows builds, so resolve the entry points at runtime. -----

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate bool ShellNotifyIconDelegate(uint message, ref NOTIFYICONDATAW data);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int ShellNotifyIconGetRectDelegate(ref NOTIFYICONIDENTIFIER identifier, ref RECT rect);

    internal static class ShellApi
    {
        public static readonly ShellNotifyIconDelegate NotifyIcon = ResolveFunction<ShellNotifyIconDelegate>("Shell_NotifyIconW");
        public static readonly ShellNotifyIconGetRectDelegate GetRect = ResolveFunction<ShellNotifyIconGetRectDelegate>("Shell_NotifyIconGetRect");

        private static T ResolveFunction<T>(string name)
            where T : Delegate
        {
            foreach (string dll in new[] { "shell32.dll", "shellapi.dll" })
            {
                IntPtr module = LoadLibraryW(dll);
                if (module == IntPtr.Zero) continue;

                IntPtr address = GetProcAddress(module, name);
                if (address != IntPtr.Zero)
                {
                    return Marshal.GetDelegateForFunctionPointer<T>(address);
                }
            }

            throw new DllNotFoundException($"Could not resolve {name} from shell32.dll or shellapi.dll.");
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string fileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
    }
}
