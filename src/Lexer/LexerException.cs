namespace Cufet.Lexer;

public sealed class LexerException : Exception
{
    public int Line { get; }
    public int Column { get; }
    public char Character { get; }

    public LexerException(int line, int column, char character)
        : base($"Unexpected character '{character}' on line {line}, column {column}.")
    {
        Line = line;
        Column = column;
        Character = character;
    }

    public LexerException(int line, int column, string message)
        : base($"Line {line}, column {column}: {message}.")
    {
        Line = line;
        Column = column;
        Character = '\0';
    }
}
