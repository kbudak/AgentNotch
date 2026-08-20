using System.Collections.Concurrent;
using System.Globalization;
using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentNotch.App.Diagnostics;

// Lets the test project call the tailBytes overload and inspect the cache.
// InternalsVisibleTo is AllowMultiple, so other files declaring it too is fine.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("AgentNotch.Tests")]

namespace AgentNotch.App.Sessions;

/// <summary>
/// Codex CLI/IDE rollout-session collector — a faithful C# port of the proven
/// Electron collector (collectors/codex-tasks.js). Codex writes one rollout
/// *.jsonl per session under ~\.codex\sessions\yyyy\MM\dd\; the tail of that
/// file says what the agent is doing right now. Pure static parsing over temp
/// fixture-testable inputs; never throws.
/// </summary>
public static class CodexTail
{
    /// <summary>Initial tail window per rollout file (doubles backwards when needed).</summary>
    public const int TailBytes = 1024 * 1024;

    /// <summary>
    /// ANY session (working or not) whose rollout mtime is older than this is a
    /// leftover — dropped. A stale unmatched task_started usually means Codex was
    /// killed mid-turn.
    /// </summary>
    public static readonly TimeSpan IdleKeep = TimeSpan.FromMinutes(10);

    /// <summary>Only the newest rollout files are considered per poll.</summary>
    private const int MaxFiles = 20;

    /// <summary>Sanity cap for the backwards-doubling tail window.</summary>
    private const long MaxGrowBytes = 512L * 1024 * 1024;

    // ---------- public entry ----------

    /// <summary>
    /// Scans <paramref name="sessionsDir"/> (~\.codex\sessions) for the 20 newest
    /// rollout *.jsonl files and returns one <see cref="AgentSession"/> per live
    /// session, newest-first. Sub-agent rollouts (thread_source != "user"), files
    /// with no lifecycle event, and anything idle beyond <see cref="IdleKeep"/>
    /// are dropped. Never throws.
    /// </summary>
    public static List<AgentSession> Collect(string sessionsDir, DateTime nowUtc)
        => Collect(sessionsDir, nowUtc, TailBytes);

    /// <summary>Testable overload: <paramref name="tailBytes"/> is the initial tail window.</summary>
    internal static List<AgentSession> Collect(string sessionsDir, DateTime nowUtc, int tailBytes)
    {
        var sessions = new List<AgentSession>();
        try
        {
            // Evict cache entries whose rollout file vanished (cleanup, deletion).
            foreach (var key in Cache.Keys)
            {
                if (!File.Exists(key)) Cache.TryRemove(key, out _);
            }

            foreach (var file in ListRecentFiles(sessionsDir))
            {
                var session = CollectOne(file.Path, file.MtimeUtc, file.Size, tailBytes);
                if (session is null) continue;
                if (nowUtc - session.LastActivityUtc > IdleKeep) continue;
                sessions.Add(session);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("codex: collect failed: " + ex.Message);
        }
        return sessions.OrderByDescending(s => s.LastActivityUtc).ToList();
    }

    // ---------- per-file cache ----------

    internal sealed record CacheEntry(DateTime MtimeUtc, long Size, AgentSession? Session);

    /// <summary>
    /// Full path -> (mtime, size, parsed result). Rollouts only change by
    /// appending, so an unchanged (mtime, size) pair means the parse — including
    /// a "skip this file" null — is still valid and the file is not re-read.
    /// </summary>
    internal static readonly ConcurrentDictionary<string, CacheEntry> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Parses one rollout file via the cache. Null = not a live user session.</summary>
    internal static AgentSession? CollectOne(string path, DateTime mtimeUtc, long size, int tailBytes)
    {
        if (Cache.TryGetValue(path, out var hit) && hit.MtimeUtc == mtimeUtc && hit.Size == size)
            return hit.Session;

        AgentSession? session = null;
        try
        {
            session = Parse(path, mtimeUtc, tailBytes);
        }
        catch (Exception ex)
        {
            AppLog.Write("codex: parse of " + path + " failed: " + ex.Message);
        }
        Cache[path] = new CacheEntry(mtimeUtc, size, session);
        return session;
    }

    // ---------- discovery ----------

    private readonly record struct RolloutFile(string Path, long Size, DateTime MtimeUtc);

    /// <summary>The <see cref="MaxFiles"/> newest *.jsonl under root (recursive), by mtime.</summary>
    private static List<RolloutFile> ListRecentFiles(string root)
    {
        var files = new List<RolloutFile>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
            {
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists) files.Add(new RolloutFile(path, info.Length, info.LastWriteTimeUtc));
                }
                catch
                {
                    // file vanished mid-scan — skip it
                }
            }
        }
        catch
        {
            // missing dir / permission race — no Codex sessions then
            return new List<RolloutFile>();
        }
        return files.OrderByDescending(f => f.MtimeUtc).Take(MaxFiles).ToList();
    }

    // ---------- tail reading ----------

    /// <summary>
    /// Reads a tail of the rollout and parses it line-by-line. Long tool output can
    /// push the latest lifecycle event (task_started/task_complete) beyond the
    /// initial window, so the window DOUBLES backwards until a lifecycle event is
    /// included or the whole file is covered. Partial first line and malformed
    /// lines are tolerated. Never throws.
    /// </summary>
    internal static List<JsonElement> ReadEvents(string path, int tailBytes)
    {
        try
        {
            using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            long size = fs.Length;
            long len = Math.Min(size, (long)tailBytes);
            while (true)
            {
                if (len <= 0) return new List<JsonElement>();
                int want = (int)Math.Min(len, MaxGrowBytes);
                // Pooled, and parsed straight from the bytes: the widening retry means
                // this window can reach MaxGrowBytes, and a fresh array plus a UTF-16
                // copy of it per poll is exactly the large-object churn that grew the
                // process. See TranscriptTail.ParseLines.
                var buf = ArrayPool<byte>.Shared.Rent(want);
                List<JsonElement> events;
                try
                {
                    fs.Position = Math.Max(0, size - want);
                    fs.ReadExactly(buf, 0, want);
                    events = TranscriptTail.ParseLines(buf, 0, want);
                }
                finally { ArrayPool<byte>.Shared.Return(buf); }
                if (events.Any(IsLifecycle) || want >= size || len >= MaxGrowBytes)
                    return events;
                len = Math.Min(size, len * 2);
            }
        }
        catch
        {
            return new List<JsonElement>();
        }
    }

    private static bool IsLifecycle(JsonElement ev)
    {
        if (GetString(ev, "type") != "event_msg") return false;
        var type = GetString(Payload(ev), "type");
        return type == "task_started" || type == "task_complete";
    }

    // ---------- rollout parsing ----------

    /// <summary>One pass over the tail events -> session snapshot (JS collectOne).</summary>
    private static AgentSession? Parse(string path, DateTime mtimeUtc, int tailBytes)
    {
        var events = ReadEvents(path, tailBytes);
        if (events.Count == 0) return null;

        var id = FileId(path);
        string? cwd = null, model = null, threadSource = null;
        string? source = null, originator = null;
        string? lastMessage = null, lastTool = null, lastTarget = null;
        JsonElement? stateEvent = null;
        var pending = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var ev in events)
        {
            var type = GetString(ev, "type");
            var p = Payload(ev);
            if (type == "session_meta")
            {
                id = Keep(Keep(GetString(p, "id"), GetString(p, "session_id")), id)!;
                cwd = Keep(GetString(p, "cwd"), cwd);
                model = Keep(GetString(p, "model"), model);
                threadSource = Keep(GetString(p, "thread_source"), threadSource);
                source = Keep(GetString(p, "source"), source);
                originator = Keep(GetString(p, "originator"), originator);
            }
            else if (type == "turn_context")
            {
                cwd = Keep(GetString(p, "cwd"), cwd);
                model = Keep(GetString(p, "model"), model);
            }
            else if (type == "event_msg")
            {
                var pt = GetString(p, "type");
                if (pt == "task_started" || pt == "task_complete") stateEvent = p;
                else if (pt == "agent_message" && GetString(p, "phase") == "final_answer")
                    lastMessage = Keep(GetString(p, "message"), lastMessage);
            }
            else if (type == "response_item")
            {
                var pt = GetString(p, "type");
                if (pt == "function_call" || pt == "custom_tool_call")
                {
                    var rawTool = GetString(p, "name");
                    var rawInput = GetString(p, "input") ?? GetString(p, "arguments");
                    var (parsedTool, parsedTarget) = ExtractActivity(rawTool, rawInput);
                    lastTool = Keep(parsedTool, lastTool);
                    lastTarget = Keep(parsedTarget, lastTarget);
                    var callId = Keep(GetString(p, "call_id"), GetString(p, "id"));
                    if (callId is not null) pending[callId] = p;
                }
                else if (pt == "function_call_output" || pt == "custom_tool_call_output")
                {
                    if (GetString(p, "call_id") is { } callId) pending.Remove(callId);
                }
            }
        }

        // Which threads are real, user-facing tasks:
        //  * null/empty  -> a plain CLI session (codex_exec)
        //  * "user"      -> a top-level task
        //  * "subagent" / "realtime_voice" -> a task delegated from another task or a
        //    voice chat (Codex Desktop) — these ARE what the user is watching run.
        // Any other named source (guardian/reviewer/compaction internals) stays hidden.
        // Same-cwd duplicates (a task plus its reviewer sub-agents) are collapsed later.
        if (threadSource is { Length: > 0 } ts &&
            ts is not ("user" or "subagent" or "realtime_voice"))
            return null;
        if (stateEvent is not { } state) return null;

        SessionStatus status;
        var stateType = GetString(state, "type");
        if (stateType == "task_started")
        {
            status = SessionStatus.Working;
            foreach (var call in pending.Values)
            {
                var raw = $"{GetString(call, "name")} {GetString(call, "arguments")} {GetString(call, "input")}";
                if (NeedsYouCall.IsMatch(raw))
                {
                    status = SessionStatus.NeedsYou;
                    break;
                }
            }
        }
        else
        {
            lastMessage = Keep(GetString(state, "last_agent_message"), lastMessage);
            status = IsAsking(lastMessage) ? SessionStatus.NeedsYou : SessionStatus.Finished;
        }

        string summary;
        if (status == SessionStatus.Working)
            summary = lastTarget ?? (string.IsNullOrEmpty(lastTool) ? "Working…" : "Running " + lastTool);
        else if (status == SessionStatus.NeedsYou && stateType == "task_started")
            summary = "Waiting for your approval or answer";
        else if (status == SessionStatus.NeedsYou)
            summary = Squash(Keep(lastMessage, "Waiting for you")!, 100);
        else
            summary = Squash(Keep(lastMessage, "Task complete")!, 100);

        bool vscode =
            (source?.Contains("vscode", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (originator?.Contains("vscode", StringComparison.OrdinalIgnoreCase) ?? false);

        return new AgentSession
        {
            SessionId = "codex:" + id,
            Agent = "codex",
            Host = vscode ? "vscode" : null,
            Model = PrettyModel(model),
            Cwd = cwd ?? "",
            ProjectName = string.IsNullOrWhiteSpace(cwd) ? "Codex" : TranscriptTail.ProjectNameOf(cwd),
            Entrypoint = "",
            Pid = 0,
            Status = status,
            CurrentTool = status == SessionStatus.Working ? lastTool : null,
            CurrentTarget = status == SessionStatus.Working ? lastTarget : null,
            LastMessage = summary,
            TokensSpent = 0,
            StartedUtc = StartedFromName(path, mtimeUtc),
            LastActivityUtc = mtimeUtc,
        };
    }

    private static readonly Regex CmdRegex = new(@"cmd\s*[:=]\s*""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex UpdateFileRegex = new(@"Update File:\s*([^\r\n""]+)", RegexOptions.Compiled);

    private static (string? tool, string? target) ExtractActivity(string? rawTool, string? rawInput)
    {
        if (string.IsNullOrEmpty(rawTool)) return (null, null);

        if (string.Equals(rawTool, "exec", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(rawInput))
        {
            if (rawInput.Contains("exec_command", StringComparison.OrdinalIgnoreCase))
            {
                var m = CmdRegex.Match(rawInput);
                var cmd = m.Success ? m.Groups[1].Value.Replace(@"\\", @"\") : "";
                var target = cmd.Length > 0 ? "Running " + ShortCommand(cmd) : "Running a command";
                return ("exec_command", target);
            }
            if (rawInput.Contains("apply_patch", StringComparison.OrdinalIgnoreCase))
            {
                var m = UpdateFileRegex.Match(rawInput);
                string file = "";
                if (m.Success)
                {
                    var raw = m.Groups[1].Value;
                    int escN = raw.IndexOf(@"\n", StringComparison.Ordinal);
                    if (escN >= 0) raw = raw[..escN];
                    int escR = raw.IndexOf(@"\r", StringComparison.Ordinal);
                    if (escR >= 0) raw = raw[..escR];
                    raw = raw.Replace(@"\\", @"\").Trim();
                    file = Path.GetFileName(raw);
                }
                var target = file.Length > 0 ? "Editing " + file : "Editing files";
                return ("apply_patch", target);
            }
            if (rawInput.Contains("read_file", StringComparison.OrdinalIgnoreCase))
                return ("read_file", "Reading a file");
            if (rawInput.Contains("web_search", StringComparison.OrdinalIgnoreCase))
                return ("web_search", "Searching the web");
            if (rawInput.Contains("search", StringComparison.OrdinalIgnoreCase))
                return ("search", "Searching the code");
        }

        return (rawTool, null);
    }

    private static string ShortCommand(string command)
    {
        var line = command.Split('\n')[0].Trim();
        line = string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (line.Length <= 34) return line;

        var first = line.Split(' ')[0];
        bool readable = first.Length is > 0 and <= 18 &&
                        first.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or '\\');
        return readable ? first + "…" : "a command";
    }

    // ---------- text helpers ----------

    /// <summary>
    /// Is the agent's closing message asking the user something rather than
    /// reporting it is done? Looks at the tail — that's where questions live.
    /// (Codex variant of the phrase list, ported verbatim from the JS.)
    /// </summary>
    internal static bool IsAsking(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var trimmed = text.Trim();
        var tail = trimmed.Length <= 600 ? trimmed : trimmed[^600..];
        if (QuestionTail.IsMatch(tail)) return true;
        if (TurkishQuestion.IsMatch(tail)) return true;
        return AskingPhrases.IsMatch(tail);
    }

    private static readonly Regex QuestionTail =
        new("[?？]\\s*$", RegexOptions.Compiled);

    private static readonly Regex TurkishQuestion =
        new("\\b(mı|mi|mu|mü)\\s*\\?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AskingPhrases =
        new("\\b(approve|approval|confirm|confirmation|proceed|should i|shall i|do you want|" +
            "would you like|which (one|option)|onay|izin|devam edeyim|ister misin|hangisini|" +
            "seçer misin|karar ver)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>A pending call that blocks on the user (approval or question).</summary>
    private static readonly Regex NeedsYouCall =
        new("request_user_input|require_escalated|approval",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WhitespaceRun =
        new("\\s+", RegexOptions.Compiled);

    private static readonly Regex RolloutPrefix =
        new("^rollout-[^-]+-[^-]+-", RegexOptions.Compiled);

    /// <summary>"gpt-5.2-codex" -> "GPT-5.2-codex"; null stays null.</summary>
    internal static string? PrettyModel(string? id)
        => string.IsNullOrEmpty(id) ? null : Regex.Replace(id, "^gpt-", "GPT-", RegexOptions.IgnoreCase);

    /// <summary>Fallback session id: file name stripped of its rollout-date prefix.</summary>
    internal static string FileId(string path)
        => RolloutPrefix.Replace(Path.GetFileNameWithoutExtension(path), "", 1);

    private static readonly Regex RolloutStamp =
        new(@"rollout-(\d{4})-(\d{2})-(\d{2})T(\d{2})-(\d{2})-(\d{2})", RegexOptions.Compiled);

    /// <summary>
    /// Session start time, taken from the rollout file name
    /// (rollout-2026-07-18T17-04-32-&lt;id&gt;.jsonl, local time), falling back to the
    /// file's creation time and finally to its mtime.
    /// </summary>
    internal static DateTime StartedFromName(string path, DateTime fallbackUtc)
    {
        var m = RolloutStamp.Match(Path.GetFileName(path));
        if (m.Success)
        {
            try
            {
                var local = new DateTime(
                    int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture),
                    DateTimeKind.Local);
                return local.ToUniversalTime();
            }
            catch { /* out-of-range parts — fall through */ }
        }
        try { return File.GetCreationTimeUtc(path); }
        catch { return fallbackUtc; }
    }

    /// <summary>Whitespace-collapsed, capped at <paramref name="max"/> chars (JS .slice semantics).</summary>
    private static string Squash(string text, int max)
    {
        var s = WhitespaceRun.Replace(text, " ");
        return s.Length <= max ? s : s[..max];
    }

    /// <summary>JS "a || b": candidate unless null/empty, else current.</summary>
    private static string? Keep(string? candidate, string? current)
        => string.IsNullOrEmpty(candidate) ? current : candidate;

    // ---------- low-level JSON helpers ----------

    /// <summary>ev.payload when present; a default (Undefined) element otherwise.</summary>
    private static JsonElement Payload(JsonElement ev)
        => ev.ValueKind == JsonValueKind.Object && ev.TryGetProperty("payload", out var p)
            ? p
            : default;

    private static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
