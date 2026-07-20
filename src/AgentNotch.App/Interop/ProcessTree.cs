using System.Diagnostics;
using System.Runtime.InteropServices;
using AgentNotch.App.Diagnostics;

namespace AgentNotch.App.Interop;

/// <summary>
/// Cheap process-level facts for agent and host detection.
///
///  * <see cref="AnyRunning"/> — is any process with one of these names alive?
///    Lets collectors skip file scans entirely when the agent app isn't even
///    running. Backed by a ≤2 s cached name snapshot, so a burst of calls (one
///    per candidate agent per poll) costs a single Process.GetProcesses().
///  * <see cref="HostAppOf"/> — which known host app (Cursor, Antigravity,
///    VS Code) sits above a pid in the parent-process chain; resolves whether a
///    CLI session lives in an editor's integrated terminal. Toolhelp32-based —
///    works at standard user privilege and without WMI — with a 30 s per-pid
///    result cache because a pid's ancestry practically never changes.
///
/// Never throws — failures are logged via <see cref="AppLog"/> and reported as
/// false / null.
/// </summary>
public static class ProcessTree
{
    // ------------------------------------------------------------ AnyRunning ----

    private const long NamesTtlMs = 2_000;
    private static readonly object NamesGate = new();
    private static HashSet<string>? _names;
    private static long _namesAtMs;

    /// <summary>
    /// True when at least one currently-running process has one of the given
    /// names (case-insensitive; a trailing ".exe" on a query name is tolerated).
    /// </summary>
    public static bool AnyRunning(params string[] processNames)
    {
        try
        {
            if (processNames is not { Length: > 0 }) return false;
            var names = SnapshotNames();
            foreach (var name in processNames)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (names.Contains(TrimExe(name))) return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Write("proctree: AnyRunning failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>Names of all running processes, cached for <see cref="NamesTtlMs"/>.</summary>
    private static HashSet<string> SnapshotNames()
    {
        lock (NamesGate)
        {
            long now = Environment.TickCount64;
            if (_names is not null && now - _namesAtMs <= NamesTtlMs)
                return _names;

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                try { set.Add(p.ProcessName); }
                catch { /* exited mid-snapshot — skip */ }
                finally { p.Dispose(); }
            }
            _names = set;
            _namesAtMs = now;
            return set;
        }
    }

    /// <summary>"Cursor.exe" -> "Cursor" (Process.ProcessName carries no extension).</summary>
    private static string TrimExe(string name)
    {
        name = name.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name[..^4]
            : name;
    }

    // ------------------------------------------------------------- HostAppOf ----

    private const int MaxDepth = 24;
    private const long HostTtlMs = 30_000;
    private static readonly object HostGate = new();
    private static readonly Dictionary<int, (long AtMs, string? Host)> HostCache = new();

    /// <summary>
    /// Walks the parent-process chain of <paramref name="pid"/> upward (starting
    /// at the pid itself) and returns the nearest known host app: "cursor"
    /// (Cursor.exe), "antigravity" (Antigravity.exe) or "vscode" (Code.exe).
    /// Null when the chain contains none, the pid is gone, or on any error.
    /// The result (including null) is cached for 30 s per pid.
    /// </summary>
    public static string? HostAppOf(int pid)
    {
        try
        {
            if (pid <= 0) return null;

            long now = Environment.TickCount64;
            lock (HostGate)
            {
                if (HostCache.TryGetValue(pid, out var hit) && now - hit.AtMs <= HostTtlMs)
                    return hit.Host;
            }

            string? host = ResolveHost(pid);

            lock (HostGate)
            {
                // Keep the cache bounded: once it grows, evict expired entries.
                if (HostCache.Count >= 256)
                {
                    foreach (var stale in HostCache
                                 .Where(kv => now - kv.Value.AtMs > HostTtlMs)
                                 .Select(kv => kv.Key)
                                 .ToList())
                        HostCache.Remove(stale);
                }
                HostCache[pid] = (now, host);
            }
            return host;
        }
        catch (Exception ex)
        {
            AppLog.Write("proctree: HostAppOf failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Host editor of the first running process named one of <paramref name="processNames"/>,
    /// resolved by walking its parent chain (<see cref="HostAppOf"/>). Used to tell
    /// which editor a hostless (file-derived) Codex session actually runs in — the
    /// Codex extension process is a child of its host editor (e.g. Cursor.exe).
    /// Null when none are running or none resolve to a known host.
    /// </summary>
    public static string? HostOfRunningProcess(params string[] processNames)
    {
        try
        {
            if (processNames is not { Length: > 0 }) return null;
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in processNames)
                if (!string.IsNullOrWhiteSpace(n)) wanted.Add(TrimExe(n));

            foreach (var p in Process.GetProcesses())
            {
                int pid; string name;
                try { name = p.ProcessName; pid = p.Id; }
                catch { p.Dispose(); continue; }
                p.Dispose();

                if (wanted.Contains(name) && HostAppOf(pid) is { } host)
                    return host;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("proctree: HostOfRunningProcess failed: " + ex.Message);
        }
        return null;
    }

    private static string? ResolveHost(int pid)
    {
        var processes = SnapshotProcesses();
        if (processes.Count == 0) return null;

        var seen = new HashSet<int>();
        int cur = pid;
        for (int depth = 0; depth < MaxDepth; depth++)
        {
            if (cur <= 0 || !seen.Add(cur)) break;                 // top of chain / pid-reuse cycle
            if (!processes.TryGetValue(cur, out var entry)) break; // exited between snapshot and walk
            if (HostOfExe(entry.ExeFile) is { } host) return host; // nearest ancestor wins
            cur = entry.ParentPid;
        }
        return null;
    }

    private static string? HostOfExe(string exeFile)
    {
        var name = TrimExe(exeFile);
        if (name.Equals("Cursor", StringComparison.OrdinalIgnoreCase)) return "cursor";
        if (name.Equals("Antigravity", StringComparison.OrdinalIgnoreCase)) return "antigravity";
        if (name.Equals("Code", StringComparison.OrdinalIgnoreCase)) return "vscode";
        return null;
    }

    // ------------------------------------------- private interop (Toolhelp) ----
    // Toolhelp32 gives pid + parent pid + exe name in one pass. Kept private
    // here per module rules (NativeMethods only gains members other modules
    // also need).

    private readonly record struct ProcEntry(int ParentPid, string ExeFile);

    /// <summary>pid -> (parent pid, exe file name) for every running process.</summary>
    private static Dictionary<int, ProcEntry> SnapshotProcesses()
    {
        var map = new Dictionary<int, ProcEntry>();
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == InvalidHandleValue)
        {
            AppLog.Write("proctree: CreateToolhelp32Snapshot failed");
            return map;
        }
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (Process32First(snap, ref entry))
            {
                do
                {
                    map[(int)entry.th32ProcessID] =
                        new ProcEntry((int)entry.th32ParentProcessID, entry.szExeFile ?? "");
                }
                while (Process32Next(snap, ref entry));
            }
        }
        finally
        {
            CloseHandle(snap);
        }
        return map;
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32FirstW")]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32NextW")]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
