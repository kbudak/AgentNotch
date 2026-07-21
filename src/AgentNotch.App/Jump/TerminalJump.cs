using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AgentNotch.App.Diagnostics;
using AgentNotch.App.Interop;
using AgentNotch.App.Sessions;

namespace AgentNotch.App.Jump;

/// <summary>
/// "Jump to session": bring the window that actually hosts an agent session to
/// the foreground. Ported from the Electron prototype's focusSession() but done
/// entirely in-process with P/Invoke (no PowerShell child process).
///
/// Strategy per entrypoint:
///  * "cli"            — AttachConsole(pid) → GetConsoleWindow() → root owner.
///                       A ConPTY-hosted CLI has no top-level window of its own;
///                       its console window's root owner is the real terminal
///                       (Windows Terminal, conhost, …). If attaching fails,
///                       walk UP the parent-process chain (WindowsTerminal, wt,
///                       powershell, pwsh, cmd, alacritty, wezterm, …) to the
///                       first ancestor owning a visible main window.
///  * "claude-desktop" — visible top-level window of a "Claude" process.
///  * "claude-vscode"  — visible "Code"/"Cursor" window whose title contains
///                       the session cwd's folder name; else any such window.
///
/// Non-claude agents carry no pid, so they are matched purely by window:
///  * codex       — an editor (Cursor/Code/Antigravity) window titled for the
///                  project, else the Codex/ChatGPT desktop app, else a
///                  terminal window titled for the project.
///  * antigravity — a visible Antigravity window (project title preferred).
/// A claude session with a known Host targets that host app's window first and
/// only then falls back to the entrypoint strategy above.
///
/// Never throws — every failure is logged and reported as false.
/// </summary>
public static class TerminalJump
{
    /// <summary>Focus the window hosting <paramref name="session"/>. True if a window was activated.</summary>
    public static bool FocusSession(AgentSession session)
    {
        try
        {
            if (session is null)
            {
                AppLog.Write("jump: null session");
                return false;
            }

            switch (session.Agent)
            {
                case "codex":
                    return FocusCodex(session);
                case "antigravity":
                    return FocusAntigravity(session);
            }

            // claude (and unknown agents): a session known to live inside a
            // host app IS that app's window — try it first; on failure fall
            // back to the entrypoint-based strategy unchanged.
            if (session.Host is { Length: > 0 } host && FocusHostApp(host, session))
                return true;

            switch (session.Entrypoint)
            {
                case "claude-desktop":
                    return FocusClaudeDesktop();
                case "claude-vscode":
                    return FocusVsCode(session);
                default: // "cli" and anything unknown that still carries a pid
                    return FocusCli(session);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("jump: focus failed: " + ex);
            return false;
        }
    }

    // ---------------------------------------------------------------- cli ----

    private static bool FocusCli(AgentSession session)
    {
        if (session.Pid <= 0)
        {
            AppLog.Write($"jump: cli session {session.SessionId} has no pid");
            return false;
        }

        IntPtr hwnd = ResolveConsoleWindow((uint)session.Pid);
        if (!Usable(hwnd))
            hwnd = ResolveWindowedAncestor(session.Pid);

        // Integrated terminals (Orca, VS Code, …) host the CLI in a ConPTY: its
        // console window is a hidden pseudoconsole and the parent chain is often
        // orphaned, so both lookups above miss. Fall back to the running editor's
        // own window — that's what the user actually wants raised.
        if (!Usable(hwnd))
        {
            if (FocusRunningEditorHost(session)) return true;
            AppLog.Write($"jump: no hosting window found for cli pid={session.Pid}");
            return false;
        }
        return Activate(hwnd);
    }

    private static bool Usable(IntPtr hwnd)
        => hwnd != IntPtr.Zero && NativeMethods.IsWindowVisible(hwnd);

    private static bool IsEditorHostProcess(string name) =>
        name.Equals("Orca", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Code", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Code - Insiders", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Cursor", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Antigravity", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Windsurf", StringComparison.OrdinalIgnoreCase);

    /// <summary>Raise a running editor's window (project title preferred), else any.</summary>
    private static bool FocusRunningEditorHost(AgentSession session)
    {
        IntPtr hwnd = FindProcessWindowPreferTitle(IsEditorHostProcess, FolderOf(session));
        return hwnd != IntPtr.Zero && Activate(hwnd);
    }

    /// <summary>
    /// Attach to the target's console and ask for its window; the root owner of
    /// that window is the hosting terminal. AttachConsole fails if we already
    /// have a console (ERROR_ACCESS_DENIED), so detach first — and ALWAYS
    /// detach in finally so we never hold a foreign console open.
    /// </summary>
    private static IntPtr ResolveConsoleWindow(uint pid)
    {
        try
        {
            NativeMethods.FreeConsole(); // no-op if we have no console; required if we do

            if (!NativeMethods.AttachConsole(pid))
                return IntPtr.Zero; // GUI process / access denied — caller falls back to the parent walk

            IntPtr console = NativeMethods.GetConsoleWindow();
            if (console == IntPtr.Zero)
                return IntPtr.Zero;

            IntPtr root = NativeMethods.GetAncestor(console, NativeMethods.GA_ROOTOWNER);
            return root != IntPtr.Zero ? root : console;
        }
        catch (Exception ex)
        {
            AppLog.Write("jump: AttachConsole path failed: " + ex.Message);
            return IntPtr.Zero;
        }
        finally
        {
            NativeMethods.FreeConsole();
        }
    }

    /// <summary>
    /// Fallback: climb the parent-process chain from the session pid to the
    /// first ancestor that owns a visible main window — that's the hosting
    /// terminal. explorer.exe is excluded: it's a common ancestor of anything
    /// launched from the shell and owns the desktop, never the terminal.
    /// </summary>
    private static IntPtr ResolveWindowedAncestor(int pid)
    {
        var parents = SnapshotParents();
        var seen = new HashSet<int>();
        int cur = pid;
        for (int depth = 0; depth < 24; depth++)
        {
            if (cur <= 0 || !seen.Add(cur))
                break;

            IntPtr hwnd = VisibleMainWindow(cur);
            if (hwnd != IntPtr.Zero)
                return hwnd;

            if (!parents.TryGetValue(cur, out int parent))
                break;
            cur = parent;
        }
        return IntPtr.Zero;
    }

    private static IntPtr VisibleMainWindow(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (string.Equals(p.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase))
                return IntPtr.Zero;
            IntPtr h = p.MainWindowHandle;
            return h != IntPtr.Zero && NativeMethods.IsWindowVisible(h) ? h : IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero; // exited / access denied — keep climbing
        }
    }

    // ---------------------------------------------------- desktop / vscode ----

    private static bool FocusClaudeDesktop()
    {
        IntPtr hwnd = FindProcessWindow(
            name => name.Equals("claude", StringComparison.OrdinalIgnoreCase),
            titleMatches: null);
        if (hwnd == IntPtr.Zero)
        {
            AppLog.Write("jump: no visible Claude desktop window");
            return false;
        }
        return Activate(hwnd);
    }

    private static bool FocusVsCode(AgentSession session)
    {
        static bool IsEditor(string name) =>
            name.Equals("Code", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Cursor", StringComparison.OrdinalIgnoreCase);

        string folder = FolderOf(session);
        IntPtr hwnd = FindProcessWindowPreferTitle(IsEditor, folder);
        if (hwnd == IntPtr.Zero)
        {
            AppLog.Write($"jump: no visible Code/Cursor window (folder='{folder}')");
            return false;
        }
        return Activate(hwnd);
    }

    // ------------------------------------------------- codex / antigravity ----

    /// <summary>
    /// Codex sessions have no pid, so match purely by window: (1) an editor
    /// (Cursor / Code / Antigravity) window titled for the project — Codex
    /// usually runs as an IDE extension or in an integrated terminal; (2) the
    /// Codex / ChatGPT desktop app; (3) a terminal window titled for the project.
    /// </summary>
    private static bool FocusCodex(AgentSession session)
    {
        static bool IsEditorHost(string name) =>
            name.Equals("Cursor", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Code", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Antigravity", StringComparison.OrdinalIgnoreCase);

        static bool IsCodexApp(string name) =>
            name.Equals("Codex", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase);

        string folder = FolderOf(session);

        IntPtr hwnd = IntPtr.Zero;
        if (!string.IsNullOrEmpty(folder))
            hwnd = FindProcessWindow(IsEditorHost,
                title => title.Contains(folder, StringComparison.OrdinalIgnoreCase));

        if (hwnd == IntPtr.Zero)
            hwnd = FindProcessWindow(IsCodexApp, titleMatches: null);

        if (hwnd == IntPtr.Zero && !string.IsNullOrEmpty(folder))
            hwnd = FindProcessWindow(IsTerminalProcess,
                title => title.Contains(folder, StringComparison.OrdinalIgnoreCase));

        if (hwnd == IntPtr.Zero)
        {
            AppLog.Write($"jump: no window found for codex session (folder='{folder}')");
            return false;
        }
        return Activate(hwnd);
    }

    /// <summary>Visible Antigravity window, preferring one titled for the project.</summary>
    private static bool FocusAntigravity(AgentSession session)
    {
        static bool IsAntigravity(string name) =>
            name.Equals("Antigravity", StringComparison.OrdinalIgnoreCase);

        string folder = FolderOf(session);
        IntPtr hwnd = FindProcessWindowPreferTitle(IsAntigravity, folder);
        if (hwnd == IntPtr.Zero)
        {
            AppLog.Write($"jump: no visible Antigravity window (folder='{folder}')");
            return false;
        }
        return Activate(hwnd);
    }

    /// <summary>
    /// Window of the app hosting the session ("cursor" | "antigravity" |
    /// "vscode"), preferring one titled for the project. False when the host is
    /// unknown or no window was activated — the caller then falls back to the
    /// entrypoint-based strategy.
    /// </summary>
    private static bool FocusHostApp(string host, AgentSession session)
    {
        string? process = host.ToLowerInvariant() switch
        {
            "cursor" => "Cursor",
            "antigravity" => "Antigravity",
            "vscode" => "Code",
            "orca" => "Orca",
            _ => null,
        };
        if (process is null)
        {
            AppLog.Write($"jump: unknown host '{host}'");
            return false;
        }

        IntPtr hwnd = FindProcessWindowPreferTitle(
            name => name.Equals(process, StringComparison.OrdinalIgnoreCase),
            FolderOf(session));
        if (hwnd == IntPtr.Zero)
        {
            AppLog.Write($"jump: no visible {process} window for host '{host}'");
            return false;
        }
        return Activate(hwnd);
    }

    private static bool IsTerminalProcess(string name) =>
        name.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("powershell", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("pwsh", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("cmd", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("alacritty", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("wezterm", StringComparison.OrdinalIgnoreCase);

    /// <summary>Project folder used for title matching (ProjectName, else last cwd segment).</summary>
    private static string FolderOf(AgentSession session)
    {
        string folder = session.ProjectName;
        if (string.IsNullOrEmpty(folder) && !string.IsNullOrEmpty(session.Cwd))
            folder = Path.GetFileName(session.Cwd.TrimEnd('\\', '/'));
        return folder;
    }

    /// <summary>
    /// Window of a matching process whose title contains <paramref name="folder"/>;
    /// else any visible window of that process.
    /// </summary>
    private static IntPtr FindProcessWindowPreferTitle(Func<string, bool> processMatches, string folder)
    {
        IntPtr hwnd = IntPtr.Zero;
        if (!string.IsNullOrEmpty(folder))
            hwnd = FindProcessWindow(processMatches,
                title => title.Contains(folder, StringComparison.OrdinalIgnoreCase));
        if (hwnd == IntPtr.Zero)
            hwnd = FindProcessWindow(processMatches, titleMatches: null);
        return hwnd;
    }

    /// <summary>
    /// First visible top-level window whose owning process name matches — and,
    /// when a title predicate is given, whose title matches too.
    /// </summary>
    private static IntPtr FindProcessWindow(Func<string, bool> processMatches, Func<string, bool>? titleMatches)
    {
        IntPtr found = IntPtr.Zero;
        var nameCache = new Dictionary<uint, string>();

        // EnumWindows is synchronous, so the callback delegate stays alive for
        // the duration of the call — no explicit pinning needed.
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!NativeMethods.IsWindowVisible(hwnd))
                    return true;

                NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0)
                    return true;

                if (!nameCache.TryGetValue(pid, out string? name))
                    nameCache[pid] = name = ProcessNameOf((int)pid);
                if (name.Length == 0 || !processMatches(name))
                    return true;

                if (titleMatches is not null && !titleMatches(WindowTitle(hwnd)))
                    return true;

                found = hwnd;
                return false; // stop enumerating
            }
            catch
            {
                return true; // a single bad window must not abort the scan
            }
        }, IntPtr.Zero);

        return found;
    }

    private static string ProcessNameOf(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch
        {
            return "";
        }
    }

    private static string WindowTitle(IntPtr hwnd)
    {
        int len = NativeMethods.GetWindowTextLength(hwnd);
        if (len <= 0)
            return "";
        var sb = new StringBuilder(len + 1);
        NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    // ----------------------------------------------------------- activate ----

    /// <summary>
    /// Restore + raise + focus. SetForegroundWindow is subject to the
    /// foreground lock, so briefly attach our input queue to the current
    /// foreground thread's (documented same-integrity workaround), and detach
    /// in finally no matter what.
    /// </summary>
    private static bool Activate(IntPtr hwnd)
    {
        try
        {
            IntPtr root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOTOWNER);
            if (root != IntPtr.Zero)
                hwnd = root;

            if (NativeMethods.IsIconic(hwnd))
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);

            uint me = GetCurrentThreadId();
            IntPtr fg = NativeMethods.GetForegroundWindow();
            uint fgThread = fg != IntPtr.Zero
                ? NativeMethods.GetWindowThreadProcessId(fg, out _)
                : 0;

            bool attached = false;
            try
            {
                if (fgThread != 0 && fgThread != me)
                    attached = NativeMethods.AttachThreadInput(me, fgThread, true);

                NativeMethods.BringWindowToTop(hwnd);
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);
                bool ok = NativeMethods.SetForegroundWindow(hwnd);

                // SetForegroundWindow can report false yet still have worked
                // (and vice versa is rare) — trust the actual foreground state.
                if (!ok)
                    ok = NativeMethods.GetForegroundWindow() == hwnd;
                if (!ok)
                    AppLog.Write("jump: SetForegroundWindow did not take");
                return ok;
            }
            finally
            {
                if (attached)
                    NativeMethods.AttachThreadInput(me, fgThread, false);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("jump: activate failed: " + ex.Message);
            return false;
        }
    }

    // ------------------------------------------- private interop (Toolhelp) ----
    // Parent-chain snapshot via Toolhelp32 — works at standard user privilege
    // and without WMI. Kept private here per module rules (NativeMethods only
    // gains members that other modules also need).

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

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    /// <summary>pid → parent pid for every running process.</summary>
    private static Dictionary<int, int> SnapshotParents()
    {
        var map = new Dictionary<int, int>();
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == InvalidHandleValue)
        {
            AppLog.Write("jump: CreateToolhelp32Snapshot failed");
            return map;
        }
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (Process32First(snap, ref entry))
            {
                do
                {
                    map[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
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
}
