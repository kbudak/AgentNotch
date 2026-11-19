using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentNotch.App.Diagnostics;
using AgentNotch.App.Usage;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("AgentNotch.Tests")]

namespace AgentNotch.App.Sessions;

/// <summary>
/// Cursor IDE agent collector — tails <c>~\.cursor\projects\*\agent-transcripts\*\*.jsonl</c>
/// and elevates status from sibling <c>subagents\*.jsonl</c> (Task children).
/// Never throws.
/// </summary>
public static class CursorTail
{
    public const int TailBytes = 512 * 1024;
    public static readonly TimeSpan IdleKeep = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan WorkingKeep = TimeSpan.FromMinutes(45);
    private const int MaxFiles = 40;
    private const long MaxGrowBytes = 256L * 1024 * 1024;

    private static string? _lastCollectLog;

    private static readonly Regex WindowsPath =
        new(@"[A-Za-z]:\\[^\s""'\]\)>]+", RegexOptions.Compiled);

    private static readonly Regex AskQuestion =
        new(@"\?\s*$|\?\s*[""'\)]?\s*$", RegexOptions.Compiled);

    public static List<AgentSession> Collect(string cursorProjectsDir, DateTime nowUtc)
        => Collect(cursorProjectsDir, nowUtc, TailBytes);

    internal static List<AgentSession> Collect(string cursorProjectsDir, DateTime nowUtc, int tailBytes)
    {
        var sessions = new List<AgentSession>();
        int scanned = 0, skippedStale = 0, skippedNull = 0;
        try
        {
            foreach (var key in Cache.Keys)
            {
                if (!File.Exists(key)) Cache.TryRemove(key, out _);
            }

            foreach (var file in ListRecentFiles(cursorProjectsDir, nowUtc))
            {
                scanned++;
                var parentInfo = SafeFileInfo(file.Path);
                if (parentInfo is null) { skippedNull++; continue; }

                var session = CollectOne(
                    file.Path, file.ProjectSlug, parentInfo.Value.MtimeUtc, parentInfo.Value.Size, tailBytes);
                if (session is null) { skippedNull++; continue; }

                session = ElevateWithSubagents(file.Path, file.ProjectSlug, session, tailBytes);
                if (file.ActivityMtimeUtc > session.LastActivityUtc)
                    session.LastActivityUtc = file.ActivityMtimeUtc;

                var keep = session.Status is SessionStatus.Working or SessionStatus.NeedsYou
                    ? WorkingKeep
                    : IdleKeep;
                if (nowUtc - session.LastActivityUtc > keep)
                {
                    skippedStale++;
                    continue;
                }
                sessions.Add(session);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("cursor: collect failed: " + ex.Message);
        }

        var merged = sessions
            .GroupBy(s => string.IsNullOrEmpty(s.Cwd) ? s.SessionId : s.Cwd, StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderBy(s => StatusRank(s.Status))
                .ThenByDescending(s => s.LastActivityUtc)
                .First())
            .OrderBy(s => StatusRank(s.Status))
            .ThenByDescending(s => s.LastActivityUtc)
            .ToList();

        if (merged.Count > 0 || scanned > 0)
        {
            var summary = string.Join(", ", merged.Select(s => $"{s.ProjectName}:{s.Status}"));
            var line =
                $"cursor: found {merged.Count}/{scanned} sessions (null={skippedNull}, stale={skippedStale})"
                + (summary.Length > 0 ? " [" + summary + "]" : "");
            if (line != _lastCollectLog)
            {
                _lastCollectLog = line;
                AppLog.Write(line);
            }
        }

        return merged;
    }

    private static int StatusRank(SessionStatus s) => s switch
    {
        SessionStatus.NeedsYou => 0,
        SessionStatus.Working => 1,
        _ => 2,
    };

    private static (DateTime MtimeUtc, long Size)? SafeFileInfo(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Parent often <c>turn_ended</c> after spawning Task; live work / Allow-Stop
    /// lives under <c>subagents\</c>. Promote the richer child status onto the parent row.
    /// </summary>
    internal static AgentSession ElevateWithSubagents(
        string parentPath, string projectSlug, AgentSession parent, int tailBytes)
    {
        try
        {
            var sessionDir = Path.GetDirectoryName(parentPath);
            if (string.IsNullOrEmpty(sessionDir)) return parent;
            var subDir = Path.Combine(sessionDir, "subagents");
            if (!Directory.Exists(subDir)) return parent;

            var best = parent;
            foreach (var subPath in Directory.EnumerateFiles(subDir, "*.jsonl"))
            {
                var info = SafeFileInfo(subPath);
                if (info is null) continue;
                var child = CollectOne(subPath, projectSlug, info.Value.MtimeUtc, info.Value.Size, tailBytes);
                if (child is null) continue;

                if (child.LastActivityUtc > best.LastActivityUtc)
                    best.LastActivityUtc = child.LastActivityUtc;

                if (StatusRank(child.Status) < StatusRank(best.Status)
                    || (child.Status == best.Status
                        && child.Status != SessionStatus.Finished
                        && child.LastActivityUtc >= best.LastActivityUtc))
                {
                    best = new AgentSession
                    {
                        SessionId = parent.SessionId,
                        Agent = parent.Agent,
                        Host = parent.Host,
                        HostLabel = parent.HostLabel,
                        Model = parent.Model ?? CursorUsageClient.LatestModel ?? "Composer",
                        Usage = parent.Usage.Count > 0 ? parent.Usage : CursorUsageClient.LatestUsage,
                        Cwd = string.IsNullOrEmpty(parent.Cwd) ? child.Cwd : parent.Cwd,
                        ProjectName = parent.ProjectName,
                        Entrypoint = parent.Entrypoint,
                        Pid = parent.Pid,
                        Status = child.Status,
                        CurrentTool = child.CurrentTool,
                        CurrentTarget = child.CurrentTarget,
                        LastMessage = child.LastMessage,
                        TokensSpent = parent.TokensSpent,
                        StartedUtc = parent.StartedUtc,
                        LastActivityUtc = child.LastActivityUtc > parent.LastActivityUtc
                            ? child.LastActivityUtc
                            : parent.LastActivityUtc,
                    };
                }
            }
            return best;
        }
        catch (Exception ex)
        {
            AppLog.Write("cursor: subagent elevate failed: " + ex.Message);
            return parent;
        }
    }

    internal sealed record CacheEntry(DateTime MtimeUtc, long Size, AgentSession? Session);

    internal static readonly ConcurrentDictionary<string, CacheEntry> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    internal static AgentSession? CollectOne(
        string path, string projectSlug, DateTime mtimeUtc, long size, int tailBytes)
    {
        if (Cache.TryGetValue(path, out var hit) && hit.MtimeUtc == mtimeUtc && hit.Size == size)
            return hit.Session;

        AgentSession? session = null;
        try
        {
            session = Parse(path, projectSlug, mtimeUtc, tailBytes);
        }
        catch (Exception ex)
        {
            AppLog.Write("cursor: parse of " + path + " failed: " + ex.Message);
        }
        Cache[path] = new CacheEntry(mtimeUtc, size, session);
        return session;
    }

    private readonly record struct TranscriptFile(
        string Path, string ProjectSlug, long Size, DateTime ActivityMtimeUtc);

    /// <summary>Where a session's transcript lives. Paths only, no timestamps — cacheable.</summary>
    private readonly record struct TranscriptSite(string Path, string ProjectSlug, string SessionDir);

    /// <summary>
    /// How long the directory STRUCTURE is trusted. Appending to a transcript does
    /// not touch its parent directory's mtime, so there is no cheap way to spot a
    /// new session other than re-walking — but a new session is rare, while the
    /// re-walk is not: one "agent-transcripts" probe per project the user has EVER
    /// opened, on every poll tick, forever. Only the structure is cached; the mtimes
    /// that actually drive status are re-read every single tick.
    /// <see cref="InvalidateStructure"/> makes a new session appear at once.
    /// </summary>
    private static readonly TimeSpan StructureTtl = TimeSpan.FromSeconds(30);

    private static readonly object StructureGate = new();
    private static List<TranscriptSite>? _sites;
    private static string _sitesRoot = "";
    private static long _sitesAtMs = -1;

    /// <summary>Forces the next scan to re-walk — the watcher calls this on create/delete/rename.</summary>
    public static void InvalidateStructure()
    {
        lock (StructureGate) _sitesAtMs = -1;
    }

    private static List<TranscriptSite> Sites(string root)
    {
        lock (StructureGate)
        {
            long now = Environment.TickCount64;
            if (_sites is not null && _sitesRoot == root && _sitesAtMs >= 0
                && now - _sitesAtMs <= StructureTtl.TotalMilliseconds)
                return _sites;

            var sites = new List<TranscriptSite>();
            try
            {
                foreach (var projectDir in Directory.EnumerateDirectories(root))
                {
                    var slug = Path.GetFileName(projectDir);
                    if (string.IsNullOrEmpty(slug) || slug.StartsWith(".", StringComparison.Ordinal))
                        continue;

                    var transcriptsDir = Path.Combine(projectDir, "agent-transcripts");
                    if (!Directory.Exists(transcriptsDir)) continue;

                    foreach (var sessionDir in Directory.EnumerateDirectories(transcriptsDir))
                    {
                        var sessionId = Path.GetFileName(sessionDir);
                        if (string.IsNullOrEmpty(sessionId)) continue;
                        var jsonl = Path.Combine(sessionDir, sessionId + ".jsonl");
                        if (File.Exists(jsonl)) sites.Add(new TranscriptSite(jsonl, slug, sessionDir));
                    }
                }
            }
            catch
            {
                return _sites ?? new List<TranscriptSite>(); // keep the last good structure
            }

            _sites = sites;
            _sitesRoot = root;
            _sitesAtMs = now;
            return sites;
        }
    }

    private static List<TranscriptFile> ListRecentFiles(string root, DateTime nowUtc)
    {
        var files = new List<TranscriptFile>();
        if (!Directory.Exists(root)) return files;

        foreach (var site in Sites(root))
        {
            try
            {
                var info = new FileInfo(site.Path);
                if (!info.Exists) continue;
                var activity = info.LastWriteTimeUtc;

                // A Task child can be live while its parent has gone quiet, so a
                // subagent's mtime counts as the session's activity.
                var subDir = Path.Combine(site.SessionDir, "subagents");
                if (Directory.Exists(subDir))
                {
                    foreach (var sub in Directory.EnumerateFiles(subDir, "*.jsonl"))
                    {
                        try
                        {
                            var sm = File.GetLastWriteTimeUtc(sub);
                            if (sm > activity) activity = sm;
                        }
                        catch { /* vanished */ }
                    }
                }

                // Drop the long-dead BEFORE parsing. A transcript cannot be newer than
                // the file it was written into, so anything past the widest keep window
                // is discarded after the parse anyway — this just stops us reading
                // dozens of dead transcripts every tick to prove it.
                if (nowUtc - activity > WorkingKeep) continue;

                files.Add(new TranscriptFile(site.Path, site.ProjectSlug, info.Length, activity));
            }
            catch { /* vanished mid-scan */ }
        }

        return files.OrderByDescending(f => f.ActivityMtimeUtc).Take(MaxFiles).ToList();
    }

    internal static List<JsonElement> ReadEvents(string path, int tailBytes)
    {
        try
        {
            using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            long size = fs.Length;
            if (size <= 0) return new List<JsonElement>();
            long len = Math.Min(size, (long)tailBytes);
            while (true)
            {
                int want = (int)Math.Min(len, MaxGrowBytes);
                var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(want);
                List<JsonElement> events;
                try
                {
                    fs.Position = Math.Max(0, size - want);
                    fs.ReadExactly(buf, 0, want);
                    events = TranscriptTail.ParseLines(buf, 0, want);
                }
                finally { System.Buffers.ArrayPool<byte>.Shared.Return(buf); }

                if (events.Count > 0 || want >= size || len >= MaxGrowBytes)
                    return events;
                len = Math.Min(size, len * 2);
            }
        }
        catch
        {
            return new List<JsonElement>();
        }
    }

    internal static AgentSession? ParseEvents(
        IReadOnlyList<JsonElement> events, string projectSlug, DateTime mtimeUtc, string? sessionKey = null)
    {
        if (events.Count == 0) return null;

        string? cwd = DecodeProjectSlug(projectSlug);
        string? lastMessage = null;
        string? lastTool = null;
        string? lastTarget = null;
        string? lastQuestion = null;
        string? lastRole = null;
        bool lastHasToolUse = false;
        bool lastAskedUser = false;
        bool lastSpawnedTask = false;
        bool turnEnded = false;

        foreach (var ev in events)
        {
            var eventType = GetString(ev, "type");
            if (eventType is "turn_ended" or "turn_end")
            {
                turnEnded = true;
                lastHasToolUse = false;
                // Keep lastAskedUser / lastSpawnedTask — turn_end while a question
                // waits, or after Task spawn while children still run.
                continue;
            }

            var role = GetString(ev, "role");
            if (role is "user" or "assistant") lastRole = role;

            if (role != "assistant" && role != "user") continue;
            if (!ev.TryGetProperty("message", out var message)) continue;
            // Older transcripts: message is a bare string, or content is a string.
            if (message.ValueKind == JsonValueKind.String)
            {
                var text = message.GetString();
                if (role == "assistant" && !string.IsNullOrWhiteSpace(text))
                {
                    turnEnded = false;
                    lastSpawnedTask = false;
                    lastMessage = FirstLine(text);
                    cwd ??= ExtractCwd(text);
                    lastAskedUser = false; // question-shaped text is not an active ask
                    lastHasToolUse = false;
                }
                else if (role == "user")
                {
                    turnEnded = false;
                    lastAskedUser = false;
                    lastHasToolUse = false;
                    lastSpawnedTask = false;
                    lastQuestion = null;
                    lastTool = null;
                    lastTarget = null;
                }
                continue;
            }
            if (message.ValueKind != JsonValueKind.Object) continue;

            if (message.TryGetProperty("content", out var content))
            {
                if (content.ValueKind == JsonValueKind.String)
                {
                    var text = content.GetString();
                    if (role == "assistant" && !string.IsNullOrWhiteSpace(text))
                    {
                        turnEnded = false;
                        lastSpawnedTask = false;
                        lastMessage = FirstLine(text);
                        cwd ??= ExtractCwd(text);
                        lastAskedUser = false; // question-shaped text is not an active ask
                        lastHasToolUse = false;
                    }
                    else if (role == "user")
                    {
                        turnEnded = false;
                        lastAskedUser = false;
                        lastHasToolUse = false;
                        lastSpawnedTask = false;
                        lastQuestion = null;
                        lastTool = null;
                        lastTarget = null;
                    }
                    continue;
                }
                if (content.ValueKind != JsonValueKind.Array) continue;
            }
            else continue;

            bool hasToolUse = false;
            bool askedUser = false;
            bool spawnedTask = false;
            string? turnQuestion = null;
            string? turnTool = null;
            string? turnTarget = null;
            string? turnText = null;
            string? turnTextFull = null;

            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.String)
                {
                    var text = part.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        turnTextFull = AppendText(turnTextFull, text);
                        turnText = FirstLine(text);
                        cwd ??= ExtractCwd(text);
                    }
                    continue;
                }
                if (part.ValueKind != JsonValueKind.Object) continue;

                var type = GetString(part, "type");
                if (type == "text")
                {
                    var text = GetString(part, "text");
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        turnTextFull = AppendText(turnTextFull, text);
                        turnText = FirstLine(text);
                        cwd ??= ExtractCwd(text);
                    }
                }
                else if (type is "tool_use" or "tool_call")
                {
                    hasToolUse = true;
                    turnTool = GetString(part, "name") ?? GetNestedName(part);
                    if (IsTaskTool(turnTool)) spawnedTask = true;

                    JsonElement input = default;
                    bool hasInput = part.TryGetProperty("input", out input)
                                 || part.TryGetProperty("arguments", out input);
                    if (hasInput && input.ValueKind == JsonValueKind.String)
                    {
                        var raw = input.GetString();
                        if (!string.IsNullOrWhiteSpace(raw))
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(raw);
                                input = doc.RootElement.Clone();
                            }
                            catch (JsonException) { hasInput = false; }
                        }
                        else hasInput = false;
                    }
                    if (hasInput && input.ValueKind == JsonValueKind.Object)
                    {
                        cwd ??= ExtractCwdFromInput(input);
                        turnTarget = ToolTarget(turnTool, input);
                        if (IsAskTool(turnTool, hasInput ? input : default)
                            || IsPermissionGate(turnTool, input))
                        {
                            askedUser = true;
                            turnQuestion = IsAskTool(turnTool, hasInput ? input : default)
                                ? (ExtractQuestion(input) ?? ExtractMcpAsk(input) ?? turnTarget)
                                : (PermissionPrompt(turnTool, input) ?? turnTarget);
                        }
                    }
                    else if (IsAskTool(turnTool))
                    {
                        askedUser = true;
                        turnQuestion = "Waiting for your answer";
                    }
                }
            }

            if (role == "assistant")
            {
                turnEnded = false;
                lastHasToolUse = hasToolUse;
                lastAskedUser = askedUser;
                if (spawnedTask) lastSpawnedTask = true; // sticky until next user message
                if (turnText is not null) lastMessage = turnText;
                if (turnTool is not null) lastTool = turnTool;
                if (turnTarget is not null) lastTarget = turnTarget;
                if (turnQuestion is not null) lastQuestion = turnQuestion;

            }
            else if (role == "user")
            {
                turnEnded = false;
                lastAskedUser = false;
                lastHasToolUse = false;
                lastSpawnedTask = false;
                lastQuestion = null;
                lastTool = null;
                lastTarget = null;
                if (turnText is not null) lastMessage = turnText;
            }
        }

        if (lastRole is null) return null;

        SessionStatus status;
        // Orange only for an ask the agent is blocked on: AskQuestion / cursor_dialog /
        // a permission request. A question in the closing text ends the work, not a step.
        if (lastAskedUser)
        {
            status = SessionStatus.NeedsYou;
            lastQuestion ??= QuestionLine(lastMessage) ?? lastMessage;
        }
        else if (!turnEnded && (lastRole == "user" || lastHasToolUse))
            status = SessionStatus.Working;
        else if (lastSpawnedTask && DateTime.UtcNow - mtimeUtc < WorkingKeep)
            // Parent turn_ended after Task; children may still be tooling.
            status = SessionStatus.Working;
        else if (!turnEnded && lastRole == "assistant"
                 && DateTime.UtcNow - mtimeUtc < WorkingKeep)
            // Streaming / thinking often appends no tools for a long stretch; keep
            // Working so the pill does not drop to a finished/idle look mid-turn.
            status = SessionStatus.Working;
        else
            status = SessionStatus.Finished;

        var sessionId = "cursor:" + (sessionKey ?? "session");
        // Prefer the decoded project root for identity/jump — tool paths like
        // ...\Assets\foo.ico used to rename the row to "assets".
        var decodedRoot = DecodeProjectSlug(projectSlug);
        if (!string.IsNullOrWhiteSpace(decodedRoot))
            cwd = decodedRoot;
        else
            cwd ??= "";
        var projectName = !string.IsNullOrWhiteSpace(decodedRoot)
            ? TranscriptTail.ProjectNameOf(decodedRoot)
            : string.IsNullOrWhiteSpace(cwd)
                ? SlugProjectName(projectSlug)
                : TranscriptTail.ProjectNameOf(cwd);

        string summary;
        if (status == SessionStatus.NeedsYou)
            summary = Squash(lastQuestion ?? lastMessage ?? "Waiting for you", 120);
        else if (status == SessionStatus.Working)
            summary = lastTarget ?? (string.IsNullOrEmpty(lastTool) ? "Working…" : ToolPhrase(lastTool));
        else
            summary = Squash(lastMessage ?? "Done", 100);

        return new AgentSession
        {
            SessionId = sessionId,
            Agent = "cursor",
            Host = "cursor",
            HostLabel = "Cursor",
            Model = CursorUsageClient.LatestModel ?? "Composer",
            Usage = CursorUsageClient.LatestUsage,
            Cwd = cwd,
            ProjectName = projectName,
            Entrypoint = "",
            Pid = 0,
            Status = status,
            CurrentTool = status == SessionStatus.NeedsYou
                ? (lastTool ?? "AskQuestion")
                : status == SessionStatus.Working ? lastTool : null,
            CurrentTarget = status == SessionStatus.NeedsYou
                ? summary
                : status == SessionStatus.Working ? lastTarget : null,
            LastMessage = summary,
            TokensSpent = 0,
            StartedUtc = mtimeUtc,
            LastActivityUtc = mtimeUtc,
        };
    }

    internal static AgentSession? Parse(string path, string projectSlug, DateTime mtimeUtc, int tailBytes)
    {
        var events = ReadEvents(path, tailBytes);
        var key = Path.GetFileNameWithoutExtension(path);
        var session = ParseEvents(events, projectSlug, mtimeUtc, key);
        if (session is null) return null;
        try { session.StartedUtc = File.GetCreationTimeUtc(path); }
        catch { /* keep mtime */ }
        return session;
    }

    internal static string? DecodeProjectSlug(string slug)
    {
        try
        {
            if (!slug.StartsWith("c-", StringComparison.OrdinalIgnoreCase)) return null;
            var encoded = slug[2..];
            if (!encoded.StartsWith("Users-", StringComparison.OrdinalIgnoreCase)) return null;

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var profileName = Path.GetFileName(userProfile);
            var suffix = encoded["Users-".Length..];

            var slugUser = suffix.Split('-')[0];
            if (!profileName.StartsWith(slugUser, StringComparison.OrdinalIgnoreCase))
                return null;

            var afterUser = suffix[(slugUser.Length + 1)..];
            if (!afterUser.StartsWith("Desktop", StringComparison.OrdinalIgnoreCase))
                return null;

            var afterDesktop = afterUser.StartsWith("Desktop-", StringComparison.OrdinalIgnoreCase)
                ? afterUser["Desktop-".Length..]
                : afterUser.Length > "Desktop".Length && afterUser["Desktop".Length] == '-'
                    ? afterUser[("Desktop".Length + 1)..]
                    : "";

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (string.IsNullOrEmpty(afterDesktop)) return desktop;

            return DecodePathFromSuffix(desktop, afterDesktop) ?? desktop;
        }
        catch { return null; }
    }

    private static string? DecodePathFromSuffix(string root, string suffix)
    {
        var path = root;
        var remaining = suffix;
        while (!string.IsNullOrEmpty(remaining))
        {
            int bestLen = 0;
            for (int i = 1; i <= remaining.Length; i++)
            {
                if (i < remaining.Length && remaining[i] != '-') continue;
                var segment = remaining[..i];
                var candidate = Path.Combine(path, segment);
                if (Directory.Exists(candidate)) bestLen = i;
            }
            if (bestLen == 0) break;
            path = Path.Combine(path, remaining[..bestLen]);
            remaining = bestLen < remaining.Length ? remaining[(bestLen + 1)..] : "";
        }
        return path;
    }

    private static string SlugProjectName(string slug)
    {
        var decoded = DecodeProjectSlug(slug);
        if (!string.IsNullOrWhiteSpace(decoded))
            return TranscriptTail.ProjectNameOf(decoded);
        var tail = slug.Contains('-') ? slug[(slug.LastIndexOf('-') + 1)..] : slug;
        return string.IsNullOrWhiteSpace(tail) ? "Cursor" : tail;
    }

    private static string? ExtractCwd(string text)
    {
        foreach (Match m in WindowsPath.Matches(text))
        {
            var p = m.Value.TrimEnd('\\', '/', '.', ',', ';');
            if (IsCursorInternalPath(p)) continue;
            if (Directory.Exists(p)) return p;
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir) && !IsCursorInternalPath(dir) && Directory.Exists(dir))
                return dir;
        }
        return null;
    }

    /// <summary>
    /// Image/transcript paths under ~/.cursor/projects must not become the session cwd
    /// (they made ProjectName show as "assets").
    /// </summary>
    private static bool IsCursorInternalPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var marker = Path.DirectorySeparatorChar + ".cursor" + Path.DirectorySeparatorChar;
            return full.Contains(marker, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string? ExtractCwdFromInput(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in new[] { "working_directory", "path", "target_directory", "target_notebook" })
        {
            if (!input.TryGetProperty(key, out var val)) continue;
            var s = val.ValueKind == JsonValueKind.String ? val.GetString() : null;
            if (string.IsNullOrWhiteSpace(s)) continue;
            if (IsCursorInternalPath(s)) continue;
            if (Directory.Exists(s)) return s;
            var dir = Path.GetDirectoryName(s);
            if (!string.IsNullOrEmpty(dir) && !IsCursorInternalPath(dir) && Directory.Exists(dir))
                return dir;
        }
        return null;
    }

    private static bool IsAskTool(string? tool, JsonElement input = default)
    {
        if (tool is null) return false;
        if (tool.Equals("AskQuestion", StringComparison.OrdinalIgnoreCase)
            || tool.Equals("ask_question", StringComparison.OrdinalIgnoreCase)
            || tool.Equals("cursor_dialog", StringComparison.OrdinalIgnoreCase)
            || tool.Contains("AskQuestion", StringComparison.OrdinalIgnoreCase)
            || tool.Contains("AskUserQuestion", StringComparison.OrdinalIgnoreCase))
            return true;

        // Cursor often routes UI prompts through MCP (cursor_dialog / AskQuestion).
        if (tool.Equals("CallMcpTool", StringComparison.OrdinalIgnoreCase)
            && input.ValueKind == JsonValueKind.Object)
        {
            var mcpTool = GetStringProp(input, "toolName") ?? GetStringProp(input, "tool_name");
            if (mcpTool is null) return false;
            return mcpTool.Equals("AskQuestion", StringComparison.OrdinalIgnoreCase)
                || mcpTool.Equals("ask_question", StringComparison.OrdinalIgnoreCase)
                || mcpTool.Equals("cursor_dialog", StringComparison.OrdinalIgnoreCase)
                || mcpTool.Contains("AskQuestion", StringComparison.OrdinalIgnoreCase)
                || mcpTool.Contains("AskUserQuestion", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static string? ExtractMcpAsk(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;
        // CallMcpTool wraps the real prompt under arguments / request.
        foreach (var key in new[] { "arguments", "request", "params" })
        {
            if (!input.TryGetProperty(key, out var nested)) continue;
            if (nested.ValueKind == JsonValueKind.String)
            {
                var raw = nested.GetString();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    var q = ExtractQuestion(doc.RootElement);
                    if (q is not null) return q;
                }
                catch (JsonException) { /* plain string */ }
                var line = FirstLine(raw);
                if (line.Length > 0) return Squash(line, 120);
            }
            else if (nested.ValueKind == JsonValueKind.Object)
            {
                var q = ExtractQuestion(nested);
                if (q is not null) return q;
            }
        }
        return ExtractQuestion(input);
    }

    private static bool IsTaskTool(string? tool) =>
        tool is not null && tool.Equals("Task", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Cursor Allow/Stop cards: Shell/MCP calls that request elevated permissions
    /// or smart-mode approval. Also treat a bare Shell as a gate when Cursor
    /// tagged it with any required_permissions array (including "all").
    /// </summary>
    internal static bool IsPermissionGate(string? tool, JsonElement input)
    {
        if (tool is null || input.ValueKind != JsonValueKind.Object) return false;
        if (tool is not ("Shell" or "AwaitShell" or "CallMcpTool" or "FetchMcpResource"
            or "WebFetch" or "GenerateImage"))
            return false;

        if (input.TryGetProperty("required_permissions", out var perms)
            && perms.ValueKind == JsonValueKind.Array && perms.GetArrayLength() > 0)
            return true;
        if (input.TryGetProperty("request_smart_mode_approval", out var a)
            && a.ValueKind is JsonValueKind.True)
            return true;
        if (input.TryGetProperty("requestSmartModeApproval", out var b)
            && b.ValueKind is JsonValueKind.True)
            return true;
        // Nested MCP approval flags (CallMcpTool arguments).
        foreach (var key in new[] { "arguments", "request", "params" })
        {
            if (!input.TryGetProperty(key, out var nested)) continue;
            JsonElement obj = nested;
            if (nested.ValueKind == JsonValueKind.String)
            {
                var raw = nested.GetString();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    obj = doc.RootElement.Clone();
                }
                catch (JsonException) { continue; }
            }
            if (obj.ValueKind != JsonValueKind.Object) continue;
            if (obj.TryGetProperty("required_permissions", out var np)
                && np.ValueKind == JsonValueKind.Array && np.GetArrayLength() > 0)
                return true;
            if (obj.TryGetProperty("request_smart_mode_approval", out var na)
                && na.ValueKind is JsonValueKind.True)
                return true;
            if (obj.TryGetProperty("requestSmartModeApproval", out var nb)
                && nb.ValueKind is JsonValueKind.True)
                return true;
        }
        // Task / shell approval UI often lands as Shell with no permissions key
        // while still blocked — treat notify_on_output shells as gates too.
        if (tool is "Shell" or "AwaitShell"
            && input.TryGetProperty("notify_on_output", out _))
            return true;
        return false;
    }

    private static string? PermissionPrompt(string? tool, JsonElement input)
    {
        var desc = GetStringProp(input, "description");
        if (!string.IsNullOrWhiteSpace(desc))
            return "Allow: " + Squash(desc!, 100);
        var cmd = GetStringProp(input, "command");
        if (!string.IsNullOrWhiteSpace(cmd))
            return "Allow: " + ShortCommand(cmd!);
        return "Allow " + (tool ?? "tool") + " permission";
    }

    private static string? ExtractQuestion(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in new[] { "title", "question", "prompt", "message", "header" })
        {
            if (input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = FirstLine(v.GetString() ?? "");
                if (s.Length > 0) return s;
            }
        }
        if (input.TryGetProperty("questions", out var qs) && qs.ValueKind == JsonValueKind.Array)
        {
            foreach (var q in qs.EnumerateArray())
            {
                foreach (var key in new[] { "question", "prompt", "header", "title" })
                {
                    if (q.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                    {
                        var s = FirstLine(v.GetString() ?? "");
                        if (s.Length > 0) return s;
                    }
                }
            }
        }
        return null;
    }

    private static string? GetNestedName(JsonElement part)
    {
        if (part.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
            return GetString(fn, "name");
        return null;
    }

    private static string? ToolTarget(string? tool, JsonElement input)
    {
        if (string.IsNullOrEmpty(tool)) return null;
        if (IsAskTool(tool, input))
            return ExtractQuestion(input) ?? ExtractMcpAsk(input) ?? "Asking you a question";
        if (IsPermissionGate(tool, input))
            return PermissionPrompt(tool, input);

        var filePath = GetStringProp(input, "path")
                    ?? GetStringProp(input, "target_notebook")
                    ?? GetStringProp(input, "target_directory");
        if (filePath is not null && tool is "Read" or "Write" or "StrReplace" or "Delete" or "EditNotebook")
        {
            var file = Path.GetFileName(filePath.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(file)) file = filePath;
            return tool switch
            {
                "Read" => "Reading " + file,
                "Write" => "Writing " + file,
                "StrReplace" or "EditNotebook" => "Editing " + file,
                "Delete" => "Deleting " + file,
                _ => ToolPhrase(tool),
            };
        }
        if (input.TryGetProperty("command", out var cmd) && cmd.ValueKind == JsonValueKind.String)
        {
            var command = cmd.GetString();
            if (!string.IsNullOrWhiteSpace(command))
                return "Running " + ShortCommand(command);
        }
        if (input.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String)
        {
            var d = desc.GetString();
            if (!string.IsNullOrWhiteSpace(d)) return d;
        }
        if (input.TryGetProperty("pattern", out var pat) && pat.ValueKind == JsonValueKind.String)
        {
            var p = pat.GetString();
            if (!string.IsNullOrWhiteSpace(p)) return "Searching " + Squash(p, 40);
        }
        return ToolPhrase(tool);
    }

    private static string? GetStringProp(JsonElement input, string name)
        => input.ValueKind == JsonValueKind.Object
           && input.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string ToolPhrase(string tool) => tool switch
    {
        "Shell" => "Running a command",
        "Grep" => "Searching the code",
        "Glob" => "Finding files",
        "Read" => "Reading a file",
        "Write" or "StrReplace" or "EditNotebook" => "Editing a file",
        "Task" => "Running a subagent",
        "WebSearch" or "WebFetch" => "Searching the web",
        "AskQuestion" or "ask_question" or "cursor_dialog" => "Asking you a question",
        "TodoWrite" => "Updating the plan",
        _ => "Running " + tool,
    };

    private static string ShortCommand(string command)
    {
        var line = FirstLine(command);
        return line.Length <= 48 ? line : line[..45] + "…";
    }

    private static string AppendText(string? prev, string next)
        => string.IsNullOrEmpty(prev) ? next : prev + "\n" + next;

    private static string FirstLine(string text)
    {
        var idx = text.IndexOfAny(new[] { '\r', '\n' });
        var line = idx >= 0 ? text[..idx] : text;
        return line.Trim();
    }

    private static string? QuestionLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var cleaned = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = cleaned.Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("```", StringComparison.Ordinal)) continue;
            if (line.Equals("[REDACTED]", StringComparison.OrdinalIgnoreCase)) continue;
            if (AskQuestion.IsMatch(line)) return Squash(line, 120);
            if (lines.Length - i >= 6) break;
        }
        return null;
    }

    private static string Squash(string text, int max)
    {
        text = text.Trim();
        return text.Length <= max ? text : text[..Math.Max(0, max - 1)] + "…";
    }

    private static string? GetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Keep(string? next, string? prev) =>
        string.IsNullOrWhiteSpace(next) ? prev : next;
}
