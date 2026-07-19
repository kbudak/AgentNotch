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
/// One live Claude Code session: a registry entry (sessions\pid.json) joined with
/// its transcript (projects\...\sessionId.jsonl). Immutable snapshot value —
/// SessionWatcher builds a fresh list on every change.
/// </summary>
public sealed class AgentSession
{
    public string SessionId { get; set; } = "";

    public string Cwd { get; set; } = "";

    /// <summary>Last segment of <see cref="Cwd"/> ("session" when the cwd is unknown).</summary>
    public string ProjectName { get; set; } = "";

    /// <summary>"cli" | "claude-desktop" | "claude-vscode".</summary>
    public string Entrypoint { get; set; } = "";

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
