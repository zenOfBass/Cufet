using Cufet.Lexer;
using Xunit;

namespace Cufet.Lexer.Tests;

// Verbatim text: <<...>>. The claim under test is TOTALITY — nothing inside is interpreted.
// Each test below names one thing that IS interpreted in a quoted literal and shows that it is
// not interpreted here, because "raw except for one case" is the failure mode this form exists
// to avoid.
public class RawTextTests
{
    private static IReadOnlyList<Token> Lex(string source) => new Lexer(source).Tokenize();

    private static Token LexOne(string source)
    {
        var all = Lex(source);
        Assert.Equal(2, all.Count); // the token, then Eof
        return all[0];
    }

    // ── It is an ordinary String token ───────────────────────────────────

    [Fact]
    public void RawText_ProducesAStringToken()
    {
        var token = LexOne("<<hello>>");
        Assert.Equal(TokenType.String, token.Type);
        Assert.Equal("hello", token.Lexeme);
    }

    [Fact]
    public void RawText_Empty_IsTheEmptyString()
    {
        var token = LexOne("<<>>");
        Assert.Equal(TokenType.String, token.Type);
        Assert.Equal("", token.Lexeme);
    }

    // ── Nothing inside is interpreted ────────────────────────────────────

    [Fact]
    public void RawText_QuotesAreLiteral()
    {
        Assert.Equal("say \"hi\"", LexOne("<<say \"hi\">>").Lexeme);
    }

    [Fact]
    public void RawText_BracesAreLiteralNotInterpolation()
    {
        // The case the form is for. In a quoted literal this would open a hole and then fail to
        // lex `"name": "x"` as an expression.
        Assert.Equal("{\"name\": \"x\"}", LexOne("<<{\"name\": \"x\"}>>").Lexeme);
    }

    [Fact]
    public void RawText_BackslashIsLiteralAndNeedsNoPartner()
    {
        // A trailing backslash before the closing delimiter does not escape it — there are no
        // escapes to do the escaping.
        Assert.Equal(@"C:\Users\", LexOne(@"<<C:\Users\>>").Lexeme);
    }

    [Fact]
    public void RawText_EscapeSequencesAreLeftAsTyped()
    {
        Assert.Equal(@"a\nb", LexOne(@"<<a\nb>>").Lexeme);
    }

    [Fact]
    public void RawText_UnknownEscapeIsNotAnError()
    {
        // `"\q"` is a hard lexer error in a quoted literal. Here it is two characters.
        Assert.Equal(@"\q", LexOne(@"<<\q>>").Lexeme);
    }

    [Fact]
    public void RawText_CommentMarkersAreLiteral()
    {
        Assert.Equal("// not a comment /* nor this", LexOne("<<// not a comment /* nor this>>").Lexeme);
    }

    [Fact]
    public void RawText_HoldsARegularExpression()
    {
        Assert.Equal(@"^\d{3}-\d{4}$", LexOne(@"<<^\d{3}-\d{4}$>>").Lexeme);
    }

    // ── Nesting is depth-counted, like block comments ────────────────────

    [Fact]
    public void RawText_Nested_InnerDelimitersAreKept()
    {
        Assert.Equal("a <<b>> c", LexOne("<<a <<b>> c>>").Lexeme);
    }

    [Fact]
    public void RawText_Nested_TwoDeep()
    {
        Assert.Equal("<<<<x>>>>", LexOne("<<<<<<x>>>>>>").Lexeme);
    }

    [Fact]
    public void RawText_UnmatchedInnerOpenerRunsToEnd()
    {
        // An inner '<<' raises the depth, so the outer text is no longer closed by its '>>'.
        var ex = Assert.Throws<LexerException>(() => Lex("<<a <<b>>"));
        Assert.Contains("unterminated verbatim text", ex.Message);
    }

    // ── Closing is at the FIRST '>>', which is the documented corner ─────

    [Fact]
    public void RawText_ClosesAtTheFirstDoubleAngle()
    {
        // '>>>' closes at the first two and leaves a stray '>' behind — the one thing this form
        // cannot spell is text ending in '>'. Documented in GRAMMAR.md under sharp edges.
        var tokens = Lex("<<a>>>");
        Assert.Equal("a", tokens[0].Lexeme);
        Assert.Equal(TokenType.Gt, tokens[1].Type);
    }

    [Fact]
    public void RawText_SingleAngleBracketsInsideAreFine()
    {
        Assert.Equal("a < b > c", LexOne("<<a < b > c>>").Lexeme);
    }

    // ── Multi-line, and lines keep counting ──────────────────────────────

    [Fact]
    public void RawText_MayRunAcrossNewlines()
    {
        Assert.Equal("one\ntwo", LexOne("<<one\ntwo>>").Lexeme);
    }

    [Fact]
    public void RawText_CrlfBecomesOneNewline()
    {
        // ★ A line break is ONE '\n' whatever the file is stored as. Without this the same
        // program means different things depending on how git checked it out — and a multi-line
        // verbatim literal is the normal way to write one, with no escape to reach for instead.
        Assert.Equal("one\ntwo", LexOne("<<one\r\ntwo>>").Lexeme);
    }

    [Fact]
    public void RawText_CrlfIsCountedAsOneLine()
    {
        var tokens = Lex("<<one\r\ntwo>>\r\nfoo");
        Assert.Equal("foo", tokens[1].Lexeme);
        Assert.Equal(3, tokens[1].Line);
        Assert.Equal(1, tokens[1].Column);
    }

    [Fact]
    public void RawText_LoneCarriageReturnIsKept()
    {
        // Not a line break on any platform Cufet targets, so it meant a carriage return.
        Assert.Equal("a\rb", LexOne("<<a\rb>>").Lexeme);
    }

    [Fact]
    public void RawText_LineCountingContinuesAfterIt()
    {
        var tokens = Lex("<<one\ntwo>>\nfoo");
        Assert.Equal("foo", tokens[1].Lexeme);
        Assert.Equal(3, tokens[1].Line);
    }

    // ── Position is the opener's ─────────────────────────────────────────

    [Fact]
    public void RawText_PositionIsTheOpeningDelimiter()
    {
        var tokens = Lex("foo <<bar\nbaz>>");
        Assert.Equal(1, tokens[1].Line);
        Assert.Equal(5, tokens[1].Column);
    }

    // ── Unterminated is a clean lexer error naming the opener ────────────

    [Fact]
    public void RawText_Unterminated_ThrowsNamingTheOpeningPosition()
    {
        var ex = Assert.Throws<LexerException>(() => Lex("foo\n<<never closed"));
        Assert.Contains("unterminated verbatim text", ex.Message);
        Assert.Equal(2, ex.Line);
        Assert.Equal(1, ex.Column);
    }

    [Fact]
    public void RawText_Unterminated_ErrorMentionsExpectedClose()
    {
        var ex = Assert.Throws<LexerException>(() => Lex("<<open"));
        Assert.Contains(">>", ex.Message);
    }

    // ── Nothing is taken away from '<' and '>' ───────────────────────────

    [Fact]
    public void Comparison_StillLexesAsBefore()
    {
        var tokens = Lex("a < b");
        Assert.Equal(TokenType.Lt, tokens[1].Type);
    }

    [Fact]
    public void ComparisonOrEqual_StillLexesAsBefore()
    {
        var tokens = Lex("a <= b");
        Assert.Equal(TokenType.Lte, tokens[1].Type);
    }

    // ── It composes with the rest of a program ───────────────────────────

    [Fact]
    public void RawText_InsideAnInterpolationHole_IsStillVerbatim()
    {
        // The hole lexes a full expression, and a verbatim literal is an expression.
        var tokens = Lex("\"a{<<{x}>>}b\"");
        Assert.Contains(tokens, t => t.Type == TokenType.String && t.Lexeme == "{x}");
    }

    [Fact]
    public void QuotedLiteral_FollowsTheSameNewlineRule()
    {
        // The rule is about literals, not about this form — one helper serves both, so the two
        // cannot drift into disagreeing about what a line break is.
        Assert.Equal("one\ntwo", LexOne("\"one\r\ntwo\"").Lexeme);
    }

    [Fact]
    public void RawText_IsIndistinguishableFromAQuotedLiteralAfterLexing()
    {
        // Same type, same lexeme — a verbatim literal is a spelling, not a kind of value.
        var raw    = LexOne("<<hi>>");
        var quoted = LexOne("\"hi\"");
        Assert.Equal(quoted.Type, raw.Type);
        Assert.Equal(quoted.Lexeme, raw.Lexeme);
    }
}
