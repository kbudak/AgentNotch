using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AgentNotch.App.Diagnostics;
using AgentNotch.App.Interop;
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
    private double WindowChrome => IsConsole ? 44 : 104;
    private const double IslandCollapsedHeight = 108; // pill (34) + room for the drop shadow
    private const double ConsoleCollapsedHeight = 80; // the line is only 26 tall

    private MainViewModel? _vm;
    private bool _open;
    private bool _opening;               // suppress content-driven re-targeting mid-open
    private double _consoleLineWidth = 240; // the collapsed line's natural width, for a smooth close

    private bool IsConsole => ThemeManager.Normalize(_vm?.Theme) == ThemeManager.Console;
    private double ExpandedPanelWidth => IsConsole ? ConsoleExpandedWidth : IslandExpandedWidth;

    /// <summary>
    /// Collapsed width. The island pill is a fixed 248 and morphs; the console line
    /// is "as wide as its own text and marks", which no animation target can know
    /// ahead of time — NaN hands that width back to layout.
    /// </summary>
    private double CollapsedPanelWidth => IsConsole ? double.NaN : IslandCollapsedWidth;

    private double CollapsedWindowHeight => IsConsole ? ConsoleCollapsedHeight : IslandCollapsedHeight;

    public OverlayWindow()
    {
        InitializeComponent();

        Loaded += (_, _) => { ApplyThemeGeometry(); if (_open) AnimateOpen(); };
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
        Closed += (_, _) =>
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            Focusable = false;
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.MakeOverlay(hwnd);
        }
        catch (Exception ex)
        {
            AppLog.Write("overlay MakeOverlay: " + ex.Message);
        }
        Recenter();
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

    }

    /// <summary>
    /// Re-sizes the surface and the panel for the active theme. The two themes do
    /// not merely repaint — the console panel is 812 wide against the island's 576,
    /// so the window itself has to grow and re-centre.
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
                }
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
        return Math.Ceiling(ExpandedContent.DesiredSize.Height);
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
        try { Dispatcher.BeginInvoke(Recenter); }
        catch (Exception ex) { AppLog.Write("overlay display change: " + ex.Message); }
    }

    private void Recenter()
    {
        try
        {
            double screenWidth = SystemParameters.PrimaryScreenWidth;
            // The panel is centred inside the fixed-width window, so aligning the
            // window's centre to a fraction of the screen places the pill there.
            double frac = _vm?.Position switch { "left" => 0.20, "right" => 0.80, _ => 0.50 };
            Left = Math.Round(screenWidth * frac - Width / 2.0);
            Top = 0;
        }
        catch (Exception ex)
        {
            AppLog.Write("overlay recenter: " + ex.Message);
        }
    }
}
