using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AgentNotch.App.Diagnostics;
using AgentNotch.Protocol;

namespace AgentNotch.App.Ipc;

/// <summary>
/// Named-pipe NDJSON server for hook-helper processes. Accepts many
/// concurrent connections; each connection normally carries exactly one
/// <see cref="PipeEnvelope"/> line and — for blocking modes — stays open
/// until the app writes the correlated <see cref="PipeDecision"/> line.
/// Raises <see cref="RequestReceived"/> on background threads; the
/// composition root marshals to the UI thread. Never crashes on malformed
/// input; all errors are logged via <see cref="AppLog"/>.
/// </summary>
public sealed class PipeServer : IDisposable
{
    /// <summary>How long after a reply we wait for the client to hang up before closing ourselves.</summary>
    private const int PostReplyLingerMs = 2000;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _pipeName;
    private readonly CancellationTokenSource _disposeCts = new();
    // Kept for the server's lifetime on purpose: connection tasks may touch the
    // token during teardown, so we never dispose these sources (bounded, one per Start).
    private CancellationTokenSource? _linkedCts;
    private int _started;
    private int _disposed;

    public PipeServer(string pipeName = Proto.PipeName) => _pipeName = pipeName;

    /// <summary>Raised for every well-formed envelope. May fire on any thread.</summary>
    public event Action<HookRequest>? RequestReceived;

    /// <summary>Starts the accept loop on the thread pool and returns immediately.</summary>
    public void Start(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
        CancellationToken token = _linkedCts.Token;
        _ = Task.Run(async () =>
        {
            try { await AcceptLoopAsync(token).ConfigureAwait(false); }
            catch (Exception ex) { AppLog.Write("ipc: accept loop died: " + ex); }
        }, CancellationToken.None);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _disposeCts.Cancel(); }
        catch (Exception ex) { AppLog.Write("ipc: dispose: " + ex.Message); }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        AppLog.Write($@"ipc: listening on \\.\pipe\{_pipeName}");
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                // Non-zero buffer sizes matter: with the default (0) outbound quota a
                // byte-mode pipe write blocks until the client actually reads, so a
                // decision written before the helper reached its read would deadlock.
                server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 65536,
                    outBufferSize: 65536);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                NamedPipeServerStream connection = server;
                server = null; // ownership handed to the connection task
                _ = Task.Run(() => HandleConnectionAsync(connection, ct), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                server?.Dispose();
                if (ct.IsCancellationRequested) break;
                AppLog.Write("ipc: accept error: " + ex.Message);
                try { await Task.Delay(250, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        AppLog.Write("ipc: server stopped");
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        HookRequest? request = null;
        var writeLock = new SemaphoreSlim(1, 1);
        try
        {
            // leaveOpen: the reader must not close the pipe — replies still go out on it.
            var reader = new StreamReader(pipe, Utf8NoBom, false, 4096, leaveOpen: true);

            string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) return; // connected, then closed without sending anything

            PipeEnvelope? envelope = null;
            try { envelope = JsonSerializer.Deserialize<PipeEnvelope>(line, Proto.Json); }
            catch (JsonException ex) { AppLog.Write("ipc: malformed envelope json: " + ex.Message); }

            if (envelope is null || string.IsNullOrEmpty(envelope.Id))
            {
                AppLog.Write("ipc: dropping malformed/incomplete envelope — closing connection");
                return;
            }

            request = new HookRequest(envelope, decision => WriteDecisionAsync(pipe, writeLock, decision));

            try { RequestReceived?.Invoke(request); }
            catch (Exception ex) { AppLog.Write("ipc: RequestReceived handler threw: " + ex); }

            if (!request.ExpectsReply) return; // observe-only: done, close

            // Blocking mode: wait until the app responds or the client goes away.
            Task drain = DrainUntilEofAsync(reader, ct);
            Task first = await Task.WhenAny(request.Resolution, drain).ConfigureAwait(false);

            if (first == drain && !request.IsResolved)
            {
                AppLog.Write($"ipc: client vanished before reply ({request.Id})");
                return; // finally marks it abandoned
            }

            // Responded: give the client a moment to read the line and hang up.
            await Task.WhenAny(drain, Task.Delay(PostReplyLingerMs, CancellationToken.None)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutdown — fall through to cleanup
        }
        catch (Exception ex)
        {
            AppLog.Write("ipc: connection error: " + ex.Message);
        }
        finally
        {
            // No-op when already responded; otherwise tell the UI to dismiss.
            if (request is { ExpectsReply: true }) request.MarkAbandoned();
            try { pipe.Dispose(); } catch { /* already broken */ }
            writeLock.Dispose();
        }
    }

    private static async Task WriteDecisionAsync(NamedPipeServerStream pipe, SemaphoreSlim writeLock, PipeDecision decision)
    {
        string json = JsonSerializer.Serialize(decision, Proto.Json);
        byte[] payload = Utf8NoBom.GetBytes(json + "\n");
        await writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await pipe.WriteAsync(payload).ConfigureAwait(false);
            await pipe.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    /// <summary>Reads until the client closes its end. Never faults — every failure means "client gone".</summary>
    private static async Task DrainUntilEofAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? extra = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (extra is null) return;
                if (!string.IsNullOrWhiteSpace(extra))
                    AppLog.Write("ipc: unexpected extra line on connection — ignored");
            }
        }
        catch
        {
            // EOF / broken pipe / cancellation / disposal — all mean the connection is over.
        }
    }
}
