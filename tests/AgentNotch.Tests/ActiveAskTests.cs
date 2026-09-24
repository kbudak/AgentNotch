using System.Text.Json;
using AgentNotch.App.Sessions;
using Xunit;

namespace AgentNotch.Tests;

/// <summary>
/// Orange means the agent is asking you something RIGHT NOW — not that a finished
/// session's last line happens to look like a question.
///
/// Surveyed across 47 real transcripts, the final turn of a finished session was an
/// end_turn text (19), a text whose stop_reason was never written (10), a tool
/// result (8), a local /command's output (5), a prompt or an interrupt (2), or a
/// pending tool (2). With the CLI idle, every shape except the pending tool used to
/// come out orange. Each fixture below is one of those real shapes.
/// </summary>
public sealed class ActiveAskTests
{
    private static readonly TimeSpan Recent = TimeSpan.FromMinutes(1);

    private static List<JsonElement> Events(params object[] records)
        => records.Select(r =>
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(r));
            return doc.RootElement.Clone();
        }).ToList();

    private static object Assistant(string text, string? stopReason) => new
    {
        type = "assistant",
        message = stopReason is null
            ? (object)new { role = "assistant", content = new object[] { new { type = "text", text } } }
            : new { role = "assistant", content = new object[] { new { type = "text", text } }, stop_reason = stopReason },
    };

    private static object ToolUse(string name, string stopReason = "tool_use") => new
    {
        type = "assistant",
        message = new
        {
            role = "assistant",
            content = new object[] { new { type = "tool_use", id = "t1", name, input = new { command = "npm test" } } },
            stop_reason = stopReason,
        },
    };

    private static object User(object content) => new { type = "user", message = new { role = "user", content } };

    private static SessionStatus IdleFresh(List<JsonElement> events)
        => TranscriptTail.DeriveStatus(events, Recent, "idle", Recent);

    // ---------- browser site-permission dialog ----------
    // No hook fires for it and the registry says "busy" throughout; only the time a
    // browser call has gone unanswered tells a quick call from one held at the dialog.

    [Fact]
    public void BrowserCall_UnansweredPastTheThreshold_NeedsYou_EvenWhileBusy()
        => Assert.Equal(SessionStatus.NeedsYou, TranscriptTail.DeriveStatus(
            Events(ToolUse("mcp__Claude_Browser__javascript_tool")),
            TimeSpan.FromSeconds(20), "busy", TimeSpan.FromSeconds(20)));

    [Fact]
    public void BrowserCall_StillQuick_IsWorking()
        => Assert.Equal(SessionStatus.Working, TranscriptTail.DeriveStatus(
            Events(ToolUse("mcp__claude-in-chrome__navigate")),
            TimeSpan.FromSeconds(3), "busy", TimeSpan.FromSeconds(3)));

    [Fact]
    public void BrowserCall_Answered_IsNotHeldAtTheDialog()
        => Assert.Equal(SessionStatus.Working, TranscriptTail.DeriveStatus(
            Events(ToolUse("mcp__Claude_Browser__javascript_tool"),
                   User(new object[] { new { type = "tool_result", tool_use_id = "t1", content = "ok" } })),
            TimeSpan.FromSeconds(30), "busy", TimeSpan.FromSeconds(30)));

    [Fact]
    public void SlowNonBrowserTool_StaysWorking()
        => Assert.Equal(SessionStatus.Working, TranscriptTail.DeriveStatus(
            Events(ToolUse("Bash")), TimeSpan.FromMinutes(2), "busy", TimeSpan.FromMinutes(2)));

    // ---------- finished sessions: never orange ----------

    [Fact]
    public void FinishedTurn_EndingInAQuestion_IsFinished()
        => Assert.Equal(SessionStatus.Finished, IdleFresh(Events(
            Assistant("İki yol var.\n\nHangisini istersin — yoksa üçüncü bir yol mu?", "end_turn"))));

    [Fact]
    public void FinishedTurn_WhoseStopReasonWasNeverWritten_IsFinished()
        => Assert.Equal(SessionStatus.Finished, IdleFresh(Events(
            Assistant("Prompt'u hazırladım. İstersen docs/ altına da kaydedebilirim.", stopReason: null))));

    [Fact]
    public void ToolResultLeftLast_WithTheCliIdle_IsFinished()
        => Assert.Equal(SessionStatus.Finished, IdleFresh(Events(
            ToolUse("Read"),
            User(new object[] { new { type = "tool_result", tool_use_id = "t1", content = "1 export const APP_NAME" } }))));

    [Fact]
    public void LocalCommandOutputLeftLast_IsFinished()
    {
        var events = Events(
            Assistant("Done.", "end_turn"),
            User("<local-command-stdout>Login successful</local-command-stdout>"));

        Assert.Equal(SessionStatus.Finished, IdleFresh(events));
        // No registry status at all (desktop), long after: still not a question.
        Assert.Equal(SessionStatus.Finished,
            TranscriptTail.DeriveStatus(events, TimeSpan.FromMinutes(40), null, null));
    }

    [Fact]
    public void Interrupt_IsFinished()
        => Assert.Equal(SessionStatus.Finished, IdleFresh(Events(
            Assistant("Looking into it", stopReason: null),
            User(new object[] { new { type = "text", text = "[Request interrupted by user]" } }))));

    // ---------- live asks: still orange ----------

    [Fact]
    public void PendingAskUserQuestion_IsNeedsYou()
        => Assert.Equal(SessionStatus.NeedsYou, IdleFresh(Events(ToolUse("AskUserQuestion"))));

    [Fact]
    public void PendingAskUserQuestion_IsNeedsYou_EvenWithoutARegistryStatus()
        => Assert.Equal(SessionStatus.NeedsYou,
            TranscriptTail.DeriveStatus(Events(ToolUse("AskUserQuestion", "end_turn")), Recent, null, null));

    [Fact]
    public void ToolAwaitingApproval_WithTheCliIdleAtThePrompt_IsNeedsYou()
        => Assert.Equal(SessionStatus.NeedsYou, IdleFresh(Events(ToolUse("Bash"))));

    [Fact]
    public void ToolRunning_WhileTheCliIsBusy_IsWorking_NotAQuestion()
        => Assert.Equal(SessionStatus.Working,
            TranscriptTail.DeriveStatus(Events(ToolUse("Bash")), Recent, "busy", Recent));
}
