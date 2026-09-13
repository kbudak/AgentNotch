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
// How long the notch gets to answer before we hand the question back to the agent.
//
// This used to sit at 590 s — just under the registered 600 s hook timeout — on the
// theory that a human might take that long. In practice it meant that whenever the
// notch went unnoticed the agent sat frozen for TEN MINUTES with nothing on screen,
// because Claude Code will not draw its own prompt while a hook is still pending.
// (It happened three times in this machine's helper log.) Handing back after a
// minute costs an unhurried answer nothing — the terminal's own prompt appears and
// still waits for you — and it caps what a missed notch can ever cost the agent.
const int DecisionTimeoutMs = 60_000;

string mode = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "";
bool expectsReply = mode is Proto.ModePermissionRequest or Proto.ModeAskUserQuestion or Proto.ModeCursorPermission;

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

    // The notch now holds this question. Put it on the terminal too — the agent
    // will not draw its own prompt while we are pending, so without this the
    // terminal just goes quiet and you have no idea anything is waiting.
    var (title, detail) = HookSummary.Describe(mode, evt);
    var echo = new List<string> { "  [AgentNotch] " + title };
    foreach (var detailLine in detail) echo.Add("    " + detailLine);
    echo.Add($"    waiting for your answer in the notch ({DecisionTimeoutMs / 1000}s, then this prompt takes over)");
    AgentNotch.HookHelper.TerminalEcho.Write(echo.ToArray());

    using var cts = new CancellationTokenSource(DecisionTimeoutMs);
    var decision = await ReadDecisionAsync(pipe, envelope.Id, cts.Token);
    if (decision is null)
    {
        // Print nothing: Claude Code falls back to its own terminal prompt, which is
        // exactly where an unanswered question should end up.
        AgentNotch.HookHelper.TerminalEcho.Write("  [AgentNotch] no answer in the notch - over to you here.");
        Log($"{mode}: no decision in {DecisionTimeoutMs / 1000}s — handing back to the terminal");
        return 0;
    }

    // Close the loop on the terminal as well, so its transcript still records what
    // was decided rather than showing a question that silently stops mattering.
    AgentNotch.HookHelper.TerminalEcho.Write("  [AgentNotch] " + Outcome(decision.Decision));

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
            return new JsonObject
            {
                ["hookSpecificOutput"] = new JsonObject
                {
                    ["hookEventName"] = "PreToolUse",
                    ["permissionDecision"] = "deny",
                    ["permissionDecisionReason"] = AnswerSentence(d.Answers),
                }
            };
        }

        // Cursor has no mechanism to answer a question tool directly either, so the
        // same trick the Claude AskUserQuestion path uses applies: block the call and
        // put the user's picks in the message the model reads, which answers it
        // without a second round trip through the chat.
        case Proto.ModeCursorPermission when d.Behavior == "answer" && d.Answers is { Count: > 0 }:
            return new JsonObject
            {
                ["permission"] = "deny",
                ["userMessage"] = "Answered from the AgentNotch overlay.",
                ["agentMessage"] = AnswerSentence(d.Answers),
            };

        // Cursor's dialect: a flat {"permission": ...} object. userMessage is what
        // the human sees in the chat, agentMessage is what the model is told —
        // without the latter the agent just retries the command it was denied.
        case Proto.ModeCursorPermission when d.Behavior is "allow" or "deny":
        {
            var output = new JsonObject { ["permission"] = d.Behavior };
            if (d.Behavior == "deny")
            {
                output["userMessage"] = "Denied from the AgentNotch overlay.";
                output["agentMessage"] = d.Message ?? "The user denied this command in AgentNotch.";
            }
            return output;
        }

        // "pass" and anything unrecognized: print nothing → normal flow. For Cursor
        // that means its own permission dialog still runs, exactly as if AgentNotch
        // were not installed — never a silent auto-allow.
        default:
            return null;
    }
}

/// <summary>One line for the terminal saying how the notch answered.</summary>
static string Outcome(DecisionBody d) => d.Behavior switch
{
    "allow" => "allowed from the notch.",
    "deny" => "denied from the notch.",
    "answer" when d.Answers is { Count: > 0 } =>
        "answered from the notch: " + string.Join(", ", d.Answers[0].Labels),
    _ => "handed back — answer here.",
};

// What the model is told when the user picked an option in the notch. Shared by
// both dialects so an answer never reads differently depending on the agent.
static string AnswerSentence(List<QuestionAnswer> answers)
{
    var sb = new StringBuilder(
        "The user already answered this question via the AgentNotch overlay. " +
        "Do not ask again; proceed with their answer(s): ");
    for (int i = 0; i < answers.Count; i++)
    {
        if (i > 0) sb.Append("; ");
        sb.Append('[').Append(answers[i].Question).Append("] -> ")
          .Append(string.Join(", ", answers[i].Labels));
    }
    return sb.ToString();
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
