using Cufet.Lexer;
using Xunit;

namespace Cufet.Lexer.Tests;

public class BitsLiteralTests
{
    private static IReadOnlyList<Token> Lex(string source) => new Lexer(source).Tokenize();

    private static Token One(string source)
    {
        var tokens = Lex(source);
        Assert.Equal(2, tokens.Count);              // the literal, then Eof
        return tokens[0];
    }

    // ── The three bases ──────────────────────────────────────────────────

    [Theory]
    [InlineData("0xFF",  "0xFF")]
    [InlineData("0b1010", "0b1010")]
    [InlineData("0o755", "0o755")]
    public void Bits_ThreeBases_LexAsBits(string source, string expected)
    {
        var token = One(source);
        Assert.Equal(TokenType.Bits, token.Type);
        Assert.Equal(expected, token.Lexeme);
    }

    [Fact]
    public void Bits_PrefixIsCaseInsensitive_AndNormalisesToLowercase()
    {
        // Keywords are case-insensitive, so the prefix is too. The lexeme normalises so that
        // 0X10 and 0x10 cannot be told apart downstream.
        Assert.Equal("0x10", One("0X10").Lexeme);
        Assert.Equal("0b10", One("0B10").Lexeme);
        Assert.Equal("0o10", One("0O10").Lexeme);
    }

    [Fact]
    public void Bits_HexDigitsKeepTheirCase()
    {
        // The digits are the value; only the prefix is normalised.
        Assert.Equal("0xff", One("0xff").Lexeme);
        Assert.Equal("0xFF", One("0xFF").Lexeme);
    }

    // ── Leading zeros are significant ────────────────────────────────────
    // Unlike C, Java, Rust, Go and Python, where 0x0F and 0xF are identical and width comes
    // from the declared type. Here the digit count IS the width, which is what lets `not`
    // work without a signed interpretation.

    [Fact]
    public void Bits_LeadingZerosArePreserved()
    {
        Assert.Equal("0x0F", One("0x0F").Lexeme);
        Assert.Equal("0xF",  One("0xF").Lexeme);
        Assert.NotEqual(One("0x0F").Lexeme, One("0xF").Lexeme);
    }

    // ── Digit separators ─────────────────────────────────────────────────

    [Theory]
    [InlineData("0xFF_FF",        "0xFFFF")]
    [InlineData("0b1010_1010",    "0b10101010")]
    [InlineData("0o7_5_5",        "0o755")]
    [InlineData("0xDE_AD_BE_EF",  "0xDEADBEEF")]
    public void Bits_UnderscoresGroupDigitsAndAreDropped(string source, string expected)
        => Assert.Equal(expected, One(source).Lexeme);

    [Theory]
    [InlineData("0x_FF")]   // leading
    [InlineData("0xFF_")]   // trailing
    [InlineData("0xF__F")]  // doubled
    public void Bits_SeparatorMustSitBetweenDigits(string source)
    {
        var ex = Assert.Throws<LexerException>(() => Lex(source));
        Assert.Contains("between digits", ex.Message);
    }

    [Fact]
    public void Bits_SeparatorIsNotAllowedInDecimal()
    {
        // Grouping in these bases is structural (nibbles, bytes, permission triples); in decimal
        // it is cosmetic, and in a fraction it marks nothing at all. '_' is not a Cufet character
        // anywhere else — identifiers are letters, digits and internal hyphens — so 1_000 is a
        // lexer error rather than one thousand, and rather than quietly becoming two tokens.
        var ex = Assert.Throws<LexerException>(() => Lex("1_000"));
        Assert.Contains("_", ex.Message);
    }

    [Fact]
    public void Bits_SeparatorIsNotAllowedInAFraction()
    {
        Assert.Throws<LexerException>(() => Lex("3.141_592"));
    }

    // ── No bare-zero octal ───────────────────────────────────────────────

    [Fact]
    public void Bits_BareLeadingZeroStaysDecimal()
    {
        // C reads 0755 as octal 493. That footgun is not reproduced: it is seven hundred
        // and fifty-five, and octal must say so with 0o.
        var token = One("0755");
        Assert.Equal(TokenType.Number, token.Type);
        Assert.Equal("0755", token.Lexeme);
    }

    [Fact]
    public void Bits_PlainZeroIsStillANumber()
    {
        var token = One("0");
        Assert.Equal(TokenType.Number, token.Type);
        Assert.Equal("0", token.Lexeme);
    }

    // ── Digits of the wrong base ─────────────────────────────────────────

    [Theory]
    [InlineData("0b12", "binary")]
    [InlineData("0o88", "octal")]
    [InlineData("0xG1", "hex")]
    public void Bits_WrongDigitForBase_NamesTheBase(string source, string baseName)
    {
        var ex = Assert.Throws<LexerException>(() => Lex(source));
        Assert.Contains(baseName, ex.Message);
    }

    [Theory]
    [InlineData("0x")]
    [InlineData("0b")]
    [InlineData("0o")]
    public void Bits_PrefixWithNoDigits_IsAnError(string source)
    {
        var ex = Assert.Throws<LexerException>(() => Lex(source));
        Assert.Contains("at least one", ex.Message);
    }

    // ── The 64-bit ceiling ───────────────────────────────────────────────

    [Fact]
    public void Bits_SixtyFourBitsIsAllowed()
    {
        Assert.Equal(TokenType.Bits, One("0xFFFFFFFFFFFFFFFF").Type);   // 16 hex digits
        Assert.Equal(TokenType.Bits, One("0b" + new string('1', 64)).Type);
    }

    [Fact]
    public void Bits_WiderThanSixtyFour_IsAnError()
    {
        var ex = Assert.Throws<LexerException>(() => Lex("0xFFFFFFFFFFFFFFFFF"));  // 17 digits
        Assert.Contains("64", ex.Message);
    }

    // ── Interaction with surrounding code ────────────────────────────────

    [Fact]
    public void Bits_TerminatingDotIsNotConsumed()
    {
        var tokens = Lex("State 0xFF.");
        Assert.Equal(TokenType.State, tokens[0].Type);
        Assert.Equal(TokenType.Bits, tokens[1].Type);
        Assert.Equal("0xFF", tokens[1].Lexeme);
        Assert.Equal(TokenType.Dot, tokens[2].Type);
    }

    [Fact]
    public void Bits_InAnExpression()
    {
        var tokens = Lex("0xFF and 0b1010");
        Assert.Equal(TokenType.Bits, tokens[0].Type);
        Assert.Equal(TokenType.And, tokens[1].Type);
        Assert.Equal(TokenType.Bits, tokens[2].Type);
    }
}
