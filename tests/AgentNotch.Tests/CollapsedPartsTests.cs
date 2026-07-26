using AgentNotch.App.Sessions;
using AgentNotch.App.ViewModels;
using Xunit;

namespace AgentNotch.Tests;

/// <summary>
/// The collapsed pill is 248px wide and the summary shares that row with the
/// agent marks on its left, so the segment list has to stay short. These pin the
/// priority order and the cap — three segments at once used to run into the marks.
/// </summary>
public class CollapsedPartsTests
{
    private static AgentSession Row(SessionStatus status) =>
        new() { SessionId = Guid.NewGuid().ToString(), Agent = "claude", Status = status };

    private static MainViewModel With(params SessionStatus[] statuses)
    {
        var vm = new MainViewModel();
        vm.UpdateSessions(Array.ConvertAll(statuses, Row));
        return vm;
    }

    [Fact]
    public void IdleShowsNothing()
        => Assert.Empty(new MainViewModel().CollapsedParts);

    [Fact]
    public void CountsAreGroupedAndColouredByState()
    {
        var parts = With(SessionStatus.Working, SessionStatus.Working, SessionStatus.Finished).CollapsedParts;

        Assert.Collection(parts,
            p => { Assert.Equal("2", p.Count); Assert.Equal("working", p.Word); Assert.Equal("working", p.Kind); },
            p => { Assert.Equal("1", p.Count); Assert.Equal("done", p.Word); Assert.Equal("finished", p.Kind); });
    }

    [Fact]
    public void SingleNeedsYouReadsAsSingular()
    {
        var parts = With(SessionStatus.NeedsYou).CollapsedParts;
        Assert.Equal("needs you", Assert.Single(parts).Word);
    }

    [Fact]
    public void AllThreeStatesKeepTheTwoMostUrgent()
    {
        var parts = With(SessionStatus.NeedsYou, SessionStatus.Working, SessionStatus.Finished).CollapsedParts;

        Assert.Equal(2, parts.Count);
        Assert.Equal("needsyou", parts[0].Kind);
        Assert.Equal("working", parts[1].Kind);
        Assert.DoesNotContain(parts, p => p.Kind == "finished"); // dropped: least actionable
    }
}
