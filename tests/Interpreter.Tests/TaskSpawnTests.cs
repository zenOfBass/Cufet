using Cufet.Interpreter;
using Cufet.Lexer;
using System.Runtime.ExceptionServices;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

// Acceptance tests for concurrency core slice 2:
//   "Have rabbit start a task [as <name>]: ... Done."
// Verifies: spawn + structured join, requires-rabbit error, optional name,
//           soundness (region check fires from inside a task body), sequential unaffected.
public class TaskSpawnTests
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

    // ── Spawn + join ─────────────────────────────────────────────────────────

    // A single anonymous task runs and its output appears before the post-rabbit statement.
    [Fact]
    public void SingleTaskRunsBeforeRabbitDone()
    {
        var result = Run("""
            Pull a rabbit.
                Have rabbit start a task:
                    State "task ran".
                Done.
            Done.
            State "after".
            """);
        Assert.Equal("task ran\nafter", result);
    }

    // Two tasks both run and complete before the post-rabbit statement (join-at-Done.).
    [Fact]
    public void TwoTasksBothCompleteBeforeRabbitDone()
    {
        var result = Run("""
            Pull a rabbit.
                Have rabbit start a task:
                    State "task A".
                Done.
                Have rabbit start a task:
                    State "task B".
                Done.
            Done.
            State "after".
            """);
        // Tasks run in enqueue order during JoinTasks drain.
        Assert.Equal("task A\ntask B\nafter", result);
    }

    // Main rabbit body runs before tasks (tasks are enqueued and run at Done.).
    // Proves: body statements execute before tasks, tasks execute before post-Done output.
    [Fact]
    public void RabbitBodyRunsBeforeJoinedTasks()
    {
        var result = Run("""
            Pull a rabbit.
                Have rabbit start a task:
                    State "task A".
                Done.
                State "during rabbit".
                Have rabbit start a task:
                    State "task B".
                Done.
            Done.
            State "after".
            """);
        Assert.Equal("during rabbit\ntask A\ntask B\nafter", result);
    }

    // Tasks can read variables defined in the enclosing rabbit scope.
    [Fact]
    public void TaskCanReadRabbitScopeVariable()
    {
        var result = Run("""
            Pull a rabbit.
                Define message as "hello from rabbit".
                Have rabbit start a task:
                    State message.
                Done.
            Done.
            """);
        Assert.Equal("hello from rabbit", result);
    }

    // Task body can define its own locals without leaking them.
    [Fact]
    public void TaskLocalVariableDoesNotLeakOut()
    {
        var result = Run("""
            Pull a rabbit.
                Have rabbit start a task:
                    Define local as "task-local".
                    State local.
                Done.
            Done.
            """);
        Assert.Equal("task-local", result);
    }

    // Nested rabbits: each rabbit joins only the tasks spawned in its own scope.
    [Fact]
    public void NestedRabbitsJoinIndependently()
    {
        var result = Run("""
            Pull a rabbit.
                Have rabbit start a task:
                    State "outer task".
                Done.
                Pull a rabbit.
                    Have rabbit start a task:
                        State "inner task".
                    Done.
                Done.
            Done.
            State "after".
            """);
        Assert.Equal("outer task\ninner task\nafter", result);
    }

    // ── Requires active rabbit ────────────────────────────────────────────────

    // Have rabbit start a task outside any Pull a rabbit. is a parse error.
    [Fact]
    public void LaunchTaskOutsideRabbitIsParseError()
    {
        Assert.Throws<ParseException>(() => Run("""
            Have rabbit start a task:
                State "nope".
            Done.
            """));
    }

    // Inside a loop but outside a rabbit is still a parse error.
    [Fact]
    public void LaunchTaskInsideLoopButOutsideRabbitIsParseError()
    {
        Assert.Throws<ParseException>(() => Run("""
            Define i as 1.
            While i is 1, repeat:
                Have rabbit start a task:
                    State "nope".
                Done.
                i becomes 2.
            Done.
            """));
    }

    // ── Optional name ────────────────────────────────────────────────────────

    // Named form parses and runs; name is inert in slice 2 (just binds identity).
    [Fact]
    public void NamedTaskRunsIdenticallyToAnonymous()
    {
        var result = Run("""
            Pull a rabbit.
                Have rabbit start a task as worker:
                    State "worker ran".
                Done.
            Done.
            """);
        Assert.Equal("worker ran", result);
    }

    // Multiple named tasks all run.
    [Fact]
    public void MultipleNamedTasksBothRun()
    {
        var result = Run("""
            Pull a rabbit.
                Have rabbit start a task as alpha:
                    State "alpha".
                Done.
                Have rabbit start a task as beta:
                    State "beta".
                Done.
            Done.
            """);
        Assert.Equal("alpha\nbeta", result);
    }

    // ── Soundness — region check fires from inside a task body ────────────────

    // A task-local reference-type value cannot be stored into a longer-lived container.
    // The existing CheckRegionStore invariant covers this — no new machinery needed.
    [Fact]
    public void TaskLocalSeriesCannotEscapeToOuterScope()
    {
        Assert.Throws<TypeException>(() => Run("""
            Define outer as a series of number with ().
            Pull a rabbit.
                Have rabbit start a task:
                    Define inner as a series of number with (1, 2).
                    Insert inner into outer.
                Done.
            Done.
            """));
    }

    // ── Sequential programs unaffected ───────────────────────────────────────

    // A rabbit with no tasks works exactly as before.
    [Fact]
    public void RabbitWithNoTasksWorkAsBeforeSlice2()
    {
        var result = Run("""
            Pull a rabbit.
                State "in rabbit".
            Done.
            State "after".
            """);
        Assert.Equal("in rabbit\nafter", result);
    }

    // A program with no rabbits or tasks works exactly as before.
    [Fact]
    public void ProgramWithNoRabbitOrTaskUnchanged()
    {
        var result = Run("""
            Define x as 42.
            State x.
            """);
        Assert.Equal("42", result);
    }

    // ── Giving work to a rabbit BY NAME ──
    //
    // ★ A rabbit is an agent you summon and give a job to, so naming one at the point you hand it
    // work is the reason names exist at all. Until now only the bare keyword was accepted, which
    // made `Pull a rabbit as den.` decorative: the name bound a value you could print and pass, but
    // never the one form that takes a rabbit.

    [Fact]
    public void ARabbitCanBeGivenWorkByName()
    {
        Assert.Equal("3", Run("""
            Pull a rabbit as den.
                Have den start a task as work: return 1 + 2. Done.
                State the awaited result of work.
            Done.
            """));
    }

    [Fact]
    public void TheKeywordAndTheNameCanBeMixed()
    {
        // `rabbit` still means "the enclosing one", and naming it is the same rabbit either way.
        Assert.Equal("3\n10", Run("""
            Pull a rabbit as den.
                Have den start a task as work: return 1 + 2. Done.
                Have rabbit start a task as other: return 10. Done.
                State the awaited result of work.
                State the awaited result of other.
            Done.
            """));
    }

    /// <summary>
    /// Naming a rabbit pulled FURTHER OUT is refused.
    /// </summary>
    /// <remarks>
    /// ⚠ Not a spelling restriction — a lifetime one. A task is joined by its rabbit's `Done.`, so
    /// giving work to an outer rabbit would mean a task outliving the block it was written in.
    /// That is a real feature (a task that survives an inner scope) and not one to acquire by
    /// accident while adding a name, so it is refused until it is designed.
    /// </remarks>
    [Fact]
    public void AnOuterRabbitCannotBeGivenWorkFromAnInnerBlock()
    {
        var ex = Assert.Throws<TypeException>(() => Run("""
            Pull a rabbit as outer.
                Pull a rabbit as inner.
                    Have outer start a task as work: return 1. Done.
                    State the awaited result of work.
                Done.
            Done.
            """));
        Assert.Contains("not the rabbit this block belongs to", ex.Message);
    }

    [Fact]
    public void SomethingThatIsNotARabbitCannotBeGivenWork()
    {
        var ex = Assert.Throws<TypeException>(() => Run("""
            Define notarabbit as 5.
            Pull a rabbit as den.
                Have notarabbit start a task as work: return 1. Done.
                State the awaited result of work.
            Done.
            """));
        Assert.Contains("is not a rabbit", ex.Message);
    }
}
