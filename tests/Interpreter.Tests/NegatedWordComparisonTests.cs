using Cufet.Interpreter;
using Cufet.Lexer;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

// Acceptance tests for negated word-form comparisons:
//   is not greater than  →  <=
//   is not less than     →  >=
//   is not               →  !=
//   is not equal to      →  != (verbose form)
// All forms work in both condition position (If/While) and expression position.
public class NegatedWordComparisonTests
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

    private static void ParseFails(string source, string expectedFragment)
    {
        var tokens = new CufetLexer(source).Tokenize();
        var ex = Assert.ThrowsAny<Exception>(() => new Parser(tokens).Parse());
        Assert.Contains(expectedFragment, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── is not greater than (→ <=) ─────────────────────────────────────────────

    [Fact]
    public void IsNotGreaterThan_TrueWhenEqual()
    {
        // 5 is not greater than 5 → 5 <= 5 → true
        Assert.Equal("yes", Run("If 5 is not greater than 5, State \"yes\"."));
    }

    [Fact]
    public void IsNotGreaterThan_TrueWhenSmaller()
    {
        Assert.Equal("yes", Run("If 3 is not greater than 10, State \"yes\"."));
    }

    [Fact]
    public void IsNotGreaterThan_FalseWhenLarger()
    {
        Assert.Equal("no", Run("If 11 is not greater than 10, State \"yes\". Otherwise, State \"no\"."));
    }

    [Fact]
    public void IsNotGreaterThan_InWhileCondition()
    {
        Assert.Equal("3", Run("""
            Define count as 0.
            While count is not greater than 2, repeat:
                count becomes count + 1.
            Done.
            State count.
            """));
    }

    [Fact]
    public void IsNotGreaterThan_InExpressionPosition()
    {
        Assert.Equal("yes", Run("""
            Define x as 7.
            Define ok as (x is not greater than 10).
            If ok, State "yes".
            """));
    }

    // ── is not less than (→ >=) ────────────────────────────────────────────────

    [Fact]
    public void IsNotLessThan_TrueWhenEqual()
    {
        // 5 is not less than 5 → 5 >= 5 → true
        Assert.Equal("yes", Run("If 5 is not less than 5, State \"yes\"."));
    }

    [Fact]
    public void IsNotLessThan_TrueWhenLarger()
    {
        Assert.Equal("yes", Run("If 10 is not less than 3, State \"yes\"."));
    }

    [Fact]
    public void IsNotLessThan_FalseWhenSmaller()
    {
        Assert.Equal("no", Run("If 3 is not less than 10, State \"yes\". Otherwise, State \"no\"."));
    }

    [Fact]
    public void IsNotLessThan_InWhileCondition()
    {
        // count starts at 5, loops while count >= 0, subtracts 1 each time → exits at -1
        Assert.Equal("-1", Run("""
            Define count as 5.
            While count is not less than 0, repeat:
                count becomes count - 1.
            Done.
            State count.
            """));
    }

    [Fact]
    public void IsNotLessThan_InExpressionPosition()
    {
        Assert.Equal("yes", Run("""
            Define score as 75.
            Define passing as (score is not less than 50).
            If passing, State "yes".
            """));
    }

    // ── is not (→ !=) ─────────────────────────────────────────────────────────

    [Fact]
    public void IsNot_NumberInequality()
    {
        Assert.Equal("yes", Run("If 3 is not 5, State \"yes\"."));
    }

    [Fact]
    public void IsNot_FalseWhenEqual()
    {
        Assert.Equal("no", Run("If 5 is not 5, State \"yes\". Otherwise, State \"no\"."));
    }

    [Fact]
    public void IsNot_TextInequality()
    {
        Assert.Equal("yes", Run("If \"hello\" is not \"world\", State \"yes\"."));
    }

    [Fact]
    public void IsNot_InExpressionPosition()
    {
        Assert.Equal("yes", Run("""
            Define x as 3.
            Define different as (x is not 5).
            If different, State "yes".
            """));
    }

    // ── is not equal to (→ !=, verbose form) ──────────────────────────────────

    [Fact]
    public void IsNotEqualTo_TrueWhenDifferent()
    {
        Assert.Equal("yes", Run("If 3 is not equal to 5, State \"yes\"."));
    }

    [Fact]
    public void IsNotEqualTo_FalseWhenSame()
    {
        Assert.Equal("no", Run("If 5 is not equal to 5, State \"yes\". Otherwise, State \"no\"."));
    }

    [Fact]
    public void IsNotEqualTo_TextInequality()
    {
        Assert.Equal("yes", Run("If \"hello\" is not equal to \"world\", State \"yes\"."));
    }

    [Fact]
    public void IsNotEqualTo_InExpressionPosition()
    {
        Assert.Equal("yes", Run("""
            Define x as 3.
            Define different as (x is not equal to 5).
            If different, State "yes".
            """));
    }

    // ── Boundary: same value on both sides ───────────────────────────────────

    [Fact]
    public void Boundary_IsNotGreaterThan_EqualValues()
    {
        // x is not greater than x → x <= x → always true
        Assert.Equal("yes", Run("""
            Define x as 42.
            If x is not greater than x, State "yes".
            """));
    }

    [Fact]
    public void Boundary_IsNotLessThan_EqualValues()
    {
        // x is not less than x → x >= x → always true
        Assert.Equal("yes", Run("""
            Define x as 42.
            If x is not less than x, State "yes".
            """));
    }

    // ── Combined in same program ──────────────────────────────────────────────

    [Fact]
    public void NegatedForms_WorkWithVariables()
    {
        Assert.Equal("in-range", Run("""
            Define score as 75.
            Define min as 50.
            Define max as 100.
            If score is not less than min and score is not greater than max:
                State "in-range".
            Done.
            """));
    }
}
