using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

// Returning a failure from a function that did not say it can fail.
//
// ⚠ The advice was "Change the returned value to a number" — telling someone who wanted the word
// to be able to fail to stop failing. Found by writing the tutorial's lesson on failure, whose
// natural first step is adding `return a failure` to a word bound the lesson before.
public class FailureDeclarationErrorTests
{
    private static TypeException CheckFails(string source)
    {
        var program = new Parser(new CufetLexer(source).Tokenize()).Parse();
        return Assert.Throws<TypeException>(() => new TypeChecker().Check(program));
    }

    [Fact]
    public void ReturningAFailureFromAPlainFunction_PointsAtTheDeclaration()
    {
        var ex = CheckFails(
            "Bind number to area, given (the number width, the number depth):\n"
          + "    If width is less than 0, return a failure \"a width cannot be negative\".\n"
          + "    Return width * depth.\n"
          + "Done.");
        Assert.Contains("declared to give back a number, and a failure is not one", ex.Message);
        Assert.Contains("'Bind number or failure to …'", ex.Message);
        Assert.DoesNotContain("Change the returned value", ex.Message);
    }

    [Fact]
    public void ReturningTheWrongKindOfValue_KeepsItsOwnWords()
    {
        var ex = CheckFails(
            "Bind number to area, given (the number width):\n"
          + "    Return \"big\".\n"
          + "Done.");
        Assert.Contains("Change the returned value to a number", ex.Message);
    }
}
