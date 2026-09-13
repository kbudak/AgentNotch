using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentNotch.Protocol;

/// <summary>
/// Wire protocol between hook-helper processes and the AgentNotch app.
/// Transport: Windows named pipe, newline-delimited UTF-8 JSON (one message per line).
/// The helper opens a connection per hook event; blocking events keep the
/// connection open until the app replies with a Decision line (correlated by Id).
/// </summary>
public static class Proto
{
    /// <summary>Named pipe path is \\.\pipe\agentnotch (client passes just the name).</summary>
    public const string PipeName = "agentnotch";

    public const int Version = 1;

    /// <summary>Helper modes — first CLI argument, also stamped into the envelope.</summary>
    public const string ModePermissionRequest = "permission-request";
    public const string ModeAskUserQuestion = "ask-user-question";
    public const string ModeNotify = "notify";
    public const string ModeStop = "stop";
    public const string ModePrompt = "prompt";

    // Cursor IDE speaks its own hook dialect (~\.cursor\hooks.json): flat event
    // arrays, a shell-string command, and a {"permission": ...} reply instead of
    // Claude Code's hookSpecificOutput envelope. Separate modes keep the helper
    // honest about which dialect to print — the app side sees one normalised
    // HookRequest either way.
    public const string ModeCursorPermission = "cursor-permission";
    public const string ModeCursorNotify = "cursor-notify";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };
}

/// <summary>Helper → app. One per hook event.</summary>
public sealed class PipeEnvelope
{
    public int V { get; set; } = Proto.Version;

    /// <summary>Correlation id (GUID string) minted by the helper.</summary>
    public string Id { get; set; } = "";

    /// <summary>Always "hook" for helper→app messages.</summary>
    public string Kind { get; set; } = "hook";

    /// <summary>Helper mode (see Proto.Mode*).</summary>
    public string Mode { get; set; } = "";

    /// <summary>True when the helper blocks until the app replies with a Decision.</summary>
    public bool ExpectsReply { get; set; }

    /// <summary>The raw hook event JSON exactly as Claude Code piped it to the helper's stdin.</summary>
    public JsonElement Event { get; set; }
}

/// <summary>App → helper. Only sent for envelopes with ExpectsReply = true.</summary>
public sealed class PipeDecision
{
    public int V { get; set; } = Proto.Version;

    /// <summary>Must echo the envelope Id.</summary>
    public string Id { get; set; } = "";

    public string Kind { get; set; } = "decision";

    public DecisionBody Decision { get; set; } = new();
}

public sealed class DecisionBody
{
    /// <summary>
    /// "allow" | "deny" | "pass" | "answer".
    /// pass   → helper writes nothing to stdout; Claude Code falls back to its normal flow
    ///          (terminal prompt / terminal question). Also the fail-safe on timeout.
    /// answer → only for ask-user-question: Answers carries the user's picks.
    /// </summary>
    public string Behavior { get; set; } = "pass";

    /// <summary>Optional user-facing message (shown by Claude Code when denying).</summary>
    public string? Message { get; set; }

    /// <summary>Optional modified tool input (only meaningful with behavior "allow").</summary>
    public JsonElement? UpdatedInput { get; set; }

    /// <summary>For behavior "answer": the selected option label(s) per question.</summary>
    public List<QuestionAnswer>? Answers { get; set; }
}

public sealed class QuestionAnswer
{
    public string Question { get; set; } = "";
    public List<string> Labels { get; set; } = new();
}
