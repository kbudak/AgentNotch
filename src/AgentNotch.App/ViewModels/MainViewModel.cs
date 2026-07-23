using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using AgentNotch.App.Diagnostics;
using AgentNotch.App.Ipc;
using AgentNotch.App.Sessions;
using AgentNotch.App.Usage;
using AgentNotch.Protocol;

namespace AgentNotch.App.ViewModels;

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
    /// Short-lived confirmation after you resolve a prompt ("✓ Allowed",
    /// "✓ Production", "✕ Denied") — the notch reports what it sent back so you
    /// never have to switch to the terminal to check. Null when nothing to show.
    /// </summary>
    public string? ToastText { get; private set; }

    /// <summary>"ok" | "deny" — colours the confirmation.</summary>
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

    public string CollapsedText
    {
        get
        {
            if (_queue.Count > 1) return $"{_queue.Count} waiting for you";
            if (_queue.Count == 1)
                return _queue[0].Kind == PromptKind.Question ? "Claude asks" : "approval needed";

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

            var parts = new List<string>(3);
            if (needs > 0) parts.Add(needs == 1 ? "1 needs you" : $"{needs} need you");
            if (working > 0) parts.Add($"{working} working");
            if (done > 0) parts.Add($"{done} done");
            return parts.Count == 0 ? "idle" : string.Join(" · ", parts);
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
            return Sessions.Count > 0 ? Sessions[0].Agent : "";
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

    public void UpdateSessions(IReadOnlyList<AgentSession> sessions)
    {
        try
        {
            Sessions.Clear();
            if (sessions is not null)
                foreach (var s in sessions)
                    Sessions.Add(s);
        }
        catch (Exception ex)
        {
            AppLog.Write("UpdateSessions: " + ex.Message);
        }
        Raise(nameof(CollapsedText));
        Raise(nameof(StateKind));
        Raise(nameof(LeadAgent));
    }

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
        ShowToast("✓  Allowed — " + ShortTitle(prompt), "ok");
    }

    public void DenyActive()
    {
        var prompt = ActivePrompt;
        if (prompt is null || prompt.Kind != PromptKind.Permission) return;
        Resolve(prompt, new DecisionBody { Behavior = "deny", Message = "Denied from AgentNotch" }, "deny");
        ShowToast("✕  Denied — " + ShortTitle(prompt), "deny");
    }

    public void PassActive()
    {
        var prompt = ActivePrompt;
        if (prompt is null) return;
        Resolve(prompt, new DecisionBody { Behavior = "pass" }, "pass");
        ShowToast("→  Handed back to the terminal", "ok");
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
        ShowToast("✓  " + option.Label, "ok");
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
        Raise(nameof(CollapsedText));
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
