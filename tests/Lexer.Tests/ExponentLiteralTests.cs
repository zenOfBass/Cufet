using Cufet.Lexer;
using Xunit;

namespace Cufet.Lexer.Tests;

public class ExponentLiteralTests
{
    private static IReadOnlyList<Token> Lex(string source) => new Lexer(source).Tokenize();

    [Theory]
    [InlineData("6.022e23")]
    [InlineData("1E3")]
    [InlineData("1e-3")]
    [InlineData("2e+2")]
    [InlineData("7.9228162514264337593543950335e28")]
    [InlineData("1e-40")]
    public void Exponent_IsPartOfTheNumber(string source)
    {
        var tokens = Lex(source);
        Assert.Equal(2, tokens.Count);              // the literal, then Eof
        Assert.Equal(TokenType.Number, tokens[0].Type);
        Assert.Equal(source, tokens[0].Lexeme);
    }

    [Theory]
    [InlineData("1e", "1", "e")]
    [InlineData("2e.", "2", "e")]
    [InlineData("3e-", "3", "e")]
    [InlineData("4e+x", "4", "e")]
    public void AnEWithNoDigitAfterIt_EndsTheNumber(string source, string number, string next)
    {
        // Only a digit, or a sign and then a digit, makes the `e` an exponent; otherwise it is left
        // to be whatever it would have been anyway.
        var tokens = Lex(source);
        Assert.Equal(TokenType.Number, tokens[0].Type);
        Assert.Equal(number, tokens[0].Lexeme);
        Assert.StartsWith(next, tokens[1].Lexeme);
    }

    [Theory]
    [InlineData("1e29")]
    [InlineData("79228162514264337593543950336")]
    [InlineData("9.9999999999999999999999999999e28")]
    public void ANumberTooLargeToHold_IsRefusedAtTheLiteral(string literal)
    {
        // This crashed the parser with .NET's own OverflowException before exponents existed —
        // and an exponent makes it one keystroke away.
        var e = Assert.Throws<LexerException>(() => Lex("State " + literal + "."));
        Assert.Equal(7, e.Column);
        Assert.Contains($"{literal} is larger than a number can hold", e.Message);
    }

    [Theory]
    [InlineData("0.123456789012345678901234567891")]
    [InlineData("1e-40")]
    public void TooManyPlaces_IsNotRefused_ItRounds(string literal) =>
        Assert.Equal(TokenType.Number, Lex(literal)[0].Type);
}
