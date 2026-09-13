using System.Text.Json;
using AgentNotch.App.Diagnostics;
using AgentNotch.App.Ipc;
using AgentNotch.App.UI;
using AgentNotch.Protocol;

namespace AgentNotch.App.ViewModels;

public enum PromptKind { Permission, Question }

/// <summary>One selectable answer of an AskUserQuestion prompt.</summary>
public sealed record QuestionOption(int Index, string Label, string Description, string HotkeyText);

/// <summary>
/// Immutable presentation model for a single blocking hook request (permission
/// or question). All parsing happens once in the constructor and is fully
/// defensive — a malformed tool_input degrades to the generic JSON fallback,
/// never a throw.
/// </summary>
public sealed class PromptViewModel
{
    public HookRequest Request { get; }
    public PromptKind Kind { get; private set; }
    public string ToolName { get; }

    /// <summary>"Edit src\auth\middleware.ts" — or the Bash command's first line.</summary>
    public string Title { get; }

    /// <summary>"{project folder} · {permission_mode}".</summary>
    public string Subtitle { get; }

    public IReadOnlyList<DiffLine> DiffLines { get; }
    public string DiffStats { get; }     // "+3 -1" ("" when no diff)
    public string DiffAdded { get; }     // "+3" ("" when none)
    public string DiffRemoved { get; }   // "-1" ("" when none)

    /// <summary>Full Bash command ("" for non-Bash prompts).</summary>
    public string CommandText { get; }

    /// <summary>Compact raw JSON fallback for tools without a special renderer ("" otherwise).</summary>
    public string GenericJson { get; }

    public string QuestionText { get; }
    public string Header { get; }
    public IReadOnlyList<QuestionOption> Options { get; }
    public bool MultiSelect { get; }

    public PromptViewModel(HookRequest request)
    {
        Request = request;

        // Claude Code announces a question through its own helper mode. Cursor has
        // no such event — its AskQuestion arrives as an ordinary blocking tool call
        // — so the SHAPE of the request decides, not the mode. A question tool whose
        // input yields no options falls back to Permission below, so the user can
        // still allow/deny rather than face an empty card.
        bool questionMode = string.Equals(request.Mode, Proto.ModeAskUserQuestion, StringComparison.OrdinalIgnoreCase)
                            || IsAskTool(request.ToolName);
        Kind = questionMode ? PromptKind.Question : PromptKind.Permission;

        string toolName = "", title = "", subtitle = "";
        string command = "", generic = "", questionText = "", header = "";
        bool multi = false;
        IReadOnlyList<DiffLine> diff = Array.Empty<DiffLine>();
        IReadOnlyList<QuestionOption> options = Array.Empty<QuestionOption>();

        try
        {
            toolName = request.ToolName;
            string cwd = request.Cwd;
            string project = LastSegment(cwd);

            var subParts = new List<string>(2);
            if (project.Length > 0) subParts.Add(project);
            if (!string.IsNullOrEmpty(request.PermissionMode)) subParts.Add(request.PermissionMode);
            subtitle = string.Join(" · ", subParts);

            JsonElement? input = request.ToolInput;

            if (Kind == PromptKind.Question)
            {
                (questionText, header, options, multi) = ParseQuestion(input);
                if (options.Count == 0 && questionText.Length == 0)
                {
                    // Nothing renderable — treat it as a plain approval instead of
                    // showing a question card with no question and no buttons.
                    Kind = PromptKind.Permission;
                    generic = input is { } raw ? Trunc(raw.GetRawText(), 1000) : "";
                }
                else
                {
                    title = header.Length > 0 ? header : "Question";
                }
            }

            if (Kind == PromptKind.Permission && generic.Length == 0)
            {
                switch (toolName)
                {
                    case "Edit":
                    {
                        string filePath = GetStr(input, "file_path");
                        diff = DiffRenderer.Render(GetStr(input, "old_string"), GetStr(input, "new_string"));
                        title = ("Edit " + RelPath(filePath, cwd)).Trim();
                        break;
                    }
                    case "Write":
                    {
                        string filePath = GetStr(input, "file_path");
                        diff = DiffRenderer.RenderAdded(GetStr(input, "content"));
                        title = ("Write " + RelPath(filePath, cwd)).Trim();
                        break;
                    }
                    case "Bash":
                    {
                        command = GetStr(input, "command");
                        string first = FirstLine(command);
                        title = first.Length > 0 ? Trunc(first, 72) : "Bash";
                        break;
                    }
                    default:
                    {
                        generic = input is { } el ? Trunc(el.GetRawText(), 1000) : "";
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("PromptViewModel parse: " + ex.Message);
        }

        if (title.Length == 0)
            title = toolName.Length > 0 ? toolName : "Agent request";

        ToolName = toolName;
        Title = title;
        Subtitle = subtitle;
        DiffLines = diff;
        CommandText = command;
        GenericJson = generic;
        QuestionText = questionText;
        Header = header;
        Options = options;
        MultiSelect = multi;

        int added = 0, removed = 0;
        foreach (var line in diff)
        {
            if (line.Kind == "add") added++;
            else if (line.Kind == "del") removed++;
        }
        DiffAdded = added > 0 ? $"+{added}" : "";
        DiffRemoved = removed > 0 ? $"-{removed}" : "";
        DiffStats = added + removed > 0 ? $"+{added} -{removed}" : "";
    }

    /// <summary>
    /// Tool names that mean "the agent is asking YOU something" — Claude Code's
    /// AskUserQuestion, Cursor's native AskQuestion, Antigravity's ask_question.
    /// Kept in step with <see cref="Sessions.CursorTail"/>'s own ask detection.
    /// </summary>
    internal static bool IsAskTool(string? tool) =>
        !string.IsNullOrEmpty(tool)
        && (tool.Contains("AskQuestion", StringComparison.OrdinalIgnoreCase)
            || tool.Contains("AskUserQuestion", StringComparison.OrdinalIgnoreCase)
            || tool.Equals("ask_question", StringComparison.OrdinalIgnoreCase)
            || tool.Equals("cursor_dialog", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// One question + its options, out of either dialect. The agents agree on the
    /// structure (an array of questions, each with an array of options) and disagree
    /// on every field name: Claude Code writes question/header/label, Cursor writes
    /// prompt/title/label with an id, Antigravity writes question/title. Reading a
    /// small set of aliases costs nothing and stops a renamed field from silently
    /// producing an empty card.
    /// </summary>
    private static (string Question, string Header, IReadOnlyList<QuestionOption> Options, bool Multi)
        ParseQuestion(JsonElement? input)
    {
        string questionText = "", header = "";
        bool multi = false;
        var list = new List<QuestionOption>();

        if (input is { ValueKind: JsonValueKind.Object } obj &&
            obj.TryGetProperty("questions", out var questions) &&
            questions.ValueKind == JsonValueKind.Array &&
            questions.GetArrayLength() > 0)
        {
            var q = questions[0]; // single-question MVP
            questionText = FirstStr(q, "question", "prompt", "text");
            // Cursor puts the heading on the call, not on each question.
            header = FirstStr(q, "header", "title");
            if (header.Length == 0) header = FirstStr(obj, "title", "header");
            multi = IsTrue(q, "multiSelect") || IsTrue(q, "multi_select") || IsTrue(q, "multiple");

            if (q.ValueKind == JsonValueKind.Object &&
                q.TryGetProperty("options", out var opts) &&
                opts.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (var opt in opts.EnumerateArray())
                {
                    string label = opt.ValueKind == JsonValueKind.String
                        ? opt.GetString() ?? ""
                        : FirstStr(opt, "label", "name", "text", "id");
                    string description = opt.ValueKind == JsonValueKind.Object
                        ? FirstStr(opt, "description", "detail", "hint")
                        : "";
                    if (label.Length == 0) label = $"Option {i + 1}";
                    list.Add(new QuestionOption(i, label, description, i < 9 ? $"Ctrl+{i + 1}" : ""));
                    i++;
                }
            }
        }

        return (questionText, header, list, multi);
    }

    /// <summary>First of <paramref name="names"/> present as a non-empty string.</summary>
    private static string FirstStr(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (GetStr(element, name) is { Length: > 0 } value) return value;
        return "";
    }

    private static bool IsTrue(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var prop)
           && prop.ValueKind == JsonValueKind.True;

    private static string GetStr(JsonElement? element, string name)
    {
        try
        {
            if (element is { ValueKind: JsonValueKind.Object } obj &&
                obj.TryGetProperty(name, out var prop) &&
                prop.ValueKind == JsonValueKind.String)
                return prop.GetString() ?? "";
        }
        catch { /* tolerate */ }
        return "";
    }

    /// <summary>Path relative to cwd when underneath it; otherwise the path unchanged.</summary>
    private static string RelPath(string path, string cwd)
    {
        try
        {
            if (path.Length == 0 || cwd.Length == 0) return path;
            if (!Path.IsPathRooted(path)) return path;

            string full = Path.GetFullPath(path);
            string root = Path.GetFullPath(cwd).TrimEnd('\\', '/');
            if (root.Length > 0 &&
                full.Length > root.Length + 1 &&
                full.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                (full[root.Length] == '\\' || full[root.Length] == '/'))
            {
                return full[(root.Length + 1)..];
            }
        }
        catch { /* tolerate */ }
        return path;
    }

    private static string LastSegment(string path)
    {
        try
        {
            string trimmed = path.TrimEnd('\\', '/');
            int i = trimmed.LastIndexOfAny(new[] { '\\', '/' });
            return i >= 0 ? trimmed[(i + 1)..] : trimmed;
        }
        catch { return path; }
    }

    private static string FirstLine(string text)
    {
        int i = text.IndexOfAny(new[] { '\r', '\n' });
        return (i >= 0 ? text[..i] : text).Trim();
    }

    private static string Trunc(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}
