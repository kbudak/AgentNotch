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
    private DateTime _lastEmitUtc = DateTime.MinValue;
    private bool _emitScheduled;
    private bool _started;
    private bool _disposed;

    /// <summary>Fresh snapshot after any change. Any thread; emissions are ≥1s apart.</summary>
    public event Action<IReadOnlyList<AgentSession>>? SessionsChanged;

    /// <param name="claudeHome">Override for tests; default %USERPROFILE%\.claude.</param>
    public SessionWatcher(string? claudeHome = null)
    {
        _claudeHome = string.IsNullOrWhiteSpace(claudeHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : claudeHome;
        _emitTimer = new Timer(_ => Emit(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed) return;
            _started = true;
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
    }

    // ---------- scheduling ----------

    private void PollTick()
    {
        try
        {
            TryAttachWatchers(); // dirs may appear after startup; watchers may have errored
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
            lock (_snapshotGate) // protects _spend from overlapping rebuilds
            {
                snapshot = TranscriptTail.BuildSnapshot(_claudeHome, _spend, IsProcessAlive, DateTime.UtcNow);
            }
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
