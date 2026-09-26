using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

// A name used after the `Done.` of the block it was defined in.
//
// ⚠ It said "'picked' isn't defined … Define it first" to someone who had, two lines up. Found by
// writing the tutorial's lesson on lifetimes, which is about exactly what a `Done.` lets go of.
public class EndedNameErrorTests
{
    private static TypeException CheckFails(string source)
    {
        var program = new Parser(new CufetLexer(source).Tokenize()).Parse();
        return Assert.Throws<TypeException>(() => new TypeChecker().Check(program));
    }

    [Theory]
    [InlineData("Pull a rabbit as hopper.\n    Define picked as 3.\nDone.\nState picked.")]
    [InlineData("If 1 is 1:\n    Define picked as 3.\nDone.\nState picked.")]
    [InlineData("For each basket in a series with (1, 2), repeat:\n    Define picked as basket.\nDone.\nState picked.")]
    public void ANameUsedAfterItsBlockEnded_SaysSo(string source)
    {
        var ex = CheckFails(source);
        Assert.Contains("'picked' was defined on line 2, inside a block that has ended", ex.Message);
        Assert.DoesNotContain("Define it first", ex.Message);
    }

    [Fact]
    public void ANameNeverDefined_KeepsItsOwnWords()
    {
        var ex = CheckFails("State picked.");
        Assert.Contains("'picked' isn't defined", ex.Message);
    }
}
