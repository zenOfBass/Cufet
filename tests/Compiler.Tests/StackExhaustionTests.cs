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

    [Fact]
    public void TailRecursion_RunsInConstantSpace()
    {
        // ★★ THE PROPERTY THE WHOLE DESIGN EXISTS TO PROTECT, and the reason the guard catches the
        // overflow rather than predicting it. Any per-call check — a depth counter, or a test of how
        // much room is left — has to take the address of a local, and gcc will not reuse the frame of
        // a function whose local's address was taken. MEASURED three ways on one function at -O2: no
        // check flattened into a loop and ran forever on no stack at all; a depth counter segfaulted;
        // a headroom check grew the stack until it tripped. Either check takes a program that runs in
        // constant space and makes it die.
        //
        // ⚠⚠ THIS USED TO BE LINUX-ONLY, AND WAS ASSERTING SOMEONE ELSE'S WORK. It asked whether gcc
        // flattened the self-call, which gcc did on a recent version at -O2 and did not on an older
        // one — so it passed locally and went red on CI with nothing changed. MEASURED 2026-09-06:
        // `__attribute__((musttail))` on the number-returning form is REFUSED — "cannot tail-call:
        // return value used after call" — because CufetDec comes back through a hidden pointer and
        // the callee's result is copied into the caller's slot. The generated C never asked for a
        // tail call at all; a new enough optimiser was quietly rescuing it.
        //
        // The compiler now emits the loop itself, so this holds at every -O level and on mingw too —
        // which is why the gate is gone. What it pins is OURS: reintroduce a per-call guard, or lose
        // the rewrite, and this goes red on any machine that runs the suite.
        var (exitCode, _, _) = RunToDeath(CompileToBinary(TailRecursive), TimeSpan.FromSeconds(3));

        Assert.True(exitCode == StillRunning,
            $"the tail-recursive program ended with exit code {exitCode}; it should still be looping, "
            + "which means something now consumes a stack frame per call — check for a per-call guard, "
            + "or for a return that no longer qualifies for the self-call rewrite.");
    }

    [Fact]
    public void TailRecursion_DeeperThanTheInterpreterAllows_StillComputes()
    {
        // ★ What the rewrite BUYS, stated as a number. 50,000 deep is past the interpreter's fixed
        // call-depth limit and — before the rewrite — past what a compiled program's real stack had
        // on Windows. It is now a loop, so it costs one frame.
        //
        // ⚠ NOT an oracle test, and cannot be: the interpreter refuses this at depth 1000 while the
        // compiled program answers it. That divergence is deliberate and documented in DESIGN.md.
        const string src = """
            Bind number to summing, given (the number count, the number carried):
                If count is 0, return carried.
                Return cast summing on (count - 1, carried + count).
            Done.
            State cast summing on (50000, 0).
            """;
        Assert.Equal("1250025000", CompileRaw(src).Trim());
    }

    [Fact]
    public void TheSelfCallRewrite_ComputesEveryArgumentBeforeAssigningAny()
    {
        // ⚠ The one way a parameter-reassignment rewrite goes silently wrong: `cast f on (back, front)`
        // reads BOTH parameters, so assigning `front` first would feed the new `front` to the argument
        // that wanted the old one. Through the oracle, because the interpreter's ordinary recursion is
        // the definition of the right answer here.
        const string src = """
            Bind number to swapper, given (the number front, the number back):
                If front is 0, return back.
                Return cast swapper on (back - 1, front).
            Done.
            State cast swapper on (3, 5).
            State cast swapper on (7, 2).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void TheSelfCallRewrite_StandsAsideWhenSomethingMustRunFirst()
    {
        // ⚠⚠ A self-call inside a Try that catches an EXCEPTION must not become a jump. That Try
        // pushes a setjmp buffer onto a runtime stack and pops it on the way out; jumping to the top
        // of the function would skip the pop and push another next time round, walking
        // `cufet_exc_bufs` off its end. The oracle would not reliably catch that — four iterations
        // overflow nothing — so the condition is pinned here directly.
        const string guarded = """
            Bind number to risky, given (the number rounds, the number carried):
                If rounds is 0, return carried.
                Try to:
                    Return cast risky on (rounds - 1, carried + 1).
                Done.
                In case of exception:
                    Return carried.
                Done.
                Return carried.
            Done.
            State cast risky on (4, 0).
            """;

        // ★ The positive control is what makes the negative mean anything. Rename the label and the
        // DoesNotContain below would pass while testing nothing at all.
        Assert.Contains("goto cf_tj", GenerateC(TailRecursive));
        Assert.DoesNotContain("goto cf_tj", GenerateC(guarded));

        Assert.Equal(InterpretRaw(guarded), CompileRaw(guarded));
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
