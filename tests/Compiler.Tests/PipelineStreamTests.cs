using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Cufet.Compiler;
using Cufet.Interpreter;
using Xunit;
using CufetInterpreter = Cufet.Interpreter.Interpreter;
using CufetLexer = Cufet.Lexer.Lexer;
namespace Cufet.Compiler.Tests;

/// <summary>One slice of the pipeline oracle suite — see PipelineTestBase for why it is split.</summary>
public class PipelineStreamTests : PipelineTestBase
{

    [Fact]
    public void With_ReadLine_EofIsVoid_MatchesInterpreter()
    {
        // read a line from a stream → voidable text; void at end-of-stream (a present empty line
        // is "", not void).
        AssertFileOracle("""
            With the file "{PATH}" open for writing as out:
                write "only-line" to out.
            Done.
            With the file "{PATH}" open for reading as inp:
                Define a1 as read a line from inp.
                Define a2 as read a line from inp.
                If a1 is void, state "1-void". Otherwise, state a1.
                If a2 is void, state "2-void". Otherwise, state a2.
            Done.
            """);
    }

    [Fact]
    public void With_WriteThenReturn_FlushesOnAllExits_MatchesInterpreter()
    {
        // The data-loss proof: a write inside a With block must be flushed+closed on EVERY exit —
        // normal end, return-out-of-block, propagated-failure, and Try-failure-goto. A skipped
        // fclose would lose the buffered write; reading the file back proves it landed.
        AssertFileOracle("""
            Bind text to write-then-return, given (the text loc):
                With the file loc open for writing as out:
                    write "RETURN-DATA" to out.
                    return "bailed".
                Done.
                return "normal".
            Done.
            State cast write-then-return on ("{PATH}").
            State (read all from the file "{PATH}" but on failure "LOST").

            Bind text or failure to write-then-propagate, given (the text loc):
                With the file loc open for writing as out:
                    write "PROPAGATE-DATA" to out.
                    Define x as read all from the file "no-such-qq.txt" or pass the failure off.
                    write " unreached" to out.
                    return "ok".
                Done.
                return "normal".
            Done.
            Try to:
                State cast write-then-propagate on ("{PATH2}").
            Done.
            In case of failure:
                State "caught".
            Done.
            State (read all from the file "{PATH2}" but on failure "LOST").

            Try to:
                With the file "{PATH3}" open for writing as out:
                    write "TRYGOTO-DATA" to out.
                    Define y as read all from the file "no-such-qq.txt".
                    write " unreached" to out.
                Done.
            Done.
            In case of failure:
                State "try-caught".
            Done.
            State (read all from the file "{PATH3}" but on failure "LOST").
            """);
    }

    [Fact]
    public void With_NestedReturn_ClosesBothLifo_MatchesInterpreter()
    {
        // A return out of a nested With closes both files (LIFO); code after the inner block is
        // unreached, so the outer file holds only what was written before the inner block.
        AssertFileOracle("""
            Bind text to nested, given (the text locone, the text loctwo):
                With the file locone open for writing as outA:
                    write "AAA" to outA.
                    With the file loctwo open for writing as outB:
                        write "BBB" to outB.
                        return "inner".
                    Done.
                    write " unreachedA" to outA.
                Done.
                return "normal".
            Done.
            State cast nested on ("{PATH}", "{PATH2}").
            State (read all from the file "{PATH}" but on failure "LOST-A").
            State (read all from the file "{PATH2}" but on failure "LOST-B").
            """);
    }

    [Fact]
    public void Stdin_ReadLineAndLines_MatchesInterpreter()
    {
        // `the input` is stdin; read a line + read all lines consume it. Both backends fed the
        // same input via the harness.
        const string src = """
            Define first as read a line from the input.
            State "first: " joined to (first but void is "EOF").
            Define rest as read all lines from the input.
            State the number of rest.
            For each ln in rest, repeat:
                State "got: " joined to ln.
            Done.
            """;
        Assert.Equal(InterpretRaw(src, "hello\nworld\nthree\n"), CompileRaw(src, "hello\nworld\nthree\n"));
    }

    // ── Slice 9C: subprocess (run) + pipes ──
    // POSIX-only (fork/exec/pipe/waitpid). LINUX-ONLY tests: on Windows the compiled binary can't
    // build (mingw has no fork), so skip — on CI Linux both interpreter (.NET) and binary run in
    // the same environment, so command resolution matches. Commands stay trivial + deterministic
    // (echo/true/false/cat/printf) so the output is environment-independent.

    [Fact]
    public void Subprocess_Run_MatchesInterpreter()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // Launch-failure vs ran-but-nonzero: `false` is a SUCCESS record with exit-code 1; a
        // nonexistent command is a launch FAILURE (→ but-on-failure / the OS-error bridge).
        const string src = """
            Try to:
                Define r as run "echo" with arguments ("hello world").
                State "output=[" joined to (the output of r) joined to "]".
                State "exit=" joined to (the exit-code of r converted to text).
                State "errlen=" joined to (the length of (the errors of r) converted to text).
                Define t as run "true".
                State "true-exit=" joined to (the exit-code of t converted to text).
                Define f as run "false".
                State "false-exit=" joined to (the exit-code of f converted to text).
            Done.
            In case of failure:
                State "launch-failed".
            Done.
            Define fb as run "no-such-command-zzz" but on failure (a record with (the errors "", the exit-code 0, the output "LAUNCHFAIL")).
            State the output of fb.
            Try to:
                Define x as run "no-such-command-zzz".
                State the output of x.
            Done.
            In case of failure:
                State "cat: " joined to (the category of the failure but void is "none").
                State "msg: " joined to the message of the failure.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Subprocess_Pipe_MatchesInterpreter()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // run X | run Y: stdout → next stdin (buffered-sequential), pipefail exit (rightmost
        // nonzero), aggregated stderr; a stage's launch failure fails the whole pipe.
        const string src = """
            Try to:
                Define r as run "echo" with arguments ("hello") | run "cat".
                State "piped=[" joined to (the output of r) joined to "]".
                Define r2 as run "printf" with arguments ("one\ntwo\nthree\n") | run "cat".
                State "lines=" joined to (the number of (the output of r2 split by "\n") converted to text).
                Define r3 as run "true" | run "false".
                State "pipefail-exit=" joined to (the exit-code of r3 converted to text).
            Done.
            In case of failure:
                State "pipe-failed".
            Done.
            Try to:
                Define r4 as run "no-such-zzz" | run "cat".
                State the output of r4.
            Done.
            In case of failure:
                State "pipe-launch-failed: " joined to the message of the failure.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Subprocess_BarePipeStatement_MatchesInterpreter()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // Bare `run X | run Y.` statement → final stdout goes to stdout (the shell pattern).
        const string src = """
            run "echo" with arguments ("streamed to stdout") | run "cat".
            State "after".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── CONC.A+B: threads + structured join + thread-local arena + channels ──
    // LINUX-ONLY (pthreads; mingw has no fork/threads). The interpreter is NOT a bit-oracle here
    // (cooperative → it deadlocks/masks races), so we assert the DETERMINISTIC INVARIANT the
    // parallel result must satisfy regardless of interleaving — not Compile == Interpret.

    [Fact]
    public void Concurrency_ParallelSum_AggregateInvariant()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // N tasks each send their i into a channel; main collects N and sums. Order-independent:
        // total == 1+2+…+8 == 36, whatever order the threads actually run.
        const string src = """
            Pull a rabbit.
                Define ch as a channel of number.
                Define n as 8.
                For each i in the range 1 to n, repeat:
                    Have rabbit start a task:
                        Send i through ch.
                    Done.
                Done.
                Define total as 0.
                For each k in the range 1 to n, repeat:
                    Define d as the delivery from ch.
                    total becomes total + (d but void is 0).
                Done.
                State total.
                Close ch.
            Done.
            """;
        Assert.Equal("36", Compile(src));
    }

    [Fact]
    public void Concurrency_DeepCopyAtSpawn_ClosesParentMutationRace()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // The parent mutates a captured variable AFTER spawning; deep-copy-at-spawn means the task
        // sees its spawn-time snapshot (5), not the parent's later 999. (The cooperative interpreter
        // masks this race and would yield 999 — the divergence true parallelism exposes.)
        const string src = """
            Pull a rabbit.
                Define ch as a channel of number.
                Define x as 5.
                Have rabbit start a task:
                    Send x through ch.
                Done.
                x becomes 999.
                Define d as the delivery from ch.
                State d but void is -1.
            Done.
            """;
        Assert.Equal("5", Compile(src));
    }

    [Fact]
    public void Concurrency_FanOut_WorkQueue_EachItemProcessedOnce()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // The correctness invariant: 20 items each processed exactly once by SOME worker ⇒ the
        // fanned-in sum is 2·(1+…+20) == 420, whatever the (nondeterministic) work distribution.
        // Proves the shared-channel dequeue under N-worker contention never double-delivers or drops.
        Assert.Equal("420", Compile(FanOutWorkQueue));
    }

    [Fact]
    public void Concurrency_FanOut_WorkQueue_MemorySafety_ASan()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // The sharpest memory test: N workers contending on one channel + a results channel + a
        // collector series, all under ASan/LSan. close-with-contention wakes every blocked worker
        // (broadcast → void → exit), the structured join reaps all five tasks, every channel/arena
        // frees. Zero leaks / UAF, and the aggregate invariant still holds.
        Assert.Equal("420", CompileSanitized(FanOutWorkQueue));
    }

    // ── Arc 1A: book substrate + exact-decimal math + `sorted` ──
    // Books are builtin + compile-time-resolved. These are ordinary Compile == Interpret oracle
    // tests and run on BOTH platforms (no POSIX). Math totals are exact-decimal (bit-identical to
    // the interpreter's decimal overloads); `sorted` is a stable natural/by-field sort.

    [Fact]
    public void Book_Math_ExactFunctions()
    {
        const string src = """
            Pull a book on math.
                State math's floor of 3.99.
                State math's floor of -3.1.
                State math's ceiling of 3.01.
                State math's ceiling of -3.9.
                State math's round of 2.5.
                State math's round of -2.5.
                State math's round of 2.4.
                State math's absolute-value of -7.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }
}
