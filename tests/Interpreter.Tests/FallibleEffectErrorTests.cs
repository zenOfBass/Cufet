using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

// `void or failure`, and `Cast … or pass the failure off.` — a call made for its effect that can fail.
//
// ★ Both came from writing the Cufet lexer in Cufet: every scanning method was called for its effect
// and could fail, so each returned a meaningless `fact` and each call bound a throwaway name just to
// reach `or pass the failure off`. Thirteen throwaway names in 676 lines.
public class FallibleEffectErrorTests
{
    private const string Check = """
        Bind void or failure to check, given (the number n):
            If n is less than 0, return a failure "negative".
        Done.
        Bind number to plain, given (the number n), n.

        """;

    private static TypeException CheckFails(string source)
    {
        var program = new Parser(new CufetLexer(Check + source).Tokenize()).Parse();
        return Assert.Throws<TypeException>(() => new TypeChecker().Check(program));
    }

    private static void Checks(string source)
    {
        var program = new Parser(new CufetLexer(Check + source).Tokenize()).Parse();
        new TypeChecker().Check(program);
    }

    [Fact]
    public void AVoidOrFailureFunction_MayFallOffItsEndOrReturnBare()
    {
        Checks("""
            Bind void or failure to go, given (the number n):
                If n is 0:
                    Return.
                Done.
                Cast check on (n) or pass the failure off.
            Done.
            """);
    }

    [Fact]
    public void ItsResult_IsNotAValue_EvenInsideATry()
    {
        // ⚠ Inside a Try the checker unwraps a fallible call to its success type, which here is
        // void — and nothing refused that, so this failed only when it RAN.
        var ex = CheckFails("""
            Try to:
                Define x as cast check on (1).
            Done.
            In case of failure:
                State "f".
            Done.
            """);
        Assert.Contains("'check' gives nothing back — it can't be used as a value", ex.Message);
        Assert.Contains("or pass the failure off", ex.Message);
    }

    [Fact]
    public void ButOnFailure_HasNoValueToStandInFor()
    {
        var ex = CheckFails("Define x as cast check on (1) but on failure 0.");
        Assert.Contains("gives nothing back — it can't be used as a value", ex.Message);
    }

    [Fact]
    public void ReturningAValue_FromVoidOrFailure_IsRefused()
    {
        var ex = CheckFails("""
            Bind void or failure to go:
                Return 5.
            Done.
            """);
        Assert.Contains("declared 'void or failure' — when it works it gives nothing back", ex.Message);
        Assert.Contains("Write 'Return.' for success", ex.Message);
    }

    [Fact]
    public void ReturningAFailure_FromAPlainVoid_PointsAtVoidOrFailure()
    {
        var ex = CheckFails("""
            Bind void to go:
                Return a failure "no".
            Done.
            """);
        Assert.Contains("declared void, so it cannot give back a failure", ex.Message);
        Assert.Contains("'Bind void or failure to …'", ex.Message);
    }

    [Fact]
    public void PassingOff_ACallThatCannotFail_IsRefused()
    {
        var ex = CheckFails("""
            Bind void or failure to go:
                Cast plain on (1) or pass the failure off.
            Done.
            """);
        Assert.Contains("'plain' can never fail — there is no failure to pass off", ex.Message);
    }

    [Fact]
    public void PassingOff_InsideATry_IsRefused()
    {
        var ex = CheckFails("""
            Bind void or failure to go:
                Try to:
                    Cast check on (1) or pass the failure off.
                Done.
                In case of failure:
                    State "f".
                Done.
            Done.
            """);
        Assert.Contains("inside 'Try to:', a failure already goes to 'In case of failure'", ex.Message);
    }

    [Theory]
    [InlineData("Bind void to go:\n    Cast check on (1) or pass the failure off.\nDone.")]
    [InlineData("Cast check on (1) or pass the failure off.")]
    public void PassingOff_FromWhereNothingCanFail_SaysHowToDeclareIt(string source)
    {
        var ex = CheckFails(source);
        Assert.Contains("you can only pass a failure off from a function that can fail", ex.Message);
        Assert.Contains("'Bind void or failure to …'", ex.Message);
    }

    [Fact]
    public void AnUnhandledCall_InAFunctionThatCanFail_OffersPassingItOff()
    {
        var ex = CheckFails("""
            Bind void or failure to go:
                Cast check on (1).
            Done.
            """);
        Assert.Contains("'check' can fail — you must handle the failure", ex.Message);
        Assert.Contains("'… or pass the failure off.'", ex.Message);
    }

    [Fact]
    public void AnUnhandledCall_WhereNothingCanFail_OffersOnlyATry()
    {
        // Offering to pass it off here would be advice the next refusal takes back.
        var ex = CheckFails("""
            Bind number to go:
                Cast check on (1).
                Return 1.
            Done.
            """);
        Assert.Contains("Wrap this call in a 'Try to: / In case of failure:' block.", ex.Message);
        Assert.DoesNotContain("pass the failure off", ex.Message);
    }
}
