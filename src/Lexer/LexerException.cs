namespace Cofet.Lexer;

public sealed class LexerException : Exception
{
    public int Line { get; }
    public char Character { get; }

    public LexerException(int line, char character)
        : base($"Unexpected character '{character}' on line {line}.")
    {
        Line = line;
        Character = character;
    }

    public LexerException(int line, string message)
        : base($"Line {line}: {message}.")
    {
        Line = line;
        Character = '\0';
    }
}
