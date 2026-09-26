using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

// A name followed by `(` or `of` where a statement should have ended.
//
// ⚠ All three answered `expected '.', got LParen "("` or `got Of "of"` — a token type named at a
// beginner. Found by writing the tutorial's lesson on functions: `area of (4, 5)` is the natural
// guess straight after `math's square-root of (16)`, and `area(4, 5)` is what every other language
// taught. What follows `of` tells the two `of` mistakes apart.
public class CallShapeErrorTests
{
    private static ParseException ParseFails(string source)
    {
        var tokens = new CufetLexer(source).Tokenize();
        return Assert.Throws<ParseException>(() => new Parser(tokens).Parse());
    }

    private const string Area =
        "Bind number to area, given (the number width, the number depth):\n"
      + "    Return width * depth.\nDone.\n";

    [Fact]
    public void BracketsAfterAName_SayHowToCall()
    {
        var ex = ParseFails(Area + "State area(4, 5).");
        Assert.Contains("'area(' looks like a call", ex.Message);
        Assert.Contains("'cast area on (…)'", ex.Message);
        Assert.DoesNotContain("LParen", ex.Message);
    }

    [Fact]
    public void OfWithBrackets_SaysThatIsTheBookForm()
    {
        var ex = ParseFails(Area + "State area of (4, 5).");
        Assert.Contains("is how a BOOK's member is called", ex.Message);
        Assert.Contains("'cast area on (…)'", ex.Message);
        Assert.DoesNotContain("got Of", ex.Message);
    }

    [Fact]
    public void OfWithAName_SaysAFieldNeedsThe()
    {
        var ex = ParseFails(
            "Define hopper as a record with (the name \"Hopper\", the carrots 12).\n"
          + "State carrots of hopper.");
        Assert.Contains("reading a field needs 'the' in front: 'the carrots of …'", ex.Message);
    }

    [Fact]
    public void AnUnrelatedMissingDot_KeepsItsOwnWords()
    {
        // Only the three shapes are reworded; a plain missing '.' still says so.
        var ex = ParseFails("State 1 2.");
        Assert.Contains("expected '.'", ex.Message);
    }
}
