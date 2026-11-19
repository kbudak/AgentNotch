using System.Windows;
using System.Windows.Threading;
using AgentNotch.App.Diagnostics;
using AgentNotch.App.Integration;
using AgentNotch.App.Ipc;
using AgentNotch.App.Sessions;
using AgentNotch.App.UI;
using AgentNotch.App.Usage;
using AgentNotch.App.ViewModels;
using AgentNotch.Protocol;
using WinForms = System.Windows.Forms;

namespace AgentNotch.App;

public partial class App : Application
{
    private const string InstanceMutexName = "AgentNotch.SingleInstance";
    private const string RestoreEventName = "AgentNotch.ShowOverlay";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _restoreEvent;
    private RegisteredWaitHandle? _restoreWait;
    private readonly CancellationTokenSource _cts = new();
    private PipeServer? _pipe;
    private SessionWatcher? _watcher;
    private OverlayWindow? _overlay;
    private ClaudeUsageClient? _usage;
    private MainViewModel? _vm;
    private HotkeyManager? _hotkeys;
    private WinForms.NotifyIcon? _tray;
    private string? _autoBehavior; // --auto allow|deny|pass: smoke-test mode, resolves prompts automatically
    private bool _explicitQuit;
    private string _exitReason = "unknown";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Wire every unhandled exception boundary first so nothing during startup or
        // WinForms tray dispatch can terminate the app silently.
        DispatcherUnhandledException += (_, a) => { AppLog.Write("ui exception: " + a.Exception); a.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
            AppLog.Write($"fatal (terminating={a.IsTerminating}): {a.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, a) => { AppLog.Write("task exception: " + a.Exception); a.SetObserved(); };
        try
        {
            WinForms.Application.SetUnhandledExceptionMode(WinForms.UnhandledExceptionMode.CatchException);
            WinForms.Application.ThreadException += (_, a) => AppLog.Write("winforms exception: " + a.Exception);
        }
        catch (Exception ex) { AppLog.Write("winforms exception hook: " + ex.Message); }

        SessionEnding += (_, a) =>
        {
            _explicitQuit = true;
            _exitReason = "session-ending:" + a.ReasonSessionEnding;
            AppLog.Write("exit: " + _exitReason);
        };

        // Headless one-shot maintenance flags (usable while another instance runs).
        if (e.Args.Contains("--install-hooks") || e.Args.Contains("--uninstall-hooks"))
        {
            try
            {
                if (e.Args.Contains("--uninstall-hooks"))
                {
                    ClaudeCodeInstaller.Uninstall();
                    AppLog.Write("cli: hooks uninstalled");
                    if (CursorInstaller.IsCursorInstalled())
                    {
                        CursorInstaller.Uninstall();
                        AppLog.Write("cli: cursor hooks uninstalled");
                    }
                }
                else
                {
                    var helper = ResolveHelperPath()
                        ?? throw new InvalidOperationException("agentnotch-hook.exe not found next to the app.");
                    ClaudeCodeInstaller.Install(helper);
                    AppLog.Write("cli: hooks installed -> " + helper);
                    if (CursorInstaller.IsCursorInstalled())
                    {
                        CursorInstaller.Install(helper);
                        AppLog.Write("cli: cursor hooks installed -> " + helper);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("cli: hook maintenance failed: " + ex);
            }
            Shutdown();
            return;
        }

        // UI self-test: fake data through the real VM/views, window rendered to PNGs
        // with RenderTargetBitmap (no screen-capture APIs). Usage: --demo [--shot <base>]
        if (e.Args.Contains("--demo"))
        {
            _vm = new MainViewModel();
            // --theme <id> overrides the skin for this run only, so both can be shot
            // side by side without touching the user's config.
            for (int i = 0; i < e.Args.Length - 1; i++)
                if (e.Args[i] == "--theme") _vm.PreviewTheme(e.Args[i + 1]);
            _overlay = new OverlayWindow { DataContext = _vm };
            _overlay.Show();
            RunDemo(e.Args);
            return;
        }

        _instanceMutex = new Mutex(true, InstanceMutexName, out bool isFirst);
        if (!isFirst)
        {
            // If the user re-launches AgentNotch because the notch got buried or hidden,
            // wake the running instance so it immediately restores and recenters the overlay.
            try
            {
                using var wake = EventWaitHandle.OpenExisting(RestoreEventName);
                wake.Set();
                AppLog.Write("second instance — signalled running instance to restore overlay");
            }
            catch
            {
                AppLog.Write("second instance — exiting");
            }
            _explicitQuit = true;
            _exitReason = "second-instance";
            Shutdown();
            return;
        }

        try
        {
            _restoreEvent = new EventWaitHandle(false, EventResetMode.AutoReset, RestoreEventName);
            _restoreWait = ThreadPool.RegisterWaitForSingleObject(
                _restoreEvent,
                (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Normal, () => EnsureOverlayAliveAndVisible("second-instance")),
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }
        catch (Exception ex)
        {
            AppLog.Write("restore signal setup failed: " + ex.Message);
        }

        for (int i = 0; i < e.Args.Length - 1; i++)
            if (e.Args[i] == "--auto")
                _autoBehavior = e.Args[i + 1];

        _vm = new MainViewModel();

        CreateOverlay();

        _pipe = new PipeServer();
        _pipe.RequestReceived += req => Dispatcher.BeginInvoke(DispatcherPriority.Normal, () => OnHookRequest(req));
        _pipe.Start(_cts.Token);

        _watcher = new SessionWatcher();
        // Normal (not Background): Cursor Working/NeedsYou must beat Claude IO and
        // other low-priority work or the pill stays visually idle while logs say Working.
        _watcher.SessionsChanged += list => Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            _vm!.UpdateSessions(list);
            // Piggyback on the session poll: refresh limits (throttled internally)
            // and publish whatever snapshot the client currently holds.
            _usage?.RefreshIfDue();
            _vm.UpdateUsage(_usage?.Windows ?? Array.Empty<UsageWindow>());
        });
        _watcher.Start();

        _usage = new ClaudeUsageClient();
        _usage.RefreshIfDue();
        _vm.UpdateUsage(_usage.Windows); // seed from the persisted snapshot

        CreateTray();
        AppLog.Write($"started (auto={_autoBehavior ?? "off"}, helper={ResolveHelperPath() ?? "NOT FOUND"})");
    }

    private void CreateOverlay()
    {
        _hotkeys?.Dispose();
        _overlay = new OverlayWindow { DataContext = _vm };
        _overlay.TaskbarRecreated += RefreshTrayVisibility;
        _overlay.Show();
        _hotkeys = new HotkeyManager(_overlay, _vm!);
    }

    private void EnsureOverlayAliveAndVisible(string reason)
    {
        try
        {
            if (_overlay is null || PresentationSource.FromVisual(_overlay) is null)
            {
                AppLog.Write($"overlay: recreating window ({reason})");
                CreateOverlay();
                return;
            }
            _overlay.EnsureVisibleAndTopmost(reason);
        }
        catch (Exception ex)
        {
            AppLog.Write("overlay ensure alive failed: " + ex);
        }
    }

    private void RefreshTrayVisibility()
    {
        try
        {
            if (_tray is null) return;
            _tray.Visible = false;
            _tray.Visible = true;
        }
        catch (Exception ex) { AppLog.Write("tray refresh failed: " + ex.Message); }
    }

    private void OnHookRequest(HookRequest req)
    {
        if (_autoBehavior is { } behavior && req.ExpectsReply)
        {
            _ = req.RespondAsync(new DecisionBody { Behavior = behavior, Message = "AgentNotch auto mode" });
            return;
        }
        _vm!.AddHookRequest(req);
    }

    /// <summary>The hook helper ships in a "helper" folder next to the app exe (dev: copied post-build).</summary>
    internal static string? ResolveHelperPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "helper", "agentnotch-hook.exe"),
            Path.Combine(AppContext.BaseDirectory, "agentnotch-hook.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private void CreateTray()
    {
        var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        _tray = new WinForms.NotifyIcon
        {
            Icon = File.Exists(icoPath) ? new System.Drawing.Icon(icoPath) : System.Drawing.SystemIcons.Application,
            Text = "AgentNotch",
            Visible = true,
        };

        var menu = new WinForms.ContextMenuStrip();
        var showNotch = new WinForms.ToolStripMenuItem("Show / Recenter notch");
        var install = new WinForms.ToolStripMenuItem("Install Claude Code hooks");
        var uninstall = new WinForms.ToolStripMenuItem("Uninstall Claude Code hooks");
        var cursorInstall = new WinForms.ToolStripMenuItem("Install Cursor hooks");
        var cursorUninstall = new WinForms.ToolStripMenuItem("Uninstall Cursor hooks");
        var logs = new WinForms.ToolStripMenuItem("Open logs");
        var quit = new WinForms.ToolStripMenuItem("Quit AgentNotch");

        showNotch.Click += (_, _) => EnsureOverlayAliveAndVisible("tray-menu");
        _tray.MouseClick += (_, a) =>
        {
            if (a.Button == WinForms.MouseButtons.Left)
                EnsureOverlayAliveAndVisible("tray-click");
        };
        _tray.DoubleClick += (_, _) => EnsureOverlayAliveAndVisible("tray-double-click");

        install.Click += (_, _) =>
        {
            try
            {
                var helper = ResolveHelperPath()
                    ?? throw new InvalidOperationException("agentnotch-hook.exe not found next to the app.");
                ClaudeCodeInstaller.Install(helper);
                _tray!.ShowBalloonTip(4000, "AgentNotch",
                    "Claude Code hooks installed. New sessions will route approvals to the notch.",
                    WinForms.ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                AppLog.Write("install failed: " + ex);
                _tray!.ShowBalloonTip(4000, "AgentNotch", "Hook install failed: " + ex.Message, WinForms.ToolTipIcon.Error);
            }
        };
        uninstall.Click += (_, _) =>
        {
            try
            {
                ClaudeCodeInstaller.Uninstall();
                _tray!.ShowBalloonTip(4000, "AgentNotch", "Claude Code hooks removed.", WinForms.ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                AppLog.Write("uninstall failed: " + ex);
                _tray!.ShowBalloonTip(4000, "AgentNotch", "Hook removal failed: " + ex.Message, WinForms.ToolTipIcon.Error);
            }
        };
        cursorInstall.Click += (_, _) =>
        {
            try
            {
                var helper = ResolveHelperPath()
                    ?? throw new InvalidOperationException("agentnotch-hook.exe not found next to the app.");
                CursorInstaller.Install(helper);
                _tray!.ShowBalloonTip(4000, "AgentNotch",
                    "Cursor hooks installed. Shell and MCP approvals will route to the notch.",
                    WinForms.ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                AppLog.Write("cursor install failed: " + ex);
                _tray!.ShowBalloonTip(4000, "AgentNotch", "Cursor hook install failed: " + ex.Message, WinForms.ToolTipIcon.Error);
            }
        };
        cursorUninstall.Click += (_, _) =>
        {
            try
            {
                CursorInstaller.Uninstall();
                _tray!.ShowBalloonTip(4000, "AgentNotch", "Cursor hooks removed.", WinForms.ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                AppLog.Write("cursor uninstall failed: " + ex);
                _tray!.ShowBalloonTip(4000, "AgentNotch", "Cursor hook removal failed: " + ex.Message, WinForms.ToolTipIcon.Error);
            }
        };
        logs.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start("explorer.exe", AppLog.LogDirectory); }
            catch (Exception ex) { AppLog.Write("open logs failed: " + ex.Message); }
        };
        quit.Click += (_, _) =>
        {
            _explicitQuit = true;
            _exitReason = "tray-quit";
            Shutdown();
        };

        var state = ClaudeCodeInstaller.GetState();
        install.Text = state == InstallState.Installed ? "Reinstall Claude Code hooks" : "Install Claude Code hooks";

        // Cursor is optional: greyed out, and labelled, when it isn't on this machine.
        bool cursorPresent = CursorInstaller.IsCursorInstalled();
        var cursorState = cursorPresent ? CursorInstaller.GetState() : InstallState.NotInstalled;
        cursorInstall.Text = !cursorPresent ? "Install Cursor hooks (Cursor not detected)"
            : cursorState == InstallState.Installed ? "Reinstall Cursor hooks"
            : "Install Cursor hooks";
        cursorInstall.Enabled = cursorPresent;
        cursorUninstall.Enabled = cursorPresent;
        AppLog.Write($"tray: cursor detected={cursorPresent} hooks={cursorState}");

        menu.Items.Add(showNotch);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(install);
        menu.Items.Add(uninstall);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(cursorInstall);
        menu.Items.Add(cursorUninstall);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(logs);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(quit);
        _tray.ContextMenuStrip = menu;
    }

    // ---- demo / UI self-test ----

    private void RunDemo(string[] args)
    {
        string? shotBase = null, scene = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--shot") shotBase = args[i + 1];
            else if (args[i] == "--scene") scene = args[i + 1];
        }

        // A deterministic live-agent scene for checking the buddy animation.
        var buddyScene = scene?.Replace("-hover", "");
        if (buddyScene is "working" or "thinking" or "executing" or "asking" or "complete" or "approval")
        {
            _vm!.UpdateSessions(new[] {
                new AgentSession { SessionId = "buddy-demo", Agent = "codex",
                    ProjectName = "notch",
                    Status = buddyScene == "asking" ? SessionStatus.NeedsYou
                        : buddyScene == "complete" ? SessionStatus.Finished : SessionStatus.Working,
                    CurrentTool = buddyScene == "executing" ? "exec_command" : null,
                    StartedUtc = DateTime.UtcNow, LastActivityUtc = DateTime.UtcNow }
            });
            if (buddyScene == "approval")
                _vm.AddHookRequest(DemoRequest(Proto.ModePermissionRequest, new {
                    session_id = "buddy-demo", cwd = @"C:\work\notch",
                    tool_name = "Bash", tool_input = new { command = "dotnet build" }
                }));
            _vm.SetHover(scene!.EndsWith("-hover"));
            if (shotBase is null) return;
            _overlay!.IsHitTestVisible = false;
            RunBuddyFramesAsync(shotBase + "-" + scene);
            return;
        }

        // --scene sessions: the populated list — the surface the agent marks actually
        // live on, and the only one none of the other scenes covered.
        if (scene == "sessions")
        {
            DemoSessions();
            _vm!.SetHover(true);
            if (shotBase is null) return;
            _overlay!.IsHitTestVisible = false;
            RunSceneShotAsync(shotBase + "-sessions.png");
            return;
        }

        // --scene empty|settings|idle: states to verify without live agents.
        if (scene is "empty" or "settings" or "idle")
        {
            _vm!.UpdateSessions(Array.Empty<Sessions.AgentSession>());
            if (scene == "settings") _vm.ToggleSettings();
            else if (scene == "empty") _vm.SetHover(true);
            else _vm.SetHover(false);
            if (shotBase is null) return;
            _overlay!.IsHitTestVisible = false;
            RunSceneShotAsync(shotBase + "-" + scene + ".png");
            return;
        }

        DemoSessions();
        var now = DateTime.UtcNow;
        _vm!.UpdateUsage(new[]
        {
            new UsageWindow("5h", 20, now.AddHours(2).AddMinutes(14)),
            new UsageWindow("7d", 4, now.AddDays(6).AddHours(14)),
        });

        _vm.AddHookRequest(DemoRequest(Proto.ModePermissionRequest, new
        {
            session_id = "s1",
            cwd = @"C:\work\api",
            permission_mode = "default",
            hook_event_name = "PermissionRequest",
            tool_name = "Edit",
            tool_input = new
            {
                file_path = @"C:\work\api\src\auth\middleware.ts",
                old_string = "const verify = (token) =>\n  jwt.verify(token);",
                new_string = "const verify = (token) => {\n  if (!token) throw new\n    AuthError('missing');\n  return jwt.verify(token);\n}",
            },
        }));
        _vm.AddHookRequest(DemoRequest(Proto.ModeAskUserQuestion, new
        {
            session_id = "s1",
            cwd = @"C:\work\api",
            hook_event_name = "PreToolUse",
            tool_name = "AskUserQuestion",
            tool_input = new
            {
                questions = new object[]
                {
                    new
                    {
                        question = "Which deployment target?",
                        header = "Deploy",
                        multiSelect = false,
                        options = new object[]
                        {
                            // Deliberately long: the confirmation flash has to survive a
                            // real-world answer on a 248px pill, not just the word "Production".
                            new { label = "Backend'e hasSupported ekle (önerilen)", description = "Deploy to the live environment" },
                            new { label = "Staging", description = "Deploy to staging first" },
                            new { label = "Local only", description = "Skip remote deploy" },
                        },
                    },
                },
            },
        }));

        if (shotBase is null) return; // interactive demo: leave it on screen

        // Deterministic: the physical cursor must not create hover state mid-run.
        _overlay!.IsHitTestVisible = false;

        RunShotSequenceAsync(shotBase);
    }

    /// <summary>The fake session list both the scripted run and --scene sessions use.</summary>
    private void DemoSessions()
    {
        var now = DateTime.UtcNow;
        _vm!.UpdateSessions(new[]
        {
            new Sessions.AgentSession
            {
                SessionId = "s1", Agent = "claude", Model = "Claude 3.7", Cwd = @"C:\work\api", ProjectName = "api",
                Entrypoint = "cli", Pid = 1111, HostLabel = "Windows Terminal", Status = Sessions.SessionStatus.NeedsYou,
                CurrentTool = "Bash", CurrentTarget = "Deploy the API to staging", LastMessage = "Which deployment target?",
                TokensSpent = 48_200, StartedUtc = now.AddMinutes(-27), LastActivityUtc = now,
                Usage = new[] { new UsageWindow("5h", 20, now.AddHours(2).AddMinutes(14)), new UsageWindow("7d", 4, now.AddDays(6).AddHours(14)) },
            },
            new Sessions.AgentSession
            {
                SessionId = "s2", Agent = "codex", Model = "GPT-5.3-codex", Cwd = @"C:\work\shop", ProjectName = "shop",
                HostLabel = "Cursor", Status = Sessions.SessionStatus.Working, CurrentTool = "exec_command",
                LastMessage = "Running exec_command", StartedUtc = now.AddHours(-1), LastActivityUtc = now,
                Usage = new[] { new UsageWindow("5h", 45, now.AddHours(3).AddMinutes(20)), new UsageWindow("7d", 18, now.AddDays(5)) },
            },
            new Sessions.AgentSession
            {
                SessionId = "s3b", Agent = "cursor", Model = "Sonnet 3.5", Host = "cursor", HostLabel = "Cursor",
                Cwd = @"C:\work\web", ProjectName = "web", Status = Sessions.SessionStatus.Working,
                CurrentTool = "Edit", CurrentTarget = "Editing schema.prisma", TokensSpent = 1_240_000,
                LastMessage = "Editing schema.prisma", StartedUtc = now.AddMinutes(-9), LastActivityUtc = now,
                Usage = new[] { new UsageWindow("Auto", 12, now.AddDays(12)), new UsageWindow("Plan", 45, now.AddDays(12)) },
            },
            new Sessions.AgentSession
            {
                SessionId = "s4", Agent = "antigravity", Model = "Gemini 3.8 Flash", HostLabel = "Antigravity",
                Cwd = @"C:\work\bot", ProjectName = "bot", Status = Sessions.SessionStatus.Finished,
                LastMessage = "Task complete.", StartedUtc = now.AddHours(-5), LastActivityUtc = now,
                Usage = new[] { new UsageWindow("5h", 35, now.AddHours(4)), new UsageWindow("7d", 8, now.AddDays(6)) },
            },
            new Sessions.AgentSession
            {
                SessionId = "s5", Agent = "opencode", Model = "deepseek-v4.1-flash", HostLabel = null,
                Cwd = @"C:\work\core", ProjectName = "core", Status = Sessions.SessionStatus.Working,
                CurrentTool = "read_file", CurrentTarget = "Reading config.json", TokensSpent = 16_400,
                LastMessage = "Reading config.json", StartedUtc = now.AddMinutes(-15), LastActivityUtc = now,
                Usage = new[] { new UsageWindow("ctx", 24, null) },
            },
        });
    }

    private async void RunSceneShotAsync(string path)
    {
        try
        {
            await Task.Delay(1400); // let the panel finish opening
            SaveShot(_overlay!, path);
        }
        catch (Exception ex) { AppLog.Write("demo scene shot failed: " + ex); }
        Shutdown();
    }

    private async void RunBuddyFramesAsync(string path)
    {
        try
        {
            // Separate frames verify choreography rather than only a static pose.
            for (int frame = 1; frame <= 3; frame++)
            {
                await Task.Delay(650);
                SaveShot(_overlay!, path + "-" + frame + ".png");
            }
        }
        catch (Exception ex) { AppLog.Write("buddy demo failed: " + ex); }
        Shutdown();
    }

    private async void RunShotSequenceAsync(string shotBase)
    {
        try
        {
            await Task.Delay(1400);
            SaveShot(_overlay!, shotBase + "-1-permission.png");
            _vm!.AllowActive(); // → question becomes active

            await Task.Delay(1400);
            SaveShot(_overlay!, shotBase + "-2-question.png");
            _vm!.ChooseOption(0); // → queue empty → collapse starts immediately

            await Task.Delay(700); // the answer confirmation is showing by now
            SaveShot(_overlay!, shotBase + "-3-toast.png");

            await Task.Delay(2200); // toast expired -> normal collapsed pill
            SaveShot(_overlay!, shotBase + "-4-collapsed.png");
        }
        catch (Exception ex)
        {
            AppLog.Write("demo shot failed: " + ex);
        }
        Shutdown();
    }

    private static HookRequest DemoRequest(string mode, object hookEvent) => new(
        new PipeEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Mode = mode,
            ExpectsReply = true,
            Event = System.Text.Json.JsonSerializer.SerializeToElement(hookEvent),
        },
        responder: null);

    private static void SaveShot(System.Windows.Window w, string path)
    {
        int width = Math.Max(1, (int)Math.Ceiling(w.ActualWidth));
        int height = Math.Max(1, (int)Math.Ceiling(w.ActualHeight));
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
            width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(w);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
        AppLog.Write("demo shot saved: " + path);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Write($"exiting (code={e.ApplicationExitCode}, explicit={_explicitQuit}, reason={_exitReason})");
        try
        {
            _cts.Cancel();
            if (_overlay is not null)
            {
                _overlay.AllowClose = true;
                _overlay.TaskbarRecreated -= RefreshTrayVisibility;
                try { _overlay.Close(); } catch { }
            }
            _hotkeys?.Dispose();
            _pipe?.Dispose();
            _watcher?.Dispose();
            _usage?.Dispose();
            try { _restoreWait?.Unregister(null); } catch { }
            try { _restoreEvent?.Dispose(); } catch { }
            if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
            try { _instanceMutex?.ReleaseMutex(); } catch { }
            _instanceMutex?.Dispose();
        }
        catch (Exception ex) { AppLog.Write("exit cleanup: " + ex.Message); }
        base.OnExit(e);
    }
}
