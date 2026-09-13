using System.Text.Json.Nodes;
using AgentNotch.App.Integration;
using Xunit;

namespace AgentNotch.Tests;

public sealed class CursorInstallerTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "AgentNotch.Tests", Guid.NewGuid().ToString("N"));

    private const string Helper = @"C:\Program Files\AgentNotch\helper\agentnotch-hook.exe";

    private static readonly string[] OurEvents =
        { "beforeShellExecution", "beforeMCPExecution", "beforeSubmitPrompt", "stop" };

    private string HooksPath => Path.Combine(_dir, "hooks.json");

    public CursorInstallerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A real-world file: another tool already owns entries in the same events.</summary>
    private const string ForeignHooks = """
        {
          "hooks": {
            "beforeSubmitPrompt": [
              { "command": "C:\\other\\tool.exe prompt", "timeout": 10 }
            ],
            "beforeShellExecution": [
              { "command": "C:\\other\\tool.exe shell", "timeout": 10 }
            ],
            "afterFileEdit": [
              { "command": "C:\\other\\tool.exe edited" }
            ]
          },
          "version": 1
        }
        """;

    [Fact]
    public void Install_FreshFile_RegistersEveryEvent()
    {
        // Directory does not exist yet — installer must create it.
        var path = Path.Combine(_dir, "missing", ".cursor", "hooks.json");

        CursorInstaller.Install(Helper, path);

        Assert.True(File.Exists(path));
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var hooks = root["hooks"]!.AsObject();

        // The command is a single quoted shell string — Cursor's dialect, not
        // Claude Code's command+args pair — because the path has spaces in it.
        AssertOurEntry(hooks, "beforeShellExecution", "cursor-permission", 600);
        AssertOurEntry(hooks, "beforeMCPExecution", "cursor-permission", 600);
        AssertOurEntry(hooks, "beforeSubmitPrompt", "cursor-notify", 10);
        AssertOurEntry(hooks, "stop", "cursor-notify", 10);

        Assert.Equal(1, (int)root["version"]!);
        Assert.Equal(InstallState.Installed, CursorInstaller.GetState(path));
    }

    [Fact]
    public void Install_NeverRegistersPreToolUse_WhichWouldGateEveryRead()
    {
        CursorInstaller.Install(Helper, HooksPath);

        var hooks = JsonNode.Parse(File.ReadAllText(HooksPath))!.AsObject()["hooks"]!.AsObject();
        Assert.Null(hooks["preToolUse"]);
        Assert.Null(hooks["beforeReadFile"]);
    }

    [Fact]
    public void Install_Twice_IsIdempotent_NoDuplicates()
    {
        CursorInstaller.Install(Helper, HooksPath);
        var first = File.ReadAllText(HooksPath);

        CursorInstaller.Install(Helper, HooksPath);
        var second = File.ReadAllText(HooksPath);

        Assert.Equal(first, second);
        foreach (var evt in OurEvents)
            Assert.Equal(1, CountOurs(second, evt));

        // Fresh install had no file to back up; the no-op second install writes nothing.
        Assert.Empty(Directory.GetFiles(_dir, "*agentnotch-backup*"));
    }

    [Fact]
    public void Install_HelperMoved_UpdatesEntryInPlace()
    {
        CursorInstaller.Install(Helper, HooksPath);

        const string moved = @"D:\new location\AGENTNOTCH-HOOK.EXE"; // case-insensitive marker match
        CursorInstaller.Install(moved, HooksPath);

        var hooks = JsonNode.Parse(File.ReadAllText(HooksPath))!.AsObject()["hooks"]!.AsObject();
        foreach (var evt in OurEvents)
        {
            Assert.Equal(1, CountOurs(File.ReadAllText(HooksPath), evt));
            var command = (string)hooks[evt]!.AsArray()
                .OfType<JsonObject>()
                .Single(e => IsOurs(e))["command"]!;
            Assert.StartsWith($"\"{moved}\"", command);
        }
    }

    [Fact]
    public void Install_PreservesForeignEntries_AndUninstallLeavesThemAlone()
    {
        File.WriteAllText(HooksPath, ForeignHooks);
        var original = JsonNode.Parse(ForeignHooks)!.AsObject();

        CursorInstaller.Install(Helper, HooksPath);

        var hooks = JsonNode.Parse(File.ReadAllText(HooksPath))!.AsObject()["hooks"]!.AsObject();

        // Foreign entries survive verbatim, ahead of ours, in the events we share.
        Assert.Equal(2, hooks["beforeShellExecution"]!.AsArray().Count);
        Assert.True(JsonNode.DeepEquals(
            original["hooks"]!["beforeShellExecution"]![0], hooks["beforeShellExecution"]![0]));
        Assert.Equal(2, hooks["beforeSubmitPrompt"]!.AsArray().Count);

        // An event we never touch is untouched.
        Assert.True(JsonNode.DeepEquals(original["hooks"]!["afterFileEdit"], hooks["afterFileEdit"]));

        CursorInstaller.Uninstall(HooksPath);

        var after = JsonNode.Parse(File.ReadAllText(HooksPath))!.AsObject();
        Assert.True(JsonNode.DeepEquals(original["hooks"], after["hooks"]));
        Assert.Equal(InstallState.NotInstalled, CursorInstaller.GetState(HooksPath));
    }

    [Fact]
    public void Uninstall_RemovesEveryEntry_IncludingStaleEventsFromOlderVersions()
    {
        CursorInstaller.Install(Helper, HooksPath);

        // An event an older build registered but the current one no longer does.
        var root = JsonNode.Parse(File.ReadAllText(HooksPath))!.AsObject();
        root["hooks"]!["afterAgentResponse"] = new JsonArray(new JsonObject
        {
            ["command"] = $"\"{Helper}\" cursor-notify",
            ["timeout"] = 10,
        });
        File.WriteAllText(HooksPath, root.ToJsonString());

        CursorInstaller.Uninstall(HooksPath);

        var after = JsonNode.Parse(File.ReadAllText(HooksPath))!.AsObject();
        Assert.Null(after["hooks"]); // everything in it was ours
        Assert.Equal(InstallState.NotInstalled, CursorInstaller.GetState(HooksPath));
    }

    [Fact]
    public void Install_DuplicateEntries_AreCollapsedToOne()
    {
        var seeded = new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["stop"] = new JsonArray(
                    new JsonObject { ["command"] = $"\"{Helper}\" cursor-notify" },
                    new JsonObject { ["command"] = @"C:\old\agentnotch-hook.exe cursor-notify" },
                    new JsonObject { ["command"] = @"C:\keep\foreign.exe" }),
            },
        };
        File.WriteAllText(HooksPath, seeded.ToJsonString());

        CursorInstaller.Install(Helper, HooksPath);

        var stop = JsonNode.Parse(File.ReadAllText(HooksPath))!.AsObject()["hooks"]!["stop"]!.AsArray();
        Assert.Equal(2, stop.Count); // one of ours + the foreign one
        Assert.Equal(1, stop.OfType<JsonObject>().Count(IsOurs));
        Assert.Contains(stop.OfType<JsonObject>(), e => (string?)e["command"] == @"C:\keep\foreign.exe");
    }

    [Fact]
    public void MalformedFile_IsNeverOverwritten()
    {
        File.WriteAllText(HooksPath, "{ not json");

        Assert.Throws<InvalidOperationException>(() => CursorInstaller.Install(Helper, HooksPath));
        Assert.Equal("{ not json", File.ReadAllText(HooksPath));

        // GetState is informational and must stay quiet on the same file.
        Assert.Equal(InstallState.NotInstalled, CursorInstaller.GetState(HooksPath));
    }

    [Fact]
    public void GetState_PartialWhenOnlySomeEventsRegistered()
    {
        CursorInstaller.Install(Helper, HooksPath);

        var root = JsonNode.Parse(File.ReadAllText(HooksPath))!.AsObject();
        root["hooks"]!.AsObject().Remove("stop");
        File.WriteAllText(HooksPath, root.ToJsonString());

        Assert.Equal(InstallState.Partial, CursorInstaller.GetState(HooksPath));
    }

    // ------------------------------------------------------------------ helpers

    private static bool IsOurs(JsonObject entry) =>
        ((string?)entry["command"])?.Contains("agentnotch-hook", StringComparison.OrdinalIgnoreCase) == true;

    private static int CountOurs(string json, string evt) =>
        JsonNode.Parse(json)!.AsObject()["hooks"]?[evt]?.AsArray()
            .OfType<JsonObject>().Count(IsOurs) ?? 0;

    private static void AssertOurEntry(JsonObject hooks, string evt, string mode, int timeout)
    {
        var ours = hooks[evt]!.AsArray().OfType<JsonObject>().Where(IsOurs).ToList();
        var entry = Assert.Single(ours);
        Assert.Equal($"\"{Helper}\" {mode}", (string?)entry["command"]);
        Assert.Equal(timeout, (int)entry["timeout"]!);
    }
}
