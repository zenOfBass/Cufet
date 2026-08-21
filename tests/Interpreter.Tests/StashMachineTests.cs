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

    /// <summary>
    /// A judgement arm carries a BINDING as well as a narrowing, and both survive the split.
    /// </summary>
    /// <remarks>
    /// ★ The binding is what made this harder than `If`. `it` is not restated as a condition — it
    /// is made an ordinary local, so it earns a hoisting slot, the subject is evaluated ONCE, and
    /// every later block restores `it` from its slot rather than re-evaluating a subject that may
    /// have moved on. The narrowing is then a guard like any other: `it is a &lt;case&gt;`.
    ///
    /// Both halves are load-bearing here — `it + 100` needs a number and `the length of it` needs
    /// a text, so a resumption that restored the binding without the narrowing would not compile.
    /// </remarks>
    [Fact]
    public void ABuryInsideAJudgementArm_KeepsTheBindingAndTheNarrowing()
    {
        Assert.Equal("101\n5", Run("""
            Bind number to sorter, given (the rabbit helper, the series of (number or text) things):
                For each thing in things, repeat:
                    Judge thing, where it is:
                        A number, have helper bury it + 100.
                        A text, have helper bury the length of it.
                    Done.
                Done.
            Done.

            Pull a rabbit as den.
                Define source as cast sorter on (den, a series of (number or text) with (1, "hello")).
                Repeat:
                    Define next as unbury source.
                    If next is void:
                        Stop.
                    Done.
                    State next.
                Until false.
            Done.
            """));
    }

    /// <summary>
    /// A lone arm's `Otherwise` narrows by elimination, so a bury may live there too.
    /// </summary>
    /// <remarks>
    /// ⚠ This is the case that needed the checker taught to narrow the `Otherwise` of a NEGATED
    /// test first — the guard here is `it is not a number`, and until that narrowed, the guard
    /// restored nothing and the arm read `it` at its declared union type.
    /// </remarks>
    [Fact]
    public void ABuryInsideALoneArmsOtherwise_KeepsTheNarrowing()
    {
        Assert.Equal("101\n5", Run("""
            Bind number to sorter, given (the rabbit helper, the series of (number or text) things):
                For each thing in things, repeat:
                    Judge thing, where it is:
                        A number, have helper bury it + 100.
                        Otherwise, have helper bury the length of it.
                    Done.
                Done.
            Done.

            Pull a rabbit as den.
                Define source as cast sorter on (den, a series of (number or text) with (1, "hello")).
                Repeat:
                    Define next as unbury source.
                    If next is void:
                        Stop.
                    Done.
                    State next.
                Until false.
            Done.
            """));
    }

    /// <summary>
    /// A GROUPED arm keeps its narrowing too — it states itself as a disjunction.
    /// </summary>
    /// <remarks>
    /// ★ `it is a A or it is a B` is the only way to say "one of these, not the others": a single
    /// test names one case, elimination names all but one, and a group is neither. Both front ends
    /// narrow that condition to the sub-union, so the guard restores exactly what the arm had.
    ///
    /// `the length of it` in the second arm is the load-bearing half — it only compiles if the
    /// grouped arm's guard left the OTHER arm's narrowing intact rather than widening everything.
    /// </remarks>
    [Fact]
    public void ABuryInsideAGroupedJudgementArm_KeepsTheNarrowing()
    {
        Assert.Equal("1\n5\n1", Run("""
            Bind number to sorter, given (the rabbit helper, the series of (number or text or fact) things):
                For each thing in things, repeat:
                    Judge thing, where it is:
                        A number or a fact, have helper bury 1.
                        A text, have helper bury the length of it.
                    Done.
                Done.
            Done.

            Pull a rabbit as den.
                Define source as cast sorter on (den,
                    a series of (number or text or fact) with (7, "hello", true)).
                Repeat:
                    Define next as unbury source.
                    If next is void:
                        Stop.
                    Done.
                    State next.
                Until false.
            Done.
            """));
    }

    /// <summary>
    /// An `Otherwise` after SEVERAL arms narrows to the residue, named as a disjunction.
    /// </summary>
    /// <remarks>
    /// `If it` is what proves it: a fact is the only thing that may be a condition on its own, so
    /// the guard must have narrowed `(number or text or fact)` down to the one case the arms left.
    /// </remarks>
    [Fact]
    public void ABuryInsideAnOtherwiseAfterSeveralArms_NarrowsToTheResidue()
    {
        Assert.Equal("107\n5\n999\n0", Run("""
            Bind number to sorter, given (the rabbit helper, the series of (number or text or fact) things):
                For each thing in things, repeat:
                    Judge thing, where it is:
                        A number, have helper bury it + 100.
                        A text, have helper bury the length of it.
                        Otherwise:
                            If it:
                                Have helper bury 999.
                            Done.
                            Otherwise:
                                Have helper bury 0.
                            Done.
                        Done.
                    Done.
                Done.
            Done.

            Pull a rabbit as den.
                Define source as cast sorter on (den,
                    a series of (number or text or fact) with (7, "hello", true, false)).
                Repeat:
                    Define next as unbury source.
                    If next is void:
                        Stop.
                    Done.
                    State next.
                Until false.
            Done.
            """));
    }

    /// <summary>
    /// A burying program may also do I/O — the type substitution walks an ENUM without choking.
    /// </summary>
    /// <remarks>
    /// ★ Latent since the substitution walk was written, and it crashed the type checker rather
    /// than mis-compiling: `Cufet.Interpreter` holds four enums (ReadForm, FileReadForm,
    /// PathCheckKind, OpenMode), an enum has no constructor, and the walk's rebuild arm called
    /// `GetConstructors().First()` on one — "Sequence contains no elements".
    ///
    /// ⚠ It hid because the walk runs only for a program containing a `bury`, and nothing combined
    /// a bury with file I/O or `the input`. Two features, each covered, never crossed. It surfaced
    /// only when a second caller started running the same walk on ordinary programs.
    /// </remarks>
    [Fact]
    public void ABuryingProgram_MayAlsoReadTheInput()
    {
        Assert.Equal("none\n1", Run("""
            Bind number to counting-up, given (the rabbit helper, the number first-value):
                Define next as first-value.
                Repeat:
                    Have helper bury next.
                    The next becomes next + 1.
                Until false.
            Done.

            Pull a rabbit as den.
                Define line as read a line from the input but void is "none".
                State line.
                Define source as cast counting-up on (den, 1).
                State unbury source but void is 0.
            Done.
            """));
    }

    [Fact]
    public void ABuryInsideAJudgementsOtherwise_NeedsAClosedUnion()
    {
        // The leftover cases have to be NAMED to resume into them, and only a closed union says
        // what they are.
        Assert.Contains("closed union", Refused("""
            Bind number to sorter, given (the rabbit helper, the number thing):
                Judge thing, where it is:
                    A number, have helper bury 1.
                    Otherwise, have helper bury 2.
                Done.
            Done.

            Pull a rabbit as den.
                Define source as cast sorter on (den, 5).
                State unbury source but void is 0.
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

    // ── `For each` over a stash ────────────────────────────────────────────
    //
    // The loop stands for the drain that used to be written by hand, so what these ask is whether
    // the drain it stands for is the one a person would have written: `Stop` and `Skip` mean what
    // they mean in any loop, a spent stash ends it, and the iterator is a plain T inside the body.

    [Fact]
    public void AForEachOverAStash_TakesEveryValueUntilItIsSpent()
    {
        Assert.Equal("rabbit\nwarren", Run("""
            Bind text to long-words-in, given (the rabbit helper, the series of text words):
                For each word in words, repeat:
                    If the length of word is less than 4:
                        Skip.
                    Done.
                    Have helper bury word.
                Done.
            Done.

            Pull a rabbit as hopper.
            Define found as cast long-words-in on (hopper, a series with ("a", "rabbit", "in", "the", "warren")).
            For each word in found, repeat:
                State word.
            Done.
            Done.
            """));
    }

    [Fact]
    public void AForEachOverAnEndlessStash_IsEndedByAStopInTheBody()
    {
        // The stash never runs out, so the body's `Stop` is the only thing that ends this — which
        // is the same `Stop` a series loop takes, landing on the same loop.
        Assert.Equal("3\n4\n5", Run("""
            Bind number to counting-up, given (the rabbit helper, the number first-value):
                Define next as first-value.
                Repeat:
                    Have helper bury next.
                    The next becomes next + 1.
                Until false.
            Done.

            Pull a rabbit as hopper.
            Define counter as cast counting-up on (hopper, 3).
            For each value in counter, repeat:
                If value is greater than 5:
                    Stop.
                Done.
                State value.
            Done.
            Done.
            """));
    }

    [Fact]
    public void ASkipInAStashLoop_TakesTheNextValueRatherThanEndingIt()
    {
        Assert.Equal("1\n3\n5", Run("""
            Bind number to counting-up, given (the rabbit helper, the number first-value):
                Define next as first-value.
                Repeat:
                    Have helper bury next.
                    The next becomes next + 1.
                Until false.
            Done.

            Pull a rabbit as hopper.
            Define counter as cast counting-up on (hopper, 1).
            For each value in counter, repeat:
                If value is greater than 5:
                    Stop.
                Done.
                If value % 2 is 0:
                    Skip.
                Done.
                State value.
            Done.
            Done.
            """));
    }

    [Fact]
    public void TheIteratorOfAStashLoop_IsThePlainHeldTypeNotAVoidable()
    {
        // What the loop is FOR. `unbury` hands back a voidable, and the drain's whole job is to
        // prove the value present before the body sees it — so arithmetic works with no `but void is`.
        Assert.Equal("20\n30", Run("""
            Bind number to counting-up, given (the rabbit helper, the number first-value):
                Define next as first-value.
                Repeat:
                    Have helper bury next.
                    The next becomes next + 1.
                Until false.
            Done.

            Pull a rabbit as hopper.
            Define counter as cast counting-up on (hopper, 2).
            For each value in counter, repeat:
                If value is greater than 3:
                    Stop.
                Done.
                State value * 10.
            Done.
            Done.
            """));
    }

    [Fact]
    public void AStashLoopInsideABuryingBody_DelegatesFromOneStashToAnother()
    {
        // ⚠ The reason the rewrite runs BEFORE the machine builder. This loop has to be split
        // across the outer function's own buries, and the machine can only do that to statements
        // it already knows how to step.
        Assert.Equal("2\n4\n6", Run("""
            Bind number to counting-up, given (the rabbit helper, the number first-value):
                Define next as first-value.
                Repeat:
                    Have helper bury next.
                    The next becomes next + 1.
                Until false.
            Done.

            Bind number to evens-of, given (the rabbit helper):
                Define inner as cast counting-up on (helper, 1).
                For each value in inner, repeat:
                    If value is greater than 6:
                        Stop.
                    Done.
                    If value % 2 is 0:
                        Have helper bury value.
                    Done.
                Done.
            Done.

            Pull a rabbit as hopper.
            Define source as cast evens-of on (hopper).
            """ + DrainNumbers));
    }

    [Fact]
    public void AStashLoopInsideAStashLoop_KeepsTheTwoApart()
    {
        Assert.Equal("1-10\n1-11\n2-10\n2-11", Run("""
            Bind number to counting-up, given (the rabbit helper, the number first-value):
                Define next as first-value.
                Repeat:
                    Have helper bury next.
                    The next becomes next + 1.
                Until false.
            Done.

            Pull a rabbit as hopper.
            Define outer-stash as cast counting-up on (hopper, 1).
            For each left in outer-stash, repeat:
                If left is greater than 2:
                    Stop.
                Done.
                Define inner-stash as cast counting-up on (hopper, 10).
                For each right in inner-stash, repeat:
                    If right is greater than 11:
                        Stop.
                    Done.
                    State (left converted to text) joined to "-" joined to (right converted to text).
                Done.
            Done.
            Done.
            """));
    }

    [Fact]
    public void AStashLoop_ShadowsAnOuterNameJustAsASeriesLoopDoes()
    {
        // ⚠ The drain declares the iterator with a `Define`, and an ordinary `Define` REFUSES a name
        // an enclosing scope already holds. A `For each` binds rather than declares — the series
        // form has always shadowed quietly — so the drain's Define is spelled as the shadow it is.
        // Without that, the stash form would refuse a program the series form accepts.
        Assert.Equal("7\n8\n99\n1\n2\n99", Run("""
            Bind number to upto, given (the rabbit helper, the number limit):
                Define next as 1.
                While next is not greater than limit, repeat:
                    Have helper bury next.
                    The next becomes next + 1.
                Done.
            Done.

            Pull a rabbit as hopper.
                Define value as 99.
                Define nums as a series with (7, 8).
                For each value in nums, repeat:
                    State value.
                Done.
                State value.

                Define counter as cast upto on (hopper, 2).
                For each value in counter, repeat:
                    State value.
                Done.
                State value.
            Done.
            """));
    }

    [Fact]
    public void AStashLoop_TakesTheBareItAndTheInlineFormToo()
    {
        // Nothing about the source changes which spellings of the loop are available.
        Assert.Equal("1\n2\n100\n200", Run("""
            Bind number to upto, given (the rabbit helper, the number limit):
                Define next as 1.
                While next is not greater than limit, repeat:
                    Have helper bury next.
                    The next becomes next + 1.
                Done.
            Done.

            Pull a rabbit as hopper.
                Define counter as cast upto on (hopper, 2).
                For each in counter, repeat:
                    State it.
                Done.

                Define other as cast upto on (hopper, 2).
                For each value in other, State value * 100.
            Done.
            """));
    }

    [Fact]
    public void LoopingOverSomethingThatIsNeitherSeriesMapNorStash_SaysSo()
    {
        Assert.Contains("Only series, maps and stashes", Assert.Throws<TypeException>(() => Run("""
            Define count as 3.
            For each value in count, repeat:
                State value.
            Done.
            """)).Message);
    }

    // ⚠ The `is not void` narrowing regression is NOT here, and could not be: the interpreter
    // narrows by value and ran that program correctly before the fix as well as after. It only ever
    // went red on the compiler, so it lives in PipelineClosureTests where it reaches its path.

    // ── A method that buries ───────────────────────────────────────────────
    //
    // A burying method becomes two METHODS, not two functions: the dispatch reads `one's <field>`
    // exactly as the body it came from did, so the receiver has to still be there to resolve
    // against. What each test below really asks is whether the receiver survived the split.

    private const string TickerObject = """
        Define object ticker with (the number first-beat, the text label):
            Bind number to ticks, given (the rabbit helper):
                Define next as one's first-beat.
                Repeat:
                    Have helper bury next.
                    The next becomes next + 1.
                Until false.
            Done.

            Bind text to describe:
                Return one's label.
            Done.
        Done.
        """;

    [Fact]
    public void AMethodCanBury_AndReadsItsOwnFields()
    {
        Assert.Equal("5\n6\n7", Run(TickerObject + """

            Pull a rabbit as hopper.
                Define clock as a new ticker { the first-beat 5, the label "clock" }.
                Define beats as cast ticks on (clock, hopper).
                State unbury beats.
                State unbury beats.
                State unbury beats.
            Done.
            """));
    }

    [Fact]
    public void TwoInstances_EachGetTheirOwnPlaceToStand()
    {
        // ★ The receiver is captured by the closure, so the state belongs to the INSTANCE rather
        // than to the method. Two tickers hand back two stashes that know nothing of each other.
        Assert.Equal("1\n100\n2\n101", Run(TickerObject + """

            Pull a rabbit as hopper.
                Define low  as a new ticker { the first-beat 1,   the label "low" }.
                Define high as a new ticker { the first-beat 100, the label "high" }.
                Define low-beats  as cast ticks on (low, hopper).
                Define high-beats as cast ticks on (high, hopper).
                State unbury low-beats.
                State unbury high-beats.
                State unbury low-beats.
                State unbury high-beats.
            Done.
            """));
    }

    [Fact]
    public void AnOrdinaryMethodStillWorksBesideABuryingOne()
    {
        // The rewrite replaces one method and leaves the rest of the type alone.
        Assert.Equal("clock: 5\nclock: 6", Run(TickerObject + """

            Pull a rabbit as hopper.
                Define clock as a new ticker { the first-beat 5, the label "clock" }.
                Define beats as cast ticks on (clock, hopper).
                Define taken as 0.
                For each beat in beats, repeat:
                    If taken is 2:
                        Stop.
                    Done.
                    State (cast describe on (clock)) joined to ": " joined to (beat converted to text).
                    The taken becomes taken + 1.
                Done.
            Done.
            """));
    }

    [Fact]
    public void AnUntoMethodCanBuryToo()
    {
        // ⚠ An `unto` method is a method written at the top level, and only its SIGNATURE is
        // registered on the type — it is never moved into the definition's method list. So it needs
        // its own arm in the rewrite, and both halves have to keep the `unto` or they land as free
        // functions with no receiver to resolve against.
        Assert.Equal("1\n3\n5", Run(TickerObject + """

            Bind number to every-other unto ticker, given (the rabbit helper):
                Define next as one's first-beat.
                Repeat:
                    Have helper bury next.
                    The next becomes next + 2.
                Until false.
            Done.

            Pull a rabbit as hopper.
                Define low as a new ticker { the first-beat 1, the label "low" }.
                Define odds as cast every-other on (low, hopper).
                For each odd-beat in odds, repeat:
                    If odd-beat is greater than 5:
                        Stop.
                    Done.
                    State odd-beat.
                Done.
            Done.
            """));
    }

    [Fact]
    public void ABuryingMethodWithNoDeclaredType_SaysWhatIsMissing()
    {
        Assert.Contains("has to say what kind", Assert.Throws<TypeException>(() => Run("""
            Define object ticker with (the number first-beat):
                Bind void to ticks, given (the rabbit helper):
                    Have helper bury one's first-beat.
                Done.
            Done.

            Pull a rabbit as hopper.
                Define clock as a new ticker { the first-beat 5 }.
                State unbury (cast ticks on (clock, hopper)).
            Done.
            """)).Message);
    }

    [Fact]
    public void TwoTypesMayEachHaveATicks_OnlyOneOfThemBurying()
    {
        // ⚠ Why burying METHODS are tracked by (type, method) rather than by name. A single
        // name-keyed set would answer "buries" for both of these, and the ordinary one would be
        // rewritten into a state machine — or be told its `number` return type is a stash.
        Assert.Equal("5\n6\n42", Run("""
            Define object generator with (the number first-beat):
                Bind number to ticks, given (the rabbit helper):
                    Define next as one's first-beat.
                    Repeat:
                        Have helper bury next.
                        The next becomes next + 1.
                    Until false.
                Done.
            Done.

            Define object plain with (the number held):
                Bind number to ticks:
                    Return one's held.
                Done.
            Done.

            Pull a rabbit as hopper.
                Define gen as a new generator { the first-beat 5 }.
                Define beats as cast ticks on (gen, hopper).
                State unbury beats.
                State unbury beats.

                Define flat as a new plain { the held 42 }.
                State cast ticks on (flat).
            Done.
            """));
    }
}
