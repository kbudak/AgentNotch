using System.Collections.ObjectModel;
using System.Linq;
using System.ComponentModel;
using System.Windows.Threading;
using AgentNotch.App.Diagnostics;
using AgentNotch.App.Ipc;
using AgentNotch.App.Sessions;
using AgentNotch.App.Usage;
using AgentNotch.Protocol;

namespace AgentNotch.App.ViewModels;

/// <summary>One chip in the collapsed pill: how many sessions are in a state, and
/// the state kind that colours it ("working" | "needsyou" | "finished" |
/// "prompt"). The colour is the label — there is no wording.</summary>
public sealed record StatusPart(string Count, string Kind);

/// <summary>One agent's mark on the console collapsed line. Colour is identity —
/// which agent — never state, so this carries the agent id and nothing else.</summary>
public sealed record AgentMark(string Agent);

/// <summary>
/// The single view model behind the notch. All mutation happens on the UI thread
/// (the composition root marshals pipe/watcher events before calling in); the
/// only cross-thread entry point is a prompt's Abandoned callback, which is
/// re-marshalled here.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly List<PromptViewModel> _queue = new();
    private readonly Integration.AppConfig _config = Integration.AppConfig.Load();
    private readonly DispatcherTimer _collapseTimer;
    private readonly DispatcherTimer _toastTimer;
    private bool _hover;      // pointer is currently over the notch
    private bool _hoverHeld;  // expansion held by hover (kept through the collapse delay)

    public MainViewModel()
    {
        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _collapseTimer.Tick += OnCollapseTick;
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2400) };
        _toastTimer.Tick += OnToastTick;
        UI.ThemeManager.Apply(_config.Theme); // before any window is built, so it opens in the right skin
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<AgentSession> Sessions { get; } = new();

    /// <summary>Plan rate-limit windows ("5h 20% · 2h14m") shown above the sessions.</summary>
    public ObservableCollection<UsageWindow> Usage { get; } = new();

    /// <summary>
    /// Short-lived confirmation after you resolve a prompt ("Allowed", "Denied",
    /// "Answered") — the notch reports what it sent back so you never have to switch
    /// to the terminal to check. Null when nothing to show.
    /// </summary>
    public string? ToastText { get; private set; }


    /// <summary>"ok" | "deny" | "pass" — picks the badge glyph and colour.</summary>
    public string ToastKind { get; private set; } = "ok";

    public bool HasToast => ToastText is { Length: > 0 };

    /// <summary>Head of the FIFO prompt queue; null when nothing is pending.</summary>
    public PromptViewModel? ActivePrompt => _queue.Count > 0 ? _queue[0] : null;

    /// <summary>Pending prompts including the active one.</summary>
    public int QueueCount => _queue.Count;

    /// <summary>"1 of 3" badge text (only shown when QueueCount > 1).</summary>
    public string QueueBadgeText => $"1 of {_queue.Count}";

    public bool IsExpanded => _hoverHeld || _queue.Count > 0 || _settingsOpen;

    private bool _settingsOpen;

    /// <summary>Whether the settings flyout is showing (gear toggles it).</summary>
    public bool SettingsOpen => _settingsOpen;

    /// <summary>Launch AgentNotch at Windows login. Reads/writes the HKCU Run entry.</summary>
    public bool LaunchAtLogin
    {
        get => Integration.StartupManager.IsEnabled();
        set { Integration.StartupManager.SetEnabled(value); Raise(nameof(LaunchAtLogin)); }
    }

    /// <summary>Show the plan-limit row (5h/7d) at the top of the panel.</summary>
    public bool ShowUsage
    {
        get => _config.ShowUsage;
        set { _config.ShowUsage = value; _config.Save(); Raise(nameof(ShowUsage)); }
    }

    /// <summary>Notch position along the top edge: "left" | "center" | "right".</summary>
    public string Position
    {
        get => _config.Position;
        set
        {
            if (string.Equals(value, _config.Position, StringComparison.Ordinal)) return;
            _config.Position = value;
            _config.Save();
            Raise(nameof(Position));
        }
    }

    private string? _themeOverride; // --theme for a self-test run; never persisted

    /// <summary>
    /// Which skin the notch wears: "island" | "glass" | "atari" | "console". It selects
    /// the palette and layout (only Console has its own tree; the rest dress Island's
    /// rounded one), so all changes go through here.
    /// </summary>
    public string Theme
    {
        get => _themeOverride ?? UI.ThemeManager.Normalize(_config.Theme);
        set
        {
            var next = UI.ThemeManager.Normalize(value);
            if (string.Equals(next, Theme, StringComparison.Ordinal)) return;
            _themeOverride = null;
            _config.Theme = next;
            _config.Save();
            ApplyTheme(next);
        }
    }

    /// <summary>Self-test only (--demo --theme): switch skin for this run, leaving config.json alone.</summary>
    public void PreviewTheme(string name)
    {
        _themeOverride = UI.ThemeManager.Normalize(name);
        ApplyTheme(_themeOverride);
    }

    private void ApplyTheme(string name)
    {
        UI.ThemeManager.Apply(name);
        RepaintConverterBindings();
        Raise(nameof(Theme));
    }

    /// <summary>
    /// Swapping the palette updates every DynamicResource on its own, but the
    /// status/usage colours come from converters, which returned a Brush once and
    /// are not re-run by a resource change. Handing the list back through a
    /// Clear/re-Add rebuilds the row containers, which re-runs them; the computed
    /// properties just need a notify. Rare enough (a settings click) that the
    /// container churn does not matter.
    /// </summary>
    private void RepaintConverterBindings()
    {
        var sessions = Sessions.ToList();
        Sessions.Clear();
        foreach (var s in sessions) Sessions.Add(s);

        var usage = Usage.ToList();
        Usage.Clear();
        foreach (var u in usage) Usage.Add(u);

        Raise(nameof(CollapsedParts));
        Raise(nameof(ToastKind));
        Raise(nameof(TightestUsage));
        Raise(nameof(AgentMarks));
    }

    /// <summary>Open/close the settings flyout; keeps the notch expanded while open.</summary>
    public void ToggleSettings()
    {
        _settingsOpen = !_settingsOpen;
        Raise(nameof(SettingsOpen));
        Raise(nameof(LaunchAtLogin)); // re-read the live registry state as it opens
        Raise(nameof(ShowUsage));
        Raise(nameof(Position));
        Raise(nameof(Theme));
        Raise(nameof(IsExpanded));
    }

    /// <summary>
    /// The collapsed pill's summary as one chip per state — a count wearing that
    /// state's colour, no wording. Spelling the states out ("1 working · 1 done")
    /// filled the pill with text that the colour already says.
    /// Empty when idle — the pill then shows nothing but the grey mark.
    /// </summary>
    public IReadOnlyList<StatusPart> CollapsedParts
    {
        get
        {
            // The pill is hidden whenever a prompt is open (that expands the notch),
            // so this only ever describes sessions.
            if (_queue.Count > 0)
                return new[] { new StatusPart(_queue.Count.ToString(), "prompt") };

            int needs = 0, working = 0, done = 0;
            foreach (var s in Sessions)
            {
                switch (s.Status)
                {
                    case SessionStatus.NeedsYou: needs++; break;
                    case SessionStatus.Working: working++; break;
                    default: done++; break;
                }
            }

            // Most urgent first. Three bare counts fit the 248px pill easily, so
            // unlike the old spelled-out version nothing has to be dropped.
            var parts = new List<StatusPart>(3);
            if (needs > 0) parts.Add(new StatusPart(needs.ToString(), "needsyou"));
            if (working > 0) parts.Add(new StatusPart(working.ToString(), "working"));
            if (done > 0) parts.Add(new StatusPart(done.ToString(), "finished"));
            return parts;
        }
    }

    /// <summary>
    /// Agent whose mark the collapsed pill shows ("" when idle). Picked to match
    /// <see cref="StateKind"/>, so the pill's shape and colour always describe the
    /// same session: an open prompt is always Claude's (only Claude Code hooks in),
    /// otherwise the agent behind the state being reported.
    /// </summary>
    public string LeadAgent
    {
        get
        {
            if (_queue.Count > 0) return "claude";
            foreach (var s in Sessions)
                if (s.Status == SessionStatus.NeedsYou) return s.Agent;
            foreach (var s in Sessions)
                if (s.Status == SessionStatus.Working) return s.Agent;
            // Finished-only: still show the agent's mark (Cursor cube, etc.) so the
            // pill does not collapse to a generic grey idle dot while a session row exists.
            foreach (var s in Sessions)
                if (!string.IsNullOrEmpty(s.Agent)) return s.Agent;
            return "";
        }
    }

    /// <summary>"idle" | "working" | "needsyou" | "prompt" | "finished" — drives the pill dot.</summary>
    public string StateKind
    {
        get
        {
            if (_queue.Count > 0) return "prompt";
            bool needs = false, working = false, any = false;
            foreach (var s in Sessions)
            {
                any = true;
                if (s.Status == SessionStatus.NeedsYou) needs = true;
                else if (s.Status == SessionStatus.Working) working = true;
            }
            if (needs) return "needsyou";
            if (working) return "working";
            return any ? "finished" : "idle";
        }
    }

    // ------------------------------------------------- console shell (theme 2)
    // The console theme states things in words where the island theme uses shape
    // and colour, so it needs a headline, a four-way shell state (the island
    // pill's StateKind cannot tell "everything finished" from "nothing running"),
    // and the single constraining quota window.

    /// <summary>"blocked" | "working" | "done" | "empty" — picks the shell glyph and its ink.</summary>
    public string ShellState
    {
        get
        {
            if (_queue.Count > 0) return "blocked";
            bool needs = false, working = false;
            foreach (var s in Sessions)
            {
                if (s.Status == SessionStatus.NeedsYou) needs = true;
                else if (s.Status == SessionStatus.Working) working = true;
            }
            if (needs) return "blocked";
            if (working) return "working";
            return Sessions.Count > 0 ? "done" : "empty";
        }
    }

    /// <summary>True while something is waiting on you — the only thing allowed to turn the shell amber.</summary>
    public bool BlockedOnYou => ShellState == "blocked";

    /// <summary>The console shell's one line: "claude needs you", "2 running · 1 done", "watching · 0 agents".</summary>
    public string Headline
    {
        get
        {
            if (ActivePrompt is { } prompt)
            {
                var agent = AgentOf(prompt);
                return prompt.Kind == PromptKind.Question ? $"{agent} is asking" : $"{agent} needs you";
            }

            int needs = 0, working = 0, done = 0;
            foreach (var s in Sessions)
            {
                switch (s.Status)
                {
                    case SessionStatus.NeedsYou: needs++; break;
                    case SessionStatus.Working: working++; break;
                    default: done++; break;
                }
            }
            if (Sessions.Count == 0) return "watching · 0 agents";

            var parts = new List<string>(3);
            if (needs > 0) parts.Add(needs == 1 ? "1 needs you" : $"{needs} need you");
            if (working > 0) parts.Add($"{working} running");
            if (done > 0) parts.Add($"{done} done");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Agent behind a prompt. Only Claude Code hooks in today, so that is the fallback.</summary>
    private string AgentOf(PromptViewModel prompt)
    {
        try
        {
            var id = prompt.Request.SessionId;
            if (!string.IsNullOrEmpty(id))
                foreach (var s in Sessions)
                    if (string.Equals(s.SessionId, id, StringComparison.Ordinal) && s.Agent is { Length: > 0 })
                        return s.Agent;
        }
        catch { /* headline must never throw */ }
        return "claude";
    }

    /// <summary>
    /// The window that will cut you off first — the console header shows only this
    /// one, by design; the full set is a separate view in the spec.
    /// </summary>
    public UsageWindow? TightestUsage
    {
        get
        {
            UsageWindow? tightest = null;
            foreach (var w in Usage)
                if (tightest is null || w.UsedPct > tightest.UsedPct) tightest = w;
            return tightest;
        }
    }

    /// <summary>
    /// One mark per AGENT on the collapsed line, not per session — the marks say
    /// which agents are on the machine, and the headline beside them already
    /// carries the counts. Three Claude sessions is one Claude mark.
    /// </summary>
    public IReadOnlyList<AgentMark> AgentMarks
    {
        get
        {
            var marks = new List<AgentMark>(4);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in Sessions)
            {
                var agent = s.Agent ?? "";
                if (seen.Add(agent)) marks.Add(new AgentMark(agent));
            }
            return marks;
        }
    }

    // ---------------------------------------------------------------- intake

    public void AddHookRequest(HookRequest request)
    {
        try
        {
            // Gate on ExpectsReply, NOT on a list of known modes. The helper and
            // PipeServer already decided from this same flag whether to hold the
            // connection open — a mode enumerated in only one of the two places is
            // a blocking request nobody ever answers, and the agent hangs for the
            // helper's full timeout before falling through.
            if (request.ExpectsReply)
            {
                var prompt = new PromptViewModel(request);
                _queue.Add(prompt);
                request.Abandoned += () => RunOnUi(() =>
                {
                    if (_queue.Remove(prompt))
                    {
                        AppLog.Write($"prompt abandoned: {prompt.Title} (queue={_queue.Count})");
                        RaiseQueueChanged();
                    }
                });
                AppLog.Write($"prompt queued: {request.Mode} \"{prompt.Title}\" (queue={_queue.Count})");
                RaiseQueueChanged();
            }
            else
            {
                // Observe-only modes (notify/stop/prompt/cursor-notify): freshness
                // nudge, no UI card and nothing waiting on a reply.
                AppLog.Write($"observe: {request.Mode} {request.HookEventName} session={request.SessionId}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("AddHookRequest: " + ex);
        }
    }

    /// <summary>Replaces the rate-limit windows shown in the header (UI thread).</summary>
    public void UpdateUsage(IReadOnlyList<UsageWindow> windows)
    {
        try
        {
            Usage.Clear();
            if (windows is not null)
                foreach (var w in windows)
                    Usage.Add(w);
        }
        catch (Exception ex)
        {
            AppLog.Write("UpdateUsage: " + ex.Message);
        }
        Raise(nameof(Usage));
        Raise(nameof(TightestUsage));
    }

    /// <summary>Flashes a confirmation in the notch for a couple of seconds.</summary>
    private void ShowToast(string text, string kind)
    {
        ToastText = text;
        ToastKind = kind;
        Raise(nameof(ToastText));
        Raise(nameof(ToastKind));
        Raise(nameof(HasToast));

        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void OnToastTick(object? sender, EventArgs e)
    {
        _toastTimer.Stop();
        ToastText = null;
        Raise(nameof(ToastText));
        Raise(nameof(HasToast));
    }

    private static string? _lastUiSessionLog;

    public void UpdateSessions(IReadOnlyList<AgentSession> sessions)
    {
        try
        {
            IReadOnlyList<AgentSession> incoming = sessions ?? Array.Empty<AgentSession>();

            // The watcher re-sorts by last activity, so a working session climbs
            // the list every tick. While the panel is open that pulls rows out
            // from under the pointer mid-click — you jump to the wrong project,
            // or miss entirely and have to click again. Freeze the order the
            // user can actually see; only the contents keep refreshing.
            if (IsExpanded && Sessions.Count > 0)
                incoming = InVisibleOrder(incoming);

            var now = DateTime.UtcNow;
            // Reconcile row-by-row instead of Clear()+Add(): an unchanged row
            // keeps its own container (and its hover/press state), so a rebuild
            // no longer destroys the Border the click is landing on.
            for (int i = 0; i < incoming.Count; i++)
            {
                if (i >= Sessions.Count)
                    Sessions.Add(incoming[i]);
                else if (RowKey(Sessions[i], now) != RowKey(incoming[i], now))
                    Sessions[i] = incoming[i];
            }
            while (Sessions.Count > incoming.Count)
                Sessions.RemoveAt(Sessions.Count - 1);
        }
        catch (Exception ex)
        {
            AppLog.Write("UpdateSessions: " + ex.Message);
        }
        RaiseShellChanged();

        try
        {
            var summary = string.Join(", ", Sessions.Select(s => $"{s.Agent}:{s.Status}"));
            var line = $"ui: sessions={Sessions.Count} kind={StateKind} lead={LeadAgent}"
                       + (summary.Length > 0 ? " [" + summary + "]" : "");
            if (line != _lastUiSessionLog)
            {
                _lastUiSessionLog = line;
                AppLog.Write(line);
            }
        }
        catch { /* logging must never break the pill */ }
    }

    /// <summary>Incoming rows re-ordered to match what is already on screen; new ones land last.</summary>
    private IReadOnlyList<AgentSession> InVisibleOrder(IReadOnlyList<AgentSession> incoming)
    {
        var slot = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < Sessions.Count; i++)
            slot.TryAdd(Sessions[i].SessionId, i);

        return incoming
            .OrderBy(s => slot.TryGetValue(s.SessionId, out int i) ? i : int.MaxValue)
            .ToList();
    }

    /// <summary>
    /// Everything the row template renders, as one string. AgentSession is a
    /// plain snapshot with no change notification, so a row can only refresh by
    /// being replaced — this decides when that is actually worth doing. The age
    /// chip is clock-driven, so it is folded in at minute resolution.
    /// </summary>
    private static string RowKey(AgentSession s, DateTime now) => string.Join('',
        s.SessionId, s.Agent, (int)s.Status, s.ProjectName, s.Model, s.HostLabel,
        s.CurrentTool, s.CurrentTarget, s.LastMessage, s.TokensSpent,
        (long)(now - s.StartedUtc).TotalMinutes);

    // ---------------------------------------------------------------- hover

    public void SetHover(bool over)
    {
        if (over)
        {
            _collapseTimer.Stop();
            _hover = true;
            if (!_hoverHeld)
            {
                _hoverHeld = true;
                Raise(nameof(IsExpanded));
            }
        }
        else
        {
            _hover = false;
            _collapseTimer.Stop();
            _collapseTimer.Start(); // collapse ~250ms after leave (unless re-entered)
        }
    }

    private void OnCollapseTick(object? sender, EventArgs e)
    {
        _collapseTimer.Stop();
        if (!_hover && _hoverHeld)
        {
            _hoverHeld = false;
            if (_settingsOpen) { _settingsOpen = false; Raise(nameof(SettingsOpen)); }
            Raise(nameof(IsExpanded));
        }
    }

    // ------------------------------------------------------------- resolution

    public void AllowActive()
    {
        var prompt = ActivePrompt;
        if (prompt is null || prompt.Kind != PromptKind.Permission) return;
        Resolve(prompt, new DecisionBody { Behavior = "allow" }, "allow");
        ShowToast("Allowed", "ok");
    }

    public void DenyActive()
    {
        var prompt = ActivePrompt;
        if (prompt is null || prompt.Kind != PromptKind.Permission) return;
        Resolve(prompt, new DecisionBody { Behavior = "deny", Message = "Denied from AgentNotch" }, "deny");
        ShowToast("Denied", "deny");
    }

    public void PassActive()
    {
        var prompt = ActivePrompt;
        if (prompt is null) return;
        Resolve(prompt, new DecisionBody { Behavior = "pass" }, "pass");
        ShowToast("Handed back", "pass");
    }


    public void ChooseOption(int index)
    {
        var prompt = ActivePrompt;
        if (prompt is null || prompt.Kind != PromptKind.Question) return;
        if (index < 0 || index >= prompt.Options.Count) return;

        var option = prompt.Options[index];
        var body = new DecisionBody
        {
            Behavior = "answer",
            Answers = new List<QuestionAnswer>
            {
                new() { Question = prompt.QuestionText, Labels = new List<string> { option.Label } },
            },
        };
        Resolve(prompt, body, $"answer #{index + 1}");
        ShowToast("Answered", "ok");
    }

    private void Resolve(PromptViewModel prompt, DecisionBody body, string what)
    {
        try
        {
            var task = prompt.Request.RespondAsync(body);
            _ = task.ContinueWith(
                t => AppLog.Write("respond failed: " + t.Exception?.GetBaseException().Message),
                TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex)
        {
            AppLog.Write("respond: " + ex.Message);
        }

        _queue.Remove(prompt);
        AppLog.Write($"prompt resolved ({what}): \"{prompt.Title}\" (queue={_queue.Count})");
        RaiseQueueChanged();
    }

    // ---------------------------------------------------------------- helpers

    private void RaiseQueueChanged()
    {
        Raise(nameof(ActivePrompt));
        Raise(nameof(QueueCount));
        Raise(nameof(QueueBadgeText));
        Raise(nameof(IsExpanded));
        RaiseShellChanged();
    }

    /// <summary>Everything the collapsed line and the console header derive from the session list and queue.</summary>
    private void RaiseShellChanged()
    {
        Raise(nameof(CollapsedParts));
        Raise(nameof(StateKind));
        Raise(nameof(LeadAgent));
        Raise(nameof(ShellState));
        Raise(nameof(BlockedOnYou));
        Raise(nameof(Headline));
        Raise(nameof(AgentMarks));
    }

    private void Raise(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static void RunOnUi(Action action)
    {
        try
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) action();
            else dispatcher.BeginInvoke(action);
        }
        catch (Exception ex)
        {
            AppLog.Write("RunOnUi: " + ex.Message);
        }
    }
}
