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

    /// <summary>Every selectable theme, as (id, its declared keys). Adding a theme to
    /// <see cref="ThemeManager.All"/> without shipping its palette fails here.</summary>
    private static IEnumerable<(string Id, HashSet<string> Keys)> Palettes()
    {
        var ui = UiDir();
        foreach (var id in ThemeManager.All)
        {
            var file = ThemeManager.FileOf(id) + ".xaml";
            var path = Path.Combine(ui, "Themes", file);
            Assert.True(File.Exists(path), $"theme '{id}' has no palette at {file}");
            yield return (id, KeysOf(path));
        }
    }

    [Fact]
    public void EveryThemeHasItsOwnPaletteFile()
    {
        // FileOf falls back to Island for an unknown id; without this, a theme added
        // to All but not to FileOf would quietly borrow Island's palette.
        var files = ThemeManager.All.Select(ThemeManager.FileOf).ToList();
        Assert.Equal(files.Count, files.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void AllPalettesDeclareTheSameTokens()
    {
        var palettes = Palettes().ToList();
        var island = palettes.Single(p => p.Id == ThemeManager.Island).Keys;

        Assert.NotEmpty(island);
        foreach (var (id, keys) in palettes)
        {
            Assert.Empty(island.Except(keys));  // this theme is missing an island token
            Assert.Empty(keys.Except(island));  // island is missing this theme's token
        }
    }

    [Fact]
    public void EveryDynamicResourceKeyIsDeclaredByAllPalettes()
    {
        var ui = UiDir();
        // Theme.xaml holds the styles; the rest are the views that consume them.
        var referenced = Directory.EnumerateFiles(ui, "*.xaml", SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.Combine("UI", "Themes"), StringComparison.Ordinal))
            .SelectMany(p => Regex.Matches(File.ReadAllText(p), @"\{DynamicResource (\w+)\}")
                                  .Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(referenced);
        foreach (var (id, keys) in Palettes())
            Assert.Empty(referenced.Except(keys));
    }

    [Theory]
    [InlineData("console", ThemeManager.Console)]
    [InlineData("CONSOLE", ThemeManager.Console)]
    [InlineData("glass", ThemeManager.Glass)]
    [InlineData(" GLASS ", ThemeManager.Glass)]
    [InlineData("atari", ThemeManager.Atari)]
    [InlineData(" Atari ", ThemeManager.Atari)]
    [InlineData("atari-dark", ThemeManager.AtariDark)]
    [InlineData("ATARI-DARK", ThemeManager.AtariDark)]
    [InlineData(" island ", ThemeManager.Island)]
    [InlineData("", ThemeManager.Island)]
    [InlineData(null, ThemeManager.Island)]
    [InlineData("solarized", ThemeManager.Island)] // a hand-edited config must not unstyle the app
    public void UnknownThemeIdsFallBackToIsland(string? input, string expected)
        => Assert.Equal(expected, ThemeManager.Normalize(input));
}
