namespace Cufet.Lexer;

// Line is 1-based; Column is the 1-based character offset of the token's FIRST character within
// its line. A token that spans lines (a multi-line string literal) reports where it opened.
public sealed record Token(TokenType Type, string Lexeme, int Line, int Column)
{
    /// <summary>Comments written before this token, in source order; empty for almost every one.</summary>
    /// <remarks>
    /// ★★ An `init` property with a default rather than a fifth positional parameter, so that the
    /// thirty-odd places that build a token — and every test that compares one — go on saying what
    /// they said. `with { Line = … }` carries it along, which is what the fragment rebasing needs.
    /// </remarks>
    public IReadOnlyList<Comment> Leading { get; init; } = [];

    public bool IsNoise => Type == TokenType.Article;

    public override string ToString() => $"[{Type} \"{Lexeme}\" L{Line}:{Column}]";
}
