using System.Windows;
using System.Windows.Interop;
using AgentNotch.App.Diagnostics;
using AgentNotch.App.Interop;
using AgentNotch.App.ViewModels;
using Microsoft.Win32;

namespace AgentNotch.App.UI;

/// <summary>
/// The frameless, non-activating, always-on-top notch window. It is pinned
/// flush to the top-center of the primary screen and recenters itself whenever
/// its size or the display configuration changes.
/// </summary>
public partial class OverlayWindow
{
    public OverlayWindow()
    {
        InitializeComponent();

        SizeChanged += (_, _) => Recenter();
        Loaded += (_, _) => Recenter();
        MouseEnter += (_, _) => (DataContext as MainViewModel)?.SetHover(true);
        MouseLeave += (_, _) => (DataContext as MainViewModel)?.SetHover(false);

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Closed += (_, _) => SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
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
            Left = Math.Round((screenWidth - ActualWidth) / 2.0);
            Top = 0;
        }
        catch (Exception ex)
        {
            AppLog.Write("overlay recenter: " + ex.Message);
        }
    }
}
