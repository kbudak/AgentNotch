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
    private const double CollapsedPanelWidth = 280;
    private const double ExpandedPanelWidth = 576; // 560 content + 8+8 panel padding

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
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsExpanded))
            SyncExpansion();
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
            Animate(RevealHost, HeightProperty, ContentHeight(), 220, Ease.Out);
    }

    // -------------------------------------------------------------- settings

    private void OnSettingsClick(object sender, RoutedEventArgs e) => _vm?.ToggleSettings();

    private void OnToggleLaunchAtLogin(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.LaunchAtLogin = !_vm.LaunchAtLogin;
    }

    // ------------------------------------------------------------- animation

    private static class Ease
    {
        public static readonly IEasingFunction Out = new QuinticEase { EasingMode = EasingMode.EaseOut };
        public static readonly IEasingFunction Soft = new CubicEase { EasingMode = EasingMode.EaseInOut };
        public static readonly IEasingFunction In = new CubicEase { EasingMode = EasingMode.EaseIn };
    }

    private void AnimateOpen()
    {
        Animate(RevealHost, HeightProperty, ContentHeight(), 320, Ease.Out);
        Animate(NotchPanel, WidthProperty, ExpandedPanelWidth, 320, Ease.Out);
        Animate(ExpandedContent, OpacityProperty, 1, 220, Ease.Out, beginMs: 50);
        AnimateSlide(0, 320, Ease.Out);
    }

    private void AnimateClose()
    {
        Animate(RevealHost, HeightProperty, 0, 240, Ease.Soft);
        Animate(NotchPanel, WidthProperty, CollapsedPanelWidth, 240, Ease.Soft, beginMs: 40);
        // Fast-out fade: the content is mostly gone within ~60ms, which also masks
        // the prompt card unloading instantly when the queue empties mid-close.
        Animate(ExpandedContent, OpacityProperty, 0, 120, Ease.Out);
        AnimateSlide(-14, 240, Ease.Soft);
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
            Left = Math.Round((screenWidth - Width) / 2.0);
            Top = 0;
        }
        catch (Exception ex)
        {
            AppLog.Write("overlay recenter: " + ex.Message);
        }
    }
}
