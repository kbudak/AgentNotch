using System.Globalization;
using System.Windows.Media;
using AgentNotch.App.UI;
using Xunit;

namespace AgentNotch.Tests;

/// <summary>
/// The 5h / 7d chips colour by how much of the window is LEFT. The converter is
/// fed percent USED, so the inversion is the easy thing to get backwards — these
/// pin the boundaries down. With no Application running, StatusBrushes falls back
/// to its literal colours, which is what the assertions compare.
/// </summary>
public class UsageSeverityTests
{
    private static string ColorOf(double usedPct)
    {
        var brush = (SolidColorBrush)new UsageSeverityBrushConverter()
            .Convert(usedPct, typeof(Brush), null, CultureInfo.InvariantCulture);
        return brush.Color.ToString(CultureInfo.InvariantCulture);
    }

    private const string Green = "#FF4ADE80";
    private const string Yellow = "#FFFACC15";
    private const string Orange = "#FFFB923C";
    private const string Red = "#FFF87171";

    [Theory]
    [InlineData(0, Green)]    // untouched — all of it left
    [InlineData(8, Green)]    // the "5h 8%" chip
    [InlineData(48, Green)]   // the "7d 48%" chip: just over half left
    [InlineData(50, Yellow)]  // half gone
    [InlineData(74, Yellow)]
    [InlineData(75, Orange)]  // a quarter left
    [InlineData(89, Orange)]
    [InlineData(90, Red)]     // a tenth left
    [InlineData(100, Red)]    // spent
    public void RampFollowsWhatIsLeft(double usedPct, string expected)
        => Assert.Equal(expected, ColorOf(usedPct));

    [Fact]
    public void RampOnlyEverDarkens()
    {
        // Walking usage upward must never step back toward green.
        var order = new[] { Green, Yellow, Orange, Red };
        int previous = 0;
        for (double used = 0; used <= 100; used += 1)
        {
            int stage = Array.IndexOf(order, ColorOf(used));
            Assert.True(stage >= previous, $"ramp went backwards at {used}% used");
            previous = stage;
        }
    }
}
