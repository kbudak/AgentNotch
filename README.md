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

   The same helper also speaks **Cursor's** hook dialect, registered in
   `%USERPROFILE%\.cursor\hooks.json`:
   - `beforeShellExecution` / `beforeMCPExecution` — the two events where Cursor
     would raise its own permission dialog. The command (or MCP tool + input)
     lands in the notch and your Allow/Deny answers it. Cursor's `preToolUse`
     fires for *every* tool call including reads, so it is deliberately **not**
     registered — gating a file read on a human stalls the agent on work it
     never needed to ask about.
   - `beforeSubmitPrompt` / `stop` — observe-only freshness signals.

   Cursor names the same facts differently (`conversation_id`,
   `workspace_roots`, a bare `command` string instead of a tool), and expects a
   flat `{"permission": "allow"|"deny"}` reply rather than Claude Code's
   `hookSpecificOutput` envelope. Both differences are absorbed at the edges —
   `HookRequest` on the way in, the helper on the way out — so the notch, the
   diff renderer and the prompt views never learn there are two dialects. On
   "pass", timeout, or a dead app the helper prints nothing and Cursor's own
   dialog runs, exactly as if AgentNotch were not installed.
2. **Session files (the read path).** A `FileSystemWatcher` tails
   `%USERPROFILE%\.claude\projects\**\*.jsonl` transcripts and the
   `%USERPROFILE%\.claude\sessions\*.json` registry to show every live
   session: status (working / needs you / finished), current tool and target,
   cumulative token spend, which app it runs in, and how long it has been going.

## What actually reaches the notch

"Show every question in the notch" has two different ceilings, and which one an
agent hits depends entirely on whether it gives us a **write path**. Detection is
easy — every agent leaves a transcript. Answering requires the agent to *ask us
first and wait*, and only two of the four offer an event for that.

| Agent | How a question is caught | Answerable in the notch? |
|---|---|---|
| **Claude Code** | `PermissionRequest` + `PreToolUse`/`AskUserQuestion` hooks | ✅ everything |
| **Cursor** | `beforeShellExecution` + `beforeMCPExecution` hooks | ✅ shell + MCP (incl. MCP-routed AskQuestion) |
| **Cursor** (native `AskQuestion`) | transcript tail (`CursorTail`) | ⬜ shown as a row; answer in Cursor |
| **Codex** | rollout tail (`CodexTail`) | ⬜ shown as a row; answer in Codex |
| **Antigravity** | `language_server` RPC (`ask_question` step) | ⬜ shown as a row; answer in Antigravity |

A ⬜ row is not a gap in *detection* — the question text is parsed out and shown
on the row, and the pill goes amber. It is a gap in *answering*, and it is not
closeable from this side:

- **Cursor's native `AskQuestion`** has no dedicated hook. The only event that
  observes it is `preToolUse`, which fires on **every** tool call. The hook helper
  costs ~77 ms of .NET cold start per spawn (measured; `PublishReadyToRun` makes no
  difference — the cost is process + host startup, not JIT), so registering it
  would add ~20 s of latency to a 300-tool-call session in exchange for catching a
  question that is already visible on the row. Deliberately not registered. Native
  AOT would cut the spawn to ~5 ms and change this trade — it needs a
  `JsonSerializerContext` for the protocol types plus the C++ build tools.
- **Codex and Antigravity** expose no approval/question callback at all. They are
  read-only by construction.

`PromptViewModel` nevertheless parses **both** question dialects, because the
agents agree on structure and disagree on every field name — Claude Code writes
`question`/`header`/`label`, Cursor writes `prompt`/`title`/`id`. So an MCP-routed
`AskQuestion` renders as a real question card with option buttons today, and a
native one would need no further work if `preToolUse` were ever enabled.

### The question shows up in the terminal too

An agent will not draw its own prompt while a hook is pending — the hook exists to
decide the permission, so it is one or the other. Left alone that means the terminal
goes silent for as long as the notch holds the question, and if you are not looking
at the notch there is nothing anywhere telling you the agent is blocked. (It bit
this machine three times: the helper waited its full 590 s and gave up, so the agent
sat frozen for ten minutes.)

So the helper echoes the question onto the terminal itself. Not via stdout — Claude
Code reads that as the decision — and not via stderr, which it surfaces as a hook
*error*. The console is a third channel: the helper is a child of the agent and
inherits its console, so opening `CONOUT$` writes to the real terminal, past both
pipes. Verified by spawning the helper with stdout and stderr both redirected and
then reading the console screen buffer back: only the decision JSON came back on
stdout, while these lines appeared on the console.

```
  [AgentNotch] Question
    Which deployment target?
      1. Production
      2. Staging
    waiting for your answer in the notch (60s, then this prompt takes over)
  [AgentNotch] answered from the notch: Staging
```

Three rules keep it from making a mess of a terminal it does not own:

- **Plain ASCII, no ANSI.** The console's output codepage is whatever the machine
  defaults to — 857 here, not UTF-8 — so a UTF-8 `◆` arrived as `Ôùå`. Accented and
  Turkish characters are transliterated, anything else becomes `?`. And no cursor
  movement: lines append and scroll like ordinary output instead of fighting the
  agent's redraw.
- **Never blocks or throws.** A session with no console at all (Claude Desktop, a
  piped or headless run) simply has no `CONOUT$`; every failure is swallowed. An
  echo is a courtesy, and must not be able to delay a hook.
- **The notch stays authoritative.** The echo says what is being asked and where to
  answer it; it does not turn the terminal into a second answering surface. Synthetic
  keystrokes were considered and rejected — they steal focus, and an injected
  `1<Enter>` landing in a terminal that has moved on is exactly the accident a tool
  about approving actions must not have.

After **60 seconds** with no answer the helper prints nothing and exits, so the
agent's own prompt appears and waits for you there indefinitely. Nothing is lost,
and a missed notch can no longer freeze an agent for ten minutes.

### A finished session has to stay gone

"How long since this session did anything" is read from the timestamp of the last
assistant/user record — never from the transcript's file mtime.

They are not the same thing. Claude Code keeps appending bookkeeping to a transcript
long after the conversation is over: `bridge-session` records, and `system` /
`local_command` notices such as Remote Control connecting and disconnecting. Every
one of those touches the file. Reading mtime as activity meant each write reset the
session's age, un-dropped it, and — since its last real turn was an old `end_turn` —
put it back on the notch reading "Done", indefinitely. Measured live: a session whose
last real turn was **two days old** carried a **nine-minute-old** mtime, and simulating
one bookkeeping write resurrected four dead sessions at once.

Only a real turn refreshes a session now, so a finished one leaves after `IdleKeep`
and cannot come back until you actually say something. A future-dated record is
clamped to now, so clock skew cannot pin a session to "just active", and a tail
holding no turn at all still falls back to mtime rather than vanishing.

Cursor's transcripts end on real turns or `turn_ended` with no bookkeeping tail, so
`CursorTail` is unaffected and was left alone.

### Orange means the agent is asking right now

Amber appears only for an ask the agent is blocked on at this moment:

- an ask tool waiting for its answer — Claude Code's `AskUserQuestion`, Cursor's
  `AskQuestion` / `cursor_dialog`, Antigravity's `ask_question` step;
- a tool waiting for approval while the CLI sits idle at the permission prompt;
- Codex's pending `request_user_input` / escalation call.

A question in a finished turn's closing words — "Hangisini istersin?", "nasıl devam
edelim?" — is not one. The work has stopped and the agent is waiting on nothing, so
it reads as finished. The same holds for everything a finished session can leave as
its last line. Across 47 real transcripts that was an end_turn text (19), a text
whose `stop_reason` was never written (10), a tool result (8), a local `/command`'s
output (5), a prompt or an interrupt (2), or a pending tool (2). With the CLI idle,
all of them except the pending tool used to come out orange. That is why reopening
Claude on an old project could light a question that nobody was asking. An earlier
version also scanned the closing text for asking words, and the stems matched
nouns and labels ("onaylı fotoğraf", "izin ver"); that detection is gone entirely.

### Working vs finished: read the block, not the stop reason

Claude Code writes one transcript line **per content block** while the model
streams, and that line's `stop_reason` is frequently absent when it lands. So the
tail of a live transcript is normally *mid-turn*, not the end of one. Deriving
"done" from `stop_reason` alone therefore reported a thinking agent as finished —
and because that one line then sits at the tail for the entire length of a long
thought, the pill went green and stayed green while the agent was still working.

The turn ends on **text the model signed off**. `thinking` and `tool_use` are
never where it stops. Measured over 37 real transcripts, keying off the last
content block instead of the stop reason corrects 600 states:

| last block | `stop_reason` | was | now | count |
|---|---|---|---|---|
| `thinking` | `null` | Finished | **Working** | 209 |
| `thinking` | `end_turn` | Finished | **Working** | 92 |
| `tool_use` | `null` | Finished | **Working** | 232 |
| `text` | `null` | Finished | **Working** | 67 |
| `text` | `end_turn` | Finished | Finished | 267 |
| `text` | `stop_sequence` | Finished | Finished | 13 |

Three qualifications keep it honest:

- A pending **ask** tool (`AskUserQuestion`) is a `tool_use` block, but it is the
  agent waiting on *you* — it short-circuits to NeedsYou before the mid-turn rule
  can swallow it.
- A **`text` block with no stop reason** is only *probably* mid-stream, so it gets
  the short `WorkingGrace` window rather than the long `BusyMax` one. A genuinely
  finished session cannot sit on a false "working" for the best part of an hour.
- An **interrupt** (Esc mid-thought) leaves a thinking block at the tail, so a
  fresh registry `idle` still hands the turn back to you.

This matters most for sessions the registry cannot help with. A `claude-desktop`
session writes **no `status` field at all** — verified live — so the `busy`/`idle`
fast path never fires for it and the transcript is the only evidence there is.

### One rule worth keeping

Whether a request gets a UI card is decided by `HookRequest.ExpectsReply` — the
same flag the helper and `PipeServer` use to decide whether to hold the pipe open.
It must never be re-derived from a list of known modes: a blocking mode missing
from such a list is a request nobody ever answers, and the agent hangs for the
helper's full 590 s timeout before falling through. `CursorHookPayloadTests` pins
this.

## Reading the notch

Shape says **which agent**, colour says **what state**:

| | |
|---|---|
| 👾 invader | Claude |
| ✿ six-lobed knot | Codex |
| ✦ sparkle | Antigravity (Gemini) |
| ⬢ cube | Cursor agent |

Every mark is drawn in the same 24x24 box but only ever rendered at **11-17px**,
which is the whole design constraint. The Codex knot and the Cursor cube were both
redrawn for it: the literal OpenAI knot collapsed into a featureless smudge at that
size, and Cursor's silhouette had been reduced to a bare hexagon after an earlier
hollow outline thinned to a hairline. Codex keeps the knot's six lobes and hex
centre at a weight that survives; Cursor is drawn the way
Claude is: as pixel art. Its mark is a pointy-top hexagon on a 9x10 grid with the
cursor arrow from Cursor's own logo cut out of it — traced from the splash logo
Cursor ships (`out/vs/glass/browser/media/cursor-splash-logo-normal.png`) and then
squared up by hand, because a raw threshold of the logo came out lumpy and
lopsided. It is one seamless outline plus the arrow's hole, so no seams open
between cells at fractional sizes. Like every other mark it takes the state colour:
blue while working, orange while asking, green when done.
| 🟢 green | working right now |
| 🟠 amber | waiting on you |
| 🔵 blue | finished — click the row to jump there |
| 🟧 orange | an approval is open in the notch |

Each row also carries chips for the agent and the app hosting it ("Claude ·
Windows Terminal", "Codex · Cursor"), plus its age. Resolving a prompt flashes a
one-word confirmation in the kind's colour — `✓ Allowed`, `✓ Answered`, `✕ Denied`.
Deliberately just the verb: naming what it applied to put an arbitrarily long answer
label on a 248px pill, which clipped however it was laid out. The glyph and the
colour already carry the meaning, and the terminal echo records the full answer.

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

**Install Cursor hooks** does the same for `~\.cursor\hooks.json`. The item is
greyed out and labelled "Cursor not detected" when Cursor is not on the machine
(detected by its home directory, or a running `Cursor` / `cursor-agent`
process). Both installers merge rather than overwrite: every foreign hook and
unknown property in those files round-trips untouched, a malformed file is
refused outright rather than clobbered, and the write is backed up and atomic.

`AgentNotch.exe --install-hooks` / `--uninstall-hooks` does both agents
headlessly, skipping Cursor when it is not present.

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
    gated on a running Cursor process; jump focuses Cursor. Write path:
    `~\.cursor\hooks.json` shell and MCP approvals answered from the notch.
  - **Cursor / VS Code / Antigravity as hosts** — Claude Code sessions running
    inside an editor are labeled with their host ("claude · cursor — web") via
    parent-process detection, and jump focuses that editor's window.
- [ ] Phase 6 — `uiAccess` signing, sounds, settings UI
