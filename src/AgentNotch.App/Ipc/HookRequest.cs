using System.Text.Json;
using AgentNotch.App.Diagnostics;
using AgentNotch.Protocol;

namespace AgentNotch.App.Ipc;

/// <summary>
/// One hook event received from a helper process over the named pipe.
/// Blocking events (<see cref="ExpectsReply"/> = true) hold their pipe
/// connection open until <see cref="RespondAsync"/> writes a decision line,
/// the client vanishes (<see cref="Abandoned"/>), or the app shuts down.
/// Thread-safe; events may fire on any thread.
/// </summary>
public sealed class HookRequest
{
    private enum State { Pending, Resolved, Abandoned }

    private readonly object _gate = new();
    private readonly Func<PipeDecision, Task>? _responder;
    private readonly TaskCompletionSource _resolution = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Action? _abandonedHandlers;
    private State _state;

    internal HookRequest(PipeEnvelope envelope, Func<PipeDecision, Task>? responder)
    {
        _responder = responder;
        Id = envelope.Id;
        Mode = envelope.Mode;
        ExpectsReply = envelope.ExpectsReply;
        // Clone so the element stays valid after the transport buffers are gone.
        // (default(JsonElement) has ValueKind Undefined and cannot be cloned.)
        Event = envelope.Event.ValueKind == JsonValueKind.Undefined ? default : envelope.Event.Clone();

        HookEventName = GetString("hook_event_name");
        PermissionMode = GetString("permission_mode");

        // Cursor names the same facts differently (conversation_id / workspace_roots)
        // and sends a bare `command` string for beforeShellExecution instead of a
        // tool. Normalising here — the one place raw hook JSON becomes typed facts —
        // means every consumer downstream (PromptViewModel, the diff renderer, the
        // views) keeps working against the Claude Code shape it already knows.
        SessionId = FirstNonEmpty(GetString("session_id"), GetString("conversation_id"));
        Cwd = FirstNonEmpty(GetString("cwd"), FirstWorkspaceRoot());

        var shellCommand = GetString("command");
        ToolName = FirstNonEmpty(GetString("tool_name"), shellCommand.Length > 0 ? "Bash" : "");
        ToolInput = Event.ValueKind == JsonValueKind.Object
                    && Event.TryGetProperty("tool_input", out JsonElement toolInput)
                    && toolInput.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? toolInput
            : shellCommand.Length > 0
                ? JsonSerializer.SerializeToElement(new { command = shellCommand })
                : null;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
            if (!string.IsNullOrEmpty(value)) return value;
        return "";
    }

    /// <summary>Cursor sends the open folders as an array; the first is the session's project.</summary>
    private string FirstWorkspaceRoot()
    {
        try
        {
            if (Event.ValueKind == JsonValueKind.Object
                && Event.TryGetProperty("workspace_roots", out var roots)
                && roots.ValueKind == JsonValueKind.Array)
            {
                foreach (var root in roots.EnumerateArray())
                    if (root.ValueKind == JsonValueKind.String && root.GetString() is { Length: > 0 } value)
                        return value;
            }
        }
        catch { /* tolerate */ }
        return "";
    }

    /// <summary>Envelope correlation id (echoed back in the decision).</summary>
    public string Id { get; }

    /// <summary>Helper mode — one of the <c>Proto.Mode*</c> constants.</summary>
    public string Mode { get; }

    /// <summary>True when the helper blocks until a decision line is written.</summary>
    public bool ExpectsReply { get; }

    /// <summary>Raw hook event JSON as Claude Code piped it to the helper (clone, safe to hold).</summary>
    public JsonElement Event { get; }

    /// <summary>e.g. "PermissionRequest", "Notification" ("" when absent).</summary>
    public string HookEventName { get; }

    /// <summary>e.g. "Edit", "Bash" ("" when absent).</summary>
    public string ToolName { get; }

    /// <summary>Claude Code session id ("" when absent).</summary>
    public string SessionId { get; }

    /// <summary>Working directory of the session ("" when absent).</summary>
    public string Cwd { get; }

    /// <summary>e.g. "default", "acceptEdits" ("" when absent).</summary>
    public string PermissionMode { get; }

    /// <summary>The tool's input object, when the event carries one.</summary>
    public JsonElement? ToolInput { get; }

    /// <summary>True once this request can no longer be responded to (answered or abandoned).</summary>
    public bool IsResolved
    {
        get { lock (_gate) return _state != State.Pending; }
    }

    /// <summary>
    /// Raised (once, any thread) when the client vanished before a reply —
    /// the UI should dismiss the prompt. Subscribing after the fact invokes
    /// the handler immediately so a late-marshaled UI never misses it.
    /// </summary>
    public event Action? Abandoned
    {
        add
        {
            if (value is null) return;
            bool fireNow;
            lock (_gate)
            {
                fireNow = _state == State.Abandoned;
                if (!fireNow) _abandonedHandlers += value;
            }
            if (fireNow) InvokeAbandonedHandlers(value);
        }
        remove
        {
            lock (_gate) _abandonedHandlers -= value;
        }
    }

    /// <summary>
    /// Writes the decision line to the waiting helper. Idempotent and never
    /// throws: a no-op when the request expects no reply, was already
    /// responded to, or was abandoned. IO errors are swallowed and logged
    /// (the client may have timed out and gone away).
    /// </summary>
    public async Task RespondAsync(DecisionBody decision)
    {
        if (!ExpectsReply) return;
        lock (_gate)
        {
            if (_state != State.Pending) return;
            _state = State.Resolved;
        }

        try
        {
            if (_responder is not null)
                await _responder(new PipeDecision { Id = Id, Decision = decision }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Write($"ipc: respond failed for {Id}: {ex.Message}");
        }
        finally
        {
            _resolution.TrySetResult();
        }
    }

    /// <summary>Completes when the request is responded to or abandoned (server-internal).</summary>
    internal Task Resolution => _resolution.Task;

    /// <summary>Marks the request abandoned (client gone before a reply); no-op if already resolved.</summary>
    internal void MarkAbandoned()
    {
        Action? handlers;
        lock (_gate)
        {
            if (_state != State.Pending) return;
            _state = State.Abandoned;
            handlers = _abandonedHandlers;
            _abandonedHandlers = null;
        }
        _resolution.TrySetResult();
        InvokeAbandonedHandlers(handlers);
    }

    private void InvokeAbandonedHandlers(Action? handlers)
    {
        if (handlers is null) return;
        try { handlers(); }
        catch (Exception ex) { AppLog.Write($"ipc: Abandoned handler threw for {Id}: {ex}"); }
    }

    private string GetString(string name)
        => Event.ValueKind == JsonValueKind.Object
           && Event.TryGetProperty(name, out JsonElement value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
