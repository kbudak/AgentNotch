using AgentNotch.App.Usage;
using Xunit;

namespace AgentNotch.Tests;

/// <summary>
/// When Claude Code's token has expired, AgentNotch runs `claude -p /usage` so Claude
/// Code renews it. The run takes seconds and a start-up's worth of work, so it must
/// not repeat on every 2-second poll for as long as the token stays expired.
/// </summary>
public sealed class UsageTokenRenewTests
{
    private static readonly DateTime Now = new(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NeverTried_RenewsAtOnce()
        => Assert.True(ClaudeUsageClient.ShouldRenew(DateTime.MinValue, Now));

    [Theory]
    [InlineData(0)]
    [InlineData(2)]      // the next poll
    [InlineData(1799)]   // just under the interval
    public void RecentAttempt_DoesNotRepeat(int secondsAgo)
        => Assert.False(ClaudeUsageClient.ShouldRenew(Now.AddSeconds(-secondsAgo), Now));

    [Fact]
    public void AfterTheInterval_TriesAgain()
        => Assert.True(ClaudeUsageClient.ShouldRenew(Now - ClaudeUsageClient.RenewEvery, Now));
}
