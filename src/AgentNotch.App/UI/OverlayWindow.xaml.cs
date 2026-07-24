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
    private const double CollapsedPanelWidth = 248;
    private const double ExpandedPanelWidth = 576; // 560 content + 8+8 panel padding

    // AllowsTransparency makes this a layered window: WPF hands the ENTIRE window
    // surface to UpdateLayeredWindow on every frame, so idle cost scales with the
    // window's area, not with what actually changed. At 640x800 the collapsed
    // pill's spinner was repainting 2 MB per frame. The HWND still never resizes
    // *during* an animation — it is sized once at each end.
    private const double ExpandedWindowHeight = 800;
    private const double CollapsedWindowHeight = 120; // pill (38) + room for the drop shadow

    private MainViewModel? _vm;
    private bool _open;

    public OverlayWindow()
    {
        InitializeComponent();

        Loaded += (_, _) => { Recenter(); if (_open) AnimateOpen(); };
        MouseEnter += (_, _) => _vm?.SetHover(true);
        MouseLeave += (_, _) => _vm?.SetHover(false);
        DataContextChanged += OnDataContextChanged;
        ExpandedContent.SizeChanged += OnContentSizeChanged;

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
        if (_open && IsLoaded)
            Animate(RevealHost, HeightProperty, ContentHeight(), 200, Ease.Morph);
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

    // Width, height and slide share one duration and one curve so the panel moves as a
    // single smooth shape rather than three loosely-synced parts.
    private void AnimateOpen()
    {
        Height = ExpandedWindowHeight;              // grow the surface first, then animate inside it
        RevealHost.Visibility = Visibility.Visible; // before measuring — a collapsed element measures 0
        Animate(RevealHost, HeightProperty, ContentHeight(), 240, Ease.Morph);
        Animate(NotchPanel, WidthProperty, ExpandedPanelWidth, 240, Ease.Morph);
        Animate(ExpandedContent, OpacityProperty, 1, 190, Ease.Out, beginMs: 40);
        AnimateSlide(0, 240, Ease.Morph);
    }

    private void AnimateClose()
    {
        // Height 0 + ClipToBounds only HIDES the panel — WPF still lays it out and
        // re-rasterizes it every frame, and this is a layered window (software
        // compositing), so the row spinners kept burning CPU while collapsed.
        // Collapsing once the curtain is shut takes the whole subtree out of the
        // render pass; the notch spends nearly all its life in this state.
        var height = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)) { EasingFunction = Ease.Morph };
        height.Completed += (_, _) =>
        {
            if (_open) return; // re-opened mid-close — leave the surface alone
            RevealHost.Visibility = Visibility.Collapsed;
            Height = CollapsedWindowHeight;
        };
        RevealHost.BeginAnimation(HeightProperty, height, HandoffBehavior.SnapshotAndReplace);

        Animate(NotchPanel, WidthProperty, CollapsedPanelWidth, 200, Ease.Morph);
        // Fade the content out a touch faster than the shape closes, so it's gone
        // before the panel finishes shrinking (also masks the prompt card unloading).
        Animate(ExpandedContent, OpacityProperty, 0, 110, Ease.Out);
        AnimateSlide(-12, 200, Ease.Morph);
    }

    private double ContentHeight()
    {
        ExpandedContent.Measure(new Size(560, double.PositiveInfinity));
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

    private void AnimateSlide(double to, int ms, IEasingFunction ease)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };
        ContentSlide.BeginAnimation(TranslateTransform.YProperty, animation, HandoffBehavior.SnapshotAndReplace);
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
