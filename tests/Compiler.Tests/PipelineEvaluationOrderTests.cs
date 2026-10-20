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
}
