using Cufet.Interpreter;
using Cufet.Lexer;
using System.Runtime.ExceptionServices;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

// Acceptance tests for concurrency core slice 5: SIGINT at yield points + Yield.
//   Yield.               — cooperative scheduler yield + interrupt checkpoint
//   an interrupt is requested  — renamed fact (was "an interrupt has been requested")
//   DrainUntil checks _interruptRequested — blocked delivery/await wakes on interrupt
public class YieldTests
{
    private static string Run(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
        var output  = new StringWriter();
        RunOnLargeStack(() => new Interpreter(output).Execute(program));
        return output.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    // Pre-set the interrupt flag before running; used to simulate Ctrl-C arriving
    // before the program starts. Tests that hit a Yield or a blocked yield point
    // with the flag set will throw InterruptUnwind and propagate out of RunOnLargeStack.
    private static string RunInterrupted(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
        var output  = new StringWriter();
        var interp  = new Interpreter(output);
        interp.SimulateInterrupt();
        RunOnLargeStack(() => interp.Execute(program));
        return output.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    private static void RunOnLargeStack(Action action)
    {
        Exception? caught = null;
        var thread = new Thread(
            () => { try { action(); } catch (Exception e) { caught = e; } },
            16 * 1024 * 1024);
        thread.Start();
        thread.Join();
        if (caught is not null)
            ExceptionDispatchInfo.Capture(caught).Throw();
    }

    // ── Fact rename ───────────────────────────────────────────────────────────

    [Fact]
    public void FactRename_NewFormTypechecks()
    {
        // "an interrupt is requested" is the correct form — type-checks and runs.
        Assert.Equal("ok", Run("""
            Define r as an interrupt is requested.
            If r, State "interrupted". Otherwise, State "ok".
            """));
    }

    [Fact]
    public void FactRename_OldFormIsParseError()
    {
        // The old phrase "an interrupt has been requested" is no longer accepted.
        Assert.Throws<ParseException>(() => Run("""
            Define r as an interrupt has been requested.
            """));
    }

    // ── Yield. — basic behaviour ──────────────────────────────────────────────

    [Fact]
    public void Yield_WithNoInterrupt_ResumesNormally()
    {
        // When no interrupt is pending, Yield. is a no-op: execution continues on the
        // next statement in the same scope — not the start of the next loop iteration.
        var output = Run("""
            Define count as 0.
            While count is less than 3, repeat:
                Yield.
                count becomes count + 1.
            Done.
            State count.
            """);
        Assert.Equal("3", output);
    }

    [Fact]
    public void Yield_MakesTightLoopInterruptible()
    {
        // Without Yield. a tight loop has no yield points, so an interrupt can never
        // be observed. With Yield. the interrupt fires on the first iteration instead
        // of the loop running forever. The test verifies termination via the exception.
        Assert.ThrowsAny<Exception>(() => RunInterrupted("""
            While 1 = 1, repeat:
                Yield.
            Done.
            """));
    }

    [Fact]
    public void Yield_OutsideRabbit_ChecksInterrupt()
    {
        // Yield. in a sequential program (no scheduler) still checks the interrupt flag.
        Assert.ThrowsAny<Exception>(() => RunInterrupted("""
            Yield.
            State "after yield".
            """));
    }

    // ── Yield. — cooperative multitasking ────────────────────────────────────

    [Fact]
    public void Yield_LetsOtherTasksRun()
    {
        // looper runs first (enqueued first). Its Yield. gives the anonymous quick task
        // a chance to run between iterations. Both tasks complete; output shows both ran.
        var output = Run("""
            Pull a rabbit.
                Have rabbit start a task as looper:
                    Define i as 0.
                    While i is less than 3, repeat:
                        Yield.
                        i becomes i + 1.
                    Done.
                    State "looper done".
                Done.
                Have rabbit start a task:
                    State "quick done".
                Done.
            Done.
            """);
        Assert.Contains("quick done", output);
        Assert.Contains("looper done", output);
    }

    [Fact]
    public void Yield_ResumesHereNotNextIteration()
    {
        // Yield. in the MIDDLE of a loop body — code after it in the same iteration runs.
        // Verifies Yield is a scheduler-yield, not a loop-control statement like Skip.
        var output = Run("""
            Pull a rabbit.
                Have rabbit start a task as t:
                    Define items as a series with (1, 2, 3).
                    Define acc as 0.
                    For each n in items, repeat:
                        acc becomes acc + n.
                        Yield.
                        acc becomes acc + 10.
                    Done.
                    State acc.
                Done.
            Done.
            """);
        // Each iteration: acc += n then += 10. Items are 1, 2, 3.
        // Iteration 1: acc = 0+1 = 1, Yield, acc = 1+10 = 11
        // Iteration 2: acc = 11+2 = 13, Yield, acc = 13+10 = 23
        // Iteration 3: acc = 23+3 = 26, Yield, acc = 26+10 = 36
        Assert.Equal("36", output);
    }

    // ── Blocked yield points — interrupt wakes them ───────────────────────────

    [Fact]
    public void DeliveryBlockedByInterrupt_DoesNotDeadlock()
    {
        // Without interrupt, a delivery on an empty unclosed channel deadlocks
        // (no tasks will ever send). With interrupt set, DrainUntil exits via
        // InterruptUnwind instead of throwing the deadlock error.
        var ex = Assert.ThrowsAny<Exception>(() => RunInterrupted("""
            Pull a rabbit.
                Define ch as a channel of number.
                Define val as the delivery from ch.
            Done.
            """));
        Assert.DoesNotContain("deadlock", ex.Message ?? "");
    }

    [Fact]
    public void AwaitedResult_InterruptedWhileWaiting_DoesNotHang()
    {
        // Awaiting a running task when the interrupt is already set should unwind
        // immediately rather than waiting for the task to complete.
        Assert.ThrowsAny<Exception>(() => RunInterrupted("""
            Pull a rabbit.
                Have rabbit start a task as t:
                    return 42.
                Done.
                Define r as the awaited result of t.
                State r.
            Done.
            """));
    }
}
