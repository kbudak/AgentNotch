using System.Text.Json;
using AgentNotch.App.Sessions;
using Xunit;

namespace AgentNotch.Tests;

public sealed class CursorTailTests
{
    private static readonly DateTime Now = new(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);

    private static JsonElement Ev(string role, params object[] parts)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            role,
            message = new { content = parts },
        }));
        return doc.RootElement.Clone();
    }

    private static object Text(string text) => new { type = "text", text };

    private static object Tool(string name, object? input = null) => new
    {
        type = "tool_use",
        name,
        input = input ?? new { },
    };

    private static JsonElement JsonDoc(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Working_WhenLastIsToolUse()
    {
        var events = new[]
        {
            Ev("user", Text("fix the bug")),
            Ev("assistant",
                Text("Looking into it."),
                Tool("Read", new { path = @"C:\work\app\Program.cs" })),
        };

        var s = CursorTail.ParseEvents(events, "c-Users-test-Desktop-app", Now, "abc");
        Assert.NotNull(s);
        Assert.Equal(SessionStatus.Working, s!.Status);
        Assert.Equal("cursor", s.Agent);
        Assert.Equal("cursor:abc", s.SessionId);
        Assert.Equal("Read", s.CurrentTool);
        Assert.Contains("Program.cs", s.CurrentTarget);
    }

    [Fact]
    public void NeedsYou_WhenAskQuestionToolPending()
    {
        var events = new[]
        {
            Ev("user", Text("ship it")),
            Ev("assistant",
                Tool("AskQuestion", new
                {
                    title = "Which deploy target?",
                    questions = new[]
                    {
                        new { question = "Staging or production?", options = new[] { "staging", "prod" } },
                    },
                })),
        };

        var s = CursorTail.ParseEvents(events, "c-Users-test-Desktop-app", Now, "q1");
        Assert.NotNull(s);
        Assert.Equal(SessionStatus.NeedsYou, s!.Status);
        Assert.Contains("Which deploy target?", s.LastMessage);
        Assert.Equal("AskQuestion", s.CurrentTool);
    }

    [Fact]
    public void NeedsYou_WhenShellRequiresPermissions()
    {
        var events = new[]
        {
            Ev("user", Text("run it")),
            Ev("assistant",
                Text("Need elevated shell."),
                Tool("Shell", new
                {
                    command = "Get-Process AgentNotch",
                    description = "Check AgentNotch process",
                    required_permissions = new[] { "all" },
                })),
        };

        var s = CursorTail.ParseEvents(events, "c-Users-test-Desktop-app", DateTime.UtcNow, "perm");
        Assert.NotNull(s);
        Assert.Equal(SessionStatus.NeedsYou, s!.Status);
        Assert.Contains("Allow", s.LastMessage);
        Assert.Equal("Shell", s.CurrentTool);
    }

    [Fact]
    public void NeedsYou_WhenAssistantTextEndsWithQuestion()
    {
        var events = new[]
        {
            Ev("user", Text("continue")),
            Ev("assistant", Text("Should I rewrite the auth module?")),
        };

        var s = CursorTail.ParseEvents(events, "c-Users-test-Desktop-app", Now, "q2");
        Assert.NotNull(s);
        Assert.Equal(SessionStatus.NeedsYou, s!.Status);
        Assert.Contains("auth module", s.LastMessage);
    }

    [Fact]
    public void Finished_WhenAssistantCompletesWithoutQuestion()
    {
        var events = new[]
        {
            Ev("user", Text("rename the file")),
            Ev("assistant", Text("Renamed Foo.cs to Bar.cs.")),
            JsonDoc("""{"type":"turn_ended","status":"success"}"""),
        };

        var s = CursorTail.ParseEvents(events, "c-Users-test-Desktop-app", DateTime.UtcNow, "done");
        Assert.NotNull(s);
        Assert.Equal(SessionStatus.Finished, s!.Status);
        Assert.Contains("Renamed", s.LastMessage);
    }

    [Fact]
    public void NeedsYou_WhenCallMcpToolCursorDialog()
    {
        var events = new[]
        {
            Ev("user", Text("ask me")),
            Ev("assistant",
                Tool("CallMcpTool", new
                {
                    server = "cursor-app-control",
                    toolName = "cursor_dialog",
                    arguments = new { title = "Pick a theme", question = "Island or console?" },
                })),
        };

        var s = CursorTail.ParseEvents(events, "c-Users-test-Desktop-app", DateTime.UtcNow, "mcp-ask");
        Assert.NotNull(s);
        Assert.Equal(SessionStatus.NeedsYou, s!.Status);
        Assert.True(
            s.LastMessage.Contains("theme", StringComparison.OrdinalIgnoreCase)
            || s.LastMessage.Contains("Island", StringComparison.OrdinalIgnoreCase)
            || s.LastMessage.Contains("question", StringComparison.OrdinalIgnoreCase),
            s.LastMessage);
    }

    [Fact]
    public void Working_WhenRecentAssistantTextWithoutTurnEnd()
    {
        var events = new[]
        {
            Ev("user", Text("keep going")),
            Ev("assistant", Text("Still investigating the notch bindings.")),
        };

        var s = CursorTail.ParseEvents(events, "c-Users-test-Desktop-app", DateTime.UtcNow, "think");
        Assert.NotNull(s);
        Assert.Equal(SessionStatus.Working, s!.Status);
    }

    [Fact]
    public void Working_WhenUserJustReplied()
    {
        var events = new[]
        {
            Ev("assistant", Tool("AskQuestion", new { title = "Pick one" })),
            Ev("user", Text("staging")),
        };

        var s = CursorTail.ParseEvents(events, "c-Users-test-Desktop-app", Now, "reply");
        Assert.NotNull(s);
        Assert.Equal(SessionStatus.Working, s!.Status);
    }

    [Fact]
    public void Working_AfterUserMessageBeforeAssistantTools()
    {
        var events = new[]
        {
            Ev("assistant", Text("Done.")),
            JsonDoc("""{"type":"turn_ended","status":"success"}"""),
            Ev("user", Text("fix the notch")),
        };

        var s = CursorTail.ParseEvents(events, "c-Users-test-Desktop-app", Now, "open");
        Assert.NotNull(s);
        Assert.Equal(SessionStatus.Working, s!.Status);
        Assert.Equal("Working…", s.LastMessage);
    }

    [Fact]
    public void Working_AfterTurnEndedWhenTaskSpawned()
    {
        var events = new[]
        {
            Ev("user", Text("fix everything")),
            Ev("assistant",
                Tool("Task", new { description = "Fix Cursor notch", prompt = "go" })),
            Ev("assistant", Text("Delegated to subagent.")),
            JsonDoc("""{"type":"turn_ended","status":"success"}"""),
        };

        var s = CursorTail.ParseEvents(events, "c-Users-test-Desktop-app", DateTime.UtcNow, "task");
        Assert.NotNull(s);
        Assert.Equal(SessionStatus.Working, s!.Status);
    }

    [Fact]
    public void NeedsYou_WhenQuestionIsOnLastLine()
    {
        var events = new[]
        {
            Ev("user", Text("continue")),
            Ev("assistant", Text("I found two approaches.\n\nShould I use the simpler one?")),
        };

        var s = CursorTail.ParseEvents(events, "c-Users-test-Desktop-app", Now, "q3");
        Assert.NotNull(s);
        Assert.Equal(SessionStatus.NeedsYou, s!.Status);
        Assert.Contains("simpler", s.LastMessage);
    }

    [Fact]
    public void NeedsYou_SurvivesTurnEndedAfterAsk()
    {
        var events = new[]
        {
            Ev("user", Text("ship it")),
            Ev("assistant",
                Tool("AskQuestion", new { title = "Staging or production?" })),
            JsonDoc("""{"type":"turn_ended","status":"success"}"""),
        };

        var s = CursorTail.ParseEvents(events, "c-Users-test-Desktop-app", Now, "q4");
        Assert.NotNull(s);
        Assert.Equal(SessionStatus.NeedsYou, s!.Status);
        Assert.Contains("Staging", s.LastMessage);
    }

    [Fact]
    public void NeedsYou_SurvivesTurnEndedAfterTextQuestion()
    {
        var events = new[]
        {
            Ev("user", Text("go")),
            Ev("assistant", Text("Ready.\n\nDeploy to production now?")),
            JsonDoc("""{"type":"turn_ended","status":"success"}"""),
        };

        var s = CursorTail.ParseEvents(events, "c-Users-test-Desktop-app", Now, "q5");
        Assert.NotNull(s);
        Assert.Equal(SessionStatus.NeedsYou, s!.Status);
        Assert.Contains("production", s.LastMessage);
    }

    [Fact]
    public void Finished_AfterTurnEnded()
    {
        var events = new[]
        {
            Ev("user", Text("ship it")),
            Ev("assistant", Text("Shipped.")),
            JsonDoc("""{"type":"turn_ended","status":"success"}"""),
        };

        var s = CursorTail.ParseEvents(events, "c-Users-test-Desktop-app", Now, "ended");
        Assert.NotNull(s);
        Assert.Equal(SessionStatus.Finished, s!.Status);
    }

    [Fact]
    public void Parse_MessageAsBareString_DoesNotThrow()
    {
        var events = new[]
        {
            JsonDoc("""{"role":"user","message":"just a string"}"""),
            JsonDoc("""{"role":"assistant","message":"All done."}"""),
        };
        var s = CursorTail.ParseEvents(events, "c-Users-test-Desktop-app", Now, "str");
        Assert.NotNull(s);
        Assert.Equal(SessionStatus.Finished, s!.Status);
    }

    [Fact]
    public void EmptyEvents_ReturnsNull()
    {
        Assert.Null(CursorTail.ParseEvents(Array.Empty<JsonElement>(), "slug", Now));
    }

    [Fact]
    public void ElevateWithSubagents_PromotesNeedsYouFromChild()
    {
        var root = Path.Combine(Path.GetTempPath(), "agentnotch-cursor-" + Guid.NewGuid().ToString("N"));
        var sessionDir = Path.Combine(root, "agent-transcripts", "parent-id");
        var subDir = Path.Combine(sessionDir, "subagents");
        Directory.CreateDirectory(subDir);

        var parentPath = Path.Combine(sessionDir, "parent-id.jsonl");
        File.WriteAllText(parentPath,
            """{"role":"user","message":{"content":[{"type":"text","text":"go"}]}}""" + "\n" +
            """{"role":"assistant","message":{"content":[{"type":"tool_use","name":"Task","input":{"description":"child"}}]}}""" + "\n" +
            """{"type":"turn_ended","status":"success"}""" + "\n");

        var childPath = Path.Combine(subDir, "child.jsonl");
        File.WriteAllText(childPath,
            """{"role":"user","message":{"content":[{"type":"text","text":"go"}]}}""" + "\n" +
            """{"role":"assistant","message":{"content":[{"type":"tool_use","name":"Shell","input":{"command":"echo hi","description":"echo","required_permissions":["all"]}}]}}""" + "\n");

        try
        {
            CursorTail.Cache.Clear();
            var parent = CursorTail.CollectOne(parentPath, "c-Users-test-Desktop-app",
                File.GetLastWriteTimeUtc(parentPath), new FileInfo(parentPath).Length, CursorTail.TailBytes);
            Assert.NotNull(parent);

            var elevated = CursorTail.ElevateWithSubagents(parentPath, "c-Users-test-Desktop-app", parent!, CursorTail.TailBytes);
            Assert.Equal(SessionStatus.NeedsYou, elevated.Status);
            Assert.Equal(parent!.SessionId, elevated.SessionId);
            Assert.Contains("Allow", elevated.LastMessage);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
            CursorTail.Cache.Clear();
        }
    }
}
