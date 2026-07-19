namespace AgentNotch.App.Diagnostics;

/// <summary>Minimal file logger — %LOCALAPPDATA%\AgentNotch\logs\app.log. Never throws.</summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentNotch", "logs", "app.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                // Simple size cap so the log can't grow unbounded.
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 2_000_000)
                    File.Move(LogPath, LogPath + ".old", overwrite: true);
                File.AppendAllText(LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch { /* never throw from logging */ }
    }

    public static string LogDirectory => Path.GetDirectoryName(LogPath)!;
}
