using Cufet.Lexer;
using Xunit;

namespace Cufet.Lexer.Tests;

// Foreign source: [...]. The claim under test is that NOTHING inside is Cufet — the brackets are
// the one delimiter in the language whose contents the lexer does not read, and each test below
// names something the lexer WOULD do to that text anywhere else and shows that it does not.
public class AxiomTextTests
{
    private static IReadOnlyList<Token> Lex(string source) => new Lexer(source).Tokenize();

    private static Token LexOne(string source)
    {
        var all = Lex(source);
        Assert.Equal(2, all.Count); // the token, then Eof
        return all[0];
    }

    [Fact]
    public void Axiom_ProducesAnAxiomToken()
    {
        var token = LexOne("[getpid()]");
        Assert.Equal(TokenType.Axiom, token.Type);
        Assert.Equal("getpid()", token.Lexeme);
    }

    [Fact]
    public void Axiom_MayBeEmpty()
    {
        // Refusing an empty axiom is the checker's business if it is anyone's — the lexer's job
        // ends at the brackets.
        Assert.Equal("", LexOne("[]").Lexeme);
    }

    [Fact]
    public void Axiom_KeepsBracketPairs()
    {
        // ★ The reason this is depth-counted rather than "read to the first ']'": a subscript is
        // C's commonest bracket, so stopping at the first one would make the delimiter unusable
        // for the language it exists to carry.
        Assert.Equal("argv[0]", LexOne("[argv[0]]").Lexeme);
        Assert.Equal("a[b[c]]", LexOne("[a[b[c]]]").Lexeme);
    }

    [Fact]
    public void Axiom_DoesNotInterpretQuotesOrEscapes()
    {
        Assert.Equal("puts(\"a\\nb\")", LexOne("[puts(\"a\\nb\")]").Lexeme);
    }

    [Fact]
    public void Axiom_DoesNotInterpretBracesAsInterpolation()
    {
        Assert.Equal("{ return 1; }", LexOne("[{ return 1; }]").Lexeme);
    }

    [Fact]
    public void Axiom_DoesNotTreatItsContentAsComments()
    {
        // Cufet's own comment syntax means nothing here — this is C, and `/* */` is C's.
        Assert.Equal("/* note */ 1", LexOne("[/* note */ 1]").Lexeme);
    }

    [Fact]
    public void Axiom_MaySpanLines()
    {
        Assert.Equal("int x = 1;\nreturn x;", LexOne("[int x = 1;\nreturn x;]").Lexeme);
    }

    [Fact]
    public void Axiom_NormalisesCrLfToOneNewline()
    {
        // The same language rule verbatim text follows: a break is ONE '\n' in the value however
        // the file stores it, so a program does not mean different things per checkout.
        Assert.Equal("a\nb", LexOne("[a\r\nb]").Lexeme);
    }

    [Fact]
    public void Axiom_LineCountingContinuesAfterIt()
    {
        var tokens = Lex("[one\ntwo]\nfoo");
        Assert.Equal("foo", tokens[1].Lexeme);
        Assert.Equal(3, tokens[1].Line);
    }

    [Fact]
    public void Axiom_ReportsTheOpenersPosition()
    {
        var tokens = Lex("Define x as [a\nb].");
        var axiom = tokens.First(t => t.Type == TokenType.Axiom);
        Assert.Equal(1, axiom.Line);
        Assert.Equal(13, axiom.Column);
    }

    [Fact]
    public void Axiom_Unterminated_Refuses()
    {
        var e = Assert.Throws<LexerException>(() => Lex("[getpid()"));
        Assert.Contains("unterminated foreign source", e.Message);
    }

    [Fact]
    public void Axiom_UnbalancedClosingBracketInside_EndsItEarly()
    {
        // ⚠ The known edge, pinned so it is a decision rather than a surprise: the scan counts
        // brackets and nothing else, so a lone ']' inside a C string literal closes the axiom.
        // `<<...>>` has the same edge, and closing it would mean knowing which foreign language
        // this is — which the brackets deliberately do not say.
        var tokens = Lex("[a]b");
        Assert.Equal(TokenType.Axiom, tokens[0].Type);
        Assert.Equal("a", tokens[0].Lexeme);
        Assert.Equal("b", tokens[1].Lexeme);
    }

    [Fact]
    public void Axiom_EndedEarlyByAStringsBracket_RefusesRatherThanTruncatingQuietly()
    {
        // ★ The saving grace of the edge above: what follows an early close is read as Cufet, and
        // C almost never continues into anything Cufet will accept. `[puts("]")]` closes at the
        // bracket inside the string literal and then meets `")]`, which is an unterminated Cufet
        // string — so the writer gets a refusal rather than an axiom quietly missing its tail.
        Assert.ThrowsAny<LexerException>(() => Lex("[puts(\"]\")]"));
    }
}
