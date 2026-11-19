using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentNotch.App.Diagnostics;
using Microsoft.Data.Sqlite;

namespace AgentNotch.App.Usage;

/// <summary>
/// Reads Cursor's authentication token from %APPDATA%\Cursor\User\globalStorage\state.vscdb
/// and queries the Cursor period usage endpoint (https://api2.cursor.sh/aiserver.v1.DashboardService/GetCurrentPeriodUsage).
/// Caches responses for 2 minutes to minimize network traffic. Never throws.
/// </summary>
public static class CursorUsageClient
{
    private const string RpcUrl =
        "https://api2.cursor.sh/aiserver.v1.DashboardService/GetCurrentPeriodUsage";

    private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private static readonly object Gate = new();
    private static DateTime _lastAttemptUtc = DateTime.MinValue;
    private static DateTime _lastFetchedUtc = DateTime.MinValue;
    private static bool _refreshing;

    private static volatile IReadOnlyList<UsageWindow> _latestWindows = Array.Empty<UsageWindow>();
    private static volatile string? _latestModel;

    public static IReadOnlyList<UsageWindow> LatestUsage
    {
        get
        {
            RefreshIfDue();
            return _latestWindows;
        }
    }

    public static string? LatestModel
    {
        get
        {
            RefreshIfDue();
            return _latestModel;
        }
    }

    public static string DbPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Cursor", "User", "globalStorage", "state.vscdb");

    public static void RefreshIfDue()
    {
        try
        {
            lock (Gate)
            {
                if (_refreshing) return;
                var now = DateTime.UtcNow;
                if (now - _lastAttemptUtc < MinRefreshInterval) return;
                if (now - _lastFetchedUtc < FreshFor && _latestWindows.Count > 0) return;
                _refreshing = true;
                _lastAttemptUtc = now;
            }
            _ = Task.Run(RefreshAsync);
        }
        catch (Exception ex)
        {
            AppLog.Write("cursor_usage: refresh schedule failed: " + ex.Message);
            lock (Gate) { _refreshing = false; }
        }
    }

    private static async Task RefreshAsync()
    {
        try
        {
            var db = DbPath;
            if (!File.Exists(db)) return;

            string? token = null;
            string? modelPref = null;

            using (var conn = OpenReadOnly(db))
            {
                if (conn is not null)
                {
                    token = ReadItem(conn, "cursorAuth/accessToken");
                    modelPref = ReadItem(conn, "cursor/lastSingleModelPreference");
                }
            }

            if (!string.IsNullOrEmpty(modelPref))
            {
                _latestModel = ParseModelPreference(modelPref);
            }

            if (string.IsNullOrEmpty(token)) return;

            using var req = new HttpRequestMessage(HttpMethod.Post, RpcUrl)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");

            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;

            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var windows = ParseUsageJson(body);
            if (windows.Count > 0)
            {
                _latestWindows = windows;
                _lastFetchedUtc = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("cursor_usage: refresh failed: " + ex.Message);
        }
        finally
        {
            lock (Gate) { _refreshing = false; }
        }
    }

    internal static string? ParseModelPreference(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("composer", out var comp) &&
                comp.ValueKind == JsonValueKind.String)
            {
                var val = comp.GetString();
                if (string.IsNullOrWhiteSpace(val) || val.Equals("default", StringComparison.OrdinalIgnoreCase))
                    return "Composer";
                return CleanCursorModel(val);
            }
        }
        catch { }
        return null;
    }

    public static string CleanCursorModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model) || model.Equals("default", StringComparison.OrdinalIgnoreCase))
            return "Composer";
        if (model.StartsWith("claude-3-5-sonnet", StringComparison.OrdinalIgnoreCase)) return "Sonnet 3.5";
        if (model.StartsWith("claude-3-7-sonnet", StringComparison.OrdinalIgnoreCase)) return "Sonnet 3.7";
        if (model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)) return "Claude";
        if (model.StartsWith("gpt-4o", StringComparison.OrdinalIgnoreCase)) return "GPT-4o";
        if (model.StartsWith("o1", StringComparison.OrdinalIgnoreCase)) return "o1";
        if (model.StartsWith("o3", StringComparison.OrdinalIgnoreCase)) return "o3";
        if (model.StartsWith("composer-", StringComparison.OrdinalIgnoreCase)) return "Composer " + model["composer-".Length..];
        return model;
    }

    internal static List<UsageWindow> ParseUsageJson(string json)
    {
        var list = new List<UsageWindow>(2);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            DateTime? resetUtc = null;
            if (root.TryGetProperty("billingCycleEnd", out var endProp) && endProp.ValueKind == JsonValueKind.String)
            {
                var s = endProp.GetString();
                if (!string.IsNullOrEmpty(s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
                {
                    resetUtc = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
                }
            }

            if (root.TryGetProperty("planUsage", out var plan) && plan.ValueKind == JsonValueKind.Object)
            {
                double total = 0, auto = 0;
                if (plan.TryGetProperty("totalPercentUsed", out var tp) && tp.TryGetDouble(out var td))
                    total = Math.Clamp(td, 0, 100);
                if (plan.TryGetProperty("autoPercentUsed", out var ap) && ap.TryGetDouble(out var ad))
                    auto = Math.Clamp(ad, 0, 100);

                if (auto > 0)
                    list.Add(new UsageWindow("Auto", auto, resetUtc));

                list.Add(new UsageWindow("Plan", total, resetUtc));
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("cursor_usage: parse json failed: " + ex.Message);
        }
        return list;
    }

    private static SqliteConnection? OpenReadOnly(string dbPath)
    {
        try
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 2,
            }.ToString();
            var conn = new SqliteConnection(cs);
            conn.Open();
            return conn;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadItem(SqliteConnection conn, string key)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM ItemTable WHERE key = $k LIMIT 1";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
        catch
        {
            return null;
        }
    }
}
