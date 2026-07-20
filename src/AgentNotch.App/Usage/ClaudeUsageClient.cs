using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using AgentNotch.App.Diagnostics;

namespace AgentNotch.App.Usage;

/// <summary>One rate-limit window ("5h", "7d") from Claude's usage endpoint.</summary>
public sealed record UsageWindow(string Label, double UsedPct, DateTime? ResetsAtUtc)
{
    /// <summary>"20%" — the share of this window already consumed.</summary>
    public string PercentText => ((int)Math.Round(UsedPct)).ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>"2h14m" / "6d14h" — time left before the window resets ("" when unknown).</summary>
    public string ResetText
    {
        get
        {
            if (ResetsAtUtc is not { } resets) return "";
            var left = resets - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return "now";
            if (left.TotalDays >= 1) return $"{(int)left.TotalDays}d{left.Hours}h";
            if (left.TotalHours >= 1) return $"{(int)left.TotalHours}h{left.Minutes}m";
            return $"{Math.Max(1, (int)left.TotalMinutes)}m";
        }
    }
}

/// <summary>
/// Reads the signed-in Claude plan's rate-limit windows, the same numbers
/// <c>/usage</c> shows: the OAuth token Claude Code already stored in
/// <c>~\.claude\.credentials.json</c> is sent to Anthropic's own usage endpoint.
/// This is the app's ONLY outbound network call.
///
/// The endpoint rate-limits aggressive polling, so a successful read is reused
/// for 150s, failures back off for 5 minutes, and the last good snapshot is
/// persisted so a restart still shows limits instead of a blank header. An
/// expired token is never refreshed by us — using Claude Code refreshes it.
/// Never throws; <see cref="Windows"/> is simply empty when unavailable.
/// </summary>
public sealed class ClaudeUsageClient : IDisposable
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";

    private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan BackoffFor = TimeSpan.FromMinutes(5);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Windows we surface, in display order.</summary>
    private static readonly (string Key, string Label)[] Known =
    {
        ("five_hour", "5h"),
        ("seven_day", "7d"),
        ("seven_day_opus", "7d Opus"),
    };

    private readonly object _gate = new();
    private readonly string _credentialsPath;
    private readonly string _cachePath;
    private volatile IReadOnlyList<UsageWindow> _windows = Array.Empty<UsageWindow>();
    private DateTime _fetchedUtc = DateTime.MinValue;
    private DateTime _nextTryUtc = DateTime.MinValue;
    private bool _refreshing;
    private volatile bool _disposed;

    public ClaudeUsageClient(string? claudeHome = null)
    {
        var home = string.IsNullOrWhiteSpace(claudeHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : claudeHome;
        _credentialsPath = Path.Combine(home, ".credentials.json");
        _cachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentNotch", "usage.json");
        _windows = LoadCache();
    }

    /// <summary>Latest known windows; empty until the first successful read.</summary>
    public IReadOnlyList<UsageWindow> Windows => _windows;

    /// <summary>
    /// Requests a refresh. Non-blocking and safe to call every poll: at most one
    /// runs at a time, a fresh snapshot is reused, and failures back off.
    /// </summary>
    public void RefreshIfDue()
    {
        try
        {
            if (_disposed) return;
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                if (_refreshing) return;
                if (_windows.Count > 0 && now - _fetchedUtc < FreshFor) return;
                if (now < _nextTryUtc) return;
                _refreshing = true;
            }
            _ = Task.Run(RefreshAsync);
        }
        catch (Exception ex)
        {
            AppLog.Write("usage: scheduling failed: " + ex.Message);
            lock (_gate) { _refreshing = false; }
        }
    }

    public void Dispose() => _disposed = true;

    private async Task RefreshAsync()
    {
        try
        {
            var token = ReadAccessToken();
            if (token is null)
            {
                // Not signed in (or the token expired — Claude Code rewrites it on
                // next use). Keep whatever we last knew rather than blanking out.
                lock (_gate) { _nextTryUtc = DateTime.UtcNow + BackoffFor; }
                return;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", "AgentNotch");

            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                AppLog.Write($"usage: HTTP {(int)response.StatusCode} — backing off");
                lock (_gate) { _nextTryUtc = DateTime.UtcNow + BackoffFor; }
                return;
            }

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var windows = Parse(body);
            if (windows.Count == 0)
            {
                AppLog.Write("usage: no recognized windows in response");
                lock (_gate) { _nextTryUtc = DateTime.UtcNow + BackoffFor; }
                return;
            }

            _windows = windows;
            lock (_gate)
            {
                _fetchedUtc = DateTime.UtcNow;
                _nextTryUtc = DateTime.MinValue;
            }
            SaveCache(windows);
        }
        catch (Exception ex)
        {
            AppLog.Write("usage: refresh failed: " + ex.Message);
            lock (_gate) { _nextTryUtc = DateTime.UtcNow + BackoffFor; }
        }
        finally
        {
            lock (_gate) { _refreshing = false; }
        }
    }

    /// <summary>Maps the usage payload to windows. Pure — unit-tested.</summary>
    public static List<UsageWindow> Parse(string json)
    {
        var windows = new List<UsageWindow>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return windows;

            foreach (var (key, label) in Known)
            {
                if (!root.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) continue;
                if (!w.TryGetProperty("utilization", out var u)) continue;

                double used;
                if (u.ValueKind == JsonValueKind.Number) used = u.GetDouble();
                else if (u.ValueKind == JsonValueKind.String &&
                         double.TryParse(u.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                    used = parsed;
                else continue;
                if (!double.IsFinite(used)) continue;

                // The extra per-model windows only add noise until they're actually used.
                if (key != "five_hour" && key != "seven_day" && used <= 0) continue;

                windows.Add(new UsageWindow(label, Math.Clamp(used, 0, 100), ResetsAt(w)));
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("usage: parse failed: " + ex.Message);
        }
        return windows;
    }

    private static DateTime? ResetsAt(JsonElement window)
    {
        if (!window.TryGetProperty("resets_at", out var r)) return null;
        try
        {
            if (r.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(r.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
                return dto.UtcDateTime;

            if (r.ValueKind == JsonValueKind.Number && r.TryGetDouble(out var epoch))
                return DateTimeOffset.FromUnixTimeMilliseconds((long)(epoch < 1e12 ? epoch * 1000 : epoch)).UtcDateTime;
        }
        catch { /* unparseable — no reset time */ }
        return null;
    }

    /// <summary>The OAuth access token Claude Code stored, or null when absent/expired.</summary>
    private string? ReadAccessToken()
    {
        try
        {
            if (!File.Exists(_credentialsPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(_credentialsPath));
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) ||
                oauth.ValueKind != JsonValueKind.Object)
                return null;

            if (oauth.TryGetProperty("expiresAt", out var exp) &&
                exp.ValueKind == JsonValueKind.Number &&
                exp.TryGetInt64(out var expiresAtMs))
            {
                var expiresUtc = DateTimeOffset.FromUnixTimeMilliseconds(expiresAtMs).UtcDateTime;
                if (DateTime.UtcNow >= expiresUtc - TimeSpan.FromSeconds(30)) return null; // stale
            }

            return oauth.TryGetProperty("accessToken", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
        }
        catch (Exception ex)
        {
            AppLog.Write("usage: credentials unreadable: " + ex.Message);
            return null;
        }
    }

    // ---------- disk cache (survives restarts and rate-limit windows) ----------

    private sealed record CachedWindow(string Label, double UsedPct, DateTime? ResetsAtUtc);

    private IReadOnlyList<UsageWindow> LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return Array.Empty<UsageWindow>();
            var cached = JsonSerializer.Deserialize<List<CachedWindow>>(File.ReadAllText(_cachePath));
            if (cached is null) return Array.Empty<UsageWindow>();
            return cached.Select(c => new UsageWindow(c.Label, c.UsedPct, c.ResetsAtUtc)).ToList();
        }
        catch
        {
            return Array.Empty<UsageWindow>();
        }
    }

    private void SaveCache(IReadOnlyList<UsageWindow> windows)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var payload = windows.Select(w => new CachedWindow(w.Label, w.UsedPct, w.ResetsAtUtc)).ToList();
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(payload));
        }
        catch (Exception ex)
        {
            AppLog.Write("usage: cache write failed: " + ex.Message);
        }
    }
}
