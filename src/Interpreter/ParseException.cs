using NLP.Lexer;

namespace NLP.Interpreter;

public sealed class ParseException : Exception
{
    public int Line { get; }

    public ParseException(Token got, string expected)
        : base($"Line {got.Line}: expected {expected}, got {got.Type} \"{got.Lexeme}\".")
    {
        Line = got.Line;
    }
}
