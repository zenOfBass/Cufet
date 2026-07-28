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
}
