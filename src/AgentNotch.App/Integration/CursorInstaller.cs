using System.Text.Json.Nodes;
using AgentNotch.App.Diagnostics;
using AgentNotch.Protocol;

namespace AgentNotch.App.Integration;

/// <summary>
/// Installs/removes the AgentNotch hook entries in Cursor's <c>~\.cursor\hooks.json</c>.
///
/// Cursor's hook file is a flatter dialect than Claude Code's settings.json —
/// <c>hooks[event]</c> is an array of command entries directly, with no matcher
/// groups, and <c>command</c> is a SHELL STRING (exe plus arguments) rather than a
/// path with a separate args array. Everything else follows the same rules as
/// <see cref="ClaudeCodeInstaller"/>:
///  * merge with JsonNode, preserving every foreign hook and unknown property —
///    Cursor users routinely have other tools registered in this same file,
///  * recognise OUR entries purely by "agentnotch-hook" appearing in the command,
///  * idempotent install (update in place, collapse duplicates, no-op rewrite),
///  * surgical uninstall (only our entries; prune only what we emptied),
///  * backup + atomic write via <see cref="JsonSettingsFile"/>.
///
/// Only the two events where Cursor would otherwise raise its own permission
/// dialog are registered as blocking. Cursor's <c>preToolUse</c> fires for EVERY
/// tool call including reads — routing that through a human would stall the agent
/// on work it never needed to ask about — so it is deliberately left alone.
/// </summary>
public static class CursorInstaller
{
    /// <summary>Marker that identifies our hook command entries (case-insensitive).</summary>
    private const string CommandMarker = "agentnotch-hook";

    /// <summary>(event, helper mode arg, timeout seconds). Blocking events come first.</summary>
    private static readonly (string Event, string Mode, int Timeout)[] HookSpecs =
    {
        // Blocking: the notch answers instead of Cursor's own dialog.
        ("beforeShellExecution", Proto.ModeCursorPermission, 600),
        ("beforeMCPExecution", Proto.ModeCursorPermission, 600),
        // Observe-only freshness signals — the helper forwards and exits.
        ("beforeSubmitPrompt", Proto.ModeCursorNotify, 10),
        ("stop", Proto.ModeCursorNotify, 10),
    };

    /// <summary>Default location of Cursor's hook file.</summary>
    public static string DefaultHooksPath => Path.Combine(CursorHome, "hooks.json");

    private static string CursorHome => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor");

    /// <summary>
    /// Is Cursor present on this machine at all? True when it has a home directory
    /// (it is or was installed) or a process is running right now. Drives whether
    /// the tray offers the Cursor hook actions.
    /// </summary>
    public static bool IsCursorInstalled()
    {
        try
        {
            if (Directory.Exists(CursorHome)) return true;
            return Interop.ProcessTree.AnyRunning("Cursor", "cursor-agent");
        }
        catch (Exception ex)
        {
            AppLog.Write("cursor installer: detection failed: " + ex.Message);
            return false;
        }
    }

    public static InstallState GetState(string? hooksPath = null)
    {
        var path = hooksPath ?? DefaultHooksPath;
        try
        {
            if (!File.Exists(path))
                return InstallState.NotInstalled;

            var root = JsonSettingsFile.LoadRoot(path);
            if (root["hooks"] is not JsonObject hooks)
                return InstallState.NotInstalled;

            int found = 0;
            foreach (var (evt, _, _) in HookSpecs)
                if (hooks[evt] is JsonArray entries && entries.OfType<JsonObject>().Any(IsOurs))
                    found++;

            return found == 0 ? InstallState.NotInstalled
                 : found == HookSpecs.Length ? InstallState.Installed
                 : InstallState.Partial;
        }
        catch (Exception ex)
        {
            // GetState is informational (tray menu label) — never throw.
            AppLog.Write("cursor installer: GetState failed: " + ex.Message);
            return InstallState.NotInstalled;
        }
    }

    public static void Install(string helperExePath, string? hooksPath = null)
    {
        if (string.IsNullOrWhiteSpace(helperExePath))
            throw new ArgumentException("Helper executable path is required.", nameof(helperExePath));

        var path = hooksPath ?? DefaultHooksPath;
        var root = JsonSettingsFile.LoadRoot(path);
        var before = root.ToJsonString(JsonSettingsFile.WriteOptions);

        if (root["hooks"] is null)
            root["hooks"] = new JsonObject();
        if (root["hooks"] is not JsonObject hooks)
            throw new InvalidOperationException(
                $"'{path}': top-level \"hooks\" is not a JSON object — refusing to rewrite it.");

        // Cursor stamps a schema version; leave an existing one exactly as it is.
        if (root["version"] is null)
            root["version"] = 1;

        foreach (var (evt, mode, timeout) in HookSpecs)
            EnsureEntry(hooks, path, evt, mode, timeout, helperExePath);

        var after = root.ToJsonString(JsonSettingsFile.WriteOptions);
        if (File.Exists(path) && after == before)
        {
            AppLog.Write($"cursor installer: {path} already up to date — nothing written");
            return;
        }

        JsonSettingsFile.Save(path, after);
        AppLog.Write($"cursor installer: hooks installed into {path} (helper: {helperExePath})");
    }

    public static void Uninstall(string? hooksPath = null)
    {
        var path = hooksPath ?? DefaultHooksPath;
        if (!File.Exists(path))
        {
            AppLog.Write($"cursor installer: uninstall — {path} does not exist, nothing to do");
            return;
        }

        var root = JsonSettingsFile.LoadRoot(path);
        var before = root.ToJsonString(JsonSettingsFile.WriteOptions);

        if (root["hooks"] is JsonObject hooks)
        {
            bool removedEvents = false;

            // Scan EVERY event array, not just the four we register now, so entries
            // left by an older version are cleaned up too.
            foreach (var eventName in hooks.Select(p => p.Key).ToList())
            {
                if (hooks[eventName] is not JsonArray entries) continue;

                bool removed = false;
                for (int i = entries.Count - 1; i >= 0; i--)
                {
                    if (entries[i] is JsonObject entry && IsOurs(entry))
                    {
                        entries.RemoveAt(i);
                        removed = true;
                    }
                }

                // Prune the event array only if our removals emptied it.
                if (removed && entries.Count == 0)
                {
                    hooks.Remove(eventName);
                    removedEvents = true;
                }
            }

            // Foreign events are never removed above, so an empty "hooks" here means
            // everything in it was ours.
            if (removedEvents && hooks.Count == 0)
                root.Remove("hooks");
        }

        var after = root.ToJsonString(JsonSettingsFile.WriteOptions);
        if (after == before)
        {
            AppLog.Write($"cursor installer: uninstall — no AgentNotch entries in {path}, nothing written");
            return;
        }

        JsonSettingsFile.Save(path, after);
        AppLog.Write($"cursor installer: hooks removed from {path}");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// The shell string Cursor runs. Quoted because the install path routinely
    /// contains spaces (%LOCALAPPDATA%\Programs, "Program Files").
    /// </summary>
    internal static string CommandLine(string helperExePath, string mode) =>
        $"\"{helperExePath}\" {mode}";

    /// <summary>Idempotently merges one of our hook registrations into hooks[evt].</summary>
    private static void EnsureEntry(
        JsonObject hooks, string path, string evt, string mode, int timeout, string helperExePath)
    {
        if (hooks[evt] is null)
            hooks[evt] = new JsonArray();
        if (hooks[evt] is not JsonArray entries)
            throw new InvalidOperationException(
                $"'{path}': \"hooks.{evt}\" is not a JSON array — refusing to rewrite it.");

        // Keep the FIRST of ours — updated in place, so an unchanged entry
        // round-trips to identical JSON — and drop every duplicate behind it.
        JsonObject? keep = null;
        for (int i = 0; i < entries.Count; )
        {
            if (entries[i] is JsonObject entry && IsOurs(entry))
            {
                if (keep is null) { keep = entry; i++; }
                else entries.RemoveAt(i); // duplicate; don't advance, the list shifted
            }
            else i++;
        }

        if (keep is not null)
        {
            keep["command"] = CommandLine(helperExePath, mode);
            keep["timeout"] = timeout;
        }
        else
        {
            entries.Add(new JsonObject
            {
                ["command"] = CommandLine(helperExePath, mode),
                ["timeout"] = timeout,
            });
        }
    }

    private static bool IsOurs(JsonObject entry) =>
        JsonSettingsFile.GetString(entry["command"]) is { } command
        && command.Contains(CommandMarker, StringComparison.OrdinalIgnoreCase);
}
