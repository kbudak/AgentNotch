using System.Windows.Media.Animation;

namespace AgentNotch.App.UI;

/// <summary>Five agent-coloured level bars — the console theme's empty state.
/// Only animates while loaded and visible so hidden themes never tick WPF.</summary>
public partial class EqualiserBars
{
    private readonly Storyboard? _storyboard;
    private bool _running;

    public EqualiserBars()
    {
        InitializeComponent();
        _storyboard = TryFindResource("EqualiserStoryboard") as Storyboard;
        Loaded += (_, _) => SyncAnimation();
        Unloaded += (_, _) => StopAnimation();
        IsVisibleChanged += (_, _) => SyncAnimation();
    }

    private void SyncAnimation()
    {
        if (IsLoaded && IsVisible) StartAnimation();
        else StopAnimation();
    }

    private void StartAnimation()
    {
        if (_running || _storyboard is null) return;
        _running = true;
        _storyboard.Begin(this, isControllable: true);
    }

    private void StopAnimation()
    {
        if (!_running || _storyboard is null) return;
        _running = false;
        _storyboard.Stop(this);
    }
}
