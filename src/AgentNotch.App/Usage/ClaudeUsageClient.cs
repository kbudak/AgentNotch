using System.Diagnostics;
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

    /// <summary>How often, at most, we ask Claude Code to renew an expired token.</summary>
    internal static readonly TimeSpan RenewEvery = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RenewTimeout = TimeSpan.FromMinutes(2);
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
    private long _loggedExpiryMs; // the expiry we already reported, so it is logged once, not every backoff
    private bool _tokenExpired;
    private DateTime _lastRenewUtc = DateTime.MinValue;
    private int _renewing;
    private volatile bool _disposed;

    public static ClaudeUsageClient? Shared { get; set; }
    public static IReadOnlyList<UsageWindow> SharedWindows => Shared?.Windows ?? Array.Empty<UsageWindow>();

    public ClaudeUsageClient(string? claudeHome = null)
    {
        Shared = this;
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
    public IReadOnlyList<UsageWindow> Windows => RollOver(_windows, DateTime.UtcNow);

    /// <summary>
    /// A window whose reset time has passed has started over, so the percentage we
    /// hold belongs to a window that already ended. Shown with a "now" countdown —
    /// what the header did for hours while Claude Code's token sat expired — it read
    /// as current and was simply wrong: 69% used where the fresh window stood at 3%.
    /// A rolled-over window is shown as the fresh window it became, 0% with no known
    /// reset, until the next successful read replaces it.
    /// </summary>
    internal static IReadOnlyList<UsageWindow> RollOver(IReadOnlyList<UsageWindow> windows, DateTime nowUtc)
    {
        if (!windows.Any(w => w.ResetsAtUtc is { } r && r <= nowUtc)) return windows;
        return windows
            .Select(w => w.ResetsAtUtc is { } r && r <= nowUtc ? w with { UsedPct = 0, ResetsAtUtc = null } : w)
            .ToList();
    }

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
                // Not signed in, or the token expired. Keep whatever we last knew rather
                // than blanking out — and if it expired, ask Claude Code to renew it.
                if (_tokenExpired && ShouldRenew(_lastRenewUtc, DateTime.UtcNow))
                    AskClaudeCodeToRenewToken();
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

    internal static bool ShouldRenew(DateTime lastAttemptUtc, DateTime nowUtc)
        => nowUtc - lastAttemptUtc >= RenewEvery;

    /// <summary>
    /// Has Claude Code renew its own token, by running its local `/usage` command.
    ///
    /// Claude Code only rewrites ~\.claude\.credentials.json while one of its CLI
    /// sessions runs, and sessions launched from Claude Desktop get their auth from the
    /// desktop app instead — so a day in Desktop left the token expired and the header
    /// frozen for hours. `claude auth status` was checked and renews nothing; `claude -p
    /// /usage` renews the token and refreshes the usage figures, and it is a local
    /// command, so it makes no model call and spends none of the limit it reports.
    ///
    /// `--setting-sources project` in an empty folder of our own keeps the user's hooks
    /// and status line out of it (without it the run fired every Stop hook and briefly
    /// put a phantom session on the notch), and `--no-session-persistence` keeps it out
    /// of the transcript history. AgentNotch never touches the credentials itself.
    /// </summary>
    private void AskClaudeCodeToRenewToken()
    {
        if (Interlocked.Exchange(ref _renewing, 1) == 1) return;
        _lastRenewUtc = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AgentNotch", "usage-refresh");
                Directory.CreateDirectory(dir);

                var psi = new ProcessStartInfo("cmd.exe",
                    "/c claude -p /usage --no-session-persistence --setting-sources project")
                {
                    WorkingDirectory = dir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var process = Process.Start(psi);
                if (process is null)
                {
                    AppLog.Write("usage: could not start claude to renew its token");
                    return;
                }
                // Drain both pipes, or a full buffer would stall the child.
                var drainOut = process.StandardOutput.ReadToEndAsync();
                var drainErr = process.StandardError.ReadToEndAsync();

                using var timeout = new CancellationTokenSource(RenewTimeout);
                try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                    AppLog.Write("usage: claude /usage timed out while renewing the token");
                    return;
                }
                await Task.WhenAll(drainOut, drainErr).ConfigureAwait(false);

                AppLog.Write($"usage: asked Claude Code to renew its token (claude -p /usage exited {process.ExitCode})");
                lock (_gate) { _nextTryUtc = DateTime.MinValue; } // read the renewed token on the next poll
            }
            catch (Exception ex)
            {
                AppLog.Write("usage: renewing the token via claude failed: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _renewing, 0);
            }
        });
    }

    /// <summary>The OAuth access token Claude Code stored, or null when absent/expired.</summary>
    private string? ReadAccessToken()
    {
        _tokenExpired = false;
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
                if (DateTime.UtcNow >= expiresUtc - TimeSpan.FromSeconds(30))
                {
                    _tokenExpired = true;
                    // This used to return silently, so a header frozen for hours left no
                    // trace. Claude Code only rewrites this token when one of ITS sessions
                    // runs; a day spent in Claude Desktop leaves it expired.
                    if (_loggedExpiryMs != expiresAtMs)
                    {
                        _loggedExpiryMs = expiresAtMs;
                        AppLog.Write($"usage: Claude Code's token expired at {expiresUtc:u}; asking Claude Code to renew it");
                    }
                    return null;
                }
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
