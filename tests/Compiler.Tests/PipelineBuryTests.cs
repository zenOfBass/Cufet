using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Compiler.Tests;

/// <summary>
/// `Bury &lt;value&gt;.` — the suspension primitive, reachable from any function.
/// </summary>
/// <remarks>
/// <para>
/// ★★ THE POINT OF THIS FILE IS WHAT IS ABSENT. Every program below suspends and resumes without
/// pulling, naming or passing a rabbit anywhere. The bare form had been removed on the reasoning
/// that *"a rabbit is the agent that does memory work"* — but MEASURED 2026-09-19, the named rabbit
/// is inert: `cd_rabbit` compiles to an empty struct, threaded into the closure and never read, and
/// swapping which rabbit a bury names changes one line of emitted C that nothing loads. The buried
/// state lives in the region open at the CALL SITE. So the bare form is not a new power; it is the
/// one that was always there with the ceremony dropped, and it is what lets a module a person
/// writes suspend at all.
/// </para>
/// <para>
/// ⚠ A test here that starts needing a rabbit has lost the plot: grep these sources for "rabbit"
/// and the answer must stay zero outside the equivalence test, which needs one by construction.
/// </para>
/// </remarks>
public class PipelineBuryTests : PipelineTestBase
{
    [Fact]
    public void Bury_GeneratorWithNoRabbitAnywhere_BothBackendsAgree()
    {
        // Not one mention of a rabbit — the whole reason the bare form is back.
        const string src = """
            Bind number to counting-up, given (the number first-value):
                Define next as first-value.
                Repeat:
                    Bury next.
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
    public void Bury_RunsOut_AndTheStashReportsSpent()
    {
        // A finite generator: two values, then void forever. `unbury` narrowing is what a caller
        // sees, and it must read the same on both backends once the body falls off its end.
        const string src = """
            Bind text to two-names, given ():
                Bury "ada".
                Bury "grace".
            Done.

            Define names as cast two-names on ().
            State (unbury names) but void is "spent".
            State (unbury names) but void is "spent".
            State (unbury names) but void is "spent".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Bury_TwoStashesFromOneFunction_KeepSeparateState()
    {
        // Each cast makes its own machine. Interleaving them is what catches state that leaked
        // into something shared — and with no rabbit around, there is nothing shared to blame.
        const string src = """
            Bind number to ticks, given (the number from-here):
                Define n as from-here.
                Repeat:
                    Bury n.
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
    public void Bury_InsideAnIfArm_StillSuspends()
    {
        // The detection walk reads arm bodies through the NAMESPACE, not through IStatement —
        // see StashDetectionTests for why that is load-bearing.
        const string src = """
            Bind number to picky, given (the fact go):
                If go:
                    Bury 1.
                Done.
                Bury 2.
            Done.

            Define s as cast picky on (true).
            State (unbury s) but void is -1.
            State (unbury s) but void is -1.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void BareBury_AndHaveRabbitBury_ProduceTheSameProgram()
    {
        // ★★ The equivalence the whole slice rests on. `Have <rabbit> bury <x>.` is the SAME
        // statement with an agent named — kept because it reads well where a rabbit is in hand,
        // not because the rabbit does anything. Two sources differing only in the ceremony must
        // run identically on both backends; if these ever diverge, the rabbit has grown a
        // privilege back.
        const string bare = """
            Bind number to counter, given (the number begin-at):
                Define n as begin-at.
                Repeat:
                    Bury n.
                    The n becomes n + 1.
                Until false.
            Done.
            Pull a rabbit as hopper.
                Define s as cast counter on (7).
                State (unbury s) but void is -1.
                State (unbury s) but void is -1.
            Done.
            """;
        const string commanded = """
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
        Assert.Equal(InterpretRaw(bare), InterpretRaw(commanded));
        Assert.Equal(InterpretRaw(bare), CompileRaw(bare));
        Assert.Equal(InterpretRaw(commanded), CompileRaw(commanded));
    }

    [Fact]
    public void Bury_InsideAMethod_SuspendsTheMethod()
    {
        // ★ A METHOD reaches the primitive through a different door — MethodSignature, and the
        // (owner, name) pair in _buryingMethods — so the free-function tests above do not cover it.
        const string src = """
            Define object clock with (the number at):
                Bind number to ticks, given ():
                    Define n as one's at.
                    Repeat:
                        Bury n.
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

    // ── Refusals ─────────────────────────────────────────────────────────────────────────────

    // ★★ These three close a LIVE DIVERGENCE, not a hypothetical. A suspension inside a lambda
    // passed `check`, ran with the suspension silently inert on the interpreter, and killed the
    // compiler on its own internal safety valve. Both spellings did it, which is why both are here.

    [Fact]
    public void BareBury_InsideALambda_IsRefused()
    {
        var ex = Assert.Throws<StashUnsupportedException>(() => InterpretRaw("""
            Bind void to go, given ():
                Define f as a function given (the number x): Bury x. Done.
                State "made it".
            Done.
            Cast go on ().
            """));
        Assert.Contains("cannot hand a value out and pause", ex.Message);
        Assert.Contains("'bury'", ex.Message);
    }

    [Fact]
    public void CommandedBury_InsideALambda_IsRefused()
    {
        // ⚠ A separate PARSE PATH from the bare form, so it needs its own test rather than an
        // assumption that one covers the other.
        var ex = Assert.Throws<StashUnsupportedException>(() => InterpretRaw("""
            Bind void to go, given (the rabbit helper):
                Define f as a function given (the number x): Have helper bury x. Done.
                State "made it".
            Done.
            Pull a rabbit as hopper.
                Cast go on (hopper).
            Done.
            """));
        Assert.Contains("cannot hand a value out and pause", ex.Message);
    }

    [Fact]
    public void AnOrdinaryLambda_InsideASuspendingFunction_StillWorks()
    {
        // ★ THE GUARD AGAINST OVER-REFUSING, and the reason the refusal probes the lambda's OWN
        // body. A lambda that does not suspend is ordinary wherever it sits — including inside a
        // function that does suspend, which is exactly where a too-wide check would bite.
        const string src = """
            Bind number to counting-up, given (the number first-value):
                Define double-it as a function given (the number x): Return x * 2. Done.
                Define next as first-value.
                Repeat:
                    Bury cast double-it on (next).
                    The next becomes next + 1.
                Until false.
            Done.

            Define counter as cast counting-up on (3).
            State (unbury counter) but void is -1.
            State (unbury counter) but void is -1.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Bury_AtTopLevel_IsRefused()
    {
        // There is no caller to hand a value to and nothing to resume.
        var ex = Assert.Throws<ParseException>(() =>
            new Parser(new CufetLexer("Bury 1.").Tokenize()).Parse());
        Assert.Contains("only meaningful inside a function", ex.Message);
    }
}
