using System.Management;
using BrightnessTray.Interop;

namespace BrightnessTray.Services;

/// <summary>Enumerates all displays that expose brightness control.</summary>
internal static class MonitorService
{
    /// <summary>
    /// Returns every detected display: DDC/CI-capable external monitors first
    /// (in display order), then WMI-controllable built-in panels.
    /// Handles must be released via <see cref="MonitorItem.Close"/> when no
    /// longer needed. Must be called off the UI thread (DDC/CI is slow).
    /// </summary>
    public static List<MonitorItem> Enumerate()
    {
        var items = new List<MonitorItem>();
        EnumerateDdc(items);
        EnumerateWmi(items);
        return items;
    }

    private static void EnumerateDdc(List<MonitorItem> items)
    {
        var handles = new HashSet<IntPtr>();
        var nameCounts = new Dictionary<string, int>();

        _ = NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr monitor, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data) =>
            {
                uint count = 0;
                if (NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, ref count) && count > 0)
                {
                    var monitors = new NativeMethods.PHYSICAL_MONITOR[count];
                    if (NativeMethods.GetPhysicalMonitorsFromHMONITOR(monitor, count, monitors))
                    {
                        foreach (var physical in monitors)
                        {
                            // The same handle can be reported more than once.
                            if (!handles.Add(physical.Handle)) continue;

                            uint min = 0, cur = 0, max = 0;
                            bool supports = NativeMethods.GetMonitorBrightness(
                                physical.Handle, ref min, ref cur, ref max);

                            string description = string.IsNullOrWhiteSpace(physical.Description)
                                ? $"Monitor {items.Count + 1}"
                                : physical.Description;

                            // Identical monitors share a description; make keys unique.
                            nameCounts.TryGetValue(description, out int seen);
                            seen++;
                            nameCounts[description] = seen;
                            string name = seen == 1 ? description : $"{description} #{seen}";

                            items.Add(new DdcMonitorItem(physical.Handle, name, name, supports, min, max));
                        }
                    }
                }
                return true;
            }, IntPtr.Zero);
    }

    private static void EnumerateWmi(List<MonitorItem> items)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi",
                "SELECT InstanceName FROM WmiMonitorBrightness");
            using var results = searcher.Get();
            foreach (var obj in results)
            {
                using (obj)
                {
                    if (obj["InstanceName"] is string instance && !string.IsNullOrEmpty(instance))
                    {
                        items.Add(new WmiMonitorItem(instance));
                    }
                }
            }
        }
        catch
        {
            // No WMI-brightness-capable panel (typical desktop) or WMI unavailable.
        }
    }
}
