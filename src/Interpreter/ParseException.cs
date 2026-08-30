using Cufet.Lexer;

namespace Cufet.Interpreter;

public sealed class ParseException : Exception
{
    public int Line { get; }
    public int Column { get; }

    public ParseException(Token got, string expected)
        : base($"Line {SourceMap.Display(got.Line)}, column {got.Column}: expected {expected}, got {got.Type} \"{got.Lexeme}\".")
    {
        Line = got.Line;
        Column = got.Column;
    }

    public ParseException(int line, int column, string message)
        : base($"Line {SourceMap.Display(line)}, column {column}: {message}")
    {
        Line = line;
        Column = column;
    }
}
