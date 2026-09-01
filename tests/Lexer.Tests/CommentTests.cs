using Cufet.Lexer;
using Xunit;

namespace Cufet.Lexer.Tests;

public class CommentTests
{
    private static IReadOnlyList<Token> Lex(string source) => new Lexer(source).Tokenize();

    private static IReadOnlyList<Token> LexTokens(string source)
    {
        var all = Lex(source);
        return all.Take(all.Count - 1).ToList(); // strip Eof
    }

    // ── Line comments ────────────────────────────────────────────────────

    [Fact]
    public void LineComment_AloneProducesNoTokens()
    {
        var tokens = LexTokens("// a comment");
        Assert.Empty(tokens);
    }

    [Fact]
    public void LineComment_EndsAtNewline_CodeAfterIsLexed()
    {
        var tokens = LexTokens("// note\nfoo");
        Assert.Single(tokens);
        Assert.Equal("foo", tokens[0].Lexeme);
    }

    [Fact]
    public void LineComment_AfterCodeOnSameLine()
    {
        var tokens = LexTokens("foo // trailing note\nbar");
        Assert.Equal(2, tokens.Count);
        Assert.Equal("foo", tokens[0].Lexeme);
        Assert.Equal("bar", tokens[1].Lexeme);
    }

    [Fact]
    public void LineComment_AtEndOfFileWithoutNewline_IsFine()
    {
        // Nothing terminates it but EOF. This must not throw the way an unclosed block does.
        var tokens = LexTokens("foo // and then nothing");
        Assert.Single(tokens);
        Assert.Equal("foo", tokens[0].Lexeme);
    }

    [Fact]
    public void LineComment_DoesNotSwallowTheFollowingLine()
    {
        var tokens = LexTokens("// one\n// two\nfoo");
        Assert.Single(tokens);
        Assert.Equal("foo", tokens[0].Lexeme);
    }

    [Fact]
    public void LineComment_CufetSyntaxInsideIsIgnored()
    {
        var tokens = LexTokens("// Define y as 99.\nfoo");
        Assert.Single(tokens);
        Assert.DoesNotContain(tokens, t => t.Type == TokenType.Define);
        Assert.DoesNotContain(tokens, t => t.Type == TokenType.Dot);
    }

    // ── Division is untouched ────────────────────────────────────────────
    // '/' has no lookahead of its own, so the only thing '//' could previously have meant was
    // division by a unary slash — not an expression Cufet has. Nothing valid was taken away.

    [Fact]
    public void Division_StillLexesAsSlash()
    {
        var tokens = LexTokens("6 / 2");
        Assert.Equal(3, tokens.Count);
        Assert.Equal(TokenType.Slash, tokens[1].Type);
    }

    [Fact]
    public void Division_WithoutSpaces_StillLexesAsSlash()
    {
        var tokens = LexTokens("6/2");
        Assert.Equal(3, tokens.Count);
        Assert.Equal(TokenType.Slash, tokens[1].Type);
    }

    // ── Block comments ───────────────────────────────────────────────────

    [Fact]
    public void BlockComment_AloneProducesNoTokens()
    {
        var tokens = LexTokens("/* a comment */");
        Assert.Empty(tokens);
    }

    [Fact]
    public void BlockComment_BeforeCode_Transparent()
    {
        var tokens = LexTokens("/* note */ foo");
        Assert.Single(tokens);
        Assert.Equal(TokenType.Identifier, tokens[0].Type);
        Assert.Equal("foo", tokens[0].Lexeme);
    }

    [Fact]
    public void BlockComment_AfterCode_Transparent()
    {
        var tokens = LexTokens("foo /* note */");
        Assert.Single(tokens);
        Assert.Equal("foo", tokens[0].Lexeme);
    }

    [Fact]
    public void BlockComment_BetweenTokens_Transparent()
    {
        var tokens = LexTokens("foo /* note */ bar");
        Assert.Equal(2, tokens.Count);
        Assert.Equal("foo", tokens[0].Lexeme);
        Assert.Equal("bar", tokens[1].Lexeme);
    }

    [Fact]
    public void BlockComment_MultiLine_ProducesNoTokens()
    {
        var tokens = LexTokens("/* line one\nline two */");
        Assert.Empty(tokens);
    }

    [Fact]
    public void BlockComment_MultiLine_BeforeCode_Transparent()
    {
        var tokens = LexTokens("/* line one\nline two */ foo");
        Assert.Single(tokens);
        Assert.Equal("foo", tokens[0].Lexeme);
    }

    [Fact]
    public void BlockComment_DotInsideIsNotDotToken()
    {
        var tokens = LexTokens("/* this. has. periods. */ foo");
        Assert.Single(tokens);
        Assert.DoesNotContain(tokens, t => t.Type == TokenType.Dot);
    }

    [Fact]
    public void BlockComment_CufetSyntaxInsideIsIgnored()
    {
        var tokens = LexTokens("/* Define y as 99. */ foo");
        Assert.Single(tokens);
        Assert.DoesNotContain(tokens, t => t.Type == TokenType.Define);
    }

    // ── Line numbers survive comments ────────────────────────────────────
    // Both forms must keep _line accurate, or every diagnostic after a comment points at the
    // wrong line — the failure the editor squiggles would show first.

    [Fact]
    public void LineComment_PreservesLineNumbers()
    {
        var tokens = LexTokens("// one\n// two\nfoo");
        Assert.Equal(3, tokens[0].Line);
    }

    [Fact]
    public void BlockComment_PreservesLineNumbersAcrossItsBody()
    {
        var tokens = LexTokens("/* one\ntwo\nthree */ foo");
        Assert.Equal(3, tokens[0].Line);
    }

    // ── Comment markers inside string literals are just text ─────────────
    // Strings are consumed whole before whitespace-skipping ever looks at them, so this holds
    // by construction — locked here so a future lexer change cannot quietly break it.

    [Fact]
    public void LineCommentMarker_InsideString_IsNotAComment()
    {
        var tokens = LexTokens("\"http://example.com\"");
        Assert.Single(tokens);
        Assert.Equal(TokenType.String, tokens[0].Type);
        Assert.Equal("http://example.com", tokens[0].Lexeme);
    }

    [Fact]
    public void BlockCommentMarker_InsideString_IsNotAComment()
    {
        var tokens = LexTokens("\"/* not a comment */\" foo");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenType.String, tokens[0].Type);
        Assert.Equal("foo", tokens[1].Lexeme);
    }

    // ── Nesting: an inner '/*' must be closed before the outer comment ends ──────────────
    // Comments nest so that commenting out a block which already contains comments works.
    // Under a non-nesting rule the inner '*/' ends the outer comment, the rest of the block is
    // lexed as code, and the trailing '*/' becomes an unexpected-character error. Rust, Swift
    // and D spell block comments this way AND nest them, for exactly this reason.

    [Fact]
    public void BlockComment_Nesting_InnerCloserDoesNotEndOuter()
    {
        // The first '*/' closes only the INNER comment; 'c' is still commented out.
        var tokens = LexTokens("/* a /* b */ c */ d");
        Assert.Single(tokens);
        Assert.Equal("d", tokens[0].Lexeme);
    }

    [Fact]
    public void BlockComment_Nesting_CommentingOutABlockThatHasComments()
    {
        // The motivating case, in the shape people actually write it.
        var tokens = LexTokens("""
            /* disabled for now

            Bind number to helper, given (the number n):
                /* double it */
                return n * 2.
            Done.

            */
            after
            """);
        Assert.Single(tokens);
        Assert.Equal("after", tokens[0].Lexeme);
    }

    [Fact]
    public void BlockComment_Nesting_DeeplyNested()
    {
        var tokens = LexTokens("/* one /* two /* three */ two */ one */ out");
        Assert.Single(tokens);
        Assert.Equal("out", tokens[0].Lexeme);
    }

    [Fact]
    public void BlockComment_Nesting_UnclosedInnerIsUnterminated()
    {
        // An inner '/*' that never closes leaves the outer open too — and the error names the
        // OUTERMOST opening line, which is the one the author has to go find.
        var ex = Assert.Throws<LexerException>(() => Lex("/* outer /* inner */"));
        Assert.Contains("unterminated comment", ex.Message);
    }

    // ── A line comment inside a block comment is just text ───────────────

    [Fact]
    public void BlockComment_ContainingLineCommentMarker_IsUnaffected()
    {
        var tokens = LexTokens("/* a // b\n still commented */ foo");
        Assert.Single(tokens);
        Assert.Equal("foo", tokens[0].Lexeme);
    }

    // ── A block-opener inside a line comment does not open a block ───────

    [Fact]
    public void LineComment_ContainingBlockOpener_DoesNotOpenABlock()
    {
        // '/*' here is text on a commented line; the next line must lex normally rather than
        // being swallowed by a block that was never really opened.
        var tokens = LexTokens("// what about /* this\nfoo");
        Assert.Single(tokens);
        Assert.Equal("foo", tokens[0].Lexeme);
    }

    // ── Unterminated block comment is a lexer error ──────────────────────

    [Fact]
    public void BlockComment_Unterminated_ThrowsLexerException()
    {
        var ex = Assert.Throws<LexerException>(() => Lex("/* no closing marker"));
        Assert.Contains("unterminated comment", ex.Message);
    }

    [Fact]
    public void BlockComment_Unterminated_ErrorMentionsExpectedClose()
    {
        var ex = Assert.Throws<LexerException>(() => Lex("/* open"));
        Assert.Contains("*/", ex.Message);
    }

    // ── Multiple comments in one source ──────────────────────────────────

    [Fact]
    public void Comment_Multiple_AllStripped()
    {
        var tokens = LexTokens("/* first */ foo /* second */ bar /* third */");
        Assert.Equal(2, tokens.Count);
        Assert.Equal("foo", tokens[0].Lexeme);
        Assert.Equal("bar", tokens[1].Lexeme);
    }

    [Fact]
    public void Comment_BothFormsMixed()
    {
        var tokens = LexTokens("/* block */ foo // line\n/* block */ bar");
        Assert.Equal(2, tokens.Count);
        Assert.Equal("foo", tokens[0].Lexeme);
        Assert.Equal("bar", tokens[1].Lexeme);
    }
    // ── Kept, not discarded ──────────────────────────────────────────────
    //
    // ★★ Every test above says a comment produces NO TOKEN, and all of them still hold — that is
    // the design. A comment rides on the token that follows it instead, so the parser cannot
    // notice this exists, and no `SkipNoise` site had to learn a new thing to skip.
    //
    // ⚠ What is under test here is that the text survives at all. It was being eaten inside
    // SkipWhitespace and thrown away, so the sentence an author wrote above a declaration could
    // not be read by anything downstream.

    [Fact]
    public void ALineComment_IsCarriedOnTheTokenAfterIt()
    {
        var tokens = LexTokens("// why this exists\nfoo");
        var carried = Assert.Single(tokens[0].Leading);
        Assert.Equal(CommentKind.Line, carried.Kind);
        Assert.Equal(" why this exists", carried.Text);
    }

    [Fact]
    public void ABlockComment_IsCarriedOnTheTokenAfterIt()
    {
        var tokens = LexTokens("/* why this exists */ foo");
        var carried = Assert.Single(tokens[0].Leading);
        Assert.Equal(CommentKind.Block, carried.Kind);
        Assert.Equal(" why this exists ", carried.Text);
    }

    [Fact]
    public void SeveralComments_AllArriveInSourceOrder()
    {
        // A paragraph written as consecutive line comments is one explanation, and the order it
        // was written in is the whole of its meaning.
        var tokens = LexTokens("// first\n// second\n/* third */\nfoo");
        Assert.Equal(3, tokens[0].Leading.Count);
        Assert.Equal(" first",  tokens[0].Leading[0].Text);
        Assert.Equal(" second", tokens[0].Leading[1].Text);
        Assert.Equal(" third ", tokens[0].Leading[2].Text);
    }

    [Fact]
    public void AComment_ReportsWhereItsMarkerOpened()
    {
        var tokens = LexTokens("foo\n   // indented\nbar");
        var carried = Assert.Single(tokens[1].Leading);
        Assert.Equal(2, carried.Line);
        Assert.Equal(4, carried.Column);
    }

    [Fact]
    public void ANestedBlockComment_KeepsTheInnerMarkers()
    {
        // ⚠ The text is what was written between the OUTERMOST pair. An inner opener is something
        // its author typed on purpose — and commenting out a block that already has comments in it
        // is the reason nesting exists at all.
        var tokens = LexTokens("/* outer /* inner */ still outer */ foo");
        var carried = Assert.Single(tokens[0].Leading);
        Assert.Equal(" outer /* inner */ still outer ", carried.Text);
    }

    [Fact]
    public void ACommentAfterTheLastToken_IsCarriedOnEof()
    {
        // Nothing an author wrote is dropped for sitting at the end of the file.
        var all = Lex("foo\n// trailing note");
        var eof = all[^1];
        Assert.Equal(TokenType.Eof, eof.Type);
        Assert.Equal(" trailing note", Assert.Single(eof.Leading).Text);
    }

    [Fact]
    public void ATokenWithNoCommentBeforeIt_CarriesNone()
    {
        var tokens = LexTokens("foo bar");
        Assert.Empty(tokens[0].Leading);
        Assert.Empty(tokens[1].Leading);
    }

    [Fact]
    public void AFragmentsComment_IsRebasedLikeItsTokens()
    {
        // ⚠⚠ A fragment starts at line 1 of nowhere, and a comment carries a position exactly as a
        // token does. Rebasing one and not the other would point a reader at a file that does not
        // exist — which is the whole reason the rebasing is there.
        var all = new Lexer("// note\nfoo", lineOffset: 10, columnOffset: 4).Tokenize();
        var carried = Assert.Single(all[0].Leading);
        Assert.Equal(11, carried.Line);     // 1 + 10
        Assert.Equal(5,  carried.Column);   // first line, so the column shifts too
    }
}
