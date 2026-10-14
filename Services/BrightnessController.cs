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
    private bool _hasCurrent;         // false until a real value was read or applied
    private bool _hasTargets;
    private bool _applying;           // one apply chain at a time; extras coalesce
    private int? _pendingPercent;     // latest value requested while an apply runs
    private bool _restoreSuppressed;  // user changed brightness; stop the startup restore

    /// <summary>Raised on the UI thread whenever the current brightness value changes.</summary>
    public event Action<int>? PercentChanged;

    /// <summary>Raised on the UI thread after the monitor list changed.</summary>
    public event Action? MonitorsChanged;

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
        int? current = await Task.Run(() =>
        {
            // Enumeration is slow (WMI + DDC); run it outside the gate so
            // pending applies don't queue behind it. The read uses the fresh
            // handles before they are published, so no concurrent close can
            // reach them.
            var list = MonitorService.Enumerate();
            int? result = ReadCurrentPercent(list);
            lock (_gate)
            {
                foreach (var old in _monitors) old.Close();
                _monitors = list;
                _hasTargets = list.Any(m => m.SupportsBrightness);
                if (result is int value)
                {
                    _currentPercent = value;
                    _hasCurrent = true;
                }
            }
            return result;
        }).ConfigureAwait(false);

        _dispatcher.TryEnqueue(() =>
        {
            MonitorsChanged?.Invoke();
            if (current is int percent)
            {
                PercentChanged?.Invoke(percent);
            }
        });
    }

    /// <summary>Applies a brightness value to every controllable display and persists it.</summary>
    public Task ApplyAsync(int percent)
    {
        _restoreSuppressed = true; // user intent wins over the startup restore

        lock (_gate)
        {
            if (_applying)
            {
                // An apply chain is running (slow DDC writes): keep only the
                // latest value; it is applied when the running chain drains.
                _pendingPercent = Math.Clamp(percent, 0, 100);
                return Task.CompletedTask;
            }
            _applying = true;
        }
        return ApplyCoreAsync(percent);
    }

    /// <summary>Wheel support: steps the current value by <paramref name="notches"/> * 5.</summary>
    public Task StepAsync(int notches)
    {
        lock (_gate)
        {
            // Ignore wheel input until a real value was read or applied,
            // otherwise stepping up from the 0 placeholder dims the displays.
            if (!_hasCurrent) return Task.CompletedTask;
            return ApplyAsync(_currentPercent + notches * WheelStep);
        }
    }

    private async Task ApplyCoreAsync(int percent)
    {
        try
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
                            Settings.Current.Brightness[target.Key] = percent;
                        }
                        else
                        {
                            anyFailed = true;
                        }
                    }
                    return (anyOk, anyFailed);
                }
            }).ConfigureAwait(false);

            if (results.AnyOk)
            {
                Settings.Save();
                lock (_gate)
                {
                    _currentPercent = percent;
                    _hasCurrent = true;
                }
                RaisePercentChanged(percent);
            }

            if (results.AnyFailed)
            {
                // Something refused the value (e.g. a display was unplugged
                // mid-drag): resync state from hardware instead of keeping a
                // lie. Under the gate so a concurrent refresh cannot close
                // the handles we are reading from.
                int? actual;
                lock (_gate) actual = ReadCurrentPercent(_monitors);
                if (actual is int value)
                {
                    lock (_gate) _currentPercent = value;
                    RaisePercentChanged(value);
                }
            }
        }
        finally
        {
            int? next;
            lock (_gate)
            {
                _applying = false;
                next = _pendingPercent;
                _pendingPercent = null;
            }
            if (next is int queued) _ = ApplyAsync(queued);
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
                if (_restoreSuppressed) return; // the user already changed brightness

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

                    // Snapshot under the gate: the apply chain may be writing
                    // the dictionary on another thread right now.
                    var saved = new List<KeyValuePair<string, int>>();
                    lock (_gate) saved.AddRange(Settings.Current.Brightness);

                    int matched = 0;
                    bool allOk = true;
                    foreach (var (key, value) in saved)
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
                            lock (_gate)
                            {
                                _currentPercent = restored;
                                _hasCurrent = true;
                            }
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
