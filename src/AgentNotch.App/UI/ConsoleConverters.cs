using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AgentNotch.App.Sessions;
using AgentNotch.App.Usage;

namespace AgentNotch.App.UI;

/// <summary>
/// Converters used only by the console theme. Its rules differ from the island
/// theme's in two ways that matter:
///
///  * colour splits in two — an agent's colour is IDENTITY (which agent) and the
///    status glyph carries STATE, where the island theme fuses them by tinting the
///    agent mark;
///  * amber is reserved. It only ever means "blocked on you", so Claude's own
///    orange must not double as a status.
///
/// All of them are null-tolerant: a binding failure must never take the notch down.
/// </summary>
internal static class ConsoleInk
{
    /// <summary>Status glyph vocabulary — one character each, no icons.</summary>
    public static string Glyph(SessionStatus status) => status switch
    {
        SessionStatus.Working => "●",
        SessionStatus.NeedsYou => "▲",
        _ => "✓",
    };

    /// <summary>Running wears the agent's own colour; the other states are semantic.</summary>
    public static Brush Ink(AgentSession s) => s.Status switch
    {
        SessionStatus.Working => StatusBrushes.AgentInk(s.Agent),
        SessionStatus.NeedsYou => StatusBrushes.You,
        _ => StatusBrushes.Finished,
    };
}

/// <summary>Agent id ("claude") → its identity colour.</summary>
public sealed class AgentInkConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => StatusBrushes.AgentInk(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>AgentSession → its agent's identity colour.</summary>
public sealed class SessionAgentInkConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => StatusBrushes.AgentInk(value is AgentSession s ? s.Agent : null);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>AgentSession → the agent's name, lowercase, as the table's AGENT cell.</summary>
public sealed class SessionAgentNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            if (value is AgentSession s && s.Agent is { Length: > 0 } agent)
                return agent.Trim().ToLowerInvariant();
        }
        catch { /* fall through */ }
        return "agent";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>AgentSession → its single-character status glyph.</summary>
public sealed class SessionGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try { if (value is AgentSession s) return ConsoleInk.Glyph(s.Status); }
        catch { /* fall through */ }
        return "○";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>AgentSession → the ink of its status glyph.</summary>
public sealed class SessionGlyphInkConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try { if (value is AgentSession s) return ConsoleInk.Ink(s); }
        catch { /* fall through */ }
        return StatusBrushes.Idle;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>AgentSession → row background: waiting rows are lifted, everything else is bare.</summary>
public sealed class SessionRowBgConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            if (value is AgentSession { Status: SessionStatus.NeedsYou })
                return StatusBrushes.Named("RowWaitingBrush", 0x14, 0x14, 0x14);
        }
        catch { /* fall through */ }
        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>AgentSession → the ink of its ACTIVITY cell; the state, not the agent.</summary>
public sealed class SessionActivityInkConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            if (value is AgentSession s)
                return s.Status switch
                {
                    SessionStatus.NeedsYou => StatusBrushes.You,
                    SessionStatus.Finished => StatusBrushes.Finished,
                    _ => StatusBrushes.Muted,
                };
        }
        catch { /* fall through */ }
        return StatusBrushes.Muted;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>QuestionOption → its bare shortcut digit for the 26px key column ("Ctrl+2" → "2").</summary>
public sealed class OptionKeyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            if (value is ViewModels.QuestionOption o)
                return o.Index < 9 ? (o.Index + 1).ToString(CultureInfo.InvariantCulture) : "";
        }
        catch { /* fall through */ }
        return "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Shell state ("blocked"|"working"|"done"|"empty") → the header/line glyph.</summary>
public sealed class ShellGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value as string) switch { "blocked" => "▲", "working" => "●", "done" => "✓", _ => "○" };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Shell state → its ink. Only "blocked" is allowed to be amber.</summary>
public sealed class ShellInkConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value as string) switch
        {
            "blocked" => StatusBrushes.You,
            "done" => StatusBrushes.Finished,
            "empty" => StatusBrushes.Dim,
            _ => StatusBrushes.Body,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// UsageWindow → how much of the window is LEFT ("11%"). The console reads what
/// remains, not what is spent — that is the number you decide on.
/// </summary>
public sealed class UsageRemainingConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            double used = value is UsageWindow w
                ? w.UsedPct
                : System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
            int left = (int)Math.Round(Math.Clamp(100 - used, 0, 100));
            return left.ToString(CultureInfo.InvariantCulture) + "%";
        }
        catch { return ""; }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// The console's own quota ramp, three stops as specified: under 20% left is
/// danger, under 50% is the "you" amber, at or above 50% is fine. Deliberately
/// coarser than the island theme's four-stop ramp.
/// </summary>
public sealed class UsageSpecBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            double used = value is UsageWindow w
                ? w.UsedPct
                : System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
            double left = 100 - used;
            if (left < 20) return StatusBrushes.Danger;
            if (left < 50) return StatusBrushes.You;
            return StatusBrushes.Finished;
        }
        catch { return StatusBrushes.Idle; }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Fakes CSS letter-spacing, which WPF's TextBlock has no property for, by
/// weaving U+2009 THIN SPACE between characters. Used only on the short tracked
/// capitals (column heads, kicker chips, buttons, footer) the design specifies.
/// </summary>
public sealed class TrackedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value as string ?? "";
        return text.Length < 2 ? text : string.Join(' ', text.ToCharArray());
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Theme id → Visibility, so both themes' trees can sit side by side. Parameter is the theme that shows.</summary>
public sealed class ThemeVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(ThemeManager.Normalize(value as string), parameter as string, StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>DiffLine kind → the sign column: "+", "−" or a blank for context.</summary>
public sealed class DiffSignConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value as string) switch { "add" => "+", "del" => "−", _ => " " };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>DiffLine → the line number actually worth showing (new side, else old).</summary>
public sealed class DiffNumberConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            if (value is DiffLine line)
                return line.NewNo is { Length: > 0 } n ? n : line.OldNo ?? "";
        }
        catch { /* fall through */ }
        return "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// bool == parameter ("true"/"false"), for an on/off pair of segments. Unchecking
/// returns DoNothing exactly like <see cref="EqualsParamConverter"/>, so the two
/// halves of a pair never fight over the same source property.
/// </summary>
public sealed class BoolEqualsParamConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b == Wanted(parameter);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Wanted(parameter) : Binding.DoNothing;

    private static bool Wanted(object? parameter)
        => string.Equals(parameter as string, "true", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Prompt title → its leading verb ("Edit", "Bash"), which the design sets in
/// tracked amber caps ahead of the target. <see cref="PromptTargetConverter"/>
/// returns the remainder.
/// </summary>
public sealed class PromptVerbConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var title = (value as string ?? "").Trim();
        int space = title.IndexOf(' ');
        return (space > 0 ? title[..space] : title).ToUpperInvariant();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Prompt title minus its verb — the path or command the verb acts on.</summary>
public sealed class PromptTargetConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var title = (value as string ?? "").Trim();
        int space = title.IndexOf(' ');
        return space > 0 ? title[(space + 1)..] : "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Prompt kind → the kicker chip's word.</summary>
public sealed class PromptKickerConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ViewModels.PromptKind.Question ? "ASKING" : "PERMISSION";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
