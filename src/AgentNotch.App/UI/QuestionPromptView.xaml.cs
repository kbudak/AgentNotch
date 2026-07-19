using System.Windows;
using AgentNotch.App.Diagnostics;
using AgentNotch.App.ViewModels;

namespace AgentNotch.App.UI;

/// <summary>AskUserQuestion card. DataContext is the active PromptViewModel;
/// clicking an option answers with that option's label (single-select MVP).</summary>
public partial class QuestionPromptView
{
    public QuestionPromptView()
    {
        InitializeComponent();
    }

    private MainViewModel? MainVm => Window.GetWindow(this)?.DataContext as MainViewModel;

    private void OnOption(object sender, RoutedEventArgs e)
    {
        try
        {
            if ((sender as FrameworkElement)?.DataContext is QuestionOption option)
                MainVm?.ChooseOption(option.Index);
        }
        catch (Exception ex)
        {
            AppLog.Write("option click: " + ex.Message);
        }
    }

    private void OnPass(object sender, RoutedEventArgs e)
    {
        try { MainVm?.PassActive(); }
        catch (Exception ex) { AppLog.Write("pass click: " + ex.Message); }
    }
}
