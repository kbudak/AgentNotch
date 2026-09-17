using AgentNotch.App.Usage;
using Xunit;

namespace AgentNotch.Tests;

/// <summary>
/// The header showed "5h 3% now · 7d 69% now" for hours while Claude's own panel
/// said 13% and 3%: the persisted snapshot outlived its windows, and each reset time
/// in the past rendered as "now" next to a percentage from the window that had ended.
/// </summary>
public sealed class UsageRolloverTests
{
    private static readonly DateTime Now = new(2026, 9, 14, 9, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void WindowStillRunning_IsLeftAlone()
    {
        var windows = new[] { new UsageWindow("5h", 19, Now.AddHours(2)) };

        var shown = ClaudeUsageClient.RollOver(windows, Now);

        Assert.Same(windows, shown); // nothing expired: no copy, no change
    }

    [Fact]
    public void WindowPastItsReset_IsShownAsTheFreshWindow_NotTheOldPercentage()
    {
        // The exact snapshot that was stuck on screen: resets at 03:00/03:10, read at 09:30.
        var shown = ClaudeUsageClient.RollOver(new[]
        {
            new UsageWindow("5h", 19, new DateTime(2026, 9, 14, 3, 10, 0, DateTimeKind.Utc)),
            new UsageWindow("7d", 80, new DateTime(2026, 9, 14, 3, 0, 0, DateTimeKind.Utc)),
        }, Now);

        Assert.All(shown, w =>
        {
            Assert.Equal(0, w.UsedPct);
            Assert.Null(w.ResetsAtUtc);
            Assert.NotEqual("now", w.ResetText);
        });
        Assert.Equal(new[] { "5h", "7d" }, shown.Select(w => w.Label));
    }

    [Fact]
    public void OnlyTheExpiredWindowRollsOver()
    {
        var shown = ClaudeUsageClient.RollOver(new[]
        {
            new UsageWindow("5h", 19, Now.AddMinutes(-5)),
            new UsageWindow("7d", 42, Now.AddDays(3)),
        }, Now);

        Assert.Equal(0, shown[0].UsedPct);
        Assert.Equal(42, shown[1].UsedPct);
        Assert.Equal(Now.AddDays(3), shown[1].ResetsAtUtc);
    }

    [Fact]
    public void WindowWithNoResetTime_IsNeverRolledOver()
    {
        var shown = ClaudeUsageClient.RollOver(new[] { new UsageWindow("5h", 55, null) }, Now);
        Assert.Equal(55, shown[0].UsedPct);
    }
}
