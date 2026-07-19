using System.Windows;
using System.Windows.Input;
using AgentNotch.App.Diagnostics;
using AgentNotch.App.Jump;
using AgentNotch.App.Sessions;

namespace AgentNotch.App.UI;

/// <summary>Live session list. Clicking a row jumps to the terminal/editor
/// window hosting that agent session.</summary>
public partial class SessionsView
{
    public SessionsView()
    {
        InitializeComponent();
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
        catch (Exception ex)
        {
            AppLog.Write("jump click: " + ex.Message);
        }
    }
}
