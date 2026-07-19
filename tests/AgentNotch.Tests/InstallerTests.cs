using System.Text.Json.Nodes;
using AgentNotch.App.Integration;
using Xunit;

namespace AgentNotch.Tests;

public sealed class InstallerTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "AgentNotch.Tests", Guid.NewGuid().ToString("N"));

    private const string Helper = @"C:\Tools\AgentNotch\helper\agentnotch-hook.exe";

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public InstallerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // Foreign content that must survive install/uninstall byte-for-byte (JSON-wise).
    private const string ForeignSettings = """
        {
          "model": "opus",
          "env": { "FOO": "bar" },
          "hooks": {
            "PreToolUse": [
              { "matcher": "Bash", "hooks": [ { "type": "command", "command": "C:\\guard\\check.exe", "timeout": 5 } ] }
            ],
            "Notification": [
              { "hooks": [ { "type": "command", "command": "C:\\bin\\toast.exe" } ] }
            ],
            "SessionStart": [
              { "hooks": [ { "type": "command", "command": "warmup.cmd" } ] }
            ]
          },
          "permissions": { "allow": [ "Bash(git status:*)" ] }
        }
        """;

    [Fact]
    public void Install_FreshFile_WritesAllFiveEntries()
    {
        // Directory does not exist yet — installer must create it.
        var path = Path.Combine(_dir, "missing", ".claude", "settings.json");

        ClaudeCodeInstaller.Install(Helper, path);

        Assert.True(File.Exists(path));
        var hooks = JsonNode.Parse(File.ReadAllText(path))!.AsObject()["hooks"]!.AsObject();

        AssertOurEntry(hooks, "PermissionRequest", "*", "permission-request", 600);
        AssertOurEntry(hooks, "PreToolUse", "AskUserQuestion", "ask-user-question", 600);
        AssertOurEntry(hooks, "Notification", null, "notify", 10);
        AssertOurEntry(hooks, "Stop", null, "stop", 10);
        AssertOurEntry(hooks, "UserPromptSubmit", null, "prompt", 10);

        Assert.Equal(InstallState.Installed, ClaudeCodeInstaller.GetState(path));

        // No pre-existing file → no backup.
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*agentnotch-backup*"));
    }

    [Fact]
    public void Install_Twice_IsIdempotent_NoDuplicates()
    {
        ClaudeCodeInstaller.Install(Helper, SettingsPath);
        var first = File.ReadAllText(SettingsPath);

        ClaudeCodeInstaller.Install(Helper, SettingsPath);
        var second = File.ReadAllText(SettingsPath);

        Assert.Equal(first, second);

        var hooks = JsonNode.Parse(second)!.AsObject()["hooks"]!.AsObject();
        foreach (var evt in new[] { "PermissionRequest", "PreToolUse", "Notification", "Stop", "UserPromptSubmit" })
            Assert.Equal(1, CountOurCommands(hooks, evt));

        // Fresh install had no file to back up; the no-op second install writes nothing.
        Assert.Empty(Directory.GetFiles(_dir, "*agentnotch-backup*"));
    }

    [Fact]
    public void Install_HelperMoved_UpdatesEntryInPlace()
    {
        ClaudeCodeInstaller.Install(Helper, SettingsPath);

        const string moved = @"D:\new location\AGENTNOTCH-HOOK.EXE"; // case-insensitive marker match
        ClaudeCodeInstaller.Install(moved, SettingsPath);

        var hooks = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject()["hooks"]!.AsObject();
        foreach (var evt in new[] { "PermissionRequest", "PreToolUse", "Notification", "Stop", "UserPromptSubmit" })
        {
            Assert.Equal(1, CountOurCommands(hooks, evt));
            Assert.Equal(moved, (string?)OurCommands(hooks, evt).Single()["command"]);
        }
    }

    [Fact]
    public void Install_PreservesForeignHooksAndUnknownProperties()
    {
        File.WriteAllText(SettingsPath, ForeignSettings);

        ClaudeCodeInstaller.Install(Helper, SettingsPath);

        var root = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        var original = JsonNode.Parse(ForeignSettings)!.AsObject();

        // Unknown top-level properties untouched.
        Assert.True(JsonNode.DeepEquals(original["model"], root["model"]));
        Assert.True(JsonNode.DeepEquals(original["env"], root["env"]));
        Assert.True(JsonNode.DeepEquals(original["permissions"], root["permissions"]));

        var hooks = root["hooks"]!.AsObject();

        // Foreign PreToolUse matcher-group survives verbatim, ahead of ours.
        var preToolUse = hooks["PreToolUse"]!.AsArray();
        Assert.Equal(2, preToolUse.Count);
        Assert.True(JsonNode.DeepEquals(original["hooks"]!["PreToolUse"]![0], preToolUse[0]));

        // Foreign Notification hook survives.
        var notification = hooks["Notification"]!.AsArray();
        Assert.Equal(2, notification.Count);
        Assert.True(JsonNode.DeepEquals(original["hooks"]!["Notification"]![0], notification[0]));

        // Foreign event we do not own is untouched.
        Assert.True(JsonNode.DeepEquals(original["hooks"]!["SessionStart"], hooks["SessionStart"]));

        Assert.Equal(InstallState.Installed, ClaudeCodeInstaller.GetState(SettingsPath));
    }

    [Fact]
    public void Uninstall_RestoresForeignOnlyContent()
    {
        File.WriteAllText(SettingsPath, ForeignSettings);

        ClaudeCodeInstaller.Install(Helper, SettingsPath);
        ClaudeCodeInstaller.Uninstall(SettingsPath);

        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(ForeignSettings),
            JsonNode.Parse(File.ReadAllText(SettingsPath))));
        Assert.Equal(InstallState.NotInstalled, ClaudeCodeInstaller.GetState(SettingsPath));
    }

    [Fact]
    public void Uninstall_SharedGroup_RemovesOnlyOurCommand()
    {
        const string seeded = """
            {
              "hooks": {
                "Stop": [
                  { "hooks": [
                      { "type": "command", "command": "C:\\bin\\other.exe" },
                      { "type": "command", "command": "C:\\x\\agentnotch-hook.exe", "args": ["stop"], "timeout": 10 }
                  ] }
                ]
              }
            }
            """;
        File.WriteAllText(SettingsPath, seeded);

        ClaudeCodeInstaller.Uninstall(SettingsPath);

        var hooks = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject()["hooks"]!.AsObject();
        var cmds = hooks["Stop"]!.AsArray()[0]!["hooks"]!.AsArray();
        Assert.Single(cmds);
        Assert.Equal(@"C:\bin\other.exe", (string?)cmds[0]!["command"]);
    }

    [Fact]
    public void Uninstall_NoOurEntries_LeavesFileUntouched()
    {
        File.WriteAllText(SettingsPath, ForeignSettings);

        ClaudeCodeInstaller.Uninstall(SettingsPath);

        Assert.Equal(ForeignSettings, File.ReadAllText(SettingsPath));
        Assert.Empty(Directory.GetFiles(_dir, "*agentnotch-backup*"));
    }

    [Fact]
    public void MalformedJson_Throws_AndDoesNotClobber()
    {
        const string garbage = "{ this is not json";
        File.WriteAllText(SettingsPath, garbage);

        Assert.Throws<InvalidOperationException>(() => ClaudeCodeInstaller.Install(Helper, SettingsPath));
        Assert.Throws<InvalidOperationException>(() => ClaudeCodeInstaller.Uninstall(SettingsPath));

        Assert.Equal(garbage, File.ReadAllText(SettingsPath));
        Assert.Empty(Directory.GetFiles(_dir, "*agentnotch-backup*"));
    }

    [Fact]
    public void Install_ExistingFile_CreatesTimestampedBackup()
    {
        File.WriteAllText(SettingsPath, ForeignSettings);

        ClaudeCodeInstaller.Install(Helper, SettingsPath);

        var backups = Directory.GetFiles(_dir, "settings.json.agentnotch-backup-*");
        Assert.Single(backups);
        Assert.Matches(@"^settings\.json\.agentnotch-backup-\d{8}-\d{6}$", Path.GetFileName(backups[0]));
        Assert.Equal(ForeignSettings, File.ReadAllText(backups[0])); // backup = pre-install content
    }

    [Fact]
    public void GetState_ReportsPartialAndNotInstalled()
    {
        Assert.Equal(InstallState.NotInstalled, ClaudeCodeInstaller.GetState(SettingsPath));

        ClaudeCodeInstaller.Install(Helper, SettingsPath);
        var root = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        root["hooks"]!.AsObject().Remove("Stop");
        File.WriteAllText(SettingsPath, root.ToJsonString());

        Assert.Equal(InstallState.Partial, ClaudeCodeInstaller.GetState(SettingsPath));
    }

    // ---------------------------------------------------------------- helpers

    private static bool IsOurs(JsonObject cmd) =>
        ((string?)cmd["command"])?.Contains("agentnotch-hook", StringComparison.OrdinalIgnoreCase) == true;

    private static IEnumerable<JsonObject> OurCommands(JsonObject hooks, string evt) =>
        hooks[evt]!.AsArray().OfType<JsonObject>()
            .SelectMany(g => g["hooks"] is JsonArray a ? a.OfType<JsonObject>() : Enumerable.Empty<JsonObject>())
            .Where(IsOurs);

    private static int CountOurCommands(JsonObject hooks, string evt) => OurCommands(hooks, evt).Count();

    private static void AssertOurEntry(JsonObject hooks, string evt, string? matcher, string mode, int timeout)
    {
        var ourGroups = hooks[evt]!.AsArray().OfType<JsonObject>()
            .Where(g => g["hooks"] is JsonArray a && a.OfType<JsonObject>().Any(IsOurs))
            .ToList();
        Assert.Single(ourGroups);
        var group = ourGroups[0];

        if (matcher is null)
            Assert.False(group.ContainsKey("matcher"));
        else
            Assert.Equal(matcher, (string?)group["matcher"]);

        var cmds = group["hooks"]!.AsArray().OfType<JsonObject>().Where(IsOurs).ToList();
        Assert.Single(cmds);
        var cmd = cmds[0];
        Assert.Equal("command", (string?)cmd["type"]);
        Assert.Equal(Helper, (string?)cmd["command"]);
        var args = cmd["args"]!.AsArray();
        Assert.Single(args);
        Assert.Equal(mode, (string?)args[0]);
        Assert.Equal(timeout, (int?)cmd["timeout"]);
    }
}
