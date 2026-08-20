namespace AgentNotch.App.Sessions;

/// <summary>Coarse state of a live agent session.</summary>
public enum SessionStatus
{
    /// <summary>Actively producing output (running a tool, streaming, thinking).</summary>
    Working,

    /// <summary>Handed the turn back with a question or is blocked on an approval prompt.</summary>
    NeedsYou,

    /// <summary>Handed the turn back having simply completed the work.</summary>
    Finished,
}

/// <summary>
/// One live agent session from any supported tool. For Claude Code this is a
/// registry entry (sessions\pid.json) joined with its transcript; for Codex a
/// rollout file; for Antigravity a cascade trajectory. Immutable snapshot
/// value — SessionWatcher builds a fresh list on every change.
/// </summary>
public sealed class AgentSession
{
    public string SessionId { get; set; } = "";

    /// <summary>Which agent produced this session: "claude" | "codex" | "antigravity" | "cursor".</summary>
    public string Agent { get; set; } = "claude";

    /// <summary>
    /// App hosting the session when it isn't a plain terminal: "cursor" |
    /// "antigravity" | "vscode" | null. Drives the row label ("claude · cursor")
    /// and which window the jump targets.
    /// </summary>
    public string? Host { get; set; }

    /// <summary>Model name when the source reports one (e.g. "GPT-5.2", "Gemini").</summary>
    public string? Model { get; set; }

    /// <summary>
    /// Where the session actually runs, spelled for humans: "Cursor", "VS Code",
    /// "Antigravity", "Windows Terminal", "PowerShell", "Claude"… Shown as a chip
    /// on the row. Null when it could not be resolved.
    /// </summary>
    public string? HostLabel { get; set; }

    /// <summary>When the session began (UTC) — drives the "27m" age chip. Default = unknown.</summary>
    public DateTime StartedUtc { get; set; }

    public string Cwd { get; set; } = "";

    /// <summary>Last segment of <see cref="Cwd"/> ("session" when the cwd is unknown).</summary>
    public string ProjectName { get; set; } = "";

    /// <summary>"cli" | "claude-desktop" | "claude-vscode" (claude only; "" otherwise).</summary>
    public string Entrypoint { get; set; } = "";

    /// <summary>Owning process id when known; 0 for file/RPC-derived sessions (codex, antigravity).</summary>
    public int Pid { get; set; }

    public SessionStatus Status { get; set; }

    /// <summary>Tool the agent is running or blocked on (e.g. "Bash", "Edit"); null when none applies.</summary>
    public string? CurrentTool { get; set; }

    /// <summary>File path or command excerpt for <see cref="CurrentTool"/> (at most 60 chars).</summary>
    public string? CurrentTarget { get; set; }

    /// <summary>Last assistant text: first line only, at most 120 chars.</summary>
    public string? LastMessage { get; set; }

    /// <summary>Cumulative token spend this session: input + cache-creation + output.</summary>
    public long TokensSpent { get; set; }

    /// <summary>Transcript last-write time (UTC) — "how recently did it do anything".</summary>
    public DateTime LastActivityUtc { get; set; }
}
