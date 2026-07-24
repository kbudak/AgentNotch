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
    /// The Claude Code invader: pixel-art creature on a 12x8 grid (2 units per
    /// cell) — head with two square eyes, arms out to both sides, four legs.
    /// Even-odd fill turns the eyes into holes, so the panel shows through them
    /// and the body alone carries the status colour.
    /// </summary>
    public static readonly Geometry Claude = Frozen(
        // head top → right arm → down the right side → four legs (right to left)
        "M4,4 L20,4 L20,8 L24,8 L24,12 L20,12 L20,20 " +
        "L18,20 L18,16 L16,16 L16,20 L14,20 L14,16 L10,16 L10,20 " +
        "L8,20 L8,16 L6,16 L6,20 L4,20 " +
        // up the left side → left arm → back to the head
        "L4,12 L0,12 L0,8 L4,8Z " +
        "M6,6 L8,6 L8,8 L6,8Z M16,6 L18,6 L18,8 L16,8Z");

    /// <summary>
    /// The OpenAI knot (Codex). Nonzero fill: the six interlocking loops overlap,
    /// and only the small centre hexagon is meant to be a hole.
    /// </summary>
    public static readonly Geometry Codex = Frozen(
        "F1 M22.2819,9.8211a5.9847,5.9847 0 0 0-.5157-4.9108 6.0462,6.0462 0 0 0-6.5098-2.9" +
        "A6.0651,6.0651 0 0 0 4.9807,4.1818a5.9847,5.9847 0 0 0-3.9977,2.9 6.0462,6.0462 0 0 0 .7427,7.0966" +
        "a5.98,5.98 0 0 0 .511,4.9107 6.051,6.051 0 0 0 6.5146,2.9001A5.9847,5.9847 0 0 0 13.2599,24" +
        "a6.0557,6.0557 0 0 0 5.7718-4.2058 5.9894,5.9894 0 0 0 3.9977-2.9001 6.0557,6.0557 0 0 0-.7475-7.0729z" +
        "m-9.022,12.6081a4.4755,4.4755 0 0 1-2.8764-1.0408l.1419-.0804 4.7783-2.7582" +
        "a.7948.7948 0 0 0 .3927-.6813v-6.7369l2.02,1.1686a.071.071 0 0 1 .038.052v5.5826" +
        "a4.504,4.504 0 0 1-4.4945,4.4944zm-9.6607-4.1254a4.4708,4.4708 0 0 1-.5346-3.0137l.142.0852 4.783,2.7582" +
        "a.7712.7712 0 0 0 .7806,0l5.8428-3.3685v2.3324a.0804.0804 0 0 1-.0332.0615L9.74,19.9502" +
        "a4.4992,4.4992 0 0 1-6.1408-1.6464zM2.3408,7.8956a4.485,4.485 0 0 1 2.3655-1.9728V11.6" +
        "a.7664.7664 0 0 0 .3879.6765l5.8144,3.3543-2.0201,1.1685a.0757.0757 0 0 1-.071,0l-4.8303-2.7865" +
        "A4.504,4.504 0 0 1 2.3408,7.872zm16.5963,3.8558L13.1038,8.364 15.1192,7.2a.0757.0757 0 0 1 .071,0" +
        "l4.8303,2.7913a4.4944,4.4944 0 0 1-.6765,8.1042v-5.6772a.79.79 0 0 0-.407-.667z" +
        "m2.0107-3.0231l-.142-.0852-4.7735-2.7818a.7759.7759 0 0 0-.7854,0L9.409,9.2297V6.8974" +
        "a.0662.0662 0 0 1 .0284-.0615l4.8303-2.7866a4.4992,4.4992 0 0 1 6.6802,4.66zM8.3065,12.863" +
        "l-2.02-1.1638a.0804.0804 0 0 1-.038-.0567V6.0742a4.4992,4.4992 0 0 1 7.3757-3.4537l-.142.0805" +
        "L8.704,5.459a.7948.7948 0 0 0-.3927.6813zm1.0976-2.3654l2.602-1.4998 2.6069,1.4998v2.9994" +
        "l-2.5974,1.4997-2.6067-1.4997Z");

    /// <summary>
    /// The Gemini diamond (Antigravity): a four-point star with concave sides,
    /// drawn hollow — even-odd fill turns the inner star into the opening.
    /// </summary>
    public static readonly Geometry Antigravity = Frozen(
        "M12,1.6 C12.7,7.4 16.6,11.3 22.4,12 C16.6,12.7 12.7,16.6 12,22.4 " +
        "C11.3,16.6 7.4,12.7 1.6,12 C7.4,11.3 11.3,7.4 12,1.6Z " +
        "M12,6.2 C12.39,9.43 14.57,11.61 17.8,12 C14.57,12.39 12.39,14.57 12,17.8 " +
        "C11.61,14.57 9.43,12.39 6.2,12 C9.43,11.61 11.61,9.43 12,6.2Z");

    /// <summary>Cursor-style wireframe cube (host marker / future agent source).</summary>
    public static readonly Geometry Cursor = Frozen(
        "M12,1.8 L21.3,7.2 L21.3,16.8 L12,22.2 L2.7,16.8 L2.7,7.2Z " +
        "M12,4.7 L18.8,8.65 L18.8,15.35 L12,19.3 L5.2,15.35 L5.2,8.65Z");

    /// <summary>Neutral dot — unknown agent, or the idle pill.</summary>
    public static readonly Geometry Generic = Frozen("M7,12 A5,5 0 1 0 17,12 A5,5 0 1 0 7,12Z");

    // ---- Pixel-art STATUS glyphs (shape = what state) --------------------------
    // Built from an ASCII bitmap so they read as chunky retro pixels and stay easy
    // to tweak. Colour comes from the status brush; the working spinner is rotated
    // by the view. All are centred in the same 24x24 box as the agent marks.

    /// <summary>
    /// Working: a pixel "C" ring (open on one side). The view rotates it in discrete
    /// 90° steps so the squares always land back on the grid — crisp, never smeared,
    /// while the open side visibly jumps around like a retro loader.
    /// </summary>
    public static readonly Geometry Spinner = FromMatrix(
        " XXX ",
        "X    ",
        "X    ",
        "X    ",
        " XXX ");

    /// <summary>Needs you: a clean pixel question mark (a question is waiting).</summary>
    public static readonly Geometry Question = FromMatrix(
        " XXX ",
        "X   X",
        "    X",
        "   X ",
        "  X  ",
        "     ",
        "  X  ");

    /// <summary>Finished: a symmetric pixel check mark (done).</summary>
    public static readonly Geometry Check = FromMatrix(
        "      X",
        "     X ",
        "X   X  ",
        " X X   ",
        "  X    ");

    /// <summary>A cog/gear mark for the settings button: 8 square teeth + a centre hole.</summary>
    public static readonly Geometry Gear = BuildGear();

    private static Geometry BuildGear()
    {
        const int teeth = 8;
        const double cx = 12, cy = 12, rOut = 11.2, rIn = 7.6, rHole = 3.4;
        var outer = new StreamGeometry();
        using (var ctx = outer.Open())
        {
            var pts = new System.Windows.Point[teeth * 4];
            double stepA = 2 * Math.PI / teeth;
            for (int t = 0; t < teeth; t++)
            {
                double a = t * stepA;
                pts[t * 4 + 0] = Polar(cx, cy, rOut, a);
                pts[t * 4 + 1] = Polar(cx, cy, rOut, a + stepA * 0.5);
                pts[t * 4 + 2] = Polar(cx, cy, rIn,  a + stepA * 0.5);
                pts[t * 4 + 3] = Polar(cx, cy, rIn,  a + stepA);
            }
            ctx.BeginFigure(pts[0], true, true);
            for (int i = 1; i < pts.Length; i++) ctx.LineTo(pts[i], true, false);
        }
        outer.Freeze();
        var group = new GeometryGroup { FillRule = FillRule.EvenOdd }; // centre ellipse = hole
        group.Children.Add(outer);
        group.Children.Add(new EllipseGeometry(new System.Windows.Point(cx, cy), rHole, rHole));
        group.Freeze();
        return group;
    }

    private static System.Windows.Point Polar(double cx, double cy, double r, double a)
        => new(cx + r * Math.Cos(a), cy + r * Math.Sin(a));

    /// <summary>Mark for an agent id ("claude" | "codex" | "antigravity" | "cursor").</summary>
    public static Geometry For(string? agent) => agent?.Trim().ToLowerInvariant() switch
    {
        "claude" => Claude,
        "codex" => Codex,
        "antigravity" => Antigravity,
        "cursor" => Cursor,
        _ => Generic,
    };

    /// <summary>Pixel status glyph: spinner while working, "?" when it needs you, "✓" when done.</summary>
    public static Geometry StatusGlyph(SessionStatus status) => status switch
    {
        SessionStatus.Working => Spinner,
        SessionStatus.NeedsYou => Question,
        _ => Check,
    };

    /// <summary>Pixel status glyph from the collapsed-pill state string.</summary>
    public static Geometry StatusGlyph(string? state) => state switch
    {
        "working" => Spinner,
        "needsyou" or "prompt" => Question,
        "finished" => Check,
        _ => Generic,
    };

    /// <summary>
    /// Builds a filled geometry from an ASCII bitmap: every non-space cell becomes a
    /// small square. The grid is centred in a 24x24 box so all marks share a scale.
    /// Paths are emitted with the invariant culture — a comma decimal separator (e.g.
    /// on a Turkish system) would otherwise corrupt the path data.
    /// </summary>
    private static Geometry FromMatrix(params string[] rows)
    {
        // Integer cell + gap on the 24-unit grid, and the whole bitmap snapped to a
        // whole-unit origin, so every square lands on the grid — no sub-pixel skew
        // that would read as "crooked".
        const double cell = 3, gap = 1, step = cell + gap;
        int h = rows.Length, w = 0;
        foreach (var r in rows) if (r.Length > w) w = r.Length;
        double gridW = w * step - gap, gridH = h * step - gap;
        double ox = Math.Round((24 - gridW) / 2.0), oy = Math.Round((24 - gridH) / 2.0);

        var sb = new System.Text.StringBuilder();
        for (int r = 0; r < h; r++)
            for (int c = 0; c < rows[r].Length; c++)
            {
                if (rows[r][c] == ' ') continue;
                double x = ox + c * step, y = oy + r * step;
                sb.Append(FormattableString.Invariant(
                    $"M{x:0.##},{y:0.##} h{cell:0.##} v{cell:0.##} h-{cell:0.##} Z "));
            }
        return Frozen(sb.ToString());
    }

    private static Geometry Frozen(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze(); // shared across every row; freezing makes it thread-safe and cheap
        return geometry;
    }
}

/// <summary>
/// One colour per state, resolved from the theme so the palette has a single home.
/// working = blue (running), needs-you = orange (a question is waiting), finished =
/// green (done), prompt = orange (approval card open), idle = grey (nothing running).
/// The usage meter keeps its own green/amber/red ramp (see <see cref="Good"/> /
/// <see cref="Warn"/>) so it stays semantic regardless of the status palette.
/// </summary>
internal static class StatusBrushes
{
    public static Brush Working => Lookup("StatusWorkingBrush", 0x3B, 0x82, 0xF6);
    public static Brush NeedsYou => Lookup("StatusNeedsYouBrush", 0xFB, 0x92, 0x3C);
    public static Brush Finished => Lookup("StatusFinishedBrush", 0x4A, 0xDE, 0x80);
    public static Brush Prompt => Lookup("StatusPromptBrush", 0xFB, 0x92, 0x3C);
    public static Brush Idle => Lookup("StatusIdleBrush", 0x6B, 0x72, 0x80);
    public static Brush Danger => Lookup("AccentRedBrush", 0xF8, 0x71, 0x71);
    public static Brush Good => Lookup("AccentGreenBrush", 0x4A, 0xDE, 0x80); // meter: safe
    public static Brush Warn => Lookup("AccentAmberBrush", 0xF5, 0x9E, 0x0B); // meter: caution

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
                // CurrentTarget already reads as a phrase ("Writing middleware.ts");
                // only bare tool names need dressing up.
                if (s.CurrentTarget is { Length: > 0 } activity) return activity;
                if (s.CurrentTool is { Length: > 0 } tool) return ToolPhrase.Of(tool);
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

/// <summary>AgentSession → its pixel status glyph (spinner / "?" / "✓").</summary>
public sealed class SessionStatusIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            if (value is AgentSession s) return AgentIcons.StatusGlyph(s.Status);
        }
        catch { /* fall through */ }
        return AgentIcons.Check;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// State → Visibility for swapping the working spinner against the static "?"/"✓"
/// glyph. Accepts either an AgentSession or a state string; the parameter is the
/// state to match, optionally prefixed "!" to invert (e.g. "!working").
/// </summary>
public sealed class StateVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string state = value is AgentSession s
            ? s.Status switch
            {
                SessionStatus.Working => "working",
                SessionStatus.NeedsYou => "needsyou",
                _ => "finished",
            }
            : value as string ?? "";
        var want = parameter as string ?? "";
        bool invert = want.StartsWith('!');
        if (invert) want = want[1..];
        bool match = string.Equals(state, want, StringComparison.Ordinal);
        return (invert ? !match : match) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>value == parameter → bool. Two-way: checking returns the parameter (segmented picker).</summary>
public sealed class EqualsParamConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value as string, parameter as string, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? (parameter ?? Binding.DoNothing) : Binding.DoNothing;
}

/// <summary>Collapsed-pill state string → its pixel status glyph.</summary>
public sealed class StateKindIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => AgentIcons.StatusGlyph(value as string);

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

/// <summary>Bare tool names other agents report → something a human would say.</summary>
internal static class ToolPhrase
{
    public static string Of(string tool) => tool switch
    {
        "exec_command" or "shell" or "local_shell" => "Running a command",
        "apply_patch" or "edit_file" or "str_replace" => "Editing files",
        "read_file" or "view" => "Reading a file",
        "search" or "grep" or "ripgrep" => "Searching the code",
        "web_search" or "browser" => "Searching the web",
        "update_plan" or "todo" => "Updating the plan",
        "request_user_input" => "Asking you a question",
        _ => tool,
    };
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
            if (pct >= 70) return StatusBrushes.Warn;
            return StatusBrushes.Good;
        }
        catch { return StatusBrushes.Idle; }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Toast kind ("ok" | "deny" | "pass") → its colour.</summary>
public sealed class ToastBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value as string) switch
        {
            "deny" => StatusBrushes.Danger,
            "pass" => StatusBrushes.Working,
            _ => StatusBrushes.Good,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Toast kind → the glyph shown in its badge.</summary>
public sealed class ToastGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value as string) switch { "deny" => "✕", "pass" => "→", _ => "✓" };

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
