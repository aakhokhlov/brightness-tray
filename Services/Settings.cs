using System.IO;
using System.Text.Json;

namespace BrightnessTray.Services;

/// <summary>User-visible settings, persisted as JSON.</summary>
internal sealed class AppSettings
{
    public bool Autostart { get; set; } = true;

    /// <summary>Restore the last known brightness to all displays at app start.</summary>
    public bool RestoreOnStartup { get; set; } = true;

    /// <summary>Last applied brightness per display key. Never cleared automatically.</summary>
    public Dictionary<string, int> Brightness { get; set; } = new();
}

internal static class Settings
{
    public static AppSettings Current { get; private set; } = new();

    internal static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BrightnessTray");

    private static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    private static readonly object SaveGate = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;

            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options);
            if (loaded is not null)
            {
                Current = loaded;
            }
        }
        catch
        {
            // Unreadable or corrupt settings: keep defaults, keep the file.
        }
    }

    public static void Save()
    {
        try
        {
            lock (SaveGate)
            {
                Directory.CreateDirectory(DirectoryPath);
                // Write-then-move so a crash mid-write (e.g. Exit during a
                // background save) can't leave a truncated settings file.
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Options));
                File.Move(tmp, FilePath, overwrite: true);
            }
        }
        catch
        {
            // Persisting settings is never worth crashing the app for.
        }
    }
}
