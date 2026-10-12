using BrightnessTray.Interop;
using BrightnessTray.Services;
using BrightnessTray.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace BrightnessTray;

/// <summary>
/// Composition root: single-instance guard, settings, autostart, tray icon,
/// wheel monitor and the brightness flyout. No main window.
/// </summary>
public partial class App : Application
{
    private static Mutex? _mutex;

    private BrightnessController? _controller;
    private FlyoutWindow? _flyout;
    private TrayIconWindow? _tray;
    private TrayWheelMonitor? _wheel;

    public App()
    {
        _mutex = new Mutex(initiallyOwned: true, @"Local\BrightnessTray.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            // Another instance is already running.
            Environment.Exit(0);
        }

        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Settings.Load();
        Autostart.Sync(Settings.Current.Autostart);

        var controller = new BrightnessController(DispatcherQueue.GetForCurrentThread());
        _controller = controller;
        controller.PercentChanged += percent => _tray?.SetTooltip($"Brightness — {percent}%");

        _flyout = new FlyoutWindow(controller);

        using var iconStream = typeof(App).Assembly.GetManifestResourceStream("BrightnessTray.Assets.Icon.ico")
            ?? throw new InvalidOperationException("Embedded tray icon resource 'BrightnessTray.Assets.Icon.ico' not found.");
        _tray = new TrayIconWindow(iconStream);
        _tray.SetTooltip("Brightness");
        _tray.LeftClick += OnTrayLeftClick;
        _tray.RightClick += OnTrayRightClick;
        _tray.DisplaySettingsChanged += () => controller.ScheduleRefresh();

        _wheel = new TrayWheelMonitor(() => _tray.WindowHandle, () => _tray.IconId);
        _wheel.Wheel += OnTrayWheel;

        controller.Initialize();
    }

    private void OnTrayLeftClick(NativeMethods.POINT anchor)
    {
        if (_flyout is null) return;

        if (_flyout.IsVisible)
        {
            _flyout.HideNow();
        }
        else if (Environment.TickCount64 - _flyout.LastHiddenMs < 300)
        {
            // The click that deactivated (and hid) the flyout a moment ago:
            // treat it as "toggle off" instead of reopening immediately.
        }
        else
        {
            _flyout.ShowNear(anchor);
        }
    }

    private void OnTrayRightClick(NativeMethods.POINT anchor)
    {
        _tray?.ShowContextMenu(anchor.X, anchor.Y, BuildMenu());
    }

    private void OnTrayWheel(int notches)
    {
        if (_controller is { HasTargets: true })
        {
            _ = _controller.StepAsync(notches);
        }
    }

    private static List<TrayMenuItem> BuildMenu() => new()
    {
        new TrayMenuItem
        {
            Text = "Autostart with Windows",
            Checked = Settings.Current.Autostart,
            OnClick = () =>
            {
                Settings.Current.Autostart = !Settings.Current.Autostart;
                Settings.Save();
                Autostart.Sync(Settings.Current.Autostart);
            },
        },
        new TrayMenuItem
        {
            Text = "Restore brightness on startup",
            Checked = Settings.Current.RestoreOnStartup,
            OnClick = () =>
            {
                Settings.Current.RestoreOnStartup = !Settings.Current.RestoreOnStartup;
                Settings.Save();
            },
        },
        new TrayMenuItem { IsSeparator = true },
        new TrayMenuItem { Text = "Exit", OnClick = ExitApp },
    };

    private static void ExitApp()
    {
        if (Current is not App app) return;

        Settings.Save();
        app._controller?.Dispose();
        app._flyout?.Close();
        app._wheel?.Dispose();
        app._tray?.Dispose();
        _mutex?.Dispose();
        Environment.Exit(0);
    }
}
