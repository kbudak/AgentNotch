using System.Windows;
using System.Windows.Threading;
using AgentNotch.App.Diagnostics;
using AgentNotch.App.Integration;
using AgentNotch.App.Ipc;
using AgentNotch.App.Sessions;
using AgentNotch.App.UI;
using AgentNotch.App.ViewModels;
using AgentNotch.Protocol;
using WinForms = System.Windows.Forms;

namespace AgentNotch.App;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private readonly CancellationTokenSource _cts = new();
    private PipeServer? _pipe;
    private SessionWatcher? _watcher;
    private OverlayWindow? _overlay;
    private MainViewModel? _vm;
    private HotkeyManager? _hotkeys;
    private WinForms.NotifyIcon? _tray;
    private string? _autoBehavior; // --auto allow|deny|pass: smoke-test mode, resolves prompts automatically

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Headless one-shot maintenance flags (usable while another instance runs).
        if (e.Args.Contains("--install-hooks") || e.Args.Contains("--uninstall-hooks"))
        {
            try
            {
                if (e.Args.Contains("--uninstall-hooks"))
                {
                    ClaudeCodeInstaller.Uninstall();
                    AppLog.Write("cli: hooks uninstalled");
                }
                else
                {
                    var helper = ResolveHelperPath()
                        ?? throw new InvalidOperationException("agentnotch-hook.exe not found next to the app.");
                    ClaudeCodeInstaller.Install(helper);
                    AppLog.Write("cli: hooks installed -> " + helper);
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
            _overlay = new OverlayWindow { DataContext = _vm };
            _overlay.Show();
            RunDemo(e.Args);
            return;
        }

        _instanceMutex = new Mutex(true, "AgentNotch.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            AppLog.Write("second instance — exiting");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, a) => { AppLog.Write("ui exception: " + a.Exception); a.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => AppLog.Write("fatal: " + a.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, a) => { AppLog.Write("task exception: " + a.Exception); a.SetObserved(); };

        for (int i = 0; i < e.Args.Length - 1; i++)
            if (e.Args[i] == "--auto")
                _autoBehavior = e.Args[i + 1];

        _vm = new MainViewModel();

        _overlay = new OverlayWindow { DataContext = _vm };
        _overlay.Show();
        _hotkeys = new HotkeyManager(_overlay, _vm);

        _pipe = new PipeServer();
        _pipe.RequestReceived += req => Dispatcher.BeginInvoke(DispatcherPriority.Normal, () => OnHookRequest(req));
        _pipe.Start(_cts.Token);

        _watcher = new SessionWatcher();
        _watcher.SessionsChanged += list => Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _vm!.UpdateSessions(list));
        _watcher.Start();

        CreateTray();
        AppLog.Write($"started (auto={_autoBehavior ?? "off"}, helper={ResolveHelperPath() ?? "NOT FOUND"})");
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
        var install = new WinForms.ToolStripMenuItem("Install Claude Code hooks");
        var uninstall = new WinForms.ToolStripMenuItem("Uninstall Claude Code hooks");
        var logs = new WinForms.ToolStripMenuItem("Open logs");
        var quit = new WinForms.ToolStripMenuItem("Quit AgentNotch");

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
        logs.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start("explorer.exe", AppLog.LogDirectory); }
            catch (Exception ex) { AppLog.Write("open logs failed: " + ex.Message); }
        };
        quit.Click += (_, _) => Shutdown();

        var state = ClaudeCodeInstaller.GetState();
        install.Text = state == InstallState.Installed ? "Reinstall Claude Code hooks" : "Install Claude Code hooks";

        menu.Items.Add(install);
        menu.Items.Add(uninstall);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(logs);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(quit);
        _tray.ContextMenuStrip = menu;
    }

    // ---- demo / UI self-test ----

    private void RunDemo(string[] args)
    {
        string? shotBase = null;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--shot")
                shotBase = args[i + 1];

        _vm!.UpdateSessions(new[]
        {
            new Sessions.AgentSession { SessionId = "s1", Cwd = @"C:\work\api", ProjectName = "api", Entrypoint = "cli", Pid = 1111, Status = Sessions.SessionStatus.NeedsYou, CurrentTool = "Bash", CurrentTarget = "npm run deploy", LastMessage = "Which deployment target?", TokensSpent = 48_200, LastActivityUtc = DateTime.UtcNow },
            new Sessions.AgentSession { SessionId = "s2", Agent = "codex", Model = "GPT-5.2", Cwd = @"C:\work\shop", ProjectName = "shop", Status = Sessions.SessionStatus.Working, CurrentTool = "exec_command", LastMessage = "Running exec_command", LastActivityUtc = DateTime.UtcNow },
            new Sessions.AgentSession { SessionId = "s3", Host = "cursor", Cwd = @"C:\work\web", ProjectName = "web", Entrypoint = "claude-vscode", Pid = 2222, Status = Sessions.SessionStatus.Working, CurrentTool = "Edit", CurrentTarget = @"src\db\schema.prisma", TokensSpent = 1_240_000, LastActivityUtc = DateTime.UtcNow },
            new Sessions.AgentSession { SessionId = "s4", Agent = "antigravity", Model = "Gemini", Cwd = @"C:\work\bot", ProjectName = "bot", Status = Sessions.SessionStatus.Finished, LastMessage = "Task complete.", LastActivityUtc = DateTime.UtcNow },
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
                            new { label = "Production", description = "Deploy to the live environment" },
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

            await Task.Delay(90); // mid-retract (exit anim: 180ms scale / 140ms fade)
            SaveShot(_overlay!, shotBase + "-3-collapsing.png");

            await Task.Delay(900);
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
        try
        {
            _cts.Cancel();
            _hotkeys?.Dispose();
            _pipe?.Dispose();
            _watcher?.Dispose();
            if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
            _instanceMutex?.Dispose();
        }
        catch (Exception ex) { AppLog.Write("exit cleanup: " + ex.Message); }
        base.OnExit(e);
    }
}
