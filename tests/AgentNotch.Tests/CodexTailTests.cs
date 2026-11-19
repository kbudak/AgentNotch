using System.Text;
using System.Text.Json;
using AgentNotch.App.Sessions;
using Xunit;

namespace AgentNotch.Tests;

/// <summary>
/// Fixture-file tests for the Codex rollout collector (CodexTail): status
/// derivation, sub-agent exclusion, idle dropping, backwards-doubling tail
/// growth, and the per-file (mtime, size) parse cache.
/// </summary>
public sealed class CodexTailTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "agentnotch-codex-tests-" + Guid.NewGuid().ToString("N"));

    public CodexTailTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---------- fixture builders (rollout JSONL lines) ----------

    private static string Meta(
        string id = "sess-1",
        string? cwd = @"C:\proj\shop",
        string? model = null,
        string threadSource = "user",
        string? source = null,
        string? originator = null) =>
        JsonSerializer.Serialize(new
        {
            type = "session_meta",
            payload = new
            {
                id,
                session_id = id,
                cwd,
                model,
                thread_source = threadSource,
                source,
                originator,
            },
        });

    private static string TurnContext(string? cwd = null, string? model = null) =>
        JsonSerializer.Serialize(new { type = "turn_context", payload = new { cwd, model } });

    private static string TaskStarted() =>
        JsonSerializer.Serialize(new { type = "event_msg", payload = new { type = "task_started" } });

    private static string TaskComplete(string? lastAgentMessage = null) =>
        JsonSerializer.Serialize(new
        {
            type = "event_msg",
            payload = new { type = "task_complete", last_agent_message = lastAgentMessage },
        });

    private static string FinalAnswer(string message) =>
        JsonSerializer.Serialize(new
        {
            type = "event_msg",
            payload = new { type = "agent_message", phase = "final_answer", message },
        });

    private static string ToolCall(string name, string callId, string arguments = "{}") =>
        JsonSerializer.Serialize(new
        {
            type = "response_item",
            payload = new { type = "function_call", name, call_id = callId, arguments },
        });

    private static string ToolOutput(string callId, string output = "ok") =>
        JsonSerializer.Serialize(new
        {
            type = "response_item",
            payload = new { type = "function_call_output", call_id = callId, output },
        });

    /// <summary>Writes a rollout file (creating subdirs) and pins its mtime.</summary>
    private string WriteRollout(string relativePath, DateTime mtimeUtc, params string[] lines)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        File.SetLastWriteTimeUtc(path, mtimeUtc);
        return path;
    }

    // ---------- tests ----------

    private static string Stamped(string line, DateTime at)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(line)!;
        node["timestamp"] = at.ToString("O");
        return node.ToJsonString();
    }

    [Fact]
    public void OpenWindowsAppendHandleDoesNotMakeAnActiveTurnStale()
    {
        var now = DateTime.UtcNow;
        WriteRollout("open-handle.jsonl", now.AddHours(-2), Meta(),
            Stamped(TaskStarted(), now.AddMinutes(-2)),
            Stamped(ToolCall("shell", "c1"), now.AddSeconds(-1)));
        var session = Assert.Single(CodexTail.Collect(_dir, now));
        Assert.Equal(SessionStatus.Working, session.Status);
        Assert.Equal(now.AddSeconds(-1), session.LastActivityUtc);
    }

    [Fact]
    public void FrozenMtimeActiveFileIsNotLostBehindTwentyCompletedFiles()
    {
        var now = DateTime.UtcNow;
        for (int i = 0; i < 22; i++)
            WriteRollout($"finished-{i}.jsonl", now.AddMinutes(-1),
                Meta(id: $"done-{i}"), TaskStarted(), TaskComplete());
        WriteRollout("live.jsonl", now.AddHours(-3), Meta(id: "live"),
            Stamped(TaskStarted(), now.AddSeconds(-2)));
        Assert.Contains(CodexTail.Collect(_dir, now), s => s.SessionId == "codex:live");
    }

    [Fact]
    public void AppendWithUnchangedMtimeUpdatesLiveActivityAndThinking()
    {
        var now = DateTime.UtcNow;
        var oldMtime = now.AddHours(-1);
        var path = WriteRollout("append.jsonl", oldMtime, Meta(),
            Stamped(TaskStarted(), now.AddSeconds(-3)),
            Stamped(ToolCall("shell", "c1"), now.AddSeconds(-2)));
        Assert.False(Assert.Single(CodexTail.Collect(_dir, now)).IsThinking);
        File.AppendAllText(path, Stamped(ToolOutput("c1"), now) + "\n");
        File.SetLastWriteTimeUtc(path, oldMtime);
        var next = Assert.Single(CodexTail.Collect(_dir, now));
        Assert.True(next.IsThinking);
        Assert.Equal(now, next.LastActivityUtc);
    }

    [Fact]
    public void PreviousTurnsUnansweredQuestionCannotBlockTheNewTurn()
    {
        var now = DateTime.UtcNow;
        WriteRollout("turns.jsonl", now, Meta(), TaskStarted(),
            ToolCall("request_user_input", "old-question"), TaskComplete(), TaskStarted());
        var next = Assert.Single(CodexTail.Collect(_dir, now));
        Assert.Equal(SessionStatus.Working, next.Status);
        Assert.True(next.IsThinking);
        Assert.Null(next.CurrentTool);
    }

    [Fact]
    public void PendingQuestionsAndPermissionsExposeDifferentBuddyStates()
    {
        var now = DateTime.UtcNow;
        WriteRollout("question.jsonl", now, Meta(id: "question"), TaskStarted(),
            ToolCall("request_user_input", "q"));
        WriteRollout("permission.jsonl", now, Meta(id: "permission"), TaskStarted(),
            ToolCall("shell", "p", "{\"sandbox_permissions\":\"require_escalated\"}"));
        var sessions = CodexTail.Collect(_dir, now);
        Assert.False(sessions.Single(s => s.SessionId == "codex:question").NeedsApproval);
        Assert.True(sessions.Single(s => s.SessionId == "codex:permission").NeedsApproval);
    }

    [Fact]
    public void CommandsReadingApprovalCodeAreWorkNotAUserQuestion()
    {
        var now = DateTime.UtcNow;
        WriteRollout("source.jsonl", now, Meta(), TaskStarted(),
            ToolCall("exec", "code", "text(await tools.exec_command({cmd:'rg request_user_input approval require_escalated src'}));"));
        var session = Assert.Single(CodexTail.Collect(_dir, now));
        Assert.Equal(SessionStatus.Working, session.Status);
        Assert.False(session.NeedsApproval);
    }

    [Fact]
    public void ActualCodeModeQuestionIsDetectedButQuotedSourceIsNot()
    {
        var now = DateTime.UtcNow;
        WriteRollout("real.jsonl", now, Meta(id: "real"), TaskStarted(),
            ToolCall("exec", "q", "await tools.request_user_input({questions: []});"));
        WriteRollout("quoted.jsonl", now, Meta(id: "quoted"), TaskStarted(),
            ToolCall("exec", "patch", "const patch = 'await tools.request_user_input({questions: []});'; text(await tools.apply_patch(patch));"));
        var sessions = CodexTail.Collect(_dir, now);
        Assert.Equal(SessionStatus.NeedsYou, sessions.Single(s => s.SessionId == "codex:real").Status);
        Assert.Equal(SessionStatus.Working, sessions.Single(s => s.SessionId == "codex:quoted").Status);
    }

    [Fact]
    public void WorkingSessionReportsToolModelAndHost()
    {
        var now = DateTime.UtcNow;
        WriteRollout(
            Path.Combine("2026", "07", "20", "rollout-2026-07-20T10-00-00-abc.jsonl"),
            now.AddSeconds(-30),
            Meta(id: "sess-1", cwd: @"C:\proj\shop", model: "gpt-5.2-codex", source: "vscode"),
            TaskStarted(),
            ToolCall("shell", "c1"));

        var sessions = CodexTail.Collect(_dir, now);

        var s = Assert.Single(sessions);
        Assert.Equal("codex:sess-1", s.SessionId);
        Assert.Equal("codex", s.Agent);
        Assert.Equal(SessionStatus.Working, s.Status);
        Assert.Equal("shell", s.CurrentTool);
        Assert.Equal("Running shell", s.LastMessage);
        Assert.Equal("GPT-5.2-codex", s.Model);
        Assert.Equal("vscode", s.Host);
        Assert.Equal(@"C:\proj\shop", s.Cwd);
        Assert.Equal("shop", s.ProjectName);
        Assert.Equal("", s.Entrypoint);
        Assert.Equal(0, s.Pid);
        Assert.Equal(0, s.TokensSpent);
    }

    [Fact]
    public void ExecCommandActivityIsParsed()
    {
        var now = DateTime.UtcNow;
        WriteRollout(
            Path.Combine("2026", "08", "17", "rollout-2026-08-17T12-00-00-exec.jsonl"),
            now.AddSeconds(-20),
            Meta(id: "sess-exec", cwd: @"C:\work\demo-app", model: "gpt-5.6-sol", source: "vscode", originator: "Codex Desktop"),
            TaskStarted(),
            JsonSerializer.Serialize(new
            {
                type = "response_item",
                payload = new
                {
                    type = "custom_tool_call",
                    name = "exec",
                    call_id = "c_exec_1",
                    input = "const r = await tools.exec_command({cmd:\"npm run build\"});",
                },
            }));

        var s = Assert.Single(CodexTail.Collect(_dir, now));
        Assert.Equal(SessionStatus.Working, s.Status);
        Assert.Equal("exec_command", s.CurrentTool);
        Assert.Equal("Running npm run build", s.CurrentTarget);
        Assert.Equal("Running npm run build", s.LastMessage);
    }

    [Fact]
    public void ApplyPatchActivityIsParsed()
    {
        var now = DateTime.UtcNow;
        WriteRollout(
            Path.Combine("2026", "08", "17", "rollout-2026-08-17T12-00-00-patch.jsonl"),
            now.AddSeconds(-20),
            Meta(id: "sess-patch", cwd: @"C:\work\demo-app", model: "gpt-5.6-sol"),
            TaskStarted(),
            JsonSerializer.Serialize(new
            {
                type = "response_item",
                payload = new
                {
                    type = "custom_tool_call",
                    name = "exec",
                    call_id = "c_patch_1",
                    input = "const patch = \"*** Update File: C:\\\\app\\\\Dashboard.tsx\\n\"; text(await tools.apply_patch(patch));",
                },
            }));

        var s = Assert.Single(CodexTail.Collect(_dir, now));
        Assert.Equal(SessionStatus.Working, s.Status);
        Assert.Equal("apply_patch", s.CurrentTool);
        Assert.Equal("Editing Dashboard.tsx", s.CurrentTarget);
        Assert.Equal("Editing Dashboard.tsx", s.LastMessage);
    }

    [Fact]
    public void PendingEscalationCallMeansNeedsYou()
    {
        var now = DateTime.UtcNow;
        WriteRollout("r1.jsonl", now.AddSeconds(-30),
            Meta(),
            TaskStarted(),
            ToolCall("shell", "c9", arguments: "{\"require_escalated\":true,\"cmd\":\"npm i\"}"));

        var s = Assert.Single(CodexTail.Collect(_dir, now));
        Assert.Equal(SessionStatus.NeedsYou, s.Status);
        Assert.Equal("Waiting for your approval or answer", s.LastMessage);
        Assert.Null(s.CurrentTool); // only Working rows carry a tool
    }

    [Fact]
    public void AnsweredEscalationCallIsWorkingAgain()
    {
        var now = DateTime.UtcNow;
        WriteRollout("r1.jsonl", now.AddSeconds(-30),
            Meta(),
            TaskStarted(),
            ToolCall("request_user_input", "c9"),
            ToolOutput("c9"));

        var s = Assert.Single(CodexTail.Collect(_dir, now));
        Assert.Equal(SessionStatus.Working, s.Status);
        Assert.Equal("Running request_user_input", s.LastMessage);
    }

    [Fact]
    public void TaskCompleteIsFinishedWithSquashedMessage()
    {
        var now = DateTime.UtcNow;
        WriteRollout("r1.jsonl", now.AddSeconds(-30),
            Meta(model: null),
            TurnContext(model: "gpt-5.6-sol"),
            TaskStarted(),
            FinalAnswer("ignored — task_complete's last_agent_message wins"),
            TaskComplete("All done.\nDeployed   cleanly."));

        var s = Assert.Single(CodexTail.Collect(_dir, now));
        Assert.Equal(SessionStatus.Finished, s.Status);
        Assert.Equal("All done. Deployed cleanly.", s.LastMessage); // whitespace-collapsed
        Assert.Equal("GPT-5.6-sol", s.Model); // model picked up from turn_context
        Assert.Null(s.Host); // no vscode source/originator
        Assert.Null(s.CurrentTool);
    }

    [Fact]
    public void QuestionEndingFinalMessage_IsFinished_TheTaskIsOver()
    {
        var now = DateTime.UtcNow;
        WriteRollout("r1.jsonl", now.AddSeconds(-30),
            Meta(),
            TaskStarted(),
            TaskComplete("Should I also update the README?"));

        var s = Assert.Single(CodexTail.Collect(_dir, now));
        Assert.Equal(SessionStatus.Finished, s.Status);
        Assert.Equal("Should I also update the README?", s.LastMessage);
    }

    [Fact]
    public void TurkishQuestionAtTheEnd_IsFinished()
    {
        var now = DateTime.UtcNow;
        WriteRollout("r1.jsonl", now.AddSeconds(-30),
            Meta(),
            TaskStarted(),
            TaskComplete("Devam edeyim mi? Sonra commit atarım."));

        var s = Assert.Single(CodexTail.Collect(_dir, now));
        Assert.Equal(SessionStatus.Finished, s.Status);
    }

    [Fact]
    public void NonUserThreadSourceIsExcluded()
    {
        var now = DateTime.UtcNow;
        WriteRollout("r1.jsonl", now.AddSeconds(-30),
            Meta(threadSource: "guardian"),
            TaskStarted(),
            ToolCall("shell", "c1"));

        Assert.Empty(CodexTail.Collect(_dir, now));
    }

    [Fact]
    public void DelegatedAndVoiceThreadSourcesAreIncluded()
    {
        // A task delegated from another task / a voice chat (Codex Desktop) reports
        // "subagent" / "realtime_voice" — the user is watching these run, so they must
        // show, unlike the internal guardian/reviewer threads.
        var now = DateTime.UtcNow;
        WriteRollout("sub.jsonl", now.AddSeconds(-10), Meta(threadSource: "subagent"), TaskStarted());
        WriteRollout("voice.jsonl", now.AddSeconds(-5), Meta(threadSource: "realtime_voice"), TaskStarted());

        var sessions = CodexTail.Collect(_dir, now);
        Assert.Equal(2, sessions.Count);
        Assert.All(sessions, s => Assert.Equal(SessionStatus.Working, s.Status));
    }

    [Fact]
    public void NoLifecycleEventIsSkipped()
    {
        var now = DateTime.UtcNow;
        WriteRollout("r1.jsonl", now.AddSeconds(-30),
            Meta(),
            FinalAnswer("just chatting, no task lifecycle"));

        Assert.Empty(CodexTail.Collect(_dir, now));
    }

    [Fact]
    public void SessionsIdleBeyondTenMinutesAreDropped()
    {
        var now = DateTime.UtcNow;
        WriteRollout("finished.jsonl", now.AddMinutes(-11),
            Meta(id: "old-finished"), TaskStarted(), TaskComplete("Done."));
        WriteRollout("working.jsonl", now.AddMinutes(-11),
            Meta(id: "old-working"), TaskStarted(), ToolCall("shell", "c1"));

        // Both a finished AND a stale "working" rollout (Codex killed mid-turn) go.
        Assert.Empty(CodexTail.Collect(_dir, now));
    }

    [Fact]
    public void LifecycleBeyondFirstTailIsFoundByGrowingBackwards()
    {
        var now = DateTime.UtcNow;
        // Lifecycle + tool call sit at the START; a fat tool output pushes them
        // far beyond a tiny initial tail window.
        WriteRollout("r1.jsonl", now.AddSeconds(-30),
            Meta(),
            TaskStarted(),
            ToolCall("shell", "c1"),
            ToolOutput("c1", output: new string('x', 4096)));

        var sessions = CodexTail.Collect(_dir, now, tailBytes: 64);

        var s = Assert.Single(sessions);
        Assert.Equal(SessionStatus.Working, s.Status);
        Assert.Equal("shell", s.CurrentTool);
        Assert.Equal("Running shell", s.LastMessage);
    }

    [Fact]
    public void MissingMetaFallsBackToFileNameIdAndCodexProject()
    {
        var now = DateTime.UtcNow;
        WriteRollout("rollout-2026-07-20T10-00-00-019fabcd.jsonl", now.AddSeconds(-30),
            TaskStarted(),
            ToolCall("shell", "c1"));

        var s = Assert.Single(CodexTail.Collect(_dir, now));
        // JS strips only /^rollout-[^-]+-[^-]+-/ — ported verbatim.
        Assert.Equal("codex:20T10-00-00-019fabcd", s.SessionId);
        Assert.Equal("", s.Cwd);
        Assert.Equal("Codex", s.ProjectName);
        Assert.Null(s.Model);
        Assert.Null(s.Host);
    }

    [Fact]
    public void SessionsAreSortedNewestFirst()
    {
        var now = DateTime.UtcNow;
        WriteRollout("older.jsonl", now.AddMinutes(-2),
            Meta(id: "older"), TaskStarted(), ToolCall("shell", "c1"));
        WriteRollout("newer.jsonl", now.AddMinutes(-1),
            Meta(id: "newer"), TaskStarted(), ToolCall("shell", "c1"));

        var sessions = CodexTail.Collect(_dir, now);

        Assert.Equal(2, sessions.Count);
        Assert.Equal("codex:newer", sessions[0].SessionId);
        Assert.Equal("codex:older", sessions[1].SessionId);
    }

    [Fact]
    public void UnchangedFileIsServedFromCacheWithoutReread()
    {
        var now = DateTime.UtcNow;
        var path = WriteRollout("r1.jsonl", now.AddSeconds(-30),
            Meta(), TaskStarted(), ToolCall("shell", "c1"));

        var first = CodexTail.Collect(_dir, now);
        Assert.Single(first);

        // Replace the content with same-length garbage but keep (mtime, size)
        // identical — a re-read would now parse zero events and drop the session.
        var mtime = File.GetLastWriteTimeUtc(path);
        var length = new FileInfo(path).Length;
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(new string('#', (int)length)));
        File.SetLastWriteTimeUtc(path, mtime);

        var second = CodexTail.Collect(_dir, now.AddSeconds(1));

        Assert.Single(second);
        Assert.Same(first[0], second[0]); // cache hit returns the parsed instance
    }

    [Fact]
    public void MissingSessionsDirReturnsEmpty()
    {
        Assert.Empty(CodexTail.Collect(Path.Combine(_dir, "does-not-exist"), DateTime.UtcNow));
    }
}
