using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentNotch.App.Diagnostics;
using AgentNotch.App.Usage;

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
    private const string QuotaPath =
        "/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary";
    private const string ConfigPath =
        "/exa.language_server_pb.LanguageServerService/GetCascadeModelConfigData";
    private const string CsrfHeader = "x-codeium-csrf-token";

    /// <summary>How much of each Antigravity log we read from the end looking for the token.</summary>
    private const int LogTailBytes = 512 * 1024;

    /// <summary>At most this many trajectories are surfaced.</summary>
    private const int MaxTasks = 4;

    /// <summary>Floor between actual refresh attempts — callers may invoke <see cref="RefreshIfDue"/> freely.</summary>
    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromSeconds(1.5);

    /// <summary>How long a discovered (token, ports) endpoint is trusted before re-discovery.</summary>
    private static readonly TimeSpan ServerTtl = TimeSpan.FromSeconds(60);

    /// <summary>How long quotas and model configs are cached before re-fetching.</summary>
    private static readonly TimeSpan QuotaRefreshInterval = TimeSpan.FromSeconds(15);

    /// <summary>Non-working trajectories idle longer than this are leftovers — drop them.</summary>
    private static readonly TimeSpan TaskKeep = TimeSpan.FromMinutes(10);

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
    }) { Timeout = TimeSpan.FromSeconds(8) };

    private readonly object _gate = new();
    private volatile IReadOnlyList<AgentSession> _latest = Array.Empty<AgentSession>();
    private (string Token, int[] Ports)? _server;
    private int _lastGoodPort;
    private DateTime _serverCheckedUtc = DateTime.MinValue;
    private DateTime _lastAttemptUtc = DateTime.MinValue;
    private DateTime _quotasLastFetchedUtc = DateTime.MinValue;
    private volatile IReadOnlyList<UsageWindow> _geminiUsage = Array.Empty<UsageWindow>();
    private volatile IReadOnlyList<UsageWindow> _claudeGptUsage = Array.Empty<UsageWindow>();
    private volatile IReadOnlyList<UsageWindow> _argonUsage = Array.Empty<UsageWindow>();
    private volatile IReadOnlyDictionary<string, string> _modelLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    internal static volatile IReadOnlyList<UsageWindow> SharedGeminiUsage = Array.Empty<UsageWindow>();

    /// <summary>Latest Gemini usage windows for use across providers (e.g. OpenCode with Gemini).</summary>
    public static IReadOnlyList<UsageWindow> LatestGeminiUsage => SharedGeminiUsage;
    private bool _refreshing;
    private volatile bool _disposed;

    /// <summary>Result of the last successful fetch; empty while Antigravity isn't running.</summary>
    public IReadOnlyList<AgentSession> Latest => _latest;

    /// <summary>Fired whenever Antigravity sessions are updated.</summary>
    public event Action? Refreshed;

    /// <summary>
    /// Requests a refresh. Non-blocking and safe to call as often as convenient:
    /// at most one refresh runs at a time, actual attempts are ≥1.5s apart, and the
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
            if (server is { } srv)
            {
                // Probe the last working port first so we don't pay failed-port roundtrips every 1.5s.
                int preferred = _lastGoodPort;
                IEnumerable<int> orderedPorts = preferred > 0 && Array.IndexOf(srv.Ports, preferred) >= 0
                    ? srv.Ports.OrderByDescending(p => p == preferred)
                    : srv.Ports;

                foreach (var port in orderedPorts)
                {
                    if (_disposed) return;
                    if (await FetchTrajectoriesAsync(port, srv.Token).ConfigureAwait(false) is { } response)
                    {
                        _lastGoodPort = port;
                        await RefreshQuotasAndConfigsAsync(port, srv.Token).ConfigureAwait(false);
                        var tasks = MapTrajectories(response, DateTime.UtcNow);
                        if (tasks.Count > 0)
                        {
                            _latest = tasks;
                            Refreshed?.Invoke();
                            return;
                        }
                        break; // RPC responded cleanly on the active port; no need to probe other ports
                    }
                }
            }

            // Fallback: if RPC returned empty or was unreachable, scan recent local brain transcripts.
            if (Interop.ProcessTree.AnyRunning("antigravity", "language_server"))
            {
                var fallbackTasks = CollectFromBrainDir(DateTime.UtcNow);
                if (fallbackTasks.Count > 0)
                {
                    _latest = fallbackTasks;
                    Refreshed?.Invoke();
                    return;
                }
            }

            // Antigravity closed or idle with no tasks.
            var wasNotEmpty = _latest.Count > 0;
            _latest = Array.Empty<AgentSession>();
            if (wasNotEmpty) Refreshed?.Invoke();
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

    private static Task<JsonElement?> FetchTrajectoriesAsync(int port, string token)
        => CallRpcAsync(port, token, RpcPath);

    private static async Task<JsonElement?> CallRpcAsync(int port, string token, string path, string jsonPayload = "{}")
    {
        try
        {
            var content = new StringContent(jsonPayload, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}{path}")
            {
                Content = content,
            };
            request.Headers.TryAddWithoutValidation(CsrfHeader, token);

            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False)
                return null;
            return root.Clone();
        }
        catch
        {
            return null;
        }
    }

    private async Task RefreshQuotasAndConfigsAsync(int port, string token)
    {
        if (DateTime.UtcNow - _quotasLastFetchedUtc < QuotaRefreshInterval) return;
        _quotasLastFetchedUtc = DateTime.UtcNow;

        try
        {
            var quotaDoc = await CallRpcAsync(port, token, QuotaPath).ConfigureAwait(false);
            if (quotaDoc is { } qRoot) ParseQuotas(qRoot);

            var configDoc = await CallRpcAsync(port, token, ConfigPath).ConfigureAwait(false);
            if (configDoc is { } cRoot) ParseModelConfigs(cRoot);
        }
        catch (Exception ex)
        {
            AppLog.Write("antigravity: quota/config refresh failed: " + ex.Message);
        }
    }

    internal void ParseQuotas(JsonElement root)
    {
        try
        {
            JsonElement groupsElem = default;
            if (root.TryGetProperty("response", out var resp) && resp.TryGetProperty("groups", out var g1))
                groupsElem = g1;
            else if (root.TryGetProperty("groups", out var g2))
                groupsElem = g2;

            if (groupsElem.ValueKind != JsonValueKind.Array) return;

            var gemini = new List<UsageWindow>(2);
            var claudeGpt = new List<UsageWindow>(2);
            var argon = new List<UsageWindow>(1);

            foreach (var group in groupsElem.EnumerateArray())
            {
                var name = FirstString(group, "displayName", "label") ?? "";
                if (!group.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var b in buckets.EnumerateArray())
                {
                    var window = FirstString(b, "window") ?? "";
                    double remaining = 1.0;
                    if (b.TryGetProperty("remainingFraction", out var rf) && rf.TryGetDouble(out var d))
                        remaining = d;

                    double usedPct = Math.Clamp(Math.Round((1.0 - remaining) * 100), 0, 100);

                    DateTime? resetUtc = null;
                    if (FirstString(b, "resetTime") is { } rtStr &&
                        DateTimeOffset.TryParse(rtStr, CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
                    {
                        resetUtc = dto.UtcDateTime;
                    }

                    string label = window switch
                    {
                        "5h" => "5h",
                        "weekly" => "7d",
                        _ => window,
                    };

                    if (name.Contains("Argon", StringComparison.OrdinalIgnoreCase))
                    {
                        argon.Add(new UsageWindow("Argon", usedPct, resetUtc));
                    }
                    else if (name.Contains("Claude", StringComparison.OrdinalIgnoreCase) ||
                             name.Contains("GPT", StringComparison.OrdinalIgnoreCase))
                    {
                        claudeGpt.Add(new UsageWindow(label, usedPct, resetUtc));
                    }
                    else if (name.Contains("Gemini", StringComparison.OrdinalIgnoreCase))
                    {
                        gemini.Add(new UsageWindow(label, usedPct, resetUtc));
                    }
                }
            }

            if (gemini.Count > 0)
            {
                gemini.Sort((a, b) => string.CompareOrdinal(a.Label, b.Label));
                _geminiUsage = gemini;
                SharedGeminiUsage = gemini;
            }
            if (claudeGpt.Count > 0)
            {
                claudeGpt.Sort((a, b) => string.CompareOrdinal(a.Label, b.Label));
                _claudeGptUsage = claudeGpt;
            }
            if (argon.Count > 0)
            {
                _argonUsage = argon;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("antigravity: parse quotas failed: " + ex.Message);
        }
    }

    internal void ParseModelConfigs(JsonElement root)
    {
        try
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("clientModelConfigs", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    string? label = FirstString(item, "label");
                    string? modelId = FirstString(item, "modelId");
                    string? placeholder = null;
                    if (item.TryGetProperty("modelOrAlias", out var moa))
                        placeholder = FirstString(moa, "model");

                    if (!string.IsNullOrEmpty(label))
                    {
                        var clean = CleanModelLabel(label);
                        if (!string.IsNullOrEmpty(modelId)) map[modelId] = clean;
                        if (!string.IsNullOrEmpty(placeholder)) map[placeholder] = clean;
                    }
                }
            }

            if (root.TryGetProperty("defaultOverrideModelConfig", out var def) &&
                def.TryGetProperty("modelOrAlias", out var defMoa))
            {
                var defModel = FirstString(defMoa, "model");
                if (!string.IsNullOrEmpty(defModel) && map.TryGetValue(defModel, out var clean))
                    map["default"] = clean;
            }

            if (map.Count > 0) _modelLabels = map;
        }
        catch (Exception ex)
        {
            AppLog.Write("antigravity: parse model configs failed: " + ex.Message);
        }
    }

    public static string CleanModelLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "Gemini";
        int idx = label.IndexOf('(');
        if (idx > 0) label = label[..idx].Trim();
        return label.Trim();
    }

    public IReadOnlyList<UsageWindow> GetUsageForModel(string? model)
    {
        var m = (model ?? "").ToLowerInvariant();
        if (m.Contains("claude") || m.Contains("gpt") || m.Contains("3p"))
            return _claudeGptUsage.Count > 0 ? _claudeGptUsage : _geminiUsage;
        if (m.Contains("argon"))
        {
            var list = new List<UsageWindow>();
            var g5h = _geminiUsage.FirstOrDefault(w => w.Label == "5h");
            if (g5h is not null) list.Add(g5h);
            var argon = _argonUsage.FirstOrDefault() ?? _geminiUsage.FirstOrDefault(w => w.Label == "7d");
            if (argon is not null) list.Add(argon);
            return list.Count > 0 ? list : _geminiUsage;
        }
        return _geminiUsage;
    }

    // ---------- discovery (process + token + ports) ----------

    private static (string Token, int[] Ports)? DiscoverServer()
    {
        try
        {
            int pid = FindLanguageServerPid();
            if (pid == 0) return null;
            var token = FindCsrfToken(pid);
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
        @"(?:--)?csrf_token[\s=]+([0-9a-fA-F-]+)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Latest CSRF token: first checks language_server command line via Win32 query;
    /// falls back to Antigravity's own startup logs (head + tail).
    /// </summary>
    private static string? FindCsrfToken(int pid = 0)
    {
        if (pid > 0)
        {
            var cmd = Interop.ProcessTree.GetCommandLine(pid);
            if (!string.IsNullOrEmpty(cmd))
            {
                var match = CsrfTokenPattern.Match(cmd);
                if (match.Success) return match.Groups[1].Value;
            }
        }

        var logs = new List<FileInfo>();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        void TryAddLogs(string dir)
        {
            if (!Directory.Exists(dir)) return;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.log", SearchOption.AllDirectories))
                {
                    try { logs.Add(new FileInfo(file)); } catch { }
                }
            }
            catch { }
        }

        TryAddLogs(Path.Combine(appData, "Antigravity", "logs"));
        TryAddLogs(Path.Combine(appData, "Antigravity IDE", "logs"));
        TryAddLogs(Path.Combine(localAppData, "Antigravity", "logs"));
        TryAddLogs(Path.Combine(userProfile, ".gemini", "antigravity", "logs"));

        foreach (var log in logs.OrderByDescending(SafeLastWriteUtc))
        {
            var head = ReadHeadText(log.FullName);
            if (head is not null)
            {
                var matches = CsrfTokenPattern.Matches(head);
                if (matches.Count > 0) return matches[^1].Groups[1].Value;
            }
            var tail = ReadTailText(log.FullName);
            if (tail is not null)
            {
                var matches = CsrfTokenPattern.Matches(tail);
                if (matches.Count > 0) return matches[^1].Groups[1].Value;
            }
        }
        return null;
    }

    private static DateTime SafeLastWriteUtc(FileInfo file)
    {
        try { return file.LastWriteTimeUtc; } catch { return DateTime.MinValue; }
    }

    /// <summary>First ≤64KB of a log file — startup flags including --csrf_token land here.</summary>
    private static string? ReadHeadText(string path)
    {
        try
        {
            using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            int len = (int)Math.Min(fs.Length, 64 * 1024);
            if (len <= 0) return null;
            var buf = ArrayPool<byte>.Shared.Rent(len);
            try
            {
                fs.ReadExactly(buf, 0, len);
                return Encoding.UTF8.GetString(buf, 0, len);
            }
            finally { ArrayPool<byte>.Shared.Return(buf); }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Last ≤<paramref name="maxBytes"/> of a (possibly still-open) file; null when unreadable.</summary>
    private static string? ReadTailText(string path, int maxBytes = LogTailBytes)
    {
        try
        {
            using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            long size = fs.Length;
            int len = (int)Math.Min(size, maxBytes);
            if (len <= 0) return null;
            fs.Position = size - len;
            var buf = ArrayPool<byte>.Shared.Rent(len);
            try
            {
                fs.ReadExactly(buf, 0, len);
                return Encoding.UTF8.GetString(buf, 0, len);
            }
            finally { ArrayPool<byte>.Shared.Return(buf); }
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
                if (size < sizeof(int)) return Array.Empty<int>();

                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    uint result = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
                    if (result == ErrorInsufficientBuffer) continue;
                    if (result != 0 || size < sizeof(int)) return Array.Empty<int>();

                    int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
                    int count = Marshal.ReadInt32(buffer);
                    int maxRows = (size - sizeof(int)) / rowSize;
                    if (count <= 0) return Array.Empty<int>();
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

    private static readonly Regex Whitespace = new(
        "\\s+", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public struct TranscriptInfo
    {
        public SessionStatus? Status;
        public string? Tool;
        public string? Target;
        public string? Message;
        public string? Cwd;
        public string? Model;
        public DateTime? TimestampUtc;
    }

    private static readonly Regex ModelSelectionPattern = new(
        @"Model Selection from .*? to (.*?)(?:\.|\r|\n|\\n)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly record struct TranscriptCacheEntry(DateTime MtimeUtc, long Size, TranscriptInfo Info);

    private static readonly ConcurrentDictionary<string, TranscriptCacheEntry> TranscriptCache =
        new(StringComparer.OrdinalIgnoreCase);

    private const int TranscriptTailBytes = 256 * 1024;

    private static readonly string BrainDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".gemini", "antigravity", "brain");

    /// <summary>
    /// Reads the tail of a conversation's transcript.jsonl to discover the live step:
    /// questions asked by the model (ask_question -> NeedsYou), pending tool calls,
    /// or final answers. Also detects the model selected by the user.
    /// Memoized on (mtime, size) so historical transcripts aren't re-read every 1.5s.
    /// </summary>
    public static TranscriptInfo InspectTranscript(string? conversationId)
    {
        if (string.IsNullOrEmpty(conversationId)) return default;
        var transcriptPath = Path.Combine(BrainDir, conversationId, ".system_generated", "logs", "transcript.jsonl");

        DateTime mtimeUtc;
        long size;
        try
        {
            var fi = new FileInfo(transcriptPath);
            if (!fi.Exists) return default;
            mtimeUtc = fi.LastWriteTimeUtc;
            size = fi.Length;
        }
        catch
        {
            return default;
        }

        if (TranscriptCache.TryGetValue(transcriptPath, out var cached)
            && cached.MtimeUtc == mtimeUtc && cached.Size == size)
            return cached.Info;

        var info = ParseTranscriptTail(transcriptPath);
        if (TranscriptCache.Count >= 256)
            TranscriptCache.Clear();
        TranscriptCache[transcriptPath] = new TranscriptCacheEntry(mtimeUtc, size, info);
        return info;
    }

    private static TranscriptInfo ParseTranscriptTail(string transcriptPath)
    {
        try
        {
            var tail = ReadTailText(transcriptPath, TranscriptTailBytes);
            if (string.IsNullOrEmpty(tail)) return default;

            var lines = tail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length == 0) return default;

            string? detectedCwd = null;
            string? detectedModel = null;

            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i];
                if (!line.StartsWith('{') || !line.EndsWith('}')) continue;

                if (detectedModel is null && line.Contains("Model Selection from", StringComparison.OrdinalIgnoreCase))
                {
                    var m = ModelSelectionPattern.Match(line);
                    if (m.Success) detectedModel = CleanModelLabel(m.Groups[1].Value);
                }

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (detectedModel is null && root.TryGetProperty("model", out var mProp) && mProp.ValueKind == JsonValueKind.String)
                {
                    var ms = mProp.GetString();
                    if (!string.IsNullOrEmpty(ms)) detectedModel = CleanModelLabel(ms);
                }

                var type = FirstString(root, "type") ?? "";
                var source = FirstString(root, "source") ?? "";
                var createdStr = FirstString(root, "created_at", "timestamp");
                DateTime? timeUtc = null;
                if (!string.IsNullOrEmpty(createdStr) &&
                    DateTimeOffset.TryParse(createdStr, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
                {
                    timeUtc = dto.UtcDateTime;
                }

                if (detectedCwd is null)
                {
                    if (root.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var call in tc.EnumerateArray())
                        {
                            if (call.TryGetProperty("args", out var args))
                                detectedCwd = FirstString(args, "Cwd", "cwd", "workdir");
                            if (detectedCwd is not null) break;
                        }
                    }
                }

                if (type == "USER_INPUT" || source == "USER_EXPLICIT")
                {
                    return new TranscriptInfo
                    {
                        Status = SessionStatus.Working,
                        Message = "Thinking…",
                        Cwd = detectedCwd,
                        Model = detectedModel,
                        TimestampUtc = timeUtc,
                    };
                }

                if (type != "PLANNER_RESPONSE")
                {
                    // A tool execution step (RUN_COMMAND, REPLACE_FILE_CONTENT, VIEW_FILE, etc.)
                    // means the agent is in the middle of executing actions in its turn.
                    var toolName = type.ToLowerInvariant();
                    return new TranscriptInfo
                    {
                        Status = SessionStatus.Working,
                        Tool = toolName,
                        Target = "Working…",
                        Message = "Working…",
                        Cwd = detectedCwd,
                        Model = detectedModel,
                        TimestampUtc = timeUtc,
                    };
                }

                if (type == "PLANNER_RESPONSE")
                {
                    if (root.TryGetProperty("tool_calls", out var toolCalls) &&
                        toolCalls.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var call in toolCalls.EnumerateArray())
                        {
                            if (call.ValueKind != JsonValueKind.Object) continue;
                            var toolName = FirstString(call, "name");
                            if (string.IsNullOrEmpty(toolName)) continue;

                            if (toolName.Equals("ask_question", StringComparison.OrdinalIgnoreCase))
                            {
                                string? qText = null;
                                if (call.TryGetProperty("args", out var args) &&
                                    args.TryGetProperty("questions", out var qs) &&
                                    qs.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var q in qs.EnumerateArray())
                                    {
                                        qText = FirstString(q, "question", "title", "text");
                                        if (!string.IsNullOrEmpty(qText)) break;
                                    }
                                }
                                var msg = CollapseExcerpt(qText, 100) ?? "Asking you a question";
                                return new TranscriptInfo
                                {
                                    Status = SessionStatus.NeedsYou,
                                    Tool = "ask_question",
                                    Target = msg,
                                    Message = msg,
                                    Cwd = detectedCwd,
                                    Model = detectedModel,
                                    TimestampUtc = timeUtc,
                                };
                            }

                            string? target = null;
                            if (call.TryGetProperty("args", out var toolArgs))
                            {
                                if (toolName.Equals("run_command", StringComparison.OrdinalIgnoreCase))
                                {
                                    var cmd = FirstString(toolArgs, "CommandLine", "command", "cmd");
                                    if (!string.IsNullOrEmpty(cmd))
                                        target = "Running " + ShortCommand(cmd);
                                }
                                else if (toolName.Contains("file", StringComparison.OrdinalIgnoreCase) ||
                                         toolName.Contains("replace", StringComparison.OrdinalIgnoreCase) ||
                                         toolName.Contains("write", StringComparison.OrdinalIgnoreCase))
                                {
                                    var path = FirstString(toolArgs, "TargetFile", "path", "AbsolutePath", "file_path");
                                    if (!string.IsNullOrEmpty(path))
                                        target = "Editing " + Path.GetFileName(path);
                                }
                            }

                            target ??= "Running " + toolName;
                            return new TranscriptInfo
                            {
                                Status = SessionStatus.Working,
                                Tool = toolName,
                                Target = target,
                                Message = target,
                                Cwd = detectedCwd,
                                Model = detectedModel,
                                TimestampUtc = timeUtc,
                            };
                        }
                    }

                    var content = FirstString(root, "content");
                    if (!string.IsNullOrEmpty(content))
                    {
                        // A closing message is a finished task, question mark or not; only an
                        // ask_question step (handled above) is an active ask.
                        var msg = CollapseExcerpt(content, 100) ?? "Task complete";
                        return new TranscriptInfo
                        {
                            Status = SessionStatus.Finished,
                            Message = msg,
                            Cwd = detectedCwd,
                            Model = detectedModel,
                            TimestampUtc = timeUtc,
                        };
                    }
                }
            }

            if (detectedModel is null)
            {
                var head = ReadHeadText(transcriptPath);
                if (!string.IsNullOrEmpty(head) && head.Contains("Model Selection from", StringComparison.OrdinalIgnoreCase))
                {
                    var m = ModelSelectionPattern.Match(head);
                    if (m.Success) detectedModel = CleanModelLabel(m.Groups[1].Value);
                }
            }

            if (detectedModel is not null)
                return new TranscriptInfo { Model = detectedModel };
        }
        catch (Exception ex)
        {
            AppLog.Write("antigravity: transcript tail parse failed: " + ex.Message);
        }

        return default;
    }

    public List<AgentSession> CollectFromBrainDir(DateTime nowUtc)
        => CollectFromBrainDir(nowUtc, _modelLabels, GetUsageForModel);

    /// <summary>Fallback: scans recent conversation directories in ~/.gemini/antigravity/brain.</summary>
    public static List<AgentSession> CollectFromBrainDir(
        DateTime nowUtc,
        IReadOnlyDictionary<string, string>? modelLabels = null,
        Func<string?, IReadOnlyList<UsageWindow>>? usageSelector = null)
    {
        var tasks = new List<AgentSession>();
        if (!Directory.Exists(BrainDir)) return tasks;

        try
        {
            var dirs = Directory.GetDirectories(BrainDir);
            foreach (var dir in dirs)
            {
                var dirInfo = new DirectoryInfo(dir);
                var id = dirInfo.Name;
                if (id.StartsWith('.')) continue;

                var transcriptPath = Path.Combine(dir, ".system_generated", "logs", "transcript.jsonl");
                if (!File.Exists(transcriptPath)) continue;

                var mtimeUtc = File.GetLastWriteTimeUtc(transcriptPath);
                if (nowUtc - mtimeUtc > TaskKeep) continue;

                var info = InspectTranscript(id);
                if (info.Status is not { } status) continue;

                var updatedAtUtc = info.TimestampUtc ?? mtimeUtc;
                if (status != SessionStatus.Working && nowUtc - updatedAtUtc > TaskKeep) continue;

                var cwd = info.Cwd ?? "";
                string model = !string.IsNullOrEmpty(info.Model)
                    ? info.Model
                    : (modelLabels is not null && modelLabels.TryGetValue("default", out var dm) ? dm : "Gemini");

                tasks.Add(new AgentSession
                {
                    SessionId = "antigravity:" + id,
                    Agent = "antigravity",
                    Host = "antigravity",
                    Model = model,
                    Usage = usageSelector?.Invoke(model) ?? Array.Empty<UsageWindow>(),
                    HostLabel = "Antigravity",
                    Cwd = cwd,
                    ProjectName = ProjectNameOf(cwd),
                    Entrypoint = "",
                    StartedUtc = updatedAtUtc,
                    Pid = 0,
                    Status = status,
                    CurrentTool = status == SessionStatus.Working || status == SessionStatus.NeedsYou ? info.Tool : null,
                    CurrentTarget = status == SessionStatus.Working || status == SessionStatus.NeedsYou ? info.Target : null,
                    LastMessage = info.Message ?? (status == SessionStatus.Working ? "Working…" : "Task complete"),
                    TokensSpent = 0,
                    LastActivityUtc = updatedAtUtc,
                });
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("antigravity: brain scan failed: " + ex.Message);
        }

        return tasks.OrderByDescending(t => t.LastActivityUtc).Take(MaxTasks).ToList();
    }

    private static string? ExtractConversationId(JsonElement t)
    {
        if (FirstString(t, "rootConversationId", "conversationId", "trajectoryId", "trajectory_id", "id") is { } s)
            return s;
        if (t.TryGetProperty("trajectoryMetadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
        {
            if (FirstString(meta, "rootConversationId", "conversationId", "trajectoryId", "trajectory_id", "id") is { } ms)
                return ms;
        }
        return null;
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

    public List<AgentSession> MapTrajectories(JsonElement response, DateTime nowUtc)
        => MapTrajectories(response, nowUtc, _modelLabels, GetUsageForModel);

    /// <summary>
    /// Maps a GetAllCascadeTrajectories response to display sessions — the pure,
    /// unit-tested core. Tolerates any response shape: the trajectory list is the
    /// first array found under trajectories/cascadeTrajectories/summaries/items
    /// (recursively, depth ≤3); items with an unrecognized status are skipped;
    /// non-working items idle over 10 minutes are dropped; at most 4 returned.
    /// </summary>
    public static List<AgentSession> MapTrajectories(
        JsonElement response,
        DateTime nowUtc,
        IReadOnlyDictionary<string, string>? modelLabels = null,
        Func<string?, IReadOnlyList<UsageWindow>>? usageSelector = null)
    {
        var tasks = new List<AgentSession>();
        try
        {
            if (FirstContainer(response, 0) is not { } container) return tasks;

            foreach (var t in EnumerateItems(container))
            {
                var rawStatus = (FirstString(t, StatusKeys) ?? "").ToLowerInvariant();
                var rpcUpdatedAtUtc = CoerceTimestamp(FirstTruthy(t, TimeKeys), nowUtc);
                bool rpcWorking = WorkingStatus.IsMatch(rawStatus);

                // Skip disk inspection for historical finished trajectories that haven't been
                // touched in > TaskKeep — avoids reading 80+ old transcripts on startup.
                var convId = ExtractConversationId(t);
                if (!rpcWorking && nowUtc - rpcUpdatedAtUtc > TaskKeep)
                {
                    if (string.IsNullOrEmpty(convId)) continue;
                    var tp = Path.Combine(BrainDir, convId, ".system_generated", "logs", "transcript.jsonl");
                    try
                    {
                        var fi = new FileInfo(tp);
                        if (!fi.Exists || nowUtc - fi.LastWriteTimeUtc > TaskKeep) continue;
                    }
                    catch { continue; }
                }

                var summary = CollapseExcerpt(FirstString(t, SummaryKeys), 100);
                var transcript = InspectTranscript(convId);

                SessionStatus? mapped = null;
                if (transcript.Status is { } tsStatus)
                {
                    mapped = tsStatus;
                }
                else if (NeedsYouStatus.IsMatch(rawStatus)) mapped = SessionStatus.NeedsYou;
                else if (rpcWorking) mapped = SessionStatus.Working;
                else if (FinishedStatus.IsMatch(rawStatus))
                    mapped = SessionStatus.Finished;
                if (mapped is not { } status) continue;

                var updatedAtUtc = transcript.TimestampUtc ?? rpcUpdatedAtUtc;
                if (status != SessionStatus.Working && nowUtc - updatedAtUtc > TaskKeep) continue;

                var cwd = ExtractCwd(t) ?? transcript.Cwd;
                var id = FirstIdString(t) ?? convId ??
                         (cwd ?? "agent") + ":" + ToEpochMs(updatedAtUtc).ToString(CultureInfo.InvariantCulture);

                var message = transcript.Message ?? summary ?? status switch
                {
                    SessionStatus.Working => "Working…",
                    SessionStatus.Finished => "Task complete",
                    _ => "Waiting for you",
                };
                var tool = (status == SessionStatus.Working || status == SessionStatus.NeedsYou) ? transcript.Tool : null;
                var target = (status == SessionStatus.Working || status == SessionStatus.NeedsYou) ? transcript.Target : null;

                string? model = transcript.Model;
                if (string.IsNullOrEmpty(model))
                {
                    if (FirstString(t, "model", "modelId") is { } rawModel)
                    {
                        if (modelLabels is not null && modelLabels.TryGetValue(rawModel, out var resolved))
                            model = resolved;
                        else
                            model = CleanModelLabel(rawModel);
                    }
                    else if (modelLabels is not null && modelLabels.TryGetValue("default", out var defModel))
                    {
                        model = defModel;
                    }
                }
                model ??= "Gemini";

                tasks.Add(new AgentSession
                {
                    SessionId = "antigravity:" + id,
                    Agent = "antigravity",
                    Host = "antigravity",
                    Model = model,
                    Usage = usageSelector?.Invoke(model) ?? Array.Empty<UsageWindow>(),
                    HostLabel = "Antigravity",
                    Cwd = cwd ?? "",
                    ProjectName = ProjectNameOf(cwd),
                    Entrypoint = "",
                    StartedUtc = CoerceTimestamp(FirstTruthy(t, CreatedKeys), updatedAtUtc),
                    Pid = 0,
                    Status = status,
                    CurrentTool = tool,
                    CurrentTarget = target,
                    LastMessage = message,
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
