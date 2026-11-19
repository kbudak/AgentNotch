using System.Windows.Media.Animation;

namespace AgentNotch.App.UI;

/// <summary>
/// A pixel-dot loading spinner: twelve square dots on a ring whose opacity chases
/// around so a bright head appears to orbit. The dots never move, so the pixels
/// stay crisp; the motion is pure opacity, so it reads smooth. Colour comes from
/// <see cref="System.Windows.Controls.Control.Foreground"/>.
/// The storyboard only runs while loaded AND visible so collapsed instances never
/// trigger layered-window repaints.
/// </summary>
public partial class PixelSpinner
{
    private readonly Storyboard? _storyboard;
    private bool _running;

    public PixelSpinner()
    {
        InitializeComponent();
        _storyboard = TryFindResource("SpinStoryboard") as Storyboard;
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
