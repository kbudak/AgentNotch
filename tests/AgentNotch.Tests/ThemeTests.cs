using System.Text.RegularExpressions;
using System.Xml.Linq;
using AgentNotch.App.UI;
using Xunit;

namespace AgentNotch.Tests;

/// <summary>
/// The theme system is all string keys resolved at runtime: a token that only one
/// palette defines, or a view that asks for a key nobody declares, fails silently
/// as an unstyled control rather than as a build error. These read the shipped XAML
/// as XML — no WPF Application needed — and pin both halves of that contract.
/// </summary>
public class ThemeTests
{
    private const string Ns = "{http://schemas.microsoft.com/winfx/2006/xaml}";

    private static string UiDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentNotch.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir); // tests must run from inside the repo
        return Path.Combine(dir!.FullName, "src", "AgentNotch.App", "UI");
    }

    private static HashSet<string> KeysOf(string xamlPath) =>
        XDocument.Load(xamlPath).Root!.Elements()
            .Select(e => (string?)e.Attribute(Ns + "Key"))
            .Where(k => k is not null)
            .Select(k => k!)
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void BothPalettesDeclareTheSameTokens()
    {
        var ui = UiDir();
        var island = KeysOf(Path.Combine(ui, "Themes", "Island.xaml"));
        var console = KeysOf(Path.Combine(ui, "Themes", "Console.xaml"));

        Assert.Empty(island.Except(console));  // console is missing an island token
        Assert.Empty(console.Except(island));  // island is missing a console token
        Assert.NotEmpty(island);
    }

    [Fact]
    public void EveryDynamicResourceKeyIsDeclaredByBothPalettes()
    {
        var ui = UiDir();
        var island = KeysOf(Path.Combine(ui, "Themes", "Island.xaml"));
        var console = KeysOf(Path.Combine(ui, "Themes", "Console.xaml"));

        // Theme.xaml holds the styles; the rest are the views that consume them.
        var referenced = Directory.EnumerateFiles(ui, "*.xaml", SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.Combine("UI", "Themes"), StringComparison.Ordinal))
            .SelectMany(p => Regex.Matches(File.ReadAllText(p), @"\{DynamicResource (\w+)\}")
                                  .Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(referenced);
        Assert.Empty(referenced.Except(island));
        Assert.Empty(referenced.Except(console));
    }

    [Theory]
    [InlineData("console", ThemeManager.Console)]
    [InlineData("CONSOLE", ThemeManager.Console)]
    [InlineData(" island ", ThemeManager.Island)]
    [InlineData("", ThemeManager.Island)]
    [InlineData(null, ThemeManager.Island)]
    [InlineData("solarized", ThemeManager.Island)] // a hand-edited config must not unstyle the app
    public void UnknownThemeIdsFallBackToIsland(string? input, string expected)
        => Assert.Equal(expected, ThemeManager.Normalize(input));
}
