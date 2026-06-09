namespace Cufet.Lexer;

public sealed record Token(TokenType Type, string Lexeme, int Line)
{
    public bool IsNoise => Type == TokenType.Article;

    public override string ToString() => $"[{Type} \"{Lexeme}\" L{Line}]";
}
