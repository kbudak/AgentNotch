using AgentNotch.App.Sessions;
using AgentNotch.App.ViewModels;
using Xunit;

namespace AgentNotch.Tests;

/// <summary>
/// The collapsed pill shows one chip per state — a count wearing that state's
/// colour, no wording. These pin the grouping and the most-urgent-first order
/// the chips are laid out in.
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
    public void SessionsAreGroupedIntoOneChipPerState()
    {
        var parts = With(SessionStatus.Working, SessionStatus.Working, SessionStatus.Finished).CollapsedParts;

        Assert.Collection(parts,
            p => { Assert.Equal("2", p.Count); Assert.Equal("working", p.Kind); },
            p => { Assert.Equal("1", p.Count); Assert.Equal("finished", p.Kind); });
    }

    [Fact]
    public void AllThreeStatesAreKept()
    {
        var parts = With(SessionStatus.NeedsYou, SessionStatus.Working, SessionStatus.Finished).CollapsedParts;

        // Most urgent first — bare counts fit the pill, so nothing is dropped.
        Assert.Equal(new[] { "needsyou", "working", "finished" }, parts.Select(p => p.Kind));
    }

    [Fact]
    public void OnlyOneStatePresentYieldsOneChip()
    {
        var chip = Assert.Single(With(SessionStatus.NeedsYou, SessionStatus.NeedsYou).CollapsedParts);
        Assert.Equal("2", chip.Count);
        Assert.Equal("needsyou", chip.Kind);
    }
}
