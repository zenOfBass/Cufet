using Cufet.Interpreter;
using Cufet.Lexer;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

// Tests for the educational error emitted when a top-level function references a
// top-level Defined value. The semantics are unchanged (reference still fails); the
// error teaches the fix instead of misdirecting with "X isn't defined".
public class TopLevelDataScopeErrorTests
{
    private static RuntimeException RunFails(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
        var output = new StringWriter();
        return Assert.Throws<RuntimeException>(() => new Interpreter(output).Execute(program));
    }

    private static string Run(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
        var output = new StringWriter();
        new Interpreter(output).Execute(program);
        return output.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    // ── Educational error fires for top-level data referenced in top-level function ─

    [Fact]
    public void TopLevelData_ReferencedInFunction_GivesEducationalError()
    {
        var ex = RunFails("""
            Define total as 0.
            Bind void to show:
                State total.
            Done.
            Cast show.
            """);
        Assert.Contains("top-level value", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TopLevelData_ErrorNames_TheVariable()
    {
        var ex = RunFails("""
            Define total as 0.
            Bind void to show:
                State total.
            Done.
            Cast show.
            """);
        Assert.Contains("total", ex.Message);
    }

    [Fact]
    public void TopLevelData_ErrorMentions_Parameter()
    {
        var ex = RunFails("""
            Define config as "prod".
            Bind void to show:
                State config.
            Done.
            Cast show.
            """);
        Assert.Contains("parameter", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TopLevelData_ErrorMentions_Closure()
    {
        var ex = RunFails("""
            Define config as "prod".
            Bind void to show:
                State config.
            Done.
            Cast show.
            """);
        Assert.Contains("closure", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // Error still fires when function is called from inside a rabbit block.
    [Fact]
    public void TopLevelData_CalledFromRabbit_StillEducational()
    {
        var ex = RunFails("""
            Define total as 99.
            Bind void to show:
                State total.
            Done.
            Pull a rabbit.
                Cast show.
            Done.
            """);
        Assert.Contains("top-level value", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // Error fires when the reference is nested inside a function called from a top-level function.
    [Fact]
    public void TopLevelData_InNestedCall_StillEducational()
    {
        var ex = RunFails("""
            Define total as 42.
            Bind void to inner:
                State total.
            Done.
            Bind void to outer:
                Cast inner.
            Done.
            Cast outer.
            """);
        Assert.Contains("top-level value", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Genuine undefined still gives the plain error ─────────────────────────────

    [Fact]
    public void GenuineUndefined_GivesPlainError()
    {
        var ex = RunFails("""
            Bind void to show:
                State nonexistent.
            Done.
            Cast show.
            """);
        Assert.Contains("isn't defined", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("top-level value", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── The fix patterns work ──────────────────────────────────────────────────────

    // Parameter fix: passing the data explicitly works.
    [Fact]
    public void Fix_PassAsParameter_Works()
    {
        Assert.Equal("42", Run("""
            Define total as 42.
            Bind void to show, given (the number total):
                State total.
            Done.
            Cast show on (total).
            """));
    }

    // Closure fix: wrapper function creates a closure that captures the data.
    [Fact]
    public void Fix_ClosureWrapper_Works()
    {
        Assert.Equal("42", Run("""
            Define total as 42.
            Bind void to run-with-total, given (the number t):
                Bind void to show:
                    State t.
                Done.
                Cast show.
            Done.
            Cast run-with-total on (total).
            """));
    }

    // Top-level functions can still call each other (mutual recursion still works).
    [Fact]
    public void MutualRecursion_BetweenTopLevelFunctions_Works()
    {
        Assert.Equal("done", Run("""
            Bind void to ping, given (the number n):
                If n = 0, return.
                Cast pong on (n - 1).
            Done.
            Bind void to pong, given (the number n):
                If n = 0:
                    State "done".
                    return.
                Done.
                Cast ping on (n - 1).
            Done.
            Cast ping on (3).
            """));
    }
}
