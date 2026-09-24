using System.IO;
using System.Text.Json;
using AgentNotch.App.Diagnostics;

namespace AgentNotch.App.Integration;

/// <summary>
/// User settings persisted to %APPDATA%\AgentNotch\config.json. Everything is
/// fail-soft: a missing or corrupt file just yields defaults, and a failed save
/// is logged and swallowed — settings must never take the overlay down.
/// ("Launch at login" lives in the registry, not here — see <see cref="StartupManager"/>.)
/// </summary>
public sealed class AppConfig
{
    /// <summary>Show the 5h/7d plan-limit row at the top of the panel.</summary>
    public bool ShowUsage { get; set; } = true;

    /// <summary>Where the notch sits along the top edge: "left" | "center" | "right".</summary>
    public string Position { get; set; } = "center";

    /// <summary>Which palette to paint with: "island" | "glass" | "atari" | "atari-dark" | "console".</summary>
    public string Theme { get; set; } = UI.ThemeManager.Island;

    /// <summary>
    /// The Atari dark-mode switch. Kept separately from <see cref="Theme"/> so it
    /// survives a trip through another skin: picking Atari again returns to the
    /// variant you left.
    /// </summary>
    public bool AtariDark { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AgentNotch", "config.json");

    public static AppConfig Load()
    {
        try
        {
            var path = FilePath;
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path)) ?? new AppConfig();
        }
        catch (Exception ex) { AppLog.Write("config load: " + ex.Message); }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { AppLog.Write("config save: " + ex.Message); }
    }
}
