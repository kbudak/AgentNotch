namespace AgentNotch.App.UI;

/// <summary>One rendered diff row. Kind: "add" | "del" | "ctx" | "gap".</summary>
public sealed record DiffLine(string Kind, string? OldNo, string? NewNo, string Text);

/// <summary>
/// Minimal line diff for the permission card: LCS-based, with unchanged context
/// collapsed into "gap" rows and a hard cap on rendered lines. Pure and
/// exception-free — a pathological input degrades to a coarse diff, never a throw.
/// </summary>
public static class DiffRenderer
{
    private const int MaxRenderedLines = 200;
    private const int ContextLines = 3;

    /// <summary>Upper bound on the LCS table (rows*cols); above it we fall back to prefix/suffix trimming.</summary>
    private const long MaxLcsCells = 2_250_000;

    /// <summary>Diff of an Edit tool call (old_string → new_string).</summary>
    public static IReadOnlyList<DiffLine> Render(string oldText, string newText)
    {
        try
        {
            var a = SplitLines(oldText);
            var b = SplitLines(newText);
            var raw = (long)a.Length * b.Length <= MaxLcsCells
                ? LcsDiff(a, b)
                : CoarseDiff(a, b);

            bool anyChange = false;
            foreach (var l in raw)
            {
                if (l.Kind != "ctx") { anyChange = true; break; }
            }
            if (!anyChange) return Array.Empty<DiffLine>();

            return Collapse(raw);
        }
        catch
        {
            return Array.Empty<DiffLine>();
        }
    }

    /// <summary>All-added rendering for a Write tool call (new file content), capped.</summary>
    public static IReadOnlyList<DiffLine> RenderAdded(string newText)
    {
        try
        {
            var b = SplitLines(newText);
            var result = new List<DiffLine>(Math.Min(b.Length, MaxRenderedLines) + 1);
            int take = Math.Min(b.Length, MaxRenderedLines - 1);
            for (int i = 0; i < take; i++)
                result.Add(new DiffLine("add", null, (i + 1).ToString(), b[i]));
            if (b.Length > take)
                result.Add(new DiffLine("gap", null, null, $"… {b.Length - take} more lines"));
            return result;
        }
        catch
        {
            return Array.Empty<DiffLine>();
        }
    }

    private static string[] SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }

    /// <summary>Classic LCS backtrack producing ctx/del/add rows with 1-based line numbers.</summary>
    private static List<DiffLine> LcsDiff(string[] a, string[] b)
    {
        int n = a.Length, m = b.Length;
        var dp = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                dp[i, j] = string.Equals(a[i], b[j], StringComparison.Ordinal)
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        var result = new List<DiffLine>(n + m);
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (string.Equals(a[x], b[y], StringComparison.Ordinal))
            {
                result.Add(new DiffLine("ctx", (x + 1).ToString(), (y + 1).ToString(), a[x]));
                x++; y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1])
            {
                result.Add(new DiffLine("del", (x + 1).ToString(), null, a[x]));
                x++;
            }
            else
            {
                result.Add(new DiffLine("add", null, (y + 1).ToString(), b[y]));
                y++;
            }
        }
        while (x < n) { result.Add(new DiffLine("del", (x + 1).ToString(), null, a[x])); x++; }
        while (y < m) { result.Add(new DiffLine("add", null, (y + 1).ToString(), b[y])); y++; }
        return result;
    }

    /// <summary>Fallback for very large inputs: trim common prefix/suffix, everything between is del+add.</summary>
    private static List<DiffLine> CoarseDiff(string[] a, string[] b)
    {
        int prefix = 0;
        while (prefix < a.Length && prefix < b.Length &&
               string.Equals(a[prefix], b[prefix], StringComparison.Ordinal))
            prefix++;

        int suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix &&
               string.Equals(a[a.Length - 1 - suffix], b[b.Length - 1 - suffix], StringComparison.Ordinal))
            suffix++;

        var result = new List<DiffLine>();
        for (int i = 0; i < prefix; i++)
            result.Add(new DiffLine("ctx", (i + 1).ToString(), (i + 1).ToString(), a[i]));
        for (int i = prefix; i < a.Length - suffix; i++)
            result.Add(new DiffLine("del", (i + 1).ToString(), null, a[i]));
        for (int j = prefix; j < b.Length - suffix; j++)
            result.Add(new DiffLine("add", null, (j + 1).ToString(), b[j]));
        for (int k = 0; k < suffix; k++)
        {
            int ai = a.Length - suffix + k;
            int bj = b.Length - suffix + k;
            result.Add(new DiffLine("ctx", (ai + 1).ToString(), (bj + 1).ToString(), a[ai]));
        }
        return result;
    }

    /// <summary>Collapse long unchanged runs into gap rows and enforce the render cap.</summary>
    private static List<DiffLine> Collapse(List<DiffLine> raw)
    {
        var keep = new bool[raw.Count];
        for (int i = 0; i < raw.Count; i++)
        {
            if (raw[i].Kind == "ctx") continue;
            int from = Math.Max(0, i - ContextLines);
            int to = Math.Min(raw.Count - 1, i + ContextLines);
            for (int k = from; k <= to; k++) keep[k] = true;
        }

        var result = new List<DiffLine>(Math.Min(raw.Count, MaxRenderedLines) + 1);
        int idx = 0;
        while (idx < raw.Count)
        {
            if (result.Count >= MaxRenderedLines)
            {
                result.Add(new DiffLine("gap", null, null, $"… {raw.Count - idx} more lines"));
                break;
            }

            if (keep[idx])
            {
                result.Add(raw[idx]);
                idx++;
            }
            else
            {
                int j = idx;
                while (j < raw.Count && !keep[j]) j++;
                int skipped = j - idx;
                result.Add(new DiffLine("gap", null, null,
                    $"⋯ {skipped} unchanged line{(skipped == 1 ? "" : "s")}"));
                idx = j;
            }
        }
        return result;
    }
}
