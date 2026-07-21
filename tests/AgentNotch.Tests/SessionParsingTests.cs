using System.Text;
using System.Text.Json;
using AgentNotch.App.Sessions;
using Xunit;

namespace AgentNotch.Tests;

/// <summary>
/// Fixture-file tests for the pure parsing/derivation layer (TranscriptTail):
/// status derivation, incremental token spend, and 256KB-tail partial-line tolerance.
/// </summary>
public sealed class SessionParsingTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "agentnotch-tests-" + Guid.NewGuid().ToString("N"));

    public SessionParsingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---------- fixture builders ----------

    private static string AssistantText(string text, string stopReason = "end_turn") =>
        JsonSerializer.Serialize(new
        {
            type = "assistant",
            message = new
            {
                role = "assistant",
                content = new object[] { new { type = "text", text } },
                stop_reason = stopReason,
            },
        });

    private static string AssistantUsage(long input, long cacheCreate, long output, long cacheRead = 0) =>
        JsonSerializer.Serialize(new
        {
            type = "assistant",
            message = new
            {
                role = "assistant",
                content = new object[] { new { type = "text", text = "ok" } },
                stop_reason = "end_turn",
                usage = new
                {
                    input_tokens = input,
                    cache_creation_input_tokens = cacheCreate,
                    cache_read_input_tokens = cacheRead,
                    output_tokens = output,
                },
            },
        });

    private static string AssistantToolUse(string tool, string stopReason = "tool_use", string command = "npm test") =>
        JsonSerializer.Serialize(new
        {
            type = "assistant",
            message = new
            {
                role = "assistant",
                content = new object[] { new { type = "tool_use", id = "t1", name = tool, input = new { command } } },
                stop_reason = stopReason,
            },
        });

    /// <summary>An assistant tool_use whose input is spelled out by the caller.</summary>
    private static string AssistantToolUseInput(string tool, object input) =>
        JsonSerializer.Serialize(new
        {
            type = "assistant",
            message = new
            {
                role = "assistant",
                content = new object[] { new { type = "tool_use", id = "t1", name = tool, input } },
                stop_reason = "tool_use",
            },
        });

    private static string UserToolResult() =>
        JsonSerializer.Serialize(new
        {
            type = "user",
            message = new
            {
                role = "user",
                content = new object[] { new { type = "tool_result", tool_use_id = "t1", content = "done" } },
            },
        });

    private static IReadOnlyList<JsonElement> Events(params string[] lines) =>
        TranscriptTail.ParseLines(string.Join("\n", lines));

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string WriteRegistry(string home, int pid, string sessionId, string cwd,
        string? status = null, long? statusUpdatedAtMs = null)
    {
        var dir = Path.Combine(home, "sessions");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, pid + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            pid,
            sessionId,
            cwd,
            entrypoint = "cli",
            status,
            statusUpdatedAt = statusUpdatedAtMs,
        }));
        return path;
    }

    private string WriteTranscript(string home, string sessionId, params string[] lines)
    {
        var dir = Path.Combine(home, "projects", "C--encoded-project");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, sessionId + ".jsonl");
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    private static string TranscriptPath(string home, string sessionId)
        => Path.Combine(home, "projects", "C--encoded-project", sessionId + ".jsonl");

    // ---------- status derivation ----------

    [Fact]
    public void TrailingUserMessage_RecentTranscript_IsWorking()
    {
        var events = Events(AssistantToolUse("Bash"), UserToolResult());
        var status = TranscriptTail.DeriveStatus(events, TimeSpan.FromSeconds(30), null, null);
        Assert.Equal(SessionStatus.Working, status);
    }

    [Fact]
    public void TrailingUserMessage_StaleTranscript_IsNeedsYou()
    {
        var events = Events(AssistantToolUse("Bash"), UserToolResult());
        var status = TranscriptTail.DeriveStatus(events, TimeSpan.FromMinutes(25), null, null);
        Assert.Equal(SessionStatus.NeedsYou, status);
    }

    [Fact]
    public void FreshBusyRegistryStatus_OverridesEndTurnTranscript()
    {
        var events = Events(AssistantText("Refactor complete. All tests pass."));
        var status = TranscriptTail.DeriveStatus(events, TimeSpan.FromMinutes(1), "busy", TimeSpan.FromMinutes(1));
        Assert.Equal(SessionStatus.Working, status);
    }

    [Fact]
    public void StaleBusyRegistryStatus_FallsBackToTranscript()
    {
        var events = Events(AssistantText("Refactor complete. All tests pass."));
        var status = TranscriptTail.DeriveStatus(events, TimeSpan.FromMinutes(1), "busy", TimeSpan.FromMinutes(30));
        Assert.Equal(SessionStatus.Finished, status);
    }

    [Fact]
    public void EndTurnEndingWithQuestion_IsNeedsYou()
    {
        var events = Events(AssistantText("I found two options.\nShould I refactor the parser?"));
        var status = TranscriptTail.DeriveStatus(events, TimeSpan.FromMinutes(1), null, null);
        Assert.Equal(SessionStatus.NeedsYou, status);
    }

    [Fact]
    public void EndTurnWithAskingPhrase_NoQuestionMark_IsNeedsYou()
    {
        var events = Events(AssistantText("Do you want me to also update the tests"));
        var status = TranscriptTail.DeriveStatus(events, TimeSpan.FromMinutes(1), null, null);
        Assert.Equal(SessionStatus.NeedsYou, status);
    }

    [Fact]
    public void EndTurnWithAskUserQuestionTool_IsNeedsYou()
    {
        var events = Events(AssistantToolUse("AskUserQuestion", stopReason: "end_turn"));
        var status = TranscriptTail.DeriveStatus(events, TimeSpan.FromMinutes(1), null, null);
        Assert.Equal(SessionStatus.NeedsYou, status);
    }

    [Fact]
    public void EndTurnPlainStatement_IsFinished_EvenWhenRecent()
    {
        var events = Events(AssistantText("Refactor complete. All tests pass."));
        var status = TranscriptTail.DeriveStatus(events, TimeSpan.FromSeconds(5), null, null);
        Assert.Equal(SessionStatus.Finished, status);
    }

    [Fact]
    public void PendingTool_IdleRegistry_IsNeedsYou_ApprovalPrompt()
    {
        var events = Events(AssistantToolUse("Bash"));
        Assert.True(TranscriptTail.IsPendingTool(events));
        var status = TranscriptTail.DeriveStatus(events, TimeSpan.FromSeconds(30), "idle", TimeSpan.FromSeconds(30));
        Assert.Equal(SessionStatus.NeedsYou, status);
    }

    [Fact]
    public void PendingTool_NoRegistryStatus_RecentIsWorking()
    {
        var events = Events(AssistantToolUse("Bash"));
        var status = TranscriptTail.DeriveStatus(events, TimeSpan.FromSeconds(30), null, null);
        Assert.Equal(SessionStatus.Working, status);
    }

    // ---------- token spend ----------

    [Fact]
    public void Spend_SumsInputCacheCreationAndOutput_ExcludingCacheReads()
    {
        var file = WriteFile("spend.jsonl",
            AssistantUsage(100, 50, 10, cacheRead: 9999) + "\n" +
            UserToolResult() + "\n" + // no usage — must be skipped
            AssistantUsage(7, 0, 3) + "\n");

        var state = TranscriptTail.AddSpend(file, default);

        Assert.Equal(170L, state.Tokens);
        Assert.Equal(new FileInfo(file).Length, state.Offset);
    }

    [Fact]
    public void Spend_SecondRead_OnlyParsesAppendedBytes()
    {
        var file = WriteFile("spend-incr.jsonl", AssistantUsage(100, 50, 10) + "\n");
        var first = TranscriptTail.AddSpend(file, default);
        Assert.Equal(160L, first.Tokens);

        File.AppendAllText(file, AssistantUsage(1, 2, 3) + "\n");
        var second = TranscriptTail.AddSpend(file, first);

        Assert.Equal(166L, second.Tokens);
        Assert.Equal(new FileInfo(file).Length, second.Offset);
        Assert.True(second.Offset > first.Offset);
    }

    [Fact]
    public void Spend_PartialTrailingLine_DeferredUntilNewlineArrives()
    {
        var complete = AssistantUsage(100, 0, 10) + "\n";
        var pending = AssistantUsage(5, 0, 9);
        int cut = pending.Length - 6; // split mid-JSON, no trailing newline yet
        var file = WriteFile("spend-partial.jsonl", complete + pending[..cut]);

        var first = TranscriptTail.AddSpend(file, default);
        Assert.Equal(110L, first.Tokens);
        Assert.Equal((long)Encoding.UTF8.GetByteCount(complete), first.Offset);

        File.AppendAllText(file, pending[cut..] + "\n");
        var second = TranscriptTail.AddSpend(file, first);
        Assert.Equal(124L, second.Tokens);
    }

    // ---------- tail reading ----------

    [Fact]
    public void ReadTail_ToleratesPartialFirstLine_InBigTranscript()
    {
        // First line is bigger than the 256KB tail window, so the read starts mid-line.
        var padding = JsonSerializer.Serialize(new { type = "padding", data = new string('x', 300_000) });
        var file = WriteFile("tail.jsonl",
            padding + "\n" + AssistantText("Tail intact.") + "\n" + UserToolResult() + "\n");
        Assert.True(new FileInfo(file).Length > TranscriptTail.TailBytes);

        var events = TranscriptTail.ReadTailEvents(file);

        Assert.Equal(2, events.Count); // the cut-off padding line is dropped, rest parse
        Assert.Equal("assistant", events[0].GetProperty("type").GetString());
        Assert.Equal("user", events[1].GetProperty("type").GetString());
    }

    // ---------- full snapshot ----------

    [Fact]
    public void Snapshot_WorkingSession_CarriesToolTargetSpendAndProjectName()
    {
        var home = Path.Combine(_dir, "home-working");
        var cwd = Path.Combine(_dir, "repos", "projA");
        WriteRegistry(home, 4242, "s1", cwd, status: "busy",
            statusUpdatedAtMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        WriteTranscript(home, "s1",
            AssistantUsage(100, 50, 10),
            AssistantToolUse("Bash"),
            UserToolResult());

        var cache = new Dictionary<string, TranscriptTail.SpendState>();
        var list = TranscriptTail.BuildSnapshot(home, cache, _ => true, DateTime.UtcNow);

        var s = Assert.Single(list);
        Assert.Equal(SessionStatus.Working, s.Status);
        Assert.Equal("s1", s.SessionId);
        Assert.Equal(4242, s.Pid);
        Assert.Equal("projA", s.ProjectName);
        Assert.Equal("cli", s.Entrypoint);
        Assert.Equal("Bash", s.CurrentTool);
        // The row shows a readable phrase, never the raw command.
        Assert.Equal("Running npm test", s.CurrentTarget);
        Assert.Equal(160L, s.TokensSpent);
        Assert.Equal("ok", s.LastMessage);
    }

    // ---------- activity phrasing (rows must never show raw arguments) ----------

    /// <summary>Runs a transcript whose last tool_use has the given input, returns the row's activity text.</summary>
    private string ActivityFor(string tool, object input, [System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        var home = Path.Combine(_dir, "act-" + name);
        WriteRegistry(home, 4242, "s1", Path.Combine(_dir, "repos", "projA"), status: "busy",
            statusUpdatedAtMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        WriteTranscript(home, "s1", AssistantToolUseInput(tool, input), UserToolResult());

        var list = TranscriptTail.BuildSnapshot(
            home, new Dictionary<string, TranscriptTail.SpendState>(), _ => true, DateTime.UtcNow);
        return Assert.Single(list).CurrentTarget ?? "";
    }

    [Fact]
    public void Activity_FileTools_ShowVerbAndFileNameOnly()
    {
        Assert.Equal("Editing middleware.ts",
            ActivityFor("Edit", new { file_path = @"C:\work\api\src\auth\middleware.ts" }));
        Assert.Equal("Writing notes.md",
            ActivityFor("Write", new { file_path = "/home/k/docs/notes.md" }));
        Assert.Equal("Reading schema.prisma",
            ActivityFor("Read", new { file_path = @"C:\work\db\schema.prisma" }));
    }

    [Fact]
    public void Activity_Bash_PrefersTheHumanDescription()
    {
        Assert.Equal("List project directory contents",
            ActivityFor("PowerShell", new
            {
                command = "Get-ChildItem -Force \"C:\\work\" | Select-Object Name, Length",
                description = "List project directory contents",
            }));
    }

    [Fact]
    public void Activity_ShortCommand_IsShownInFull()
        => Assert.Equal("Running git status", ActivityFor("Bash", new { command = "git status" }));

    [Fact]
    public void Activity_ScriptedOneLiner_CollapsesToTheProgram()
        => Assert.Equal("Running npm…", ActivityFor("Bash", new
        {
            command = "npm run build -- --configuration production --verbose --no-cache",
        }));

    [Fact]
    public void Activity_UnreadableOneLiner_SaysNothingSpecific()
        => Assert.Equal("Running a command", ActivityFor("PowerShell", new
        {
            command = "try{Get-Process AgentNotch -EA Stop|Stop-Process -Force}catch{}; Start-Sleep -m 900",
        }));

    [Fact]
    public void Activity_SearchAndDelegation_ReadAsPhrases()
    {
        Assert.Equal("Searching TODO", ActivityFor("Grep", new { pattern = "TODO" }));
        Assert.Equal("Running a subagent", ActivityFor("Task", new { description = "x" }));
        Assert.Equal("Searching the web", ActivityFor("WebSearch", new { query = "wpf path geometry" }));
    }

    [Fact]
    public void Dedup_PrefersEditorHostedSession_OverMoreRecentTerminalOne()
    {
        var home = Path.Combine(_dir, "dedup-host");
        var cwd = Path.Combine(_dir, "repos", "belediyem");
        // Terminal session is MORE recent; editor session older.
        WriteRegistry(home, 100, "term", cwd, status: "busy",
            statusUpdatedAtMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        WriteTranscript(home, "term", AssistantToolUse("Bash"), UserToolResult());
        WriteRegistry(home, 200, "editor", cwd, status: "busy",
            statusUpdatedAtMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        WriteTranscript(home, "editor", AssistantToolUse("Bash"), UserToolResult());
        // Make the terminal transcript newer so recency alone would pick it.
        File.SetLastWriteTimeUtc(TranscriptPath(home, "term"), DateTime.UtcNow);
        File.SetLastWriteTimeUtc(TranscriptPath(home, "editor"), DateTime.UtcNow.AddMinutes(-5));

        // Classifier: pid 100 = plain terminal (rank 2), pid 200 = editor (rank 0).
        Func<int, int> hostRank = pid => pid == 100 ? 2 : pid == 200 ? 0 : 1;
        var list = TranscriptTail.BuildSnapshot(
            home, new Dictionary<string, TranscriptTail.SpendState>(), _ => true, DateTime.UtcNow, hostRank);

        var s = Assert.Single(list);
        Assert.Equal(200, s.Pid); // editor-hosted wins despite terminal being newer
    }

    [Fact]
    public void Snapshot_StaleRegistrySid_FallsBackToNewestProjectTranscript()
    {
        var home = Path.Combine(_dir, "stale-sid");
        const string cwd = @"C:\enc\proj";
        WriteRegistry(home, 4242, "missing-sid", cwd, status: "busy",
            statusUpdatedAtMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        // Live transcript sits under a DIFFERENT id (editor resume rotated it), in the
        // encoded project dir: C:\enc\proj -> "C--enc-proj".
        var pdir = Path.Combine(home, "projects", "C--enc-proj");
        Directory.CreateDirectory(pdir);
        File.WriteAllText(Path.Combine(pdir, "rotated-id.jsonl"),
            string.Join("\n", AssistantToolUse("Bash"), UserToolResult()) + "\n");

        var list = TranscriptTail.BuildSnapshot(
            home, new Dictionary<string, TranscriptTail.SpendState>(), _ => true, DateTime.UtcNow);

        var s = Assert.Single(list);
        Assert.Equal(4242, s.Pid);          // the session shows despite the stale sid
        Assert.Equal("proj", s.ProjectName);
    }

    [Fact]
    public void Dedup_WithoutClassifier_KeepsMostRecent()
    {
        var home = Path.Combine(_dir, "dedup-recent");
        var cwd = Path.Combine(_dir, "repos", "proj");
        WriteRegistry(home, 100, "old", cwd, status: "busy", statusUpdatedAtMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        WriteTranscript(home, "old", AssistantToolUse("Bash"), UserToolResult());
        WriteRegistry(home, 200, "new", cwd, status: "busy", statusUpdatedAtMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        WriteTranscript(home, "new", AssistantToolUse("Bash"), UserToolResult());
        File.SetLastWriteTimeUtc(TranscriptPath(home, "old"), DateTime.UtcNow.AddMinutes(-5));
        File.SetLastWriteTimeUtc(TranscriptPath(home, "new"), DateTime.UtcNow);

        var list = TranscriptTail.BuildSnapshot(
            home, new Dictionary<string, TranscriptTail.SpendState>(), _ => true, DateTime.UtcNow);

        Assert.Equal(200, Assert.Single(list).Pid); // no classifier -> recency wins (unchanged behaviour)
    }

    [Fact]
    public void Snapshot_DeadProcess_IsSkipped_AndSpendCachePruned()
    {
        var home = Path.Combine(_dir, "home-dead");
        WriteRegistry(home, 777, "s1", Path.Combine(_dir, "projX"));
        WriteTranscript(home, "s1", AssistantText("Working on it"));

        var cache = new Dictionary<string, TranscriptTail.SpendState> { ["ghost"] = new(5, 5) };
        var list = TranscriptTail.BuildSnapshot(home, cache, _ => false, DateTime.UtcNow);

        Assert.Empty(list);
        Assert.Empty(cache); // closed sessions must not leak cache entries
    }

    [Fact]
    public void Snapshot_NonWorkingSessionIdleTooLong_IsDropped()
    {
        var home = Path.Combine(_dir, "home-idle");
        WriteRegistry(home, 31, "sIdle", Path.Combine(_dir, "projIdle"));
        var transcript = WriteTranscript(home, "sIdle", AssistantText("Everything is committed."));
        File.SetLastWriteTimeUtc(transcript, DateTime.UtcNow.AddMinutes(-15));

        var cache = new Dictionary<string, TranscriptTail.SpendState>();
        var list = TranscriptTail.BuildSnapshot(home, cache, _ => true, DateTime.UtcNow);

        Assert.Empty(list);
    }

    [Fact]
    public void Snapshot_DedupsPerCwd_KeepingMostRecentlyActive()
    {
        var home = Path.Combine(_dir, "home-dedup");
        var cwd = Path.Combine(_dir, "projB");
        WriteRegistry(home, 21, "sOld", cwd);
        WriteRegistry(home, 22, "sNew", cwd);
        var oldTranscript = WriteTranscript(home, "sOld", AssistantText("Everything is committed."));
        WriteTranscript(home, "sNew", AssistantText("Should I continue with step two?"));
        File.SetLastWriteTimeUtc(oldTranscript, DateTime.UtcNow.AddMinutes(-5));

        var cache = new Dictionary<string, TranscriptTail.SpendState>();
        var list = TranscriptTail.BuildSnapshot(home, cache, _ => true, DateTime.UtcNow);

        var s = Assert.Single(list);
        Assert.Equal("sNew", s.SessionId);
        Assert.Equal(SessionStatus.NeedsYou, s.Status);
    }

    [Fact]
    public void Snapshot_SortsNeedsYouThenFinishedThenWorking()
    {
        var home = Path.Combine(_dir, "home-sort");
        WriteRegistry(home, 11, "sNeeds", Path.Combine(_dir, "proj1"));
        WriteRegistry(home, 12, "sDone", Path.Combine(_dir, "proj2"));
        WriteRegistry(home, 13, "sWork", Path.Combine(_dir, "proj3"), status: "busy",
            statusUpdatedAtMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        WriteTranscript(home, "sNeeds", AssistantText("Should I refactor the parser?"));
        WriteTranscript(home, "sDone", AssistantText("Refactor complete. All tests pass."));
        WriteTranscript(home, "sWork", AssistantToolUse("Bash"), UserToolResult());

        var cache = new Dictionary<string, TranscriptTail.SpendState>();
        var list = TranscriptTail.BuildSnapshot(home, cache, _ => true, DateTime.UtcNow);

        Assert.Equal(3, list.Count);
        Assert.Equal(
            new[] { SessionStatus.NeedsYou, SessionStatus.Finished, SessionStatus.Working },
            list.Select(s => s.Status).ToArray());
    }
}
