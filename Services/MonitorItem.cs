using System.Management;
using BrightnessTray.Interop;

namespace BrightnessTray.Services;

/// <summary>
/// One controllable display (external monitor via DDC/CI or built-in panel
/// via WMI). All values are 0-100 percent.
/// </summary>
internal abstract class MonitorItem
{
    /// <summary>Stable key used to persist the brightness value.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>Friendly display name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>False when the display was detected but cannot be controlled.</summary>
    public bool SupportsBrightness { get; init; }

    /// <summary>Current brightness in percent, or null when it cannot be read.</summary>
    public abstract int? GetPercent();

    /// <summary>Sets the brightness in percent; true on success.</summary>
    public abstract bool SetPercent(int percent);

    /// <summary>Releases native resources (DDC handles).</summary>
    public virtual void Close() { }

    protected static int ClampPercent(int percent) => Math.Clamp(percent, 0, 100);

    /// <summary>Maps a 0-100 percent value into the display's raw DDC range.</summary>
    internal static int PercentToRaw(int percent, uint min, uint max)
    {
        int lo = (int)min, hi = (int)max;
        int raw = (int)Math.Round(lo + (hi - lo) * ClampPercent(percent) / 100.0);
        return Math.Clamp(raw, lo, hi);
    }

    /// <summary>Maps a raw DDC value back to 0-100 percent; null for an empty range.</summary>
    internal static int? RawToPercent(uint cur, uint min, uint max)
    {
        if (max <= min) return null;
        return ClampPercent((int)Math.Round((cur - min) * 100.0 / (max - min)));
    }
}

/// <summary>External monitor controlled through DDC/CI (dxva2).</summary>
internal sealed class DdcMonitorItem : MonitorItem
{
    private IntPtr _handle;
    private uint _min;
    private uint _max;
    private bool _closed;

    public DdcMonitorItem(IntPtr handle, string key, string name, bool supports, uint min, uint max)
    {
        _handle = handle;
        _min = min;
        _max = max;
        Key = key;
        Name = name;
        SupportsBrightness = supports;
    }

    public override int? GetPercent()
    {
        if (!SupportsBrightness || _closed) return null;

        uint min = 0, cur = 0, max = 0;
        if (!NativeMethods.GetMonitorBrightness(_handle, ref min, ref cur, ref max)) return null;

        int? percent = RawToPercent(cur, min, max);
        if (percent is not null)
        {
            _min = min;
            _max = max;
        }
        return percent;
    }

    public override bool SetPercent(int percent)
    {
        if (!SupportsBrightness || _closed || _max <= _min) return false;
        return NativeMethods.SetMonitorBrightness(_handle, (uint)PercentToRaw(percent, _min, _max));
    }

    public override void Close()
    {
        if (!_closed && _handle != IntPtr.Zero)
        {
            _ = NativeMethods.DestroyPhysicalMonitor(_handle);
        }
        _closed = true;
        _handle = IntPtr.Zero;
    }
}

/// <summary>Built-in panel controlled through WMI (root\wmi).</summary>
internal sealed class WmiMonitorItem : MonitorItem
{
    private readonly string _instanceName;

    public WmiMonitorItem(string instanceName)
    {
        _instanceName = instanceName;
        Key = instanceName;
        Name = "Built-in display";
        SupportsBrightness = true;
    }

    public override int? GetPercent()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi",
                $"SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE InstanceName = '{Escape(_instanceName)}'");
            using var results = searcher.Get();
            foreach (var obj in results)
            {
                using (obj)
                {
                    if (obj["CurrentBrightness"] is byte current) return current;
                }
            }
        }
        catch
        {
            // WMI brightness unavailable (e.g. driver does not expose it).
        }
        return null;
    }

    public override bool SetPercent(int percent)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi",
                $"SELECT * FROM WmiMonitorBrightnessMethods WHERE InstanceName = '{Escape(_instanceName)}'");
            using var results = searcher.Get();
            foreach (var obj in results)
            {
                if (obj is ManagementObject method)
                {
                    using (method)
                    {
                        method.InvokeMethod("SetBrightness", new object[] { 100u, (byte)ClampPercent(percent) });
                        return true;
                    }
                }
            }
        }
        catch
        {
            // WMI brightness unavailable.
        }
        return false;
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");
}
