using Cufet.Lexer;
using Xunit;

namespace Cufet.Lexer.Tests;

// Every token carries the 1-based column of its FIRST character within its line. The lexer
// tracks the offset of the current line's start and subtracts; these tests pin that arithmetic,
// which is the only place in the whole column pipeline where it can be wrong by one.
public class ColumnTests
{
    private static IReadOnlyList<Token> Lex(string source) => new Lexer(source).Tokenize();

    private static IReadOnlyList<Token> LexTokens(string source)
    {
        var all = Lex(source);
        return all.Take(all.Count - 1).ToList(); // strip Eof
    }

    // ── The first token on a line ────────────────────────────────────────

    [Fact]
    public void FirstTokenOfTheFile_IsColumnOne()
    {
        var tokens = LexTokens("State 5.");
        Assert.Equal(1, tokens[0].Line);
        Assert.Equal(1, tokens[0].Column);
    }

    [Fact]
    public void MidLineTokens_CountFromTheLineStart()
    {
        //         1234567
        var tokens = LexTokens("State 5.");
        Assert.Equal(("State", 1, 1), (tokens[0].Lexeme, tokens[0].Line, tokens[0].Column));
        Assert.Equal(("5", 1, 7),     (tokens[1].Lexeme, tokens[1].Line, tokens[1].Column));
        Assert.Equal((".", 1, 8),     (tokens[2].Lexeme, tokens[2].Line, tokens[2].Column));
    }

    [Fact]
    public void LeadingIndentation_ShiftsTheColumn()
    {
        var tokens = LexTokens("    foo");
        Assert.Equal(1, tokens[0].Line);
        Assert.Equal(5, tokens[0].Column);
    }

    [Fact]
    public void TokenAfterANewline_RestartsAtColumnOne()
    {
        var tokens = LexTokens("State 5.\nState 6.");
        Assert.Equal((1, 1), (tokens[0].Line, tokens[0].Column));
        Assert.Equal((2, 1), (tokens[3].Line, tokens[3].Column)); // second 'State'
        Assert.Equal((2, 7), (tokens[4].Line, tokens[4].Column)); // its '6'
    }

    [Fact]
    public void CarriageReturnBeforeNewline_CountsAsPartOfTheOldLine()
    {
        // '\r' is ordinary whitespace; only '\n' opens a new line, and the line starts just
        // past it, so a CRLF file is not off by one.
        var tokens = LexTokens("foo\r\nbar");
        Assert.Equal((1, 1), (tokens[0].Line, tokens[0].Column));
        Assert.Equal((2, 1), (tokens[1].Line, tokens[1].Column));
    }

    // ── Across comments ──────────────────────────────────────────────────

    [Fact]
    public void TokenAfterALineComment_IsColumnOneOnTheNextLine()
    {
        var tokens = LexTokens("// note\nfoo");
        Assert.Equal((2, 1), (tokens[0].Line, tokens[0].Column));
    }

    [Fact]
    public void TokenAfterASingleLineBlockComment_ContinuesOnTheSameLine()
    {
        //                     1234567890123
        var tokens = LexTokens("/* note */ foo");
        Assert.Equal((1, 12), (tokens[0].Line, tokens[0].Column));
    }

    [Fact]
    public void TokenAfterAMultiLineBlockComment_CountsFromTheCommentsLastLine()
    {
        // Line 3 reads "three */ foo": 'f' is the tenth character on it.
        var tokens = LexTokens("/* one\ntwo\nthree */ foo");
        Assert.Equal((3, 10), (tokens[0].Line, tokens[0].Column));
    }

    // ── Strings and interpolation ────────────────────────────────────────

    [Fact]
    public void StringLiteral_ReportsItsOpeningQuote()
    {
        //                     123456789
        var tokens = LexTokens("State \"hi\".");
        Assert.Equal(TokenType.String, tokens[1].Type);
        Assert.Equal((1, 7), (tokens[1].Line, tokens[1].Column));
    }

    [Fact]
    public void MultiLineStringLiteral_ReportsWhereItOpened_AndTheNextTokenIsStillRight()
    {
        // The literal spans two lines. Its own position is where it opened; the tokens after it
        // must be measured from the line the literal ENDED on, not from where it started.
        // Line 1 is  State "a   and line 2 is  b". foo
        //            1234567 8                 123456
        var tokens = LexTokens("State \"a\nb\". foo");
        Assert.Equal(TokenType.String, tokens[1].Type);
        Assert.Equal((1, 7), (tokens[1].Line, tokens[1].Column));
        Assert.Equal((".", 2, 3), (tokens[2].Lexeme, tokens[2].Line, tokens[2].Column));
        Assert.Equal(("foo", 2, 5), (tokens[3].Lexeme, tokens[3].Line, tokens[3].Column));
    }

    [Fact]
    public void InterpolatedString_HoleTokensCarryTheirOwnColumns()
    {
        //                     1234567890123456
        var tokens = LexTokens("State \"n={x}\".");
        Assert.Equal(TokenType.InterpolOpen, tokens[1].Type);
        Assert.Equal((1, 7), (tokens[1].Line, tokens[1].Column));   // the opening quote

        var open = tokens.First(t => t.Type == TokenType.InterpolHoleOpen);
        Assert.Equal((1, 10), (open.Line, open.Column));            // the '{'

        var inner = tokens.First(t => t.Type == TokenType.Identifier);
        Assert.Equal(("x", 1, 11), (inner.Lexeme, inner.Line, inner.Column));

        var close = tokens.First(t => t.Type == TokenType.InterpolHoleClose);
        Assert.Equal((1, 12), (close.Line, close.Column));          // the '}'
    }

    [Fact]
    public void TokenAfterAnInterpolatedString_KeepsCounting()
    {
        //                     1234567890123456
        var tokens = LexTokens("State \"n={x}\".");
        Assert.Equal((".", 1, 14), (tokens[^1].Lexeme, tokens[^1].Line, tokens[^1].Column));
    }

    // ── Symbols, possessives, numbers, bits ──────────────────────────────

    [Fact]
    public void TwoCharacterSymbol_ReportsItsFirstCharacter()
    {
        //                     123456789012
        var tokens = LexTokens("If x is >= 1");
        var gte = tokens.First(t => t.Type == TokenType.Gte);
        Assert.Equal((1, 9), (gte.Line, gte.Column));
    }

    [Fact]
    public void PossessiveAndBitsAndNumbers_ReportTheirFirstCharacter()
    {
        //                     1234567890123456789
        var tokens = LexTokens("State alice's 0xFF.");
        Assert.Equal((TokenType.Possessive, 1, 12), (tokens[2].Type, tokens[2].Line, tokens[2].Column));
        Assert.Equal((TokenType.Bits, 1, 15), (tokens[3].Type, tokens[3].Line, tokens[3].Column));
    }

    // ── Errors ───────────────────────────────────────────────────────────

    [Fact]
    public void LexerError_CarriesTheColumnOfTheOffendingCharacter()
    {
        //                                            123456789
        var ex = Assert.Throws<LexerException>(() => Lex("State ~x."));
        Assert.Equal(1, ex.Line);
        Assert.Equal(7, ex.Column);
    }

    [Fact]
    public void LexerError_OnALaterLine_CountsFromThatLinesStart()
    {
        var ex = Assert.Throws<LexerException>(() => Lex("State 1.\nState Foo."));
        Assert.Equal(2, ex.Line);
        Assert.Equal(7, ex.Column); // 'Foo' — an identifier must start lowercase
    }
}
