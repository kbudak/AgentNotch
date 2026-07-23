namespace AgentNotch.App.UI;

/// <summary>
/// A pixel-dot loading spinner: eight square dots on a ring whose opacity chases
/// around so a bright head appears to orbit. The dots never move, so the pixels
/// stay crisp; the motion is pure opacity, so it reads smooth. Colour comes from
/// <see cref="System.Windows.Controls.Control.Foreground"/>.
/// </summary>
public partial class PixelSpinner
{
    public PixelSpinner()
    {
        InitializeComponent();
    }
}
