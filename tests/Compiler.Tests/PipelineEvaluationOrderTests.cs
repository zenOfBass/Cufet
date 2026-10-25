using Xunit;
namespace Cufet.Compiler.Tests;

/// <summary>One slice of the pipeline oracle suite — see PipelineTestBase for why it is split.</summary>
///
/// ★ Operands are worked out LEFT TO RIGHT, as the interpreter works them out. C leaves the order
/// of a call's arguments open, and gcc took them last first, so `cast add on (cast tick on (c),
/// cast tick on (c))` printed 21 built and 12 interpreted, and `a - b` subtracted the wrong way
/// round whenever both sides called something. No corpus program had two operands that both did
/// something, which is why the oracle never saw it. Each program here prints what each call did,
/// so an operand worked out out of turn shows in the output.
public class PipelineEvaluationOrderTests : PipelineTestBase
{
    private const string Tick = """
        Bind number to tick, given (the series of number seen):
            Insert 0 into seen.
            State "tick {the number of seen}".
            Return the number of seen.
        Done.
        Define c as a series of number.

        """;

    [Fact]
    public void ACallsArguments_AreWorkedOutInOrder()
    {
        const string src = Tick + """
            Bind number to add, given (the number x, the number y):
                Return x * 10 + y.
            Done.
            State cast add on (cast tick on (c), cast tick on (c)).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Contains("12", Interpret(src));
    }

    [Fact]
    public void AnOperatorsSides_AreWorkedOutInOrder()
    {
        const string src = Tick + """
            State cast tick on (c) - cast tick on (c).
            State cast tick on (c) is less than cast tick on (c).
            State "{cast tick on (c)} then {cast tick on (c)}".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void AMethodsArguments_AndAFunctionValuesArguments_AreWorkedOutInOrder()
    {
        const string src = Tick + """
            Define object pad with (the number base):
                Bind number to mix, given (the number x, the number y):
                    Return one's base + x * 10 + y.
                Done.
            Done.
            Define p as a new pad { the base 100 }.
            State cast mix on (p, cast tick on (c), cast tick on (c)).
            State cast p's mix on (cast tick on (c), cast tick on (c)).
            Define f as a function given (the number x, the number y): Return x * 10 + y. Done.
            State cast f on (cast tick on (c), cast tick on (c)).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // Inside a Try a fallible call is already lifted into statements of its own, in order, so this
    // one holds the ordering to leaving the jump to the handler intact — and the second argument
    // of a failed call unworked.
    [Fact]
    public void AFailingArgument_InATry_StillStopsTheOnesAfterIt()
    {
        const string src = Tick + """
            Bind number or failure to checked, given (the number x):
                State "checking {x}".
                If x is greater than 2, return a failure "too big".
                Return x.
            Done.
            Bind number to add, given (the number x, the number y):
                Return x * 10 + y.
            Done.
            Try to:
                State cast add on (cast checked on (1), cast checked on (2)).
                State cast add on (cast checked on (3), cast checked on (cast tick on (c))).
            Done.
            In case of failure:
                State "failed: {the message of the failure}".
            Done.
            State the number of c.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void And_StillStopsAtAFalseLeftSide()
    {
        const string src = Tick + """
            If cast tick on (c) is 5 and cast tick on (c) is 2, State "both".
            State the number of c.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // A matrix's parts were the four the 2026-10-16 fix missed: an item written or read, the sized
    // form and scaling each handed C their parts as arguments, so a set worked out its value first.
    [Fact]
    public void AMatrixsParts_AreWorkedOutInOrder()
    {
        const string src = Tick + """
            Pull a book on collections.
                Bind matrix to grid, given (the series of number seen):
                    Insert 0 into seen.
                    State "grid {the number of seen}".
                    Return a matrix with ((1, 2), (3, 4)).
                Done.
                Define m as a matrix with 3 by 3.
                The item at (cast tick on (c), cast tick on (c)) of m becomes cast tick on (c).
                The item at (1, 1) of cast grid on (c) becomes cast tick on (c).
                State m.
                State item at (cast tick on (c) - 4, cast tick on (c) - 4) of m.
                State item at (1, cast tick on (c) - 7) of cast grid on (c).
                State a matrix with cast tick on (c) - 9 by cast tick on (c) - 9 filled with cast tick on (c).
                State cast grid on (c) * cast tick on (c).
                State cast tick on (c) * cast grid on (c).
                Try to:
                    State cast grid on (c) + cast grid on (c).
                Done.
                In case of failure:
                    State "failed".
                Done.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ★ Not every node goes in the order written: the interpreter works out the thing operated on
    // first — the text of `replace … in t` and of `the characters from … of t`, the map of `the
    // entry for k in m`, the series of `Insert v into s`. Every one of these handed C its operands as
    // plain arguments until 2026-10-24, and `cufet build` matched only where gcc happened to agree.
    [Fact]
    public void TextsMapsAndSets_WorkTheirOperandsOut_AsTheInterpreterDoes()
    {
        const string src = """
            Bind number to at, given (the series of number seen, the number made):
                Insert 0 into seen.
                State "at {the number of seen} {made}".
                Return made.
            Done.
            Bind text to word, given (the series of number seen, the text made):
                Insert 0 into seen.
                State "word {the number of seen} {made}".
                Return made.
            Done.
            Bind map from text to number to table, given (the series of number seen, the map from text to number held):
                Insert 0 into seen.
                State "table {the number of seen}".
                Return held.
            Done.
            Bind series of number to row, given (the series of number seen, the series of number held):
                Insert 0 into seen.
                State "row {the number of seen}".
                Return held.
            Done.
            Define c as a series of number.

            State "-- texts".
            If cast word on (c, "hello") contains cast word on (c, "ell"), state "contains".
            If cast word on (c, "hello") starts with cast word on (c, "he"), state "starts".
            If cast word on (c, "hello") ends with cast word on (c, "lo"), state "ends".
            State the position of cast word on (c, "l") in cast word on (c, "hello").
            State cast word on (c, "a,b") split by cast word on (c, ",").
            State replace cast word on (c, "a") with cast word on (c, "X") in cast word on (c, "banana").
            State the characters from cast at on (c, 2) to cast at on (c, 3) of cast word on (c, "hello").
            State the characters from cast at on (c, 4) to the end of cast word on (c, "hello").
            State the first (cast at on (c, 2)) characters of cast word on (c, "hello").
            State the last (cast at on (c, 2)) characters of cast word on (c, "hello").

            State "-- maps".
            Define m as a map with ("a" : 1, "b" : 2).
            State the entry for cast word on (c, "a") in cast table on (c, m).
            State from cast table on (c, m), the entry for cast word on (c, "b").
            If cast table on (c, m) has a key for cast word on (c, "a"), state "key".
            If cast table on (c, m) has an entry for cast word on (c, "a"), state "entry".
            In cast table on (c, m), the entry for cast word on (c, "z") becomes cast at on (c, 9).
            State m.
            Remove cast word on (c, "z") from cast table on (c, m).
            State m.

            State "-- series".
            Define s as a series of number with (1, 2, 3).
            Insert cast at on (c, 7) into cast row on (c, s).
            Insert cast at on (c, 8) after item (cast at on (c, 1)) of cast row on (c, s).
            Insert cast at on (c, 6) into the start of cast row on (c, s).
            Item (cast at on (c, 2)) of cast row on (c, s) becomes cast at on (c, 5).
            State s.
            State item (cast at on (c, 3)) of cast row on (c, s).
            Remove item (cast at on (c, 1)) from cast row on (c, s).
            State s.

            State "-- sets".
            Pull a book on collections.
                Bind set of text to bag, given (the series of number seen, the set of text held):
                    Insert 0 into seen.
                    State "bag {the number of seen}".
                    Return held.
                Done.
                Define seen-words as a set of text with ("a").
                If cast bag on (c, seen-words) has cast word on (c, "a"), state "has".
                Insert cast word on (c, "q") into cast bag on (c, seen-words).
                State seen-words.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ⚠ Worse than out of order: a series read for its length and again for its item pasted its C
    // twice, so `item 1 of cast row on (c, s)` called `row` twice — and so did every chase form,
    // `Item … becomes`, and the value of an `Insert` into a set.
    [Fact]
    public void ACollectionWorkedOutFromACall_IsWorkedOutOnce()
    {
        const string src = """
            Bind number to at, given (the series of number seen, the number made):
                Insert 0 into seen.
                State "at {the number of seen} {made}".
                Return made.
            Done.
            Bind text to word, given (the series of number seen, the text made):
                Insert 0 into seen.
                State "word {the number of seen} {made}".
                Return made.
            Done.
            Bind series of number to row, given (the series of number seen, the series of number held):
                Insert 0 into seen.
                State "row {the number of seen}".
                Return held.
            Done.
            Define c as a series of number.

            State "-- one side a call".
            Define s as a series of number with (1, 2, 3, 2).
            State item 1 of cast row on (c, s).
            State the last of cast row on (c, s).
            Item 1 of cast row on (c, s) becomes 10.
            Insert 4 into cast row on (c, s).
            Remove 2 from cast row on (c, s).
            Remove item 1 from cast row on (c, s).
            State s.

            State "-- remove a value".
            Remove cast at on (c, 3) from cast row on (c, s).
            State s.

            Pull a book on collections.
                Bind chase to buffer, given (the series of number seen, the chase held):
                    Insert 0 into seen.
                    State "buffer {the number of seen}".
                    Return held.
                Done.
                Bind set of text to bag, given (the series of number seen, the set of text held):
                    Insert 0 into seen.
                    State "bag {the number of seen}".
                    Return held.
                Done.
                State "-- chases".
                Define out as a chase.
                Insert cast word on (c, "he") into cast buffer on (c, out).
                Insert cast word on (c, "!") into the start of cast buffer on (c, out).
                Insert cast word on (c, "y") after item (cast at on (c, 2)) of cast buffer on (c, out).
                Insert cast at on (c, 72) into cast buffer on (c, out).
                Insert cast at on (c, 73) after item (cast at on (c, 1)) of cast buffer on (c, out).
                State out converted to text.
                Item (cast at on (c, 1)) of cast buffer on (c, out) becomes cast word on (c, "?").
                The last of cast buffer on (c, out) becomes cast word on (c, "Z").
                State out converted to text.
                State item (cast at on (c, 2)) of cast buffer on (c, out).
                Remove item (cast at on (c, 1)) from cast buffer on (c, out).
                Remove cast word on (c, "y") from cast buffer on (c, out).
                Remove the last from cast buffer on (c, out).
                State out converted to text.

                State "-- one side a call, sets".
                Define seen-words as a set of text with ("a").
                If cast bag on (c, seen-words) has "a", state "has".
                Insert "b" into cast bag on (c, seen-words).
                State seen-words.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }
}
