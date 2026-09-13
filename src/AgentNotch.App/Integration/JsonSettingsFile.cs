using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentNotch.App.Diagnostics;

namespace AgentNotch.App.Integration;

/// <summary>
/// Read/write discipline shared by every settings file AgentNotch edits but does
/// not own (Claude Code's <c>settings.json</c>, Cursor's <c>hooks.json</c>):
///  * parse with JsonNode so unknown properties round-trip untouched,
///  * refuse outright to rewrite a file we cannot faithfully parse,
///  * back up before the first write, then write atomically.
/// Kept in one place so a second agent's installer cannot quietly acquire weaker
/// guarantees than the first one's.
/// </summary>
internal static class JsonSettingsFile
{
    public static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Missing/empty file → fresh object; malformed → InvalidOperationException.</summary>
    public static JsonObject LoadRoot(string path)
    {
        if (!File.Exists(path))
            return new JsonObject();

        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
            return new JsonObject();

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"'{path}' contains malformed JSON. Fix or remove the file manually — AgentNotch will not overwrite it.", ex);
        }

        if (node is not JsonObject obj)
            throw new InvalidOperationException(
                $"'{path}' is not a JSON object — refusing to rewrite it.");
        return obj;
    }

    /// <summary>Backs up the existing file, then writes atomically via a temp file.</summary>
    public static void Save(string path, string json)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        if (File.Exists(path))
        {
            var backup = path + $".agentnotch-backup-{DateTime.Now:yyyyMMdd-HHmmss}";
            var candidate = backup;
            for (int i = 1; File.Exists(candidate); i++)
                candidate = backup + "-" + i; // same-second collision (e.g. install then uninstall)
            File.Copy(path, candidate);
            AppLog.Write($"installer: backed up settings to {candidate}");
        }

        var tmp = path + ".agentnotch-tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(path))
            File.Replace(tmp, path, destinationBackupFileName: null);
        else
            File.Move(tmp, path);
    }

    /// <summary>The string value of a node, or null when it is absent or not a string.</summary>
    public static string? GetString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
