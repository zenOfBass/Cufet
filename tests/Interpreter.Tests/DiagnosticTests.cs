using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

// The warnings channel itself — not any rule that uses it. A warning is the one thing the front
// end can say without stopping, so the properties worth pinning are that it collects rather than
// throws, and that a clean program says nothing.
public class DiagnosticTests
{
    private static TypeChecker Check(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        var checker = new TypeChecker();
        checker.Check(program);
        return checker;
    }

    [Fact]
    public void CleanProgram_ReportsNothing()
    {
        var checker = Check("Define n as 5.\nState n.");
        Assert.Empty(checker.Diagnostics.Items);
        Assert.False(checker.Diagnostics.Any);
    }

    [Fact]
    public void Bag_CollectsWithoutThrowing()
    {
        var bag = new DiagnosticBag();
        bag.Warn("something worth saying", 3, 7);

        var only = Assert.Single(bag.Items);
        Assert.Equal(DiagnosticSeverity.Warning, only.Severity);
        Assert.Equal("warning", only.SeverityName);
        Assert.Equal(3, only.Line);
        Assert.Equal(7, only.Column);
    }

    [Fact]
    public void Bag_NeverReportsAPositionOfZero()
    {
        // A diagnostic with no position of its own still has to point somewhere a reader can go.
        var bag = new DiagnosticBag();
        bag.Warn("no position");
        bag.Warn("zeroed", 0, 0);

        Assert.All(bag.Items, d => Assert.True(d.Line >= 1 && d.Column >= 1));
    }
}
