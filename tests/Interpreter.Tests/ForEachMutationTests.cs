using Cufet.Interpreter;
using Cufet.Lexer;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

// Guarantee tests for the for-each structural-mutation guard.
// Add/Remove during iteration is caught at runtime with an educational error that
// names the collection and the line. Element-value assignment is allowed (no count change).
public class ForEachMutationTests
{
    private static RuntimeException SeriesFails(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
        var output  = new StringWriter();
        return Assert.Throws<RuntimeException>(() =>
            new Interpreter(output).Execute(program));
    }

    private static string Run(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
        var output  = new StringWriter();
        new Interpreter(output).Execute(program);
        return output.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    // ── Series: Add during iteration ──────────────────────────────────────────

    [Fact]
    public void ForEach_SeriesAdd_RuntimeError()
    {
        var ex = SeriesFails("""
            Define items as a series of number with (1, 2, 3).
            For each x in items, repeat:
                Add 4 to items.
            Done.
            """);
        Assert.Contains("items", ex.Message);
        Assert.Contains("modified", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ForEach_SeriesAdd_ErrorNamesCollect()
    {
        var ex = SeriesFails("""
            Define items as a series of number with (1, 2, 3).
            For each x in items, repeat:
                Add 4 to items.
            Done.
            """);
        Assert.Contains("collect", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ForEach_SeriesAdd_ErrorNamesWhile()
    {
        var ex = SeriesFails("""
            Define items as a series of number with (1, 2, 3).
            For each x in items, repeat:
                Add 4 to items.
            Done.
            """);
        Assert.Contains("While", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ForEach_SeriesRemove_RuntimeError()
    {
        var ex = SeriesFails("""
            Define items as a series of number with (1, 2, 3).
            For each x in items, repeat:
                Remove the first item from items.
            Done.
            """);
        Assert.Contains("items", ex.Message);
        Assert.Contains("modified", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Map: mutation during iteration ────────────────────────────────────────

    [Fact]
    public void ForEach_MapAdd_RuntimeError()
    {
        var ex = SeriesFails("""
            Define scores as a map with ("alice" : 10, "bob" : 20).
            For each pair in scores, repeat:
                In scores, the entry for "carol" becomes 30.
            Done.
            """);
        Assert.Contains("modified", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ForEach_MapAdd_ErrorNamesVariable()
    {
        var ex = SeriesFails("""
            Define scores as a map with ("alice" : 10, "bob" : 20).
            For each pair in scores, repeat:
                In scores, the entry for "carol" becomes 30.
            Done.
            """);
        Assert.Contains("scores", ex.Message);
    }

    // ── Aliased mutation — same List<object> via a different variable ──────────

    [Fact]
    public void ForEach_AliasedMutation_RuntimeError()
    {
        var ex = SeriesFails("""
            Define items as a series of number with (1, 2, 3).
            Define alias as items.
            For each x in items, repeat:
                Add 4 to alias.
            Done.
            """);
        Assert.Contains("modified", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ForEach_AliasedMutation_ErrorNamesOriginal()
    {
        var ex = SeriesFails("""
            Define items as a series of number with (1, 2, 3).
            Define alias as items.
            For each x in items, repeat:
                Add 4 to alias.
            Done.
            """);
        // Error names 'items' (the iterated variable), not 'alias'.
        Assert.Contains("items", ex.Message);
    }

    // ── Element-value assignment is allowed (no count change) ─────────────────

    [Fact]
    public void ForEach_ElementAssignment_IsAllowed()
    {
        // Modifying element values during iteration is not caught — the loop
        // still visits all original elements, just observing changed values.
        var result = Run("""
            Define items as a series of number with (1, 2, 3).
            For each x in items, repeat:
                the first of items becomes 99.
            Done.
            State the first of items.
            """);
        Assert.Equal("99", result);
    }

    // ── Mutation of a DIFFERENT series inside the loop is fine ────────────────

    [Fact]
    public void ForEach_MutateOtherSeries_IsAllowed()
    {
        var result = Run("""
            Define items as a series of number with (1, 2, 3).
            Define collected as a series of number.
            For each x in items, repeat:
                Add x to collected.
            Done.
            State the number of collected.
            """);
        Assert.Equal("3", result);
    }
}
