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
    public PromptKind Kind { get; }
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
        Kind = string.Equals(request.Mode, Proto.ModeAskUserQuestion, StringComparison.OrdinalIgnoreCase)
            ? PromptKind.Question
            : PromptKind.Permission;

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
                title = header.Length > 0 ? header : "Question";
            }
            else
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
            questionText = GetStr(q, "question");
            header = GetStr(q, "header");
            multi = q.ValueKind == JsonValueKind.Object &&
                    q.TryGetProperty("multiSelect", out var ms) &&
                    ms.ValueKind == JsonValueKind.True;

            if (q.ValueKind == JsonValueKind.Object &&
                q.TryGetProperty("options", out var opts) &&
                opts.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (var opt in opts.EnumerateArray())
                {
                    string label = opt.ValueKind == JsonValueKind.String
                        ? opt.GetString() ?? ""
                        : GetStr(opt, "label");
                    string description = opt.ValueKind == JsonValueKind.Object ? GetStr(opt, "description") : "";
                    if (label.Length == 0) label = $"Option {i + 1}";
                    list.Add(new QuestionOption(i, label, description, i < 9 ? $"Ctrl+{i + 1}" : ""));
                    i++;
                }
            }
        }

        return (questionText, header, list, multi);
    }

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
