using System.Globalization;
using System.Windows;
using System.Windows.Data;
using AgentNotch.App.Sessions;

namespace AgentNotch.App.UI;

/// <summary>
/// Value converters used by the notch XAML. All converters are null-tolerant and
/// never throw — a binding failure must never take the overlay down.
/// The AgentSession-based converters take the whole session item (Binding with no
/// path) and read its members in C#, so they work regardless of how the Sessions
/// module declares them.
/// </summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class CollectionToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            if (value is System.Collections.ICollection c)
                return c.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (value is System.Collections.IEnumerable e)
            {
                foreach (var _ in e) return Visibility.Visible;
            }
        }
        catch { /* fall through */ }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Visible when the bound integer is greater than one (queue badge).</summary>
public sealed class GreaterThanOneToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            return System.Convert.ToInt32(value, CultureInfo.InvariantCulture) > 1
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch { return Visibility.Collapsed; }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>AgentSession → "working" | "needsyou" | "finished" (drives the row dot).</summary>
public sealed class SessionStateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            if (value is AgentSession s)
            {
                return s.Status switch
                {
                    SessionStatus.Working => "working",
                    SessionStatus.NeedsYou => "needsyou",
                    _ => "finished",
                };
            }
        }
        catch { /* fall through */ }
        return "finished";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>AgentSession → "claude — {ProjectName}".</summary>
public sealed class SessionTitleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            if (value is AgentSession s)
            {
                var name = s.ProjectName;
                return string.IsNullOrWhiteSpace(name) ? "claude" : $"claude — {name}";
            }
        }
        catch { /* fall through */ }
        return "claude";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>AgentSession → "Bash(npm test)" / current tool / last assistant message.</summary>
public sealed class SessionSubtitleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            if (value is AgentSession s)
            {
                string tool = s.CurrentTool ?? "";
                string target = s.CurrentTarget ?? "";
                if (tool.Length > 0)
                    return target.Length > 0 ? $"{tool}({target})" : tool;
                return s.LastMessage ?? "";
            }
        }
        catch { /* fall through */ }
        return "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>AgentSession → short-form cumulative token spend ("48.2k").</summary>
public sealed class SessionTokensConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            if (value is AgentSession s) return TokenFormat.Short(s.TokensSpent);
        }
        catch { /* fall through */ }
        return "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>1234 → "1.2k", 48200 → "48.2k", 1200000 → "1.2M".</summary>
public static class TokenFormat
{
    public static string Short(long value)
    {
        if (value <= 0) return "";
        if (value < 1_000) return value.ToString(CultureInfo.InvariantCulture);
        if (value < 1_000_000)
            return (value / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k";
        return (value / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M";
    }
}
