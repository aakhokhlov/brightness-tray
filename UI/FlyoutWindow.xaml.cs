using BrightnessTray.Interop;
using BrightnessTray.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace BrightnessTray.UI;

/// <summary>
/// The small volume-flyout-style panel above the tray icon: one slider that
/// controls every controllable display, plus a note row for the rest.
/// Borderless, always on top, hidden from Alt-Tab, hides when deactivated.
/// </summary>
public sealed partial class FlyoutWindow : Window
{
    private const double WidthDip = 360;
    private const double BaseHeightDip = 56;   // padding + slider row
    private const double InfoRowHeightDip = 20;

    private readonly BrightnessController _controller;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _applyTimer;
    private readonly IntPtr _hwnd;
    private bool _updatingFromCode;
    private bool _applying;
    private int _infoRowCount;
    private bool _visible;

    internal bool IsVisible => _visible;

    /// <summary>Tick count of the last hide, used to detect click-to-toggle.</summary>
    internal long LastHiddenMs { get; private set; }

    internal FlyoutWindow(BrightnessController controller)
    {
        _controller = controller;
        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        ConfigureChrome();

        SystemBackdrop = new DesktopAcrylicBackdrop();
        Activated += OnActivated;

        _controller.PercentChanged += OnPercentChanged;
        _controller.MonitorsChanged += OnMonitorsChanged;

        // Debounces slider dragging: DDC/CI writes are slow, so the value is
        // applied a short moment after the last change.
        _applyTimer = DispatcherQueue.CreateTimer();
        _applyTimer.Interval = TimeSpan.FromMilliseconds(350);
        _applyTimer.Tick += async (_, _) =>
        {
            _applyTimer.Stop();
            if (_applying)
            {
                // A previous apply is still in flight: wait for it to finish.
                _applyTimer.Start();
                return;
            }

            _applying = true;
            try
            {
                await _controller.ApplyAsync((int)BrightnessSlider.Value);
            }
            finally
            {
                _applying = false;
            }
        };
    }

    /// <summary>Removes the window chrome, keeps it out of Alt-Tab and the taskbar.</summary>
    private void ConfigureChrome()
    {
        AppWindow.Title = "Brightness";
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        ExtendsContentIntoTitleBar = true;

        long style = NativeMethods.GetWindowLongPtrW(_hwnd, NativeMethods.GWL_STYLE).ToInt64();
        style &= ~(NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME | NativeMethods.WS_SYSMENU);
        style |= NativeMethods.WS_POPUP;
        _ = NativeMethods.SetWindowLongPtrW(_hwnd, NativeMethods.GWL_STYLE, new IntPtr(style));

        long exStyle = NativeMethods.GetWindowLongPtrW(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW;
        _ = NativeMethods.SetWindowLongPtrW(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));

        _ = NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER |
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);

        int round = NativeMethods.DWMWCP_ROUND;
        _ = NativeMethods.DwmSetWindowAttribute(_hwnd,
            NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }

    /// <summary>Shows the flyout above the tray anchor with the slider ready for input.</summary>
    internal void ShowNear(NativeMethods.POINT anchor)
    {
        RebuildRows();
        SizeInt32 size = ComputeSize();
        NativeMethods.RECT work = GetWorkArea(anchor);

        int x = Math.Clamp(anchor.X - size.Width / 2, work.Left,
            Math.Max(work.Left, work.Right - size.Width));
        int y = anchor.Y - size.Height - 12;
        if (y < work.Top)
        {
            y = Math.Min(anchor.Y + 16, work.Bottom - size.Height);
        }

        AppWindow.Resize(size);
        AppWindow.Move(new PointInt32(x, y));
        // The flyout must own the foreground (hidden + always-on-top + no
        // taskbar entry means nothing else gives it focus): keyboard input,
        // Esc handling and click-away deactivation all depend on it.
        _ = NativeMethods.SetForegroundWindow(_hwnd);
        AppWindow.Show();
        Activate();
        _visible = true;

        // The slider is the hot path: focus it so arrow keys work immediately.
        BrightnessSlider.Focus(FocusState.Programmatic);
    }

    internal void HideNow()
    {
        if (!_visible) return;
        _visible = false;
        LastHiddenMs = Environment.TickCount64;
        AppWindow.Hide();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated && _visible)
        {
            HideNow();
        }
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape)
        {
            HideNow();
        }
    }

    private void OnSliderValueChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (_updatingFromCode) return;

        PercentText.Text = $"{(int)BrightnessSlider.Value}%";
        _applyTimer.Stop();
        _applyTimer.Start();
    }

    private void OnPercentChanged(int percent)
    {
        _updatingFromCode = true;
        try
        {
            if (BrightnessSlider.IsEnabled)
            {
                BrightnessSlider.Value = percent;
            }
            PercentText.Text = $"{percent}%";
        }
        finally
        {
            _updatingFromCode = false;
        }
    }

    private void OnMonitorsChanged()
    {
        if (!_visible) return;

        RebuildRows();
        AppWindow.Resize(ComputeSize());
    }

    private void RebuildRows()
    {
        InfoRows.Children.Clear();

        IReadOnlyList<MonitorItem> monitors = _controller.Monitors;
        BrightnessSlider.IsEnabled = _controller.HasTargets;

        if (!_controller.HasTargets)
        {
            _infoRowCount = 1;
            InfoRows.Children.Add(InfoText(monitors.Count == 0
                ? "No displays found"
                : "No controllable displays"));
            return;
        }

        var unsupported = monitors.Where(m => !m.SupportsBrightness).ToList();
        _infoRowCount = unsupported.Count;
        foreach (var monitor in unsupported)
        {
            InfoRows.Children.Add(InfoText($"{monitor.Name} — no DDC/CI"));
        }
    }

    private static TextBlock InfoText(string text) => new()
    {
        Text = text,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Foreground = SecondaryBrush(),
    };

    private static Brush SecondaryBrush()
    {
        if (Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out object? brush) &&
            brush is Brush b)
        {
            return b;
        }
        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    private SizeInt32 ComputeSize()
    {
        double scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        double heightDip = BaseHeightDip + (_infoRowCount > 0 ? 10 + _infoRowCount * InfoRowHeightDip : 0);
        return new SizeInt32(
            (int)Math.Round(WidthDip * scale),
            (int)Math.Round(heightDip * scale));
    }

    private static NativeMethods.RECT GetWorkArea(NativeMethods.POINT anchor)
    {
        IntPtr monitor = NativeMethods.MonitorFromPoint(anchor, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };
        _ = NativeMethods.GetMonitorInfoW(monitor, ref info);
        return info.rcWork;
    }
}
