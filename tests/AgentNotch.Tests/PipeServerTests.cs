using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AgentNotch.App.Ipc;
using AgentNotch.Protocol;
using Xunit;

namespace AgentNotch.Tests;

/// <summary>
/// PipeServer integration tests over a real named pipe (unique name per test,
/// so a running AgentNotch instance can never interfere). Every wait is
/// wrapped in a 5s timeout so a regression can never hang the test run.
/// </summary>
public sealed class PipeServerTests
{
    private const int TimeoutMs = 5000;

    // ---------- helpers ----------

    private static string UniquePipeName() => "agentnotch-test-" + Guid.NewGuid().ToString("N");

    private static async Task<T> Await<T>(Task<T> task, string what)
    {
        Task winner = await Task.WhenAny(task, Task.Delay(TimeoutMs));
        Assert.True(winner == task, $"timed out waiting for {what}");
        return await task;
    }

    private static async Task Await(Task task, string what)
    {
        Task winner = await Task.WhenAny(task, Task.Delay(TimeoutMs));
        Assert.True(winner == task, $"timed out waiting for {what}");
        await task;
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(string pipeName)
    {
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(TimeoutMs);
        return client;
    }

    private static async Task SendLineAsync(NamedPipeClientStream client, string line)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
        await Await(client.WriteAsync(bytes).AsTask(), "pipe write");
        await client.FlushAsync();
    }

    private static string EnvelopeJson(string id, string mode, bool expectsReply, object hookEvent)
    {
        var envelope = new PipeEnvelope
        {
            Id = id,
            Mode = mode,
            ExpectsReply = expectsReply,
            Event = JsonSerializer.SerializeToElement(hookEvent),
        };
        return JsonSerializer.Serialize(envelope, Proto.Json);
    }

    /// <summary>Reads a line, treating a broken/closed pipe as EOF (null).</summary>
    private static async Task<string?> ReadLineOrNullAsync(StreamReader reader)
    {
        try { return await reader.ReadLineAsync(); }
        catch (IOException) { return null; }
        catch (ObjectDisposedException) { return null; }
    }

    /// <summary>One RequestReceived subscription that routes requests to per-id awaiters.</summary>
    private static Func<string, Task<HookRequest>> RouteRequests(PipeServer server)
    {
        var byId = new ConcurrentDictionary<string, TaskCompletionSource<HookRequest>>();
        TaskCompletionSource<HookRequest> For(string id) =>
            byId.GetOrAdd(id, _ => new TaskCompletionSource<HookRequest>(TaskCreationOptions.RunContinuationsAsynchronously));
        server.RequestReceived += r => For(r.Id).TrySetResult(r);
        return id => For(id).Task;
    }

    // ---------- tests ----------

    [Fact]
    public async Task PermissionRequest_RoundTrips_AndRespondIsIdempotent()
    {
        string pipeName = UniquePipeName();
        using var server = new PipeServer(pipeName);
        using var cts = new CancellationTokenSource();
        Func<string, Task<HookRequest>> requestById = RouteRequests(server);
        server.Start(cts.Token);

        using var client = await ConnectAsync(pipeName);
        await SendLineAsync(client, EnvelopeJson("id-1", Proto.ModePermissionRequest, expectsReply: true, new
        {
            session_id = "sess-42",
            transcript_path = @"C:\work\demo\t.jsonl",
            cwd = @"C:\work\demo",
            permission_mode = "default",
            hook_event_name = "PermissionRequest",
            tool_name = "Bash",
            tool_input = new { command = "git status" },
        }));

        HookRequest request = await Await(requestById("id-1"), "RequestReceived");
        Assert.Equal("id-1", request.Id);
        Assert.Equal(Proto.ModePermissionRequest, request.Mode);
        Assert.True(request.ExpectsReply);
        Assert.False(request.IsResolved);
        Assert.Equal("PermissionRequest", request.HookEventName);
        Assert.Equal("Bash", request.ToolName);
        Assert.Equal("sess-42", request.SessionId);
        Assert.Equal(@"C:\work\demo", request.Cwd);
        Assert.Equal("default", request.PermissionMode);
        Assert.NotNull(request.ToolInput);
        Assert.Equal("git status", request.ToolInput!.Value.GetProperty("command").GetString());
        Assert.Equal(JsonValueKind.Object, request.Event.ValueKind);

        await Await(request.RespondAsync(new DecisionBody { Behavior = "allow" }), "RespondAsync");
        Assert.True(request.IsResolved);

        var reader = new StreamReader(client, Encoding.UTF8, false, 4096, leaveOpen: true);
        string? decisionLine = await Await(reader.ReadLineAsync(), "decision line");
        Assert.NotNull(decisionLine);
        PipeDecision? decision = JsonSerializer.Deserialize<PipeDecision>(decisionLine!, Proto.Json);
        Assert.NotNull(decision);
        Assert.Equal("id-1", decision!.Id);
        Assert.Equal("decision", decision.Kind);
        Assert.Equal("allow", decision.Decision.Behavior);

        // Second respond must be a silent no-op: no second line, connection just closes.
        await Await(request.RespondAsync(new DecisionBody { Behavior = "deny" }), "second RespondAsync");
        Assert.Null(await Await(ReadLineOrNullAsync(reader), "connection close after reply"));
    }

    [Fact]
    public async Task ObserveEnvelope_FiresEvent_NoReply_AndServerKeepsAccepting()
    {
        string pipeName = UniquePipeName();
        using var server = new PipeServer(pipeName);
        using var cts = new CancellationTokenSource();
        Func<string, Task<HookRequest>> requestById = RouteRequests(server);
        server.Start(cts.Token);

        using (var client = await ConnectAsync(pipeName))
        {
            await SendLineAsync(client, EnvelopeJson("id-obs", Proto.ModeNotify, expectsReply: false, new
            {
                session_id = "sess-1",
                hook_event_name = "Notification",
                message = "Claude needs your permission to use Bash",
            }));

            HookRequest request = await Await(requestById("id-obs"), "observe RequestReceived");
            Assert.False(request.ExpectsReply);
            Assert.Equal(Proto.ModeNotify, request.Mode);
            Assert.Equal("Notification", request.HookEventName);
            Assert.Equal("sess-1", request.SessionId);
            Assert.Equal("", request.ToolName);
            Assert.Null(request.ToolInput);

            // Responding to an observe-only request is a documented no-op — must not throw.
            await Await(request.RespondAsync(new DecisionBody { Behavior = "allow" }), "no-op respond");

            // Server closes the connection right after an observe envelope.
            var reader = new StreamReader(client, Encoding.UTF8, false, 4096, leaveOpen: true);
            Assert.Null(await Await(ReadLineOrNullAsync(reader), "server-side close"));
        }

        // The accept loop is still alive: a fresh blocking request round-trips.
        using var client2 = await ConnectAsync(pipeName);
        await SendLineAsync(client2, EnvelopeJson("id-2", Proto.ModePermissionRequest, expectsReply: true, new
        {
            hook_event_name = "PermissionRequest",
            tool_name = "Edit",
        }));
        HookRequest second = await Await(requestById("id-2"), "request after observe");
        await Await(second.RespondAsync(new DecisionBody { Behavior = "pass" }), "respond after observe");
    }

    [Fact]
    public async Task MalformedLine_ClosesThatConnection_ServerStillAccepts()
    {
        string pipeName = UniquePipeName();
        using var server = new PipeServer(pipeName);
        using var cts = new CancellationTokenSource();
        Func<string, Task<HookRequest>> requestById = RouteRequests(server);
        server.Start(cts.Token);

        using (var bad = await ConnectAsync(pipeName))
        {
            await SendLineAsync(bad, "this is { not valid json");
            var reader = new StreamReader(bad, Encoding.UTF8, false, 4096, leaveOpen: true);
            Assert.Null(await Await(ReadLineOrNullAsync(reader), "close after malformed line"));
        }

        using var good = await ConnectAsync(pipeName);
        await SendLineAsync(good, EnvelopeJson("id-good", Proto.ModePermissionRequest, expectsReply: true, new
        {
            hook_event_name = "PermissionRequest",
            tool_name = "Write",
            tool_input = new { file_path = @"C:\tmp\a.txt", content = "hi" },
        }));

        HookRequest request = await Await(requestById("id-good"), "request after malformed line");
        Assert.Equal("id-good", request.Id);
        Assert.Equal("Write", request.ToolName);

        await Await(request.RespondAsync(new DecisionBody { Behavior = "deny", Message = "no" }), "respond");
        var goodReader = new StreamReader(good, Encoding.UTF8, false, 4096, leaveOpen: true);
        string? line = await Await(goodReader.ReadLineAsync(), "decision after malformed line");
        Assert.NotNull(line);
        Assert.Equal("id-good", JsonSerializer.Deserialize<PipeDecision>(line!, Proto.Json)!.Id);
    }

    [Fact]
    public async Task ClientDisconnect_BeforeReply_RaisesAbandoned_AndRespondBecomesNoOp()
    {
        string pipeName = UniquePipeName();
        using var server = new PipeServer(pipeName);
        using var cts = new CancellationTokenSource();
        Func<string, Task<HookRequest>> requestById = RouteRequests(server);
        server.Start(cts.Token);

        var client = await ConnectAsync(pipeName);
        await SendLineAsync(client, EnvelopeJson("id-gone", Proto.ModePermissionRequest, expectsReply: true, new
        {
            hook_event_name = "PermissionRequest",
            tool_name = "Bash",
            tool_input = new { command = "echo hi" },
        }));

        HookRequest request = await Await(requestById("id-gone"), "RequestReceived");
        var abandoned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        request.Abandoned += () => abandoned.TrySetResult();

        client.Dispose(); // helper timed out / died without reading a decision

        await Await(abandoned.Task, "Abandoned event");
        Assert.True(request.IsResolved);

        // After abandonment, responding must be a silent no-op.
        await Await(request.RespondAsync(new DecisionBody { Behavior = "allow" }), "post-abandon respond");
    }
}
