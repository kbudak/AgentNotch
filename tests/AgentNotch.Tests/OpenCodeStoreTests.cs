using AgentNotch.App.Sessions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentNotch.Tests;

/// <summary>
/// OpenCode is a read-only source: the notch derives working / finished / needs-you
/// from the CLI's own SQLite store. These build a throwaway store with the exact
/// tables the collector reads and pin the derivation, so a schema or status-rule
/// regression fails here rather than silently dropping the row from the notch.
/// </summary>
public class OpenCodeStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _db;

    public OpenCodeStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "opencode-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _db = Path.Combine(_dir, "opencode.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void CreateSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            create table session_v2 (
                id text primary key, directory text, title text, model text,
                tokens_input integer, tokens_output integer,
                tokens_cache_read integer, tokens_cache_write integer,
                time_created integer, time_updated integer, time_idle integer, time_archived integer);
            create table session_message (
                session_id text, type text, seq integer, data text);
            create table session_pending (
                id text, session_id text, type text, data text);
            """;
        cmd.ExecuteNonQuery();
    }

    private void Seed(
        string id, string directory, long created, long updated, long? idle,
        string lastType, string? assistantData = null, string? pendingType = null, string? pendingData = null)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = _db }.ToString());
        conn.Open();
        if (!TableExists(conn)) CreateSchema(conn);

        using (var s = conn.CreateCommand())
        {
            s.CommandText =
                "insert or replace into session_v2 " +
                "(id, directory, title, model, tokens_input, tokens_output, tokens_cache_read, tokens_cache_write, " +
                " time_created, time_updated, time_idle, time_archived) " +
                "values ($id,$dir,'My project','{\"id\":\"gpt-x\"}',10,20,30,40,$c,$u,$i,0)";
            s.Parameters.AddWithValue("$id", id);
            s.Parameters.AddWithValue("$dir", directory);
            s.Parameters.AddWithValue("$c", created);
            s.Parameters.AddWithValue("$u", updated);
            s.Parameters.AddWithValue("$i", (object?)idle ?? DBNull.Value);
            s.ExecuteNonQuery();
        }

        using (var m = conn.CreateCommand())
        {
            m.CommandText = "insert into session_message (session_id, type, seq, data) values ($s,'user',1,'{}')";
            m.Parameters.AddWithValue("$s", id);
            m.ExecuteNonQuery();
        }
        if (lastType != "user")
        {
            using var m = conn.CreateCommand();
            m.CommandText = "insert into session_message (session_id, type, seq, data) values ($s,$t,2,$d)";
            m.Parameters.AddWithValue("$s", id);
            m.Parameters.AddWithValue("$t", lastType);
            m.Parameters.AddWithValue("$d", (object?)assistantData ?? "{}");
            m.ExecuteNonQuery();
        }

        if (pendingType is not null)
        {
            using var p = conn.CreateCommand();
            p.CommandText = "insert into session_pending (id, session_id, type, data) values ('p1',$s,$t,$d)";
            p.Parameters.AddWithValue("$s", id);
            p.Parameters.AddWithValue("$t", pendingType);
            p.Parameters.AddWithValue("$d", (object?)pendingData ?? "{}");
            p.ExecuteNonQuery();
        }
    }

    private static bool TableExists(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select count(*) from sqlite_master where type='table' and name='session_v2'";
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    private static long Ms(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    [Fact]
    public void WorkingSessionWithRunningTool_ReadsAsWorking()
    {
        var now = DateTime.UtcNow;
        var assistant = """
            {"content":[{"type":"reasoning","text":"thinking…"},
                        {"type":"tool","name":"read","state":{"status":"running","input":{"path":"C:\\code\\api\\server.ts"}}}]}
            """;
        Seed("ses_working", @"C:\code\api", Ms(now.AddMinutes(-3)), Ms(now.AddSeconds(-2)),
             idle: Ms(now.AddMinutes(-9)), lastType: "assistant", assistantData: assistant);

        var sessions = OpenCodeStore.Collect(_db, now);

        var s = Assert.Single(sessions);
        Assert.Equal(SessionStatus.Working, s.Status);
        Assert.Equal("opencode", s.Agent);
        Assert.Equal("api", s.ProjectName);
        Assert.Equal("read", s.CurrentTool);
        Assert.Contains("server.ts", s.CurrentTarget);
        Assert.False(s.IsThinking);
        Assert.Equal(70, s.TokensSpent); // 10 + 20 + 40 (cache-read excluded)
    }

    [Fact]
    public void LastMessageIdle_ReadsAsFinished()
    {
        var now = DateTime.UtcNow;
        var assistant = """
            {"content":[{"type":"text","text":"All done — tests pass."}]}
            """;
        Seed("ses_done", @"C:\code\\web", Ms(now.AddMinutes(-6)), Ms(now.AddMinutes(-1)),
             idle: Ms(now.AddMinutes(-1)), lastType: "assistant", assistantData: assistant);
        // The idle row is the latest, so override the trailing message with one.
        AppendMessage("ses_done", "idle", 3, "{\"outcome\":\"succeeded\"}");

        var sessions = OpenCodeStore.Collect(_db, now);

        var s = Assert.Single(sessions);
        Assert.Equal(SessionStatus.Finished, s.Status);
        Assert.Equal("web", s.ProjectName);
        Assert.Equal("gpt-x", s.Model);
    }

    [Fact]
    public void PendingPermission_ReadsAsNeedsYou()
    {
        var now = DateTime.UtcNow;
        Seed("ses_ask", @"C:\code\\cli", Ms(now.AddMinutes(-2)), Ms(now.AddSeconds(-1)),
             idle: null, lastType: "assistant",
             assistantData: "{\"content\":[]}",
             pendingType: "permission",
             pendingData: "{\"description\":\"Allow running npm install?\"}");

        var sessions = OpenCodeStore.Collect(_db, now);

        var s = Assert.Single(sessions);
        Assert.Equal(SessionStatus.NeedsYou, s.Status);
        Assert.True(s.NeedsApproval);
        Assert.Contains("npm install", s.LastMessage);
    }

    [Fact]
    public void SiblingSessionsInOneDirectory_CollapseToTheMostUrgent()
    {
        var now = DateTime.UtcNow;
        // Two sessions share a working directory: one finished, one still working.
        Seed("ses_old", @"C:\code\\shared", Ms(now.AddMinutes(-8)), Ms(now.AddMinutes(-5)),
             idle: Ms(now.AddMinutes(-5)), lastType: "idle");
        Seed("ses_new", @"C:\code\\shared", Ms(now.AddMinutes(-4)), Ms(now.AddSeconds(-3)),
             idle: Ms(now.AddMinutes(-4)), lastType: "user");
        // ses_new's latest message: a fresh user turn (working).
        AppendMessage("ses_new", "user", 3, "{\"text\":\"keep going\"}");

        var sessions = OpenCodeStore.Collect(_db, now);

        var s = Assert.Single(sessions);
        Assert.Equal(SessionStatus.Working, s.Status);
    }

    [Fact]
    public void ModelJson_ExtractsCleanModelName()
    {
        var now = DateTime.UtcNow;
        Seed("ses_deepseek", @"C:\code\project", Ms(now.AddMinutes(-5)), Ms(now.AddSeconds(-10)),
             idle: null, lastType: "user");

        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _db }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "update session_v2 set model = $m where id = 'ses_deepseek'";
            cmd.Parameters.AddWithValue("$m", "{\"id\":\"evrenllm/deepseek-v4.1-flash\"}");
            cmd.ExecuteNonQuery();
        }

        var sessions = OpenCodeStore.Collect(_db, now);
        var s = Assert.Single(sessions);
        Assert.Equal("deepseek-v4.1-flash", s.Model);
    }

    [Fact]
    public void MissingDatabase_YieldsNothing()
        => Assert.Empty(OpenCodeStore.Collect(Path.Combine(_dir, "nope.db"), DateTime.UtcNow));

    private void AppendMessage(string sessionId, string type, int seq, string data)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = _db }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "insert into session_message (session_id, type, seq, data) values ($s,$t,$q,$d)";
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$t", type);
        cmd.Parameters.AddWithValue("$q", seq);
        cmd.Parameters.AddWithValue("$d", data);
        cmd.ExecuteNonQuery();
    }
}

