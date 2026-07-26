using System.Windows;
using AgentNotch.App.Diagnostics;

namespace AgentNotch.App.UI;

/// <summary>
/// Swaps the palette dictionary that sits under the shared styles.
///
/// App.xaml merges the palette (Themes/*.xaml) into slot 0 and Theme.xaml (styles +
/// converters) into slot 1, and every colour, font and corner radius in the styles
/// and views is a DynamicResource. Replacing slot 0 therefore repaints the entire
/// notch without rebuilding a single control — see
/// <see cref="ViewModels.MainViewModel.Theme"/> for the one thing that does need a
/// nudge afterwards (converters, which return a Brush once and are not re-run by a
/// resource change).
/// </summary>
public static class ThemeManager
{
    /// <summary>The original VibeIsland look: rounded soft-black capsule.</summary>
    public const string Island = "island";

    /// <summary>The AgentNotch Console direction: square, monospace, hairline rules.</summary>
    public const string Console = "console";

    /// <summary>Every selectable theme id, in the order the settings picker shows them.</summary>
    public static readonly string[] All = { Island, Console };

    /// <summary>Unknown/missing ids fall back to Island rather than leaving the app unstyled.</summary>
    public static string Normalize(string? name)
        => Array.Find(All, t => string.Equals(t, name?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Island;

    /// <summary>Loads the theme's palette into slot 0. Fail-soft: a bad id keeps the current look.</summary>
    public static void Apply(string? name)
    {
        var app = Application.Current;
        if (app is null) return; // unit tests / designer — nothing to repaint

        var id = Normalize(name);
        var file = id == Console ? "Console" : "Island";
        try
        {
            var dict = new ResourceDictionary { Source = new Uri($"UI/Themes/{file}.xaml", UriKind.Relative) };
            if (app.Resources.MergedDictionaries.Count == 0) app.Resources.MergedDictionaries.Add(dict);
            else app.Resources.MergedDictionaries[0] = dict;
        }
        catch (Exception ex) { AppLog.Write($"theme: apply '{id}' failed: {ex.Message}"); }
    }
}
