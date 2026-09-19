using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Compiler.Tests;

/// <summary>
/// `Have &lt;rabbit&gt; bury &lt;value&gt;.` through the oracle — suspension, resumption, and the two
/// shapes that are refused.
/// </summary>
/// <remarks>
/// <para>
/// ★★ A BURY IS ALWAYS COMMANDED. A rabbit is an agent you summon and give work to; burying is
/// memory work, so it is handed over rather than done in the air. There is no bare `Bury x.`
/// </para>
/// <para>
/// ⚠ That was re-opened and re-closed on 2026-09-19. The bare form was allowed for a day on the
/// measurement that the named rabbit is INERT in the lowering — `cd_rabbit` is an empty struct
/// nothing reads, and the buried state lives in the region open at the call site. The measurement
/// is true and is not the point: the rabbit is load-bearing in the LANGUAGE, and the lowering
/// catching up to that is the work. `BareBury_IsRefused` is the guard; do not re-derive the
/// decision from the emitted C a third time.
/// </para>
/// </remarks>
public class PipelineBuryTests : PipelineTestBase
{
    [Fact]
    public void Bury_RunsOut_AndTheStashReportsSpent()
    {
        // A finite generator: two values, then void forever. `unbury` narrowing is what a caller
        // sees, and it must read the same on both backends once the body falls off its end.
        const string src = """
            Bind text to two-names, given (the rabbit helper):
                Have helper bury "ada".
                Have helper bury "grace".
            Done.

            Pull a rabbit as hopper.
                Define names as cast two-names on (hopper).
                State (unbury names) but void is "spent".
                State (unbury names) but void is "spent".
                State (unbury names) but void is "spent".
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Bury_TwoStashesFromOneFunction_KeepSeparateState()
    {
        // ★ Each cast makes its own machine, and they share a rabbit — so interleaving them is
        // what catches state that leaked into the AGENT rather than staying with the work.
        const string src = """
            Bind number to ticks, given (the rabbit helper, the number from-here):
                Define n as from-here.
                Repeat:
                    Have helper bury n.
                    The n becomes n + 10.
                Until false.
            Done.

            Pull a rabbit as hopper.
                Define left as cast ticks on (hopper, 1).
                Define right as cast ticks on (hopper, 500).
                State (unbury left) but void is -1.
                State (unbury right) but void is -1.
                State (unbury left) but void is -1.
                State (unbury right) but void is -1.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Bury_InsideAMethod_SuspendsTheMethod()
    {
        // ★ A METHOD reaches suspension through a different door — MethodSignature, and the
        // (owner, name) pair in _buryingMethods — so the free-function tests do not cover it.
        const string src = """
            Define object clock with (the number at):
                Bind number to ticks, given (the rabbit helper):
                    Define n as one's at.
                    Repeat:
                        Have helper bury n.
                        The n becomes n + 1.
                    Until false.
                Done.
            Done.

            Pull a rabbit as hopper.
                Define c as a new clock { the at 10 }.
                Define t as cast c's ticks on (hopper).
                State (unbury t) but void is -1.
                State (unbury t) but void is -1.
            Done.
            """;
        Assert.Equal("10\n11\n".ReplaceLineEndings(), InterpretRaw(src).ReplaceLineEndings());
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── Refusals ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BareBury_IsRefused()
    {
        // ★★ The guard on the decision, not on a parser detail. A bury names the agent doing it.
        var ex = Assert.Throws<ParseException>(() =>
            new Parser(new CufetLexer("""
                Bind number to counting-up, given (the number first-value):
                    Bury first-value.
                Done.
                """).Tokenize()).Parse());
        Assert.Contains("a bury needs a rabbit to do it", ex.Message);
    }

    [Fact]
    public void Bury_InsideALambda_IsRefused()
    {
        // ★★ Closes a LIVE DIVERGENCE. This passed `check`, ran with the suspension silently inert
        // on the interpreter, and killed the compiler on its own internal safety valve — because
        // the rewrite walk treats a lambda as opaque (correctly: a nested function's statements
        // belong to IT) and then nothing asked the lambda itself. Two correct rules with a gap.
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
        Assert.Contains("'bury'", ex.Message);
    }

    [Fact]
    public void AnOrdinaryLambda_InsideASuspendingFunction_StillWorks()
    {
        // ★ THE GUARD AGAINST OVER-REFUSING, and the reason the refusal probes the lambda's OWN
        // body. A lambda that does not suspend is ordinary wherever it sits — including inside a
        // function that does, which is exactly where a too-wide check would bite.
        const string src = """
            Bind number to counting-up, given (the rabbit helper, the number first-value):
                Define double-it as a function given (the number x): Return x * 2. Done.
                Define next as first-value.
                Repeat:
                    Have helper bury cast double-it on (next).
                    The next becomes next + 1.
                Until false.
            Done.

            Pull a rabbit as hopper.
                Define counter as cast counting-up on (hopper, 3).
                State (unbury counter) but void is -1.
                State (unbury counter) but void is -1.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }
}
