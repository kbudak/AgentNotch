# AgentNotch

A native Windows "dynamic island": a floating, always-on-top notch pinned to the
top-center of the screen that monitors AI coding agents (Claude Code first),
shows their live activity, and lets you **approve/deny permission requests and
answer agent questions without leaving your editor or terminal** — VibeIsland,
but for Windows.

## How it works (and how it deliberately doesn't)

No screen reading. No OCR. No pixel scraping. No process injection. No kernel
driver, no service, no admin.

Everything runs locally over a localhost named pipe, with exactly **one**
outbound request: the plan's rate-limit windows (the `5h` / `7d` numbers in the
header) come from Anthropic's own usage endpoint, called with the OAuth token
Claude Code already stored in `~\.claude\.credentials.json` — the same data
`/usage` shows you. Nothing else leaves the machine, and no telemetry is sent.

Integration is two-sided:

1. **Hooks (the write path).** AgentNotch registers Claude Code hooks in
   `%USERPROFILE%\.claude\settings.json`:
   - `PermissionRequest` — fires **only** when Claude Code would show a
     permission dialog. The hook helper forwards the request over the pipe, the
     notch expands with the tool + target + rendered diff, and your
     Allow/Deny click travels back and resolves the dialog. Latency added ≈
     your think time only.
   - `PreToolUse` (matcher `AskUserQuestion`) — the agent's multiple-choice
     questions appear as buttons in the notch; your pick is fed back to the
     model.
   - `Notification` / `Stop` / `UserPromptSubmit` — observe-only freshness
     signals; the helper forwards and exits immediately.
2. **Session files (the read path).** A `FileSystemWatcher` tails
   `%USERPROFILE%\.claude\projects\**\*.jsonl` transcripts and the
   `%USERPROFILE%\.claude\sessions\*.json` registry to show every live
   session: status (working / needs you / finished), current tool and target,
   cumulative token spend, which app it runs in, and how long it has been going.

## Reading the notch

Shape says **which agent**, colour says **what state**:

| | |
|---|---|
| ✳ sunburst | Claude |
| ⬡ hex knot | Codex |
| ✦ sparkle | Antigravity (Gemini) |
| ▢ cube | Cursor agent |
| 🟢 green | working right now |
| 🟠 amber | waiting on you |
| 🔵 blue | finished — click the row to jump there |
| 🟧 orange | an approval is open in the notch |

Each row also carries chips for the agent and the app hosting it ("Claude ·
Windows Terminal", "Codex · Cursor"), plus its age. Resolving a prompt flashes a
confirmation of what was sent back (`✓ Allowed`, `✓ Production`, `✕ Denied`).

### The permission critical path

```
Claude Code ─PermissionRequest─▶ agentnotch-hook.exe ─▶ \\.\pipe\agentnotch ─▶ notch UI
                                                                                │ Allow (Ctrl+Y)
Claude Code ◀─ stdout decision JSON ◀─ agentnotch-hook.exe ◀────────────────────┘ Deny (Ctrl+N)
```

**Fail-safe by construction:** if AgentNotch isn't running, the pipe connect
fails and the helper exits silently with no output → Claude Code behaves exactly
as if AgentNotch were never installed (normal terminal prompt). Same on timeout
(the helper gives up before Claude Code's own 600 s hook timeout) and on any
helper error. The agent can never hang on us. The notch also has a
"terminal" pass action that hands any prompt back to the terminal explicitly.

## Projects

| Project | What |
|---|---|
| `src/AgentNotch.App` | WPF overlay app: notch window, pipe server, session watcher, hook installer, terminal jump, tray icon |
| `src/AgentNotch.HookHelper` | `agentnotch-hook.exe` — tiny CLI Claude Code spawns per hook event; stdin → pipe → stdout |
| `src/AgentNotch.Protocol` | Shared wire types (NDJSON over the named pipe) |
| `tests/AgentNotch.Tests` | xunit: pipe protocol, settings merge, transcript parsing |

## Overlay window mechanics

The notch is a borderless, transparent WPF window with
`WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST` applied to its HWND:
always on top, hidden from Alt-Tab, and **it never takes keyboard focus** —
clicking Allow/Deny leaves your terminal focused. Because it never has focus,
Ctrl+Y / Ctrl+N / Ctrl+1..9 are registered as *global* hotkeys, and only while a
prompt is actually pending.

## Build & run

```powershell
cd AgentNotch
dotnet build AgentNotch.sln
dotnet run --project src/AgentNotch.App
```

Then right-click the tray icon → **Install Claude Code hooks** (merges entries
into `settings.json`, backing it up first; **new** Claude Code sessions pick the
hooks up). **Uninstall Claude Code hooks** removes only AgentNotch's entries and
leaves everything else untouched.

Smoke mode for automated testing: `AgentNotch.exe --auto allow|deny|pass`
resolves every incoming prompt automatically after showing it.

## Privilege model

Everything runs at standard user level: reading/writing your own
`settings.json`, watching your own files, one named pipe, a topmost overlay,
`AttachConsole`-based terminal resolution. No elevation, no `uiAccess`
manifest (guaranteed foreground over elevated windows is a possible later
phase and would require signing + Program Files).

## Phase status

- [x] Phase 0 — scaffold (solution, overlay pill, ex-styles)
- [x] Phase 1 — MVP: pipe server + hook helper + installer + PermissionRequest Allow/Deny
- [x] Phase 2 — live activity: session watcher, multiple sessions, token spend
- [x] Phase 3 — rich prompts: diff rendering, AskUserQuestion option buttons, queue
- [x] Phase 4 (basic) — terminal jump (console/parent-chain → HWND → foreground)
- [x] Phase 5 — multi-agent sources:
  - **Codex** — `%USERPROFILE%\.codex\sessions\**\rollout-*.jsonl` tail parsing
    (task lifecycle, pending approval calls, final answers); rows gated on a
    running Codex/ChatGPT process; jump targets the hosting editor or terminal.
  - **Antigravity** — local `language_server` RPC (`GetAllCascadeTrajectories`;
    CSRF token from its logs, ports from the TCP table); jump targets the
    Antigravity window.
  - **Cursor agent** — `%USERPROFILE%\.cursor\projects\*\agent-transcripts\*\*.jsonl`
    tail parsing (working / needs-you / finished, AskQuestion text in the row);
    gated on a running Cursor process; jump focuses Cursor.
  - **Cursor / VS Code / Antigravity as hosts** — Claude Code sessions running
    inside an editor are labeled with their host ("claude · cursor — web") via
    parent-process detection, and jump focuses that editor's window.
- [ ] Phase 6 — `uiAccess` signing, sounds, settings UI
