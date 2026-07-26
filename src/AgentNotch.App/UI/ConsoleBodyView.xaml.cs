using System.Windows;
using System.Windows.Input;
using AgentNotch.App.Diagnostics;
using AgentNotch.App.Jump;
using AgentNotch.App.Sessions;
using AgentNotch.App.ViewModels;

namespace AgentNotch.App.UI;

/// <summary>
/// The console theme's expanded panel. DataContext is the MainViewModel; the
/// prompt block re-scopes to ActivePrompt, so every handler resolves the main
/// view model from the hosting window rather than from its own DataContext.
/// </summary>
public partial class ConsoleBodyView
{
    public ConsoleBodyView()
    {
        InitializeComponent();
    }

    private MainViewModel? MainVm => Window.GetWindow(this)?.DataContext as MainViewModel;

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        try { MainVm?.ToggleSettings(); }
        catch (Exception ex) { AppLog.Write("console settings click: " + ex.Message); }
    }

    private void OnSessionRowClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if ((sender as FrameworkElement)?.DataContext is AgentSession session)
            {
                bool activated = TerminalJump.FocusSession(session);
                if (!activated) AppLog.Write("jump: no window activated for session");
            }
        }
        catch (Exception ex) { AppLog.Write("console jump click: " + ex.Message); }
    }

    private void OnAllow(object sender, RoutedEventArgs e)
    {
        try { MainVm?.AllowActive(); }
        catch (Exception ex) { AppLog.Write("console allow: " + ex.Message); }
    }

    private void OnDeny(object sender, RoutedEventArgs e)
    {
        try { MainVm?.DenyActive(); }
        catch (Exception ex) { AppLog.Write("console deny: " + ex.Message); }
    }

    private void OnPass(object sender, RoutedEventArgs e) => Pass();

    /// <summary>The typed-answer row is a Border, not a Button — same action.</summary>
    private void OnPassBorder(object sender, MouseButtonEventArgs e) => Pass();

    private void Pass()
    {
        try { MainVm?.PassActive(); }
        catch (Exception ex) { AppLog.Write("console pass: " + ex.Message); }
    }

    private void OnOption(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if ((sender as FrameworkElement)?.DataContext is QuestionOption option)
                MainVm?.ChooseOption(option.Index);
        }
        catch (Exception ex) { AppLog.Write("console option click: " + ex.Message); }
    }
}
