using Cufet.Interpreter;
using Cufet.Lexer;
using System.Runtime.ExceptionServices;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

// Acceptance tests for concurrency core slice 4: task results.
//   Have rabbit start a task as <name>:  return <value>.  Done.
//   the awaited result of <name>
public class TaskResultTests
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

    // ── Spawn-compute-collect ─────────────────────────────────────────────────

    // Task computes a value and returns it; main flow awaits and prints.
    [Fact]
    public void SpawnComputeCollect()
    {
        var output = Run("""
            Pull a rabbit.
                Have rabbit start a task as fetcher:
                    Define x as 21 + 21.
                    return x.
                Done.
                Define answer as the awaited result of fetcher.
                State answer.
            Done.
            """);
        Assert.Equal("42", output);
    }

    // Task that returns text.
    [Fact]
    public void TaskReturnsText()
    {
        var output = Run("""
            Pull a rabbit.
                Have rabbit start a task as greeter:
                    return "hello from task".
                Done.
                Define msg as the awaited result of greeter.
                State msg.
            Done.
            """);
        Assert.Equal("hello from task", output);
    }

    // ── Await yields until done ───────────────────────────────────────────────

    // Task is enqueued but hasn't run yet; awaiting it drains the scheduler and collects.
    [Fact]
    public void AwaitYieldsUntilTaskDone()
    {
        var output = Run("""
            Pull a rabbit.
                Have rabbit start a task as worker:
                    return 99.
                Done.
                Define result as the awaited result of worker.
                State result.
            Done.
            """);
        Assert.Equal("99", output);
    }

    // ── Main flow does other work before awaiting ─────────────────────────────

    [Fact]
    public void MainDoesWorkThenAwaits()
    {
        var output = Run("""
            Pull a rabbit.
                Have rabbit start a task as background:
                    return 10.
                Done.
                State "before await".
                Define result as the awaited result of background.
                State result.
            Done.
            """);
        Assert.Equal("before await\n10", output);
    }

    // ── Never-awaited named task ──────────────────────────────────────────────

    // A named task that is never awaited still runs and joins at Done. (no error, result dropped).
    [Fact]
    public void NeverAwaitedNamedTaskRunsAndJoins()
    {
        var output = Run("""
            Pull a rabbit.
                Have rabbit start a task as sideEffect:
                    State "side effect ran".
                    return 0.
                Done.
            Done.
            """);
        Assert.Equal("side effect ran", output);
    }

    // ── Double-await caches ───────────────────────────────────────────────────

    // Awaiting the same task twice returns the same value; task runs only once.
    [Fact]
    public void DoubleAwaitCaches()
    {
        var output = Run("""
            Pull a rabbit.
                Have rabbit start a task as counter:
                    State "task ran".
                    return 7.
                Done.
                Define r1 as the awaited result of counter.
                Define r2 as the awaited result of counter.
                State r1.
                State r2.
            Done.
            """);
        Assert.Equal("task ran\n7\n7", output);
    }

    // ── Await after task is already complete ──────────────────────────────────

    // If the task finishes before we await (e.g. joined by another await), result is immediate.
    [Fact]
    public void AwaitAfterCompletionIsImmediate()
    {
        var output = Run("""
            Pull a rabbit.
                Have rabbit start a task as t1:
                    return 5.
                Done.
                Have rabbit start a task as t2:
                    return 10.
                Done.
                Define r1 as the awaited result of t1.
                Define r2 as the awaited result of t2.
                State r1 + r2.
            Done.
            """);
        Assert.Equal("15", output);
    }

    // ── Fallible task ─────────────────────────────────────────────────────────

    // Task that returns a failure — awaited result is T or failure, handled with but on failure.
    [Fact]
    public void FallibleTaskHandledAtAwaitSite()
    {
        var output = Run("""
            Pull a rabbit.
                Have rabbit start a task as risky:
                    return a failure "task failed" of category "err".
                    return 0.
                Done.
                Define r as the awaited result of risky but on failure (99).
                State r.
            Done.
            """);
        Assert.Equal("99", output);
    }

    // Task that may or may not fail depending on logic.
    [Fact]
    public void FallibleTaskSuccessPath()
    {
        var output = Run("""
            Pull a rabbit.
                Have rabbit start a task as compute:
                    If 1 is 1, return 42. Otherwise, return a failure "bad".
                Done.
                Define r as the awaited result of compute but on failure (0).
                State r.
            Done.
            """);
        Assert.Equal("42", output);
    }

    // ── Type errors ───────────────────────────────────────────────────────────

    // 'the awaited result of' on a non-task is a type error.
    [Fact]
    public void AwaitNonTaskIsTypeError()
    {
        Assert.Throws<TypeException>(() => Run("""
            Pull a rabbit.
                Define x as 5.
                Define r as the awaited result of x.
            Done.
            """));
    }

    // 'the awaited result of' on a void task (no return) is a type error.
    [Fact]
    public void AwaitVoidTaskIsTypeError()
    {
        Assert.Throws<TypeException>(() => Run("""
            Pull a rabbit.
                Have rabbit start a task as t:
                    State "no return".
                Done.
                Define r as the awaited result of t.
            Done.
            """));
    }

    // Fallible task result that is not handled is a type error.
    [Fact]
    public void UnhandledFallibleTaskIsTypeError()
    {
        Assert.Throws<TypeException>(() => Run("""
            Pull a rabbit.
                Have rabbit start a task as risky:
                    return a failure "oops".
                    return 0.
                Done.
                Define r as the awaited result of risky.
            Done.
            """));
    }

    // ── Producer with task result ─────────────────────────────────────────────

    // Task sends through channel and returns the count; main awaits the count.
    [Fact]
    public void TaskResultCombinesWithChannels()
    {
        var output = Run("""
            Pull a rabbit.
                Define ch as a channel of number.
                Have rabbit start a task as producer:
                    Send 1 through ch.
                    Send 2 through ch.
                    Send 3 through ch.
                    Close ch.
                    return 3.
                Done.
                Define total as 0.
                Define val as the delivery from ch.
                While val is not void, repeat:
                    total becomes total + (val but void is 0).
                    val becomes the delivery from ch.
                Done.
                Define count as the awaited result of producer.
                State total.
                State count.
            Done.
            """);
        Assert.Equal("6\n3", output);
    }
}
