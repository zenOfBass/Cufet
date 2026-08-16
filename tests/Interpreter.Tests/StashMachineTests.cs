using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

/// <summary>
/// The state machine a burying function becomes — one test per shape it has to linearise.
/// </summary>
/// <remarks>
/// <para>
/// ★ These run the REWRITTEN program, which is the whole point. `Check` hands back the program to
/// run, and a caller that ignores it runs a body still full of `bury` statements — both backends
/// refuse that loudly, so the mistake is loud, but only if a test actually executes what `Check`
/// returned. Every helper here does.
/// </para>
/// <para>
/// What each test is really asking is whether state SURVIVES. A loop counter, an iterator, a series
/// being built up — each lives in a slot between resumptions, and each shape below is a different
/// way for that to go wrong: a counter that resets, an iterator that restarts, a `Skip` that lands
/// in the wrong block.
/// </para>
/// </remarks>
public class StashMachineTests
{
    private static string Run(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var parsed  = new Parser(tokens).Parse();
        var program = new TypeChecker().Check(parsed);   // ★ the rewritten one, not `parsed`
        var output  = new System.IO.StringWriter();
        new Interpreter(output).Execute(program);
        return output.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    private static TypeException Refused(string source) =>
        Assert.Throws<StashUnsupportedException>(() => Run(source));

    // Drains a stash of numbers to standard output, so a test can say what it expects as a list.
    private const string DrainNumbers = """
        Repeat:
            Define value as unbury source.
            If value is void:
                Stop.
            Done.
            State value.
        Until false.
        Done.
        """;

    // ── The shapes that have to linearise ──────────────────────────────────

    [Fact]
    public void AWhileLoop_KeepsItsCounterAcrossBurys()
    {
        // The simplest thing that cannot work without a slot: `step-value` is written after the
        // bury and read before it, so a resumption that started the body over would print 1 forever.
        Assert.Equal("1\n2\n3", Run("""
            Bind number to upto-three, given (the rabbit helper):
                Define step-value as 1.
                While step-value is not greater than 3, repeat:
                    Have helper bury step-value.
                    The step-value becomes step-value + 1.
                Done.
            Done.

            Pull a rabbit as den.
            Define source as cast upto-three on (den).
            """ + DrainNumbers));
    }

    [Fact]
    public void AForEach_ResumesAtTheNextItem()
    {
        Assert.Equal("10\n20\n30", Run("""
            Bind number to tenfold, given (the rabbit helper, the series of number values):
                For each value in values, repeat:
                    Have helper bury value * 10.
                Done.
            Done.

            Pull a rabbit as den.
            Define source as cast tenfold on (den, a series with (1, 2, 3)).
            """ + DrainNumbers));
    }

    [Fact]
    public void ASkip_PassesOverAnItemWithoutEndingTheLoop()
    {
        Assert.Equal("2\n4\n6", Run("""
            Bind number to evens-only, given (the rabbit helper, the series of number values):
                For each value in values, repeat:
                    If value % 2 is not 0:
                        Skip.
                    Done.
                    Have helper bury value.
                Done.
            Done.

            Pull a rabbit as den.
            Define source as cast evens-only on (den, a series with (1, 2, 3, 4, 5, 6)).
            """ + DrainNumbers));
    }

    [Fact]
    public void AStop_EndsTheLoopAndTheRestOfTheBodyStillRuns()
    {
        Assert.Equal("1\n2\n99", Run("""
            Bind number to upto-two-then-marker, given (the rabbit helper, the series of number values):
                For each value in values, repeat:
                    If value is greater than 2:
                        Stop.
                    Done.
                    Have helper bury value.
                Done.
                Have helper bury 99.
            Done.

            Pull a rabbit as den.
            Define source as cast upto-two-then-marker on (den, a series with (1, 2, 3, 4)).
            """ + DrainNumbers));
    }

    [Fact]
    public void ARepeatUntil_TestsAfterTheBody()
    {
        Assert.Equal("2\n1\n0", Run("""
            Bind number to countdown, given (the rabbit helper, the number origin):
                Repeat:
                    The origin becomes origin - 1.
                    Have helper bury origin.
                Until origin is 0.
            Done.

            Pull a rabbit as den.
            Define source as cast countdown on (den, 3).
            """ + DrainNumbers));
    }

    [Fact]
    public void NestedLoops_EachKeepTheirOwnPlace()
    {
        // ★ Two counters in flight at once, and the inner one is re-declared on every outer pass.
        // A single shared slot, or an inner counter that failed to reset, both show up here.
        Assert.Equal("11\n12\n21\n22", Run("""
            Bind number to grid, given (the rabbit helper):
                Define row as 1.
                While row is not greater than 2, repeat:
                    Define column as 1.
                    While column is not greater than 2, repeat:
                        Have helper bury row * 10 + column.
                        The column becomes column + 1.
                    Done.
                    The row becomes row + 1.
                Done.
            Done.

            Pull a rabbit as den.
            Define source as cast grid on (den).
            """ + DrainNumbers));
    }

    [Fact]
    public void AReassignedParameter_KeepsItsNewValue()
    {
        // A parameter is handed to the machine afresh on every resumption, so one that the body
        // WRITES needs a slot like any local — otherwise every resumption starts from the argument.
        Assert.Equal("9\n8\n7\n6", Run("""
            Bind number to downward, given (the rabbit helper, the number counter):
                While counter is greater than 6, repeat:
                    The counter becomes counter - 1.
                    Have helper bury counter.
                Done.
            Done.

            Pull a rabbit as den.
            Define source as cast downward on (den, 10).
            """ + DrainNumbers));
    }

    [Fact]
    public void ARegionLocal_IsTheSameOneOnEveryResumption()
    {
        // A series is shared, not snapshotted — so what was inserted before a bury is still there
        // after it. If the slot round-tripped a COPY this would print 1, 1, 1.
        Assert.Equal("1\n2\n3", Run("""
            Bind number to running-total, given (the rabbit helper, the series of number values):
                Define collected as a series of number.
                For each value in values, repeat:
                    Insert value into collected.
                    Define tally as the number of collected.
                    Have helper bury tally.
                Done.
            Done.

            Pull a rabbit as den.
            Define source as cast running-total on (den, a series with (5, 6, 7)).
            """ + DrainNumbers));
    }

    // ── What a spent stash does ────────────────────────────────────────────

    [Fact]
    public void AStashStaysSpent_HoweverOftenItIsAsked()
    {
        Assert.Equal("1\nvoid\nvoid", Run("""
            Bind number to just-one, given (the rabbit helper):
                Have helper bury 1.
            Done.

            Pull a rabbit as den.
            Define source as cast just-one on (den).
            State unbury source.
            State unbury source.
            State unbury source.
            Done.
            """));
    }

    [Fact]
    public void TwoStashesFromOneFunction_DoNotShareState()
    {
        Assert.Equal("1\n1\n2\n2", Run("""
            Bind number to pair, given (the rabbit helper):
                Have helper bury 1.
                Have helper bury 2.
            Done.

            Pull a rabbit as den.
            Define one-stash as cast pair on (den).
            Define other-stash as cast pair on (den).
            State unbury one-stash.
            State unbury other-stash.
            State unbury one-stash.
            State unbury other-stash.
            Done.
            """));
    }

    // ── A stash is a value ─────────────────────────────────────────────────
    //
    // ★ These pass through StashTypeSubstitution, and each one would have been a "the compiler
    // cannot represent a stash of number yet" before it existed. They are asserted here on the
    // interpreter because that is where behaviour is pinned; that the COMPILER agrees is the
    // oracle test's job, on examples/language/stashes.cufe.

    private const string CountingUp = """
        Bind number to counting-up, given (the rabbit helper, the number first-value):
            Define next as first-value.
            Repeat:
                Have helper bury next.
                The next becomes next + 1.
            Until false.
        Done.

        """;

    [Fact]
    public void AStash_CanBePassedToAFunction()
    {
        Assert.Equal("7\n8", Run(CountingUp + """
            Bind void to take-two, given (the stash of number source):
                State (unbury source but void is 0).
                State (unbury source but void is 0).
            Done.

            Pull a rabbit as den.
                Cast take-two on (cast counting-up on (den, 7)).
            Done.
            """));
    }

    [Fact]
    public void StashesInASeries_EachKeepTheirOwnPlace()
    {
        // The second pass is the assertion. If a series held a COPY, or if the closures shared
        // state, the two rounds would not read 1,10 then 2,11.
        Assert.Equal("1\n10\n2\n11", Run(CountingUp + """
            Pull a rabbit as den.
                Define many as a series of stash of number.
                Insert (cast counting-up on (den, 1)) into many.
                Insert (cast counting-up on (den, 10)) into many.
                For each one-stash in many, repeat:
                    State (unbury one-stash but void is 0).
                Done.
                For each one-stash in many, repeat:
                    State (unbury one-stash but void is 0).
                Done.
            Done.
            """));
    }

    [Fact]
    public void AStash_CanDrainAnotherStash()
    {
        // ★ Delegation, which is the reason first-class mattered: `inner` is a `stash of number`
        // held as a local INSIDE a burying function, so it has to survive that function's own
        // resumptions as well as produce values of its own.
        Assert.Equal("1\n4\n9\n1\n8\n27", Run("""
            Bind number to squares, given (the rabbit helper, the number upto):
                Define side as 1.
                While side is not greater than upto, repeat:
                    Have helper bury side * side.
                    The side becomes side + 1.
                Done.
            Done.

            Bind number to squares-and-cubes, given (the rabbit helper, the number upto):
                Define inner as cast squares on (helper, upto).
                Repeat:
                    Define value as unbury inner.
                    If value is void:
                        Stop.
                    Done.
                    Have helper bury value.
                Until false.
                Define side as 1.
                While side is not greater than upto, repeat:
                    Have helper bury side * side * side.
                    The side becomes side + 1.
                Done.
            Done.

            Pull a rabbit as den.
            Define source as cast squares-and-cubes on (den, 3).
            """ + DrainNumbers));
    }

    // ── The refusals ───────────────────────────────────────────────────────
    //
    // Each of these is a shape whose meaning a step number cannot carry, and every one of them is
    // refused during the check — so both backends refuse identically and nothing can interpret one
    // way and compile another.

    /// <summary>
    /// ★ A bury inside a type test works — the arm's condition is carried into its block and
    /// re-tested there, which hands the narrowing back.
    /// </summary>
    /// <remarks>
    /// The re-test is not a real branch. Every hoisted local is restored from its slot before the
    /// guard runs, so the subject holds exactly what it held when the arm was chosen and the
    /// condition gives exactly the answer it gave then. It exists so the compiler can see which
    /// case of the union is in hand; without it the block resumes at the declared type and the
    /// generated C will not build.
    /// </remarks>
    [Fact]
    public void ABuryInsideATypeTest_KeepsTheNarrowing()
    {
        Assert.Equal("two\nfour", Run("""
            Bind text to texts-only, given (the rabbit helper, the series of (number or text) things):
                For each thing in things, repeat:
                    If thing is a text:
                        Have helper bury thing.
                    Done.
                Done.
            Done.

            Pull a rabbit as den.
            Define source as cast texts-only on (den, a series of (number or text) with (1, "two", 3, "four")).
            Repeat:
                Define found as unbury source.
                If found is void:
                    Stop.
                Done.
                State found.
            Until false.
            Done.
            """));
    }

    [Fact]
    public void ATypeTestNarrowingToNumber_AlsoSurvives()
    {
        // The other direction: the arm narrows to `number`, and arithmetic on it has to type-check.
        Assert.Equal("2\n6", Run("""
            Bind number to doubled, given (the rabbit helper, the series of (number or text) things):
                For each thing in things, repeat:
                    If thing is a number:
                        Have helper bury thing * 2.
                    Done.
                Done.
            Done.

            Pull a rabbit as den.
            Define source as cast doubled on (den, a series of (number or text) with (1, "two", 3)).
            """ + DrainNumbers));
    }

    /// <summary>
    /// The `Otherwise` of a type test narrows too — by elimination — and survives the split.
    /// </summary>
    /// <remarks>
    /// ★ This one was refused for an hour, and the refusal was correct at the time: the guard for an
    /// else arm is the NEGATED test, and the compiler did not narrow on `is not a &lt;type&gt;` at
    /// all. Fixing that — a plain divergence in ordinary code, nothing to do with stashes — lifted
    /// this case with no stash-specific code at all. Worth remembering as a shape: a refusal whose
    /// stated reason lives in another component is a refusal that disappears when that component is
    /// fixed, not one to design around.
    /// </remarks>
    [Fact]
    public void TheOtherwiseOfATypeTest_NarrowsByElimination()
    {
        Assert.Equal("number: 1\ntext: two\nnumber: 3", Run("""
            Bind text to describe-all, given (the rabbit helper, the series of (number or text) things):
                For each thing in things, repeat:
                    If thing is a text:
                        Have helper bury "text: " joined to thing.
                    Done.
                    Otherwise:
                        Have helper bury "number: " joined to (thing converted to text).
                    Done.
                Done.
            Done.

            Pull a rabbit as den.
            Define source as cast describe-all on (den, a series of (number or text) with (1, "two", 3)).
            Repeat:
                Define line as unbury source.
                If line is void:
                    Stop.
                Done.
                State line.
            Until false.
            Done.
            """));
    }

    [Fact]
    public void ABuryInsideAJudgement_IsRefused()
    {
        Assert.Contains("judgement", Refused("""
            Bind number to sorter, given (the rabbit helper, the (number or text) thing):
                Judge thing, where it is:
                    A number, have helper bury 1.
                    A text, have helper bury 2.
                Done.
            Done.

            Pull a rabbit as den.
                Define source as cast sorter on (den, 5).
                State unbury source.
            Done.
            """).Message);
    }

    [Fact]
    public void AReturnInsideABuryingFunction_IsRefused()
    {
        Assert.Contains("can't also return", Refused("""
            Bind number to confused, given (the rabbit helper):
                Have helper bury 1.
                Return 2.
            Done.

            Pull a rabbit as den.
            Define source as cast confused on (den).
            State unbury source.
            Done.
            """).Message);
    }

    [Fact]
    public void ABuryInsideAForEachOverAMap_IsRefused()
    {
        // A resumption counts back to where the loop was, and a map's entries have no position to
        // count to.
        Assert.Contains("not a series", Refused("""
            Bind text to keys-of, given (the rabbit helper, the map from text to number ages):
                For each pair in ages, repeat:
                    Have helper bury the key of pair.
                Done.
            Done.

            Pull a rabbit as den.
            Define source as cast keys-of on (den, a map from text to number).
            State unbury source.
            Done.
            """).Message);
    }

    [Fact]
    public void AShadowedNameInsideABuryingFunction_IsRefused()
    {
        // Linearising flattens every scope in the body into one, so the shadow would land on top of
        // the very name it was written to protect.
        Assert.Contains("can't shadow", Assert.Throws<TypeException>(() => Run("""
            Bind number to shadowy, given (the rabbit helper):
                Define depth as 1.
                While depth is less than 2, repeat:
                    Define a shadow depth as 9.
                    Have helper bury depth.
                    The depth becomes depth + 1.
                Done.
            Done.

            Pull a rabbit as den.
            Define source as cast shadowy on (den).
            State unbury source.
            Done.
            """)).Message);
    }

    [Fact]
    public void OneNameAtTwoTypesInsideABuryingFunction_IsRefused()
    {
        // Sibling scopes may each declare `label` anywhere else in the language; here they become
        // one slot, and a slot holds one type.
        Assert.Contains("in another", Assert.Throws<TypeException>(() => Run("""
            Bind number to two-minded, given (the rabbit helper, the fact go):
                If go:
                    Define label as 1.
                    Have helper bury label.
                Done.
                Otherwise:
                    Define label as "one".
                    Have helper bury the length of label.
                Done.
            Done.

            Pull a rabbit as den.
            Define source as cast two-minded on (den, true).
            State unbury source.
            Done.
            """)).Message);
    }
}
