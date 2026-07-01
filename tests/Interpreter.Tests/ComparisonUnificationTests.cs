using Cufet.Interpreter;
using Cufet.Lexer;
using System.Runtime.ExceptionServices;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

// Acceptance tests for comparison-form unification: both symbol forms (< > <= >= =)
// and word forms (is less than / is greater than / is / is not) work in BOTH expression
// position and condition position. Word forms remain idiomatic; the split is gone.
public class ComparisonUnificationTests
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

    // ── Symbol forms in condition position ───────────────────────────────────

    [Fact]
    public void SymbolLt_InIfCondition()
    {
        Assert.Equal("yes", Run("Define x as 2. If x < 5, State \"yes\". Otherwise, State \"no\"."));
        Assert.Equal("no",  Run("Define x as 7. If x < 5, State \"yes\". Otherwise, State \"no\"."));
    }

    [Fact]
    public void SymbolGt_InIfCondition()
    {
        Assert.Equal("yes", Run("Define x as 7. If x > 5, State \"yes\". Otherwise, State \"no\"."));
        Assert.Equal("no",  Run("Define x as 3. If x > 5, State \"yes\". Otherwise, State \"no\"."));
    }

    [Fact]
    public void SymbolLte_InIfCondition()
    {
        Assert.Equal("yes", Run("Define x as 5. If x <= 5, State \"yes\". Otherwise, State \"no\"."));
        Assert.Equal("no",  Run("Define x as 6. If x <= 5, State \"yes\". Otherwise, State \"no\"."));
    }

    [Fact]
    public void SymbolGte_InIfCondition()
    {
        Assert.Equal("yes", Run("Define x as 5. If x >= 5, State \"yes\". Otherwise, State \"no\"."));
        Assert.Equal("no",  Run("Define x as 4. If x >= 5, State \"yes\". Otherwise, State \"no\"."));
    }

    [Fact]
    public void SymbolEqual_InIfCondition()
    {
        // = in condition position (was previously a hard parse error "use 'is' instead").
        Assert.Equal("yes", Run("Define x as 5. If x = 5, State \"yes\". Otherwise, State \"no\"."));
        Assert.Equal("no",  Run("Define x as 4. If x = 5, State \"yes\". Otherwise, State \"no\"."));
    }

    [Fact]
    public void SymbolLt_InWhileCondition()
    {
        // The canonical "tripped-over" case that motivated this change.
        Assert.Equal("3", Run("""
            Define count as 0.
            While count < 3, repeat:
                count becomes count + 1.
            Done.
            State count.
            """));
    }

    [Fact]
    public void SymbolGte_InWhileCondition()
    {
        Assert.Equal("0", Run("""
            Define n as 3.
            While n >= 1, repeat:
                n becomes n - 1.
            Done.
            State n.
            """));
    }

    [Fact]
    public void SymbolGt_InBlockIfCondition()
    {
        Assert.Equal("big", Run("""
            Define x as 10.
            If x > 5:
                State "big".
            Done.
            """));
    }

    // ── Word forms in expression position ────────────────────────────────────

    [Fact]
    public void WordLessThan_InExpressionPosition()
    {
        // Word-form comparison inside a Define (expression position).
        Assert.Equal("true",  Run("Define x as 2. Define b as (x is less than 5). State b."));
        Assert.Equal("false", Run("Define x as 7. Define b as (x is less than 5). State b."));
    }

    [Fact]
    public void WordGreaterThan_InExpressionPosition()
    {
        Assert.Equal("true",  Run("Define x as 7. Define b as (x is greater than 5). State b."));
        Assert.Equal("false", Run("Define x as 3. Define b as (x is greater than 5). State b."));
    }

    [Fact]
    public void WordIs_InExpressionPosition()
    {
        // "is" as equality in expression position.
        Assert.Equal("true",  Run("Define x as 5. Define b as (x is 5). State b."));
        Assert.Equal("false", Run("Define x as 4. Define b as (x is 5). State b."));
    }

    [Fact]
    public void WordIsNot_InExpressionPosition()
    {
        Assert.Equal("true",  Run("Define x as 4. Define b as (x is not 5). State b."));
        Assert.Equal("false", Run("Define x as 5. Define b as (x is not 5). State b."));
    }

    [Fact]
    public void WordOrMore_InExpressionPosition()
    {
        Assert.Equal("true",  Run("Define x as 5. Define b as (x is 5 or more). State b."));
        Assert.Equal("false", Run("Define x as 4. Define b as (x is 5 or more). State b."));
    }

    [Fact]
    public void WordOrLess_InExpressionPosition()
    {
        Assert.Equal("true",  Run("Define x as 5. Define b as (x is 5 or less). State b."));
        Assert.Equal("false", Run("Define x as 6. Define b as (x is 5 or less). State b."));
    }

    // ── Equivalence: both forms produce the same boolean ─────────────────────

    [Fact]
    public void SymbolAndWordForms_AreEquivalent_Lt()
    {
        const string sym  = "Define x as 2. Define b as (x < 5). State b.";
        const string word = "Define x as 2. Define b as (x is less than 5). State b.";
        Assert.Equal(Run(sym), Run(word));
    }

    [Fact]
    public void SymbolAndWordForms_AreEquivalent_Gt()
    {
        const string sym  = "Define x as 7. Define b as (x > 5). State b.";
        const string word = "Define x as 7. Define b as (x is greater than 5). State b.";
        Assert.Equal(Run(sym), Run(word));
    }

    [Fact]
    public void SymbolAndWordForms_AreEquivalent_Equality()
    {
        const string sym  = "Define x as 5. Define b as (x = 5). State b.";
        const string word = "Define x as 5. Define b as (x is 5). State b.";
        Assert.Equal(Run(sym), Run(word));
    }

    // ── = and is both mean equality everywhere ────────────────────────────────

    [Fact]
    public void EqualSign_AndIs_BothWork_InCondition()
    {
        Assert.Equal("yes", Run("If 5 = 5, State \"yes\"."));
        Assert.Equal("yes", Run("If 5 is 5, State \"yes\"."));
    }

    [Fact]
    public void EqualSign_AndIs_BothWork_InExpression()
    {
        Assert.Equal(Run("State (5 = 5)."), Run("State (5 is 5)."));
    }

    // ── Existing idiomatic usage unaffected ───────────────────────────────────

    [Fact]
    public void WordForm_InCondition_StillWorks()
    {
        // The idiomatic style (word-forms in condition position) is unchanged.
        Assert.Equal("yes", Run("Define x as 2. If x is less than 5, State \"yes\". Otherwise, State \"no\"."));
        Assert.Equal("yes", Run("Define x as 7. If x is greater than 5, State \"yes\". Otherwise, State \"no\"."));
        Assert.Equal("yes", Run("Define x as 5. If x is 5, State \"yes\"."));
    }

    [Fact]
    public void SymbolForm_InExpression_StillWorks()
    {
        // The pre-existing symbol-in-expression usage is unchanged.
        Assert.Equal("true", Run("State 2 < 5."));
        Assert.Equal("true", Run("State 7 > 5."));
        Assert.Equal("true", Run("State 5 = 5."));
    }
}
