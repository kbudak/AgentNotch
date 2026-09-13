using System.Text.Json;
using AgentNotch.App.Sessions;
using Xunit;

namespace AgentNotch.Tests;

/// <summary>
/// A finished session must leave the notch and STAY gone.
///
/// It did not, because "how long since this session did anything" was read off the
/// transcript's file mtime — and Claude Code keeps appending bookkeeping to a
/// transcript long after the conversation is over: `bridge-session` records, and
/// `system`/`local_command` notices such as Remote Control connecting and
/// disconnecting. Each one touches the file, so mtime jumps to now, the age resets,
/// the dropped session comes back, and — since its last real turn was an old
/// end_turn — it reappears reading "Done". Observed live: a session whose last real
/// turn was two days old carried an mtime nine minutes old.
///
/// Every record shape below is taken from a real transcript.
/// </summary>
public sealed class StaleSessionTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "agentnotch-stale-" + Guid.NewGuid().ToString("N"));

    public StaleSessionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ---------- fixtures ----------

    private static string Assistant(DateTime whenUtc, string text = "All done.") =>
        JsonSerializer.Serialize(new
        {
            type = "assistant",
            timestamp = whenUtc.ToString("o"),
            message = new
            {
                role = "assistant",
                content = new object[] { new { type = "text", text } },
                stop_reason = "end_turn",
            },
        });

    /// <summary>The Remote Control notice that kept resurrecting finished sessions.</summary>
    private static string LocalCommand(DateTime whenUtc) =>
        JsonSerializer.Serialize(new
        {
            type = "system",
            subtype = "local_command",
            level = "info",
            timestamp = whenUtc.ToString("o"),
            content = "<local-command-stdout>Remote Control disconnected.</local-command-stdout>",
        });

    private static string BridgeSession() =>
        JsonSerializer.Serialize(new { type = "bridge-session" });

    private string Home(string name, params string[] lines)
    {
        var home = Path.Combine(_dir, name);
        var sessions = Path.Combine(home, "sessions");
        var project = Path.Combine(home, "projects", "C--encoded");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(project);

        File.WriteAllText(Path.Combine(sessions, "4242.json"), JsonSerializer.Serialize(new
        {
            pid = 4242,
            sessionId = "s1",
            cwd = Path.Combine(_dir, "repos", "projA"),
            entrypoint = "cli",
        }));
        File.WriteAllText(Path.Combine(project, "s1.jsonl"), string.Join("\n", lines) + "\n");
        return home;
    }

    private static List<AgentSession> Snapshot(string home) =>
        TranscriptTail.BuildSnapshot(
            home, new Dictionary<string, TranscriptTail.SpendState>(), _ => true, DateTime.UtcNow);

    // ---------- the bug ----------

    [Fact]
    public void FinishedTwoDaysAgo_StaysGone_WhenBookkeepingTouchesTheFile()
    {
        var now = DateTime.UtcNow;
        // Last real turn two days ago; the file itself was written a moment ago.
        var home = Home("ghost",
            Assistant(now.AddDays(-2)),
            BridgeSession(),
            LocalCommand(now.AddSeconds(-5)));

        Assert.Empty(Snapshot(home));
    }

    [Fact]
    public void FinishedLongAgo_StaysGone_EvenWithTheFileJustRewritten()
    {
        var now = DateTime.UtcNow;
        var home = Home("ghost2", Assistant(now.AddHours(-3)), LocalCommand(now));

        // Touch it explicitly: mtime is now, yet nothing has happened in three hours.
        File.SetLastWriteTimeUtc(Path.Combine(home, "projects", "C--encoded", "s1.jsonl"), now);

        Assert.Empty(Snapshot(home));
    }

    // ---------- what must still work ----------

    [Fact]
    public void JustFinished_IsStillShown_SoYouCanSeeAndJumpToIt()
    {
        var home = Home("fresh", Assistant(DateTime.UtcNow.AddMinutes(-1)));

        var s = Assert.Single(Snapshot(home));
        Assert.Equal(SessionStatus.Finished, s.Status);
    }

    [Fact]
    public void BookkeepingAfterARecentTurn_DoesNotDisturbIt()
    {
        var now = DateTime.UtcNow;
        var home = Home("fresh2", Assistant(now.AddMinutes(-2)), BridgeSession(), LocalCommand(now));

        Assert.Single(Snapshot(home));
    }

    // ---------- the reader itself ----------

    [Fact]
    public void LastTurnTimeUtc_IgnoresEverythingThatIsNotATurn()
    {
        var turnAt = new DateTime(2026, 9, 7, 20, 22, 36, DateTimeKind.Utc);
        var events = TranscriptTail.ParseLines(string.Join("\n",
            Assistant(turnAt),
            BridgeSession(),
            LocalCommand(turnAt.AddDays(2))));

        Assert.Equal(turnAt, TranscriptTail.LastTurnTimeUtc(events));
    }

    [Fact]
    public void LastTurnTimeUtc_IsNullWhenTheTailHoldsNoTurn()
    {
        var events = TranscriptTail.ParseLines(
            string.Join("\n", BridgeSession(), LocalCommand(DateTime.UtcNow)));

        Assert.Null(TranscriptTail.LastTurnTimeUtc(events));
    }

    [Fact]
    public void AFutureDatedRecord_CannotMakeASessionLookEternallyFresh()
    {
        // Clock skew between machines must not pin a session to "just active".
        var home = Home("skew", Assistant(DateTime.UtcNow.AddDays(-2)),
                        Assistant(DateTime.UtcNow.AddHours(6)));

        // The future stamp is clamped to now, so the session counts as active rather
        // than negative-aged — it must not crash or invert, and it stays visible.
        var list = Snapshot(home);
        Assert.All(list, s => Assert.True(s.LastActivityUtc <= DateTime.UtcNow.AddSeconds(5)));
    }
}
