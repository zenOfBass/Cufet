using Cufet.Interpreter;
using Xunit;
namespace Cufet.Compiler.Tests;

/// <summary>One slice of the pipeline oracle suite — see PipelineTestBase for why it is split.</summary>
///
/// Cufet source with holes: `Define cufet x, given (…), as […]`, filled by `Cite x on (…)` before
/// the program runs. A hole takes a name the block makes (a term), a name it calls (which says what
/// it is), or a literal. Everything a block holds is checked; a mistake in the block is blamed on
/// the block's line, and a fill that does not fit on the `Cite`.
public class PipelineCufetHoleTests : PipelineTestBase
{
    private const string Walker = """
        Define object walker with (the number place):
            Bind number to peek:
                Return one's place.
            Done.
            Bind number or failure to atom:
                One's place becomes one's place + 1.
                Return one's place.
            Done.
            Bind text to wrong:
                Return "".
            Done.
        Done.

        """;

    private const string Rung = """
        Pull a book on cufet.
            Define cufet rung, given (the term level, the number or failure function next unto walker, the text kind), as [
                Bind number or failure to level unto walker:
                    Define left as cast one's next or pass the failure off.
                    If kind is "x", return left + 1.
                    Return left.
                Done.
            ].

        """;

    private static TypeException Refused(string source) =>
        Assert.Throws<TypeException>(() => GenerateC(source));

    /// <remarks>
    /// ★ What a function cannot do. A method cannot be passed, and a lambda calling one works on a
    /// snapshot of the object, so a step taken through it is lost. A filled block is the method as
    /// if written out, so `place` is the object's own: the count at the end is the proof.
    /// </remarks>
    [Fact]
    public void AFilledBlock_MakesMethodsThatStepTheObjectItself()
    {
        var src = Walker + Rung + """
                Cite rung on ("first", atom, "x").
                Cite rung on ("second", first, "y").
            Done.
            Define the reader as a new walker { the place 0 }.
            State cast reader's first but on failure 0.
            State cast reader's second but on failure 0.
            State reader's place.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("2\n3\n2", Interpret(src));
    }

    /// <remarks>
    /// ⚠ Holes are cut in one pass, as C source's are: what fills a hole is never searched. Here
    /// the term fills `made` with the name of the OTHER hole, and a rename done in a loop would
    /// fill it again and call itself.
    /// </remarks>
    [Fact]
    public void AFillSpellingAnotherHole_IsNotFilledAgain()
    {
        const string src = """
            Bind number to source:
                Return 7.
            Done.
            Pull a book on cufet.
                Define cufet s, given (the term made, the number function called), as [
                    Bind number to made:
                        Return cast called + 1.
                    Done.
                ].
                Cite s on ("called", source).
            Done.
            State cast called.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("8", Interpret(src));
    }

    [Fact]
    public void ValuesAndATermOnALocal_FillOnBothBackends()
    {
        const string src = """
            Pull a book on cufet.
                Define cufet doubled, given (the term made, the number n, the text who, the fact loud), as [
                    Bind text to made:
                        Define kept as 0.
                        If loud, the kept becomes n * 2.
                        Return "{who} {kept}".
                    Done.
                ].
                Define cufet tally, given (the term made, the number n), as [
                    Define the made as n.
                ].
                Cite doubled on ("first", 4, "one", true).
                Cite doubled on ("second", -1, "two", true).
                Cite doubled on ("third", 9, "three", false).

                Bind number to counted:
                    Cite tally on ("seen", 5).
                    The seen becomes seen + 1.
                    Return seen.
                Done.
            Done.
            State cast first.
            State cast second.
            State cast third.
            State cast counted.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("one 8\ntwo -2\nthree 0\n6", Interpret(src));
    }

    /// <remarks>
    /// One block filled with two functions: each copy reaches its own.
    /// </remarks>
    [Fact]
    public void TwoCopies_KeepTheirOwnCalls()
    {
        const string src = """
            Bind number to one-more, given (the number n):
                Return n + 1.
            Done.
            Bind number to two-more, given (the number n):
                Return n + 2.
            Done.
            Pull a book on cufet.
                Define cufet applied, given (the term made, the number function step given (the number)), as [
                    Bind number to made, given (the number n):
                        Return cast step on (n) * 10.
                    Done.
                ].
                Cite applied on ("by-one", one-more).
                Cite applied on ("by-two", two-more).
            Done.
            State cast by-one on (1).
            State cast by-two on (1).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("20\n30", Interpret(src));
    }

    /// <remarks>★ The promise: a mistake in a block is blamed on the block's own line.</remarks>
    [Fact]
    public void AMistakeInACitedBlock_IsBlamedOnTheBlock()
    {
        var e = Refused(Walker + Rung.Replace("Return left.", "Return left + \"oops\".") + """
                Cite rung on ("first", atom, "x").
            Done.
            """);
        Assert.Contains("arithmetic", e.Message);
        Assert.Equal(18, e.Line);
    }

    /// <remarks>
    /// ★ A block nobody cites is still checked, against what its `given` says. An editor checks the
    /// open file as the entry, so a block is often uncited where it is read.
    /// </remarks>
    [Fact]
    public void AMistakeInAnUncitedBlock_IsStillRefused()
    {
        var e = Refused(Walker + Rung.Replace("Return left.", "Return left + \"oops\".") + "Done.\nState 1.\n");
        Assert.Contains("arithmetic", e.Message);
        Assert.Equal(18, e.Line);
    }

    [Fact]
    public void AnUncitedBlockCallingAHoleWrongly_IsRefused()
    {
        var e = Refused(Walker + Rung.Replace("cast one's next or", "cast one's next on (1) or") + "Done.\nState 1.\n");
        Assert.Contains("expects 0 argument", e.Message);
    }

    [Fact]
    public void ARightUncitedBlock_ChangesNothing()
    {
        var src = Walker + Rung + "Done.\nState 1.\n";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("1", Interpret(src));
    }

    [Theory]
    [InlineData("""Cite rung on ("first", atom).""",             "'rung' has 3 holes, and this fills 2")]
    [InlineData("""Cite rung.""",                                "'rung' has 3 holes, and this fills 0")]
    [InlineData("""Cite rung on ("two words", atom, "x").""",    "'two words' cannot name anything")]
    [InlineData("""Cite rung on ("if", atom, "x").""",           "'if' cannot name anything")]
    [InlineData("""Cite rung on (first, atom, "x").""",          "'level' in 'rung' is filled with a name in quotes")]
    [InlineData("""Cite rung on ("first", "atom", "x").""",      "'next' in 'rung' is filled with a name, written bare")]
    [InlineData("""Cite rung on ("first", atom, 1).""",          "'kind' in 'rung' is filled with a text written out")]
    [InlineData("""Cite rung on ("first", absent, "x").""",      "there is no method of 'walker' called 'absent'")]
    [InlineData("""Cite rung on ("first", wrong, "x").""",       "'wrong' does not fit 'next'")]
    public void AFillThatDoesNotFit_IsRefusedAtTheCite(string cite, string message)
    {
        var e = Refused(Walker + Rung + "    " + cite + "\nDone.\n");
        Assert.Contains(message, e.Message);
        Assert.Equal(21, e.Line);
    }

    [Theory]
    [InlineData("(the term level), as [Define the tally as level.]",              "'level' is a term, so 's' has to make it")]
    [InlineData("(the series of number xs), as [Define the tally as xs.]",        "'xs' cannot be a hole")]
    [InlineData("(the number n), as [Define the tally as 0.]",                    "'s' never uses its hole 'n'")]
    [InlineData("(the number n, the text n), as [Define the tally as n.]",        "'s' has two holes called 'n'")]
    [InlineData("(the number n), as [Bind number to f: Define n as 1. Return n. Done.]", "'s' declares 'n' again")]
    [InlineData("(), as [Define the tally as 0.]",                                "'s' has a 'given' that names nothing")]
    public void AHoleTheBlockDoesNotHonour_IsRefusedAtTheBlock(string rest, string message)
    {
        var e = Refused($"Pull a book on cufet.\n    Define cufet s, given {rest}.\nDone.\nState 1.\n");
        Assert.Contains(message, e.Message);
        Assert.Equal(2, e.Line);
    }

    [Fact]
    public void ABlockWithNoHoles_TakesNoFill()
    {
        var e = Refused("""
            Pull a book on cufet.
                Define cufet plain as [Define the tally as 0.].
                Cite plain on (1).
            Done.
            """);
        Assert.Contains("'plain' has no holes", e.Message);
        Assert.Equal(3, e.Line);
    }

    /// <remarks>⚠ These two spellings belong to a hole, which is filled with a name.</remarks>
    [Theory]
    [InlineData("the number function next unto walker")]
    [InlineData("the number or failure function next")]
    public void AHolesSpellings_StayOutOfAnOrdinaryGiven(string parameter)
    {
        Assert.ThrowsAny<Exception>(() => GenerateC(
            Walker + "Bind number to f, given (" + parameter + "):\n    Return 1.\nDone.\n"));
    }
}
