using Cufet.Interpreter;
using Cufet.Lexer;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

// Tests for the educational error emitted when a top-level function references a
// top-level Defined value. The semantics are unchanged (the reference still fails); the
// error teaches the fix instead of misdirecting with "X isn't defined".
//
// ★ The refusal moved from RUN TIME to CHECK TIME. It used to live only in the interpreter, so
// one program got three answers: `check` reported no problems, running it refused, and compiling
// it emitted undeclared C and blamed the compiler. The rule now lives in the TypeChecker, which
// both backends run, so they refuse identically and `check` catches it.
//
// These tests therefore assert on the CHECKER. What they are really protecting is the message —
// that it names the variable, explains the rule, and offers both fixes — so the assertions below
// are unchanged; only where the exception comes from moved.
public class TopLevelDataScopeErrorTests
{
    private static TypeException RunFails(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        return Assert.Throws<TypeException>(() => new TypeChecker().Check(program));
    }

    // ⚠ There was a RunFailsAtRuntime here, for "the cases the checker still cannot see". There are
    // no such cases left in this file: an unresolvable name in a body is refused when the program
    // is checked, so every test here goes through RunFails.

    private static string Run(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        program = new TypeChecker().Check(program);
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

    // ★ The control: a name never defined anywhere is not the same case as one deliberately hidden
    // from a top-level function, and it must not borrow that message.
    //
    // ⚠⚠ This test used to assert a RUNTIME failure, and its note explained why — the checker
    // inferred the name to null and left it to the interpreter, so keeping this on the runtime path
    // proved the hidden-data refusal fired for hidden data specifically. That distinction is GONE
    // as of 2026-08-21: a body resolves the names it can see where it is WRITTEN, plus modules its
    // caller pulled, so a name that is neither is refused when the program is checked. Both cases
    // are check-time now, and they are still told apart by their messages, which is what the
    // assertions below check.
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
