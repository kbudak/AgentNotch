using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentNotch.HookHelper;

/// <summary>
/// Writes a few lines straight onto the terminal the agent is running in, so a
/// question the notch is holding is still visible where you are actually looking.
///
/// It cannot go through stdout: Claude Code captures the hook's stdout and reads it
/// as the decision, so anything printed there would either be parsed as a verdict or
/// injected into the model's context. stderr is surfaced as a hook ERROR, which is
/// worse. The console is the third channel — this process is a child of the agent
/// and inherits its console, so opening CONOUT$ writes to the real terminal, past
/// both pipes. Verified: with stdout and stderr both redirected, only the decision
/// JSON came back on stdout while these lines appeared in the console buffer.
///
/// Entirely best-effort. A session with no console at all (Claude Desktop, a piped
/// or headless run) has no CONOUT$ to open, and every failure is swallowed: an echo
/// is a courtesy, and must never be able to break or delay a hook.
/// </summary>
internal static class TerminalEcho
{
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    /// <summary>Set once we know there is no console, so later calls cost nothing.</summary>
    private static bool _unavailable;

    public static void Write(params string[] lines)
    {
        if (_unavailable || lines is not { Length: > 0 }) return;
        try
        {
            using var handle = CreateFileW(
                "CONOUT$", GenericWrite, FileShareRead | FileShareWrite,
                IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

            if (handle.IsInvalid)
            {
                _unavailable = true;
                return;
            }

            // No ANSI, no cursor moves, no colour: the agent owns this terminal and
            // redraws it constantly. Plain lines append and scroll like any other
            // output; anything that repositioned the cursor would fight its TUI.
            var text = new StringBuilder();
            text.Append(Environment.NewLine);
            foreach (var line in lines)
                text.Append(Ascii(line)).Append(Environment.NewLine);

            // The console's output codepage is whatever the machine defaults to (857
            // on this one, not UTF-8), so UTF-8 bytes render as mojibake — the first
            // cut printed "Ôùå" where it meant a diamond. Writing ASCII makes the
            // encoding question disappear rather than answering it wrongly on someone
            // else's locale.
            using var stream = new FileStream(handle, FileAccess.Write, bufferSize: 1, isAsync: false);
            var bytes = Encoding.ASCII.GetBytes(text.ToString());
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }
        catch
        {
            // No console, no permission, a closed terminal — all just mean "no echo".
        }
    }

    /// <summary>
    /// Folds a line to plain ASCII. Agent questions carry real prose, accents and
    /// all, and a byte the console cannot map looks worse than a plain substitute —
    /// so the common ones are transliterated and anything else becomes '?'.
    /// </summary>
    internal static string Ascii(string line)
    {
        var sb = new StringBuilder(line.Length);
        foreach (var c in line)
        {
            if (c is '\r' or '\n' or '\t') { sb.Append(' '); continue; }
            sb.Append(c is >= ' ' and <= '~' ? c : Fold(c));
        }
        return sb.ToString();
    }

    private static char Fold(char c) => c switch
    {
        '—' or '–' => '-',        // em / en dash
        '…' => '.',                    // ellipsis
        '‘' or '’' => '\'',       // curly quotes
        '“' or '”' => '"',
        'ı' => 'i', 'İ' => 'I',   // Turkish dotless / dotted i
        'ş' => 's', 'Ş' => 'S',   // s-cedilla
        'ğ' => 'g', 'Ğ' => 'G',   // g-breve
        'ç' => 'c', 'Ç' => 'C',
        'ö' => 'o', 'Ö' => 'O',
        'ü' => 'u', 'Ü' => 'U',
        _ => '?',
    };

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);
}
