using System.Text.Json;
using AgentNotch.App.Ipc;
using AgentNotch.App.ViewModels;
using AgentNotch.Protocol;
using Xunit;

namespace AgentNotch.Tests;

/// <summary>
/// Cursor's hook payloads use different field names than Claude Code's. They are
/// normalised once in <see cref="HookRequest"/>, so everything downstream — most
/// visibly <see cref="PromptViewModel"/> — keeps working unchanged. These tests
/// pin that seam: change the mapping and the prompt silently goes blank.
/// </summary>
public sealed class CursorHookPayloadTests
{
    private static HookRequest Request(string mode, object hookEvent) => new(
        new PipeEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Mode = mode,
            ExpectsReply = mode is Proto.ModeCursorPermission
                            or Proto.ModePermissionRequest or Proto.ModeAskUserQuestion,
            Event = JsonSerializer.SerializeToElement(hookEvent),
        },
        responder: null);

    [Fact]
    public void BeforeShellExecution_MapsOntoTheBashPromptRenderer()
    {
        var req = Request(Proto.ModeCursorPermission, new
        {
            conversation_id = "conv-42",
            generation_id = "gen-7",
            hook_event_name = "beforeShellExecution",
            workspace_roots = new[] { @"C:\work\api" },
            command = "npm run deploy -- --prod\necho done",
            cwd = @"C:\work\api",
        });

        Assert.Equal("conv-42", req.SessionId);
        Assert.Equal(@"C:\work\api", req.Cwd);
        // No tool_name in the payload — a bare `command` IS a shell call.
        Assert.Equal("Bash", req.ToolName);

        var vm = new PromptViewModel(req);
        Assert.Equal(PromptKind.Permission, vm.Kind);
        Assert.Equal("npm run deploy -- --prod\necho done", vm.CommandText);
        Assert.Equal("npm run deploy -- --prod", vm.Title); // first line
        Assert.Equal("api", vm.Subtitle);
    }

    [Fact]
    public void BeforeShellExecution_FallsBackToWorkspaceRootWhenCwdIsAbsent()
    {
        var req = Request(Proto.ModeCursorPermission, new
        {
            conversation_id = "conv-1",
            hook_event_name = "beforeShellExecution",
            workspace_roots = new[] { @"C:\work\shop", @"C:\work\other" },
            command = "rm -rf build",
        });

        Assert.Equal(@"C:\work\shop", req.Cwd);
        Assert.Equal("shop", new PromptViewModel(req).Subtitle);
    }

    [Fact]
    public void BeforeMcpExecution_KeepsItsToolNameAndInput()
    {
        var req = Request(Proto.ModeCursorPermission, new
        {
            conversation_id = "conv-9",
            hook_event_name = "beforeMCPExecution",
            tool_name = "linear_create_issue",
            tool_input = new { title = "Ship it", team = "core" },
        });

        Assert.Equal("linear_create_issue", req.ToolName);
        Assert.Equal("Ship it", req.ToolInput!.Value.GetProperty("title").GetString());

        // No special renderer for MCP tools — the generic JSON fallback shows the input.
        var vm = new PromptViewModel(req);
        Assert.Equal("linear_create_issue", vm.Title);
        Assert.Contains("Ship it", vm.GenericJson);
    }

    [Fact]
    public void ClaudeCodePayload_StillWinsOverTheCursorFallbacks()
    {
        var req = Request(Proto.ModePermissionRequest, new
        {
            session_id = "claude-session",
            conversation_id = "should-lose",
            cwd = @"C:\work\api",
            workspace_roots = new[] { @"C:\should\lose" },
            hook_event_name = "PermissionRequest",
            tool_name = "Edit",
            tool_input = new { file_path = @"C:\work\api\app.ts", old_string = "a", new_string = "b" },
        });

        Assert.Equal("claude-session", req.SessionId);
        Assert.Equal(@"C:\work\api", req.Cwd);
        Assert.Equal("Edit", req.ToolName);
        Assert.Equal("Edit app.ts", new PromptViewModel(req).Title);
    }

    /// <summary>
    /// The regression that matters most: a blocking request the UI does not queue
    /// is never answered, so the agent hangs for the helper's whole timeout. The
    /// queue gate must follow ExpectsReply — the same flag PipeServer uses to keep
    /// the connection open — and never a hand-maintained list of modes.
    /// </summary>
    [Theory]
    [InlineData(Proto.ModeCursorPermission)]
    [InlineData(Proto.ModePermissionRequest)]
    [InlineData(Proto.ModeAskUserQuestion)]
    public void EveryBlockingMode_IsQueuedAsAPrompt(string mode)
    {
        var vm = new MainViewModel();
        vm.AddHookRequest(Request(mode, new
        {
            conversation_id = "c",
            hook_event_name = "beforeShellExecution",
            command = "npm test",
        }));

        Assert.Equal(1, vm.QueueCount);
        Assert.NotNull(vm.ActivePrompt);
        Assert.True(vm.IsExpanded); // a waiting agent must open the notch
        Assert.Equal("prompt", vm.StateKind);
    }

    [Theory]
    [InlineData(Proto.ModeCursorNotify)]
    [InlineData(Proto.ModeNotify)]
    [InlineData(Proto.ModeStop)]
    [InlineData(Proto.ModePrompt)]
    public void ObserveOnlyModes_NeverQueueACard(string mode)
    {
        var vm = new MainViewModel();
        vm.AddHookRequest(Request(mode, new { conversation_id = "c", hook_event_name = "stop" }));

        Assert.Equal(0, vm.QueueCount);
        Assert.False(vm.IsExpanded);
    }

    /// <summary>
    /// The exact shape Cursor writes for a native AskQuestion — taken from a real
    /// transcript. It agrees with Claude Code on structure and disagrees on every
    /// field name: prompt/title/id where Claude writes question/header/label.
    /// </summary>
    [Fact]
    public void CursorAskQuestion_RendersAsAnAnswerableQuestion_NotRawJson()
    {
        var req = Request(Proto.ModeCursorPermission, new
        {
            conversation_id = "conv-5",
            hook_event_name = "preToolUse",
            tool_name = "AskQuestion",
            tool_input = new
            {
                title = "Desktop ödeme görünümü tercihi",
                questions = new object[]
                {
                    new
                    {
                        id = "integration_mode",
                        prompt = "Desktop için iyzico görünümünü hangi yöntemle düzeltelim?",
                        options = new object[]
                        {
                            new { id = "popup_keep", label = "Mevcut popup kalsın" },
                            new { id = "inline", label = "Sayfa içine gömülsün" },
                        },
                    },
                },
            },
        });

        var vm = new PromptViewModel(req);
        Assert.Equal(PromptKind.Question, vm.Kind);
        Assert.Equal("Desktop için iyzico görünümünü hangi yöntemle düzeltelim?", vm.QuestionText);
        Assert.Equal("Desktop ödeme görünümü tercihi", vm.Header);
        Assert.Equal(2, vm.Options.Count);
        Assert.Equal("Mevcut popup kalsın", vm.Options[0].Label);
        Assert.Equal("Ctrl+1", vm.Options[0].HotkeyText);
        Assert.Equal("", vm.GenericJson); // NOT the raw-JSON fallback
    }

    [Fact]
    public void ClaudeQuestionDialect_StillParses()
    {
        var req = Request(Proto.ModeAskUserQuestion, new
        {
            session_id = "s1",
            hook_event_name = "PreToolUse",
            tool_name = "AskUserQuestion",
            tool_input = new
            {
                questions = new object[]
                {
                    new
                    {
                        question = "Which deployment target?",
                        header = "Deploy",
                        multiSelect = true,
                        options = new object[]
                        {
                            new { label = "Production", description = "The live environment" },
                        },
                    },
                },
            },
        });

        var vm = new PromptViewModel(req);
        Assert.Equal(PromptKind.Question, vm.Kind);
        Assert.Equal("Which deployment target?", vm.QuestionText);
        Assert.Equal("Deploy", vm.Header);
        Assert.True(vm.MultiSelect);
        Assert.Equal("The live environment", vm.Options[0].Description);
    }

    [Fact]
    public void AskToolWithNoParsableOptions_FallsBackToAllowDeny()
    {
        var req = Request(Proto.ModeCursorPermission, new
        {
            conversation_id = "c",
            hook_event_name = "preToolUse",
            tool_name = "AskQuestion",
            tool_input = new { somethingUnexpected = "shape we have never seen" },
        });

        var vm = new PromptViewModel(req);
        // Better a plain approval than a question card with no question and no buttons.
        Assert.Equal(PromptKind.Permission, vm.Kind);
        Assert.Contains("shape we have never seen", vm.GenericJson);
    }

    [Fact]
    public void MissingCommandAndToolName_DegradesInsteadOfThrowing()
    {
        var req = Request(Proto.ModeCursorPermission, new
        {
            conversation_id = "conv-0",
            hook_event_name = "beforeShellExecution",
        });

        Assert.Equal("", req.ToolName);
        Assert.Null(req.ToolInput);
        Assert.Equal("Agent request", new PromptViewModel(req).Title);
    }
}
