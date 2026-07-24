using AgentNotch.App.Sessions;
using AgentNotch.App.ViewModels;
using Xunit;

namespace AgentNotch.Tests;

/// <summary>
/// Clicking a session row jumps to its window, so the row must sit still long
/// enough to be clicked. The watcher re-emits every second and sorts by last
/// activity, so a naive Clear()+Add() rebuilt every container and re-ordered
/// them under the pointer — which is what made a click land on the wrong
/// project, or on nothing at all.
/// </summary>
public class SessionListStabilityTests
{
    private static AgentSession Row(string id, string project, SessionStatus status = SessionStatus.Working,
                                    string? tool = null) =>
        new()
        {
            SessionId = id,
            Agent = "claude",
            ProjectName = project,
            Status = status,
            CurrentTool = tool,
            StartedUtc = new DateTime(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc),
        };

    [Fact]
    public void UnchangedSnapshot_KeepsTheSameRowInstances()
    {
        var vm = new MainViewModel();
        vm.UpdateSessions(new[] { Row("a", "belediyem"), Row("b", "limit-notch-app") });
        var first = vm.Sessions[0];
        var second = vm.Sessions[1];

        // A fresh but identical snapshot — exactly what an idle poll tick delivers.
        vm.UpdateSessions(new[] { Row("a", "belediyem"), Row("b", "limit-notch-app") });

        Assert.Same(first, vm.Sessions[0]);
        Assert.Same(second, vm.Sessions[1]);
    }

    [Fact]
    public void ChangedRow_IsReplaced_AndItsNeighbourIsNot()
    {
        var vm = new MainViewModel();
        vm.UpdateSessions(new[] { Row("a", "belediyem"), Row("b", "limit-notch-app") });
        var untouched = vm.Sessions[1];

        vm.UpdateSessions(new[] { Row("a", "belediyem", tool: "Bash"), Row("b", "limit-notch-app") });

        Assert.Equal("Bash", vm.Sessions[0].CurrentTool);
        Assert.Same(untouched, vm.Sessions[1]);
    }

    [Fact]
    public void WhileExpanded_RowsKeepTheirVisiblePosition()
    {
        var vm = new MainViewModel();
        vm.UpdateSessions(new[] { Row("a", "belediyem"), Row("b", "limit-notch-app") });
        vm.SetHover(true); // panel open — the user is aiming at a row
        Assert.True(vm.IsExpanded);

        // The watcher now ranks "b" first (it just became NeedsYou).
        vm.UpdateSessions(new[] { Row("b", "limit-notch-app", SessionStatus.NeedsYou), Row("a", "belediyem") });

        Assert.Equal("belediyem", vm.Sessions[0].ProjectName);
        Assert.Equal("limit-notch-app", vm.Sessions[1].ProjectName);
        Assert.Equal(SessionStatus.NeedsYou, vm.Sessions[1].Status); // content still refreshed
    }

    [Fact]
    public void WhileCollapsed_TheWatchersOrderWins()
    {
        var vm = new MainViewModel();
        vm.UpdateSessions(new[] { Row("a", "belediyem"), Row("b", "limit-notch-app") });
        Assert.False(vm.IsExpanded);

        vm.UpdateSessions(new[] { Row("b", "limit-notch-app", SessionStatus.NeedsYou), Row("a", "belediyem") });

        Assert.Equal("limit-notch-app", vm.Sessions[0].ProjectName);
    }

    [Fact]
    public void ShorterSnapshot_DropsTheExtraRows()
    {
        var vm = new MainViewModel();
        vm.UpdateSessions(new[] { Row("a", "one"), Row("b", "two"), Row("c", "three") });

        vm.UpdateSessions(new[] { Row("a", "one") });

        Assert.Single(vm.Sessions);
        Assert.Equal("one", vm.Sessions[0].ProjectName);
    }
}
