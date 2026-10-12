using System.Runtime.InteropServices;

namespace BrightnessTray.Interop;

/// <summary>A context menu item shown on right click.</summary>
internal sealed class TrayMenuItem
{
    public string Text { get; init; } = string.Empty;
    public bool Checked { get; init; }
    public bool IsSeparator { get; init; }
    public Action? OnClick { get; init; }
}

/// <summary>
/// Hidden message window that owns the Shell_NotifyIcon tray icon.
/// Uses the NOTIFYICON_VERSION_4 callback protocol:
///   LOWORD(lParam) = event, HIWORD(lParam) = icon id, wParam = anchor point.
/// </summary>
internal sealed class TrayIconWindow : IDisposable
{
    private readonly NativeMethods.WndProcDelegate _wndProc; // keep the delegate alive
    private readonly IntPtr _hwnd;
    private readonly IntPtr _hIcon;
    private readonly uint _taskbarCreatedMessage;
    private NativeMethods.NOTIFYICONDATAW _nid;
    private long _lastSelectTicks;
    private bool _disposed;

    public IntPtr WindowHandle => _hwnd;
    public uint IconId => 1;

    /// <summary>Left click or Enter key on the icon; argument = anchor point.</summary>
    public event Action<NativeMethods.POINT>? LeftClick;

    /// <summary>Right click or Menu key on the icon; argument = anchor point.</summary>
    public event Action<NativeMethods.POINT>? RightClick;

    /// <summary>WM_DISPLAYCHANGE: display topology or resolution changed.</summary>
    public event Action? DisplaySettingsChanged;

    public TrayIconWindow(Stream iconStream)
    {
        _wndProc = WndProc;

        var wc = new NativeMethods.WNDCLASSW
        {
            lpfnWndProc = _wndProc,
            hInstance = NativeMethods.GetModuleHandleW(null),
            lpszClassName = "BrightnessTrayIconWindow",
        };
        _ = NativeMethods.RegisterClassW(ref wc);

        _hwnd = NativeMethods.CreateWindowExW(0, "BrightnessTrayIconWindow", "BrightnessTrayIcon", 0,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessageW("TaskbarCreated");

        _hIcon = CreateIconFromIcoStream(iconStream);
        if (_hIcon == IntPtr.Zero)
        {
            throw new IOException("Tray icon could not be created from the embedded resource.");
        }

        _nid = new NativeMethods.NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = IconId,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP,
            uCallbackMessage = NativeMethods.WM_APP_CALLBACK,
            hIcon = _hIcon,
            szTip = "Brightness",
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };
        AddIcon();
    }

    public void SetTooltip(string text)
    {
        if (_disposed || text.Length >= 128) return;

        var nid = _nid;
        nid.szTip = text;
        nid.uFlags = NativeMethods.NIF_TIP;
        _ = NativeMethods.ShellApi.NotifyIcon(NativeMethods.NIM_MODIFY, ref nid);
    }

    /// <summary>Shows a native popup menu at the anchor and invokes the chosen item.</summary>
    public void ShowContextMenu(int x, int y, IReadOnlyList<TrayMenuItem> items)
    {
        if (_disposed) return;

        IntPtr menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        try
        {
            var map = new Dictionary<IntPtr, TrayMenuItem>();
            IntPtr id = IntPtr.Zero;
            foreach (TrayMenuItem item in items)
            {
                id = new IntPtr(id.ToInt64() + 1);
                if (item.IsSeparator)
                {
                    _ = NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, IntPtr.Zero, string.Empty);
                }
                else
                {
                    uint flags = NativeMethods.MF_STRING | (item.Checked ? NativeMethods.MF_CHECKED : 0);
                    if (NativeMethods.AppendMenuW(menu, flags, id, item.Text))
                    {
                        map[id] = item;
                    }
                }
            }

            // Standard NotifyIcon recipe: makes the menu close when the user
            // clicks elsewhere, and pumps messages while the menu is open.
            _ = NativeMethods.SetForegroundWindow(_hwnd);
            uint choice = NativeMethods.TrackPopupMenuEx(menu,
                NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_BOTTOMALIGN,
                x, y, _hwnd, IntPtr.Zero);
            _ = NativeMethods.PostMessageW(_hwnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (choice != 0 && map.TryGetValue(new IntPtr(choice), out TrayMenuItem? selected))
            {
                selected.OnClick?.Invoke();
            }
        }
        finally
        {
            _ = NativeMethods.DestroyMenu(menu);
        }
    }

    private void AddIcon()
    {
        var nid = _nid;
        if (!NativeMethods.ShellApi.NotifyIcon(NativeMethods.NIM_ADD, ref nid))
        {
            throw new InvalidOperationException("Shell_NotifyIcon(NIM_ADD) failed.");
        }

        var version = _nid;
        version.uFlags = 0;
        version.uVersion = NativeMethods.NOTIFYICON_VERSION_4;
        _ = NativeMethods.ShellApi.NotifyIcon(NativeMethods.NIM_SETVERSION, ref version);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _taskbarCreatedMessage)
        {
            // Explorer restarted: re-register the icon.
            if (!_disposed) AddIcon();
        }
        else if (msg == NativeMethods.WM_APP_CALLBACK)
        {
            uint evt = (uint)(lParam.ToInt64() & 0xFFFF);

            // NIN_POPUPOPEN / NIN_POPUPCLOSE (tooltip opened/closed while
            // hovering or keyboard-focusing the icon) are not click events.
            if (evt is NativeMethods.NIN_POPUPOPEN or NativeMethods.NIN_POPUPCLOSE)
            {
                return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
            }

            bool keyboard = evt is NativeMethods.NIN_KEYSELECT;
            if (evt is NativeMethods.WM_LBUTTONUP or NativeMethods.NIN_SELECT
                or NativeMethods.NIN_KEYSELECT)
            {
                // A mouse click is delivered as both WM_LBUTTONUP and
                // NIN_SELECT; act on the first one only.
                long ticks = Environment.TickCount64;
                if (ticks - _lastSelectTicks > 250)
                {
                    _lastSelectTicks = ticks;
                    LeftClick?.Invoke(AnchorForEvent(keyboard, wParam));
                }
            }
            else if (evt is NativeMethods.WM_RBUTTONUP or NativeMethods.WM_CONTEXTMENU)
            {
                RightClick?.Invoke(AnchorForEvent(keyboard: false, IntPtr.Zero));
            }
        }
        else if (msg == NativeMethods.WM_DISPLAYCHANGE)
        {
            DisplaySettingsChanged?.Invoke();
        }
        else if (msg == NativeMethods.WM_DESTROY)
        {
            _ = NativeMethods.PostMessageW(hWnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);
        }

        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// The flyout anchor: the center of the icon's on-screen rectangle, so
    /// mouse and keyboard activation place the flyout in the same spot. Falls
    /// back to the cursor position when the rect is unavailable.
    /// </summary>
    private NativeMethods.POINT AnchorForEvent(bool keyboard, IntPtr wParam)
    {
        var identifier = new NativeMethods.NOTIFYICONIDENTIFIER
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONIDENTIFIER>(),
            hWnd = _hwnd,
            uID = IconId,
        };

        var rect = new NativeMethods.RECT();
        if (NativeMethods.ShellApi.GetRect(ref identifier, ref rect) == 0 &&
            rect.Right > rect.Left && rect.Bottom > rect.Top)
        {
            return new NativeMethods.POINT
            {
                X = (rect.Left + rect.Right) / 2,
                Y = (rect.Top + rect.Bottom) / 2,
            };
        }

        if (keyboard)
        {
            // NIN_KEYSELECT carries the icon's top-left corner in wParam.
            long w = wParam.ToInt64();
            return new NativeMethods.POINT
            {
                X = (short)(w & 0xFFFF),
                Y = (short)((w >> 16) & 0xFFFF),
            };
        }

        _ = NativeMethods.GetCursorPos(out var cursor);
        return cursor;
    }

    /// <summary>
    /// Parses an .ico stream and creates the HICON best matching the standard
    /// tray size. Entries may be BMP- or PNG-encoded (both are passed to
    /// CreateIconFromResourceEx unchanged).
    /// </summary>
    private static IntPtr CreateIconFromIcoStream(Stream stream)
    {
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt16() != 0) return IntPtr.Zero; // reserved
        if (reader.ReadUInt16() != 1) return IntPtr.Zero; // must be an .ico
        ushort count = reader.ReadUInt16();
        if (count == 0) return IntPtr.Zero;

        // Gather entries, preferring the one closest to the system icon size
        // (breaking ties toward the larger image).
        int desired = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXICON);
        uint bestOffset = 0;
        uint bestSize = 0;
        int bestScore = int.MaxValue;

        for (int i = 0; i < count; i++)
        {
            byte width = reader.ReadByte();  // 0 means 256
            byte height = reader.ReadByte(); // 0 means 256
            _ = reader.ReadBytes(6);         // color count, reserved, planes, bit count
            uint size = reader.ReadUInt32();
            uint offset = reader.ReadUInt32();

            int w = width == 0 ? 256 : width;
            int score = Math.Abs(w - desired) * 2 - (w > desired ? 1 : 0);
            if (score < bestScore || (score == bestScore && w > bestSize))
            {
                bestScore = score;
                bestOffset = offset;
                bestSize = size;
            }
        }
        if (bestSize == 0) return IntPtr.Zero;

        _ = stream.Seek(bestOffset, SeekOrigin.Begin);
        byte[] data = reader.ReadBytes((int)bestSize);

        // Entries may be BMP- or PNG-encoded; CreateIconFromResourceEx accepts
        // both as-is (PNG entries must keep their 8-byte file header).
        return NativeMethods.CreateIconFromResourceEx(data, (uint)data.Length, fIcon: true,
            dwVer: 0x00030000, cxDesired: 0, cyDesired: 0, flags: NativeMethods.LR_DEFAULTSIZE);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var nid = _nid;
        nid.uFlags = 0;
        _ = NativeMethods.ShellApi.NotifyIcon(NativeMethods.NIM_DELETE, ref nid);
        _ = NativeMethods.DestroyWindow(_hwnd);
        if (_hIcon != IntPtr.Zero) _ = NativeMethods.DestroyIcon(_hIcon);
    }
}
