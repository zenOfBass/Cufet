using Cufet.Interpreter;
using Cufet.Lexer;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

// Every AST node that carries a Line now carries the matching Column, taken from the same token
// the Line came from. These tests pin the handful of shapes that stand in for the rest: a
// statement keyword, a nested expression, and the two error paths that report a position.
public class AstColumnTests
{
    private static Program Parse(string source) =>
        new Parser(new CufetLexer(source).Tokenize()).Parse();

    [Fact]
    public void DefineStatement_CarriesTheColumnOfItsKeyword()
    {
        //                     1234567890
        var program = Parse("Define x as 1.\n    Define y as 2.");

        var first = Assert.IsType<DefineStatement>(program.Statements[0]);
        Assert.Equal((1, 1), (first.Line, first.Column));

        var second = Assert.IsType<DefineStatement>(program.Statements[1]);
        Assert.Equal((2, 5), (second.Line, second.Column));
    }

    [Fact]
    public void NestedExpressionNodes_CarryTheirOwnOperatorColumns()
    {
        //                  1234567890123456
        var program = Parse("State 1 + 2 * 3.");
        var state   = Assert.IsType<StateStatement>(program.Statements[0]);

        // '+' at column 9, with '*' at column 13 on its right.
        var plus = Assert.IsType<BinaryExpression>(state.Value);
        Assert.Equal(TokenType.Plus, plus.Op);
        Assert.Equal((1, 9), (plus.Line, plus.Column));

        var star = Assert.IsType<BinaryExpression>(plus.Right);
        Assert.Equal(TokenType.Star, star.Op);
        Assert.Equal((1, 13), (star.Line, star.Column));
    }

    [Fact]
    public void VariableReference_CarriesTheColumnOfItsName()
    {
        //                  123456789012345678
        var program = Parse("Define x as 1. State x.");
        var state   = Assert.IsType<StateStatement>(program.Statements[1]);
        var vr      = Assert.IsType<VariableReference>(state.Value);
        Assert.Equal((1, 22), (vr.Line, vr.Column));
    }

    [Fact]
    public void ParseError_CarriesLineAndColumn()
    {
        //                                                       1234567890
        var ex = Assert.Throws<ParseException>(() => Parse("Define x as 1.\nState 5"));
        Assert.Equal(2, ex.Line);
        Assert.Equal(8, ex.Column); // the Eof sitting where the '.' should be
        Assert.Contains("column 8", ex.Message);
    }

    [Fact]
    public void TypeError_CarriesLineAndColumnStructurally()
    {
        //                              1234567890123
        var program = Parse("Define x as 1.\nx becomes \"text\".");
        var ex = Assert.Throws<TypeException>(() => new TypeChecker().Check(program));
        Assert.Equal(2, ex.Line);
        Assert.Equal(1, ex.Column);
        // The prose still names the line, which is what the CLI's fallback reads.
        Assert.Contains("Here on line 2", ex.Message);
    }
}
