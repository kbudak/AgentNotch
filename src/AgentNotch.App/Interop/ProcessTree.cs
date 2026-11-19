using System.Runtime.InteropServices;
using AgentNotch.App.Diagnostics;

namespace AgentNotch.App.Interop;

/// <summary>
/// Cheap process-level facts for agent and host detection.
///
/// Everything here is answered from ONE Toolhelp32 snapshot cached for
/// <c>SnapshotTtlMs</c> — never from System.Diagnostics.Process, whose
/// ProcessName getter re-queries the whole system per pid.
///
///  * <see cref="AnyRunning"/> / <see cref="IsAlive"/> — name and pid liveness,
///    a dictionary lookup each. Lets collectors skip file scans entirely when
///    the agent app isn't even running.
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

    /// <summary>
    /// True when at least one currently-running process has one of the given
    /// names (case-insensitive; a trailing ".exe" on a query name is tolerated).
    /// </summary>
    public static bool AnyRunning(params string[] processNames)
        => FirstPidNamed(processNames) > 0;

    /// <summary>
    /// Pid of the first running process named one of <paramref name="processNames"/>,
    /// or 0. One shared snapshot lookup — no per-name system scan.
    /// </summary>
    public static int FirstPidNamed(params string[] processNames)
    {
        try
        {
            if (processNames is not { Length: > 0 }) return 0;
            var byName = Current().FirstPidByName;
            foreach (var name in processNames)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (byName.TryGetValue(TrimExe(name), out int pid)) return pid;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("proctree: FirstPidNamed failed: " + ex.Message);
        }
        return 0;
    }

    /// <summary>Is this pid a live process? Snapshot lookup, no handle open.</summary>
    public static bool IsAlive(int pid)
    {
        try { return pid > 0 && Snapshot().ContainsKey(pid); }
        catch (Exception ex)
        {
            AppLog.Write("proctree: IsAlive failed: " + ex.Message);
            return true; // unknowable — don't drop a session on a transient failure
        }
    }

    /// <summary>"Cursor.exe" -> "Cursor" (Process.ProcessName carries no extension).</summary>
    public static string TrimExe(string name)
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
        int pid = FirstPidNamed(processNames);
        return pid > 0 ? HostAppOf(pid) : null;
    }

    /// <summary>
    /// Display name of the app hosting the first running process named one of
    /// <paramref name="processNames"/> — the chip text for hostless (file-derived)
    /// sessions such as Codex rollouts.
    /// </summary>
    public static string? HostLabelOfRunningProcess(params string[] processNames)
    {
        int pid = FirstPidNamed(processNames);
        return pid > 0 ? HostLabelOf(pid) : null;
    }

    private static readonly HashSet<string> TerminalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "sh", "bash", "mintty", "zsh", "fish",
        "powershell", "pwsh", "cmd", "conhost", "OpenConsole",
        "WindowsTerminal", "wt", "alacritty", "wezterm", "wezterm-gui", "warp", "tmux",
    };

    private static readonly HashSet<string> EditorNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Orca", "orca-terminal-daemon", "Code", "Code - Insiders",
        "Cursor", "Antigravity", "Windsurf", "devenv", "ChatGPT", "Codex",
    };

    /// <summary>Exe name (no extension) of a pid's parent, or null when orphaned/unknown.</summary>
    public static string? ParentProcessName(int pid)
    {
        try
        {
            var map = Snapshot();
            if (!map.TryGetValue(pid, out var self)) return null;
            if (!map.TryGetValue(self.ParentPid, out var parent)) return null; // dead/orphan parent
            return TrimExe(parent.ExeFile);
        }
        catch (Exception ex)
        {
            AppLog.Write("proctree: ParentProcessName failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Preference for collapsing same-project sessions: lower wins. An editor's
    /// integrated terminal is what the user looks at, so it beats a plain shell
    /// window. The EDITOR verdict walks the whole ancestor chain (Orca spawns
    /// claude ← cmd ← orca-terminal-daemon, so the immediate parent alone would
    /// lie and say "terminal"); the terminal verdict then falls back to the
    /// immediate parent. 0 = editor-hosted, 1 = orphaned/unknown, 2 = terminal.
    /// </summary>
    public static int SessionHostRank(int pid)
    {
        if (HostAppOf(pid) is not null) return 0;     // an editor is somewhere up the chain
        var parent = ParentProcessName(pid);
        if (parent is null) return 1;                 // orphaned — Orca-style integrated terminal
        if (EditorNames.Contains(parent)) return 0;
        if (TerminalNames.Contains(parent)) return 2; // a standalone terminal window
        return 1;
    }

    private static string? ResolveHost(int pid)
    {
        var processes = Snapshot();
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
        if (name.Equals("Orca", StringComparison.OrdinalIgnoreCase)) return "orca";
        // Orca's integrated terminal spawns sessions under its daemon (claude ← cmd ←
        // orca-terminal-daemon) — that chain is still "hosted by Orca".
        if (name.Equals("orca-terminal-daemon", StringComparison.OrdinalIgnoreCase)) return "orca";
        if (name.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase)) return "chatgpt";
        if (name.Equals("Codex", StringComparison.OrdinalIgnoreCase)) return "codex";
        return null;
    }

    // ------------------------------------------------------------ HostLabelOf ----

    /// <summary>
    /// Editors AND terminals, mapped to the name a human would use. Wider than
    /// <see cref="HostOfExe"/> — which only knows the apps we can jump to — because
    /// the row chip should still say "Windows Terminal" for a plain CLI session.
    /// </summary>
    private static readonly Dictionary<string, string> HostLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Cursor"] = "Cursor",
        ["Antigravity"] = "Antigravity",
        ["Orca"] = "Orca",
        ["orca-terminal-daemon"] = "Orca",
        ["Code"] = "VS Code",
        ["Code - Insiders"] = "VS Code",
        ["windsurf"] = "Windsurf",
        ["devenv"] = "Visual Studio",
        ["ChatGPT"] = "ChatGPT",
        ["Codex"] = "Codex",
        ["WindowsTerminal"] = "Windows Terminal",
        ["wt"] = "Windows Terminal",
        ["powershell"] = "PowerShell",
        ["pwsh"] = "PowerShell",
        ["cmd"] = "Command Prompt",
        ["conhost"] = "Console",
        ["OpenConsole"] = "Windows Terminal",
        ["alacritty"] = "Alacritty",
        ["wezterm"] = "WezTerm",
        ["wezterm-gui"] = "WezTerm",
        ["Hyper"] = "Hyper",
        ["Tabby"] = "Tabby",
        ["mintty"] = "Git Bash",
        ["bash"] = "Git Bash",
        ["warp"] = "Warp",
        ["claude"] = "Claude",
    };

    /// <summary>
    /// Human-readable name of the nearest app hosting <paramref name="pid"/>
    /// (editor or terminal), or null when nothing recognizable is in the chain.
    /// Cached with the same 30s per-pid policy as <see cref="HostAppOf"/>.
    /// </summary>
    public static string? HostLabelOf(int pid)
    {
        try
        {
            if (pid <= 0) return null;

            long now = Environment.TickCount64;
            lock (LabelGate)
            {
                if (LabelCache.TryGetValue(pid, out var hit) && now - hit.AtMs <= HostTtlMs)
                    return hit.Label;
            }

            string? label = null;
            var processes = Snapshot();
            if (processes.Count > 0)
            {
                var seen = new HashSet<int>();
                int cur = pid;
                for (int depth = 0; depth < MaxDepth; depth++)
                {
                    if (cur <= 0 || !seen.Add(cur)) break;
                    if (!processes.TryGetValue(cur, out var entry)) break;
                    // depth 0 is the session process itself (claude.exe) — its own
                    // name must not become the "hosted in" chip. And an editor
                    // anywhere up the chain outranks a shell wrapper below it
                    // (Orca spawns claude ← cmd ← orca-terminal-daemon: the chip
                    // must say Orca, not Command Prompt), so keep walking past
                    // terminal names until the chain ends.
                    var name = TrimExe(entry.ExeFile);
                    if (depth > 0)
                    {
                        if (EditorNames.Contains(name) && HostLabels.TryGetValue(name, out var editor))
                        {
                            label = editor;
                            break;
                        }
                        if (label is null && HostLabels.TryGetValue(name, out var found))
                            label = found; // remember the nearest terminal, keep looking for an editor
                    }
                    cur = entry.ParentPid;
                }
            }

            lock (LabelGate)
            {
                if (LabelCache.Count >= 256)
                {
                    foreach (var stale in LabelCache
                                 .Where(kv => now - kv.Value.AtMs > HostTtlMs)
                                 .Select(kv => kv.Key)
                                 .ToList())
                        LabelCache.Remove(stale);
                }
                LabelCache[pid] = (now, label);
            }
            return label;
        }
        catch (Exception ex)
        {
            AppLog.Write("proctree: HostLabelOf failed: " + ex.Message);
            return null;
        }
    }

    private static readonly object LabelGate = new();
    private static readonly Dictionary<int, (long AtMs, string? Label)> LabelCache = new();

    // ------------------------------------------- private interop (Toolhelp) ----
    // Toolhelp32 gives pid + parent pid + exe name in one pass. Kept private
    // here per module rules (NativeMethods only gains members other modules
    // also need).

    public readonly record struct ProcEntry(int ParentPid, string ExeFile);

    private sealed record ProcSnapshot(
        Dictionary<int, ProcEntry> ByPid,
        Dictionary<string, int> FirstPidByName);

    private static readonly ProcSnapshot EmptySnapshot = new(new(), new(StringComparer.OrdinalIgnoreCase));

    // One Toolhelp pass answers every question this class asks, so the whole
    // module shares a single short-lived snapshot: a poll tick resolves host,
    // label, rank and liveness for every session off ONE syscall instead of one
    // per lookup. The TTL is well under the 1s emit floor, so a session that
    // starts or dies is still picked up by the next rebuild.
    private const long SnapshotTtlMs = 750;
    private static readonly object SnapshotGate = new();
    private static ProcSnapshot? _snapshot;
    private static long _snapshotAtMs;

    /// <summary>pid -> (parent pid, exe file name) for every running process.</summary>
    public static IReadOnlyDictionary<int, ProcEntry> Snapshot() => Current().ByPid;

    private static ProcSnapshot Current()
    {
        lock (SnapshotGate)
        {
            long now = Environment.TickCount64;
            if (_snapshot is not null && now - _snapshotAtMs <= SnapshotTtlMs)
                return _snapshot;

            var byPid = SnapshotProcesses();
            if (byPid.Count == 0) return _snapshot ?? EmptySnapshot; // snapshot failed — keep the last good one

            var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (pid, entry) in byPid)
                byName.TryAdd(TrimExe(entry.ExeFile), pid);

            _snapshot = new ProcSnapshot(byPid, byName);
            _snapshotAtMs = now;
            return _snapshot;
        }
    }

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

    // --------------------------------------------------- GetCommandLine ----

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ProcessCommandLineInformation = 60;

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        IntPtr processInformation,
        uint processInformationLength,
        out uint returnLength);

    /// <summary>
    /// Reads the command line of a same-user process via ProcessCommandLineInformation (Win8.1+).
    /// Used to extract --csrf_token from a running language_server.exe when its startup log
    /// has grown past the tail window. Never throws; returns null on any failure.
    /// </summary>
    public static string? GetCommandLine(int pid)
    {
        if (pid <= 0) return null;
        IntPtr hProc = IntPtr.Zero;
        try
        {
            hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (hProc == IntPtr.Zero) return null;

            int headerSize = Marshal.SizeOf<UNICODE_STRING>();
            _ = NtQueryInformationProcess(hProc, ProcessCommandLineInformation, IntPtr.Zero, 0, out uint needed);
            if (needed < (uint)headerSize || needed > 64 * 1024) needed = 8192;

            IntPtr buf = Marshal.AllocHGlobal((int)needed);
            try
            {
                int status = NtQueryInformationProcess(hProc, ProcessCommandLineInformation, buf, needed, out _);
                if (status != 0) return null;

                var ustr = Marshal.PtrToStructure<UNICODE_STRING>(buf);
                if (ustr.Buffer == IntPtr.Zero || ustr.Length == 0 || (ustr.Length & 1) != 0) return null;

                long bufStart = buf.ToInt64();
                long strStart = ustr.Buffer.ToInt64();
                if (strStart < bufStart + headerSize || strStart + ustr.Length > bufStart + needed)
                    return null;

                return Marshal.PtrToStringUni(ustr.Buffer, ustr.Length / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("proctree: GetCommandLine failed: " + ex.Message);
            return null;
        }
        finally
        {
            if (hProc != IntPtr.Zero) CloseHandle(hProc);
        }
    }
}
