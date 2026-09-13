using System.Text.Json;
using System.Text.Json.Nodes;
using AgentNotch.App.Diagnostics;
using AgentNotch.Protocol;

namespace AgentNotch.App.Integration;

public enum InstallState { NotInstalled, Installed, Partial }

/// <summary>
/// Installs/removes the AgentNotch hook entries in Claude Code's settings.json.
///
/// Design rules:
///  * The file belongs to the user and to Claude Code — we merge with
///    System.Text.Json.Nodes and PRESERVE every unknown property and every
///    hook we did not add. Nothing foreign is reordered or rewritten.
///  * OUR entries are recognized purely by the command path containing
///    "agentnotch-hook" (case-insensitive).
///  * Install is idempotent: an existing entry is updated in place (helper
///    moved), duplicates are collapsed, and an unchanged file is not rewritten.
///  * Uninstall is surgical: it removes only our command entries and prunes
///    only the matcher-groups/arrays that removal emptied.
///  * A timestamped backup is taken before the first write, and the write
///    itself is atomic (temp file + File.Replace/Move).
///  * Malformed existing JSON throws InvalidOperationException — we never
///    clobber a file we cannot faithfully round-trip.
/// </summary>
public static class ClaudeCodeInstaller
{
    /// <summary>Marker that identifies our hook command entries (case-insensitive).</summary>
    private const string CommandMarker = "agentnotch-hook";

    private static readonly JsonSerializerOptions WriteOptions = JsonSettingsFile.WriteOptions;

    /// <summary>The five hook registrations we own: (event, matcher — null = no matcher property, helper mode arg, timeout seconds).</summary>
    private static readonly (string Event, string? Matcher, string Mode, int Timeout)[] HookSpecs =
    {
        ("PermissionRequest", "*", Proto.ModePermissionRequest, 600),
        ("PreToolUse", "AskUserQuestion", Proto.ModeAskUserQuestion, 600),
        ("Notification", null, Proto.ModeNotify, 10),
        ("Stop", null, Proto.ModeStop, 10),
        ("UserPromptSubmit", null, Proto.ModePrompt, 10),
    };

    public static InstallState GetState(string? settingsPath = null)
    {
        var path = ResolvePath(settingsPath);
        try
        {
            if (!File.Exists(path))
                return InstallState.NotInstalled;

            var root = LoadRoot(path);
            if (root["hooks"] is not JsonObject hooks)
                return InstallState.NotInstalled;

            int found = 0;
            foreach (var (evt, _, _, _) in HookSpecs)
            {
                if (hooks[evt] is JsonArray groups && groups.OfType<JsonObject>().Any(g =>
                        g["hooks"] is JsonArray cmds && cmds.OfType<JsonObject>().Any(IsOurs)))
                    found++;
            }

            return found == 0 ? InstallState.NotInstalled
                 : found == HookSpecs.Length ? InstallState.Installed
                 : InstallState.Partial;
        }
        catch (Exception ex)
        {
            // GetState is informational (tray menu label) — never throw.
            AppLog.Write("installer: GetState failed: " + ex.Message);
            return InstallState.NotInstalled;
        }
    }

    public static void Install(string helperExePath, string? settingsPath = null)
    {
        if (string.IsNullOrWhiteSpace(helperExePath))
            throw new ArgumentException("Helper executable path is required.", nameof(helperExePath));

        var path = ResolvePath(settingsPath);
        var root = LoadRoot(path);
        var before = root.ToJsonString(WriteOptions);

        if (root["hooks"] is null)
            root["hooks"] = new JsonObject();
        if (root["hooks"] is not JsonObject hooks)
            throw new InvalidOperationException(
                $"'{path}': top-level \"hooks\" is not a JSON object — refusing to rewrite it.");

        foreach (var (evt, matcher, mode, timeout) in HookSpecs)
            EnsureEntry(hooks, path, evt, matcher, mode, timeout, helperExePath);

        var after = root.ToJsonString(WriteOptions);
        if (File.Exists(path) && after == before)
        {
            AppLog.Write($"installer: {path} already up to date — nothing written");
            return;
        }

        Save(path, after);
        AppLog.Write($"installer: hooks installed into {path} (helper: {helperExePath})");
    }

    public static void Uninstall(string? settingsPath = null)
    {
        var path = ResolvePath(settingsPath);
        if (!File.Exists(path))
        {
            AppLog.Write($"installer: uninstall — {path} does not exist, nothing to do");
            return;
        }

        var root = LoadRoot(path);
        var before = root.ToJsonString(WriteOptions);

        if (root["hooks"] is JsonObject hooks)
        {
            bool removedEvents = false;

            // Scan EVERY event array (not just our five) so stale entries from
            // older versions are cleaned up too.
            foreach (var eventName in hooks.Select(p => p.Key).ToList())
            {
                if (hooks[eventName] is not JsonArray groups)
                    continue;

                bool removedFromEvent = false;
                for (int g = groups.Count - 1; g >= 0; g--)
                {
                    if (groups[g] is not JsonObject group || group["hooks"] is not JsonArray cmds)
                        continue;

                    bool removedFromGroup = false;
                    for (int c = cmds.Count - 1; c >= 0; c--)
                    {
                        if (cmds[c] is JsonObject cmd && IsOurs(cmd))
                        {
                            cmds.RemoveAt(c);
                            removedFromGroup = true;
                        }
                    }

                    // Prune only matcher-groups WE emptied (and only when they
                    // carry no foreign properties besides matcher/hooks).
                    if (removedFromGroup && cmds.Count == 0 && IsGroupPrunable(group))
                        groups.RemoveAt(g);

                    removedFromEvent |= removedFromGroup;
                }

                // Prune the event array only if our removals emptied it.
                if (removedFromEvent && groups.Count == 0)
                {
                    hooks.Remove(eventName);
                    removedEvents = true;
                }
            }

            // If removing our events left "hooks" empty, everything in it was
            // ours (foreign events — even empty ones — are never removed above).
            if (removedEvents && hooks.Count == 0)
                root.Remove("hooks");
        }

        var after = root.ToJsonString(WriteOptions);
        if (after == before)
        {
            AppLog.Write($"installer: uninstall — no AgentNotch entries in {path}, nothing written");
            return;
        }

        Save(path, after);
        AppLog.Write($"installer: hooks removed from {path}");
    }

    // ---------------------------------------------------------------- helpers

    private static string ResolvePath(string? settingsPath) =>
        settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "settings.json");

    private static JsonObject LoadRoot(string path) => JsonSettingsFile.LoadRoot(path);

    /// <summary>Idempotently merges one of our hook registrations into hooks[evt].</summary>
    private static void EnsureEntry(
        JsonObject hooks, string path, string evt, string? matcher, string mode, int timeout, string helperExePath)
    {
        if (hooks[evt] is null)
            hooks[evt] = new JsonArray();
        if (hooks[evt] is not JsonArray groups)
            throw new InvalidOperationException(
                $"'{path}': \"hooks.{evt}\" is not a JSON array — refusing to rewrite it.");

        // Pass 1: the first of our commands already sitting in a group with the
        // desired matcher is the one we keep (updated in place below).
        JsonObject? keep = null;
        foreach (var groupNode in groups)
        {
            if (groupNode is not JsonObject group || group["hooks"] is not JsonArray cmds)
                continue;
            if (!MatcherMatches(group, matcher))
                continue;
            foreach (var cmdNode in cmds)
            {
                if (cmdNode is JsonObject cmd && IsOurs(cmd))
                {
                    keep = cmd;
                    break;
                }
            }
            if (keep is not null)
                break;
        }

        // Pass 2: drop every OTHER copy of our command (duplicates, or entries
        // stranded under a wrong matcher), pruning matcher-groups this emptied.
        for (int g = groups.Count - 1; g >= 0; g--)
        {
            if (groups[g] is not JsonObject group || group["hooks"] is not JsonArray cmds)
                continue;

            bool removed = false;
            for (int c = cmds.Count - 1; c >= 0; c--)
            {
                if (cmds[c] is JsonObject cmd && IsOurs(cmd) && !ReferenceEquals(cmd, keep))
                {
                    cmds.RemoveAt(c);
                    removed = true;
                }
            }
            if (removed && cmds.Count == 0 && IsGroupPrunable(group))
                groups.RemoveAt(g);
        }

        if (keep is not null)
        {
            // Update in place — JsonObject keeps property positions, so an
            // unchanged entry round-trips to identical JSON (no-op install).
            keep["type"] = "command";
            keep["command"] = helperExePath;
            keep["args"] = new JsonArray(JsonValue.Create(mode));
            keep["timeout"] = timeout;
        }
        else
        {
            var group = new JsonObject();
            if (matcher is not null)
                group["matcher"] = matcher;
            group["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = helperExePath,
                ["args"] = new JsonArray(JsonValue.Create(mode)),
                ["timeout"] = timeout,
            });
            groups.Add(group);
        }
    }

    private static bool IsOurs(JsonObject cmd) =>
        GetString(cmd["command"]) is { } command
        && command.Contains(CommandMarker, StringComparison.OrdinalIgnoreCase);

    private static bool MatcherMatches(JsonObject group, string? desired)
    {
        var node = group["matcher"];
        if (desired is null)
            return node is null; // absent (or explicit null) — both mean "no matcher"
        return GetString(node) is { } value && string.Equals(value, desired, StringComparison.Ordinal);
    }

    /// <summary>A group is prunable when it carries nothing foreign besides matcher/hooks.</summary>
    private static bool IsGroupPrunable(JsonObject group) =>
        group.All(p => p.Key is "matcher" or "hooks");

    private static string? GetString(JsonNode? node) => JsonSettingsFile.GetString(node);

    private static void Save(string path, string json) => JsonSettingsFile.Save(path, json);
}
