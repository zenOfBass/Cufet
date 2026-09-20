using Cufet.Interpreter;
using Xunit;

namespace Cufet.Compiler.Tests;

/// <summary>
/// `Have rabbit start a task, repeat:` — a task whose body is a loop, and whose fault costs one
/// turn instead of the program.
/// </summary>
/// <remarks>
/// <para>
/// ★★ WHAT THIS CLOSES. A task that raised with nobody awaiting it tore the whole program down:
/// the fault reached the rabbit's join, the process exited 1, and statements after the rabbit
/// never ran. "Let it crash" is exactly the inversion of that, and
/// <see cref="AFaultEveryTurn_CostsTheTurnNotTheProgram"/> is the pin — the same division by zero,
/// the same place, and now the program finishes.
/// </para>
/// <para>
/// ★ The SPELLING reuses `repeat`, which the language had already spent on `While …, repeat:` and
/// `For each …, repeat:`, so a task that loops reads like every other loop and costs no new word.
/// ⚠ It was not free: `Have rabbit start a task, Repeat: … Until x.` used to parse as the inline
/// one-statement body form, and the two readings differ ONLY in what closes the body (`Done.` vs
/// `Until`) — no bounded lookahead separates them. The grammar gives the spelling to the repeating
/// task; <see cref="TheOldInlineRepeatUntilReading_IsRefusedWithTheRewrite"/> pins that the refusal
/// hands back the replacement rather than just failing.
/// </para>
/// <para>
/// ⚠⚠ WHY THE EXIT IS IN THE BODY. "Loops until the rabbit closes" was the first reading and it
/// cannot cross the two backends: compiled tasks are real pthreads, so the iteration count would be
/// whatever the threads reached before `Done.`, while the interpreter is cooperative, so a loop
/// with no yield point never returns control and the rabbit's `Done.` is never reached at all — a
/// nondeterministic count on one side and a hang on the other. The rabbit still bounds the task's
/// LIFETIME; it does not decide the count. So the exit is `Stop`, in the body.
/// </para>
/// <para>
/// ★ A consequence worth stating, because it shapes every test here: the task-capture refusal means
/// a repeating task cannot keep a counter in an enclosing variable, so its progress AND its exit
/// arrive as messages. A repeating task is the actor loop, and a channel is how you stop one.
/// </para>
/// </remarks>
public class PipelineRepeatingTaskTests : PipelineTestBase
{
    [Fact]
    public void ARepeatingTask_TakesATurnPerMessage_AndStopsWhenTold()
    {
        const string src = """
            Pull a rabbit.
                Define jobs as a channel of number.
                Have rabbit start a task, repeat:
                    Define arrival as the delivery from jobs.
                    If arrival is void, stop.
                    State "got {arrival but void is 0}".
                Done.
                Send 1 through jobs.
                Send 2 through jobs.
                Close jobs.
            Done.
            State "after".
            """;
        Assert.Equal("got 1\ngot 2\nafter\n".ReplaceLineEndings(), InterpretRaw(src).ReplaceLineEndings());
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void AFaultEveryTurn_CostsTheTurnNotTheProgram()
    {
        // ★★ THE HEADLINE. `1 / 0` on every single turn. The turn dies, the next one begins, and
        // the program reaches `after` and exits 0 — on BOTH backends.
        const string src = """
            Pull a rabbit.
                Define jobs as a channel of number.
                Have rabbit start a task, repeat:
                    Define arrival as the delivery from jobs.
                    If arrival is void, stop.
                    State "before {arrival but void is 0}".
                    Define bad as 1 / 0.
                    State "unreachable".
                Done.
                Send 1 through jobs.
                Send 2 through jobs.
                Close jobs.
            Done.
            State "after".
            """;
        Assert.Equal("before 1\nbefore 2\nafter\n".ReplaceLineEndings(), InterpretRaw(src).ReplaceLineEndings());
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void AnOrdinaryTasksFault_STILL_EndsTheProgram()
    {
        // ★ The CONTROL, and the reason the test above means anything. Identical fault, ordinary
        // task: the program dies at the rabbit's join and never reaches `after`. If this ever goes
        // green with `after` in it, the repeating path has leaked into ordinary tasks.
        const string src = """
            Pull a rabbit.
                Have rabbit start a task:
                    Define bad as 1 / 0.
                Done.
            Done.
            State "after".
            """;
        AssertFaultOracle(src);
        Assert.DoesNotContain("after", InterpretThroughFaultRaw(src));
    }

    [Fact]
    public void Skip_StartsTheNextTurn()
    {
        const string src = """
            Pull a rabbit.
                Define jobs as a channel of number.
                Have rabbit start a task, repeat:
                    Define arrival as the delivery from jobs.
                    If arrival is void, stop.
                    Define got as arrival but void is 0.
                    If got is 2, skip.
                    State "got {got}".
                Done.
                Send 1 through jobs.
                Send 2 through jobs.
                Send 3 through jobs.
                Close jobs.
            Done.
            State "after".
            """;
        Assert.Equal("got 1\ngot 3\nafter\n".ReplaceLineEndings(), InterpretRaw(src).ReplaceLineEndings());
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void StopIsOnlyALoopWord_SoAnOrdinaryTaskStillRefusesIt()
    {
        // ★ `Stop` is gated on the parser's loop depth. A repeating task raises it — which is the
        // whole reason `Stop` and `Skip` needed no rule of their own — so an ORDINARY task must
        // still refuse the word, or the gate has been widened by accident.
        const string src = """
            Pull a rabbit.
                Have rabbit start a task:
                    Stop.
                Done.
            Done.
            """;
        var ex = Assert.Throws<ParseException>(() => InterpretRaw(src));
        Assert.Contains("outside a loop", ex.Message);
    }

    [Fact]
    public void TheOldInlineRepeatUntilReading_IsRefusedWithTheRewrite()
    {
        // ⚠ This shape was LEGAL before `, repeat:` existed — an inline one-statement task body
        // that happened to be a Repeat-Until loop. Taking the spelling has to cost a message that
        // says how to write it now, not just "expected statement keyword, got Until".
        const string src = """
            Pull a rabbit.
                Have rabbit start a task, Repeat: State "tick". Until 1 is 1.
            Done.
            """;
        var ex = Assert.Throws<ParseException>(() => InterpretRaw(src));
        Assert.Contains("closes with 'Done.' rather than 'Until'", ex.Message);
        Assert.Contains("Have rabbit start a task: Repeat: ... Until x. Done.", ex.Message);
    }

    [Fact]
    public void TheRewriteTheRefusalRecommends_ActuallyWorks()
    {
        // ★ A refusal that recommends a rewrite is a promise. This is the promise being kept —
        // the exact text the message prints, run on both backends.
        const string src = """
            Pull a rabbit.
                Have rabbit start a task:
                    Define seen as 0.
                    Repeat:
                        The seen becomes seen + 1.
                        State "tick {seen}".
                    Until seen is 2.
                Done.
            Done.
            State "after".
            """;
        Assert.Equal("tick 1\ntick 2\nafter\n".ReplaceLineEndings(), InterpretRaw(src).ReplaceLineEndings());
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void APerTurnObject_IsUnmadeEveryTurn_NotOnceAtTheEnd()
    {
        // ★★ The turn is a SCOPE, on both backends. Interpreted that is EnterScope/ExitScope per
        // turn; compiled it is EmitLoopBody's per-iteration unmaker snapshot. Get either wrong and
        // the closes bunch up at the end instead of interleaving — which is what this pins.
        const string src = """
            Define object gate with (the number id).
            Bind unmaking a gate to close-gate, State "closed {one's id}".

            Pull a rabbit.
                Define jobs as a channel of number.
                Have rabbit start a task, repeat:
                    Define arrival as the delivery from jobs.
                    If arrival is void, stop.
                    Define g as a new gate { the id arrival but void is 0 }.
                    State "open {g's id}".
                Done.
                Send 1 through jobs.
                Send 2 through jobs.
                Close jobs.
            Done.
            State "after".
            """;
        Assert.Equal("open 1\nclosed 1\nopen 2\nclosed 2\nafter\n".ReplaceLineEndings(),
                     InterpretRaw(src).ReplaceLineEndings());
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void SkipMidTurn_StillUnmakesWhatTheTurnHadMade()
    {
        // ★★ This is what EmitLoopBody actually buys, and the test above did NOT catch it:
        // `Skip` compiles to `UnwindTo(LoopExit)continue;`, and with no loop-exit mark pushed
        // UnmakerRunStmt(null) emits the EMPTY STRING — so a Skip would jump straight to the next
        // turn leaving the turn's objects unmade, while the interpreter's ExitScope unmakes them.
        // MEASURED: swapping EmitLoopBody for EmitScopedBlock leaves every other test in this file
        // green, and turns `closed 1` compiled into nothing at all.
        const string src = """
            Define object gate with (the number id).
            Bind unmaking a gate to close-gate, State "closed {one's id}".

            Pull a rabbit.
                Define jobs as a channel of number.
                Have rabbit start a task, repeat:
                    Define arrival as the delivery from jobs.
                    If arrival is void, stop.
                    Define got as arrival but void is 0.
                    Define g as a new gate { the id got }.
                    State "open {got}".
                    If got is 1, skip.
                    State "end {got}".
                Done.
                Send 1 through jobs.
                Send 2 through jobs.
                Close jobs.
            Done.
            State "after".
            """;
        Assert.Equal("open 1\nclosed 1\nopen 2\nend 2\nclosed 2\nafter\n".ReplaceLineEndings(),
                     InterpretRaw(src).ReplaceLineEndings());
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void ManyFaultingTurns_DoNotDriftTheArenaDepth()
    {
        // ⚠ A faulting turn longjmps past every emit-time arena pop, so the turn's recovery has to
        // unwind them itself. Nothing PRINTS differently when it does not — the drift is invisible
        // until it passes CUFET_ARENA_MAX_DEPTH (64), which the runtime's own note calls an
        // out-of-bounds write. So the test buys enough turns to cross it: 70 faults, each inside
        // its own region, and the program still has to come out the other side.
        const string src = """
            Define object workspace with () and region.

            Pull a rabbit.
                Define jobs as a channel of number.
                Have rabbit start a task, repeat:
                    Define arrival as the delivery from jobs.
                    If arrival is void, stop.
                    Pull workspace as bench.
                        Define bad as 1 / 0.
                    Done.
                Done.
                For each n in range 1 to 70, repeat:
                    Send n through jobs.
                Done.
                Close jobs.
            Done.
            State "survived".
            """;
        Assert.Equal("survived\n".ReplaceLineEndings(), InterpretRaw(src).ReplaceLineEndings());
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void ARepeatingTask_RefusesACapturedResource()
    {
        // ★ "A restartable body acquires its own resources." A capture is materialised ONCE,
        // before the first turn, so a captured resource is shared by every turn — and a turn
        // abandoned mid-use hands the next one whatever it left behind.
        const string src = """
            Define object gate with (the number id).
            Bind unmaking a gate to close-gate, State "closed {one's id}".

            Pull a rabbit.
                Define jobs as a channel of number.
                Define shared as a new gate { the id 1 }.
                Have rabbit start a task, repeat:
                    Define arrival as the delivery from jobs.
                    If arrival is void, stop.
                    State "{shared's id}".
                Done.
                Close jobs.
            Done.
            """;
        var ex = Assert.Throws<TypeException>(() => InterpretRaw(src));
        Assert.Contains("captures 'shared'", ex.Message);
        Assert.Contains("unmade when it goes out of scope", ex.Message);
    }

    [Fact]
    public void AnOrdinaryTask_MayStillCaptureAResource()
    {
        // ⚠ The rule is narrow ON PURPOSE — it is about a body that runs MORE THAN ONCE. An
        // ordinary task capturing the same object is untouched, and if this ever goes red the
        // refusal has escaped the case that justifies it.
        //
        // ⚠⚠ ACCEPTANCE ONLY, and deliberately not an oracle assertion. Writing this as
        // `Assert.Equal(InterpretRaw(src), CompileRaw(src))` FAILS, on a divergence that has
        // nothing to do with repeating tasks: an ordinary task capturing an object that has an
        // unmaker prints `saw 7 / closed 7` interpreted and `closed 7 / saw 7` compiled. MEASURED
        // 2026-09-20 — deterministic over six runs, so not an interleaving, and reproduced on the
        // installed 0.23.0 build, so it predates this work. Asserting only acceptance here keeps
        // that bug from being silently absorbed into this file; it wants its own fix.
        const string src = """
            Define object gate with (the number id).
            Bind unmaking a gate to close-gate, State "closed {one's id}".

            Pull a rabbit.
                Define shared as a new gate { the id 7 }.
                Have rabbit start a task:
                    State "saw {shared's id}".
                Done.
            Done.
            State "after".
            """;
        Assert.Contains("saw 7", InterpretRaw(src));
        Assert.Contains("saw 7", CompileRaw(src));
    }
}
