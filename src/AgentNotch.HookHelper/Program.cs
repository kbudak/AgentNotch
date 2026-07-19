using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentNotch.Protocol;

// AgentNotch hook helper.
//
// Claude Code spawns this per hook event, pipes the event JSON to stdin and
// (for blocking events) waits on the process; whatever we print to stdout
// decides the outcome. Contract with the agent:
//
//   * NEVER hang: every wait has a timeout, every failure path exits 0.
//   * NEVER print anything except a valid decision JSON — for observe-only
//     events plain stdout would be injected into the model's context.
//   * If the AgentNotch app is not running (pipe connect fails), exit 0
//     silently so Claude Code behaves exactly as if we were not installed.

const int ConnectTimeoutObserveMs = 500;
const int ConnectTimeoutBlockingMs = 1500;
// Claude Code kills command hooks at the registered timeout (600 s); stay under it.
const int DecisionTimeoutMs = 590_000;

string mode = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "";
bool expectsReply = mode is Proto.ModePermissionRequest or Proto.ModeAskUserQuestion;

try
{
    string stdin;
    using (var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8))
        stdin = await reader.ReadToEndAsync();

    if (string.IsNullOrWhiteSpace(stdin) || mode.Length == 0)
        return 0;

    JsonElement evt;
    try { evt = JsonDocument.Parse(stdin).RootElement.Clone(); }
    catch { Log($"{mode}: stdin was not JSON"); return 0; }

    var envelope = new PipeEnvelope
    {
        Id = Guid.NewGuid().ToString("N"),
        Mode = mode,
        ExpectsReply = expectsReply,
        Event = evt,
    };

    using var pipe = new NamedPipeClientStream(".", Proto.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    try
    {
        await pipe.ConnectAsync(expectsReply ? ConnectTimeoutBlockingMs : ConnectTimeoutObserveMs);
    }
    catch
    {
        // App not running — behave as if uninstalled.
        return 0;
    }

    var line = JsonSerializer.Serialize(envelope, Proto.Json) + "\n";
    var bytes = Encoding.UTF8.GetBytes(line);
    await pipe.WriteAsync(bytes);
    await pipe.FlushAsync();

    if (!expectsReply)
        return 0;

    using var cts = new CancellationTokenSource(DecisionTimeoutMs);
    var decision = await ReadDecisionAsync(pipe, envelope.Id, cts.Token);
    if (decision is null)
    {
        Log($"{mode}: no decision (timeout or app closed) — passing through");
        return 0;
    }

    var output = BuildHookOutput(mode, decision.Decision);
    if (output is not null)
        Console.Out.Write(output.ToJsonString());
    return 0;
}
catch (Exception ex)
{
    Log($"{mode}: unexpected error: {ex.Message}");
    return 0; // Fail open: never break or hang the agent.
}

static async Task<PipeDecision?> ReadDecisionAsync(NamedPipeClientStream pipe, string id, CancellationToken ct)
{
    try
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
                return null; // App closed the connection.
            if (line.Length == 0)
                continue;

            PipeDecision? msg;
            try { msg = JsonSerializer.Deserialize<PipeDecision>(line, Proto.Json); }
            catch { continue; }

            if (msg is { Kind: "decision" } && msg.Id == id)
                return msg;
        }
    }
    catch (OperationCanceledException) { }
    catch (Exception ex) { Log($"read: {ex.Message}"); }
    return null;
}

// Maps an app decision onto the exact JSON Claude Code expects on stdout.
// Returns null for "pass" → we print nothing and the normal flow applies.
static JsonObject? BuildHookOutput(string mode, DecisionBody d)
{
    switch (mode)
    {
        case Proto.ModePermissionRequest when d.Behavior == "allow":
        {
            var decision = new JsonObject { ["behavior"] = "allow" };
            if (d.UpdatedInput is { } updated)
                decision["updatedInput"] = JsonNode.Parse(updated.GetRawText());
            return new JsonObject
            {
                ["hookSpecificOutput"] = new JsonObject
                {
                    ["hookEventName"] = "PermissionRequest",
                    ["decision"] = decision,
                }
            };
        }
        case Proto.ModePermissionRequest when d.Behavior == "deny":
            return new JsonObject
            {
                ["hookSpecificOutput"] = new JsonObject
                {
                    ["hookEventName"] = "PermissionRequest",
                    ["decision"] = new JsonObject
                    {
                        ["behavior"] = "deny",
                        ["message"] = d.Message ?? "Denied from the AgentNotch overlay.",
                    }
                }
            };

        case Proto.ModeAskUserQuestion when d.Behavior == "answer" && d.Answers is { Count: > 0 }:
        {
            // There is no hook mechanism to answer AskUserQuestion directly; blocking
            // the tool with the user's picks in the deny reason is the supported way
            // to feed the answer back to the model without re-prompting in the terminal.
            var sb = new StringBuilder(
                "The user already answered this question via the AgentNotch overlay. " +
                "Do not ask again; proceed with their answer(s): ");
            for (int i = 0; i < d.Answers.Count; i++)
            {
                if (i > 0) sb.Append("; ");
                sb.Append('[').Append(d.Answers[i].Question).Append("] -> ")
                  .Append(string.Join(", ", d.Answers[i].Labels));
            }
            return new JsonObject
            {
                ["hookSpecificOutput"] = new JsonObject
                {
                    ["hookEventName"] = "PreToolUse",
                    ["permissionDecision"] = "deny",
                    ["permissionDecisionReason"] = sb.ToString(),
                }
            };
        }

        // "pass" and anything unrecognized: print nothing → normal flow.
        default:
            return null;
    }
}

static void Log(string message)
{
    try
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentNotch", "logs");
        Directory.CreateDirectory(dir);
        File.AppendAllText(
            Path.Combine(dir, "helper.log"),
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
    }
    catch { /* logging must never fail the hook */ }
}
