using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AgentNotch.App.Diagnostics;
using AgentNotch.App.Interop;
using AgentNotch.App.Jump;
using AgentNotch.App.ViewModels;
using Microsoft.Win32;

namespace AgentNotch.App.UI;

/// <summary>
/// The frameless, non-activating, always-on-top notch window.
///
/// The window itself is a FIXED-size fully transparent surface pinned to the
/// top-center of the primary screen — the HWND never resizes, so the
/// expand/collapse animation is pure composition and stays smooth. The panel
/// opens "dynamic island"-style: the curtain (RevealHost.Height) and the panel
/// width animate together while the content slides down and fades in — the
/// content is never scaled, so text keeps its shape. All animations are
/// To-only, which makes quick hover in/out reversals continue from the
/// current value instead of jumping.
/// </summary>
public partial class OverlayWindow
{
    // Geometry is per theme. The island is a 248px pill that opens to a 576px
    // capsule; the console is a status LINE that fits its own text and opens to the
    // handoff's 812px panel, which needs a wider surface to sit in.
    private const double IslandCollapsedWidth = 248; // matches OverlayWindow.xaml NotchPanel.Width
    private const double MinimalCollapsedWidth = 156;
    private const double IslandExpandedWidth = 576;  // 560 content + 8+8 panel padding
    private const double ConsoleExpandedWidth = 700; // 700 content, no panel padding
    private const double IslandWindowWidth = 640;
    private const double ConsoleWindowWidth = 780;   // 700 + the 28+28 shadow margins, rounded up

    // AllowsTransparency makes this a layered window: WPF hands the ENTIRE window
    // surface to UpdateLayeredWindow on every frame, so cost scales with the
    // window's AREA, not with what actually changed — during an open/close that is
    // a full-surface CPU blit per frame, which is what made the morph stutter. So
    // the surface is sized to the content it is about to hold rather than to a
    // worst case, and it is still only ever resized at the ends, never mid-flight.
    private const double MaxWindowHeight = 800;
    // What the surface needs on top of the curtain: the 32px shadow gutter, plus
    // (island only) the quota/gear header row and the panel's own padding.
    private double WindowChrome => IsConsole ? 44 : IsMinimal ? 184 : 104;
    private const double IslandCollapsedHeight = 108; // pill (34) + room for the drop shadow
    private const double MinimalCollapsedHeight = 88;
    private const double ConsoleCollapsedHeight = 80; // the line is only 26 tall

    private MainViewModel? _vm;
    private bool _open;
    private bool _opening;               // suppress content-driven re-targeting mid-open
    private double _consoleLineWidth = 240; // the collapsed line's natural width, for a smooth close
    private IntPtr _hwnd;
    private HwndSource? _hwndSource;
    private uint _taskbarCreatedMsg;
    private readonly DispatcherTimer _healthTimer;

    /// <summary>Only set true by App.OnExit — blocks accidental Alt+F4 / WM_CLOSE from stranding the app invisible.</summary>
    public bool AllowClose { get; set; }

    /// <summary>Fired when explorer.exe restarts so the tray icon can be re-registered.</summary>
    public event Action? TaskbarRecreated;

    private bool IsConsole => ThemeManager.Normalize(_vm?.Theme) == ThemeManager.Console;
    private bool IsMinimal => ThemeManager.Normalize(_vm?.Theme) == ThemeManager.Minimal;
    private double ExpandedPanelWidth => IsConsole ? ConsoleExpandedWidth : IslandExpandedWidth;

    /// <summary>
    /// Collapsed width. The island pill is a fixed 248 and morphs; the console line
    /// is "as wide as its own text and marks", which no animation target can know
    /// ahead of time — NaN hands that width back to layout.
    /// </summary>
    private double CollapsedPanelWidth => IsConsole ? double.NaN : IsMinimal ? MinimalCollapsedWidth : IslandCollapsedWidth;

    private double CollapsedWindowHeight => IsConsole ? ConsoleCollapsedHeight : IsMinimal ? MinimalCollapsedHeight : IslandCollapsedHeight;

    public OverlayWindow()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            ApplyThemeGeometry();
            if (_open) AnimateOpen();
        };
        MouseEnter += (_, _) => _vm?.SetHover(true);
        MouseLeave += (_, _) => _vm?.SetHover(false);
        DataContextChanged += OnDataContextChanged;
        ExpandedContent.SizeChanged += OnContentSizeChanged;
        ConsoleLine.SizeChanged += (_, e) =>
        {
            if (double.IsNaN(NotchPanel.Width) && e.NewSize.Width > 1)
                _consoleLineWidth = Math.Ceiling(e.NewSize.Width);
        };

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        StateChanged += OnStateChanged;

        // Lightweight self-healing check: DWM restarts, fullscreen games/videos, Win+D,
        // or monitor sleep/wake can strip WS_EX_TOPMOST or hide the window without firing
        // DisplaySettingsChanged. Does nothing unless state actually drifted.
        _healthTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(10),
        };
        _healthTimer.Tick += (_, _) => VerifyOverlayHealth();
        _healthTimer.Start();

        Closed += (_, _) =>
        {
            _healthTimer.Stop();
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            StateChanged -= OnStateChanged;
            try { _hwndSource?.RemoveHook(WndProc); } catch { }
            _hwndSource = null;
            if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            AppLog.Write("overlay: blocked unexpected window close");
            EnsureVisibleAndTopmost("blocked-close");
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            Focusable = false;
            _hwnd = new WindowInteropHelper(this).Handle;
            _taskbarCreatedMsg = NativeMethods.RegisterWindowMessage("TaskbarCreated");
            _hwndSource = HwndSource.FromHwnd(_hwnd);
            _hwndSource?.AddHook(WndProc);
            NativeMethods.MakeOverlay(_hwnd);
        }
        catch (Exception ex)
        {
            AppLog.Write("overlay MakeOverlay: " + ex.Message);
        }
        Recenter();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_DISPLAYCHANGE || msg == NativeMethods.WM_DPICHANGED)
        {
            ScheduleRecovery("display-change");
        }
        else if (_taskbarCreatedMsg != 0 && (uint)msg == _taskbarCreatedMsg)
        {
            ScheduleRecovery("taskbar-recreated");
            try { TaskbarRecreated?.Invoke(); } catch { }
        }
        return IntPtr.Zero;
    }

    /// <summary>Brings the notch back if hidden, minimized, off-screen, or knocked out of topmost Z-order.</summary>
    public void EnsureVisibleAndTopmost(string? reason = null)
    {
        try
        {
            if (AllowClose) return;
            if (WindowState != WindowState.Normal)
                WindowState = WindowState.Normal;
            if (!IsVisible)
                Show();
            if (Opacity < 1)
                Opacity = 1;
            Topmost = true;
            Recenter();
            if (_hwnd != IntPtr.Zero)
                NativeMethods.MakeOverlay(_hwnd);
            if (!string.IsNullOrEmpty(reason))
                AppLog.Write($"overlay: restored ({reason})");
        }
        catch (Exception ex)
        {
            AppLog.Write("overlay restore failed: " + ex.Message);
        }
    }

    private void VerifyOverlayHealth()
    {
        try
        {
            if (AllowClose || !IsLoaded) return;
            bool drifted = !IsVisible
                           || WindowState != WindowState.Normal
                           || !double.IsFinite(Left) || !double.IsFinite(Top)
                           || (_hwnd != IntPtr.Zero && (!NativeMethods.IsWindowVisible(_hwnd) || !NativeMethods.HasTopmostStyle(_hwnd)));
            if (drifted)
                EnsureVisibleAndTopmost("health-check");
        }
        catch (Exception ex)
        {
            AppLog.Write("overlay health check: " + ex.Message);
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (!AllowClose && WindowState != WindowState.Normal)
            EnsureVisibleAndTopmost("state-changed");
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            ScheduleRecovery("power-resume");
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock
            or SessionSwitchReason.ConsoleConnect
            or SessionSwitchReason.RemoteConnect)
            ScheduleRecovery("session-" + e.Reason);
    }

    private void ScheduleRecovery(string reason)
    {
        try
        {
            if (AllowClose) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, () => EnsureVisibleAndTopmost(reason));
        }
        catch (Exception ex)
        {
            AppLog.Write("overlay schedule recovery: " + ex.Message);
        }
    }

    // ------------------------------------------------------------ view-model

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as MainViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
        SyncExpansion();
        Recenter(); // apply the saved notch position now the view model is available
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsExpanded))
            SyncExpansion();
        else if (e.PropertyName == nameof(MainViewModel.Position))
            Recenter();
        else if (e.PropertyName == nameof(MainViewModel.Theme))
            ApplyThemeGeometry();
        else if (e.PropertyName == nameof(MainViewModel.ActivePrompt) && _vm?.ActivePrompt is not null)
            EnsureVisibleAndTopmost("prompt-arrived");
    }

    /// <summary>
    /// Re-sizes the surface and panel for the active layout. Console is 812 wide;
    /// Island and Glass share the 576-wide rounded layout.
    /// </summary>
    private void ApplyThemeGeometry()
    {
        try
        {
            Width = IsConsole ? ConsoleWindowWidth : IslandWindowWidth;
            if (IsLoaded)
            {
                UpdateLayout(); // the other theme's tree has to lay out before it can be measured
                NotchPanel.BeginAnimation(WidthProperty, null); // drop any in-flight width animation
                NotchPanel.Width = _open ? ExpandedPanelWidth : CollapsedPanelWidth;
                if (_open)
                {
                    RevealHost.BeginAnimation(HeightProperty, null);
                    RevealHost.Height = ContentHeight();
                    Height = Math.Min(MaxWindowHeight, ContentHeight() + WindowChrome);
                }
                else Height = CollapsedWindowHeight;
            }
            Recenter();
        }
        catch (Exception ex) { AppLog.Write("overlay theme geometry: " + ex.Message); }
    }

    private void SyncExpansion()
    {
        bool open = _vm?.IsExpanded == true;
        if (open == _open) return;
        _open = open;
        if (!IsLoaded) return; // the Loaded handler animates the initial state
        if (open) AnimateOpen();
        else AnimateClose();
    }

    /// <summary>Content grew or shrank while open (prompt card swapped) — morph the curtain.</summary>
    private void OnContentSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        // HEIGHT only. The panel's width animates while opening, and ExpandedContent
        // is centre-aligned, so its arranged width changes every frame — reacting to
        // that re-started the curtain from wherever it had got to, on every frame,
        // and it crawled to 4px instead of opening.
        if (!_open || !IsLoaded || !e.HeightChanged) return;
        double content = ContentHeight();
        GrowWindowFor(content);
        // Mid-open, re-target on the open's own duration and curve: swapping in a
        // shorter animation halfway through is what read as a hitch.
        Animate(RevealHost, HeightProperty, content, _opening ? 240 : 200, Ease.Morph);
    }

    /// <summary>Keeps the surface just big enough for the panel it is about to show.</summary>
    private void GrowWindowFor(double contentHeight)
    {
        double wanted = Math.Min(MaxWindowHeight, Math.Ceiling(contentHeight) + WindowChrome);
        if (wanted > Height) Height = wanted;
    }

    // -------------------------------------------------------------- settings

    private void OnSettingsClick(object sender, RoutedEventArgs e) => _vm?.ToggleSettings();

    private void OnBuddyClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_open || _vm?.LeadSession is not { } session) return;
        try { TerminalJump.FocusSession(session); }
        catch (Exception ex) { AppLog.Write("buddy jump: " + ex.Message); }
    }

    // ------------------------------------------------------------- animation

    private static class Ease
    {
        // The island morph: cubic ease-in-out — smooth in and out but quicker off the
        // mark than a higher-power curve, so it feels fast without snapping.
        public static readonly IEasingFunction Morph = new CubicEase { EasingMode = EasingMode.EaseInOut };
        // Gentle decel for the content fade.
        public static readonly IEasingFunction Out = new SineEase { EasingMode = EasingMode.EaseOut };
    }

    // Width and height share one duration and one curve, and the content neither
    // travels nor waits — the panel opens as one shape in both axes at once.
    private void AnimateOpen()
    {
        // Pin the width BEFORE anything can lay the panel out again. The collapsed
        // console panel is auto-sized, so the moment the 700-wide body becomes
        // visible any layout pass snaps the panel straight to 700 — and the width
        // animation below then had nothing left to travel. That snap, followed by
        // the curtain, is what read as "widens first, then opens downward".
        if (double.IsNaN(NotchPanel.Width)) NotchPanel.Width = _consoleLineWidth;

        RevealHost.Visibility = Visibility.Visible; // before measuring — a collapsed element measures 0
        ExpandedContent.UpdateLayout();             // ...and a just-shown subtree measures short until it arranges
        double content = ContentHeight();
        Height = Math.Min(MaxWindowHeight, Math.Ceiling(content) + WindowChrome);

        _opening = true;
        var curtain = new DoubleAnimation(content, TimeSpan.FromMilliseconds(240)) { EasingFunction = Ease.Morph };
        curtain.Completed += (_, _) => _opening = false;
        RevealHost.BeginAnimation(HeightProperty, curtain, HandoffBehavior.SnapshotAndReplace);

        Animate(NotchPanel, WidthProperty, ExpandedPanelWidth, 240, Ease.Morph);
        // No delay and no travel: the panel growing IS the animation, so the content
        // has to already be there as the curtain uncovers it.
        Animate(ExpandedContent, OpacityProperty, 1, 140, Ease.Out);
    }

    private void AnimateClose()
    {
        // Height 0 + ClipToBounds only HIDES the panel — WPF still lays it out and
        // re-rasterizes it every frame, and this is a layered window (software
        // compositing), so the row spinners kept burning CPU while collapsed.
        // Collapsing once the curtain is shut takes the whole subtree out of the
        // render pass; the notch spends nearly all its life in this state.
        _opening = false;
        var height = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)) { EasingFunction = Ease.Morph };
        height.Completed += (_, _) =>
        {
            if (_open) return; // re-opened mid-close — leave the surface alone
            RevealHost.Visibility = Visibility.Collapsed;
            Height = CollapsedWindowHeight;
            // Hand the console line's width back to layout only once the morph has
            // landed on it — dropping to auto up front made the panel snap instead
            // of close.
            if (IsConsole)
            {
                NotchPanel.BeginAnimation(WidthProperty, null);
                NotchPanel.Width = double.NaN;
            }
        };
        RevealHost.BeginAnimation(HeightProperty, height, HandoffBehavior.SnapshotAndReplace);

        Animate(NotchPanel, WidthProperty, IsConsole ? _consoleLineWidth : CollapsedPanelWidth, 200, Ease.Morph);
        // Fade the content out a touch faster than the shape closes, so it's gone
        // before the panel finishes shrinking (also masks the prompt card unloading).
        Animate(ExpandedContent, OpacityProperty, 0, 110, Ease.Out);
    }

    // Unconstrained: each theme's body carries its own width (island 560, console
    // 812), so the measure must not impose one.
    private double ContentHeight()
    {
        ExpandedContent.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double h = ExpandedContent.DesiredSize.Height;
        return double.IsFinite(h) && h > 0 ? Math.Ceiling(h) : 0;
    }

    private static void Animate(FrameworkElement target, DependencyProperty property,
                                double to, int ms, IEasingFunction ease, int beginMs = 0)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = ease,
            BeginTime = TimeSpan.FromMilliseconds(beginMs),
        };
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    // ------------------------------------------------------------- placement

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        ScheduleRecovery("display-settings-changed");
    }

    private void Recenter()
    {
        try
        {
            double screenWidth = SystemParameters.PrimaryScreenWidth;
            if (!double.IsFinite(screenWidth) || screenWidth < 200) return;
            // The panel is centred inside the fixed-width window, so aligning the
            // window's centre to a fraction of the screen places the pill there.
            double frac = _vm?.Position switch { "left" => 0.20, "right" => 0.80, _ => 0.50 };
            Left = Math.Round(screenWidth * frac - Width / 2.0);
            Top = 0; // the notch hangs off the top edge, flush with it
        }
        catch (Exception ex)
        {
            AppLog.Write("overlay recenter: " + ex.Message);
        }
    }
}
