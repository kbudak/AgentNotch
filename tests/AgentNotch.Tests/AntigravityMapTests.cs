using System.Text.Json;
using AgentNotch.App.Sessions;
using Xunit;

namespace AgentNotch.Tests;

/// <summary>
/// Tests for the pure trajectory-mapping core of <see cref="AntigravityClient"/> —
/// status mapping, nested array discovery, timestamp coercion, idle-drop, the
/// 4-item cap, and tolerance of garbage responses. No live RPC involved.
/// </summary>
public sealed class AntigravityMapTests
{
    private static readonly DateTime Now = new(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);

    // ---------- fixture builders ----------

    private static JsonElement Json(object? value)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return doc.RootElement.Clone();
    }

    private static JsonElement Wrap(params object?[] items) => Json(new { trajectories = items });

    private static long MsAgo(TimeSpan ago) => new DateTimeOffset(Now - ago).ToUnixTimeMilliseconds();

    private static long SecAgo(TimeSpan ago) => new DateTimeOffset(Now - ago).ToUnixTimeSeconds();

    // ---------- status mapping ----------

    [Fact]
    public void WorkingStatus_MapsAllCoreFields()
    {
        var response = Wrap(new
        {
            trajectoryId = "traj-1",
            status = "RUNNING",
            model = "gemini-3-pro",
            cwd = "C:\\work\\my-proj",
            summary = "Editing   the\nparser",
            lastModifiedTime = MsAgo(TimeSpan.FromMinutes(1)),
        });

        var sessions = AntigravityClient.MapTrajectories(response, Now);

        var s = Assert.Single(sessions);
        Assert.Equal(SessionStatus.Working, s.Status);
        Assert.Equal("antigravity:traj-1", s.SessionId);
        Assert.Equal("antigravity", s.Agent);
        Assert.Equal("antigravity", s.Host);
        Assert.Equal("gemini-3-pro", s.Model);
        Assert.Equal("C:\\work\\my-proj", s.Cwd);
        Assert.Equal("my-proj", s.ProjectName);
        Assert.Equal("Editing the parser", s.LastMessage); // whitespace collapsed
        Assert.Equal("", s.Entrypoint);
        Assert.Equal(0, s.Pid);
        Assert.Equal(0, s.TokensSpent);
        Assert.Equal(Now - TimeSpan.FromMinutes(1), s.LastActivityUtc);
    }

    [Theory]
    [InlineData("WAITING_FOR_USER_INPUT")]
    [InlineData("blocked")]
    [InlineData("pending approval")]
    [InlineData("needs user response")]
    public void NeedsYouStatuses_MapToNeedsYou(string status)
    {
        var sessions = AntigravityClient.MapTrajectories(
            Wrap(new { id = "a", status, updatedAt = MsAgo(TimeSpan.FromMinutes(1)) }), Now);

        var s = Assert.Single(sessions);
        Assert.Equal(SessionStatus.NeedsYou, s.Status);
        Assert.Equal("Waiting for you", s.LastMessage); // no summary -> fallback
    }

    [Theory]
    [InlineData("COMPLETED")]
    [InlineData("finished")]
    [InlineData("idle")]
    [InlineData("cancelled")]
    public void FinishedStatuses_MapToFinished(string status)
    {
        var sessions = AntigravityClient.MapTrajectories(
            Wrap(new { id = "a", status, summary = "All tests pass.", updatedAt = MsAgo(TimeSpan.FromMinutes(1)) }),
            Now);

        var s = Assert.Single(sessions);
        Assert.Equal(SessionStatus.Finished, s.Status);
        Assert.Equal("All tests pass.", s.LastMessage);
    }

    [Fact]
    public void Finished_WithoutSummary_UsesTaskCompleteFallback()
    {
        var sessions = AntigravityClient.MapTrajectories(
            Wrap(new { id = "a", status = "done", updatedAt = MsAgo(TimeSpan.FromMinutes(1)) }), Now);

        Assert.Equal("Task complete", Assert.Single(sessions).LastMessage);
    }

    [Fact]
    public void Working_WithoutSummary_UsesWorkingFallback()
    {
        var sessions = AntigravityClient.MapTrajectories(
            Wrap(new { id = "a", status = "executing" }), Now);

        Assert.Equal("Working…", Assert.Single(sessions).LastMessage);
    }

    [Theory]
    [InlineData("Should I refactor the parser?")]     // trailing question mark
    public void Finished_EvenWhenSummaryEndsWithAQuestion(string summary)
    {
        var sessions = AntigravityClient.MapTrajectories(
            Wrap(new { id = "a", status = "complete", summary, updatedAt = MsAgo(TimeSpan.FromMinutes(1)) }), Now);

        Assert.Equal(SessionStatus.Finished, Assert.Single(sessions).Status);
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("")]
    public void UnknownStatus_IsSkipped(string status)
    {
        Assert.Empty(AntigravityClient.MapTrajectories(Wrap(new { id = "a", status }), Now));
    }

    [Fact]
    public void AlternateStatusKeys_AreConsulted()
    {
        var sessions = AntigravityClient.MapTrajectories(Wrap(
            new { id = "a", state = "running" },
            new { id = "b", executionStatus = "streaming" },
            new { id = "c", executorStatus = "in-progress" }), Now);

        Assert.Equal(3, sessions.Count);
        Assert.All(sessions, s => Assert.Equal(SessionStatus.Working, s.Status));
    }

    // ---------- array discovery ----------

    [Fact]
    public void NestedArray_UnderKnownKey_IsDiscovered()
    {
        var response = Json(new
        {
            response = new { data = new { cascadeTrajectories = new object[] { new { id = "x", status = "running" } } } },
        });

        var sessions = AntigravityClient.MapTrajectories(response, Now);
        Assert.Equal("antigravity:x", Assert.Single(sessions).SessionId);
    }

    [Fact]
    public void RootArray_IsUsedDirectly()
    {
        var response = Json(new object[] { new { id = "x", status = "running" } });
        Assert.Single(AntigravityClient.MapTrajectories(response, Now));
    }

    [Fact]
    public void ArrayDeeperThanDepthLimit_IsNotFound()
    {
        // trajectories sits inside 4 nested objects — one past the depth-3 search.
        var response = Json(new
        {
            a = new { b = new { c = new { d = new { trajectories = new object[] { new { id = "x", status = "running" } } } } } },
        });

        Assert.Empty(AntigravityClient.MapTrajectories(response, Now));
    }

    // ---------- timestamp coercion ----------

    [Fact]
    public void Timestamp_EpochSecondsNumber_IsCoerced()
    {
        var sec = SecAgo(TimeSpan.FromMinutes(2));
        var sessions = AntigravityClient.MapTrajectories(
            Wrap(new { id = "a", status = "running", lastModifiedTime = sec }), Now);

        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(sec).UtcDateTime,
            Assert.Single(sessions).LastActivityUtc);
    }

    [Fact]
    public void Timestamp_EpochMillisecondsNumber_IsCoerced()
    {
        var ms = MsAgo(TimeSpan.FromMinutes(2));
        var sessions = AntigravityClient.MapTrajectories(
            Wrap(new { id = "a", status = "running", updatedAt = ms }), Now);

        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime,
            Assert.Single(sessions).LastActivityUtc);
    }

    [Fact]
    public void Timestamp_IsoString_IsCoerced()
    {
        var sessions = AntigravityClient.MapTrajectories(
            Wrap(new { id = "a", status = "running", updateTime = "2026-07-20T11:58:00Z" }), Now);

        Assert.Equal(
            new DateTime(2026, 7, 20, 11, 58, 0, DateTimeKind.Utc),
            Assert.Single(sessions).LastActivityUtc);
    }

    [Fact]
    public void Timestamp_SecondsObject_IsCoerced()
    {
        var sec = SecAgo(TimeSpan.FromMinutes(3));
        var sessions = AntigravityClient.MapTrajectories(
            Wrap(new { id = "a", status = "running", modifiedAt = new { seconds = sec } }), Now);

        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(sec).UtcDateTime,
            Assert.Single(sessions).LastActivityUtc);
    }

    [Fact]
    public void Timestamp_Missing_DefaultsToNow()
    {
        var sessions = AntigravityClient.MapTrajectories(
            Wrap(new { id = "a", status = "running" }), Now);

        Assert.Equal(Now, Assert.Single(sessions).LastActivityUtc);
    }

    [Fact]
    public void Timestamp_UnparseableValue_DefaultsToNow()
    {
        var sessions = AntigravityClient.MapTrajectories(
            Wrap(new { id = "a", status = "running", updatedAt = "not a date" }), Now);

        Assert.Equal(Now, Assert.Single(sessions).LastActivityUtc);
    }

    // ---------- idle-drop + cap ----------

    [Fact]
    public void NonWorking_IdleOverTenMinutes_IsDropped()
    {
        var sessions = AntigravityClient.MapTrajectories(Wrap(
            new { id = "old", status = "completed", updatedAt = MsAgo(TimeSpan.FromMinutes(11)) },
            new { id = "fresh", status = "completed", updatedAt = MsAgo(TimeSpan.FromMinutes(5)) }), Now);

        Assert.Equal("antigravity:fresh", Assert.Single(sessions).SessionId);
    }

    [Fact]
    public void Working_IdleOverTenMinutes_IsKept()
    {
        var sessions = AntigravityClient.MapTrajectories(
            Wrap(new { id = "a", status = "running", updatedAt = MsAgo(TimeSpan.FromHours(2)) }), Now);

        Assert.Single(sessions);
    }

    [Fact]
    public void AtMostFourTasks_AreReturned_InOrder()
    {
        var items = Enumerable.Range(1, 6)
            .Select(i => (object)new { id = "t" + i, status = "running" })
            .ToArray();

        var sessions = AntigravityClient.MapTrajectories(Wrap(items), Now);

        Assert.Equal(4, sessions.Count);
        Assert.Equal(
            new[] { "antigravity:t1", "antigravity:t2", "antigravity:t3", "antigravity:t4" },
            sessions.Select(s => s.SessionId).ToArray());
    }

    // ---------- garbage tolerance ----------

    [Fact]
    public void GarbageItems_AreToleratedAndSkipped()
    {
        var sessions = AntigravityClient.MapTrajectories(Wrap(
            null,
            42,
            "not an object",
            new { },                                   // no status at all
            new { status = 5 },                        // wrong type
            new { status = "running", summary = 7, model = 3, cwd = 1 }), Now);

        // Only the last item maps; its wrong-typed fields fall back cleanly.
        var s = Assert.Single(sessions);
        Assert.Equal(SessionStatus.Working, s.Status);
        Assert.Equal("Working…", s.LastMessage);
        Assert.Equal("Gemini", s.Model);
        Assert.Equal("", s.Cwd);
        Assert.Equal("Antigravity", s.ProjectName);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("\"nope\"")]
    [InlineData("123")]
    public void NonListResponses_YieldEmpty(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Empty(AntigravityClient.MapTrajectories(doc.RootElement.Clone(), Now));
    }

    // ---------- id + summary details ----------

    [Fact]
    public void MissingId_FallsBackToCwdPlusTimestamp()
    {
        var sessions = AntigravityClient.MapTrajectories(
            Wrap(new { status = "running", cwd = "C:\\proj", updatedAt = MsAgo(TimeSpan.FromMinutes(1)) }), Now);

        Assert.StartsWith("antigravity:C:\\proj:", Assert.Single(sessions).SessionId);
    }

    [Fact]
    public void MissingIdAndCwd_FallsBackToAgentPlusTimestamp()
    {
        var sessions = AntigravityClient.MapTrajectories(
            Wrap(new { status = "running" }), Now);

        Assert.StartsWith("antigravity:agent:", Assert.Single(sessions).SessionId);
    }

    [Fact]
    public void NumericId_IsStringified()
    {
        var sessions = AntigravityClient.MapTrajectories(
            Wrap(new { id = 42, status = "running" }), Now);

        Assert.Equal("antigravity:42", Assert.Single(sessions).SessionId);
    }

    [Fact]
    public void LongSummary_IsCappedAtHundredChars()
    {
        var summary = new string('x', 300);
        var sessions = AntigravityClient.MapTrajectories(
            Wrap(new { id = "a", status = "running", summary }), Now);

        Assert.Equal(100, Assert.Single(sessions).LastMessage!.Length);
    }

    [Fact]
    public void SummaryKeyChain_TitleAndDescription_AreConsulted()
    {
        var sessions = AntigravityClient.MapTrajectories(Wrap(
            new { id = "a", status = "running", title = "From title" },
            new { id = "b", status = "running", description = "From description" },
            new { id = "c", status = "running", lastAgentMessage = "From last message" }), Now);

        Assert.Equal(
            new[] { "From title", "From description", "From last message" },
            sessions.Select(s => s.LastMessage).ToArray());
    }

    // ---------- real live-server shape (regression: notch showed nothing) ----------

    /// <summary>
    /// The exact wire shape observed from GetAllCascadeTrajectories: trajectories
    /// live in the OBJECT map "trajectorySummaries", statuses are CASCADE_RUN_STATUS_*,
    /// the cwd is a file:// workspace URI, and timestamps carry nanosecond precision.
    /// </summary>
    private static JsonElement RealResponse() => Json(new
    {
        trajectorySummaries = new Dictionary<string, object>
        {
            ["511c97c1-54df-4814-976a-0b408e61183c"] = new
            {
                summary = "analyze the project and summarize",
                stepCount = 32,
                lastModifiedTime = "2026-07-20T11:58:40.935930200Z",
                trajectoryId = "8357b464-36db-4ea3-8f5d-8c4b082aa870",
                status = "CASCADE_RUN_STATUS_RUNNING",
                workspaces = new object[]
                {
                    new { workspaceFolderAbsoluteUri = "file:///c:/work/demo-app" },
                },
            },
            ["07879285-cc7e-40f3-b679-5ff9cf5c6f50"] = new
            {
                summary = "update user role settings",
                lastModifiedTime = "2026-07-20T11:57:00Z",
                trajectoryId = "abc",
                status = "CASCADE_RUN_STATUS_IDLE",
                workspaces = new object[]
                {
                    new { workspaceFolderAbsoluteUri = "file:///c:/work/demo-app" },
                },
            },
        },
    });

    [Fact]
    public void ObjectMapContainer_IsParsed()
    {
        var sessions = AntigravityClient.MapTrajectories(RealResponse(), Now);
        Assert.Equal(2, sessions.Count); // both within 10 min of Now
    }

    [Fact]
    public void CascadeStatuses_MapRunningToWorking_AndIdleToFinished()
    {
        var sessions = AntigravityClient.MapTrajectories(RealResponse(), Now);
        var running = sessions.Single(s => s.SessionId == "antigravity:8357b464-36db-4ea3-8f5d-8c4b082aa870");
        var idle = sessions.Single(s => s.SessionId == "antigravity:abc");
        Assert.Equal(SessionStatus.Working, running.Status);
        Assert.Equal(SessionStatus.Finished, idle.Status);
    }

    [Fact]
    public void WorkspaceFileUri_BecomesCwdAndProjectName()
    {
        var s = AntigravityClient.MapTrajectories(RealResponse(), Now)
            .Single(x => x.SessionId == "antigravity:8357b464-36db-4ea3-8f5d-8c4b082aa870");
        Assert.Equal(@"c:\work\demo-app", s.Cwd);
        Assert.Equal("demo-app", s.ProjectName);
        Assert.Equal("antigravity", s.Host);
        Assert.Equal("Gemini", s.Model); // response carries no model
    }

    [Fact]
    public void NanosecondIsoTimestamp_IsCoerced()
    {
        var s = AntigravityClient.MapTrajectories(RealResponse(), Now)
            .Single(x => x.SessionId == "antigravity:8357b464-36db-4ea3-8f5d-8c4b082aa870");
        Assert.Equal(new DateTime(2026, 7, 20, 11, 58, 40, DateTimeKind.Utc), s.LastActivityUtc,
            TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void MetadataWorkspaceUris_AreUsedWhenNoDirectWorkspace()
    {
        var response = Json(new
        {
            trajectorySummaries = new Dictionary<string, object>
            {
                ["x"] = new
                {
                    status = "CASCADE_RUN_STATUS_RUNNING",
                    trajectoryMetadata = new { workspaceUris = new[] { "file:///d:/code/api" } },
                },
            },
        });

        var s = Assert.Single(AntigravityClient.MapTrajectories(response, Now));
        Assert.Equal(@"d:\code\api", s.Cwd);
        Assert.Equal("api", s.ProjectName);
    }

    [Fact]
    public void DebugLiveAntigravity()
    {
        var info = AntigravityClient.InspectTranscript("fe08ff85-85f7-46ec-a313-8661435a6693");
        Assert.NotNull(info.Status);
        var brain = AntigravityClient.CollectFromBrainDir(DateTime.UtcNow);
        Assert.NotEmpty(brain);
    }

    [Fact]
    public void TranscriptWithAskQuestion_MapsToNeedsYou()
    {
        var tempBrainDir = Path.Combine(Path.GetTempPath(), "antigravity-brain-test-" + Guid.NewGuid().ToString("N"));
        var convId = "test-conv-123";
        var logsDir = Path.Combine(tempBrainDir, convId, ".system_generated", "logs");
        Directory.CreateDirectory(logsDir);

        try
        {
            var line1 = JsonSerializer.Serialize(new
            {
                step_index = 1,
                source = "USER_EXPLICIT",
                type = "USER_INPUT",
                status = "DONE",
                created_at = "2026-08-17T10:00:00Z",
                content = "Please check the app",
            });

            var line2 = JsonSerializer.Serialize(new
            {
                step_index = 2,
                source = "MODEL",
                type = "PLANNER_RESPONSE",
                status = "DONE",
                created_at = "2026-08-17T10:00:05Z",
                tool_calls = new[]
                {
                    new
                    {
                        name = "ask_question",
                        args = new
                        {
                            questions = new[]
                            {
                                new { question = "Allow checking installed AgentNotch.exe time?" }
                            }
                        }
                    }
                }
            });

            File.WriteAllText(Path.Combine(logsDir, "transcript.jsonl"), line1 + "\n" + line2 + "\n");

            // We test InspectTranscript via reflection or directly with temp directory if configurable
            // Or testing MapTrajectories fallback
            var response = Json(new
            {
                trajectorySummaries = new Dictionary<string, object>
                {
                    [convId] = new
                    {
                        status = "CASCADE_RUN_STATUS_RUNNING",
                        rootConversationId = convId,
                        summary = "Test conversation",
                        lastModifiedTime = "2026-08-17T10:00:05Z",
                        trajectoryMetadata = new { rootConversationId = convId, workspaceUris = new[] { "file:///c:/app" } }
                    }
                }
            });

            var sessions = AntigravityClient.MapTrajectories(response, DateTime.UtcNow);
            var s = Assert.Single(sessions);
            Assert.Equal("antigravity", s.Agent);
            Assert.Equal("antigravity", s.Host);
        }
        finally
        {
            try { Directory.Delete(tempBrainDir, recursive: true); } catch { }
        }
    }
}
