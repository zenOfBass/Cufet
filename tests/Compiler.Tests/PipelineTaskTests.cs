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
public class PipelineTaskTests : PipelineTestBase
{

    [Fact]
    public void Exception_NestedInnermostWins_ThenReRaisesOutward()
    {
        // The inner handler catches first; without Suppress it re-raises the SAME exception to the
        // outer handler (same message). The statement after the inner Try is not reached.
        const string src = """
            Try to:
                Try to:
                    Define z as 5 / 0.
                Done.
                In case of exception (the exception):
                    State "inner caught".
                    State the message of the exception.
                Done.
                State "not reached".
            Done.
            In case of exception (the exception):
                State "outer caught the re-raise".
                State the message of the exception.
                Suppress the exception.
            Done.
            State "after nesting".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Exception_ComposesWithFailureHandler()
    {
        // One Try, BOTH handlers: a value failure routes to In case of failure; a runtime fault
        // routes to In case of exception. The two mechanisms (goto vs longjmp) stay independent.
        const string src = """
            Bind the number or failure to risky, given (the fact which):
                If which, return a failure "a value failure" of category "biz".
                return 5.
            Done.
            Try to:
                Define r1 as cast risky on (true).
                State "no failure".
            Done.
            In case of failure:
                State "failure handler: " joined to the message of the failure.
            Done.
            In case of exception (the exception):
                State "exception handler (wrong)".
                Suppress the exception.
            Done.
            Try to:
                Define r2 as cast risky on (false).
                State r2.
                Define r3 as r2 / 0.
            Done.
            In case of failure:
                State "failure handler (wrong)".
            Done.
            In case of exception (the exception):
                State "exception handler: " joined to the message of the exception.
                Suppress the exception.
            Done.
            State "end".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Exception_CleanupOnLongjmp_FileFlushedAndClosed()
    {
        // ★ The crux: a fault INSIDE a With-open block, caught by an OUTER handler — the longjmp
        // jumps past the emit-time fclose, so the RUNTIME registry must flush+close the file. The
        // read-back proves no data loss (the 9B proof, applied to the nonlocal-jump path).
        var path = (Path.GetTempPath().Replace('\\', '/').TrimEnd('/')) + "/cufet-eprime-" + Guid.NewGuid().ToString("N")[..8] + ".txt";
        try
        {
            string src = $"""
                Try to:
                    With the file "{path}" open for writing as out:
                        write "written before the fault" to out.
                        Define x as 1 / 0.
                        write "never written" to out.
                    Done.
                Done.
                In case of exception (the exception):
                    State "caught: " joined to the message of the exception.
                    Suppress the exception.
                Done.
                Try to:
                    Define back as read all from the file "{path}".
                    State back.
                Done.
                In case of failure:
                    State "read failed".
                Done.
                """;
            Assert.Equal(InterpretRaw(src), CompileRaw(src));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Exception_Uncaught_KeepsPrintExitBehavior()
    {
        // No handler → the pre-exception behavior is unchanged: message to stderr, exit 1. The
        // interpreter throws RuntimeException; the compiled binary prints what ran before the fault.
        const string src = """
            State "before".
            Define x as 1 / 0.
            State "after".
            """;
        Assert.Throws<RuntimeException>(() => Interpret(src));
        Assert.Equal("before", Compile(src));
    }

    [Fact]
    public void Exception_LoopScopedTry_SuppressAndContinue()
    {
        // A Try inside a loop: each iteration's handler catches independently; setjmp-modified
        // locals (the counter) survive the longjmps (gcc's returns_twice conservatism, verified).
        const string src = """
            Define counter as 0.
            While counter is less than 3, repeat:
                Try to:
                    Define q as 1 / (counter - 1).
                    State q.
                Done.
                In case of exception (the exception):
                    State "loop caught".
                    Suppress the exception.
                Done.
                counter becomes counter + 1.
            Done.
            State "done".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Exception_MemorySafety_ASan()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // The registries free exactly-once across the longjmp (no double-close/free; arena pops).
        const string src = """
            Try to:
                Define xs as a series with (1, 2, 3).
                State item 99 of xs.
            Done.
            In case of exception (the exception):
                State "caught oob".
                Suppress the exception.
            Done.
            State "done".
            """;
        Assert.Equal(Interpret(src), CompileWithASan(src));
    }

    [Fact]
    public void Sort_MemorySafety_ASan()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // `sorted` builds a NEW arena series (non-mutating); it must free cleanly at scope exit.
        const string src = """
            Define nums as a series with (5, 3, 8, 1, 9, 2, 7).
            Define s as nums sorted.
            State s.
            """;
        Assert.Equal(Interpret(src), CompileWithASan(src));
    }

    // ── CONC.C: named tasks + `the awaited result of` (result crosses task → awaiter) ──
    // LINUX-ONLY (pthreads). Unlike the channel spawn-collect pattern, an AWAIT drains the
    // cooperative interpreter deterministically (no deadlock) and the awaited VALUE is
    // deterministic regardless of timing — so these ARE true Compile == Interpret oracle tests.

    // ── Awaits inside tasks (result boxes) ───────────────────────────────────
    // A named task publishes its result to a box; awaiters wait on the box and deep-copy into
    // their own arena. Nobody joins at an await site — pthread_join happens once, in the rabbit's
    // Done. teardown. That is what makes the two-awaiters case below safe BY CONSTRUCTION: a
    // check-then-join guard is only sound while exactly one thread can run it.
    //
    // A cycle cannot be written: awaiting a task needs its name in scope, so it was declared
    // earlier, so the wait graph is a DAG and the front end rejects the forward reference a cycle
    // would require. There is no deadlock case to test because there is no deadlock to have.

    [Fact]
    public void Concurrency_TwoTasksAwaitTheSameTask_MatchesInterpreter()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // THE case the previous design could not express. Both awaiters get their own arena copy
        // of one published envelope; 5*2 + 5*3 = 25.
        const string src = """
            Pull a rabbit.
                Have rabbit start a task as base-task:
                    return 5.
                Done.
                Have rabbit start a task as left-task:
                    Define v as the awaited result of base-task.
                    return v * 2.
                Done.
                Have rabbit start a task as right-task:
                    Define w as the awaited result of base-task.
                    return w * 3.
                Done.
                State (the awaited result of left-task) + (the awaited result of right-task).
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Concurrency_AwaitChainThreeDeep_MatchesInterpreter()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        const string src = """
            Pull a rabbit.
                Have rabbit start a task as one-task:
                    return 1.
                Done.
                Have rabbit start a task as two-task:
                    Define a-val as the awaited result of one-task.
                    return a-val + 1.
                Done.
                Have rabbit start a task as three-task:
                    Define b-val as the awaited result of two-task.
                    return b-val + 1.
                Done.
                State the awaited result of three-task.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Concurrency_AwaitInsideTask_NestedReference_DeepCopies()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // The crux: an object holding a series crosses task → task. Arenas are thread-local, so a
        // shallow copy would dangle the moment the producing task pops its arena — which ASan
        // catches. Reading 4 back means the copy went all the way through.
        const string src = """
            Define object box with (the series of number nums, the text label):
            Done.
            Pull a rabbit.
                Have rabbit start a task as maker:
                    Define b as a new box { the nums (a series of number with (1, 2, 3, 4)), the label "b" }.
                    return b.
                Done.
                Have rabbit start a task as reader:
                    Define got as the awaited result of maker.
                    return the number of (the nums of got).
                Done.
                State the awaited result of reader.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Concurrency_AwaitInsideTask_TextResult_MatchesInterpreter()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        const string src = """
            Pull a rabbit.
                Have rabbit start a task as maker:
                    return "hello".
                Done.
                Have rabbit start a task as user:
                    Define s as the awaited result of maker.
                    return s joined to " world".
                Done.
                State the awaited result of user.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Concurrency_AwaitInsideTask_FallibleResult_MatchesInterpreter()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        const string src = """
            Pull a rabbit.
                Have rabbit start a task as risky:
                    If 1 is 2, return 1.
                    return a failure "nope" of category "test".
                Done.
                Have rabbit start a task as handler:
                    Define v as (the awaited result of risky) but on failure 99.
                    return v.
                Done.
                State the awaited result of handler.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Concurrency_NamedTaskNeverAwaited_StillFreesItsResult()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // Ownership moved: the envelope now lives in the box until Done. frees it through the
        // recorded freeenv, rather than being freed at an await that may never happen. A
        // reference-typed result nobody reads must still free deeply — LSan is the real assertion.
        const string src = """
            Pull a rabbit.
                Have rabbit start a task as ignored:
                    return "never read".
                Done.
                Have rabbit start a task as loud:
                    return 7.
                Done.
                State the awaited result of loud.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Concurrency_AwaitedResult_Number()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // A named task computes a value and returns it; the awaiter joins, deep-copies the
        // heap-bridged result into itself, and prints it. Deterministic result: 42.
        const string src = """
            Pull a rabbit.
                Have rabbit start a task as fetcher:
                    Define x as 21 + 21.
                    return x.
                Done.
                Define answer as the awaited result of fetcher.
                State answer.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Concurrency_DoubleAwait_CachesResult()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // Awaiting the same task twice joins it ONCE (guarded by the joined-flag) and reads the
        // cached result the second time — the task body ("task ran") runs exactly once. Proves
        // no double pthread_join (undefined) and no double-free of the result bridge.
        const string src = """
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
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Concurrency_TwoTasks_AwaitBoth()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // Two named tasks, each result crosses its own task → awaiter boundary; the awaiter sums
        // them (5 + 10 == 15). Each join synchronizes its own result independently.
        const string src = """
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
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Concurrency_FallibleTask_HandledAtAwaitSite()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // A task whose result is `number or failure`: the failing path returns a failure, and the
        // awaited result flows through the SAME fallible machinery as a fallible call — `but on
        // failure` supplies the default (99). Reuses slice-6 `cfl_N` end to end.
        const string src = """
            Pull a rabbit.
                Have rabbit start a task as risky:
                    return a failure "task failed" of category "err".
                    return 0.
                Done.
                Define r as the awaited result of risky but on failure (99).
                State r.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Concurrency_NeverAwaitedNamedTask_ASan_FreesResultBridge()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // A named task that returns a value but is NEVER awaited still runs and joins at the
        // rabbit's Done.; the structured teardown captures + frees its heap-bridged result so it
        // does not leak. ASan/LSan must be clean (the free-on-all-paths proof for un-awaited results).
        const string src = """
            Pull a rabbit.
                Have rabbit start a task as sideEffect:
                    State "side effect ran".
                    return 0.
                Done.
            Done.
            """;
        Assert.Equal(Interpret(src), CompileWithASan(src));
    }

    // ── Text/reference task results (channel-of-T follow-on: `the awaited result of` beyond num/fact) ──
    // The task→awaiter boundary is the third direction of the SAME heap bridge as a channel send: on
    // return the result is deep-copied to a malloc'd envelope (channel-of-T copy-family), pthread_exit'd,
    // and the await joins → arena-copies into the awaiter → frees the envelope. An await drains the
    // interpreter deterministically, so the awaited VALUE is deterministic ⇒ true Compile==Interpret.

    [Fact]
    public void Concurrency_AwaitedResult_Text()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        const string src = """
            Pull a rabbit.
                Have rabbit start a task as greeter:
                    Define s as "hello " joined to "world".
                    return s.
                Done.
                Define got as the awaited result of greeter.
                State got.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Concurrency_AwaitedResult_Series()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // The result is a series (reference type). The task's arena is popped after return, so the
        // heap bridge + arena copy must be genuinely deep — a shallow copy would be a use-after-free
        // (ASan would catch it); a clean, correct read proves the deep copy crossed arena-independently.
        const string src = """
            Pull a rabbit.
                Have rabbit start a task as maker:
                    Define xs as a series of number with (1, 2, 3).
                    return xs.
                Done.
                Define got as the awaited result of maker.
                State "len=" joined to ((the number of got) converted to text) joined to " first=" joined to ((the first of got) converted to text).
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Concurrency_AwaitedResult_ObjectWithSeriesField_DeepCopy()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // The crux of a GENUINELY-deep result copy: an object whose field is a series. The whole
        // nested structure must cross the task→awaiter boundary and survive the task's arena teardown.
        const string src = """
            Define object bundle with (the text label, the series of number nums).
            Pull a rabbit.
                Have rabbit start a task as maker:
                    Define ns as a series of number with (10, 20, 30).
                    Define b as a new bundle { the label "made", the nums ns }.
                    return b.
                Done.
                Define got as the awaited result of maker.
                State (the label of got) joined to " nums-len=" joined to ((the number of (the nums of got)) converted to text).
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Concurrency_AwaitedResult_Map()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        const string src = """
            Pull a rabbit.
                Have rabbit start a task as maker:
                    Define m as a map from text to number with ("a" : 1, "b" : 2).
                    return m.
                Done.
                Define got as the awaited result of maker.
                State "size=" joined to ((the size of got) converted to text) joined to " a=" joined to ((the entry for "a" in got but void is 0) converted to text).
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Concurrency_DoubleAwait_ReferenceResult()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // Double-await of a REFERENCE result: the body ("ran") runs exactly once, the join happens
        // once, and the cached arena copy is read on both awaits — no double-join, no double-free.
        const string src = """
            Pull a rabbit.
                Have rabbit start a task as maker:
                    State "ran".
                    return a series of text with ("a", "b").
                Done.
                Define r1 as the awaited result of maker.
                Define r2 as the awaited result of maker.
                State (the first of r1).
                State (the second of r2).
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Concurrency_FallibleTask_TextInner_ComposesWithButOnFailure()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // A `text or failure` task result: the wrapper (cfl) composes with the reference inner (text)
        // — the deep-copy family handles the inner T while the failable machinery (5C/6) is untouched.
        const string src = """
            Pull a rabbit.
                Have rabbit start a task as risky:
                    If 1 is 2, return a failure "nope" of category "err".
                    return "recovered text".
                Done.
                Define got as the awaited result of risky but on failure ("defaulted").
                State got.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Concurrency_NeverAwaitedReferenceResult_ASan_FreesNestedBridge()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // A named task whose REFERENCE result is never awaited: the Done.-join teardown must free the
        // whole heap bridge THROUGH the slot's freeenv (not just the envelope pointer), so the nested
        // series allocations free too. ASan/LSan clean = the free-on-all-paths proof for reference results.
        const string src = """
            Pull a rabbit.
                Have rabbit start a task as maker:
                    State "side effect".
                    return a series of text with ("x", "y", "z").
                Done.
                State "done".
            Done.
            """;
        Assert.Equal(Interpret(src), CompileWithASan(src));
    }
}
