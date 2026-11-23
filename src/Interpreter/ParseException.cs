using Cufet.Lexer;

namespace Cufet.Interpreter;

public sealed class ParseException : Exception
{
    public int Line { get; }
    public int Column { get; }

    /// <remarks>
    /// ⚠⚠ This template was `expected X, got Kind "lexeme"`, and both halves were the parser's own
    /// vocabulary: `got Eof ""`, `got NumberKw "number"`, `expected expression`, `expected RBrace`.
    /// MEASURED 2026-11-22 by breaking the example programs 1,072 ways: 699 of 1,040 refusals came
    /// through here, 366 of them the file simply ending — which, in an editor that checks as you
    /// type, is every half-written line. Both halves now say what a reader would.
    /// </remarks>
    public ParseException(Token got, string expected)
        : base($"Line {SourceMap.Display(got.Line)}, column {got.Column}: expected {Wanted(expected)}, but {Found(got)}.")
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

    /// <summary>The two expectations callers spell in the parser's terms, in a reader's.</summary>
    private static string Wanted(string expected) => expected switch
    {
        "expression"        => "a value",
        "statement keyword" => "an instruction (like 'State …' or 'Define …')",
        _                   => expected,
    };

    /// <summary>What was found, said as a reader would say it.</summary>
    /// <remarks>★ Mirrored in self-hosting/Cufet/parser/reader.cufe (`found-said`); the parser
    /// parity tests hold the two to the same words.</remarks>
    internal static string Found(Token got) => got.Type switch
    {
        TokenType.Eof                               => "the file ended",
        TokenType.Identifier                        => $"found the name '{got.Lexeme}'",
        TokenType.Number                            => $"found the number {got.Lexeme}",
        TokenType.String or TokenType.StringPiece   => $"found the text \"{got.Lexeme}\"",
        TokenType.InterpolOpen                      => "found a text",
        TokenType.Axiom                             => "found a block in square brackets",
        TokenType.Possessive                        => $"found the {got.Lexeme}",
        _ when got.Lexeme.Length > 0 && char.IsLetter(got.Lexeme[0]) => $"found the word '{got.Lexeme}'",
        _ when got.Lexeme.Length > 0                => $"found the mark '{got.Lexeme}'",
        _                                           => "found something else",
    };
}
