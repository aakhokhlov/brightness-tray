using Microsoft.UI.Dispatching;

namespace BrightnessTray.Services;

/// <summary>
/// Owns the monitor list and the current brightness value, applies changes
/// to every controllable display, persists them, and restores saved values
/// at startup. All DDC/WMI work runs on background threads; events are
/// raised on the UI thread.
/// </summary>
internal sealed class BrightnessController : IDisposable
{
    /// <summary>Brightness change per wheel notch.</summary>
    private const int WheelStep = 5;

    private readonly DispatcherQueue _dispatcher;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private List<MonitorItem> _monitors = new();
    private int _currentPercent;
    private bool _hasTargets;

    /// <summary>Raised on the UI thread whenever the current brightness value changes.</summary>
    public event Action<int>? PercentChanged;

    /// <summary>Raised on the UI thread after the monitor list changed.</summary>
    public event Action? MonitorsChanged;

    public int CurrentPercent => _currentPercent;

    public bool HasTargets => _hasTargets;

    /// <summary>Snapshot of the last enumerated displays (may be empty).</summary>
    public IReadOnlyList<MonitorItem> Monitors
    {
        get { lock (_gate) return _monitors; }
    }

    public BrightnessController(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>Starts background initialization: refresh + optional restore.</summary>
    public void Initialize()
    {
        _ = RefreshAsync();

        if (Settings.Current.RestoreOnStartup && Settings.Current.Brightness.Count > 0)
        {
            _ = Task.Run(() => RestoreSavedBrightnessAsync(_cts.Token));
        }
    }

    public void ScheduleRefresh() => _ = RefreshAsync();

    /// <summary>Re-enumerates displays and refreshes the current value.</summary>
    public async Task RefreshAsync()
    {
        List<MonitorItem> fresh = await Task.Run(() =>
        {
            lock (_gate)
            {
                var list = MonitorService.Enumerate();
                foreach (var old in _monitors) old.Close();
                _monitors = list;
                _hasTargets = list.Any(m => m.SupportsBrightness);
                return list;
            }
        }).ConfigureAwait(false);

        int? current = await Task.Run(() => ReadCurrentPercent(fresh)).ConfigureAwait(false);
        int percent = _currentPercent;
        if (current is int value)
        {
            lock (_gate) _currentPercent = value;
            percent = value;
        }

        _dispatcher.TryEnqueue(() =>
        {
            MonitorsChanged?.Invoke();
            if (current is not null)
            {
                PercentChanged?.Invoke(percent);
            }
        });
    }

    /// <summary>Applies a brightness value to every controllable display and persists it.</summary>
    public Task ApplyAsync(int percent) => ApplyCoreAsync(percent, persist: true);

    /// <summary>Wheel support: steps the current value by <paramref name="notches"/> * 5.</summary>
    public Task StepAsync(int notches) => ApplyAsync(_currentPercent + notches * WheelStep);

    private async Task ApplyCoreAsync(int percent, bool persist)
    {
        percent = Math.Clamp(percent, 0, 100);

        (bool AnyOk, bool AnyFailed) results = await Task.Run(() =>
        {
            lock (_gate)
            {
                bool anyOk = false;
                bool anyFailed = false;
                foreach (var target in _monitors.Where(m => m.SupportsBrightness))
                {
                    if (target.SetPercent(percent))
                    {
                        anyOk = true;
                        if (persist)
                        {
                            Settings.Current.Brightness[target.Key] = percent;
                        }
                    }
                    else
                    {
                        anyFailed = true;
                    }
                }
                return (anyOk, anyFailed);
            }
        }).ConfigureAwait(false);

        if (persist && results.AnyOk)
        {
            Settings.Save();
        }

        if (results.AnyOk)
        {
            lock (_gate) _currentPercent = percent;
            RaisePercentChanged(percent);
        }

        if (results.AnyFailed)
        {
            // Something refused the value (e.g. a display was unplugged mid-drag):
            // resync state from hardware instead of keeping a lie.
            int? actual = await Task.Run(() => ReadCurrentPercent(Monitors)).ConfigureAwait(false);
            if (actual is int value)
            {
                lock (_gate) _currentPercent = value;
                RaisePercentChanged(value);
            }
        }
    }

    /// <summary>
    /// Ported from the legacy implementation: after boot displays may need some
    /// time before they accept DDC/CI commands, so retry for a while.
    /// </summary>
    private async Task RestoreSavedBrightnessAsync(CancellationToken token)
    {
        try
        {
            for (int attempt = 0; attempt < 20 && !token.IsCancellationRequested; attempt++)
            {
                await Task.Delay(attempt == 0 ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(3), token)
                    .ConfigureAwait(false);

                var list = MonitorService.Enumerate();
                try
                {
                    var byKey = new Dictionary<string, MonitorItem>();
                    foreach (var monitor in list)
                    {
                        if (monitor.SupportsBrightness && !byKey.ContainsKey(monitor.Key))
                        {
                            byKey[monitor.Key] = monitor;
                        }
                    }

                    int matched = 0;
                    bool allOk = true;
                    foreach (var (key, value) in Settings.Current.Brightness)
                    {
                        if (byKey.TryGetValue(key, out var monitor))
                        {
                            matched++;
                            if (!monitor.SetPercent(value))
                            {
                                allOk = false;
                            }
                        }
                    }

                    if (matched > 0 && allOk)
                    {
                        int? current = ReadCurrentPercent(list);
                        if (current is int restored)
                        {
                            lock (_gate) _currentPercent = restored;
                            RaisePercentChanged(restored);
                        }
                        break;
                    }
                }
                finally
                {
                    foreach (var monitor in list) monitor.Close();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // App is exiting.
        }
    }

    private static int? ReadCurrentPercent(IReadOnlyList<MonitorItem> monitors)
    {
        foreach (var monitor in monitors)
        {
            if (!monitor.SupportsBrightness) continue;

            int? percent = monitor.GetPercent();
            if (percent is not null) return percent;
        }
        return null;
    }

    private void RaisePercentChanged(int percent)
    {
        _dispatcher.TryEnqueue(() => PercentChanged?.Invoke(percent));
    }

    public void Dispose()
    {
        _cts.Cancel();
        lock (_gate)
        {
            foreach (var monitor in _monitors) monitor.Close();
            _monitors = new List<MonitorItem>();
        }
        _cts.Dispose();
    }
}
