using System.Diagnostics;
using Xunit;

namespace Cufet.Compiler.Tests;

/// <summary>
/// What a compiled program does when it recurses past the end of its stack.
/// </summary>
/// <remarks>
/// <para>
/// ⚠⚠ It used to VANISH. MEASURED 2026-09-05: on Windows, exit code 0xC00000FD and not one
/// character on either stream; on Linux, a segfault whose only word — "Segmentation fault" —
/// comes from the SHELL and disappears the moment the program is run from anything else. The
/// interpreted form has always said what happened.
/// </para>
/// <para>
/// ★ These do not go through the oracle, and cannot. The two backends disagree here BY DESIGN:
/// the interpreter refuses at a fixed call depth and raises an ordinary catchable exception, while
/// a compiled program runs until the machine's real stack is gone and then ends. Both the depth
/// and the catchability are deliberate — see DESIGN.md.
/// </para>
/// </remarks>
public class StackExhaustionTests : PipelineTestBase
{
    // gcc flattens a self-call in tail position into a loop, so a runaway that is meant to exhaust
    // the stack must have something left to do after the call comes back.
    private const string RunsOutOfStack = """
        Bind number to deepen, given (the number depth):
            Return (cast deepen on (depth + 1)) + 1.
        Done.

        State cast deepen on (1).
        """;

    // The same program with the call in tail position — nothing to do afterwards.
    private const string TailRecursive = """
        Bind number to deepen, given (the number depth):
            Return cast deepen on (depth + 1).
        Done.

        State cast deepen on (1).
        """;

    [Fact]
    public void AProgramThatRunsOutOfStack_SaysSoInsteadOfVanishing()
    {
        var (exitCode, _, errors) = RunToDeath(CompileToBinary(RunsOutOfStack), TimeSpan.FromSeconds(60));

        Assert.Contains("ran out of stack", errors);

        // ★ Exit 1, the same as any other Cufet program that ends badly — not 0xC00000FD and not a
        // POSIX signal. A caller that only ever looks at the exit code should not be able to tell
        // this apart from a program that refused for any other reason.
        Assert.Equal(1, exitCode);
    }

    [LinuxFact]
    public void TailRecursion_IsStillFlattenedIntoALoop()
    {
        // ★★ THE PROPERTY THE WHOLE DESIGN EXISTS TO PROTECT, and the reason the guard catches the
        // overflow rather than predicting it. Any per-call check — a depth counter, or a test of how
        // much room is left — has to take the address of a local, and gcc will not reuse the frame of
        // a function whose local's address was taken. MEASURED three ways on one function at -O2: no
        // check flattened into a loop and ran forever on no stack at all; a depth counter segfaulted;
        // a headroom check grew the stack until it tripped. Either check takes a program that runs in
        // constant space and makes it die.
        //
        // ⚠ Linux only, and that is not squeamishness — mingw does NOT flatten this, measured: the
        // same program overflows on Windows. Asserting it there would pin an accident that is not
        // true on the platform it names.
        var (exitCode, _, _) = RunToDeath(CompileToBinary(TailRecursive), TimeSpan.FromSeconds(3));

        Assert.True(exitCode == StillRunning,
            $"the tail-recursive program ended with exit code {exitCode}; it should still be looping, "
            + "which means something now consumes a stack frame per call — check for a per-call guard.");
    }

    /// <summary>Reported when a program was still running when its time ran out.</summary>
    private const int StillRunning = int.MinValue;

    /// <summary>
    /// Runs a binary that is expected to end badly, keeping both streams and the exit code.
    /// </summary>
    /// <remarks>
    /// ⚠ Not <see cref="PipelineTestBase.RunBinary"/>, which discards stderr and treats a crash as
    /// a test failure. Both of those are exactly what is under examination here.
    /// </remarks>
    private static (int ExitCode, string Output, string Errors) RunToDeath(string binPath, TimeSpan patience)
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName               = binPath,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        })!;

        var output = proc.StandardOutput.ReadToEndAsync();
        var errors = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit((int)patience.TotalMilliseconds))
        {
            proc.Kill(entireProcessTree: true);
            return (StillRunning, "", "");
        }

        return (proc.ExitCode, output.GetAwaiter().GetResult(), errors.GetAwaiter().GetResult());
    }
}
