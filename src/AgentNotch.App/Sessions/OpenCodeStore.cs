using System.Text.Json;
using AgentNotch.App.Diagnostics;
using AgentNotch.App.Usage;
using Microsoft.Data.Sqlite;

namespace AgentNotch.App.Sessions;

/// <summary>
/// OpenCode collector — reads the CLI's own SQLite store
/// (<c>~/.local/share/opencode/opencode.db</c>) read-only and derives one
/// <see cref="AgentSession"/> per live session.
///
/// OpenCode exposes no hook/approval callback and no JSONL transcript, so, like
/// Codex, it is a <b>read-only</b> source: the notch shows working / needs-you /
/// finished and your own terminal answers the prompt. The truth lives in two
/// tables:
///  * <c>session_v2</c> — one row per session: directory, title, model, token
///    counters and the <c>time_*</c> clock (milliseconds since the epoch).
///  * <c>session_message</c> — the streamed turn. Its latest row's <c>type</c> is
///    the live status: <c>idle</c> means the turn was handed back (finished),
///    anything else (<c>user</c>/<c>assistant</c>/<c>synthetic</c>/…) means the
///    agent is still working. An <c>assistant</c> row carries the model's
///    content parts, whose last <c>tool</c> part gives the current tool and
///    target.
///
/// Everything is best-effort and never throws: a locked/absent database simply
/// yields no rows.
/// </summary>
public static class OpenCodeStore
{
    public static readonly TimeSpan IdleKeep = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan WorkingKeep = TimeSpan.FromMinutes(45);

    /// <summary>%USERPROFILE%\.local\share\opencode\opencode.db — OpenCode's CLI store.</summary>
    public static string DefaultDbPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "opencode", "opencode.db");

    public static List<AgentSession> Collect(string dbPath, DateTime nowUtc)
    {
        var sessions = new List<AgentSession>();
        try
        {
            if (!File.Exists(dbPath)) return sessions;
            using var conn = Open(dbPath);
            if (conn is null) return sessions;

            var pending = ReadPending(conn);
            long cutoff = new DateTimeOffset(nowUtc - WorkingKeep).ToUnixTimeMilliseconds();

            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "select id, directory, title, model, " +
                "       coalesce(tokens_input,0), coalesce(tokens_output,0), " +
                "       coalesce(tokens_cache_read,0), coalesce(tokens_cache_write,0), " +
                "       time_created, time_updated, time_idle " +
                "from session_v2 " +
                "where coalesce(time_archived,0) = 0 " +
                "  and coalesce(time_updated, time_created, 0) >= $cutoff";
            cmd.Parameters.AddWithValue("$cutoff", cutoff);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                try
                {
                    var session = Build(conn, reader, pending, nowUtc);
                    if (session is null) continue;
                    var keep = session.Status is SessionStatus.Working or SessionStatus.NeedsYou
                        ? WorkingKeep
                        : IdleKeep;
                    if (nowUtc - session.LastActivityUtc > keep) continue;
                    sessions.Add(session);
                }
                catch (Exception ex)
                {
                    AppLog.Write("opencode: build row failed: " + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("opencode: collect failed: " + ex.Message);
        }

        // A task and its reviewer/sub-agent sessions share a working directory;
        // collapse them to one row (most urgent, then most recent) so a single
        // session never sprouts phantom duplicates — same rule Codex uses.
        return sessions
            .GroupBy(s => string.IsNullOrEmpty(s.Cwd) ? s.SessionId : s.Cwd,
                     StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderBy(s => Rank(s.Status))
                .ThenByDescending(s => s.LastActivityUtc)
                .First())
            .OrderBy(s => Rank(s.Status))
            .ThenByDescending(s => s.LastActivityUtc)
            .ToList();
    }

    private static int Rank(SessionStatus s) => s switch
    {
        SessionStatus.NeedsYou => 0,
        SessionStatus.Working => 1,
        _ => 2,
    };

    private static SqliteConnection? Open(string dbPath)
    {
        try
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,      // don't pin the WAL between 2s polls
                DefaultTimeout = 2,   // seconds; give a busy writer a moment, then give up
            }.ToString();
            var conn = new SqliteConnection(cs);
            conn.Open();
            return conn;
        }
        catch (Exception ex)
        {
            AppLog.Write("opencode: open failed: " + ex.Message);
            return null;
        }
    }

    // ---------- pending asks (permission / question) ----------

    private readonly record struct Pending(string Type, string? Data);

    private static Dictionary<string, Pending> ReadPending(SqliteConnection conn)
    {
        var map = new Dictionary<string, Pending>(StringComparer.Ordinal);
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "select session_id, type, data from session_pending";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var sid = reader.IsDBNull(0) ? "" : reader.GetString(0);
                if (string.IsNullOrEmpty(sid)) continue;
                map[sid] = new Pending(
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2));
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("opencode: pending read failed: " + ex.Message);
        }
        return map;
    }

    // ---------- one session ----------

    private static AgentSession? Build(
        SqliteConnection conn, SqliteDataReader reader,
        Dictionary<string, Pending> pending, DateTime nowUtc)
    {
        var id = reader.GetString(0);
        var directory = reader.IsDBNull(1) ? "" : reader.GetString(1);
        var title = reader.IsDBNull(2) ? null : reader.GetString(2);
        var modelJson = reader.IsDBNull(3) ? null : reader.GetString(3);
        long tokens = ReadLong(reader, 4) + ReadLong(reader, 5) + ReadLong(reader, 7); // input + output + cache-write (cache-read excluded, matching Claude's spend)
        var created = FromMs(ReadLong(reader, 8));
        var updated = FromMs(ReadLong(reader, 9));
        var idleAt = FromMs(ReadLong(reader, 10));

        var lastType = LastMessageType(conn, id);
        var assistant = LastAssistantData(conn, id);

        var info = ParseAssistant(assistant);

        pending.TryGetValue(id, out var pend);
        bool hasPending = !string.IsNullOrEmpty(pend.Type);

        SessionStatus status;
        bool needsApproval = false;
        string? question = null;

        if (hasPending || info.AskTool is { Length: > 0 })
        {
            status = SessionStatus.NeedsYou;
            var kind = (pend.Type + " " + (info.AskTool ?? "")).ToLowerInvariant();
            needsApproval = kind.Contains("permission") || kind.Contains("approval")
                        || kind.Contains("confirm");
            question = ExtractPendingText(pend.Data) ?? info.Question
                    ?? info.LastText ?? "Waiting for your answer";
        }
        else if (lastType is null)
        {
            // No messages yet: fall back to the session clock. Updated after the
            // last idle write means a turn is in flight.
            status = updated > idleAt ? SessionStatus.Working : SessionStatus.Finished;
        }
        else
        {
            status = lastType == "idle" ? SessionStatus.Finished : SessionStatus.Working;
        }

        string? currentTool = null, currentTarget = null;
        bool thinking = false;
        if (status == SessionStatus.Working)
        {
            if (info.RunningTool is { Length: > 0 } tool)
            {
                currentTool = tool;
                currentTarget = TargetPhrase(tool, info.RunningInput);
            }
            else
            {
                thinking = true; // between tool calls: reasoning/streaming
            }
        }
        else if (status == SessionStatus.NeedsYou)
        {
            currentTool = info.AskTool ?? "question";
            currentTarget = question;
        }

        var cwd = directory ?? "";
        var project = ProjectNameOf(cwd);

        string summary;
        if (status == SessionStatus.NeedsYou)
            summary = Squash(question ?? "Waiting for you", 120);
        else if (status == SessionStatus.Working)
            summary = currentTarget ?? (currentTool is null ? "Working…" : currentTool);
        else
            summary = Squash(info.LastText ?? title ?? "Done", 100);

        var lastActivity = updated > idleAt ? updated : idleAt;
        if (lastActivity == default) lastActivity = created;

        var model = ParseModel(modelJson);
        var usage = GetUsageForModel(model, tokens);

        return new AgentSession
        {
            SessionId = "opencode:" + id,
            Agent = "opencode",
            Host = null,
            HostLabel = null,       // brand chip alone says "OpenCode"; no window to jump to
            Model = model,
            Usage = usage,
            Cwd = cwd,
            ProjectName = project,
            Entrypoint = "",
            Pid = 0,
            Status = status,
            IsThinking = thinking,
            NeedsApproval = needsApproval,
            CurrentTool = currentTool,
            CurrentTarget = currentTarget,
            LastMessage = summary,
            TokensSpent = tokens,
            StartedUtc = created == default ? lastActivity : created,
            LastActivityUtc = lastActivity,
        };
    }

    public static IReadOnlyList<UsageWindow> GetUsageForModel(string? model, long tokensSpent)
    {
        var list = new List<UsageWindow>();
        var m = (model ?? "").ToLowerInvariant();

        if (m.Contains("claude"))
        {
            var cw = ClaudeUsageClient.SharedWindows;
            if (cw.Count > 0) list.AddRange(cw);
        }
        else if (m.Contains("gpt") || m.Contains("codex") || m.Contains("o1") || m.Contains("o3"))
        {
            var codexDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
            var cu = CodexTail.GetLatestUsage(codexDir, DateTime.UtcNow);
            if (cu.Count > 0) list.AddRange(cu);
        }
        else if (m.Contains("gemini"))
        {
            var gu = AntigravityClient.LatestGeminiUsage;
            if (gu.Count > 0) list.AddRange(gu);
        }

        // Context limit (for custom or open models)
        long limit = GetContextLimit(model);
        if (limit > 0 && tokensSpent > 0)
        {
            double pct = Math.Clamp(Math.Round((double)tokensSpent / limit * 100), 0, 100);
            list.Add(new UsageWindow("ctx", pct, null));
        }

        return list;
    }

    private static readonly object ConfigGate = new();
    private static Dictionary<string, long>? _cachedLimits;
    private static DateTime _configReadUtc = DateTime.MinValue;

    internal static long GetContextLimit(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return 0;
        lock (ConfigGate)
        {
            if (_cachedLimits is null || DateTime.UtcNow - _configReadUtc > TimeSpan.FromSeconds(30))
            {
                _cachedLimits = LoadContextLimits();
                _configReadUtc = DateTime.UtcNow;
            }
            var clean = model.Contains('/') ? model[(model.LastIndexOf('/') + 1)..] : model;
            if (_cachedLimits.TryGetValue(clean, out var limit)) return limit;
            if (_cachedLimits.TryGetValue(model, out var limitFull)) return limitFull;
        }
        return 0;
    }

    private static Dictionary<string, long> LoadContextLimits()
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var paths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "opencode", "opencode.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "opencode", "opencode.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "opencode", "opencode.json"),
        };

        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                if (root.TryGetProperty("provider", out var providers) && providers.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prov in providers.EnumerateObject())
                    {
                        if (prov.Value.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var m in models.EnumerateObject())
                            {
                                if (m.Value.TryGetProperty("limit", out var lim) &&
                                    lim.TryGetProperty("context", out var ctx) &&
                                    ctx.TryGetInt64(out var ctxVal))
                                {
                                    map[m.Name] = ctxVal;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }
        return map;
    }

    private static long ReadLong(SqliteDataReader reader, int i)
        => reader.IsDBNull(i) ? 0 : reader.GetInt64(i);

    private static DateTime FromMs(long ms)
    {
        try
        {
            if (ms <= 0) return default;
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
        }
        catch { return default; }
    }

    private static string? ParseModel(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String)
            {
                var s = id.GetString();
                if (string.IsNullOrWhiteSpace(s)) return null;
                return s.Contains('/') ? s[(s.LastIndexOf('/') + 1)..] : s;
            }
        }
        catch (JsonException) { /* a bare string, or unknown shape */ }
        return null;
    }

    private static string? LastMessageType(SqliteConnection conn, string sessionId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select type from session_message where session_id = $s order by seq desc limit 1";
        cmd.Parameters.AddWithValue("$s", sessionId);
        return cmd.ExecuteScalar() as string;
    }

    private static string? LastAssistantData(SqliteConnection conn, string sessionId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select data from session_message where session_id = $s and type = 'assistant' order by seq desc limit 1";
        cmd.Parameters.AddWithValue("$s", sessionId);
        return cmd.ExecuteScalar() as string;
    }

    // ---------- assistant content ----------

    private sealed class AssistantInfo
    {
        public string? RunningTool;
        public JsonElement RunningInput;
        public string? AskTool;
        public string? Question;
        public string? LastText;
    }

    private static AssistantInfo ParseAssistant(string? data)
    {
        var info = new AssistantInfo();
        if (string.IsNullOrWhiteSpace(data)) return info;
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return info;
            if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                return info;

            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object) continue;
                var type = GetString(part, "type");
                if (type == "text")
                {
                    var text = GetString(part, "text");
                    var line = FirstLine(text ?? "");
                    if (line.Length > 0) info.LastText = line;
                }
                else if (type == "tool")
                {
                    var name = GetString(part, "name");
                    var state = part.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.Object
                        ? st : default;
                    var status = state.ValueKind == JsonValueKind.Object ? GetString(state, "status") : null;
                    bool running = status is "running" or "pending";

                    if (running)
                    {
                        info.RunningTool = name;
                        // Clone: the JsonDocument is disposed when this method returns,
                        // but the input is read again later when phrasing the target.
                        info.RunningInput = state.ValueKind == JsonValueKind.Object
                            && state.TryGetProperty("input", out var input)
                            ? input.Clone() : default;
                        if (IsAskTool(name))
                        {
                            info.AskTool = name;
                            info.Question = ExtractQuestion(info.RunningInput) ?? info.Question;
                        }
                    }
                }
            }
        }
        catch (JsonException) { /* truncated stream — keep what we have */ }
        return info;
    }

    private static bool IsAskTool(string? tool)
    {
        if (string.IsNullOrEmpty(tool)) return false;
        var t = tool.ToLowerInvariant();
        return t.Contains("ask") || t.Contains("question")
            || t.Contains("permission") || t.Contains("confirm");
    }

    private static string? ExtractQuestion(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in new[] { "question", "prompt", "title", "header", "message", "text" })
        {
            var s = GetString(input, key);
            var line = FirstLine(s ?? "");
            if (line.Length > 0) return Squash(line, 120);
        }
        if (input.TryGetProperty("questions", out var qs) && qs.ValueKind == JsonValueKind.Array)
        {
            foreach (var q in qs.EnumerateArray())
            {
                var s = GetString(q, "question") ?? GetString(q, "prompt") ?? GetString(q, "header");
                var line = FirstLine(s ?? "");
                if (line.Length > 0) return Squash(line, 120);
            }
        }
        return null;
    }

    private static string? ExtractPendingText(string? data)
    {
        if (string.IsNullOrWhiteSpace(data)) return null;
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var q = ExtractQuestion(doc.RootElement);
            if (q is not null) return q;
            var tool = GetString(doc.RootElement, "tool") ?? GetString(doc.RootElement, "name");
            var detail = GetString(doc.RootElement, "description") ?? GetString(doc.RootElement, "command");
            if (!string.IsNullOrWhiteSpace(detail)) return Squash(FirstLine(detail!), 120);
            if (!string.IsNullOrWhiteSpace(tool)) return "Allow " + tool;
        }
        catch (JsonException) { /* not JSON — fall back to the raw text */ }
        return Squash(FirstLine(data), 120);
    }

    // ---------- phrasing ----------

    private static string? TargetPhrase(string? tool, JsonElement input)
    {
        if (string.IsNullOrEmpty(tool)) return null;
        var path = GetString(input, "path") ?? GetString(input, "filePath") ?? GetString(input, "file");
        var pattern = GetString(input, "pattern");
        var command = GetString(input, "command");
        var file = string.IsNullOrEmpty(path) ? null : Path.GetFileName(path!.TrimEnd('\\', '/'));

        return tool.ToLowerInvariant() switch
        {
            "read" or "read_file" or "view" => "Reading " + (file ?? "a file"),
            "write" or "write_file" or "create" => "Writing " + (file ?? "a file"),
            "edit" or "patch" or "str_replace" or "multiedit" => "Editing " + (file ?? "a file"),
            "glob" or "list" or "find" => "Finding " + Squash(FirstLine(pattern ?? "files"), 40),
            "grep" or "search" or "ripgrep" => "Searching " + Squash(FirstLine(pattern ?? "the code"), 40),
            "shell" or "bash" or "exec" or "run" => "Running " + ShortCommand(command),
            _ => ToolLabel(tool),
        };
    }

    private static string ToolLabel(string tool) => tool.ToLowerInvariant() switch
    {
        "task" => "Running a subagent",
        "webfetch" or "fetch" => "Fetching a page",
        "todowrite" or "todo" => "Updating the plan",
        _ => "Running " + tool,
    };

    private static string ShortCommand(string? command)
    {
        var line = FirstLine(command ?? "");
        if (line.Length == 0) return "a command";
        return line.Length <= 48 ? line : line[..45] + "…";
    }

    // ---------- small helpers ----------

    private static string GetString(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static string FirstLine(string text)
    {
        var idx = text.IndexOfAny(new[] { '\r', '\n' });
        return (idx >= 0 ? text[..idx] : text).Trim();
    }

    private static string Squash(string text, int max)
    {
        text = text.Trim();
        return text.Length <= max ? text : text[..Math.Max(0, max - 1)] + "…";
    }

    private static string ProjectNameOf(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return "session";
        var trimmed = cwd.TrimEnd('\\', '/');
        var cut = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
        var name = cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
        return string.IsNullOrWhiteSpace(name) ? "session" : name;
    }
}
