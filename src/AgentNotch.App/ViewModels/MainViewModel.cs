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

/// <summary>One segment of the collapsed pill: a count, the word it counts, and
/// the state kind that colours the count ("working" | "needsyou" | "finished" |
/// "prompt"). Count is empty for segments that are pure wording.</summary>
public sealed record StatusPart(string Count, string Word, string Kind);

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

    /// <summary>What the confirmation applied to ("Write(hello.txt)"); may be empty.</summary>
    public string ToastDetail { get; private set; } = "";

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

    /// <summary>Open/close the settings flyout; keeps the notch expanded while open.</summary>
    public void ToggleSettings()
    {
        _settingsOpen = !_settingsOpen;
        Raise(nameof(SettingsOpen));
        Raise(nameof(LaunchAtLogin)); // re-read the live registry state as it opens
        Raise(nameof(ShowUsage));
        Raise(nameof(Position));
        Raise(nameof(IsExpanded));
    }

    /// <summary>
    /// The collapsed pill's summary, split so each count can be coloured by the
    /// state it counts. As one flat string ("1 working · 1 done") the segments
    /// ran together and the separator was the loudest thing in the pill.
    /// Empty when idle — the pill then shows nothing but the grey mark.
    /// </summary>
    private const int MaxCollapsedParts = 2;

    public IReadOnlyList<StatusPart> CollapsedParts
    {
        get
        {
            if (_queue.Count > 1)
                return new[] { new StatusPart(_queue.Count.ToString(), "waiting for you", "prompt") };
            if (_queue.Count == 1)
                return new[] { new StatusPart("", _queue[0].Kind == PromptKind.Question
                    ? "Claude asks" : "approval needed", "prompt") };

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

            // Built most-urgent first, then capped: all three at once overflows the
            // 248px pill and runs into the marks on its left. Two is what fits, and
            // the ones that get dropped are the least actionable — the expanded
            // panel below lists every session anyway.
            var parts = new List<StatusPart>(3);
            if (needs > 0) parts.Add(new StatusPart(needs.ToString(), needs == 1 ? "needs you" : "need you", "needsyou"));
            if (working > 0) parts.Add(new StatusPart(working.ToString(), "working", "working"));
            if (done > 0) parts.Add(new StatusPart(done.ToString(), "done", "finished"));
            if (parts.Count > MaxCollapsedParts) parts.RemoveRange(MaxCollapsedParts, parts.Count - MaxCollapsedParts);
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
            // Idle (no sessions): show the Claude mark in the idle grey, not a bare dot.
            return Sessions.Count > 0 ? Sessions[0].Agent : "claude";
        }
    }

    /// <summary>"idle" | "working" | "needsyou" | "prompt" — drives the pill dot.</summary>
    public string StateKind
    {
        get
        {
            if (_queue.Count > 0) return "prompt";
            bool needs = false, working = false;
            foreach (var s in Sessions)
            {
                if (s.Status == SessionStatus.NeedsYou) needs = true;
                else if (s.Status == SessionStatus.Working) working = true;
            }
            return needs ? "needsyou" : working ? "working" : "idle";
        }
    }

    // ---------------------------------------------------------------- intake

    public void AddHookRequest(HookRequest request)
    {
        try
        {
            if (request.Mode is Proto.ModePermissionRequest or Proto.ModeAskUserQuestion)
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
                // Observe-only modes (notify/stop/prompt): freshness nudge, no UI card.
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
    }

    /// <summary>Flashes a confirmation in the notch for a couple of seconds.</summary>
    private void ShowToast(string text, string detail, string kind)
    {
        ToastText = text;
        ToastDetail = detail;
        ToastKind = kind;
        Raise(nameof(ToastText));
        Raise(nameof(ToastDetail));
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
        Raise(nameof(CollapsedParts));
        Raise(nameof(StateKind));
        Raise(nameof(LeadAgent));
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
        ShowToast("Allowed", ShortTitle(prompt), "ok");
    }

    public void DenyActive()
    {
        var prompt = ActivePrompt;
        if (prompt is null || prompt.Kind != PromptKind.Permission) return;
        Resolve(prompt, new DecisionBody { Behavior = "deny", Message = "Denied from AgentNotch" }, "deny");
        ShowToast("Denied", ShortTitle(prompt), "deny");
    }

    public void PassActive()
    {
        var prompt = ActivePrompt;
        if (prompt is null) return;
        Resolve(prompt, new DecisionBody { Behavior = "pass" }, "pass");
        ShowToast("Handed back", "over to the terminal", "pass");
    }

    /// <summary>Prompt title trimmed to fit the confirmation pill.</summary>
    private static string ShortTitle(PromptViewModel prompt)
    {
        var title = prompt.Title ?? "";
        return title.Length <= 34 ? title : title[..34] + "…";
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
        ShowToast("Answered", option.Label, "ok");
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
        Raise(nameof(CollapsedParts));
        Raise(nameof(StateKind));
        Raise(nameof(LeadAgent));
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
