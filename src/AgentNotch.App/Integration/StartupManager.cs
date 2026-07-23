using Microsoft.Win32;
using AgentNotch.App.Diagnostics;

namespace AgentNotch.App.Integration;

/// <summary>
/// Toggles "launch AgentNotch at Windows login" through the per-user Run key
/// (HKCU\...\CurrentVersion\Run). Per-user needs no elevation, and pointing the
/// value at the current executable keeps it correct across in-place updates.
/// Every call is fail-soft: a settings toggle must never take the overlay down.
/// </summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AgentNotch";

    /// <summary>The running AgentNotch.exe (host apphost), or null if unknown.</summary>
    private static string? ExePath => Environment.ProcessPath;

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch (Exception ex)
        {
            AppLog.Write("startup read: " + ex.Message);
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return;
            if (enabled)
            {
                var exe = ExePath;
                if (string.IsNullOrEmpty(exe)) return;
                key.SetValue(ValueName, "\"" + exe + "\""); // quote — the path has spaces
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("startup write: " + ex.Message);
        }
    }
}
