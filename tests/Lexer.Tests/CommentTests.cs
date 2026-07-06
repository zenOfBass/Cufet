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

    // ── Existence: comment produces no tokens ────────────────────────────

    [Fact]
    public void Comment_AloneProducesNoTokens()
    {
        var tokens = LexTokens("[[ a comment ]]");
        Assert.Empty(tokens);
    }

    [Fact]
    public void Comment_BeforeCode_Transparent()
    {
        var tokens = LexTokens("[[ note ]] foo");
        Assert.Single(tokens);
        Assert.Equal(TokenType.Identifier, tokens[0].Type);
        Assert.Equal("foo", tokens[0].Lexeme);
    }

    [Fact]
    public void Comment_AfterCode_Transparent()
    {
        var tokens = LexTokens("foo [[ note ]]");
        Assert.Single(tokens);
        Assert.Equal(TokenType.Identifier, tokens[0].Type);
        Assert.Equal("foo", tokens[0].Lexeme);
    }

    [Fact]
    public void Comment_BetweenTokens_Transparent()
    {
        var tokens = LexTokens("foo [[ note ]] bar");
        Assert.Equal(2, tokens.Count);
        Assert.Equal("foo", tokens[0].Lexeme);
        Assert.Equal("bar", tokens[1].Lexeme);
    }

    // ── Multi-line ───────────────────────────────────────────────────────

    [Fact]
    public void Comment_MultiLine_ProducesNoTokens()
    {
        var tokens = LexTokens("[[ line one\nline two ]]");
        Assert.Empty(tokens);
    }

    [Fact]
    public void Comment_MultiLine_BeforeCode_Transparent()
    {
        var tokens = LexTokens("[[ line one\nline two ]] foo");
        Assert.Single(tokens);
        Assert.Equal("foo", tokens[0].Lexeme);
    }

    // ── Dot inside comment is not a statement terminator ─────────────────

    [Fact]
    public void Comment_DotInsideIsNotDotToken()
    {
        var tokens = LexTokens("[[ this. has. periods. ]] foo");
        Assert.Single(tokens);
        Assert.Equal(TokenType.Identifier, tokens[0].Type);
        Assert.Equal("foo", tokens[0].Lexeme);
        Assert.DoesNotContain(tokens, t => t.Type == TokenType.Dot);
    }

    // ── Cufet syntax inside comment is not parsed ─────────────────────────

    [Fact]
    public void Comment_CufetSyntaxInsideIsIgnored()
    {
        // "Define" inside comment should not produce a Define token
        var tokens = LexTokens("[[ Define y as 99. ]] foo");
        Assert.Single(tokens);
        Assert.Equal(TokenType.Identifier, tokens[0].Type);
        Assert.DoesNotContain(tokens, t => t.Type == TokenType.Define);
    }

    // ── Non-nesting: first ']]' closes ───────────────────────────────────

    [Fact]
    public void Comment_NonNesting_FirstCloserEnds()
    {
        // [[ a [[ b ]] → comment closes at the inner ']]'; 'c' following is code
        var tokens = LexTokens("[[ a [[ b ]] c");
        Assert.Single(tokens);
        Assert.Equal("c", tokens[0].Lexeme);
    }

    // ── Unterminated comment is a lexer error ─────────────────────────────

    [Fact]
    public void Comment_Unterminated_ThrowsLexerException()
    {
        var ex = Assert.Throws<LexerException>(() => Lex("[[ no closing bracket"));
        Assert.Contains("unterminated comment", ex.Message);
    }

    [Fact]
    public void Comment_Unterminated_ErrorMentionsExpectedClose()
    {
        var ex = Assert.Throws<LexerException>(() => Lex("[[ open"));
        Assert.Contains("]]", ex.Message);
    }

    // ── Multiple comments in one source ──────────────────────────────────

    [Fact]
    public void Comment_Multiple_AllStripped()
    {
        var tokens = LexTokens("[[ first ]] foo [[ second ]] bar [[ third ]]");
        Assert.Equal(2, tokens.Count);
        Assert.Equal("foo", tokens[0].Lexeme);
        Assert.Equal("bar", tokens[1].Lexeme);
    }
}
