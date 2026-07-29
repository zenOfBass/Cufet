namespace Cufet.Interpreter.Tests;

/// <summary>
/// External commands for tests that actually launch a process.
///
/// Cufet's <c>run</c> is fork/exec with no shell — deliberately, since that is what makes the
/// "no <c>system()</c>" guarantee true by construction. The cost is that any test wanting shell
/// behaviour (a redirection, or <c>exit &lt;code&gt;</c>) must name a shell itself, and the shell
/// is not the same on every OS.
///
/// These tests used to hard-code <c>cmd</c> and <c>/C</c>, so the entire subprocess surface only
/// ever ran on Windows and failed outright on Linux. Nobody noticed because there was no CI; the
/// first CI run found all of it at once. Keeping the platform choices here — rather than inline
/// at each call site — means the next test to launch something cannot quietly re-introduce it.
/// </summary>
internal static class PlatformCommands
{
    /// <summary>A shell that accepts a flag followed by one command string.</summary>
    internal static string Shell => OperatingSystem.IsWindows() ? "cmd" : "/bin/sh";

    /// <summary>The flag that makes <see cref="Shell"/> read a command from its next argument.</summary>
    internal static string ShellFlag => OperatingSystem.IsWindows() ? "/C" : "-c";

    /// <summary>A command that exits with the given status.</summary>
    internal static string ExitWith(int code) =>
        OperatingSystem.IsWindows() ? $"exit /b {code}" : $"exit {code}";

    /// <summary>
    /// A command that writes to stderr. cmd needs the redirection glued to the text
    /// (<c>echo x&gt;&amp;2</c>); sh accepts either, and is written spaced because that is how a
    /// reader expects to see it.
    /// </summary>
    internal static string EchoToStderr(string text) =>
        OperatingSystem.IsWindows() ? $"echo {text}>&2" : $"echo {text} >&2";

    /// <summary>A program that filters its input by a pattern given as an argument.</summary>
    internal static string Grep => OperatingSystem.IsWindows() ? "findstr" : "grep";

    /// <summary>
    /// The one case that is not "a shell plus one command string". Used by the test asserting
    /// each argument arrives as a SEPARATE OS argument, so on POSIX it invokes /bin/echo
    /// directly — <c>sh -c echo passed-arg</c> would make "passed-arg" into $0 and print an empty
    /// line, quietly testing nothing at all.
    /// </summary>
    internal static string EchoArgProgram => OperatingSystem.IsWindows() ? "cmd" : "/bin/echo";

    /// <summary>The argument list, already formatted as Cufet source, for <see cref="EchoArgProgram"/>.</summary>
    internal static string EchoArgList(string text) =>
        OperatingSystem.IsWindows() ? $"\"/C\", \"echo\", \"{text}\"" : $"\"{text}\"";
}
