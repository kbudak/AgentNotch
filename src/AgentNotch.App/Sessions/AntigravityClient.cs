using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentNotch.App.Diagnostics;

namespace AgentNotch.App.Sessions;

/// <summary>
/// Collects Antigravity (Google) agent tasks — "cascade trajectories" — from the
/// local language_server RPC. A C# port of the agent-task part of the proven
/// Electron collector (collectors/antigravity.js), with discovery done fully
/// in-process instead of spawning PowerShell: the language_server process via
/// <see cref="Process"/>, the CSRF token from Antigravity's own startup logs, and
/// the listening loopback ports via GetExtendedTcpTable. Never throws; while
/// Antigravity isn't running <see cref="Latest"/> is simply empty.
/// </summary>
public sealed class AntigravityClient : IDisposable
{
    private const string RpcPath =
        "/exa.language_server_pb.LanguageServerService/GetAllCascadeTrajectories";
    private const string CsrfHeader = "x-codeium-csrf-token";

    /// <summary>How much of each Antigravity log we read from the end looking for the token.</summary>
    private const int LogTailBytes = 512 * 1024;

    /// <summary>At most this many trajectories are surfaced.</summary>
    private const int MaxTasks = 4;

    /// <summary>Floor between actual refresh attempts — callers may invoke <see cref="RefreshIfDue"/> freely.</summary>
    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromSeconds(10);

    /// <summary>How long a discovered (token, ports) endpoint is trusted before re-discovery.</summary>
    private static readonly TimeSpan ServerTtl = TimeSpan.FromSeconds(60);

    /// <summary>Non-working trajectories idle longer than this are leftovers — drop them.</summary>
    private static readonly TimeSpan TaskKeep = TimeSpan.FromMinutes(10);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private readonly object _gate = new();
    private volatile IReadOnlyList<AgentSession> _latest = Array.Empty<AgentSession>();
    private (string Token, int[] Ports)? _server;
    private DateTime _serverCheckedUtc = DateTime.MinValue;
    private DateTime _lastAttemptUtc = DateTime.MinValue;
    private bool _refreshing;
    private volatile bool _disposed;

    /// <summary>Result of the last successful fetch; empty while Antigravity isn't running.</summary>
    public IReadOnlyList<AgentSession> Latest => _latest;

    /// <summary>
    /// Requests a refresh. Non-blocking and safe to call as often as convenient:
    /// at most one refresh runs at a time, actual attempts are ≥10s apart, and the
    /// work happens on a fire-and-forget threadpool task. Never throws.
    /// </summary>
    public void RefreshIfDue()
    {
        try
        {
            if (_disposed) return;
            lock (_gate)
            {
                if (_refreshing) return;
                if (DateTime.UtcNow - _lastAttemptUtc < MinRefreshInterval) return;
                _refreshing = true;
                _lastAttemptUtc = DateTime.UtcNow;
            }
            _ = Task.Run(RefreshAsync);
        }
        catch (Exception ex)
        {
            AppLog.Write("antigravity: refresh scheduling failed: " + ex.Message);
            lock (_gate) { _refreshing = false; }
        }
    }

    public void Dispose() => _disposed = true;

    // ---------- refresh pipeline ----------

    private async Task RefreshAsync()
    {
        try
        {
            var server = GetServer();
            if (server is not { } srv)
            {
                // Antigravity closed (or unreachable enough that we can't even
                // discover it) — no live trajectories to show.
                _latest = Array.Empty<AgentSession>();
                return;
            }

            foreach (var port in srv.Ports)
            {
                if (_disposed) return;
                if (await FetchTrajectoriesAsync(port, srv.Token).ConfigureAwait(false) is { } response)
                {
                    _latest = MapTrajectories(response, DateTime.UtcNow);
                    return;
                }
            }

            // Cached endpoint stopped answering (app restarted?) — keep the last
            // good snapshot for now and force re-discovery on the next attempt.
            lock (_gate)
            {
                _server = null;
                _serverCheckedUtc = DateTime.MinValue;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("antigravity: refresh failed: " + ex.Message);
        }
        finally
        {
            lock (_gate) { _refreshing = false; }
        }
    }

    /// <summary>Cached (token, ports) endpoint when fresh; otherwise re-discovers.</summary>
    private (string Token, int[] Ports)? GetServer()
    {
        lock (_gate)
        {
            if (_server is { } cached && DateTime.UtcNow - _serverCheckedUtc < ServerTtl)
                return cached;
        }
        var discovered = DiscoverServer();
        lock (_gate)
        {
            _server = discovered;
            _serverCheckedUtc = DateTime.UtcNow;
        }
        return discovered;
    }

    private static async Task<JsonElement?> FetchTrajectoriesAsync(int port, string token)
    {
        try
        {
            var content = new StringContent("{}", Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}{RpcPath}")
            {
                Content = content,
            };
            request.Headers.TryAddWithoutValidation(CsrfHeader, token);

            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False)
                return null; // "no answer", like the JS falsy check
            return root.Clone();
        }
        catch
        {
            return null; // refused / timed out / non-JSON — the caller tries the next port
        }
    }

    // ---------- discovery (process + token + ports) ----------

    private static (string Token, int[] Ports)? DiscoverServer()
    {
        try
        {
            int pid = FindLanguageServerPid();
            if (pid == 0) return null;
            var token = FindCsrfToken();
            if (string.IsNullOrEmpty(token)) return null;
            var ports = ListeningLoopbackPorts(pid);
            if (ports.Length == 0) return null;
            return (token, ports);
        }
        catch (Exception ex)
        {
            AppLog.Write("antigravity: server discovery failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// The newest running language_server that belongs to Antigravity. Reading
    /// MainModule can fail for elevated processes — such a process is only an
    /// acceptable candidate when no confirmed Antigravity one exists.
    /// </summary>
    private static int FindLanguageServerPid()
    {
        // Antigravity is usually not running at all. The shared process snapshot
        // answers that for free; only then is the expensive per-process
        // MainModule/StartTime walk below worth doing.
        if (!Interop.ProcessTree.AnyRunning("language_server")) return 0;

        int best = 0, fallback = 0;
        var bestStart = DateTime.MinValue;
        var fallbackStart = DateTime.MinValue;

        Process[] processes;
        try { processes = Process.GetProcessesByName("language_server"); }
        catch { return 0; }

        foreach (var process in processes)
        {
            try
            {
                string? path = null;
                try { path = process.MainModule?.FileName; }
                catch { /* elevated or already-exited process — path unknown */ }

                var start = DateTime.MinValue;
                try { start = process.StartTime; } catch { }

                if (path is null)
                {
                    if (fallback == 0 || start > fallbackStart) { fallback = process.Id; fallbackStart = start; }
                }
                else if (path.Contains("antigravity", StringComparison.OrdinalIgnoreCase))
                {
                    if (best == 0 || start > bestStart) { best = process.Id; bestStart = start; }
                }
            }
            catch { }
            finally
            {
                try { process.Dispose(); } catch { }
            }
        }
        return best != 0 ? best : fallback;
    }

    private static readonly Regex CsrfTokenPattern = new(
        "csrf_token\\s+([0-9a-fA-F-]+)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Latest CSRF token from Antigravity's own startup logs: newest log first,
    /// last match within the tail of that log wins.
    /// </summary>
    private static string? FindCsrfToken()
    {
        var logs = new List<FileInfo>();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        try
        {
            var hubLog = new FileInfo(Path.Combine(appData, "Antigravity", "logs", "main.log"));
            if (hubLog.Exists) logs.Add(hubLog);
        }
        catch { }

        try
        {
            var ideLogs = Path.Combine(appData, "Antigravity IDE", "logs");
            if (Directory.Exists(ideLogs))
            {
                foreach (var file in Directory.EnumerateFiles(ideLogs, "*.log", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileName(file);
                    if (name.Equals("main.log", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("ls-main.log", StringComparison.OrdinalIgnoreCase))
                    {
                        try { logs.Add(new FileInfo(file)); } catch { }
                    }
                }
            }
        }
        catch { }

        foreach (var log in logs.OrderByDescending(SafeLastWriteUtc))
        {
            var tail = ReadTailText(log.FullName);
            if (tail is null) continue;
            var matches = CsrfTokenPattern.Matches(tail);
            if (matches.Count > 0) return matches[^1].Groups[1].Value;
        }
        return null;
    }

    private static DateTime SafeLastWriteUtc(FileInfo file)
    {
        try { return file.LastWriteTimeUtc; } catch { return DateTime.MinValue; }
    }

    /// <summary>Last ≤512KB of a (possibly still-open) log file; null when unreadable.</summary>
    private static string? ReadTailText(string path)
    {
        try
        {
            using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            long size = fs.Length;
            int len = (int)Math.Min(size, LogTailBytes);
            if (len <= 0) return null;
            fs.Position = size - len;
            var buf = new byte[len];
            fs.ReadExactly(buf, 0, len);
            return Encoding.UTF8.GetString(buf);
        }
        catch
        {
            return null;
        }
    }

    // ---------- listening ports (GetExtendedTcpTable) ----------

    private const int AfInet = 2;                    // AF_INET
    private const int TcpTableOwnerPidListener = 3;  // TCP_TABLE_OWNER_PID_LISTENER
    private const uint ErrorInsufficientBuffer = 122;

    /// <summary>127.0.0.1 in network byte order, read back as a little-endian uint.</summary>
    private const uint LoopbackNetworkOrder = 0x0100007F;

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable, ref int size, bool order, int addressFamily, int tableClass, uint reserved);

    /// <summary>IPv4 loopback ports the given pid is LISTENing on (the in-process netstat).</summary>
    private static int[] ListeningLoopbackPorts(int pid)
    {
        try
        {
            // The table can grow between the size query and the fetch — retry a few times.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                int size = 0;
                _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
                if (size <= 0) return Array.Empty<int>();

                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    uint result = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
                    if (result == ErrorInsufficientBuffer) continue;
                    if (result != 0) return Array.Empty<int>();

                    int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
                    int count = Marshal.ReadInt32(buffer);
                    int maxRows = (size - sizeof(int)) / rowSize;
                    if (count > maxRows) count = maxRows; // never read past the buffer

                    var ports = new List<int>();
                    var rowPtr = IntPtr.Add(buffer, sizeof(int));
                    for (int i = 0; i < count; i++, rowPtr = IntPtr.Add(rowPtr, rowSize))
                    {
                        var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr);
                        if (row.OwningPid != (uint)pid) continue;
                        if (row.LocalAddr != LoopbackNetworkOrder) continue; // 127.0.0.1 only, like the JS netstat match
                        int port = (int)(((row.LocalPort & 0xFF) << 8) | ((row.LocalPort >> 8) & 0xFF));
                        if (port > 0 && !ports.Contains(port)) ports.Add(port);
                    }
                    return ports.ToArray();
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            return Array.Empty<int>();
        }
        catch (Exception ex)
        {
            AppLog.Write("antigravity: port lookup failed: " + ex.Message);
            return Array.Empty<int>();
        }
    }

    // ---------- pure mapping (unit-tested) ----------

    // The live server returns {"trajectorySummaries": {"<guid>": {trajectory}, ...}}
    // — an OBJECT map, not an array — so the container search accepts both and the
    // trajectory list is the map's values. Other keys are kept for forward-compat.
    private static readonly string[] ContainerKeys =
        { "trajectorySummaries", "trajectories", "cascadeTrajectories", "summaries", "items" };
    private static readonly string[] StatusKeys = { "status", "state", "executionStatus", "executorStatus" };
    private static readonly string[] SummaryKeys = { "summary", "title", "description", "lastAgentMessage" };
    private static readonly string[] TimeKeys = { "lastModifiedTime", "updatedAt", "updateTime", "modifiedAt", "createdAt" };
    private static readonly string[] CwdKeys = { "cwd", "workspacePath", "projectPath", "repositoryPath" };
    // Real trajectories carry no top-level cwd — it's a file:// URI on the workspace.
    private static readonly string[] WorkspaceUriKeys = { "workspaceFolderAbsoluteUri", "gitRootAbsoluteUri" };
    private static readonly string[] CreatedKeys = { "createdTime", "createdAt", "creationTime" };
    private static readonly string[] IdKeys = { "trajectoryId", "trajectory_id", "id" };
    private static readonly string[] SecondsKeys = { "seconds", "_seconds" };

    private static readonly Regex NeedsYouStatus = new(
        "waiting.*(user|input|approval)|needs.*(user|input)|blocked|" +
        "pending.*(approval|user|input)|awaiting|user_action|user_input|require",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WorkingStatus = new(
        "running|active|executing|generating|in.?progress|streaming",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FinishedStatus = new(
        "complete|finished|done|idle|stopped|cancelled",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex QuestionTail = new(
        "[?？]\\s*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex QuestionPhrases = new(
        "\\b(approve|confirm|do you want|which option|onay|izin|ister misin|hangisini)\\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Whitespace = new(
        "\\s+", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Maps a GetAllCascadeTrajectories response to display sessions — the pure,
    /// unit-tested core. Tolerates any response shape: the trajectory list is the
    /// first array found under trajectories/cascadeTrajectories/summaries/items
    /// (recursively, depth ≤3); items with an unrecognized status are skipped;
    /// non-working items idle over 10 minutes are dropped; at most 4 returned.
    /// </summary>
    public static List<AgentSession> MapTrajectories(JsonElement response, DateTime nowUtc)
    {
        var tasks = new List<AgentSession>();
        try
        {
            if (FirstContainer(response, 0) is not { } container) return tasks;

            foreach (var t in EnumerateItems(container))
            {
                var rawStatus = (FirstString(t, StatusKeys) ?? "").ToLowerInvariant();
                var summary = CollapseExcerpt(FirstString(t, SummaryKeys), 100);

                SessionStatus? mapped = null;
                if (NeedsYouStatus.IsMatch(rawStatus)) mapped = SessionStatus.NeedsYou;
                else if (WorkingStatus.IsMatch(rawStatus)) mapped = SessionStatus.Working;
                else if (FinishedStatus.IsMatch(rawStatus))
                    mapped = AsksQuestion(summary) ? SessionStatus.NeedsYou : SessionStatus.Finished;
                if (mapped is not { } status) continue;

                var updatedAtUtc = CoerceTimestamp(FirstTruthy(t, TimeKeys), nowUtc);
                if (status != SessionStatus.Working && nowUtc - updatedAtUtc > TaskKeep) continue;

                var cwd = ExtractCwd(t);
                var id = FirstIdString(t) ??
                         (cwd ?? "agent") + ":" + ToEpochMs(updatedAtUtc).ToString(CultureInfo.InvariantCulture);

                tasks.Add(new AgentSession
                {
                    SessionId = "antigravity:" + id,
                    Agent = "antigravity",
                    Host = "antigravity",
                    Model = FirstString(t, "model") ?? "Gemini",
                    HostLabel = "Antigravity",
                    Cwd = cwd ?? "",
                    ProjectName = ProjectNameOf(cwd),
                    Entrypoint = "",
                    StartedUtc = CoerceTimestamp(FirstTruthy(t, CreatedKeys), updatedAtUtc),
                    Pid = 0,
                    Status = status,
                    LastMessage = summary ?? status switch
                    {
                        SessionStatus.Working => "Working…",
                        SessionStatus.Finished => "Task complete",
                        _ => "Waiting for you",
                    },
                    TokensSpent = 0,
                    LastActivityUtc = updatedAtUtc,
                });
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("antigravity: map failed: " + ex.Message);
        }
        return tasks.Count <= MaxTasks ? tasks : tasks.GetRange(0, MaxTasks);
    }

    /// <summary>
    /// The trajectory container in the response: an array or an object-map, found
    /// directly, under a known key, or nested at most 3 objects deep. The live
    /// server nests trajectories under the object-map key "trajectorySummaries".
    /// </summary>
    private static JsonElement? FirstContainer(JsonElement value, int depth)
    {
        if (depth > 3) return null;
        if (value.ValueKind == JsonValueKind.Array) return value;
        if (value.ValueKind != JsonValueKind.Object) return null;

        foreach (var key in ContainerKeys)
            if (value.TryGetProperty(key, out var named) &&
                named.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                return named;

        foreach (var property in value.EnumerateObject())
            if (FirstContainer(property.Value, depth + 1) is { } found)
                return found;

        return null;
    }

    /// <summary>Object trajectories in a container — array elements or object-map values.</summary>
    private static IEnumerable<JsonElement> EnumerateItems(JsonElement container)
    {
        if (container.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in container.EnumerateArray())
                if (element.ValueKind == JsonValueKind.Object)
                    yield return element;
        }
        else if (container.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in container.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.Object)
                    yield return property.Value;
        }
    }

    /// <summary>
    /// Working directory of a trajectory: a direct cwd-style key, else the first
    /// workspace's file:// URI (workspaces[].workspaceFolderAbsoluteUri, or
    /// trajectoryMetadata.workspaceUris[0]), decoded to a local path.
    /// </summary>
    private static string? ExtractCwd(JsonElement t)
    {
        if (FirstString(t, CwdKeys) is { } direct) return NormalizePathUri(direct);

        if (t.TryGetProperty("workspaces", out var workspaces) &&
            workspaces.ValueKind == JsonValueKind.Array)
        {
            foreach (var ws in workspaces.EnumerateArray())
                if (ws.ValueKind == JsonValueKind.Object && FirstString(ws, WorkspaceUriKeys) is { } uri)
                    return NormalizePathUri(uri);
        }

        if (t.TryGetProperty("trajectoryMetadata", out var meta) &&
            meta.ValueKind == JsonValueKind.Object &&
            meta.TryGetProperty("workspaceUris", out var uris) &&
            uris.ValueKind == JsonValueKind.Array)
        {
            foreach (var u in uris.EnumerateArray())
                if (u.ValueKind == JsonValueKind.String && u.GetString() is { Length: > 0 } s)
                    return NormalizePathUri(s);
        }

        return null;
    }

    /// <summary>"file:///c:/Users/..." → "c:\Users\..."; non-URI values pass through.</summary>
    private static string NormalizePathUri(string value)
    {
        if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            try { return new Uri(value).LocalPath.Replace('/', '\\'); }
            catch { /* malformed — fall through */ }
        }
        return value;
    }

    /// <summary>Does the summary end in (or contain) a question/approval ask? Includes Turkish.</summary>
    private static bool AsksQuestion(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var tail = text.Trim();
        if (tail.Length > 500) tail = tail[^500..];
        return QuestionTail.IsMatch(tail) || QuestionPhrases.IsMatch(tail);
    }

    /// <summary>Whitespace-collapsed, trimmed, ≤<paramref name="max"/> chars; null when empty.</summary>
    private static string? CollapseExcerpt(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var collapsed = Whitespace.Replace(text, " ").Trim();
        if (collapsed.Length > max) collapsed = collapsed[..max];
        return collapsed.Length == 0 ? null : collapsed;
    }

    /// <summary>Last path segment of a cwd; "Antigravity" when unknown.</summary>
    private static string ProjectNameOf(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return "Antigravity";
        try
        {
            var trimmed = cwd.TrimEnd('\\', '/');
            var name = Path.GetFileName(trimmed);
            if (!string.IsNullOrEmpty(name)) return name;
            return trimmed.Length > 0 ? trimmed : "Antigravity";
        }
        catch
        {
            return "Antigravity";
        }
    }

    /// <summary>
    /// Coerces a trajectory timestamp: epoch number (seconds when &lt; 1e12, else ms —
    /// also as a numeric string), ISO-8601 string, or a protobuf-style
    /// {seconds: ...} object. Anything else yields <paramref name="fallbackUtc"/>.
    /// </summary>
    private static DateTime CoerceTimestamp(JsonElement? value, DateTime fallbackUtc)
    {
        if (value is not { } v) return fallbackUtc;
        try
        {
            switch (v.ValueKind)
            {
                case JsonValueKind.Number:
                    return v.TryGetDouble(out var n) ? FromEpochNumber(n, fallbackUtc) : fallbackUtc;

                case JsonValueKind.String:
                {
                    var s = v.GetString() ?? "";
                    if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
                        return FromEpochNumber(num, fallbackUtc);
                    if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
                        return dto.UtcDateTime;
                    return fallbackUtc;
                }

                case JsonValueKind.Object:
                {
                    foreach (var key in SecondsKeys)
                    {
                        if (!v.TryGetProperty(key, out var sec)) continue;
                        if (sec.ValueKind == JsonValueKind.Number &&
                            sec.TryGetDouble(out var sn) && sn != 0)
                            return EpochMsToUtc(sn * 1000, fallbackUtc);
                        if (sec.ValueKind == JsonValueKind.String && sec.GetString() is { Length: > 0 } ss)
                            return double.TryParse(ss, NumberStyles.Float, CultureInfo.InvariantCulture, out var sv)
                                ? EpochMsToUtc(sv * 1000, fallbackUtc)
                                : fallbackUtc;
                    }
                    return fallbackUtc;
                }

                default:
                    return fallbackUtc;
            }
        }
        catch
        {
            return fallbackUtc;
        }
    }

    /// <summary>The JS heuristic: values under 1e12 are epoch seconds, otherwise epoch ms.</summary>
    private static DateTime FromEpochNumber(double value, DateTime fallbackUtc)
        => EpochMsToUtc(value < 1e12 ? value * 1000 : value, fallbackUtc);

    private static DateTime EpochMsToUtc(double ms, DateTime fallbackUtc)
    {
        if (!double.IsFinite(ms)) return fallbackUtc;
        try { return DateTimeOffset.FromUnixTimeMilliseconds((long)ms).UtcDateTime; }
        catch { return fallbackUtc; }
    }

    private static long ToEpochMs(DateTime utc)
    {
        try
        {
            return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc))
                .ToUnixTimeMilliseconds();
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>First key holding a non-empty string (mirrors the JS truthy-chain for strings).</summary>
    private static string? FirstString(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
            if (obj.TryGetProperty(key, out var v) &&
                v.ValueKind == JsonValueKind.String &&
                v.GetString() is { Length: > 0 } s)
                return s;
        return null;
    }

    /// <summary>First key holding a JS-truthy value of any type (for timestamp fields).</summary>
    private static JsonElement? FirstTruthy(JsonElement obj, string[] keys)
    {
        foreach (var key in keys)
        {
            if (!obj.TryGetProperty(key, out var v)) continue;
            switch (v.ValueKind)
            {
                case JsonValueKind.String when (v.GetString()?.Length ?? 0) > 0:
                    return v;
                case JsonValueKind.Number:
                    if (v.TryGetDouble(out var n) && n != 0) return v;
                    break;
                case JsonValueKind.Object:
                case JsonValueKind.Array:
                case JsonValueKind.True:
                    return v;
            }
        }
        return null;
    }

    /// <summary>Trajectory id as a string; numeric ids are stringified like the JS template literal.</summary>
    private static string? FirstIdString(JsonElement obj)
    {
        foreach (var key in IdKeys)
        {
            if (!obj.TryGetProperty(key, out var v)) continue;
            if (v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s) return s;
            if (v.ValueKind == JsonValueKind.Number)
            {
                if (v.TryGetDouble(out var n) && n == 0) continue; // falsy in JS
                return v.GetRawText();
            }
        }
        return null;
    }
}
