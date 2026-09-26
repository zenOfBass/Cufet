using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

// `void` written as a TYPE, and `at` used as an index — two gaps closed on 2026-09-26.
//
// ★ Everything past the parser already expected `void` as a type: `(T or void)` normalised to a
// voidable, the compiler's `is a` had an arm for it. No path produced one, so `x is a void`,
// `(number or void)` and a Judge arm `A void` all died as "expected type name".
public class VoidAsATypeTests
{
    private static string Run(string source)
    {
        var program = new Parser(new CufetLexer(source).Tokenize()).Parse();
        program = new TypeChecker().Check(program);
        var output = new StringWriter();
        new Interpreter(output).Execute(program);
        return output.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    private const string Lookup =
        "Define count as the entry for \"kale\" in a map with (\"carrots\" : 12).\n";

    [Fact]
    public void IsAVoid_Tests_ForAbsence()
    {
        Assert.Equal("none", Run(Lookup + "If count is a void, state \"none\".\nOtherwise, state \"some\"."));
    }

    [Fact]
    public void NumberOrVoid_IsAVoidableNumber()
    {
        Assert.Equal("3", Run("Define the (number or void) thing as 3.\nState thing."));
    }

    [Fact]
    public void AJudgeOverAVoidable_ThatMissesVoid_IsRefused()
    {
        // Coverage is proved over the two cases, so a missing one is named rather than assumed.
        var program = new Parser(new CufetLexer(Lookup
            + "Judge count, where it is:\n    A number, state \"some\".\nDone.").Tokenize()).Parse();
        var ex = Assert.Throws<TypeException>(() => new TypeChecker().Check(program));
        Assert.Contains("this judgement does not cover void", ex.Message);
    }

    [Fact]
    public void AtIsAnIndexWhenNoBracketFollows()
    {
        // ⚠ `at` is a free name, but `item at of digits` was read as the matrix form
        // `item at (row, column) of m` on the word alone — the one position it could not be used in.
        Assert.Equal("8\n(7, 5, 9)", Run(
            "Define digits as a series of number with (7, 8, 9).\n"
          + "Define at as 2.\n"
          + "State item at of digits.\n"
          + "The item at of digits becomes 5.\n"
          + "State digits."));
    }

    [Fact]
    public void TheMatrixFormStillWorks()
    {
        Assert.Equal("7", Run(
            "Pull a book on collections.\n"
          + "    Define grid as a matrix with 2 by 2 filled with 0.\n"
          + "    The item at (1, 2) of grid becomes 7.\n"
          + "    State item at (1, 2) of grid.\n"
          + "Done."));
    }
}
