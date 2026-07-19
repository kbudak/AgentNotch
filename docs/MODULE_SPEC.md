# AgentNotch — Module Contracts (source of truth for all module authors)

Windows "notch" monitor for AI coding agents, VibeIsland-style. .NET 8, WPF.
Integration is hooks + session-file watching ONLY — no OCR, no screen capture,
no injection, no drivers, standard user privilege.

Solution layout (already scaffolded):

```
AgentNotch/
  src/AgentNotch.Protocol/    Protocol.cs          (DONE — do not modify)
  src/AgentNotch.HookHelper/  Program.cs           (DONE — do not modify)
  src/AgentNotch.App/
    app.manifest, AgentNotch.App.csproj            (DONE)
    Interop/NativeMethods.cs                       (DONE — use, extend only if needed)
    Diagnostics/AppLog.cs                          (DONE)
    App.xaml / App.xaml.cs                         (composition root — DONE, do not modify)
    Ipc/          → owner: Agent A
    Integration/  → owner: Agent B
    Sessions/     → owner: Agent C
    Jump/         → owner: Agent D
    UI/, ViewModels/ → owner: Agent E
  tests/AgentNotch.Tests/                          (csproj DONE; each agent adds its own test file)
```

**Rules for every agent**
- Touch ONLY your assigned files. Shared types you must reference are specified below — assume they exist exactly as written.
- Namespaces: `AgentNotch.App.<Folder>`; tests `AgentNotch.Tests`.
- No `Console.*` in the app; log via `AgentNotch.App.Diagnostics.AppLog.Write(string)`.
- Nullable enabled; no external NuGet packages beyond what the csproj already has.
- Threading: PipeServer/SessionWatcher raise events on background threads. The
  composition root marshals to the UI thread — your events may fire on any thread.
- All background loops must swallow + log exceptions; the app must never crash
  from a malformed JSON line or a locked file.

## Wire protocol (AgentNotch.Protocol — final)

Named pipe `\\.\pipe\agentnotch`, NDJSON. Types: `PipeEnvelope` (helper→app:
`{v, id, kind:"hook", mode, expectsReply, event}` where `event` is the raw hook
stdin JSON), `PipeDecision`/`DecisionBody` (app→helper: `{v, id, kind:"decision",
decision:{behavior, message?, updatedInput?, answers?}}`).
Behaviors: `allow` | `deny` | `pass` | `answer`. `pass` = helper prints nothing →
Claude Code's normal terminal flow. Serialize with `Proto.Json` (camelCase).
Modes: `permission-request` (blocking), `ask-user-question` (blocking),
`notify`, `stop`, `prompt` (observe-only, no reply expected).

## Hook event JSON shapes (from official docs, snake_case)

Common input fields: `session_id`, `transcript_path`, `cwd`, `permission_mode`,
`hook_event_name`, and for tool events `tool_name`, `tool_input`.

- PermissionRequest adds `permission_request: {type, tool, suggestions[]}` (may be absent — tolerate).
- Notification adds `message` (string).
- Stop adds `last_assistant_message`.
- UserPromptSubmit adds `prompt`.

Tool inputs we render specially:
- `Edit`: `{file_path, old_string, new_string, replace_all?}`
- `Write`: `{file_path, content}`
- `Bash`: `{command, description?}`
- `AskUserQuestion`: `{questions: [{question, header, options: [{label, description?}], multiSelect}]}`
Everything else: generic fallback (tool name + compact JSON of input).

---

## Agent A — IPC (`Ipc/PipeServer.cs`, `Ipc/HookRequest.cs`, test `PipeServerTests.cs`)

```csharp
namespace AgentNotch.App.Ipc;

public sealed class PipeServer : IDisposable
{
    public event Action<HookRequest>? RequestReceived; // any thread
    public void Start(CancellationToken ct);           // returns immediately; accept loop on threadpool
    public void Dispose();
}

public sealed class HookRequest
{
    public string Id { get; }              // envelope id
    public string Mode { get; }            // Proto.Mode*
    public bool ExpectsReply { get; }
    public JsonElement Event { get; }      // raw hook JSON (clone, safe to hold)
    // Convenience accessors parsed from Event ("" when absent):
    public string HookEventName { get; }
    public string ToolName { get; }
    public string SessionId { get; }
    public string Cwd { get; }
    public string PermissionMode { get; }
    public JsonElement? ToolInput { get; }
    public bool IsResolved { get; }
    public event Action? Abandoned;        // client vanished before a reply — UI should dismiss
    public Task RespondAsync(DecisionBody decision); // idempotent; no-throw; no-op if !ExpectsReply/resolved/abandoned
}
```

Implementation requirements:
- `NamedPipeServerStream(Proto.PipeName, InOut, MaxAllowedServerInstances, Byte, Asynchronous)`;
  accept loop: on connect, hand the connection to a per-connection task and
  immediately create the next server stream (many helpers connect concurrently).
- Read lines (UTF-8, `\n`); parse `PipeEnvelope` with `Proto.Json`; malformed → log + close.
- One envelope per connection is the norm; after reply (or observe), close.
- Detect client disconnect while pending → mark abandoned, raise `Abandoned`, make RespondAsync a no-op.
- `RespondAsync` writes one `PipeDecision` line + flush; swallow/log IO errors (client may have timed out).
- Test (no UI): start server on a unique pipe name (make pipe name ctor-injectable with default `Proto.PipeName`),
  connect with `NamedPipeClientStream`, send a permission-request envelope, assert `RequestReceived`
  fires with parsed fields, respond, assert the client reads the correlated decision line.
  Also test: observe envelope (no reply) and malformed line don't crash the server.

## Agent B — Installer (`Integration/ClaudeCodeInstaller.cs`, test `InstallerTests.cs`)

```csharp
namespace AgentNotch.App.Integration;

public enum InstallState { NotInstalled, Installed, Partial }

public static class ClaudeCodeInstaller
{
    // settingsPath parameter (default null → %USERPROFILE%\.claude\settings.json) for testability.
    public static InstallState GetState(string? settingsPath = null);
    public static void Install(string helperExePath, string? settingsPath = null);
    public static void Uninstall(string? settingsPath = null);
}
```

Merges these entries into settings.json `hooks` (System.Text.Json.Nodes; PRESERVE
all unknown properties and existing hooks; never reorder/rewrite what's there):

- `PermissionRequest`: `{matcher:"*", hooks:[{type:"command", command:"<helperExePath>", args:["permission-request"], timeout:600}]}`
- `PreToolUse`: `{matcher:"AskUserQuestion", hooks:[{type:"command", command:"<helperExePath>", args:["ask-user-question"], timeout:600}]}`
- `Notification`: `{hooks:[{type:"command", command:"<helperExePath>", args:["notify"], timeout:10}]}` (no matcher)
- `Stop`: `{hooks:[{type:"command", command:"<helperExePath>", args:["stop"], timeout:10}]}`
- `UserPromptSubmit`: `{hooks:[{type:"command", command:"<helperExePath>", args:["prompt"], timeout:10}]}`

Rules: identify OUR entries by the command path containing `agentnotch-hook`
(case-insensitive) — Install is idempotent (update path in place if it moved,
no duplicates); Uninstall removes ONLY those entries and prunes empty
matcher-groups/arrays it emptied, leaving everything else byte-for-byte
equivalent JSON-wise. Before the FIRST write of a run: back up to
`settings.json.agentnotch-backup-yyyyMMdd-HHmmss` next to it. Write atomically
(temp file + `File.Replace`/move). Missing file/dir → create `{ "hooks": {...} }`.
Malformed existing JSON → throw `InvalidOperationException` (do NOT clobber).
Tests: fresh install into temp dir; idempotency (double install = single entries);
merge preserves foreign hooks + unknown top-level props; uninstall restores
foreign-only content; malformed JSON throws; backup file created.

## Agent C — Sessions (`Sessions/AgentSession.cs`, `Sessions/SessionWatcher.cs`, `Sessions/TranscriptTail.cs`, test `SessionParsingTests.cs`)

PORT the proven logic from the Electron app at repo root: read
`collectors/tasks.js` (status derivation, turn ownership, spend counting) and
translate it. Data sources:
1. Registry `%USERPROFILE%\.claude\sessions\<pid>.json`:
   `{pid, sessionId, cwd, entrypoint("cli"|"claude-desktop"|"claude-vscode"), status?("busy"|"waiting"|"idle"), statusUpdatedAt?, updatedAt}` — status only for cli.
   Liveness: `Process.GetProcessById` inside try/catch (dead → skip file).
2. Transcript `%USERPROFILE%\.claude\projects\<encoded>\<sessionId>.jsonl`:
   tail last 256 KB, parse per-line JSON, tolerate partial first line.

```csharp
namespace AgentNotch.App.Sessions;

public enum SessionStatus { Working, NeedsYou, Finished }

public sealed class AgentSession
{
    public string SessionId, Cwd, ProjectName, Entrypoint; // ProjectName = last cwd segment
    public int Pid;
    public SessionStatus Status;
    public string? CurrentTool;    // e.g. "Bash", "Edit"
    public string? CurrentTarget;  // file path or command excerpt (≤60 chars)
    public string? LastMessage;    // last assistant text, first line, ≤120 chars
    public long TokensSpent;       // cumulative input+cacheCreation+output this session
    public DateTime LastActivityUtc;
}

public sealed class SessionWatcher : IDisposable
{
    public SessionWatcher(string? claudeHome = null);        // default %USERPROFILE%\.claude
    public event Action<IReadOnlyList<AgentSession>>? SessionsChanged; // any thread, debounced ≥1s apart
    public void Start();  // FileSystemWatcher on sessions/ + projects/ (+ 5s fallback poll timer)
    public void Dispose();
}
```

Status derivation (translate from tasks.js `deriveStatus` — keep semantics):
registry busy/waiting fresh (<20 min) → Working; else last transcript event:
pending tool_use or user message → Working if recent else NeedsYou; assistant
end_turn → NeedsYou if it ends with a question ("?" near end / AskUserQuestion),
else Finished. Drop non-Working sessions idle >10 min. Dedup per cwd keeping most
recently active. Sort: NeedsYou, Finished, Working. Incremental token counting
with per-file byte offsets (skip lines without `"output_tokens"`).
`TranscriptTail` = static pure functions (tail read + event extraction + spend) so
tests can run on temp fixture files; put ALL parsing there, watcher just orchestrates.
Tests: fixture JSONL → status for the working/needs-you/finished cases, spend sum,
partial-line tolerance.

## Agent D — Jump (`Jump/TerminalJump.cs`)

```csharp
namespace AgentNotch.App.Jump;

public static class TerminalJump
{
    public static bool FocusSession(AgentSession session); // true if a window was activated
}
```

Port from Electron `main.js` `focusSession`, but in-process P/Invoke (NO PowerShell):
- entrypoint "cli": `AttachConsole(pid)` → `GetConsoleWindow()` → `GetAncestor(GA_ROOTOWNER)`
  → activate; ALWAYS `FreeConsole()` in finally. If AttachConsole fails, walk up the
  parent-process chain (WindowsTerminal, wt, powershell, pwsh, cmd, alacritty, wezterm)
  and use the first ancestor with a visible MainWindowHandle.
- "claude-desktop": visible top-level window of a "Claude"/"claude" process (EnumWindows).
- "claude-vscode": visible "Code"/"Cursor" window whose title contains the cwd's folder name; else any.
- Activate = `ShowWindow(SW_RESTORE if IsIconic)` + AttachThreadInput(foreground thread)
  + `BringWindowToTop` + `SetForegroundWindow`, detach in finally. Use `NativeMethods`.
  Never throw; log + return false.

## Agent E — UI + ViewModels (owns `UI/*`, `ViewModels/*`)

Files: `UI/Theme.xaml`, `UI/OverlayWindow.xaml(.cs)`, `UI/PermissionPromptView.xaml(.cs)`,
`UI/QuestionPromptView.xaml(.cs)`, `UI/SessionsView.xaml(.cs)`, `UI/DiffRenderer.cs`,
`UI/HotkeyManager.cs`, `UI/Converters.cs`, `ViewModels/MainViewModel.cs`,
`ViewModels/PromptViewModel.cs`.

### Composition contract (App.xaml.cs already calls exactly this — must compile against it)

```csharp
namespace AgentNotch.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    public MainViewModel();
    public ObservableCollection<AgentSession> Sessions { get; }
    public PromptViewModel? ActivePrompt { get; }   // head of FIFO queue, null if none
    public int QueueCount { get; }                  // pending prompts incl. active
    public bool IsExpanded { get; }                 // true while hover-held or ActivePrompt != null
    public string CollapsedText { get; }            // "idle" | "2 working" | "1 needs you · 1 working" …
    public string StateKind { get; }                // "idle"|"working"|"needsyou"|"prompt" (drives pill dot color)
    public void AddHookRequest(HookRequest request);            // UI thread
    public void UpdateSessions(IReadOnlyList<AgentSession> s);  // UI thread
    public void SetHover(bool over);                            // UI thread (expand on hover, collapse ~250ms after leave unless prompt pending)
    // Resolution API (UI thread; also used by HotkeyManager):
    public void AllowActive();                       // permission → allow
    public void DenyActive();                        // permission → deny
    public void PassActive();                        // any prompt → pass (hand back to terminal)
    public void ChooseOption(int index);             // question → answer with option #index (0-based)
}
```

`AddHookRequest`: modes permission-request / ask-user-question → build a
`PromptViewModel`, enqueue, hook its `Abandoned` → auto-remove. Observe modes:
use them to nudge freshness (e.g. Notification "permission_prompt"): no UI card.
Resolution calls `request.RespondAsync(new DecisionBody{...})` (fire-and-forget
with logging), dequeues, moves to the next queued prompt.
For questions: answer = `answers:[{question, labels:[chosenLabel]}]` (single-select
MVP; multiSelect renders but first click resolves).

`PromptViewModel` (see fields): Kind Permission|Question, ToolName, Title
("Edit src\auth\middleware.ts" — file path relative to cwd when under it;
Bash → the command first line), Subtitle (project folder · permission_mode),
DiffLines (`IReadOnlyList<DiffLine>`), DiffStats ("+3 -1"), CommandText,
GenericJson fallback, QuestionText/Header, Options (`IReadOnlyList<QuestionOption>`:
Index, Label, Description, HotkeyText "Ctrl+1"…), MultiSelect.
`DiffRenderer.Render(old, new)` → simple LCS line diff with context collapsing
(hunks, ≤200 rendered lines, "… N more lines" trailer); `Write` → all-added (cap).
`record DiffLine(string Kind /*add|del|ctx|gap*/, string? OldNo, string? NewNo, string Text)`.

### HotkeyManager
`public sealed class HotkeyManager : IDisposable { public HotkeyManager(Window host, MainViewModel vm); }`
— hooks HwndSource WndProc; registers globally ONLY while `vm.ActivePrompt != null`
(watch PropertyChanged): permission → Ctrl+Y (allow), Ctrl+N (deny);
question → Ctrl+1..Ctrl+9 (options), Ctrl+N (pass). Unregister immediately when
the queue empties (never hold hotkeys while idle). `MOD_NOREPEAT`.

### OverlayWindow
Frameless, `AllowsTransparency`, `Background=Transparent`, `Topmost`,
`ShowInTaskbar=false`, `ShowActivated=false`, `SizeToContent=WidthAndHeight`,
never focusable — call `NativeMethods.MakeOverlay(hwnd)` in `OnSourceInitialized`
and set `Focusable=false`. Position: top-center of PRIMARY screen, y=0, flush to
the top edge (recenter on size change + `SystemEvents.DisplaySettingsChanged`).
MouseEnter/Leave → `vm.SetHover`. Content = notch panel:

- **Collapsed** (always visible): pill 34px high, ~240px wide: status dot
  (StateKind: idle=#6B7280, working=#4ADE80 with soft opacity pulse,
  needsyou/prompt=#F59E0B pulse) + `CollapsedText` + tiny "✦" glyph right.
- **Expanded** (IsExpanded): width 560: if ActivePrompt → prompt card
  (PermissionPromptView or QuestionPromptView by Kind) + queue badge
  ("1 of 3") when QueueCount>1; below it always SessionsView. Smooth
  expand/collapse (150-200ms ease-out on an animated MaxHeight/Opacity —
  simple is fine, no flicker).

### VibeIsland design tokens (Theme.xaml — SolidColorBrush/Style resources)
Panel #0B0B0E @97%, bottom-corner radius 18 (top corners 0 — attached to bezel),
border #FFFFFF14 1px, drop shadow black 40% blur 24. Card bg #131316 r10,
hover #1D1D22. Text #F4F4F5 / #9CA3AF / #6B7280; fonts Segoe UI (12-13px UI),
Cascadia Mono/Consolas (12px code). Accents: amber #F59E0B (permission header
"● Permission Request" + tool line "⚠ Edit path"), teal #2DD4BF ("💬 Claude asks"),
green #4ADE80, red #F87171. Diff: line-number gutter #6B7280, add fg #4ADE80
bg #1F4ADE80(hex w/ alpha 0x1F), del fg #F87171 bg alpha 0x1F, mono, no wrap,
horizontal scroll, max diff height 260 w/ vertical scroll; stats "+3 -1" colored.
Buttons row right-aligned: Deny = bg #26262B fg #F4F4F5, Allow = bg #FAFAFA
fg #0A0A0A (both r8, padding 14x7, kbd chip inside: bg #FFFFFF12 border
#FFFFFF1F r4 10px mono "Ctrl+N"/"Ctrl+Y"). Question options: full-width rows
bg #18181B hover #27272A r8, kbd chip "Ctrl+1" left + Label; Description
smaller gray under label. Sessions rows: dot by status (green pulse/amber/gray)
+ "claude — {ProjectName}" 13px + gray mono subtitle (CurrentTool(Target) or
LastMessage) + right: TokensSpent short-form ("48.2k") gray + "↗" jump glyph on
hover; row click → `TerminalJump.FocusSession(session)`; hand cursor.
Empty state: "No active agents — waiting." small gray. Everything must be crisp
on dark; no default WPF chrome anywhere.
