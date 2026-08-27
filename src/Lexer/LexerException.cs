namespace Cufet.Lexer;

public sealed class LexerException : Exception
{
    public int Line { get; }
    public int Column { get; }
    public char Character { get; }

    /// <summary>The trouble itself, with no position in front of it.</summary>
    /// <remarks>
    /// ★ Kept apart from <see cref="Exception.Message"/> because the position is BAKED INTO
    /// the message, and text lexed at an offset has to say the same thing at a different one —
    /// see <see cref="At"/>. The alternative was reformatting a finished message by string surgery.
    /// </remarks>
    public string Detail { get; }

    public LexerException(int line, int column, char character)
        : base($"Unexpected character '{character}' on line {line}, column {column}.")
    {
        Line = line;
        Column = column;
        Character = character;
        Detail = $"unexpected character '{character}'";
    }

    public LexerException(int line, int column, string message)
        : base($"Line {line}, column {column}: {message}.")
    {
        Line = line;
        Column = column;
        Character = '\0';
        Detail = message;
    }

    /// <summary>The same trouble, reported where the text it came from actually sits.</summary>
    /// <remarks>
    /// ⚠ Source lexed on its own always starts at line 1, and a block of Cufet held inside
    /// another file does not. Without this, every complaint from inside such a block points at
    /// a line of nowhere — which is worse than no message, because it reads like a real one.
    /// </remarks>
    public LexerException At(int line, int column) =>
        Character != '\0'
            ? new LexerException(line, column, Character)
            : new LexerException(line, column, Detail);
}
