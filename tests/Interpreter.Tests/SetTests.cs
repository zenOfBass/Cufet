using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

/// <summary>
/// `a set of T` — membership without a value.
/// </summary>
/// <remarks>
/// <para>
/// ★★ A set is a MAP WITH THE VALUE REMOVED, and it is stored as exactly that: the element keyed to
/// itself. Everything about which types may be an element, how they compare, and that iteration
/// follows insertion order is the map's answer already — so these tests are about the surface and
/// the refusals, not about re-proving hashing.
/// </para>
/// <para>
/// ★ The trigger was `dijkstra.cufe`, which declared `a map from text to number`, wrote `1` into it
/// and never read the value. That is a set with a placeholder stapled on, and it is now a set.
/// </para>
/// </remarks>
public class SetTests
{
    private static string Run(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        program = new TypeChecker().Check(program);
        var output  = new System.IO.StringWriter();
        new Interpreter(output).Execute(program);
        return output.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    private const string Book = "Pull a book on collections.\n";

    // ── Holding each thing once ───────────────────────────────────────────

    [Fact]
    public void ASetHoldsEachThingOnce()
    {
        // ★ The whole difference from a series. Inserting what is already there is not an error and
        // not a second copy — it is the operation doing what it says.
        Assert.Equal("2", Run(Book + """
                Define seen as a set of text.
                Insert "adam" into seen.
                Insert "zoe" into seen.
                Insert "adam" into seen.
                State the number of seen.
            Done.
            """));
    }

    [Fact]
    public void ASetAnswersWhetherItHoldsSomething()
    {
        Assert.Equal("yes\nno", Run(Book + """
                Define seen as a set of text with ("zoe").
                If seen has "zoe", state "yes".
                If not (seen has "mira"), state "no".
            Done.
            """));
    }

    [Fact]
    public void ASetIteratesItsElementsInInsertionOrder()
    {
        // ⚠ The order is not incidental — it is what a map does on BOTH backends, measured, and a
        // set is stored as a map. An unordered answer here would be an oracle divergence waiting.
        Assert.Equal("zoe\nadam\nmira", Run(Book + """
                Define seen as a set of text with ("zoe", "adam").
                Insert "mira" into seen.
                Insert "zoe" into seen.
                For each name in seen, repeat:
                    State name.
                Done.
            Done.
            """));
    }

    [Fact]
    public void AnEmptySetCountsZero()
    {
        Assert.Equal("0", Run(Book + """
                Define seen as a set of text.
                State the number of seen.
            Done.
            """));
    }

    [Fact]
    public void ASetOfNumbersWorksTheSameWay()
    {
        Assert.Equal("3", Run(Book + """
                Define seen as a set of number.
                Insert 1 into seen.
                Insert 2 into seen.
                Insert 1 into seen.
                Insert 3 into seen.
                State the number of seen.
            Done.
            """));
    }

    // ── Refusals ──────────────────────────────────────────────────────────

    [Fact]
    public void InsertingTheWrongTypeIsRefused()
    {
        var ex = Assert.Throws<TypeException>(() => Run(Book + """
                Define seen as a set of text.
                Insert 3 into seen.
            Done.
            """));
        Assert.Contains("holds text", ex.Message);
    }

    [Fact]
    public void AskingAMapWithTheSetSpellingSaysWhichWordsItWants()
    {
        // ⚠ NOT "a map is not a set". The writer knows what they are holding; what they need is the
        // other three words.
        var ex = Assert.Throws<TypeException>(() => Run(Book + """
                Define ages as a map from text to number.
                State ages has "zoe".
            Done.
            """));
        Assert.Contains("has a key for", ex.Message);
    }

    [Fact]
    public void AskingASetForItsSizeSaysWhichWordItWants()
    {
        // A set is stored as a map and reads like a collection, so `the size of` is exactly the
        // wrong guess someone will make. The message names the right one rather than the type.
        var ex = Assert.Throws<TypeException>(() => Run(Book + """
                Define seen as a set of text.
                State the size of seen.
            Done.
            """));
        Assert.Contains("the number of seen", ex.Message);
    }

    [Fact]
    public void InsertingAtAPositionInASetIsRefused()
    {
        // ⚠ A set has no positions to insert after. Saying so beats silently ignoring the request,
        // which is what reusing the series verb would otherwise invite.
        var ex = Assert.Throws<TypeException>(() => Run(Book + """
                Define seen as a set of text with ("zoe").
                Insert "adam" after the first item of seen.
            Done.
            """));
        Assert.Contains("no positions", ex.Message);
    }
}
