using Microsoft.Win32;

namespace BrightnessTray.Services;

/// <summary>Synchronizes the HKCU Run entry with the Autostart setting.</summary>
internal static class Autostart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "BrightnessTray";

    /// <summary>
    /// Creates the Run value when enabled (recreating it if it was deleted)
    /// or removes it when disabled.
    /// </summary>
    public static void Sync(bool enabled)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null) return;

            if (enabled)
            {
                string? exePath = Environment.ProcessPath;
                if (exePath is not null)
                {
                    key.SetValue(AppName, $"\"{exePath}\"");
                }
            }
            else
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Registry unavailable: the app still works, it just won't autostart.
        }
    }
}
