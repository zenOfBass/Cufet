using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

/// <summary>
/// A name written with a space in it — `my answer` — says so, wherever it lands.
/// </summary>
/// <remarks>
/// ⚠⚠ Found writing the tutorial's lesson on names. In a `Define` it is the typed form
/// (`Define number answer as 7.`) with a type nobody defined, and was refused with
/// `'my' is not a defined type` — with NO LINE, because the resolver that throws it has no position.
/// Anywhere else the parser met the second word: `expected '.', got Identifier "answer"`.
/// </remarks>
public class NameWithASpaceTests
{
    private static TypeException CheckFails(string source)
    {
        var program = new Parser(new CufetLexer(source).Tokenize()).Parse();
        return Assert.Throws<TypeException>(() => new TypeChecker().Check(program));
    }

    private static ParseException ParseFails(string source) =>
        Assert.Throws<ParseException>(() => new Parser(new CufetLexer(source).Tokenize()).Parse());

    [Theory]
    [InlineData("Define my answer as 7.", 1)]
    [InlineData("Define hops as 1.\nDefine total hops as 2.", 2)]
    [InlineData("Bind void to f:\n    Define my answer as 7.\n    State answer.\nDone.\nCast f.", 2)]
    public void ADefineWithAnUnknownType_SaysANameCannotHaveASpace_AndWhere(string source, int line)
    {
        var ex = CheckFails(source);
        Assert.Contains("there's no type called '", ex.Message);
        Assert.Contains($"Here on line {line}, you're trying to define '", ex.Message);
        Assert.Contains("A name can't have a space in it", ex.Message);
        Assert.Equal(line, ex.Line);
    }

    [Fact]
    public void ADefineWithAnUnknownType_OffersBothReadings()
    {
        var ex = CheckFails("Define my answer as 7.");
        Assert.Contains("write it with a dash: 'my-answer'", ex.Message);
        Assert.Contains("If 'my' is meant to be a type, check its spelling, or define 'object my' first.", ex.Message);
    }

    /// <remarks>★ The guard: the typed form itself, with a type that exists, still works.</remarks>
    [Fact]
    public void ATypedDefine_StillChecks()
    {
        var program = new Parser(new CufetLexer("Define number answer as 7.\nState answer.").Tokenize()).Parse();
        new TypeChecker().Check(program);
    }

    [Theory]
    [InlineData("Define big answer as 7.\nState big answer.", 2, 7, "big", "answer")]
    [InlineData("Define answer as 7.\nThe my answer becomes 8.", 2, 5, "my", "answer")]
    [InlineData("Define my number as 7.", 1, 8, "my", "number")]
    public void TwoNamesSideBySide_SayANameCannotHaveASpace(string source, int line, int column, string first, string second)
    {
        var ex = ParseFails(source);
        Assert.Equal($"Line {line}, column {column}: '{first} {second}' is two names side by side. A name can't have "
                   + $"a space in it; if it's one name, write it with a dash: '{first}-{second}'.", ex.Message);
    }

    /// <remarks>★ The guard on keywords: `is` for `becomes` is a different mistake, and `is` is
    /// not followed by the `becomes` that was wanted, so it keeps its own message.</remarks>
    [Fact]
    public void IsForBecomes_IsNotTakenForTwoNames()
    {
        var ex = ParseFails("Define hops as 1.\nThe hops is hops + 1.");
        Assert.DoesNotContain("two names side by side", ex.Message);
    }
}
