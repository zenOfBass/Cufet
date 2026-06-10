using Cufet.Lexer;

namespace Cufet.Interpreter;

public sealed class ParseException : Exception
{
    public int Line { get; }

    public ParseException(Token got, string expected)
        : base($"Line {got.Line}: expected {expected}, got {got.Type} \"{got.Lexeme}\".")
    {
        Line = got.Line;
    }

    public ParseException(int line, string message) : base($"Line {line}: {message}")
    {
        Line = line;
    }
}
