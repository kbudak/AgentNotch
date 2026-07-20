using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AgentNotch.App.Sessions;

namespace AgentNotch.App.UI;

/// <summary>
/// Vector marks identifying each agent, drawn in a 24x24 box. The notch reads as
/// "shape = which agent, colour = what state", so these carry no colour of their
/// own — the status brush fills them.
/// </summary>
public static class AgentIcons
{
    /// <summary>
    /// The agent's face: a soft squircle with two eyes. Even-odd fill turns the
    /// eyes into holes, so the panel shows through them and the body alone
    /// carries the status colour.
    /// </summary>
    public static readonly Geometry Claude = Frozen(
        "M9,2.6 L15,2.6 A6.4,6.4 0 0 1 21.4,9 L21.4,15 A6.4,6.4 0 0 1 15,21.4 " +
        "L9,21.4 A6.4,6.4 0 0 1 2.6,15 L2.6,9 A6.4,6.4 0 0 1 9,2.6Z " +
        "M6.2,11.6 A2.5,2.5 0 1 0 11.2,11.6 A2.5,2.5 0 1 0 6.2,11.6Z " +
        "M12.8,11.6 A2.5,2.5 0 1 0 17.8,11.6 A2.5,2.5 0 1 0 12.8,11.6Z");

    /// <summary>OpenAI-style hex knot: a hexagonal ring around a solid core
    /// (even-odd fill turns the inner hexagon into the hole, the core back into ink).</summary>
    public static readonly Geometry Codex = Frozen(
        "M12,1.5 L21.09,6.75 L21.09,17.25 L12,22.5 L2.91,17.25 L2.91,6.75Z " +
        "M12,5.8 L17.37,8.9 L17.37,15.1 L12,18.2 L6.63,15.1 L6.63,8.9Z " +
        "M9.6,12 A2.4,2.4 0 1 0 14.4,12 A2.4,2.4 0 1 0 9.6,12Z");

    /// <summary>Gemini-style four-point sparkle with concave sides.</summary>
    public static readonly Geometry Antigravity = Frozen(
        "M12,1.6 C12.7,7.4 16.6,11.3 22.4,12 C16.6,12.7 12.7,16.6 12,22.4 " +
        "C11.3,16.6 7.4,12.7 1.6,12 C7.4,11.3 11.3,7.4 12,1.6Z");

    /// <summary>Cursor-style wireframe cube (host marker / future agent source).</summary>
    public static readonly Geometry Cursor = Frozen(
        "M12,1.8 L21.3,7.2 L21.3,16.8 L12,22.2 L2.7,16.8 L2.7,7.2Z " +
        "M12,4.7 L18.8,8.65 L18.8,15.35 L12,19.3 L5.2,15.35 L5.2,8.65Z");

    /// <summary>Neutral dot — unknown agent, or the idle pill.</summary>
    public static readonly Geometry Generic = Frozen("M7,12 A5,5 0 1 0 17,12 A5,5 0 1 0 7,12Z");

    /// <summary>Mark for an agent id ("claude" | "codex" | "antigravity" | "cursor").</summary>
    public static Geometry For(string? agent) => agent?.Trim().ToLowerInvariant() switch
    {
        "claude" => Claude,
        "codex" => Codex,
        "antigravity" => Antigravity,
        "cursor" => Cursor,
        _ => Generic,
    };

    private static Geometry Frozen(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze(); // shared across every row; freezing makes it thread-safe and cheap
        return geometry;
    }
}

/// <summary>
/// One colour per state, resolved from the theme so the palette has a single home.
/// working = green (running), needs-you = amber (waiting on you), finished = blue
/// (done), prompt = orange (approval card open), idle = grey (nothing running).
/// </summary>
internal static class StatusBrushes
{
    public static Brush Working => Lookup("StatusWorkingBrush", 0x4A, 0xDE, 0x80);
    public static Brush NeedsYou => Lookup("StatusNeedsYouBrush", 0xF5, 0x9E, 0x0B);
    public static Brush Finished => Lookup("StatusFinishedBrush", 0x60, 0xA5, 0xFA);
    public static Brush Prompt => Lookup("StatusPromptBrush", 0xFB, 0x92, 0x3C);
    public static Brush Idle => Lookup("StatusIdleBrush", 0x6B, 0x72, 0x80);
    public static Brush Danger => Lookup("AccentRedBrush", 0xF8, 0x71, 0x71);

    public static Brush Of(SessionStatus status) => status switch
    {
        SessionStatus.Working => Working,
        SessionStatus.NeedsYou => NeedsYou,
        _ => Finished,
    };

    private static Brush Lookup(string key, byte r, byte g, byte b)
    {
        try
        {
            if (System.Windows.Application.Current?.TryFindResource(key) is Brush themed)
                return themed;
        }
        catch { /* no Application (unit tests / designer) — use the literal */ }

        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

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

/// <summary>AgentSession → "codex — api" / "claude · cursor — web" (agent, optional host, project).</summary>
public sealed class SessionTitleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            // Agent and host are chips on the right now, so the title is just the
            // project — the thing you actually scan for.
            if (value is AgentSession s)
                return string.IsNullOrWhiteSpace(s.ProjectName) ? "session" : s.ProjectName;
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

/// <summary>AgentSession → token spend ("48.2k"), or the model name when spend is unknown (codex/antigravity).</summary>
public sealed class SessionTokensConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            if (value is AgentSession s)
            {
                var spend = TokenFormat.Short(s.TokensSpent);
                return spend.Length > 0 ? spend : s.Model ?? "";
            }
        }
        catch { /* fall through */ }
        return "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>AgentSession → its agent's vector mark (shape identifies the agent).</summary>
public sealed class SessionAgentIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            if (value is AgentSession s) return AgentIcons.For(s.Agent);
        }
        catch { /* fall through */ }
        return AgentIcons.Generic;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>AgentSession → the colour of its status (colour identifies the state).</summary>
public sealed class SessionStatusBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            if (value is AgentSession s) return StatusBrushes.Of(s.Status);
        }
        catch { /* fall through */ }
        return StatusBrushes.Idle;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Agent id string → vector mark (collapsed pill, which shows the lead agent).</summary>
public sealed class AgentIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => AgentIcons.For(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>StateKind ("idle"|"working"|"needsyou"|"prompt") → its colour.</summary>
public sealed class StateKindBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value as string) switch
        {
            "working" => StatusBrushes.Working,
            "needsyou" => StatusBrushes.NeedsYou,
            "prompt" => StatusBrushes.Prompt,
            _ => StatusBrushes.Idle,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>AgentSession → brand chip text: "Claude" | "Codex" | "Gemini".</summary>
public sealed class SessionBrandConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            if (value is AgentSession s)
                return s.Agent?.Trim().ToLowerInvariant() switch
                {
                    "codex" => "Codex",
                    "antigravity" => "Antigravity",
                    "claude" => "Claude",
                    _ => s.Agent ?? "",
                };
        }
        catch { /* fall through */ }
        return "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>AgentSession → where it runs ("Cursor", "Windows Terminal", …).</summary>
public sealed class SessionHostConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            if (value is AgentSession s)
            {
                var host = s.HostLabel;
                if (string.IsNullOrWhiteSpace(host)) return "";
                // Antigravity hosts its own agent — one chip, not two identical ones.
                var brand = s.Agent?.Trim();
                return string.Equals(host, brand, StringComparison.OrdinalIgnoreCase) ? "" : host;
            }
        }
        catch { /* fall through */ }
        return "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>AgentSession → how long it has been running: "27m", "3h", "2d".</summary>
public sealed class SessionAgeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            if (value is AgentSession s && s.StartedUtc != default)
            {
                var age = DateTime.UtcNow - s.StartedUtc;
                if (age < TimeSpan.Zero) age = TimeSpan.Zero;
                if (age.TotalMinutes < 1) return "now";
                if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m";
                if (age.TotalHours < 24) return $"{(int)age.TotalHours}h";
                return $"{(int)age.TotalDays}d";
            }
        }
        catch { /* fall through */ }
        return "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Usage percentage → green under half, amber past 70%, red past 90%.</summary>
public sealed class UsageSeverityBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            var pct = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (pct >= 90) return StatusBrushes.Danger;
            if (pct >= 70) return StatusBrushes.NeedsYou;
            return StatusBrushes.Working;
        }
        catch { return StatusBrushes.Idle; }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Toast kind ("ok" | "deny") → its colour.</summary>
public sealed class ToastBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value as string) == "deny" ? StatusBrushes.Danger : StatusBrushes.Working;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>True → Collapsed (hides the normal pill content while a toast shows).</summary>
public sealed class BoolToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

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
