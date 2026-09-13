using System.Text.Json;
using AgentNotch.App.Sessions;
using Xunit;

namespace AgentNotch.Tests;

/// <summary>
/// Claude Code writes one transcript line PER CONTENT BLOCK while the model streams,
/// and that line's stop_reason is often absent when it lands. So the tail of a live
/// transcript is normally mid-turn, not the end of one. Deriving "done" from
/// stop_reason alone made a thinking agent read as Finished — and because that one
/// line sits at the tail for the whole length of a long thought, the pill went green
/// and stayed green while the agent was still working.
///
/// Every shape below was taken from real transcripts under ~\.claude\projects.
/// </summary>
public sealed class ThinkingStatusTests
{
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(1);

    private static List<JsonElement> Turn(string? stopReason, params string[] blockTypes)
    {
        var content = blockTypes.Select(object (t) => t switch
        {
            "thinking" => new { type = "thinking", thinking = "Let me work out the next step..." },
            "tool_use" => new { type = "tool_use", name = "Bash", input = new { command = "npm test" } },
            _ => new { type = "text", text = "Looking into it." },
        }).ToArray();

        var message = stopReason is null
            ? (object)new { role = "assistant", content }
            : new { role = "assistant", content, stop_reason = stopReason };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { type = "assistant", message }));
        return new List<JsonElement> { doc.RootElement.Clone() };
    }

    // ---------- the bug: mid-thought read as done ----------

    [Theory]
    [InlineData("end_turn")]   // 92 of these in the corpus
    [InlineData("tool_use")]   // 3023
    [InlineData(null)]         // 209 — stop_reason not yet written
    public void ThinkingBlockLast_IsWorking_WhateverTheStopReasonSays(string? stopReason)
    {
        var status = TranscriptTail.DeriveStatus(Turn(stopReason, "thinking"), Fresh, null, null);
        Assert.Equal(SessionStatus.Working, status);
    }

    [Fact]
    public void PendingToolWithNoStopReasonYet_IsWorking()
    {
        // 232 in the corpus: the tool_use block is written before the reason lands.
        var status = TranscriptTail.DeriveStatus(Turn(null, "tool_use"), Fresh, null, null);
        Assert.Equal(SessionStatus.Working, status);
    }

    [Fact]
    public void NarrationTextMidStream_IsWorking()
    {
        // 67 in the corpus: text block, reason not yet known — still streaming.
        var status = TranscriptTail.DeriveStatus(Turn(null, "text"), Fresh, null, null);
        Assert.Equal(SessionStatus.Working, status);
    }

    [Fact]
    public void ThinkingThenToolUseInOneMessage_IsWorking()
    {
        // The LAST block decides, not the first.
        var status = TranscriptTail.DeriveStatus(Turn("tool_use", "thinking", "tool_use"), Fresh, null, null);
        Assert.Equal(SessionStatus.Working, status);
    }

    // ---------- what must NOT regress: genuinely finished stays finished ----------

    [Fact]
    public void EndTurnOnText_IsStillFinished()
    {
        // 267 in the corpus. A turn ends on TEXT the model signed off.
        var status = TranscriptTail.DeriveStatus(Turn("end_turn", "text"), Fresh, null, null);
        Assert.Equal(SessionStatus.Finished, status);
    }

    [Fact]
    public void StopSequenceOnText_IsStillFinished()
    {
        var status = TranscriptTail.DeriveStatus(Turn("stop_sequence", "text"), Fresh, null, null);
        Assert.Equal(SessionStatus.Finished, status);
    }

    // ---------- and the case that must never be called "working" ----------

    [Fact]
    public void OpenAskQuestion_IsNeedsYou_NotWorking()
    {
        // A pending ask tool is the agent waiting on YOU. It is a tool_use block, so
        // without an explicit carve-out the mid-turn rule would swallow exactly the
        // state the notch most needs to surface.
        var json = JsonSerializer.Serialize(new
        {
            type = "assistant",
            message = new
            {
                role = "assistant",
                content = new object[]
                {
                    new { type = "tool_use", name = "AskUserQuestion", input = new { questions = Array.Empty<object>() } },
                },
                stop_reason = "tool_use",
            },
        });
        using var doc = JsonDocument.Parse(json);
        var events = new List<JsonElement> { doc.RootElement.Clone() };

        Assert.Equal(SessionStatus.NeedsYou, TranscriptTail.DeriveStatus(events, Fresh, null, null));
        // ...and still NeedsYou, not Working, when the CLI has gone idle at the prompt.
        Assert.Equal(SessionStatus.NeedsYou,
            TranscriptTail.DeriveStatus(events, Fresh, "idle", TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void InterruptedMidThought_HandsTheTurnBack_WithoutAskingAnything()
    {
        // Esc during thinking: the transcript still ends on a thinking block, but the
        // CLI has flipped to idle. The turn is yours again, but nothing was asked.
        var status = TranscriptTail.DeriveStatus(
            Turn("tool_use", "thinking"), Fresh, "idle", TimeSpan.FromSeconds(5));
        Assert.Equal(SessionStatus.Finished, status);
    }

    [Fact]
    public void AbandonedMidThought_DoesNotClaimWorkingForever()
    {
        // No registry status to tell us otherwise, transcript frozen well past the
        // busy window: stop insisting the agent is working.
        var status = TranscriptTail.DeriveStatus(
            Turn("tool_use", "thinking"), TimeSpan.FromHours(3), null, null);
        Assert.NotEqual(SessionStatus.Working, status);
    }

    [Fact]
    public void AmbiguousTextGetsTheShortGraceOnly()
    {
        // A bare text block with no reason is only PROBABLY mid-stream. Fresh, it is
        // Working; long stale, it must not sit on a false Working for the best part
        // of an hour the way a proven mid-turn block may.
        Assert.Equal(SessionStatus.Working,
            TranscriptTail.DeriveStatus(Turn(null, "text"), TimeSpan.FromMinutes(1), null, null));
        Assert.Equal(SessionStatus.Finished,
            TranscriptTail.DeriveStatus(Turn(null, "text"), TimeSpan.FromMinutes(30), null, null));
    }

    // ---------- the block reader itself ----------

    [Fact]
    public void LastContentBlockType_ReadsTheLastBlock_AndToleratesJunk()
    {
        Assert.Equal("tool_use", TranscriptTail.LastContentBlockType(Turn("tool_use", "thinking", "tool_use")[0]));
        Assert.Equal("thinking", TranscriptTail.LastContentBlockType(Turn(null, "text", "thinking")[0]));

        using var noMessage = JsonDocument.Parse("""{"type":"assistant"}""");
        Assert.Null(TranscriptTail.LastContentBlockType(noMessage.RootElement));

        using var stringContent = JsonDocument.Parse("""{"type":"assistant","message":{"content":"plain"}}""");
        Assert.Null(TranscriptTail.LastContentBlockType(stringContent.RootElement));
    }
}
