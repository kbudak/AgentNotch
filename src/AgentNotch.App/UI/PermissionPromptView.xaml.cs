using System.Windows;
using AgentNotch.App.Diagnostics;
using AgentNotch.App.ViewModels;

namespace AgentNotch.App.UI;

/// <summary>Permission card. DataContext is the active PromptViewModel; resolution
/// goes through the MainViewModel found on the hosting window.</summary>
public partial class PermissionPromptView
{
    public PermissionPromptView()
    {
        InitializeComponent();
    }

    private MainViewModel? MainVm => Window.GetWindow(this)?.DataContext as MainViewModel;

    private void OnAllow(object sender, RoutedEventArgs e)
    {
        try { MainVm?.AllowActive(); }
        catch (Exception ex) { AppLog.Write("allow click: " + ex.Message); }
    }

    private void OnDeny(object sender, RoutedEventArgs e)
    {
        try { MainVm?.DenyActive(); }
        catch (Exception ex) { AppLog.Write("deny click: " + ex.Message); }
    }

    private void OnPass(object sender, RoutedEventArgs e)
    {
        try { MainVm?.PassActive(); }
        catch (Exception ex) { AppLog.Write("pass click: " + ex.Message); }
    }
}
