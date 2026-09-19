using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Compiler.Tests;

/// <summary>
/// `Bring <value>.` — the suspension primitive, and the proof that a rabbit is not privileged.
/// </summary>
/// <remarks>
/// <para>
/// ★★ THE POINT OF THIS FILE IS WHAT IS ABSENT. Every program below suspends and resumes without
/// pulling, naming or passing a rabbit anywhere. Before this slice the only way to suspend was
/// `Have &lt;rabbit&gt; bury &lt;value&gt;.`, which meant the one thing a person could not write was
/// the thing a rabbit does — so `bury` is now a rabbit's NAME for `Bring`, not a power only a
/// rabbit has.
/// </para>
/// <para>
/// ⚠ A test here that starts needing a rabbit has lost the plot: grep these sources for "rabbit"
/// and the answer must stay zero.
/// </para>
/// </remarks>
public class PipelineBringTests : PipelineTestBase
{
    [Fact]
    public void Bring_GeneratorWithNoRabbitAnywhere_BothBackendsAgree()
    {
        // Not one mention of a rabbit — the whole reason this slice exists.
        const string src = """
            Bind number to counting-up, given (the number first-value):
                Define next as first-value.
                Repeat:
                    Bring next.
                    The next becomes next + 1.
                Until false.
            Done.

            Define counter as cast counting-up on (3).
            State (unbury counter) but void is -1.
            State (unbury counter) but void is -1.
            State (unbury counter) but void is -1.
            """;
        Assert.Equal("3\n4\n5\n".ReplaceLineEndings(), InterpretRaw(src).ReplaceLineEndings());
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Bring_RunsOut_AndTheStashReportsSpent()
    {
        // A finite generator: two values, then void forever. `unbury` narrowing is what a caller
        // sees, and it must read the same on both backends once the body falls off its end.
        const string src = """
            Bind text to two-names, given ():
                Bring "ada".
                Bring "grace".
            Done.

            Define names as cast two-names on ().
            State (unbury names) but void is "spent".
            State (unbury names) but void is "spent".
            State (unbury names) but void is "spent".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Bring_TwoStashesFromOneFunction_KeepSeparateState()
    {
        // Each cast makes its own machine. Interleaving them is what catches state that leaked
        // into something shared — and with no rabbit around, there is nothing shared to blame.
        const string src = """
            Bind number to ticks, given (the number from-here):
                Define n as from-here.
                Repeat:
                    Bring n.
                    The n becomes n + 10.
                Until false.
            Done.

            Define left as cast ticks on (1).
            Define right as cast ticks on (500).
            State (unbury left) but void is -1.
            State (unbury right) but void is -1.
            State (unbury left) but void is -1.
            State (unbury right) but void is -1.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Bring_InsideAnIfArm_StillSuspends()
    {
        // The detection walk reads arm bodies through the NAMESPACE, not through IStatement —
        // see StashDetectionTests for why that is load-bearing. Same trap, new spelling.
        const string src = """
            Bind number to picky, given (the fact go):
                If go:
                    Bring 1.
                Done.
                Bring 2.
            Done.

            Define s as cast picky on (true).
            State (unbury s) but void is -1.
            State (unbury s) but void is -1.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Bring_AndBury_ProduceTheSameProgram()
    {
        // ★★ The equivalence this whole slice rests on: `bury` is a NAME for `Bring`. Two sources
        // differing only in the spelling must run identically on both backends. If these ever
        // diverge, the rabbit has quietly grown a privilege back.
        const string withBring = """
            Bind number to counter, given (the number begin-at):
                Define n as begin-at.
                Repeat:
                    Bring n.
                    The n becomes n + 1.
                Until false.
            Done.
            Pull a rabbit as hopper.
                Define s as cast counter on (7).
                State (unbury s) but void is -1.
                State (unbury s) but void is -1.
            Done.
            """;
        const string withBury = """
            Bind number to counter, given (the rabbit helper, the number begin-at):
                Define n as begin-at.
                Repeat:
                    Have helper bury n.
                    The n becomes n + 1.
                Until false.
            Done.
            Pull a rabbit as hopper.
                Define s as cast counter on (hopper, 7).
                State (unbury s) but void is -1.
                State (unbury s) but void is -1.
            Done.
            """;
        Assert.Equal(InterpretRaw(withBring), InterpretRaw(withBury));
        Assert.Equal(InterpretRaw(withBring), CompileRaw(withBring));
        Assert.Equal(InterpretRaw(withBury), CompileRaw(withBury));
    }

    [Fact]
    public void Bring_InsideAMethod_SuspendsTheMethod()
    {
        // ★ A METHOD reaches the primitive through a different door — MethodSignature, and the
        // (owner, name) pair in _buryingMethods — so the free-function tests above do not cover it.
        // Still no rabbit: an ordinary object suspends, which is the shape a person's own
        // region-owning type will need once it can own one.
        const string src = """
            Define object clock with (the number at):
                Bind number to ticks, given ():
                    Define n as one's at.
                    Repeat:
                        Bring n.
                        The n becomes n + 1.
                    Until false.
                Done.
            Done.

            Define c as a new clock { the at 10 }.
            Define t as cast c's ticks on ().
            State (unbury t) but void is -1.
            State (unbury t) but void is -1.
            """;
        Assert.Equal("10\n11\n".ReplaceLineEndings(), InterpretRaw(src).ReplaceLineEndings());
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── `bring` is CONTEXTUAL, so it is still a name anyone may use ──────────────────────────

    [Fact]
    public void Bring_IsNotReserved_StillUsableAsAnOrdinaryName()
    {
        // ★ The whole reason the word was affordable. A reserved word is taken from every program
        // forever, including the ones that never suspend anything.
        const string src = """
            Define bring as 5.
            The bring becomes bring + 1.
            State bring.
            Bind number to bring-back, given (the number x): Return x * 2. Done.
            State cast bring-back on (bring).
            """;
        Assert.Equal("6\n12\n".ReplaceLineEndings(), InterpretRaw(src).ReplaceLineEndings());
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── Refusals ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Bring_AtTopLevel_IsRefused()
    {
        // There is no caller to hand a value to and nothing to resume.
        var ex = Assert.Throws<ParseException>(() =>
            new Parser(new CufetLexer("Bring 1.").Tokenize()).Parse());
        Assert.Contains("only meaningful inside a function", ex.Message);
    }
}
