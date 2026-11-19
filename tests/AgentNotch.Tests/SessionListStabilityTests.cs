using AgentNotch.App.Sessions;
using AgentNotch.App.Usage;
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
        vm.UpdateSessions(new[] { Row("a", "demo-app"), Row("b", "agent-notch") });
        var first = vm.Sessions[0];
        var second = vm.Sessions[1];

        // A fresh but identical snapshot — exactly what an idle poll tick delivers.
        vm.UpdateSessions(new[] { Row("a", "demo-app"), Row("b", "agent-notch") });

        Assert.Same(first, vm.Sessions[0]);
        Assert.Same(second, vm.Sessions[1]);
    }

    [Fact]
    public void ChangedRow_IsReplaced_AndItsNeighbourIsNot()
    {
        var vm = new MainViewModel();
        vm.UpdateSessions(new[] { Row("a", "demo-app"), Row("b", "agent-notch") });
        var untouched = vm.Sessions[1];

        vm.UpdateSessions(new[] { Row("a", "demo-app", tool: "Bash"), Row("b", "agent-notch") });

        Assert.Equal("Bash", vm.Sessions[0].CurrentTool);
        Assert.Same(untouched, vm.Sessions[1]);
    }

    [Fact]
    public void WhileExpanded_RowsKeepTheirVisiblePosition()
    {
        var vm = new MainViewModel();
        vm.UpdateSessions(new[] { Row("a", "demo-app"), Row("b", "agent-notch") });
        vm.SetHover(true); // panel open — the user is aiming at a row
        Assert.True(vm.IsExpanded);

        // The watcher now ranks "b" first (it just became NeedsYou).
        vm.UpdateSessions(new[] { Row("b", "agent-notch", SessionStatus.NeedsYou), Row("a", "demo-app") });

        Assert.Equal("demo-app", vm.Sessions[0].ProjectName);
        Assert.Equal("agent-notch", vm.Sessions[1].ProjectName);
        Assert.Equal(SessionStatus.NeedsYou, vm.Sessions[1].Status); // content still refreshed
    }

    [Fact]
    public void WhileCollapsed_TheWatchersOrderWins()
    {
        var vm = new MainViewModel();
        vm.UpdateSessions(new[] { Row("a", "demo-app"), Row("b", "agent-notch") });
        Assert.False(vm.IsExpanded);

        vm.UpdateSessions(new[] { Row("b", "agent-notch", SessionStatus.NeedsYou), Row("a", "demo-app") });

        Assert.Equal("agent-notch", vm.Sessions[0].ProjectName);
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

    [Fact]
    public void UnchangedSnapshot_DoesNotRaisePropertyChanged()
    {
        var vm = new MainViewModel();
        vm.UpdateSessions(new[] { Row("a", "demo-app") });

        int notifications = 0;
        vm.PropertyChanged += (_, _) => notifications++;

        vm.UpdateSessions(new[] { Row("a", "demo-app") });

        Assert.Equal(0, notifications);
    }

    [Fact]
    public void UnchangedUsage_DoesNotRebuildOrRaisePropertyChanged()
    {
        var vm = new MainViewModel();
        var reset = DateTime.UtcNow.AddHours(2);
        vm.UpdateUsage(new[] { new UsageWindow("5h", 25, reset) });
        var original = vm.Usage[0];

        int notifications = 0;
        vm.PropertyChanged += (_, _) => notifications++;

        vm.UpdateUsage(new[] { new UsageWindow("5h", 25, reset) });

        Assert.Same(original, vm.Usage[0]);
        Assert.Equal(0, notifications);
    }
}
