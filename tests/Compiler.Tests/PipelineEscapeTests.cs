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
public class PipelineEscapeTests : PipelineTestBase
{

    [Fact]
    public void TaskCapture_TextAndMap_MatchInterpreter()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;
        const string src = """
            Define label as "hello" joined to " world".
            Define lookup as a map from text to number with ("a": 1, "b": 2).
            Pull a rabbit.
                Have rabbit start a task as len:
                    return the length of label + the size of lookup.
                Done.
                State the awaited result of len.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void TaskCapture_ObjectAndCatalogue_MatchInterpreter()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;
        const string src = """
            Define object point with (the number x, the number y).
            Define p as a new point { the x 3, the y 4 }.
            Define items as a catalogue of (number or text) with (1, "two", 3).
            Pull a rabbit.
                Have rabbit start a task as s:
                    Define n as 0.
                    For each item in items, repeat:
                        If item is a number, n becomes n + 1.
                    Done.
                    return p's x + p's y + n.
                Done.
                State the awaited result of s.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // The crux for a DEEP copy: an object with a series field, inside a series. A shallow bridge
    // would carry pointers into the parent's arena rather than rebuilding in the task's.
    [Fact]
    public void TaskCapture_NestedSeriesOfObjectsWithSeriesFields_MatchesInterpreter()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;
        const string src = """
            Define object row with (the text label, the series of number cells).
            Define grid as a series of row with ().
            Add a new row { the label "a", the cells a series of number with (1, 2, 3) } to grid.
            Add a new row { the label "b", the cells a series of number with (4, 5) } to grid.
            Pull a rabbit.
                Have rabbit start a task as total:
                    Define t as 0.
                    For each r in grid, repeat:
                        For each c in r's cells, repeat:
                            t becomes t + c.
                        Done.
                    Done.
                    return t.
                Done.
                State the awaited result of total.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void TaskCapture_TwoTasksCaptureTheSameSeries_MatchesInterpreter()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;
        // Each task gets its own copy, so there is nothing shared to race on (TSan-clean in WSL).
        const string src = """
            Define data as a series of number with (1, 2, 3, 4).
            Pull a rabbit.
                Have rabbit start a task as alpha:
                    Define t as 0.
                    For each v in data, repeat:
                        t becomes t + v.
                    Done.
                    return t.
                Done.
                Have rabbit start a task as beta:
                    Define t as 0.
                    For each v in data, repeat:
                        t becomes t + v * 2.
                    Done.
                    return t.
                Done.
                State (the awaited result of alpha) + (the awaited result of beta).
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ★ The one shape that must NOT compile. The interpreter hands task bodies the LIVE enclosing
    // binding, so it prints (1, 2, 3, 99); a copy would print (1, 2, 3). The rabbit's join is a
    // happens-before edge, so both answers are well-defined and they differ — the class that never
    // ships as a caveat. Refusing is also right on its own terms: two tasks appending to one
    // captured series is a real data race that the cooperative interpreter merely hides.
    [Fact]
    public void TaskCapture_MutatingACapturedSeries_IsRefused()
    {
        const string src = """
            Define data as a series of number with (1, 2, 3).
            Pull a rabbit.
                Have rabbit start a task:
                    Add 99 to data.
                Done.
            Done.
            State data.
            """;
        var ex = Assert.Throws<CompilerException>(() => Compile(src));
        Assert.Contains("captured from outside the task", ex.Message);
    }

    // ★ A captured NUMBER is refused on exactly the same terms, and this one was a live divergence
    // before it was: `tally becomes tally + 5` inside a task printed 5 interpreted (the interpreter
    // hands task bodies the live enclosing binding) and 0 compiled (the task writes its snapshot).
    // The parent never touches `tally` after the spawn and the rabbit's join is a happens-before
    // edge, so this is not a race — it is one program with two well-defined, differing answers.
    // Nothing about a value being small or trivially copyable makes the write meaningful.
    [Fact]
    public void TaskCapture_MutatingACapturedNumber_IsRefused()
    {
        const string src = """
            Define tally as 0.
            Pull a rabbit.
                Have rabbit start a task as bump:
                    tally becomes tally + 5.
                    return 1.
                Done.
                State the awaited result of bump.
            Done.
            State tally.
            """;
        var ex = Assert.Throws<CompilerException>(() => Compile(src));
        Assert.Contains("captured from outside the task", ex.Message);
    }

    // ★ The same write, one `If` deep — which used to slip past the refusal entirely.
    //
    // `TaskBodyMayMutate` descended by matching IExpression/IStatement, and `ConditionArm`
    // implements neither, so the body of every `If` arm was invisible to it. The refusal never
    // fired and the program compiled: `check --native` reported no problems, the interpreter
    // printed 5 and the binary printed 0. That is the exact divergence the test above exists to
    // prevent, reachable by adding one line of nesting.
    //
    // The walk MUST over-approximate. Missing a write ships a divergence; an extra refusal only
    // costs a clean error, which is why it now descends into everything in the AST namespace.
    // Found by auditing for the same hole after the Linux CI job exposed it in CollectRefsDefs.
    [Fact]
    public void TaskCapture_MutatingInsideAnIfArm_IsRefused()
    {
        const string src = """
            Define tally as 0.
            Pull a rabbit.
                Have rabbit start a task as bump:
                    If 1 is 1:
                        tally becomes tally + 5.
                    Done.
                    return 1.
                Done.
                State the awaited result of bump.
            Done.
            State tally.
            """;
        var ex = Assert.Throws<CompilerException>(() => Compile(src));
        Assert.Contains("captured from outside the task", ex.Message);
    }

    [Fact]
    public void TaskCapture_MutatingInsideAJudgeArm_IsRefused()
    {
        // `JudgeArm` had the identical hole, and the namespace-keyed descend closes both.
        const string src = """
            Define tally as 0.
            Pull a rabbit.
                Define the (number or text) subject as 7.
                Have rabbit start a task as bump:
                    Judge subject, where it is:
                        A number, tally becomes tally + 5.
                        A text, tally becomes tally + 1.
                    Done.
                    return 1.
                Done.
                State the awaited result of bump.
            Done.
            State tally.
            """;
        var ex = Assert.Throws<CompilerException>(() => Compile(src));
        Assert.Contains("captured from outside the task", ex.Message);
    }

    // ★ The same write, with the one thing that made it a divergence removed: nobody reads `tally`
    // afterwards. Interpreted, the write lands on the enclosing binding; compiled, on the task's own
    // copy — and with nothing ever looking at either, the two programs print the same thing. So it
    // compiles, and says so rather than refusing. This is the whole point of the severity split: the
    // refusal was never about the write, it was about somebody seeing it.
    [Fact]
    public void TaskCapture_DeadWrite_WarnsAndMatchesInterpreter()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;   // needs pthreads
        const string src = """
            Define tally as 0.
            Pull a rabbit.
                Have rabbit start a task as bump:
                    tally becomes tally + 5.
                    return 1.
                Done.
                State the awaited result of bump.
            Done.
            State "done".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void TaskCapture_DeadWrite_IsReportedAsAWarning()
    {
        const string src = """
            Define tally as 0.
            Pull a rabbit.
                Have rabbit start a task:
                    tally becomes tally + 5.
                Done.
            Done.
            State "done".
            """;
        var tokens  = new CufetLexer(src).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);

        var generator = new CodeGenerator();
        generator.Generate(program);

        var only = Assert.Single(generator.Diagnostics.Items);
        Assert.Equal(DiagnosticSeverity.Warning, only.Severity);
        Assert.Contains("is discarded", only.Message);
    }

    // The read that brings the refusal back, in each of the two places it can hide. Both are the
    // over-approximation earning its keep: a sibling task and a statement after the rabbit are the
    // shapes where the interpreted and compiled answers actually come apart.
    [Fact]
    public void TaskCapture_WriteReadByASiblingTask_IsStillRefused()
    {
        const string src = """
            Define tally as 0.
            Pull a rabbit.
                Have rabbit start a task:
                    tally becomes tally + 5.
                Done.
                Have rabbit start a task:
                    State tally.
                Done.
            Done.
            """;
        var ex = Assert.Throws<CompilerException>(() => Compile(src));
        Assert.Contains("captured from outside the task", ex.Message);
    }

    [Fact]
    public void TaskCapture_WriteReadAfterTheRabbit_IsStillRefused()
    {
        const string src = """
            Define tally as 0.
            Pull a rabbit.
                Have rabbit start a task:
                    tally becomes tally + 5.
                Done.
            Done.
            State tally.
            """;
        var ex = Assert.Throws<CompilerException>(() => Compile(src));
        Assert.Contains("captured from outside the task", ex.Message);
    }

    // The guard is about WRITING to a capture, not about captures being unusable: a task reads a
    // captured number freely, and a counter it defines ITSELF is a local, not a capture.
    [Fact]
    public void TaskCapture_ReadingANumberAndCountingLocally_StillWorks()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;
        const string src = """
            Define step as 5.
            Pull a rabbit.
                Have rabbit start a task as run-it:
                    Define total as 0.
                    Define i as 0.
                    While i is less than 4, repeat:
                        total becomes total + step.
                        i becomes i + 1.
                    Done.
                    return total.
                Done.
                State the awaited result of run-it.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // Mutation reached through a CALL is refused too — argument binding shares series and maps with
    // the callee, so a callee can mutate through its parameter.
    [Fact]
    public void TaskCapture_MutationThroughACall_IsRefused()
    {
        const string src = """
            Bind void to stuff, given (the series of number s):
                Add 99 to s.
            Done.

            Define data as a series of number with (1, 2, 3).
            Pull a rabbit.
                Have rabbit start a task:
                    Cast stuff on (data).
                Done.
            Done.
            State data.
            """;
        var ex = Assert.Throws<CompilerException>(() => Compile(src));
        Assert.Contains("captured from outside the task", ex.Message);
    }

    // ── ESC.3b — two more escape holes, found by ASan-sweeping every example ───────────────────
    // examples/parallelsum.cufe was a heap-use-after-free. The escape annotation drives two
    // different things — deep-copying the stored VALUE, and redirecting the destination
    // CONTAINER's own growth — but was gated on the value being region-bearing, which only the
    // first needs. A `series of number` living outside a rabbit and appended to inside one had its
    // data buffer reallocated into the rabbit's arena, so it dangled at `Done.` despite every
    // element being a plain value. The loop counts here must exceed the initial capacity (8) so
    // the append actually reallocates — that is the whole bug.

    [Fact]
    public void Esc3_PodSeriesGrownInsideRabbit_SurvivesDone()
    {
        const string src = """
            Define outer as a series of number with ().
            Pull a rabbit.
                Define i as 0.
                While i is less than 12, repeat:
                    Add i to outer.
                    i becomes i + 1.
                Done.
            Done.
            State outer.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Esc3_PodSeriesPrependedInsideRabbit_SurvivesDone()
    {
        const string src = """
            Define s as a series of number with (99).
            Pull a rabbit.
                Define i as 0.
                While i is less than 12, repeat:
                    Add i to the start of s.
                    i becomes i + 1.
                Done.
            Done.
            State s.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Esc3_PodMapGrownInsideRabbit_SurvivesDone()
    {
        const string src = """
            Define counts as a map from number to number with ().
            Pull a rabbit.
                Define i as 0.
                While i is less than 12, repeat:
                    In counts, the entry for i becomes i * 2.
                    i becomes i + 1.
                Done.
            Done.
            State the size of counts.
            State counts.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // A map is the only store in the language with TWO escaping operands, and the key half had no
    // annotation at all — a text key built inside a rabbit and put into a longer-lived map was
    // freed at `Done.` while the map still held it.
    [Fact]
    public void Esc3_ArenaTextMapKeyStoredOutward_SurvivesDone()
    {
        const string src = """
            Define store as a map from text to number with ().
            Pull a rabbit.
                Define i as 0.
                While i is less than 12, repeat:
                    Define k as "key" joined to (i converted to text).
                    In store, the entry for k becomes i.
                    i becomes i + 1.
                Done.
            Done.
            State the size of store.
            State store.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── ESC.3 — nonlocal exits out of a rabbit unwind its arena ────────────────────────────────
    // Jumping out of a `Pull a rabbit` used to skip the rabbit's `Done.` arena pop entirely. That
    // was not merely a leak: cufet_arena_top was left one level too high, so it climbed by one on
    // every such exit and, past CUFET_ARENA_MAX_DEPTH (64), the next push wrote off the end of the
    // arena array. Each of the four exits below crashed with a SEGV at ~64 iterations before the
    // fix; all four run to 100 here, so the loop count is load-bearing — do not lower it.

    [Fact]
    public void Esc3_ReturnOutOfRabbit_DoesNotDriftArenaDepth()
    {
        const string src = """
            Bind series of number to build:
                Pull a rabbit.
                    Define inner as a series of number with (1, 2, 3).
                    return inner.
                Done.
                return a series of number with ().
            Done.

            Define i as 1.
            Define total as 0.
            While i is less than 100, repeat:
                Define r as Cast build on ().
                total becomes total + the number of r.
                i becomes i + 1.
            Done.
            State total.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Esc3_SkipOutOfRabbit_DoesNotDriftArenaDepth()
    {
        const string src = """
            Define i as 0.
            Define t as 0.
            While i is less than 100, repeat:
                i becomes i + 1.
                Pull a rabbit.
                    Define s as a series of number with (1, 2).
                    t becomes t + the number of s.
                    If i is greater than 0, Skip.
                Done.
            Done.
            State t.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Esc3_FailureGotoOutOfRabbit_DoesNotDriftArenaDepth()
    {
        const string src = """
            Bind text or failure to f:
                return a failure "x" of category "y".
            Done.

            Define i as 0.
            While i is less than 100, repeat:
                i becomes i + 1.
                Try to:
                    Pull a rabbit.
                        Define v as Cast f on ().
                        State v.
                    Done.
                Done.
                In case of failure:
                    Define ignored as 1.
                Done.
            Done.
            State "done".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Esc3_PropagateOutOfRabbit_DoesNotDriftArenaDepth()
    {
        const string src = """
            Bind text or failure to inner:
                return a failure "x" of category "y".
            Done.

            Bind text or failure to outer:
                Pull a rabbit.
                    Define v as Cast inner on () or pass the failure off.
                    return v.
                Done.
                return "".
            Done.

            Define i as 0.
            While i is less than 100, repeat:
                i becomes i + 1.
                Try to:
                    Define r as Cast outer on ().
                    State r.
                Done.
                In case of failure:
                    Define ig as 1.
                Done.
            Done.
            State "done".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // The failure-goto is emitted from four places, not one — a bare fallible call, a bare pipe
    // statement, a failed file write, and a failed `With … open`. Only the first was wired when
    // arenas joined the cleanup family; these two cover the other reachable ones. (They assert on a
    // count rather than the failure message, so the OS's error text never enters the comparison.)
    [Fact]
    public void Esc3_WithOpenFailureOutOfRabbit_DoesNotDriftArenaDepth()
    {
        const string src = """
            Define i as 0.
            While i is less than 100, repeat:
                i becomes i + 1.
                Try to:
                    Pull a rabbit.
                        Define p as "no-such-dir-esc3/f" joined to ".txt".
                        With the file p open for reading as s:
                            Define line as read a line from s.
                        Done.
                    Done.
                Done.
                In case of failure:
                    Define ig as 1.
                Done.
            Done.
            State "done".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Esc3_FileWriteFailureOutOfRabbit_DoesNotDriftArenaDepth()
    {
        const string src = """
            Define i as 0.
            While i is less than 100, repeat:
                i becomes i + 1.
                Try to:
                    Pull a rabbit.
                        Define p as "no-such-dir-esc3/f" joined to ".txt".
                        Write "hello" to the file p.
                    Done.
                Done.
                In case of failure:
                    Define ig as 1.
                Done.
            Done.
            State "done".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // `Suppress` jumps to the end of its exception handler, so a rabbit opened inside the handler
    // is jumped out of exactly the way `Stop` jumps out of a loop.
    [Fact]
    public void Esc3_SuppressInsideRabbitInHandler_DoesNotDriftArenaDepth()
    {
        const string src = """
            Define i as 0.
            While i is less than 100, repeat:
                i becomes i + 1.
                Try to:
                    Define z as 0.
                    Define bad as 5 / z.
                    State bad.
                Done.
                In case of exception (the exception):
                    Pull a rabbit.
                        Define note as "caught" joined to " it".
                        Suppress the exception.
                    Done.
                Done.
            Done.
            State "done".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ★ The reason a return MERGES its rabbit's arena outward instead of copying the value into the
    // caller's: the returned value may be the caller's own, and the interpreter shares it. Copying
    // would make `r` a distinct series and print (1, 2) here — a divergence, not an optimization.
    [Fact]
    public void Esc3_ReturnedValueAliasingTheCaller_IsNotCopied()
    {
        const string src = """
            Bind series of number to passthru, given (the series of number s):
                Pull a rabbit.
                    Define ignored as 1.
                    return s.
                Done.
                return a series of number with ().
            Done.

            Define outer as a series of number with (1, 2).
            Define r as Cast passthru on (outer).
            Add 9 to r.
            State outer.
            State r.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // An I/O failure's message is arena-templated (cufet_arena_msg), so both the goto-to-handler and
    // the propagate-out-of-frame paths must move it outward before unwinding the rabbit it was
    // built in — otherwise the handler reads freed memory. (The EXCMSG fix, on the failure path.)
    [Fact]
    public void Esc3_ArenaTemplatedFailureMessage_SurvivesRabbitUnwind()
    {
        const string src = """
            Bind text or failure to load:
                Pull a rabbit.
                    Define name as "no-such-file-esc3" joined to ".txt".
                    Define v as read all from the file name or pass the failure off.
                    return v.
                Done.
                return "unreached".
            Done.

            Try to:
                Pull a rabbit.
                    Define direct as "no-such-file-esc3-b" joined to ".txt".
                    Define d as read all from the file direct.
                    State d.
                Done.
            Done.
            In case of failure:
                State the message of the failure.
            Done.

            Try to:
                Define r as Cast load on ().
                State r.
            Done.
            In case of failure:
                State the message of the failure.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // Reclamation, not just non-crashing: 20,000 iterations each building a 200-element series
    // inside a rabbit and jumping out of it. Peak RSS stays ~1.7 MB (measured in WSL), so the
    // regions really are being released. LSan cannot see this class — the arena's pointer list is
    // a live global root, so its blocks are "still reachable" — hence the oracle-match plus the
    // manual RSS check rather than a sanitizer assertion.
    [Fact]
    public void Esc3_RepeatedReturnOutOfRabbit_ReclaimsMemory()
    {
        const string src = """
            Bind number to work:
                Pull a rabbit.
                    Define s as a series of number with ().
                    Define j as 0.
                    While j is less than 200, repeat:
                        Add j to s.
                        j becomes j + 1.
                    Done.
                    return the number of s.
                Done.
                return 0.
            Done.

            Define i as 0.
            Define t as 0.
            While i is less than 20000, repeat:
                t becomes t + Cast work on ().
                i becomes i + 1.
            Done.
            State t.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Esc3_ReturnOutOfRabbitInsideTask_DoesNotDriftArenaDepth()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // A task body is its own frame with its own thread-local arena stack, so it has the same
        // exit paths — and its result is heap-bridged before the teardown, so the rabbits it
        // returns out of are genuinely reclaimed rather than merged.
        const string src = """
            Pull a rabbit.
                Have rabbit start a task as worker:
                    Pull a rabbit.
                        Define s as a series of number with (1, 2, 3, 4).
                        return the number of s.
                    Done.
                    return 0.
                Done.
                Define r as the awaited result of worker.
                State r.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── When the compiler emits C that will not build ────────────────────
    //
    // ★ `cufet check --native` reports what the code generator REFUSES, and that is only as good
    // as the generator refusing. It can also emit invalid C and return normally, in which case the
    // check reports the program clean and gcc fails at build time — which is what the Judge
    // grouped-arm bug did. Two things narrow that gap: the generator refuses instead of guessing
    // (EmitMemberAccess has no catch-all any more), and a gcc failure is reported as what it is.

    [Fact]
    public void GccFailureOnGeneratedC_IsReportedAsACompilerBug()
    {
        // Everything gcc reads was written by cufet, so an error inside that file is never the
        // author's to fix. Standing in for a code-generator defect with C that cannot compile.
        var cPath = Path.GetTempFileName() + ".c";
        var binPath = Path.GetTempFileName() + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "");
        try
        {
            File.WriteAllText(cPath, "int main(void) { struct { int a; } s; return s.nope; }\n");
            var e = Assert.Throws<CompilerException>(() => new GccInvoker().Compile(cPath, binPath));

            Assert.Contains("bug in the Cufet compiler", e.Message);
            Assert.Contains("not in your program", e.Message);
            Assert.Contains("emit-c", e.Message);          // how to get the C to report it with
            Assert.Contains("nope", e.Message);            // gcc's own words are still there
        }
        finally
        {
            try { File.Delete(cPath); } catch { }
            try { File.Delete(binPath); } catch { }
        }
    }
}
