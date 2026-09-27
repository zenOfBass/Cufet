using Cufet.Interpreter;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Compiler.Tests;

/// <summary>What the C# checker says of a program, printed the way `tools/front-end/checker.cufe` prints it.</summary>
/// <remarks>
/// ★ Its own file, depending on nothing but the interpreter, so a scratch tool can compile the very
/// same answer key the test uses — the reason <see cref="ParserTreePrinter"/> is one too.
/// </remarks>
public static class CheckerAnswer
{
    /// <summary>`ok`, `error` and the message on one line, or `parse` when there was nothing to check.</summary>
    /// <remarks>
    /// ⚠ Checked as a lone file — no source directory, so no neighbours and no book loaded from
    /// disk. The Cufet checker is given each file on its own the same way, and a pull of a book
    /// that is not bundled is then a refusal on both sides.
    /// </remarks>
    public static string Expected(string source)
    {
        Cufet.Interpreter.Program program;
        try
        {
            program = new Parser(new CufetLexer(source).Tokenize()).Parse();
        }
        catch (Exception e) when (e is ParseException or Cufet.Lexer.LexerException)
        {
            return "parse";
        }
        try
        {
            new TypeChecker().Check(program);
            return "ok";
        }
        catch (TypeException e)
        {
            return $"error\t{OneLine(e.Message)}";
        }
    }

    /// <summary>A message's line breaks written `\n`, and its backslashes doubled so they stay apart.</summary>
    public static string OneLine(string message) =>
        message.Replace("\r", "").Replace("\\", "\\\\").Replace("\n", "\\n");
}
