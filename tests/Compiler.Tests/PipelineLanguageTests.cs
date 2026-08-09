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
public class PipelineLanguageTests : PipelineTestBase
{

    [Fact]
    public void Overload_Fallible_ComposesWithTryAndButOnFailure()
    {
        // An overload whose body returns a failure makes the OPERATOR fallible (`T or failure`,
        // strict-fallible rule) — the same shape as matrix arithmetic, so it routes through the
        // existing fallible machinery: check-goto inside a Try, and `but on failure`.
        const string src = """
            Define object money with (the number cents).
            Bind overloading /, given (the lhs is a money, the rhs is a money):
                If rhs's cents is 0:
                    Return a failure "cannot divide by zero money" of category "math".
                Done.
                Return a new money { the cents lhs's cents / rhs's cents }.
            Done.
            Define big as a new money { the cents 100 }.
            Define small as a new money { the cents 5 }.
            Define zero as a new money { the cents 0 }.
            Try to:
                Define ok as big / small.
                State ok.
                Define bad as big / zero.
                State bad.
            Done.
            In case of failure:
                State "failed: " joined to the message of the failure.
            Done.
            Define fallback as (big / zero) but on failure (a new money { the cents -1 }).
            State fallback.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Overload_OnTypeWithReferenceField_FromMethodBodyAndFunction()
    {
        // The overload allocates a new arena series and its operand type holds a series field;
        // it is invoked from inside a METHOD body (`one + other`) and from an ordinary function.
        const string src = """
            Define object basket with (the text label, the series of text items):
                Bind basket to merge, given (the basket other):
                    Return one + other.
                Done.
            Done.
            Bind overloading +, given (the lhs is a basket, the rhs is a basket):
                Define merged as a series of text with ().
                For each i in lhs's items, repeat:
                    Add i to merged.
                Done.
                For each j in rhs's items, repeat:
                    Add j to merged.
                Done.
                Return a new basket { the label lhs's label joined to "+" joined to rhs's label, the items merged }.
            Done.
            Bind basket to combine, given (the basket p, the basket q):
                Return p + q.
            Done.
            Define one-b as a new basket { the label "a", the items (a series of text with ("x")) }.
            Define two-b as a new basket { the label "b", the items (a series of text with ("y", "z")) }.
            Define sum-b as one-b + two-b.
            State sum-b's label.
            State the number of sum-b's items.
            State sum-b's items.
            Define viaMethod as cast one-b's merge on (two-b).
            State viaMethod's label.
            Define fromFn as cast combine on (one-b, two-b).
            State fromFn's label.
            State the number of fromFn's items.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Overload_UsedInsideATask_CrossesChannelAndTaskResult()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // An overload called from INSIDE a task body, with the result crossing both a channel and
        // the task-result bridge. This is what caught the forward-declaration ordering bug: the
        // generated task thread functions used to be emitted BEFORE the function forward decls,
        // so any call out of a task body was an implicit declaration.
        const string src = """
            Define object vec2 with (the number x, the number y).
            Bind overloading +, given (the lhs is a vec2, the rhs is a vec2):
                Return a new vec2 { the x lhs's x + rhs's x, the y lhs's y + rhs's y }.
            Done.
            Pull a rabbit.
                Define ch as a channel of vec2.
                Have rabbit start a task as summer:
                    Define a1 as a new vec2 { the x 1, the y 2 }.
                    Define b1 as a new vec2 { the x 30, the y 40 }.
                    Define s as a1 + b1.
                    Send s through ch.
                    Return s + s.
                Done.
                Define got as the delivery from ch.
                If got is not void:
                    State got's x.
                    State got's y.
                Done.
                Define doubled as the awaited result of summer.
                State doubled's x.
                State doubled's y.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Task_CallsAFreeFunction_ForwardDeclaredBeforeTaskBodies()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // The general form of the ordering hole the overload slice uncovered: generated task thread
        // functions used to be emitted BEFORE the free-function forward declarations, so ANY call
        // out of a task body was an implicit declaration (a gcc error). No test covered it.
        const string src = """
            Bind number to triple, given (the number n):
                Return n * 3.
            Done.
            Pull a rabbit.
                Define ch as a channel of number.
                Have rabbit start a task:
                    Send cast triple on (7) through ch.
                Done.
                Define d as the delivery from ch.
                If d is not void, state d.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── Arc 3: INTERFACES (DD.1 — monomorphization) ──────────────────────────
    // MEASURED + design-locked: interface polymorphism exists only at the FUNCTION PARAMETER, and
    // the argument is a CONCRETE conformer at the call site (not stored/returned/forwarded — all
    // front-end-rejected). So the concrete type is statically known everywhere ⇒ emit one
    // specialized copy of each interface-taking callable per conformer passed. Inside a
    // specialization the parameter is concrete → existing direct dispatch, `is a T` constant-folds.
    // No runtime type tags. No vtables.

    [Fact]
    public void Interface_DispatchPicksConcreteType_PerSpecialization()
    {
        // The measured baseline: announce(dog) → Woof, announce(cat) → Meow. Dispatch resolves to
        // the concrete type's method inside each specialization; an interface param with an extra
        // ordinary arg composes (→ 30).
        const string src = """
            Define speaker as an interface for the text function speak.
            Define object dog with (the text name) and speaker:
                Bind text to speak:
                    Return "Woof".
                Done.
            Done.
            Define object cat with (the text name) and speaker:
                Bind text to speak:
                    Return "Meow".
                Done.
            Done.
            Bind void to announce, given (the speaker s):
                State cast s's speak on ().
            Done.
            Bind number to loudness, given (the speaker s, the number base):
                Return base + the length of (cast s's speak on ()).
            Done.
            Define d as a new dog { the name "Rex" }.
            Define c as a new cat { the name "Tom" }.
            Cast announce on (d).
            Cast announce on (c).
            State cast loudness on (d, 26).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Interface_IsATypeCheck_FoldsToCompileTimeConstant_AndMultiParamCombinations()
    {
        // `s is a dog` on an interface param is a compile-time constant inside a specialization
        // (StaticKindMatches — no tag). Multi-method interface. Two interface params specialize per
        // COMBINATION (dog_dog, dog_cat, cat_cat). A closure capturing the interface param sees it
        // at the concrete type.
        const string src = """
            Define speaker as an interface for { the text function speak, the number function volume }.
            Define object dog with (the text name) and speaker:
                Bind text to speak:
                    Return "Woof".
                Done.
                Bind number to volume:
                    Return 9.
                Done.
            Done.
            Define object cat with (the text name) and speaker:
                Bind text to speak:
                    Return "Meow".
                Done.
                Bind number to volume:
                    Return 3.
                Done.
            Done.
            Bind text to describe, given (the speaker s):
                If s is a dog:
                    Return "it is a dog".
                Done.
                Return "not a dog".
            Done.
            Bind number to duet, given (the speaker a1, the speaker b1):
                Return cast a1's volume on () + cast b1's volume on ().
            Done.
            Bind number to repeat-vol, given (the speaker s, the number k):
                Bind number to step, given (the number j):
                    If j is 0, Return 0.
                    Return 1 + cast step on (j - 1).
                Done.
                Return (cast s's volume on ()) * (cast step on (k)).
            Done.
            Define d as a new dog { the name "Rex" }.
            Define c as a new cat { the name "Tom" }.
            State cast describe on (d).
            State cast describe on (c).
            State cast duet on (d, c).
            State cast duet on (d, d).
            State cast duet on (c, c).
            State cast repeat-vol on (d, 3).
            State cast repeat-vol on (c, 2).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Interface_MethodTakingInterfaceParam_AndClosureCapture()
    {
        // An interface-taking METHOD (a megaphone boosting a speaker) specializes per conformer, and
        // a closure inside an interface-taking function captures the param at its concrete type.
        const string src = """
            Define speaker as an interface for the text function speak.
            Define object dog with (the text name) and speaker:
                Bind text to speak:
                    Return "Woof".
                Done.
            Done.
            Define object cat with (the text name) and speaker:
                Bind text to speak:
                    Return "Meow".
                Done.
            Done.
            Define object megaphone with (the text tag):
                Bind text to boost, given (the speaker s):
                    Return one's tag joined to ": " joined to (cast s's speak on ()).
                Done.
            Done.
            Bind text to viaClosure, given (the speaker s):
                Define shout as a function:
                    Return (cast s's speak on ()) joined to "!!!".
                Done.
                Return cast shout on ().
            Done.
            Define d as a new dog { the name "Rex" }.
            Define c as a new cat { the name "Tom" }.
            Define m as a new megaphone { the tag "LOUD" }.
            State cast m's boost on (d).
            State cast m's boost on (c).
            State cast viaClosure on (d).
            State cast viaClosure on (c).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Interface_TakingFunctionCalledFromATask()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // An interface-taking function called from inside a task body (specialization emitted for a
        // concrete conformer the task constructs), result crossing the task-result bridge. Two tasks
        // awaited in order → deterministic, so this is a genuine Compile==Interpret oracle test.
        const string src = """
            Define speaker as an interface for the text function speak.
            Define object dog with (the text name) and speaker:
                Bind text to speak:
                    Return "Woof".
                Done.
            Done.
            Define object cat with (the text name) and speaker:
                Bind text to speak:
                    Return "Meow".
                Done.
            Done.
            Bind text to announce, given (the speaker s):
                Return "heard: " joined to (cast s's speak on ()).
            Done.
            Pull a rabbit.
                Have rabbit start a task as t1:
                    Define c as a new cat { the name "Tom" }.
                    Return cast announce on (c).
                Done.
                Have rabbit start a task as t2:
                    Define d as a new dog { the name "Rex" }.
                    Return cast announce on (d).
                Done.
                Define r1 as the awaited result of t1.
                Define r2 as the awaited result of t2.
                State r1.
                State r2.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Unmaker_TopLevel_NeverFires_ButBlockFiresLIFO()
    {
        // Top-level Defines never fire (the global scope isn't a block); a block fires LIFO at exit.
        Assert.Equal(
            Interpret(UnmakerHdr + "Define first as a new handle { the id \"A\" }.\nState \"top done\"."),
            Compile(UnmakerHdr + "Define first as a new handle { the id \"A\" }.\nState \"top done\"."));
        const string block = UnmakerHdr + """
            Pull a rabbit.
                Define first as a new handle { the id "A" }.
                Define second as a new handle { the id "B" }.
                State "block body done".
            Done.
            State "after block".
            """;
        Assert.Equal(InterpretRaw(block), CompileRaw(block));
    }

    [Fact]
    public void Unmaker_FunctionFrame_NeverFires()
    {
        // A function-frame-local object never fires (the interpreter's SaveScopes/RestoreScopes
        // bypasses RunScopeUnmakers) — load-bearing for escape-via-return.
        const string src = UnmakerHdr + """
            Bind void to work:
                Define localh as a new handle { the id "L" }.
                State "fn body done".
            Done.
            Cast work.
            State "after fn".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Unmaker_Loop_FiresPerIteration_AndStopFires()
    {
        // foreach / while fire per-iteration at each iteration's block exit; Stop (a nonlocal exit)
        // fires the current iteration's unmakers before breaking.
        const string fe = UnmakerHdr + """
            For each n in the range 1 to 3, repeat:
                Define loopy as a new handle { the id "LOOP" }.
                If n is 2, Stop.
                State "iter".
            Done.
            State "after loop".
            """;
        Assert.Equal(InterpretRaw(fe), CompileRaw(fe));
    }

    [Fact]
    public void Unmaker_ValueCopy_DoubleFires()
    {
        // Objects are value types — `Define copy as orig` copies, so BOTH bindings fire (per-binding
        // hook, not ownership). This is the language, matched exactly (not deduped).
        const string src = UnmakerHdr + """
            Pull a rabbit.
                Define orig as a new handle { the id "ORIG" }.
                Define copy-of as orig.
                State "both defined".
            Done.
            State "after".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Unmaker_ContainerTemporaryField_DoNotIndependentlyFire()
    {
        // Only Define'd bindings fire: an object added to a series fires via its ORIGINAL binding
        // (the container copy never fires); a temporary never fires; a nested unmakeable field does
        // not recurse (only the container's own unmaker fires).
        const string container = UnmakerHdr + """
            Pull a rabbit.
                Define one-h as a new handle { the id "IN-SERIES" }.
                Define bag as a series with (one-h).
                State "added".
            Done.
            State "after".
            """;
        Assert.Equal(InterpretRaw(container), CompileRaw(container));
        const string temp = UnmakerHdr + """
            Pull a rabbit.
                State "id is " joined to (a new handle { the id "TEMP" })'s id.
            Done.
            """;
        Assert.Equal(InterpretRaw(temp), CompileRaw(temp));
        const string field = UnmakerHdr + """
            Define object wrapper with (the handle inner, the text tag).
            Bind unmaking a wrapper to dispose:
                State "unmake wrapper " joined to one's tag.
            Done.
            Pull a rabbit.
                Define wrap as a new wrapper { the inner (a new handle { the id "FIELD" }), the tag "W" }.
                State "made wrapper".
            Done.
            """;
        Assert.Equal(InterpretRaw(field), CompileRaw(field));
    }

    [Fact]
    public void Unmaker_Reassignment_FiresCurrentValueOnly()
    {
        // `becomes` doesn't register (only Define does) and fires the CURRENT value at block exit,
        // never the replaced one.
        const string src = UnmakerHdr + """
            Pull a rabbit.
                Define h as a new handle { the id "FIRST" }.
                h becomes a new handle { the id "SECOND" }.
                State "reassigned".
            Done.
            State "after".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Unmaker_ReturnThroughBlock_Fires()
    {
        // A `return` unwinds through the enclosing blocks in the frame, firing their unmakers (the
        // interpreter's block finallys), while the function frame itself doesn't fire.
        const string src = UnmakerHdr + """
            Bind number to work:
                Pull a rabbit.
                    Define blockh as a new handle { the id "RETURNED-THRU" }.
                    State "before return".
                    Return 42.
                Done.
            Done.
            State "result " joined to ((cast work) converted to text).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Unmaker_EscapeViaReturn_DoubleFires()
    {
        // ★ The sharp case: returning a block-local object fires its unmaker TWICE — once at the
        // inner block exit (the return unwinds through it, while the value is still being returned)
        // and once at the outer binding's block exit. Value semantics make this observable-but-safe;
        // matched exactly.
        const string src = UnmakerHdr + """
            Bind handle to make-in-block:
                Pull a rabbit.
                    Define blockh as a new handle { the id "ESCAPES-VIA-RETURN" }.
                    Return blockh.
                Done.
            Done.
            Pull a rabbit.
                Define got as cast make-in-block.
                State "got " joined to got's id.
            Done.
            State "done".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Unmaker_ExceptionUnwind_FiresBeforeHandler_SameAndCrossFrame()
    {
        // On an exception unwind, unmakers run (LIFO) BEFORE the handler — cufet_raise runs them
        // while their C-stack objects are still live, then longjmps. Cross-frame: a callee-frame
        // block's unmaker fires, then the outer block's.
        // ★ The inner handler's `Suppress` WORKAROUND IS GONE (it was there only for the E-prime
        // message-lifetime bug, now fixed): the exception RE-RAISES out of the inner handler and the
        // outer handler READS its message — proving the message survives both the inner catch's
        // arena pops and the re-raise, while the unmaker ordering is unchanged.
        const string src = UnmakerHdr + """
            Bind number to deep:
                Pull a rabbit.
                    Define fnblock as a new handle { the id "FN-BLOCK" }.
                    State "in fn block".
                    Return 1 / 0.
                Done.
            Done.
            Try to:
                Try to:
                    Pull a rabbit.
                        Define outerblock as a new handle { the id "OUTER-BLOCK" }.
                        State "before deep call".
                        Define r as cast deep.
                        State "after deep call".
                    Done.
                Done.
                In case of exception (the exception):
                    State "inner caught: " joined to the message of the exception.
                Done.
            Done.
            In case of exception (the exception):
                State "outer caught: " joined to the message of the exception.
                Suppress the exception.
            Done.
            State "after try".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Unmaker_FailurePropagate_FiresThroughBlock()
    {
        // `or pass the failure off` returns from the frame, firing the blocks it unwinds through.
        const string src = UnmakerHdr + """
            Bind number or failure to risky:
                Return a failure "boom" of category "test".
            Done.
            Bind number or failure to caller:
                Pull a rabbit.
                    Define held as a new handle { the id "HELD" }.
                    Define x as cast risky or pass the failure off.
                    Return x.
                Done.
            Done.
            Try to:
                Define r as cast caller.
                State "got " joined to (r converted to text).
            Done.
            In case of failure:
                State "failed: " joined to the message of the failure.
            Done.
            State "after".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Unmaker_InsideConcurrentTask_FiresOnItsOwnThread()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // The unmaker registry is thread-local, so a task-thread block's unmaker fires on that
        // thread (ASan/LSan/TSan clean — verified in WSL).
        const string src = UnmakerHdr + """
            Pull a rabbit.
                Have rabbit start a task as worker:
                    Pull a rabbit.
                        Define taskh as a new handle { the id "IN-TASK" }.
                        Return 7.
                    Done.
                Done.
                Define r as the awaited result of worker.
                State "result " joined to (r converted to text).
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── Refusal messages are user-facing ──────────────────────────────────────────────────────
    // A refusal is a fine place to be, but only if it says something true and actionable. Awaiting
    // inside a task used to report `'TaskHandleType' is not yet supported by the compiler (slice 5B:
    // records + objects + text)` — an internal class name, an internal slice number, and a list of
    // features that have nothing to do with it. These pin the messages a reader actually meets.

    // Was Refusal_AwaitInsideTask_ExplainsTheRestriction, asserting a clean refusal. Awaiting
    // inside a task now works, so the test asserts the behaviour instead of the apology — the
    // same conversion every shipped deferral in this suite has had.
    [Fact]
    public void Concurrency_AwaitInsideTask_MatchesInterpreter()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        const string src = """
            Pull a rabbit.
                Have rabbit start a task as inner:
                    return 7.
                Done.
                Have rabbit start a task as outer:
                    Define got as the awaited result of inner.
                    return got + 1.
                Done.
                State the awaited result of outer.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Refusal_ChannelOutsideRabbit_ExplainsTheRestriction()
    {
        const string src = """
            Define ch as a channel of number.
            State "unreached".
            """;
        var ex = Assert.Throws<CompilerException>(() => Compile(src));
        Assert.Contains("inside a rabbit", ex.Message);
        Assert.DoesNotContain("slice", ex.Message);
    }

    // ── `range` in value position ─────────────────────────────────────────────────────────────
    // Only the for-each form was ever emitted, so `Define halves as range 1 to 2 counting by 0.5.`
    // — an example in REFERENCE.md — did not compile at all.

    [Fact]
    public void Range_InValuePosition_MatchesInterpreter()
    {
        const string src = """
            Define halves as range 1 to 2 counting by 0.5.
            State halves.
            Define ups as range 1 to 5.
            State ups.
            State the number of ups.
            Define downs as range 5 to 1 counting by 2.
            State downs.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // Materializing a range and iterating one must agree — the value form reuses the for-each
    // form's direction and step logic precisely so they cannot drift.
    [Fact]
    public void Range_ValueFormAndForEachForm_Agree()
    {
        const string src = """
            Define collected as a series of number with ().
            For each n in range 5 to 1 counting by 2, repeat:
                Add n to collected.
            Done.
            State collected.
            State range 5 to 1 counting by 2.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // A LITERAL zero step is rejected by the shared type checker, but a computed one can only be
    // caught at runtime — where the compiler would otherwise spin forever. The interpreter uses
    // two different messages for zero and negative, so both are matched.
    [Theory]
    [InlineData("0",     "never makes progress")]
    [InlineData("0 - 2", "must be positive")]
    public void Range_NonPositiveComputedStep_RaisesLikeTheInterpreter(string stepExpr, string fragment)
    {
        string src = $$"""
            Define z as {{stepExpr}}.
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
        Assert.Contains(fragment, Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // The same guard on the for-each form, which had it neither.
    [Fact]
    public void Range_ForEachWithNonPositiveComputedStep_RaisesLikeTheInterpreter()
    {
        const string src = """
            Define z as 0.
            Try to:
                For each n in range 1 to 5 counting by z, repeat:
                    State n.
                Done.
            Done.
            In case of exception (the exception):
                State "caught: " joined to the message of the exception.
                Suppress the exception.
            Done.
            State "after".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── TCAP — a task may capture any type, not just number/fact/channel ───────────────────────
    // A capture crosses a thread boundary, so it travels the way a channel message does: deep-copied
    // into a heap envelope at spawn, copied into the task's own arena on arrival. That keeps the two
    // threads' arenas disentangled. Because the task only READS these, its copy is observationally
    // identical to the interpreter's shared binding — so these oracle-match exactly, unlike the
    // order-dependent concurrency tests. (Linux-only: mingw can't build pthreads.)

    [Fact]
    public void TaskCapture_Series_MatchesInterpreter()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;
        const string src = """
            Define data as a series of number with (1, 2, 3, 4, 5).
            Pull a rabbit.
                Have rabbit start a task as sum:
                    Define t as 0.
                    For each v in data, repeat:
                        t becomes t + v.
                    Done.
                    return t.
                Done.
                State the awaited result of sum.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }
}
