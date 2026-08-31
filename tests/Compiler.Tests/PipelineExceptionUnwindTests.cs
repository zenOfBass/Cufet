using Xunit;

namespace Cufet.Compiler.Tests;

/// <summary>
/// The exception landing pad, run enough times to catch a defect that is not deterministic.
/// </summary>
/// <remarks>
/// ★★ This class exists because of a bug that hid for weeks behind the shape of every other test
/// here. `Range_NonPositiveComputedStep` compiled a program that raises and catches once, ran the
/// binary once, and passed — 96% of the time. The other 4% was a real ACCESS VIOLATION, and it read
/// as "a flake under parallelism" for so long that a retry was nearly added to paper over it.
///
/// The cause: on x86-64 mingw-w64, plain `setjmp(b)` saves a frame pointer, and that makes `longjmp`
/// unwind through ntdll's RtlUnwindEx — which at -O2 reads stack memory it cannot validate and
/// faults depending on what is on the stack. `CUFET_PLAIN_SETJMP` passes a NULL context so longjmp
/// restores registers and skips the unwind, which is what the runtime always assumed: generated C
/// has no `__finally` and no destructors, and cufet_raise does its own cleanup before it jumps.
///
/// ★ **The lesson is about the test, not the bug.** One compile and one run cannot see a defect
/// that happens in a fraction of runs, however many tests are written in that shape. Compiling once
/// and running the SAME binary many times is what makes such a defect fail, and it is cheap.
/// </remarks>
public class PipelineExceptionUnwindTests : PipelineTestBase
{
    // At the measured 4% failure rate, 100 runs miss a full regression with probability
    // 0.96^100 ≈ 1.7%, against 5 in a million at the 300 this used to be.
    //
    // ⚠ **Cut from 300 on 2026-08-31 because the cost estimate that justified it was wrong by
    // thirty times.** The note here said "a process launch of a program that prints two lines, so
    // the whole test is seconds"; measured, the two tests were 96s and 85s — 600 process launches,
    // 6% of the entire compiler suite's work in three tests, and the two slowest tests in it by a
    // factor of three. A launch on Windows is not free the way the estimate assumed.
    //
    // ★ The trade is stated rather than hidden: 1.7% is a regression walking past this once in
    // sixty, and it is bought with two minutes. Raise it if that ever feels like the wrong side —
    // the arithmetic is right here, and the number is the only thing that changes.
    private const int Runs = 100;

    [Fact]
    public void RaiseAndCatch_SurvivesRepeatedRuns()
    {
        // The exact program that used to flake: a computed non-positive step raises at run time,
        // a handler catches it, suppresses it, and the program carries on to the line after.
        const string src = """
            Define z as 0 - 2.
            Try to:
                Define bad as range 1 to 5 counting by z.
                State bad.
            Done.
            In case of exception (the exception):
                State "caught: " joined to the message of the exception.
                Suppress the exception.
            Done.
            State "after".
            """;

        var expected = Norm(InterpretRaw(src));
        var binPath = CompileToBinary(src);
        try
        {
            for (int run = 1; run <= Runs; run++)
            {
                // RunBinary already refuses a crash — a negative exit code or the 0xC0000000 range
                // throws with the exit code in the message rather than surfacing as a string diff.
                var actual = Norm(RunBinary(binPath));
                Assert.True(expected == actual,
                    $"run {run} of {Runs} did not match the interpreter.\n" +
                    $"--- expected ---\n{expected}\n--- actual ---\n{actual}");
            }
        }
        finally { try { File.Delete(binPath); } catch { } }
    }

    [Fact]
    public void RaiseWithNoHandler_SurvivesRepeatedRuns()
    {
        // The other side of the pad: nothing is installed, so cufet_raise prints and exits rather
        // than jumping. It shares the raise path with the case above and is cheap to hold to the
        // same bar — a crash here would look identical from the outside and have a different cause.
        const string src = """
            Define z as 0 - 2.
            Define bad as range 1 to 5 counting by z.
            State bad.
            """;

        var binPath = CompileToBinary(src);
        try
        {
            for (int run = 1; run <= Runs; run++)
            {
                // Exits 1 with the message on stderr, which RunBinary tolerates (only a CRASH — a
                // negative code or 0xC0000000+ — is refused). Reaching here at all is the assertion.
                RunBinary(binPath);
            }
        }
        finally { try { File.Delete(binPath); } catch { } }
    }

    /// <summary>
    /// The handler's binding — bare, renamed, and written out — plus what printing one does.
    /// </summary>
    /// <remarks>
    /// ⚠⚠ Printing a failure or an exception was a LIVE DIVERGENCE, and the checker waved it
    /// through: `cufet check` said "No problems found", the interpreter printed
    /// `Cufet.Interpreter.Interpreter+FailureValue` — a C# class name — and the compiler refused to
    /// build at all. Both arms, the same way.
    ///
    /// ⚠ The RENAME needed the compiler too, which is not obvious: it resolves `the exception` by
    /// TYPE, from its own `_varTypes` table keyed by the name the body references. Keyed on the
    /// literal `"the exception"`, a chosen name arrived untyped and `the message of the trouble`
    /// was refused as "reading 'message' from a number".
    /// </remarks>
    [Fact]
    public void TheExceptionBindingAndPrinting_AgreeOnBothBackends()
    {
        const string src = """
            Try to:
                State 1 / 0.
            Done.
            In case of exception:
                State the message of the exception.
                State the exception.
                Suppress the exception.
            Done.

            Try to:
                State 2 / 0.
            Done.
            In case of exception (the trouble):
                State the message of the trouble.
                Suppress the exception.
            Done.

            Bind number or failure to risky:
                Return failure "nope".
            Done.

            Try to:
                Define x as cast risky on ().
                State x.
            Done.
            In case of failure:
                State the message of the failure.
                State the failure.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }
}
