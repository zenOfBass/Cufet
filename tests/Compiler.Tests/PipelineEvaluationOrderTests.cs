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
}
