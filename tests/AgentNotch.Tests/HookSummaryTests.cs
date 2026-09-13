using System.Text.Json;
using AgentNotch.Protocol;
using Xunit;

namespace AgentNotch.Tests;

/// <summary>
/// The lines the hook helper echoes onto the terminal while the notch holds a
/// question. Claude Code will not draw its own prompt while a hook is pending, so
/// without this the terminal simply goes quiet and you cannot tell anything is
/// waiting. These pin what that echo says — in both hook dialects.
/// </summary>
public sealed class HookSummaryTests
{
    private static (string Title, IReadOnlyList<string> Detail) Describe(string mode, object evt)
        => HookSummary.Describe(mode, JsonSerializer.SerializeToElement(evt));

    [Fact]
    public void EditPermission_NamesTheFile_NotItsWholePath()
    {
        var (title, detail) = Describe(Proto.ModePermissionRequest, new
        {
            hook_event_name = "PermissionRequest",
            tool_name = "Edit",
            tool_input = new { file_path = @"C:\work\api\src\auth\middleware.ts" },
        });

        Assert.Equal("Permission request", title);
        Assert.Equal("Edit middleware.ts", Assert.Single(detail));
    }

    [Fact]
    public void ShellPermission_ShowsTheFirstLineOfTheCommand()
    {
        var (title, detail) = Describe(Proto.ModeCursorPermission, new
        {
            hook_event_name = "beforeShellExecution",
            command = "npm run deploy -- --prod\necho done",
        });

        Assert.Equal("Permission request", title);
        Assert.Equal("npm run deploy -- --prod", Assert.Single(detail));
    }

    [Fact]
    public void ClaudeQuestion_ListsTheOptionsNumberedAsTheNotchNumbersThem()
    {
        var (title, detail) = Describe(Proto.ModeAskUserQuestion, new
        {
            tool_name = "AskUserQuestion",
            tool_input = new
            {
                questions = new object[]
                {
                    new
                    {
                        question = "Which deployment target?",
                        header = "Deploy",
                        options = new object[]
                        {
                            new { label = "Production" },
                            new { label = "Staging" },
                        },
                    },
                },
            },
        });

        Assert.Equal("Question", title);
        Assert.Equal("Which deployment target?", detail[0]);
        Assert.Equal("  1. Production", detail[1]);
        Assert.Equal("  2. Staging", detail[2]);
    }

    [Fact]
    public void CursorQuestionDialect_ReadsPromptAndIdLabels()
    {
        // Cursor writes prompt/title/id where Claude Code writes question/header/label.
        var (title, detail) = Describe(Proto.ModeCursorPermission, new
        {
            tool_name = "AskQuestion",
            tool_input = new
            {
                title = "Odeme gorunumu",
                questions = new object[]
                {
                    new
                    {
                        id = "mode",
                        prompt = "Hangi yontemle duzeltelim?",
                        options = new object[]
                        {
                            new { id = "popup", label = "Mevcut popup kalsin" },
                        },
                    },
                },
            },
        });

        Assert.Equal("Question", title);
        Assert.Equal("Hangi yontemle duzeltelim?", detail[0]);
        Assert.Equal("  1. Mevcut popup kalsin", detail[1]);
    }

    [Fact]
    public void QuestionWithNoTextFallsBackToTheCallTitle()
    {
        var (_, detail) = Describe(Proto.ModeCursorPermission, new
        {
            tool_name = "AskQuestion",
            tool_input = new
            {
                title = "Scope check",
                questions = new object[] { new { options = new object[] { new { label = "Yes" } } } },
            },
        });

        Assert.Equal("Scope check", detail[0]);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"tool_name":"Weird","tool_input":{"nested":{"deep":true}}}""")]
    [InlineData("""{"tool_name":"AskQuestion","tool_input":{"questions":[]}}""")]
    [InlineData("""{"tool_input":"a string, not an object"}""")]
    public void UnfamiliarPayloads_DegradeQuietly(string json)
    {
        // This runs inside a hook. A shape we have not seen must produce a short line,
        // never an exception that would take the agent's permission flow down with it.
        using var doc = JsonDocument.Parse(json);
        var (title, detail) = HookSummary.Describe(Proto.ModePermissionRequest, doc.RootElement);

        Assert.False(string.IsNullOrWhiteSpace(title));
        Assert.NotNull(detail);
    }

    [Fact]
    public void LongQuestionText_IsTrimmed_SoTheTerminalGetsOneLine()
    {
        var (_, detail) = Describe(Proto.ModeAskUserQuestion, new
        {
            tool_name = "AskUserQuestion",
            tool_input = new
            {
                questions = new object[] { new { question = new string('x', 400) } },
            },
        });

        Assert.True(detail[0].Length <= 141, $"echo line too long: {detail[0].Length}");
        Assert.EndsWith("…", detail[0]);
    }
}
