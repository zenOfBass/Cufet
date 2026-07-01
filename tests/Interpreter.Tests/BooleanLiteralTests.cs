using Cufet.Interpreter;
using Cufet.Lexer;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

// Acceptance tests for true/false as boolean (fact) literals.
// Before this slice, `true` and `false` were undefined identifiers — using them
// caused a runtime "variable not defined" error. Now they are reserved keywords
// that produce fact values, exactly like 5 produces a number.
public class BooleanLiteralTests
{
    private static string Run(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
        var output  = new StringWriter();
        new Interpreter(output).Execute(program);
        return output.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    private static void ExpectTypeError(string source) =>
        Assert.ThrowsAny<TypeException>(() =>
        {
            var tokens  = new CufetLexer(source).Tokenize();
            var program = new Parser(tokens).Parse();
            new TypeChecker().Check(program);
        });

    private static void ExpectParseError(string source) =>
        Assert.ThrowsAny<ParseException>(() => new Parser(new CufetLexer(source).Tokenize()).Parse());

    // ── The trap is gone ─────────────────────────────────────────────────────

    [Fact]
    public void ReturnTrue_Works()
    {
        // The #1 trap: `return true.` used to give a runtime "variable 'true' is not defined".
        Assert.Equal("yes", Run("""
            Bind fact to is-ready:
                return true.
            Done.
            Define result as Cast is-ready on ().
            If result, State "yes". Otherwise, State "no".
            """));
    }

    [Fact]
    public void ReturnFalse_Works()
    {
        Assert.Equal("no", Run("""
            Bind fact to is-ready:
                return false.
            Done.
            Define result as Cast is-ready on ().
            If result, State "yes". Otherwise, State "no".
            """));
    }

    [Fact]
    public void IfResultIsFalse_Works()
    {
        // `If result is false:` — natural pattern that was previously a runtime error.
        Assert.Equal("stopped", Run("""
            Define result as false.
            If result is false:
                State "stopped".
            Done.
            """));
    }

    [Fact]
    public void DefineFinishedAsFalse_Works()
    {
        Assert.Equal("done", Run("""
            Define finished as false.
            finished becomes true.
            If finished, State "done". Otherwise, State "not done".
            """));
    }

    [Fact]
    public void WhileFlagIsTrue_Works()
    {
        Assert.Equal("3", Run("""
            Define count as 0.
            Define keep-going as true.
            While keep-going is true, repeat:
                count becomes count + 1.
                If count is 3, keep-going becomes false.
            Done.
            State count.
            """));
    }

    // ── Type is fact ─────────────────────────────────────────────────────────

    [Fact]
    public void True_TypeChecksAsFact()
    {
        // Assigning true to a fact-typed binding: type-checks cleanly.
        Assert.Equal("true", Run("Define b as true. State b."));
    }

    [Fact]
    public void False_TypeChecksAsFact()
    {
        Assert.Equal("false", Run("Define b as false. State b."));
    }

    [Fact]
    public void True_RejectsNonBooleanContext()
    {
        // Adding a fact to a number is a type error.
        ExpectTypeError("Define x as true + 1.");
    }

    // ── Works in all boolean contexts ────────────────────────────────────────

    [Fact]
    public void True_InExpressionPosition()
    {
        // Expression position: Define b as true.
        Assert.Equal("true", Run("Define b as true. State b."));
    }

    [Fact]
    public void False_InExpressionPosition()
    {
        Assert.Equal("false", Run("Define b as false. State b."));
    }

    [Fact]
    public void True_InConditionPosition()
    {
        // Condition position: If true, ...
        Assert.Equal("yes", Run("If true, State \"yes\". Otherwise, State \"no\"."));
    }

    [Fact]
    public void False_InConditionPosition()
    {
        Assert.Equal("no", Run("If false, State \"yes\". Otherwise, State \"no\"."));
    }

    [Fact]
    public void True_InWhileCondition()
    {
        // While true terminates via an early Stop.
        Assert.Equal("1", Run("""
            Define x as 0.
            While true, repeat:
                x becomes x + 1.
                Stop.
            Done.
            State x.
            """));
    }

    // ── Composes with comparisons ─────────────────────────────────────────────

    [Fact]
    public void ComparisonResultIsTrue_Equals_LiteralTrue()
    {
        // (x > 5) and true produce the same boolean; comparing them works.
        Assert.Equal(Run("State (5 > 3)."), Run("State true."));
        Assert.Equal(Run("State (5 > 9)."), Run("State false."));
    }

    [Fact]
    public void If_ComparisonIsTrue_Works()
    {
        Assert.Equal("yes", Run("Define x as 4. If (x > 5) is false, State \"yes\"."));
    }

    [Fact]
    public void True_And_False_LogicWorks()
    {
        Assert.Equal("false", Run("State (true and false)."));
        Assert.Equal("true",  Run("State (true or false)."));
    }

    // ── Reserved: can't be used as variable/field names ───────────────────────

    [Fact]
    public void DefineTrueAsNumber_IsParseError()
    {
        // `true` is now a keyword — using it as a variable name is a parse error.
        ExpectParseError("Define true as 5.");
    }

    [Fact]
    public void DefineFalseAsNumber_IsParseError()
    {
        ExpectParseError("Define false as 5.");
    }

    // ── Old workaround still works (not broken) ───────────────────────────────

    [Fact]
    public void OldWorkaround_1Eq1_StillProducesTrueFact()
    {
        // 1 = 1 is a valid comparison that produces a true fact — not broken.
        Assert.Equal(Run("State (1 = 1)."), Run("State true."));
    }

    [Fact]
    public void OldWorkaround_1Eq0_StillProducesFalseFact()
    {
        Assert.Equal(Run("State (1 = 0)."), Run("State false."));
    }
}
