using AgentNotch.App.Sessions;
using AgentNotch.App.ViewModels;
using Xunit;

namespace AgentNotch.Tests;

public class BuddyStateTests
{
    [Fact]
    public void ToolExecutionAndReasoningSwitchWithoutRequiringAStatusChange()
    {
        var vm = new MainViewModel();
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);

        vm.UpdateSessions(new[] { Row(SessionStatus.Working, "exec_command") });
        Assert.Equal("working", vm.BuddyState);

        notifications.Clear();
        vm.UpdateSessions(new[] { Row(SessionStatus.Working, null) });
        Assert.Equal("thinking", vm.BuddyState);
        Assert.Contains(nameof(MainViewModel.BuddyState), notifications);

        vm.UpdateSessions(new[] { Row(SessionStatus.Working, "Thinking") });
        Assert.Equal("thinking", vm.BuddyState);
        vm.UpdateSessions(new[] { Row(SessionStatus.Finished, "exec_command") });
        Assert.Equal("finished", vm.BuddyState);
    }

    [Fact]
    public void WaitingAgentTakesPriorityOverAnotherAgentsWork()
    {
        var vm = new MainViewModel();
        vm.UpdateSessions(new[] {
            Row(SessionStatus.Working, "exec_command"),
            new AgentSession { SessionId = "question", Agent = "claude", Status = SessionStatus.NeedsYou }
        });
        Assert.Equal("needsyou", vm.BuddyState);
        Assert.Equal("claude", vm.LeadAgent);
        vm.UpdateSessions(Array.Empty<AgentSession>());
        Assert.Equal("idle", vm.BuddyState);
    }

    [Fact]
    public void BuddyProjectFollowsTheLeadSessionAndNotifiesOnChange()
    {
        var vm = new MainViewModel();
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        vm.UpdateSessions(new[] {
            new AgentSession { SessionId = "done", Agent = "codex", ProjectName = "old", Status = SessionStatus.Finished },
            new AgentSession { SessionId = "working", Agent = "codex", ProjectName = "limit-notch-app", Status = SessionStatus.Working }
        });
        Assert.Equal("limit-notch-app", vm.LeadProjectName);
        Assert.Equal("working", vm.LeadSession?.SessionId);
        Assert.Contains(nameof(MainViewModel.LeadProjectName), notifications);

        vm.UpdateSessions(Array.Empty<AgentSession>());
        Assert.Equal("", vm.LeadProjectName);
    }

    private static AgentSession Row(SessionStatus state, string? tool) => new()
    {
        SessionId = "buddy-state", Agent = "codex", Status = state, CurrentTool = tool
    };
}
