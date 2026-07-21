using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentNotch.App.Diagnostics;

namespace AgentNotch.App.Sessions;

/// <summary>
/// One entry from the open-session registry (claudeHome\sessions\pid.json).
/// Claude Code writes one file per OPEN session (tab/terminal) and removes it on
/// close — so this registry, not transcript mtimes, decides "is this session open".
/// </summary>
public sealed record RegistrySession(
    int Pid,
    string SessionId,
    string? Cwd,
    string Entrypoint,
    string? Status,
    DateTime? StatusUpdatedAtUtc);

/// <summary>
/// Pure transcript/registry parsing and status derivation — a faithful C# port of
/// the proven Electron collector (collectors/tasks.js). Everything here is static
/// and takes paths/strings/parsed events, so it is unit-testable against temp
/// fixture files; SessionWatcher only decides WHEN to call it.
/// </summary>
public static class TranscriptTail
{
    /// <summary>How much of a transcript we read from the end.</summary>
    public const int TailBytes = 256 * 1024;

    /// <summary>
    /// A busy/waiting registry status older than this is a leftover from a hung or
    /// abandoned session, not a live one. Generous enough for a long thinking block.
    /// Also bounds how long a mid-turn transcript keeps counting as "working".
    /// </summary>
    public static readonly TimeSpan BusyMax = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Non-working sessions idle longer than this are leftovers the user moved on
    /// from — drop them. They reappear the moment they do something again.
    /// </summary>
    public static readonly TimeSpan IdleKeep = TimeSpan.FromMinutes(10);

    // ---------- tail reading + line parsing ----------

    /// <summary>
    /// Reads the last <see cref="TailBytes"/> of a JSONL transcript and parses it
    /// line-by-line. A partial first line (the tail window usually starts mid-line)
    /// and malformed lines are silently skipped. Never throws.
    /// </summary>
    public static List<JsonElement> ReadTailEvents(string transcriptPath)
    {
        try
        {
            using var fs = new FileStream(
                transcriptPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            long size = fs.Length;
            int len = (int)Math.Min(size, TailBytes);
            if (len <= 0) return new List<JsonElement>();
            fs.Position = Math.Max(0, size - len);
            var buf = new byte[len];
            fs.ReadExactly(buf, 0, len);
            return ParseLines(Encoding.UTF8.GetString(buf));
        }
        catch
        {
            return new List<JsonElement>();
        }
    }

    /// <summary>Parses NDJSON text; unparseable lines (e.g. a cut-off first line) are skipped.</summary>
    public static List<JsonElement> ParseLines(string text)
    {
        var events = new List<JsonElement>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                events.Add(doc.RootElement.Clone());
            }
            catch (JsonException)
            {
                // partial or malformed line — tolerated by design
            }
        }
        return events;
    }

    // ---------- event inspection ----------

    /// <summary>Full text of the agent's most recent text output (searches backwards across assistant events).</summary>
    public static string? LastAssistantText(IReadOnlyList<JsonElement> events)
    {
        for (int i = events.Count - 1; i >= 0; i--)
        {
            if (GetString(events[i], "type") != "assistant") continue;
            if (MessageContent(events[i]) is not { } content) continue;
            for (int j = content.GetArrayLength() - 1; j >= 0; j--)
            {
                var block = content[j];
                if (GetString(block, "type") != "text") continue;
                var text = GetString(block, "text");
                if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
            }
        }
        return null;
    }

    /// <summary>
    /// Is the agent's closing message asking the user something (a question or an
    /// approval prompt) rather than reporting that it is done? Looks at the tail of
    /// the message — that's where questions live.
    /// </summary>
    public static bool IsAsking(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var tail = text.Length <= 400 ? text : text[^400..];
        if (QuestionTail.IsMatch(tail)) return true;
        if (TurkishQuestion.IsMatch(tail)) return true;
        return AskingPhrases.IsMatch(tail);
    }

    private static readonly Regex QuestionTail =
        new("[?？]\\s*$", RegexOptions.Compiled);

    private static readonly Regex TurkishQuestion =
        new("(mı|mi|mu|mü)\\s*\\?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AskingPhrases =
        new("\\b(yes\\s*/\\s*no|y\\s*/\\s*n|approve|confirm|proceed\\??|should i\\b|shall i\\b|" +
            "do you want|would you like|want me to|which (one|option|approach)|onayl|izin ver|" +
            "devam edeyim|devam et\\?|ister misin|onay bekl|hangisini|seçer misin|karar ver)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Tool name + short target (file path or command excerpt, max 60 chars) from
    /// the MOST RECENT assistant event only — like the Electron collector, earlier
    /// events are not consulted.
    /// </summary>
    public static (string? Tool, string? Target) LastToolUse(IReadOnlyList<JsonElement> events)
    {
        for (int i = events.Count - 1; i >= 0; i--)
        {
            if (GetString(events[i], "type") != "assistant") continue;
            if (MessageContent(events[i]) is { } content)
            {
                for (int j = content.GetArrayLength() - 1; j >= 0; j--)
                {
                    var block = content[j];
                    if (GetString(block, "type") == "tool_use" &&
                        GetString(block, "name") is { Length: > 0 } name)
                        return (name, ToolActivity(name, block));
                }
            }
            break; // only the last assistant event counts
        }
        return (null, null);
    }

    /// <summary>Last meaningful turn event (assistant or user), skipping summary/system noise.</summary>
    public static JsonElement? LastTurn(IReadOnlyList<JsonElement> events)
    {
        for (int i = events.Count - 1; i >= 0; i--)
        {
            var type = GetString(events[i], "type");
            if (type == "assistant" || type == "user") return events[i];
        }
        return null;
    }

    /// <summary>
    /// Is the last turn a proposed tool with no result yet? (Either the tool is still
    /// executing, or the agent is blocked on an approval prompt — which of the two is
    /// decided by the registry status, not by this.)
    /// </summary>
    public static bool IsPendingTool(IReadOnlyList<JsonElement> events)
        => LastTurn(events) is { } last &&
           GetString(last, "type") == "assistant" &&
           StopReason(last) == "tool_use";

    // ---------- status derivation ----------

    /// <summary>
    /// Derives the status of an OPEN session. <paramref name="registryStatus"/> is
    /// Claude Code's own signal ("busy"/"waiting" while working, "idle" when the turn
    /// is back with you) — authoritative when fresh; CLI sessions report it, desktop
    /// ones may not, so we fall back to transcript turn-ownership and stay
    /// conservative there.
    /// </summary>
    public static SessionStatus DeriveStatus(
        IReadOnlyList<JsonElement> events,
        TimeSpan transcriptAge,
        string? registryStatus,
        TimeSpan? statusAge)
    {
        if (LastTurn(events) is not { } last) return SessionStatus.Finished;

        // A live busy/waiting means genuinely working — but the status only updates
        // on transitions, so require it to be recent; a stale one is a hung session.
        bool statusFresh = statusAge is { } sa && sa < BusyMax;
        if ((registryStatus == "busy" || registryStatus == "waiting") && statusFresh)
            return SessionStatus.Working;

        bool active = transcriptAge < BusyMax; // plausibly still working (bounds abandonment)

        if (GetString(last, "type") == "assistant")
        {
            if (StopReason(last) == "tool_use")
            {
                // Pending tool, no result yet. CLI reports 'idle' at an approval
                // prompt (waiting on you); otherwise it's the agent's turn.
                if (registryStatus == "idle") return SessionStatus.NeedsYou;
                return active ? SessionStatus.Working : SessionStatus.NeedsYou;
            }

            // Assistant ended its turn -> it's YOUR turn: a question, or simply done.
            // Never "working" here, however recent.
            var (tool, _) = LastToolUse(events);
            return IsAsking(LastAssistantText(events)) || tool == "AskUserQuestion"
                ? SessionStatus.NeedsYou
                : SessionStatus.Finished;
        }

        // Last event is a user message — a new prompt, or a tool result fed back to
        // the agent. Either way it's the AGENT'S turn: it's thinking/composing (the
        // transcript stays frozen the whole time). Working — bounded so an abandoned
        // mid-turn session eventually stops looking active.
        return active ? SessionStatus.Working : SessionStatus.NeedsYou;
    }

    // ---------- token spend (incremental) ----------

    /// <summary>Per-transcript incremental parse state: byte offset consumed + running token total.</summary>
    public readonly record struct SpendState(long Offset, long Tokens);

    /// <summary>
    /// Advances the cumulative token spend by parsing only the bytes appended since
    /// <paramref name="previous"/> (fresh input + newly-written cache + output; cheap
    /// cache RE-reads excluded). Only complete lines are consumed — a partial trailing
    /// line is left for the next call. A truncated/rotated file restarts from zero.
    /// Never throws.
    /// </summary>
    public static SpendState AddSpend(string transcriptPath, SpendState previous)
    {
        try
        {
            using var fs = new FileStream(
                transcriptPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            long size = fs.Length;

            // File rotated/truncated -> reparse from the start.
            var start = previous.Offset <= size ? previous : default(SpendState);
            long need = size - start.Offset;
            if (need <= 0) return start;
            if (need > int.MaxValue) return start; // absurd; refuse to allocate

            var buf = new byte[need];
            fs.Position = start.Offset;
            fs.ReadExactly(buf, 0, buf.Length);

            int lastNl = Array.LastIndexOf(buf, (byte)'\n');
            if (lastNl < 0) return start; // no complete new line yet

            long tokens = start.Tokens;
            var text = Encoding.UTF8.GetString(buf, 0, lastNl);
            foreach (var line in text.Split('\n'))
            {
                // Cheap pre-filter: only assistant turns carry a usage object, so skip
                // the (often huge) tool-result/user lines without paying to parse them.
                if (!line.Contains("\"output_tokens\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Object &&
                        root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object &&
                        msg.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                    {
                        tokens += GetLong(usage, "input_tokens")
                                + GetLong(usage, "cache_creation_input_tokens")
                                + GetLong(usage, "output_tokens");
                    }
                }
                catch (JsonException)
                {
                }
            }
            return new SpendState(start.Offset + lastNl + 1, tokens);
        }
        catch
        {
            return previous;
        }
    }

    // ---------- registry + transcript discovery ----------

    /// <summary>Parses one sessions\pid.json registry file. Null when unreadable or incomplete.</summary>
    public static RegistrySession? ParseRegistryFile(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var sessionId = GetString(root, "sessionId");
            int pid = 0;
            if (root.TryGetProperty("pid", out var pidEl))
            {
                if (pidEl.ValueKind == JsonValueKind.Number) pid = (int)GetLong(root, "pid");
                else if (pidEl.ValueKind == JsonValueKind.String &&
                         int.TryParse(pidEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                    pid = parsed;
            }
            if (string.IsNullOrEmpty(sessionId) || pid <= 0) return null;

            var entrypoint = GetString(root, "entrypoint");
            return new RegistrySession(
                pid,
                sessionId,
                GetString(root, "cwd"),
                string.IsNullOrEmpty(entrypoint) ? "cli" : entrypoint,
                GetString(root, "status"),
                ParseTimestamp(root, "statusUpdatedAt"));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Maps sessionId -> transcript path by scanning projects\ recursively for *.jsonl.</summary>
    public static Dictionary<string, string> TranscriptMap(string projectsRoot)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in Directory.EnumerateFiles(projectsRoot, "*.jsonl", SearchOption.AllDirectories))
                map[Path.GetFileNameWithoutExtension(file)] = file;
        }
        catch
        {
            // missing dir / races while Claude Code rewrites folders — partial map is fine
        }
        return map;
    }

    // ---------- snapshot ----------

    /// <summary>
    /// Builds the full display snapshot for one Claude home directory: live registry
    /// entries joined to transcripts, statuses derived, spend advanced incrementally
    /// (via <paramref name="spendCache"/>, which is also pruned of closed sessions),
    /// deduped per cwd keeping the most recently active, sorted NeedsYou, Finished,
    /// Working (most recent first within a rank), capped at 6 rows.
    /// </summary>
    public static List<AgentSession> BuildSnapshot(
        string claudeHome,
        IDictionary<string, SpendState> spendCache,
        Func<int, bool> isAlive,
        DateTime nowUtc,
        Func<int, int>? hostPreference = null)
    {
        var live = new List<RegistrySession>();
        try
        {
            var sessionsDir = Path.Combine(claudeHome, "sessions");
            if (Directory.Exists(sessionsDir))
            {
                foreach (var file in Directory.EnumerateFiles(sessionsDir, "*.json"))
                {
                    if (ParseRegistryFile(file) is not { } reg) continue;
                    bool alive;
                    try { alive = isAlive(reg.Pid); }
                    catch { alive = false; }
                    if (alive) live.Add(reg); // dead process -> stale crash leftover, skip
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("sessions: registry scan failed: " + ex.Message);
        }

        // Evict spend state for sessions that no longer exist so the cache can't grow.
        var liveIds = new HashSet<string>(live.Select(r => r.SessionId), StringComparer.OrdinalIgnoreCase);
        foreach (var stale in spendCache.Keys.Where(k => !liveIds.Contains(k)).ToList())
            spendCache.Remove(stale);

        if (live.Count == 0) return new List<AgentSession>();

        var transcripts = TranscriptMap(Path.Combine(claudeHome, "projects"));
        var candidates = new List<AgentSession>();

        foreach (var reg in live)
        {
            try
            {
                if (!transcripts.TryGetValue(reg.SessionId, out var file)) continue;

                DateTime mtimeUtc, startedUtc;
                try
                {
                    var info = new FileInfo(file);
                    if (!info.Exists) continue;
                    mtimeUtc = info.LastWriteTimeUtc;
                    // The transcript is created when the session starts — its age is
                    // how long this agent has been on the task.
                    startedUtc = info.CreationTimeUtc;
                }
                catch { continue; }

                var age = nowUtc - mtimeUtc;
                var events = ReadTailEvents(file);
                if (events.Count == 0) continue;

                TimeSpan? statusAge = reg.StatusUpdatedAtUtc is { } su ? nowUtc - su : null;
                var status = DeriveStatus(events, age, reg.Status, statusAge);

                // Only agents running NOW: drop any non-working session gone quiet.
                if (status != SessionStatus.Working && age > IdleKeep) continue;

                spendCache.TryGetValue(reg.SessionId, out var prevSpend);
                var spend = AddSpend(file, prevSpend);
                spendCache[reg.SessionId] = spend;

                string? tool = null, target = null;
                if (status == SessionStatus.Working ||
                    (status == SessionStatus.NeedsYou && IsPendingTool(events)))
                    (tool, target) = LastToolUse(events);

                var cwd = reg.Cwd ?? "";
                candidates.Add(new AgentSession
                {
                    SessionId = reg.SessionId,
                    Cwd = cwd,
                    ProjectName = ProjectNameOf(cwd),
                    Entrypoint = reg.Entrypoint,
                    StartedUtc = startedUtc,
                    Pid = reg.Pid,
                    Status = status,
                    CurrentTool = tool,
                    CurrentTarget = target,
                    LastMessage = Excerpt(LastAssistantText(events), 120),
                    TokensSpent = spend.Tokens,
                    LastActivityUtc = mtimeUtc,
                });
            }
            catch (Exception ex)
            {
                AppLog.Write("sessions: snapshot of " + reg.SessionId + " failed: " + ex.Message);
            }
        }

        // A CLI leaves older registry files behind when new tabs open in the same
        // project. The notch represents the project, not every historical tab — keep
        // the MOST RECENTLY ACTIVE session per folder, so a stale leftover can't mask
        // the session that's running right now.
        var byProject = new Dictionary<string, AgentSession>(StringComparer.Ordinal);
        foreach (var s in candidates)
        {
            var key = (s.Cwd.Length > 0 ? s.Cwd : s.SessionId).Replace('\\', '/').ToLowerInvariant();
            if (!byProject.TryGetValue(key, out var prev) || PreferOver(s, prev, hostPreference))
                byProject[key] = s;
        }

        return byProject.Values
            .OrderBy(s => StatusRank(s.Status))
            .ThenByDescending(s => s.LastActivityUtc)
            .Take(6)
            .ToList();
    }

    /// <summary>
    /// When two sessions share a project, which represents it? The one hosted in an
    /// editor (Orca/VS Code) beats a plain-terminal one — it's the window the user
    /// looks at and expects a row-click to raise — and only then does recency break
    /// the tie. With no host classifier both rank equal, so recency alone decides.
    /// </summary>
    private static bool PreferOver(AgentSession candidate, AgentSession current, Func<int, int>? hostPreference)
    {
        int rc = hostPreference?.Invoke(candidate.Pid) ?? 0;
        int rp = hostPreference?.Invoke(current.Pid) ?? 0;
        if (rc != rp) return rc < rp;
        return candidate.LastActivityUtc > current.LastActivityUtc;
    }

    /// <summary>Display order: attention first.</summary>
    private static int StatusRank(SessionStatus status) => status switch
    {
        SessionStatus.NeedsYou => 0,
        SessionStatus.Finished => 1,
        _ => 2,
    };

    /// <summary>Last path segment of a cwd; "session" when unknown.</summary>
    public static string ProjectNameOf(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return "session";
        var trimmed = cwd.TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    /// <summary>First line of a text, trimmed, capped at <paramref name="max"/> chars (ellipsis when cut).</summary>
    public static string? Excerpt(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = text.Trim();
        int nl = line.IndexOf('\n');
        if (nl >= 0) line = line[..nl].TrimEnd();
        if (line.Length > max) line = line[..(max - 1)] + "…";
        return line.Length == 0 ? null : line;
    }

    // ---------- low-level JSON helpers ----------

    private static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long GetLong(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Number)
            return 0;
        if (value.TryGetInt64(out var n)) return n;
        try { return (long)value.GetDouble(); }
        catch { return 0; }
    }

    /// <summary>message.content when present and an array; null otherwise.</summary>
    private static JsonElement? MessageContent(JsonElement evt)
    {
        if (evt.ValueKind != JsonValueKind.Object) return null;
        if (!evt.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) return null;
        if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return null;
        return content;
    }

    /// <summary>stopReason on the event, else message.stop_reason.</summary>
    private static string? StopReason(JsonElement evt)
    {
        if (GetString(evt, "stopReason") is { } top) return top;
        if (evt.ValueKind == JsonValueKind.Object &&
            evt.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object)
            return GetString(msg, "stop_reason");
        return null;
    }

    /// <summary>Short display target for a tool_use block: file path, else command excerpt.</summary>
    /// <summary>
    /// What the agent is doing, in a few readable words: "Writing middleware.ts",
    /// "List project directory contents", "Searching the web". Raw arguments are
    /// never shown — a shell one-liner or an absolute path tells you nothing at a
    /// glance, so files are reduced to their name and commands prefer the
    /// human description Claude Code sends with every Bash call.
    /// </summary>
    private static string? ToolActivity(string tool, JsonElement toolUse)
    {
        if (toolUse.ValueKind != JsonValueKind.Object ||
            !toolUse.TryGetProperty("input", out var input) ||
            input.ValueKind != JsonValueKind.Object)
            return null;

        var file = GetString(input, "file_path");
        var description = GetString(input, "description");
        var command = GetString(input, "command");
        var pattern = GetString(input, "pattern");

        switch (tool)
        {
            case "Edit":
            case "MultiEdit":
            case "NotebookEdit":
                return file is { Length: > 0 } ? "Editing " + FileName(file) : "Editing a file";

            case "Write":
                return file is { Length: > 0 } ? "Writing " + FileName(file) : "Writing a file";

            case "Read":
            case "NotebookRead":
                return file is { Length: > 0 } ? "Reading " + FileName(file) : "Reading a file";

            case "Bash":
            case "PowerShell":
            case "BashOutput":
                if (description is { Length: > 0 }) return Excerpt(description, 46);
                return command is { Length: > 0 } ? "Running " + ShortCommand(command) : "Running a command";

            case "Grep":
            case "Glob":
                return pattern is { Length: > 0 } ? "Searching " + Excerpt(pattern, 28) : "Searching the code";

            case "WebSearch":
                return "Searching the web";

            case "WebFetch":
                return "Reading a web page";

            case "Task":
                return "Running a subagent";

            case "TodoWrite":
                return "Updating the plan";

            case "AskUserQuestion":
                return "Asking you a question";

            default:
                if (description is { Length: > 0 }) return Excerpt(description, 46);
                if (file is { Length: > 0 }) return tool + " " + FileName(file);
                return tool;
        }
    }

    /// <summary>Last path segment — "middleware.ts", not the whole absolute path.</summary>
    private static string FileName(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        int cut = trimmed.LastIndexOfAny(new[] { '\\', '/' });
        var name = cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
        return name.Length <= 34 ? name : name[..34] + "…";
    }

    /// <summary>
    /// A command worth reading: short ones in full, scripted one-liners reduced to
    /// the program that runs them (and giving up entirely when even that is noise).
    /// </summary>
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

    /// <summary>Reads a timestamp that may be epoch milliseconds (Date.now()) or an ISO string.</summary>
    private static DateTime? ParseTimestamp(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt64(out var ms))
                return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
            try { return DateTimeOffset.FromUnixTimeMilliseconds((long)value.GetDouble()).UtcDateTime; }
            catch { return null; }
        }
        if (value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            return dto.UtcDateTime;
        return null;
    }
}
