using System.Text.Json;

namespace AgentNotch.Protocol;

/// <summary>
/// A few plain lines describing what an agent is asking, built straight from the
/// raw hook event. Lives here rather than in the app because BOTH sides need it:
/// the app renders the notch card, and the hook helper echoes the same question
/// into the terminal it was spawned from. Both dialects (Claude Code's
/// question/header/label and Cursor's prompt/title/id) are read.
///
/// Deliberately dumb and defensive — this runs inside a hook, so an unfamiliar
/// payload must degrade to a short generic line, never throw.
/// </summary>
public static class HookSummary
{
    /// <summary>Heading, then zero or more detail lines. Never null, never throws.</summary>
    public static (string Title, IReadOnlyList<string> Detail) Describe(string mode, JsonElement evt)
    {
        try
        {
            string tool = Str(evt, "tool_name");
            if (tool.Length == 0 && Str(evt, "command").Length > 0) tool = "Bash";
            JsonElement? input = Obj(evt, "tool_input");

            // A question: show it with its options, numbered as the notch numbers them.
            var (question, options) = ParseQuestion(input);
            if (question.Length > 0 || options.Count > 0)
            {
                var detail = new List<string>();
                if (question.Length > 0) detail.Add(question);
                for (int i = 0; i < options.Count && i < 9; i++)
                    detail.Add($"  {i + 1}. {options[i]}");
                return ("Question", detail);
            }

            switch (tool)
            {
                case "Edit":
                case "Write":
                {
                    var file = FileName(Str(input, "file_path"));
                    return ("Permission request", new[] { $"{tool} {file}".TrimEnd() });
                }
                case "Bash":
                {
                    var command = Str(input, "command");
                    if (command.Length == 0) command = Str(evt, "command");
                    return ("Permission request", new[] { FirstLine(command, 100) });
                }
                default:
                {
                    var name = tool.Length > 0 ? tool : "Agent request";
                    return ("Permission request", new[] { name });
                }
            }
        }
        catch
        {
            return ("Agent request", Array.Empty<string>());
        }
    }

    private static (string Question, IReadOnlyList<string> Options) ParseQuestion(JsonElement? input)
    {
        var options = new List<string>();
        if (input is not { ValueKind: JsonValueKind.Object } obj) return ("", options);
        if (!obj.TryGetProperty("questions", out var questions)
            || questions.ValueKind != JsonValueKind.Array
            || questions.GetArrayLength() == 0)
            return ("", options);

        var q = questions[0];
        string text = First(q, "question", "prompt", "text");
        if (text.Length == 0) text = First(obj, "title", "header");

        if (q.ValueKind == JsonValueKind.Object
            && q.TryGetProperty("options", out var opts)
            && opts.ValueKind == JsonValueKind.Array)
        {
            foreach (var opt in opts.EnumerateArray())
            {
                var label = opt.ValueKind == JsonValueKind.String
                    ? opt.GetString() ?? ""
                    : First(opt, "label", "name", "text", "id");
                if (label.Length > 0) options.Add(Trim(label, 72));
            }
        }

        return (Trim(text, 140), options);
    }

    // ------------------------------------------------------------------ helpers

    private static string First(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (Str(element, name) is { Length: > 0 } value) return value;
        return "";
    }

    private static string Str(JsonElement? element, string name)
    {
        if (element is not { ValueKind: JsonValueKind.Object } obj) return "";
        return obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? ""
            : "";
    }

    private static JsonElement? Obj(JsonElement evt, string name)
        => evt.ValueKind == JsonValueKind.Object
           && evt.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static string FileName(string path)
    {
        if (path.Length == 0) return "";
        int i = path.LastIndexOfAny(new[] { '\\', '/' });
        return i >= 0 ? path[(i + 1)..] : path;
    }

    private static string FirstLine(string text, int max)
    {
        int i = text.IndexOfAny(new[] { '\r', '\n' });
        return Trim((i >= 0 ? text[..i] : text).Trim(), max);
    }

    private static string Trim(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}
