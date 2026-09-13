using System.Diagnostics;
using AgentNotch.App.Diagnostics;
using Timer = System.Threading.Timer;

namespace AgentNotch.App.Sessions;

/// <summary>
/// Watches the Claude Code home directory for live-session activity and emits
/// debounced <see cref="AgentSession"/> snapshots. All parsing/derivation lives in
/// <see cref="TranscriptTail"/> — this class only decides WHEN to rebuild:
///  * FileSystemWatcher on sessions\ (registry churn) and projects\ (transcript
///    appends, recursive),
///  * a 5s fallback poll (FSW misses events on some filesystems, and time-based
///    transitions like "idle too long" need a clock),
///  * a 1s debounce so a busy transcript can't flood the UI.
/// <see cref="SessionsChanged"/> fires on threadpool threads — the composition root
/// marshals to the UI thread.
/// </summary>
public sealed class SessionWatcher : IDisposable
{
    private static readonly TimeSpan MinEmitInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly object _snapshotGate = new();
    private readonly string _claudeHome;
    private readonly Dictionary<string, TranscriptTail.SpendState> _spend =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _emitTimer;
    private Timer? _pollTimer;
    private FileSystemWatcher? _sessionsWatcher;
    private FileSystemWatcher? _projectsWatcher;
    private FileSystemWatcher? _codexWatcher;
    private FileSystemWatcher? _cursorWatcher;
    private AntigravityClient? _antigravity;
    private readonly string _codexDir;
    private readonly string _cursorProjectsDir;
    // Non-claude sources and process probing are disabled when a test injects
    // its own claudeHome — unit tests must stay hermetic.
    private readonly bool _multiSource;
    private DateTime _lastEmitUtc = DateTime.MinValue;
    private bool _emitScheduled;
    private bool _started;
    private bool _disposed;
    private volatile IReadOnlyList<AgentSession> _cursorLatest = Array.Empty<AgentSession>();
    private volatile IReadOnlyList<AgentSession> _lastClaude = Array.Empty<AgentSession>();
    private string _cursorSig = "";

    /// <summary>Fresh snapshot after any change. Any thread; emissions are ≥1s apart.</summary>
    public event Action<IReadOnlyList<AgentSession>>? SessionsChanged;

    /// <param name="claudeHome">Override for tests; default %USERPROFILE%\.claude.</param>
    public SessionWatcher(string? claudeHome = null)
    {
        _multiSource = string.IsNullOrWhiteSpace(claudeHome);
        _claudeHome = _multiSource
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : claudeHome!;
        _codexDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
        _cursorProjectsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor", "projects");
        _emitTimer = new Timer(_ => Emit(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed) return;
            _started = true;
            if (_multiSource)
            {
                _antigravity = new AntigravityClient();
                _antigravity.Refreshed += Kick;
            }
            // First tick fires immediately -> initial snapshot right after startup.
            _pollTimer = new Timer(_ => PollTick(), null, TimeSpan.Zero, PollInterval);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        try { _pollTimer?.Dispose(); } catch { }
        try { _emitTimer.Dispose(); } catch { }
        try { _sessionsWatcher?.Dispose(); } catch { }
        try { _projectsWatcher?.Dispose(); } catch { }
        try { _codexWatcher?.Dispose(); } catch { }
        try { _cursorWatcher?.Dispose(); } catch { }
        try { _antigravity?.Dispose(); } catch { }
    }

    // ---------- scheduling ----------

    private void PollTick()
    {
        try
        {
            TryAttachWatchers(); // dirs may appear after startup; watchers may have errored
            _antigravity?.RefreshIfDue(); // non-blocking; result lands in Latest for the next Emit
            RefreshCursorFast(); // must not wait on Claude file IO — that can stall for minutes
            Kick();
        }
        catch (Exception ex)
        {
            AppLog.Write("SessionWatcher: poll tick failed: " + ex);
        }
    }

    /// <summary>
    /// Cursor transcripts are small and cache-friendly; Claude's BuildSnapshot can
    /// stall on a large ~/.claude tree and would otherwise starve the pill of
    /// Cursor Working/NeedsYou updates. Push a merged snapshot as soon as Cursor
    /// changes, using the last known Claude list.
    /// </summary>
    private void RefreshCursorFast()
    {
        if (!_multiSource || !Directory.Exists(_cursorProjectsDir)) return;
        try
        {
            // Same gate Codex gets: with Cursor closed there is no live agent, so
            // skip the whole ~\.cursor\projects walk (one dir per project the user
            // has ever opened) instead of paying for it every 2 s forever.
            var cursor = CursorRunning()
                ? CursorTail.Collect(_cursorProjectsDir, DateTime.UtcNow)
                : (IReadOnlyList<AgentSession>)Array.Empty<AgentSession>();
            _cursorLatest = cursor;
            var sig = string.Join("|", cursor.Select(s => $"{s.SessionId}:{s.Status}:{s.LastMessage}"));
            if (sig == _cursorSig) return;
            _cursorSig = sig;
            Publish(MergeSources(_lastClaude, DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            AppLog.Write("SessionWatcher: cursor fast refresh: " + ex.Message);
        }
    }

    private void Publish(IReadOnlyList<AgentSession> snapshot)
    {
        try { SessionsChanged?.Invoke(snapshot); }
        catch (Exception ex)
        {
            AppLog.Write("SessionWatcher: SessionsChanged handler threw: " + ex);
        }
    }

    /// <summary>Request a rebuild; coalesces bursts and keeps emissions ≥1s apart.</summary>
    private void Kick()
    {
        lock (_gate)
        {
            if (_disposed || !_started || _emitScheduled) return;
            var elapsed = DateTime.UtcNow - _lastEmitUtc;
            var delay = elapsed >= MinEmitInterval ? TimeSpan.Zero : MinEmitInterval - elapsed;
            _emitScheduled = true;
            try { _emitTimer.Change(delay, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { }
        }
    }

    private void Emit()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _emitScheduled = false;
            _lastEmitUtc = DateTime.UtcNow;
        }

        IReadOnlyList<AgentSession> snapshot;
        try
        {
            var now = DateTime.UtcNow;
            IReadOnlyList<AgentSession> claude;
            var sw = Stopwatch.StartNew();
            lock (_snapshotGate) // protects _spend from overlapping rebuilds
            {
                // Prefer the editor-hosted session when a project has several open
                // (only in production; tests inject their own home and no classifier).
                Func<int, int>? hostPref = _multiSource ? Interop.ProcessTree.SessionHostRank : null;
                claude = TranscriptTail.BuildSnapshot(_claudeHome, _spend, IsProcessAlive, now, hostPref);
            }
            if (sw.ElapsedMilliseconds > 1500)
                AppLog.Write($"SessionWatcher: Claude snapshot took {sw.ElapsedMilliseconds} ms");
            _lastClaude = claude;
            snapshot = _multiSource ? MergeSources(claude, now) : claude;
        }
        catch (Exception ex)
        {
            AppLog.Write("SessionWatcher: snapshot failed: " + ex);
            return;
        }

        Publish(snapshot);
    }

    // ---------- multi-agent merge ----------

    /// <summary>
    /// Joins the Claude snapshot with Codex rollouts, Antigravity trajectories, and
    /// Cursor agent transcripts; resolves which editor hosts each Claude session
    /// (Cursor / Antigravity / VS Code) for labeling and window jumping, then
    /// re-sorts and caps the list.
    /// </summary>
    private List<AgentSession> MergeSources(IReadOnlyList<AgentSession> claude, DateTime now)
    {
        var merged = new List<AgentSession>(claude);

        foreach (var session in claude)
        {
            if (session.Entrypoint == "claude-desktop")
            {
                // Not just "Claude" — that would collide with the brand chip and
                // get deduped away, hiding where the session actually runs.
                session.HostLabel = "Claude Desktop";
                continue;
            }
            if (session.Pid <= 0) continue;
            try
            {
                // Host = an app we can jump to (editors only); HostLabel also names
                // terminals, so a plain CLI session still says where it runs.
                session.Host = Interop.ProcessTree.HostAppOf(session.Pid);
                session.HostLabel = Interop.ProcessTree.HostLabelOf(session.Pid);
            }
            catch (Exception ex) { AppLog.Write("SessionWatcher: host detect: " + ex.Message); }
        }

        try
        {
            // Same gate the Electron collector used: closing Codex must clear its rows.
            if (Interop.ProcessTree.AnyRunning("codex", "chatgpt", "codex-code-mode-host", "OpenAI.Codex", "Codex Desktop", "ChatGPT Desktop"))
            {
                var codex = CodexTail.Collect(_codexDir, now);
                // A task and its reviewer sub-agents share a cwd and each get their own
                // rollout; collapse them to one row (the most recently active) so a
                // single running task doesn't sprout phantom duplicates.
                codex = codex
                    .GroupBy(s => string.IsNullOrEmpty(s.Cwd) ? s.SessionId : s.Cwd,
                             StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(s => s.LastActivityUtc).First())
                    .ToList();
                // A rollout only says "vscode" (some VS Code fork). Pin down the real
                // editor by walking the running Codex process up to its host window
                // app — the Codex extension is a child of Cursor.exe / Code.exe.
                var codexHost = Interop.ProcessTree.HostOfRunningProcess("codex", "chatgpt", "OpenAI.Codex");
                var codexLabel = Interop.ProcessTree.HostLabelOfRunningProcess("codex", "chatgpt", "OpenAI.Codex");
                foreach (var session in codex)
                {
                    if (session.Host == "vscode" && codexHost is not null) session.Host = codexHost;
                    session.HostLabel ??= codexLabel ?? "Codex Desktop";
                }
                merged.AddRange(codex);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("SessionWatcher: codex collect: " + ex.Message);
        }

        try
        {
            var anti = _antigravity?.Latest;
            if (anti is { Count: > 0 }) merged.AddRange(anti);
        }
        catch (Exception ex)
        {
            AppLog.Write("SessionWatcher: antigravity merge: " + ex.Message);
        }

        try
        {
            var cursor = _cursorLatest;
            if (cursor.Count == 0 && Directory.Exists(_cursorProjectsDir) && CursorRunning())
            {
                // First full emit before a poll tick, or tests without PollTick.
                cursor = CursorTail.Collect(_cursorProjectsDir, now);
                _cursorLatest = cursor;
            }
            if (cursor.Count > 0) merged.AddRange(cursor);
        }
        catch (Exception ex)
        {
            AppLog.Write("SessionWatcher: cursor collect: " + ex.Message);
        }

        static int Rank(SessionStatus s) => s switch
        {
            SessionStatus.NeedsYou => 0,
            SessionStatus.Working => 1,
            _ => 2,
        };
        return merged
            .OrderBy(s => Rank(s.Status))
            .ThenByDescending(s => s.LastActivityUtc)
            .Take(10)
            .ToList();
    }

    /// <summary>Is a Cursor window (or its headless CLI agent) actually running?</summary>
    private static bool CursorRunning() =>
        Interop.ProcessTree.AnyRunning("Cursor", "cursor-agent");

    // ---------- file system watchers ----------

    private void TryAttachWatchers()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                if (_sessionsWatcher is null)
                {
                    var dir = Path.Combine(_claudeHome, "sessions");
                    if (Directory.Exists(dir))
                        _sessionsWatcher = MakeWatcher(dir, "*.json", includeSubdirectories: false);
                }
                if (_projectsWatcher is null)
                {
                    var dir = Path.Combine(_claudeHome, "projects");
                    if (Directory.Exists(dir))
                        _projectsWatcher = MakeWatcher(dir, "*.jsonl", includeSubdirectories: true);
                }
                if (_multiSource && _codexWatcher is null && Directory.Exists(_codexDir))
                    _codexWatcher = MakeWatcher(_codexDir, "*.jsonl", includeSubdirectories: true);
                if (_multiSource && _cursorWatcher is null && Directory.Exists(_cursorProjectsDir))
                    _cursorWatcher = MakeWatcher(_cursorProjectsDir, "*.jsonl", includeSubdirectories: true);
            }
            catch (Exception ex)
            {
                AppLog.Write("SessionWatcher: watcher attach failed: " + ex.Message);
            }
        }
    }

    private FileSystemWatcher MakeWatcher(string dir, string filter, bool includeSubdirectories)
    {
        var watcher = new FileSystemWatcher(dir, filter)
        {
            IncludeSubdirectories = includeSubdirectories,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName |
                           NotifyFilters.Size | NotifyFilters.CreationTime,
            InternalBufferSize = 64 * 1024, // transcripts append fast; avoid overflow
        };
        // A create/delete/rename can mean a NEW session directory, which is the one
        // thing CursorTail's cached directory structure cannot notice on its own.
        // Appends (Changed) never change the structure, so they must not invalidate it.
        void Structural()
        {
            // One watcher type per directory, but both caches key off "a file appeared
            // or vanished", and rebuilding either is cheap — so drop both rather than
            // thread the distinction through for no measurable gain.
            CursorTail.InvalidateStructure();
            TranscriptTail.InvalidateTranscriptMap();
            Kick();
        }
        watcher.Changed += (_, _) => Kick();
        watcher.Created += (_, _) => Structural();
        watcher.Deleted += (_, _) => Structural();
        watcher.Renamed += (_, _) => Structural();
        watcher.Error += (_, e) => OnWatcherError(watcher, e);
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private void OnWatcherError(FileSystemWatcher watcher, ErrorEventArgs e)
    {
        AppLog.Write("SessionWatcher: fs watcher error: " + (e.GetException()?.Message ?? "unknown"));
        lock (_gate)
        {
            // Drop it; the next poll tick re-attaches a fresh one.
            if (ReferenceEquals(watcher, _sessionsWatcher)) _sessionsWatcher = null;
            else if (ReferenceEquals(watcher, _projectsWatcher)) _projectsWatcher = null;
            else if (ReferenceEquals(watcher, _codexWatcher)) _codexWatcher = null;
            else if (ReferenceEquals(watcher, _cursorWatcher)) _cursorWatcher = null;
        }
        try { watcher.Dispose(); } catch { }
        Kick(); // a buffer overflow means events were lost — rebuild soon
    }

    // ---------- liveness ----------

    /// <summary>
    /// Closing a session tab removes its registry file, but a hard crash can leave a
    /// stale one — verify the process actually exists before trusting the entry.
    /// </summary>
    private static bool IsProcessAlive(int pid) => Interop.ProcessTree.IsAlive(pid);
}
