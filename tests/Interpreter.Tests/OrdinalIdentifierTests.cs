using Cufet.Interpreter;
using Cufet.Lexer;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

// Acceptance tests for ordinals as contextual identifiers.
// Ordinal words (first, second, ..., tenth, last) are recognized as positional
// accessors only in the "the <ordinal> of <series>" shape. Everywhere else they
// are ordinary identifiers — variable names, parameter names, field names, etc.
public class OrdinalIdentifierTests
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

    // ── The trap is gone: ordinals as variable names ──────────────────────────

    [Fact]
    public void Define_First_AsVariable()
    {
        Assert.Equal("5", Run("Define first as 5. State first."));
    }

    [Fact]
    public void Define_Last_AsVariable()
    {
        Assert.Equal("99", Run("Define last as 99. State last."));
    }

    [Fact]
    public void Define_Second_AsVariable()
    {
        Assert.Equal("2", Run("Define second as 2. State second."));
    }

    [Fact]
    public void AllOrdinals_AreValidVariableNames()
    {
        Assert.Equal("10", Run("""
            Define first   as 1.
            Define second  as 2.
            Define third   as 3.
            Define fourth  as 4.
            Define fifth   as 5.
            Define sixth   as 6.
            Define seventh as 7.
            Define eighth  as 8.
            Define ninth   as 9.
            Define tenth   as 10.
            State tenth.
            """));
    }

    [Fact]
    public void First_AsBecomesTarget()
    {
        Assert.Equal("7", Run("""
            Define first as 1.
            first becomes 7.
            State first.
            """));
    }

    [Fact]
    public void Last_AsBecomesTarget()
    {
        Assert.Equal("42", Run("""
            Define last as 0.
            last becomes 42.
            State last.
            """));
    }

    // ── Accessor still works: existing behavior unchanged ─────────────────────

    [Fact]
    public void Accessor_FirstOf_StillWorks()
    {
        Assert.Equal("10", Run("""
            Define items as a series with (10, 20, 30).
            State first of items.
            """));
    }

    [Fact]
    public void Accessor_TheFirstOf_StillWorks()
    {
        Assert.Equal("10", Run("""
            Define items as a series with (10, 20, 30).
            State the first of items.
            """));
    }

    [Fact]
    public void Accessor_LastOf_StillWorks()
    {
        Assert.Equal("30", Run("""
            Define items as a series with (10, 20, 30).
            State last of items.
            """));
    }

    [Fact]
    public void Accessor_SecondOf_StillWorks()
    {
        Assert.Equal("20", Run("""
            Define items as a series with (10, 20, 30).
            State second of items.
            """));
    }

    [Fact]
    public void Accessor_AllOrdinals_StillWork()
    {
        Assert.Equal("1\n2\n3\n4\n5\n6\n7\n8\n9\n10", Run("""
            Define nums as a series with (1, 2, 3, 4, 5, 6, 7, 8, 9, 10).
            State first   of nums.
            State second  of nums.
            State third   of nums.
            State fourth  of nums.
            State fifth   of nums.
            State sixth   of nums.
            State seventh of nums.
            State eighth  of nums.
            State ninth   of nums.
            State tenth   of nums.
            """));
    }

    // ── No collision: variable AND accessor in the same scope ─────────────────

    [Fact]
    public void Variable_First_And_Accessor_CoexistInSameScope()
    {
        // A variable named 'first' and the ordinal accessor 'first of series'
        // both live in the same scope without collision.
        Assert.Equal("1\n10", Run("""
            Define first as 1.
            Define items as a series with (10, 20, 30).
            State first.
            State first of items.
            """));
    }

    [Fact]
    public void Variable_Last_And_Accessor_CoexistInSameScope()
    {
        Assert.Equal("99\n30", Run("""
            Define last as 99.
            Define items as a series with (10, 20, 30).
            State last.
            State last of items.
            """));
    }

    // ── Ordinals as function parameters ───────────────────────────────────────

    [Fact]
    public void Ordinal_AsParameterName()
    {
        Assert.Equal("15", Run("""
            Bind number to combine, given (the number first, the number second):
                return first + second.
            Done.
            State Cast combine on (10, 5).
            """));
    }

    // ── Ordinals as for-each iterator names ───────────────────────────────────

    [Fact]
    public void Ordinal_AsForEachIteratorName()
    {
        Assert.Equal("6", Run("""
            Define items as a series with (1, 2, 3).
            Define total as 0.
            For each first in items, repeat:
                total becomes total + first.
            Done.
            State total.
            """));
    }

    // ── Series set with ordinal still works ───────────────────────────────────

    [Fact]
    public void SeriesSet_WithOrdinalAccessor_StillWorks()
    {
        Assert.Equal("99", Run("""
            Define items as a series with (10, 20, 30).
            first of items becomes 99.
            State first of items.
            """));
    }

    // ── Text-substring with first/last still works ────────────────────────────

    [Fact]
    public void TextSubstring_FirstNCharacters_StillWorks()
    {
        Assert.Equal("hel", Run("""
            Define s as "hello".
            State first 3 characters of s.
            """));
    }

    [Fact]
    public void TextSubstring_LastNCharacters_StillWorks()
    {
        Assert.Equal("llo", Run("""
            Define s as "hello".
            State last 3 characters of s.
            """));
    }
}
