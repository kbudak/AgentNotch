using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AgentNotch.App.Sessions;
using AgentNotch.App.UI;
using AgentNotch.App.Usage;
using AgentNotch.App.ViewModels;
using Xunit;

namespace AgentNotch.Tests;

/// <summary>
/// The console theme's own logic: the shell line it writes, which of the two view
/// trees is live, and the quota ramp — which is deliberately a different, coarser
/// ramp from the island theme's.
/// </summary>
public class ConsoleShellTests
{
    private static AgentSession Row(SessionStatus status, string agent = "claude") =>
        new() { SessionId = Guid.NewGuid().ToString(), Agent = agent, Status = status };

    private static MainViewModel With(params SessionStatus[] statuses)
    {
        var vm = new MainViewModel();
        vm.UpdateSessions(Array.ConvertAll(statuses, s => Row(s)));
        return vm;
    }

    [Fact]
    public void IdleShellSaysItIsWatching()
    {
        var vm = new MainViewModel();
        Assert.Equal("empty", vm.ShellState);
        Assert.Equal("watching · 0 agents", vm.Headline);
        Assert.False(vm.BlockedOnYou);
    }

    [Fact]
    public void EverythingFinishedIsDoneNotEmpty()
    {
        // The island pill's StateKind folds both into "idle"; the console has to
        // tell them apart to pick between ✓ and ○.
        var vm = With(SessionStatus.Finished, SessionStatus.Finished);
        Assert.Equal("done", vm.ShellState);
        Assert.Equal("2 done", vm.Headline);
    }

    [Fact]
    public void WaitingOnYouOutranksWorking()
    {
        var vm = With(SessionStatus.Working, SessionStatus.NeedsYou, SessionStatus.Finished);
        Assert.Equal("blocked", vm.ShellState);
        Assert.True(vm.BlockedOnYou);
        Assert.Equal("1 needs you · 1 running · 1 done", vm.Headline);
    }

    /// <summary>
    /// Without a running Application the themed lookup falls back to a fresh
    /// SolidColorBrush per call, so brushes compare by colour, never by reference.
    /// </summary>
    private static Color ColorOf(object brush) => ((SolidColorBrush)brush).Color;

    [Fact]
    public void OnlyBlockedIsAmber()
    {
        var ink = new ShellInkConverter();
        var amber = ColorOf(ink.Convert("blocked", typeof(object), null, CultureInfo.InvariantCulture));
        Assert.Equal(Color.FromRgb(0xFF, 0xB0, 0x20), amber); // the reserved "you" amber
        foreach (var state in new[] { "working", "done", "empty" })
            Assert.NotEqual(amber, ColorOf(ink.Convert(state, typeof(object), null, CultureInfo.InvariantCulture)));
    }

    [Theory]
    [InlineData("blocked", "▲")]
    [InlineData("working", "●")]
    [InlineData("done", "✓")]
    [InlineData("empty", "○")]
    public void ShellGlyphVocabulary(string state, string glyph)
        => Assert.Equal(glyph, new ShellGlyphConverter().Convert(state, typeof(string), null, CultureInfo.InvariantCulture));

    [Fact]
    public void TightestUsageIsTheOneThatRunsOutFirst()
    {
        var vm = new MainViewModel();
        vm.UpdateUsage(new[]
        {
            new UsageWindow("7d", 4, null),
            new UsageWindow("5h", 89, null),  // least left — this is the constraining one
            new UsageWindow("24h", 40, null),
        });
        Assert.Equal("5h", vm.TightestUsage?.Label);
    }

    [Fact]
    public void HeaderReadsWhatIsLeftNotWhatIsSpent()
        => Assert.Equal("11%", new UsageRemainingConverter()
            .Convert(new UsageWindow("5h", 89, null), typeof(string), null, CultureInfo.InvariantCulture));

    [Theory]
    [InlineData(81, "danger")]  // 19% left
    [InlineData(80, "you")]     // 20% left — the first amber stop
    [InlineData(51, "you")]     // 49% left
    [InlineData(50, "ok")]      // 50% left — green from here up
    [InlineData(0, "ok")]
    public void QuotaRampHasThreeStops(double usedPct, string band)
    {
        var ramp = new UsageSpecBrushConverter();
        Color At(double used) => ColorOf(ramp.Convert(used, typeof(object), null, CultureInfo.InvariantCulture));
        var expected = band switch
        {
            "danger" => At(100),  // nothing left
            "you" => At(60),      // 40% left
            _ => At(0),           // untouched window
        };
        Assert.Equal(expected, At(usedPct));
    }

    [Fact]
    public void ThemePicksExactlyOneViewTree()
    {
        // Each theme is a whole layout, not just a palette. A skin swap that moved
        // the colours but not this flag once left the island wearing the console
        // panel, clipped by the island-sized window.
        var pick = new ThemeVisibilityConverter();
        foreach (var theme in new[] { ThemeManager.Island, ThemeManager.Console })
        {
            var shown = new[] { ThemeManager.Island, ThemeManager.Console }
                .Count(t => (Visibility)pick.Convert(theme, typeof(Visibility), t, CultureInfo.InvariantCulture)
                            == Visibility.Visible);
            Assert.Equal(1, shown);
        }
    }

    [Fact]
    public void PreviewThemeSwitchesTheSkinWithoutPersistingIt()
    {
        var vm = new MainViewModel();
        var saved = vm.Theme;

        vm.PreviewTheme(ThemeManager.Console);
        Assert.Equal(ThemeManager.Console, vm.Theme);

        // A fresh view model reads config.json again — the preview must not be there.
        Assert.Equal(saved, new MainViewModel().Theme);
    }

    [Fact]
    public void MarksNameTheAgentsOnTheLine()
    {
        var vm = new MainViewModel();
        vm.UpdateSessions(new[]
        {
            Row(SessionStatus.Working, "claude"),
            Row(SessionStatus.Working, "codex"),
            Row(SessionStatus.Finished, "antigravity"),
        });
        Assert.Equal(new[] { "claude", "codex", "antigravity" }, vm.AgentMarks.Select(m => m.Agent));
    }

    [Fact]
    public void TrackedSpacesOutCapitalsWithoutChangingThem()
    {
        var tracked = (string)new TrackedConverter()
            .Convert("AGENT", typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Equal("AGENT", tracked.Replace(" ", ""));
        Assert.Contains(' ', tracked);
    }
}
