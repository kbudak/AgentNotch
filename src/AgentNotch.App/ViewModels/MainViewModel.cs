using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using AgentNotch.App.Diagnostics;
using AgentNotch.App.Ipc;
using AgentNotch.App.Sessions;
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
    private readonly DispatcherTimer _collapseTimer;
    private bool _hover;      // pointer is currently over the notch
    private bool _hoverHeld;  // expansion held by hover (kept through the collapse delay)

    public MainViewModel()
    {
        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _collapseTimer.Tick += OnCollapseTick;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<AgentSession> Sessions { get; } = new();

    /// <summary>Head of the FIFO prompt queue; null when nothing is pending.</summary>
    public PromptViewModel? ActivePrompt => _queue.Count > 0 ? _queue[0] : null;

    /// <summary>Pending prompts including the active one.</summary>
    public int QueueCount => _queue.Count;

    /// <summary>"1 of 3" badge text (only shown when QueueCount > 1).</summary>
    public string QueueBadgeText => $"1 of {_queue.Count}";

    public bool IsExpanded => _hoverHeld || _queue.Count > 0;

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
            Raise(nameof(IsExpanded));
        }
    }

    // ------------------------------------------------------------- resolution

    public void AllowActive()
    {
        var prompt = ActivePrompt;
        if (prompt is null || prompt.Kind != PromptKind.Permission) return;
        Resolve(prompt, new DecisionBody { Behavior = "allow" }, "allow");
    }

    public void DenyActive()
    {
        var prompt = ActivePrompt;
        if (prompt is null || prompt.Kind != PromptKind.Permission) return;
        Resolve(prompt, new DecisionBody { Behavior = "deny", Message = "Denied from AgentNotch" }, "deny");
    }

    public void PassActive()
    {
        var prompt = ActivePrompt;
        if (prompt is null) return;
        Resolve(prompt, new DecisionBody { Behavior = "pass" }, "pass");
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
