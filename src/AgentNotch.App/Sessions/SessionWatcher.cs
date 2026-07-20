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
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

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
    private AntigravityClient? _antigravity;
    private readonly string _codexDir;
    // Non-claude sources and process probing are disabled when a test injects
    // its own claudeHome — unit tests must stay hermetic.
    private readonly bool _multiSource;
    private DateTime _lastEmitUtc = DateTime.MinValue;
    private bool _emitScheduled;
    private bool _started;
    private bool _disposed;

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
        _emitTimer = new Timer(_ => Emit(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed) return;
            _started = true;
            if (_multiSource) _antigravity = new AntigravityClient();
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
        try { _antigravity?.Dispose(); } catch { }
    }

    // ---------- scheduling ----------

    private void PollTick()
    {
        try
        {
            TryAttachWatchers(); // dirs may appear after startup; watchers may have errored
            _antigravity?.RefreshIfDue(); // non-blocking; result lands in Latest for the next Emit
            Kick();
        }
        catch (Exception ex)
        {
            AppLog.Write("SessionWatcher: poll tick failed: " + ex);
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
            lock (_snapshotGate) // protects _spend from overlapping rebuilds
            {
                claude = TranscriptTail.BuildSnapshot(_claudeHome, _spend, IsProcessAlive, now);
            }
            snapshot = _multiSource ? MergeSources(claude, now) : claude;
        }
        catch (Exception ex)
        {
            AppLog.Write("SessionWatcher: snapshot failed: " + ex);
            return;
        }

        try
        {
            SessionsChanged?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            AppLog.Write("SessionWatcher: SessionsChanged handler threw: " + ex);
        }
    }

    // ---------- multi-agent merge ----------

    /// <summary>
    /// Joins the Claude snapshot with Codex rollouts and Antigravity trajectories,
    /// resolves which editor hosts each Claude session (Cursor / Antigravity /
    /// VS Code) for labeling and window jumping, then re-sorts and caps the list.
    /// </summary>
    private List<AgentSession> MergeSources(IReadOnlyList<AgentSession> claude, DateTime now)
    {
        var merged = new List<AgentSession>(claude);

        foreach (var session in claude)
        {
            if (session.Pid > 0 && session.Entrypoint != "claude-desktop")
            {
                try { session.Host = Interop.ProcessTree.HostAppOf(session.Pid); }
                catch (Exception ex) { AppLog.Write("SessionWatcher: host detect: " + ex.Message); }
            }
        }

        try
        {
            // Same gate the Electron collector used: closing Codex must clear its rows.
            if (Interop.ProcessTree.AnyRunning("codex", "chatgpt", "codex-code-mode-host"))
            {
                var codex = CodexTail.Collect(_codexDir, now);
                // "vscode" from the rollout only means "some VS Code fork" — pin it
                // down when exactly one candidate host is actually running.
                bool code = Interop.ProcessTree.AnyRunning("Code");
                bool cursor = Interop.ProcessTree.AnyRunning("Cursor");
                bool anti = Interop.ProcessTree.AnyRunning("Antigravity");
                foreach (var session in codex)
                {
                    if (session.Host == "vscode")
                    {
                        if (cursor && !code && !anti) session.Host = "cursor";
                        else if (anti && !code && !cursor) session.Host = "antigravity";
                    }
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

        static int Rank(SessionStatus s) => s switch
        {
            SessionStatus.NeedsYou => 0,
            SessionStatus.Finished => 1,
            _ => 2,
        };
        return merged
            .OrderBy(s => Rank(s.Status))
            .ThenByDescending(s => s.LastActivityUtc)
            .Take(8)
            .ToList();
    }

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
        watcher.Changed += (_, _) => Kick();
        watcher.Created += (_, _) => Kick();
        watcher.Deleted += (_, _) => Kick();
        watcher.Renamed += (_, _) => Kick();
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
        }
        try { watcher.Dispose(); } catch { }
        Kick(); // a buffer overflow means events were lost — rebuild soon
    }

    // ---------- liveness ----------

    /// <summary>
    /// Closing a session tab removes its registry file, but a hard crash can leave a
    /// stale one — verify the process actually exists before trusting the entry.
    /// </summary>
    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            try { return !process.HasExited; }
            catch { return true; } // exists but not queryable — alive
        }
        catch
        {
            return false; // no such process
        }
    }
}
