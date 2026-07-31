namespace Cufet.Lexer;

// Line is 1-based; Column is the 1-based character offset of the token's FIRST character within
// its line. A token that spans lines (a multi-line string literal) reports where it opened.
public sealed record Token(TokenType Type, string Lexeme, int Line, int Column)
{
    public bool IsNoise => Type == TokenType.Article;

    public override string ToString() => $"[{Type} \"{Lexeme}\" L{Line}:{Column}]";
}
