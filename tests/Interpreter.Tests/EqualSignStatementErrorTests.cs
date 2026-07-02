using Cufet.Interpreter;
using Cufet.Lexer;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

// Tests for the '=' in statement position educational error (#3).
// 'x = 5.' as a statement gives a clear error pointing to 'becomes' and 'Define...as'.
// '=' stays comparison-only everywhere else — no footgun.
public class EqualSignStatementErrorTests
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

    private static ParseException ParseFails(string source)
    {
        var tokens = new CufetLexer(source).Tokenize();
        return Assert.Throws<ParseException>(() => new Parser(tokens).Parse());
    }

    // ── Educational error fires for assignment-mistake shape ──────────────────

    [Fact]
    public void EqualsStatement_GivesEducationalError()
    {
        var ex = ParseFails("Define x as 0. x = 5.");
        Assert.Contains("comparison, not assignment", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EqualsStatement_SuggestsBecomesForm()
    {
        var ex = ParseFails("Define x as 0. x = 5.");
        Assert.Contains("becomes", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EqualsStatement_SuggestsDefineAsForm()
    {
        var ex = ParseFails("Define x as 0. x = 5.");
        Assert.Contains("Define", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EqualsStatement_NamedInError()
    {
        var ex = ParseFails("Define count as 0. count = 10.");
        Assert.Contains("count", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EqualsStatement_AsFirstStatement()
    {
        // Even without a prior Define — the educational error fires at parse time.
        var ex = ParseFails("score = 100.");
        Assert.Contains("comparison, not assignment", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── '=' still works as comparison in valid positions ──────────────────────

    [Fact]
    public void Equal_AsComparisonInIf_StillWorks()
    {
        Assert.Equal("yes", Run("Define x as 5. If x = 5, State \"yes\"."));
    }

    [Fact]
    public void Equal_AsComparisonInDefine_StillWorks()
    {
        Assert.Equal("true", Run("Define x as 5. Define b as (x = 5). State b."));
    }

    [Fact]
    public void Equal_AsComparisonInWhile_StillWorks()
    {
        Assert.Equal("5", Run("""
            Define n as 0.
            While n = 0, repeat:
                n becomes 5.
            Done.
            State n.
            """));
    }

    [Fact]
    public void Equal_AsComparisonInReturn_StillWorks()
    {
        Assert.Equal("true", Run("""
            Bind fact to is-five, given (the number x):
                return x = 5.
            Done.
            State Cast is-five on (5).
            """));
    }

    // ── Correct forms work fine ───────────────────────────────────────────────

    [Fact]
    public void BecomesForm_Works()
    {
        Assert.Equal("5", Run("Define x as 0. x becomes 5. State x."));
    }

    [Fact]
    public void DefineAsForm_Works()
    {
        Assert.Equal("5", Run("Define x as 5. State x."));
    }
}
