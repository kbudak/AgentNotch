using System.Collections.Concurrent;
using System.Globalization;
using System.Buffers;
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

    /// <summary>Fixed window AddSpend streams through, whatever the file's size.</summary>
    private const int SpendWindowBytes = 256 * 1024;

    /// <summary>
    /// A busy/waiting registry status older than this is a leftover from a hung or
    /// abandoned session, not a live one. Set well above a single long task so a
    /// genuinely busy session — including one running a background job (a workflow)
    /// after its visible turn ended — keeps counting as working. Also bounds how long
    /// a mid-turn transcript keeps counting as "working".
    /// </summary>
    public static readonly TimeSpan BusyMax = TimeSpan.FromMinutes(45);

    /// <summary>
    /// How long a "busy" registry status is trusted while the transcript stays frozen.
    /// The registry only flips busy/idle on a turn transition, so an interrupted or
    /// orphaned session can sit at "busy" for the whole <see cref="BusyMax"/> window
    /// even though nothing is running. Real work keeps appending to the transcript
    /// (thinking, tool calls, results) every minute or so — the one exception being a
    /// tool call still in flight, which is handled separately. So outside a pending
    /// tool, only treat a session as working while its transcript is this fresh.
    /// Set generously enough to cover a long think/compose gap between transcript
    /// writes, so a genuinely busy session doesn't briefly flip out of "working".
    /// </summary>
    public static readonly TimeSpan WorkingGrace = TimeSpan.FromMinutes(5);

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
            var buf = ArrayPool<byte>.Shared.Rent(len);
            try
            {
                fs.ReadExactly(buf, 0, len);
                return ParseLines(buf, 0, len);
            }
            finally { ArrayPool<byte>.Shared.Return(buf); }
        }
        catch
        {
            return new List<JsonElement>();
        }
    }

    private readonly record struct TailEntry(DateTime MtimeUtc, long Size, IReadOnlyList<JsonElement> Events);

    private static readonly ConcurrentDictionary<string, TailEntry> TailCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <see cref="ReadTailEvents"/> memoised on (mtime, size) — the same deal
    /// <see cref="CursorTail"/> already makes for its transcripts.
    /// <see cref="BuildSnapshot"/> runs on every emit, up to once a second, but a
    /// transcript only changes when its agent writes: without this the same 256 KB
    /// window is re-read and re-parsed (and every surviving element re-cloned) for
    /// every idle session, every tick, forever. The returned list is shared —
    /// callers must treat it as read-only.
    /// </summary>
    internal static IReadOnlyList<JsonElement> ReadTailEventsCached(string path, DateTime mtimeUtc, long size)
    {
        if (TailCache.TryGetValue(path, out var hit) && hit.MtimeUtc == mtimeUtc && hit.Size == size)
            return hit.Events;

        var events = ReadTailEvents(path);
        TailCache[path] = new TailEntry(mtimeUtc, size, events);
        return events;
    }

    /// <summary>Drops memo entries for transcripts this snapshot pass did not touch.</summary>
    private static void PruneTailCache(HashSet<string> keep)
    {
        foreach (var path in TailCache.Keys)
            if (!keep.Contains(path)) TailCache.TryRemove(path, out _);
    }

    /// <summary>Parses NDJSON text; unparseable lines (e.g. a cut-off first line) are skipped.</summary>
    public static List<JsonElement> ParseLines(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return ParseLines(bytes, 0, bytes.Length);
    }

    /// <summary>
    /// Parses NDJSON straight out of a UTF-8 buffer. Deliberately never builds a
    /// string for the window or an array of its lines: at a 256 KB tail on a 2s
    /// poll both land on the large object heap, which is not compacted, so the
    /// churn showed up as steadily climbing private bytes rather than as garbage.
    /// JsonDocument reads each line in place; only the surviving elements are
    /// cloned out.
    /// </summary>
    public static List<JsonElement> ParseLines(byte[] buffer, int offset, int count)
    {
        var events = new List<JsonElement>();
        int end = offset + count, start = offset;
        while (start < end)
        {
            int nl = Array.IndexOf(buffer, (byte)'\n', start, end - start);
            int stop = nl < 0 ? end : nl;
            int len = stop - start;
            while (len > 0 && IsBlank(buffer[start + len - 1])) len--;      // trailing \r / space
            int from = start;
            while (len > 0 && IsBlank(buffer[from])) { from++; len--; }     // leading space
            if (len > 0)
            {
                try
                {
                    using var doc = JsonDocument.Parse(new ReadOnlyMemory<byte>(buffer, from, len));
                    events.Add(doc.RootElement.Clone());
                }
                catch (JsonException)
                {
                    // partial or malformed line — tolerated by design
                }
            }
            if (nl < 0) break;
            start = nl + 1;
        }
        return events;
    }

    private static bool IsBlank(byte b) => b == (byte)' ' || b == (byte)'\r' || b == (byte)'\t';

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

    /// <summary>
    /// When the agent last actually said or did something: the timestamp on the most
    /// recent assistant/user record, or null if the tail holds none.
    ///
    /// This is NOT the file's mtime, and the difference is the whole point. Claude
    /// Code keeps appending bookkeeping to a transcript long after the conversation
    /// is over — `bridge-session` records, and `system`/`local_command` notices such
    /// as Remote Control connecting and disconnecting. Every one of those touches the
    /// file. A session whose last real turn was two days ago was still showing an
    /// mtime nine minutes old, which reset its age, un-dropped it, and put it back on
    /// the notch reading "Done" forever.
    /// </summary>
    public static DateTime? LastTurnTimeUtc(IReadOnlyList<JsonElement> events)
    {
        for (int i = events.Count - 1; i >= 0; i--)
        {
            var type = GetString(events[i], "type");
            if (type is not ("assistant" or "user")) continue;
            if (ParseTimestamp(events[i], "timestamp") is { } stamp) return stamp;
        }
        return null;
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

    /// <summary>
    /// How long a browser tool call may stay unanswered before it is read as waiting
    /// on its site-permission dialog. Measured on a real session: 12 finished
    /// Claude_Browser calls took 0.7–4.9s when nothing asked, and 20–66s each time
    /// the dialog was up.
    /// ponytail: a timing heuristic, not a signal — a genuinely slow page load past
    /// 8s shows orange until it returns. Swap for a real event if the browser tool
    /// ever surfaces one (a hook, or a state file the app writes).
    /// </summary>
    public static readonly TimeSpan BrowserPromptAfter = TimeSpan.FromSeconds(8);

    /// <summary>Tools that answer to a per-site permission dialog in the app or extension.</summary>
    public static bool IsBrowserTool(string? tool)
        => tool is not null &&
           (tool.StartsWith("mcp__Claude_Browser__", StringComparison.Ordinal) ||
            tool.StartsWith("mcp__claude-in-chrome__", StringComparison.Ordinal));

    /// <summary>The last turn is a browser tool call with no result yet.</summary>
    public static bool IsPendingBrowserTool(IReadOnlyList<JsonElement> events)
        => LastTurn(events) is { } last &&
           GetString(last, "type") == "assistant" &&
           LastContentBlockType(last) == "tool_use" &&
           IsBrowserTool(LastToolUse(events).Tool);

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

        bool statusFresh = statusAge is { } sa && sa < BusyMax;
        bool transcriptFresh = transcriptAge < WorkingGrace;

        // A browser tool held at its own per-site "Allow Claude to …?" dialog. That
        // dialog belongs to the browser tool, inside a call Claude Code has already
        // started, so no PermissionRequest or Notification hook fires and the
        // registry keeps saying "busy". The one trace is the call sitting unanswered
        // far longer than browser calls take — which is why this has to run before
        // the busy check below, or it would read as ordinary work.
        if (IsPendingBrowserTool(events) && transcriptAge >= BrowserPromptAfter)
            return SessionStatus.NeedsYou;

        // Claude Code marks a session "busy"/"waiting" while it's doing work and only
        // flips to "idle" when the turn is handed back to you (interrupts flip to idle
        // too). It stays busy while a background job — e.g. a workflow — keeps running
        // after the visible turn ended and the transcript went quiet. So a busy status
        // that hasn't gone stale means working, regardless of what the transcript's
        // last event was.
        if ((registryStatus == "busy" || registryStatus == "waiting") && statusFresh)
            return SessionStatus.Working;

        if (GetString(last, "type") == "assistant")
        {
            var reason = StopReason(last);
            var block = LastContentBlockType(last);

            // Claude Code writes one transcript line PER CONTENT BLOCK as the model
            // streams, so the last line is usually mid-turn, not the end of one — and
            // its stop_reason is often still absent when the line is written. The
            // evidence is the block, not the reason: a turn ends on TEXT the model
            // signed off, and `thinking` / `tool_use` are never where it stops.
            bool midTurn = block is "thinking" or "tool_use"
                           || reason is null or "" or "tool_use";

            // Orange means the agent is asking you something RIGHT NOW. Only two things
            // in a transcript prove that: an ask tool waiting for its answer, or a tool
            // waiting for approval while the CLI sits idle at the permission prompt.
            if (block == "tool_use" && IsAskTool(LastToolUse(events).Tool))
                return SessionStatus.NeedsYou;
            if (block == "tool_use" && registryStatus == "idle" && (statusFresh || reason == "tool_use"))
                return SessionStatus.NeedsYou;

            if (midTurn)
            {
                // Idle on anything else is an interrupt, or a stream that finished before
                // its stop_reason was written (10 of 47 finished transcripts end that way).
                // The turn is back with you — but nothing is being asked.
                if (registryStatus == "idle" && statusFresh) return SessionStatus.Finished;

                // A thinking or tool_use block proves the agent is mid-turn, so it may
                // stay Working for the long window; a bare text block with no reason is
                // only PROBABLY mid-stream and gets the short grace.
                var window = block is "thinking" or "tool_use" ? BusyMax : WorkingGrace;
                if (transcriptAge < window) return SessionStatus.Working;
            }

            // The turn is over. Whatever its closing words — "Hangisini istersin?",
            // "nasıl devam edelim?" — the agent has stopped and is waiting on nothing:
            // a question at the END of the work is not a question DURING it.
            return SessionStatus.Finished;
        }

        // Last turn is a user record: a prompt or tool result the agent is working on —
        // or something written after the work ended (a local /command's output,
        // "[Request interrupted by user]"). Fresh and not idle: the agent has the turn.
        // Otherwise it is back with you, and still nobody is asking.
        if (registryStatus == "idle" && statusFresh) return SessionStatus.Finished;
        return transcriptFresh ? SessionStatus.Working : SessionStatus.Finished;
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

            fs.Position = start.Offset;
            long tokens = start.Tokens;
            long consumed = start.Offset;

            // Streamed through one pooled window rather than materialised. The old
            // path allocated byte[need] + a UTF-16 string + a Split array of every
            // line — about five times the file's size, all of it on the large object
            // heap. On a first pass over a 55 MB transcript that alone is ~275 MB
            // that the GC never hands back, and this runs for every tracked session.
            var buf = ArrayPool<byte>.Shared.Rent(SpendWindowBytes);
            try
            {
                int held = 0; // bytes of an incomplete trailing line, carried forward
                while (true)
                {
                    int read = fs.Read(buf, held, buf.Length - held);
                    if (read <= 0) break;
                    int end = held + read;
                    int lineStart = 0;
                    while (true)
                    {
                        int nl = Array.IndexOf(buf, (byte)'\n', lineStart, end - lineStart);
                        if (nl < 0) break;
                        tokens += TokensInLine(buf, lineStart, nl - lineStart);
                        consumed += nl - lineStart + 1;
                        lineStart = nl + 1;
                    }
                    held = end - lineStart;
                    if (held > 0 && lineStart > 0) Buffer.BlockCopy(buf, lineStart, buf, 0, held);
                    if (held == buf.Length)
                    {
                        // A single line longer than the window can never complete.
                        // Skip it rather than growing the buffer without bound; usage
                        // lines are small, the giant ones are tool results.
                        consumed += held;
                        held = 0;
                    }
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buf); }

            return new SpendState(consumed, tokens);
        }
        catch
        {
            return previous;
        }
    }

    /// <summary>Token total on one transcript line, or 0 when it carries no usage block.</summary>
    private static long TokensInLine(byte[] buffer, int offset, int length)
    {
        // Cheap pre-filter on the raw bytes: only assistant turns carry a usage
        // object, so the (often huge) tool-result and user lines cost one scan
        // instead of a parse — and no string.
        if (length <= 0 || buffer.AsSpan(offset, length).IndexOf(UsageKey) < 0) return 0;
        try
        {
            using var doc = JsonDocument.Parse(new ReadOnlyMemory<byte>(buffer, offset, length));
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object &&
                msg.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                return GetLong(usage, "input_tokens")
                     + GetLong(usage, "cache_creation_input_tokens")
                     + GetLong(usage, "output_tokens");
            }
        }
        catch (JsonException)
        {
        }
        return 0;
    }

    private static readonly byte[] UsageKey = Encoding.UTF8.GetBytes("\"output_tokens\"");

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

    /// <summary>
    /// How long the sessionId -> path map is trusted. It is a RECURSIVE walk of every
    /// project folder the user has ever opened, and it was running on every emit — up
    /// to once a second — for a map that only changes when a session is created or
    /// removed. Measured at 4.85 ms of BuildSnapshot's 5.48 ms: nearly the whole cost
    /// of the Claude side went on re-deriving something that had not moved.
    /// Appending to a transcript never changes it; <see cref="InvalidateTranscriptMap"/>
    /// covers the create/delete/rename that does.
    /// </summary>
    private static readonly TimeSpan MapTtl = TimeSpan.FromSeconds(30);

    private static readonly object MapGate = new();
    private static Dictionary<string, string>? _map;
    private static string _mapRoot = "";
    private static long _mapAtMs = -1;

    /// <summary>Forces the next snapshot to re-walk — the watcher calls this when a file appears or goes.</summary>
    public static void InvalidateTranscriptMap()
    {
        lock (MapGate) _mapAtMs = -1;
    }

    private static Dictionary<string, string> CachedTranscriptMap(string projectsRoot)
    {
        lock (MapGate)
        {
            long now = Environment.TickCount64;
            if (_map is not null && _mapRoot == projectsRoot && _mapAtMs >= 0
                && now - _mapAtMs <= MapTtl.TotalMilliseconds)
                return _map;

            var map = TranscriptMap(projectsRoot);
            // An empty result usually means the walk raced a rewrite, not that every
            // session vanished — keep the last good map rather than blanking the notch.
            if (map.Count == 0 && _map is { Count: > 0 } && _mapRoot == projectsRoot)
                return _map;

            _map = map;
            _mapRoot = projectsRoot;
            _mapAtMs = now;
            return map;
        }
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

        // Transcripts seen this pass; anything else in the memo is a closed session.
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (live.Count == 0)
        {
            PruneTailCache(touched);
            return new List<AgentSession>();
        }

        var transcripts = CachedTranscriptMap(Path.Combine(claudeHome, "projects"));
        var candidates = new List<AgentSession>();

        foreach (var reg in live)
        {
            try
            {
                // Registry sessionId goes stale after an editor resume/compaction
                // (the live transcript rotates to a new id the <pid>.json never learns),
                // so an editor-hosted session can have no transcript under its recorded
                // id. Fall back to the newest transcript in the same project dir so the
                // session still shows and its row can jump to the right window.
                if (!transcripts.TryGetValue(reg.SessionId, out var file))
                {
                    file = NewestTranscriptForCwd(claudeHome, reg.Cwd);
                    if (file is null) continue;
                }

                DateTime mtimeUtc, startedUtc;
                long size;
                try
                {
                    var info = new FileInfo(file);
                    if (!info.Exists) continue;
                    mtimeUtc = info.LastWriteTimeUtc;
                    size = info.Length;
                    // The transcript is created when the session starts — its age is
                    // how long this agent has been on the task.
                    startedUtc = info.CreationTimeUtc;
                }
                catch { continue; }

                touched.Add(file);
                var events = ReadTailEventsCached(file, mtimeUtc, size);
                if (events.Count == 0) continue;

                // Age from the last TURN, never from the file's mtime — see
                // LastTurnTimeUtc. Falling back to mtime only when the tail holds no
                // turn at all keeps a transcript we cannot read from vanishing, and a
                // future-dated record cannot make a session look eternally fresh.
                var activityUtc = LastTurnTimeUtc(events) ?? mtimeUtc;
                if (activityUtc > nowUtc) activityUtc = nowUtc;
                var age = nowUtc - activityUtc;

                TimeSpan? statusAge = reg.StatusUpdatedAtUtc is { } su ? nowUtc - su : null;
                var status = DeriveStatus(events, age, reg.Status, statusAge);

                // Only agents running NOW: drop any non-working session gone quiet.
                if (status != SessionStatus.Working && age > IdleKeep) continue;

                spendCache.TryGetValue(reg.SessionId, out var prevSpend);
                // Nothing appended since the last pass -> AddSpend would open the
                // file only to compute a zero-byte delta. Skip the open.
                var spend = prevSpend.Offset == size ? prevSpend : AddSpend(file, prevSpend);
                spendCache[reg.SessionId] = spend;

                string? tool = null, target = null;
                bool browserGate = status == SessionStatus.NeedsYou && IsPendingBrowserTool(events);
                if (status == SessionStatus.Working ||
                    (status == SessionStatus.NeedsYou && (IsPendingTool(events) || browserGate)))
                    (tool, target) = LastToolUse(events);
                // The notch cannot answer this dialog — it lives in the app — so the
                // row says where the question is; clicking it jumps there.
                if (browserGate) target = "Waiting for you to allow it";

                // Mid-thought there is no tool to name, so the row would fall back to
                // the previous narration — which reads as if the turn were over, the
                // very impression the status fix exists to correct. Say what is
                // actually happening instead.
                if (status == SessionStatus.Working && tool is null && target is null
                    && LastTurn(events) is { } turn && LastContentBlockType(turn) == "thinking")
                    target = "Thinking…";

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
                    LastActivityUtc = activityUtc,
                });
            }
            catch (Exception ex)
            {
                AppLog.Write("sessions: snapshot of " + reg.SessionId + " failed: " + ex.Message);
            }
        }

        PruneTailCache(touched);

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

    /// <summary>
    /// Newest transcript in the project dir for <paramref name="cwd"/>, or null.
    /// Claude Code encodes a cwd into a dir name by replacing every non-alphanumeric
    /// char with '-'; the drive letter's case is inconsistent, so match case-insensitively.
    /// </summary>
    internal static string? NewestTranscriptForCwd(string claudeHome, string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return null;
        try
        {
            var projects = Path.Combine(claudeHome, "projects");
            if (!Directory.Exists(projects)) return null;

            var encoded = System.Text.RegularExpressions.Regex.Replace(cwd, "[^A-Za-z0-9]", "-");
            foreach (var dir in Directory.EnumerateDirectories(projects))
            {
                if (!string.Equals(Path.GetFileName(dir), encoded, StringComparison.OrdinalIgnoreCase))
                    continue;

                string? newest = null;
                DateTime newestUtc = DateTime.MinValue;
                foreach (var f in Directory.EnumerateFiles(dir, "*.jsonl"))
                {
                    var w = File.GetLastWriteTimeUtc(f);
                    if (w > newestUtc) { newestUtc = w; newest = f; }
                }
                return newest;
            }
        }
        catch { /* missing dir / permission — no fallback */ }
        return null;
    }

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
    /// <summary>A tool whose whole purpose is to block on the user's answer.</summary>
    private static bool IsAskTool(string? tool) =>
        !string.IsNullOrEmpty(tool)
        && (tool.Contains("AskUserQuestion", StringComparison.OrdinalIgnoreCase)
            || tool.Contains("AskQuestion", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Type of the LAST content block of a turn — "thinking", "tool_use", "text"
    /// (or null when the shape is unfamiliar). This is what says whether the model
    /// was mid-thought when the line was written; stop_reason often is not there yet.
    /// </summary>
    internal static string? LastContentBlockType(JsonElement evt)
    {
        try
        {
            if (evt.ValueKind != JsonValueKind.Object) return null;
            if (!evt.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object)
                return null;
            if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                return null;

            string? last = null;
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                if (GetString(block, "type") is { Length: > 0 } type) last = type;
            }
            return last;
        }
        catch { return null; }
    }

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
                if (IsBrowserTool(tool))
                {
                    // "mcp__Claude_Browser__javascript_tool" means nothing on a row.
                    var action = tool[(tool.LastIndexOf("__", StringComparison.Ordinal) + 2)..];
                    return action switch
                    {
                        "javascript_tool" or "javascript_exec" => "Running JavaScript in the browser",
                        "navigate" => "Opening a page",
                        "computer" => "Using the browser",
                        "read_page" or "get_page_text" or "find" => "Reading the page",
                        "form_input" => "Filling in a form",
                        _ => "Using the browser",
                    };
                }
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
